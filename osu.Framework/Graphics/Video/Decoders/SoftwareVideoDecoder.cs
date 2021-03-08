// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using osu.Framework.Graphics.Textures;
using osu.Framework.Logging;
using osu.Framework.Threading;

namespace osu.Framework.Graphics.Video.Decoders
{
    public unsafe class SoftwareVideoDecoder : VideoDecoder
    {
        /// <summary>
        /// Cancellation token used to cancel the asynchronous decoding task
        /// </summary>
        private CancellationTokenSource decodingTaskToken = new CancellationTokenSource(); // TODO used in a few non-epic ways atm

        /// <summary>
        /// Separate thread which performs the actual video decoding
        /// </summary>
        private Task decodingTask;

        /// <summary>
        /// Pool which contains all textures ussed by the decoder
        /// </summary>
        private readonly ConcurrentQueue<Texture> availableTextures = new ConcurrentQueue<Texture>();

        /// <summary>
        /// Queue which contains all buffered decoded frames
        /// </summary>
        private readonly ConcurrentNotifyQueue<DecodedFrame> decodedFrames = new ConcurrentNotifyQueue<DecodedFrame>();

        /// <summary>
        /// Queue which contains all actions that should be executed on the decoder thread
        /// </summary>
        private readonly BlockingCollection<Action> decoderActions = new BlockingCollection<Action>();

        /// <summary>
        /// Event to pause frame retrieval while a seek is occuring
        /// </summary>
        private readonly ManualResetEventSlim seekEvent = new ManualResetEventSlim(true);

        private double? skipOutputUntilTime;

        public SoftwareVideoDecoder(Stream stream, Scheduler scheduler)
            : base(stream, scheduler)
        {
        }

        #region Disposal

        public override void Dispose()
        {
            if (Disposed)
                return;

            base.Dispose();

            StopDecoding(true);
            decoderActions.Dispose();

            if (fmtCtx != null)
            {
                fixed (AVFormatContext** ptr = &fmtCtx)
                {
                    ffmpeg.avformat_close_input(ptr);
                }
            }

            Stream.Dispose();
            Stream = null;

            if (swsCtx != null)
                ffmpeg.sws_freeContext(swsCtx);

            while (decodedFrames.TryDequeue(out var f))
                f.Texture.Dispose();

            while (availableTextures.TryDequeue(out var t))
                t.Dispose();

            seekEvent.Dispose();
        }

        #endregion

        public override double Duration => stream == null ? 0 : (stream->duration <= 0 ? fmtCtx->duration : stream->duration) * timebase * 1000;

        public override bool Looping { get; internal set; }

        /// <summary>
        /// Prepare the internal state and start the decoding in a separate thread
        /// </summary>
        protected override void StartDecodingProtected()
        {
            try
            {
                prepareDecoding();
                prepareFilters();

                // Run decoding loop on a separate thread
                decodingTask = Task.Factory.StartNew(
                    decodingLoop,
                    decodingTaskToken.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
            catch (InvalidOperationException e)
            {
                Logger.Log($"SoftwareVideoDecoder setup faulted: {e}");
                RawState = DecoderState.Faulted;
            }
            catch (DllNotFoundException e)
            {
                Logger.Log($"ffmpeg DLL not found: {e}");
                RawState = DecoderState.Faulted;
            }
        }

        /// <summary>
        /// Stop the decoding thread, but don't free up resources
        /// </summary>
        /// <param name="wait">Whether to wait for the decoding thread to actually finsh</param>
        protected override void StopDecodingProtected(bool wait)
        {
            decodingTaskToken.Cancel();

            if (wait)
                decodingTask.Wait();

            // No need to dispose:
            // https://devblogs.microsoft.com/pfxteam/do-i-need-to-dispose-of-tasks/
            decodingTask = null;

            decodingTaskToken.Dispose();
            decodingTaskToken = new CancellationTokenSource();

            RawState = DecoderState.Ready;
        }

        /// <summary>
        /// Seeks to a given position inside the video stream
        /// </summary>
        /// <param name="pos">Position in seconds to seek to</param>
        public override void Seek(double pos)
        {
            if (!CanSeek)
                throw new InvalidOperationException("This decoder cannot seek because the underlying stream used to decode the video does not support seeking.");

            seekEvent.Reset();
            decoderActions.Add(() =>
            {
                // Don't queue more frames
                decodedFrames.ItemRemoved -= frameDecodeCallback;

                // Seek
                ffmpeg.av_seek_frame(fmtCtx, stream->index, (long)(pos / timebase / 1000.0), ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(CodecCtx);
                skipOutputUntilTime = pos;

                // Discard current frames
                decodedFrames.Clear();
                decodedFrames.ItemRemoved += frameDecodeCallback;

                // Decode until we're at the right position
                // Needs to be a while loop because the first few frames decoded after the seek may not be at the correct time,
                // so just skip until pos is reached
                while (decodedFrames.Count < max_pending_frames)
                {
                    decodeSingleFrame(decodedFrame);
                }

                // Allow waiting GetDecodedFrames calls to continue
                seekEvent.Set();
            });
        }

        public override void ReturnFrames(IEnumerable<DecodedFrame> frames)
        {
            foreach (var f in frames)
            {
                ((VideoTexture)f.Texture.TextureGL).FlushUploads();
                availableTextures.Enqueue(f.Texture);
            }
        }

        public override IEnumerable<DecodedFrame> GetDecodedFrames()
        {
            // Wait for any possible pending seek to finish, so we don't send old frames
            seekEvent.Wait();

            var frames = new List<DecodedFrame>(decodedFrames.Count);
            while (decodedFrames.TryDequeue(out var df))
                frames.Add(df);

            return frames;
        }

        private bool decodeNextFrame(AVFrame* frame)
        {
            ffmpeg.av_frame_unref(frame);

            int receiveError;

            do
            {
                try
                {
                    int res;

                    do
                    {
                        res = ffmpeg.av_read_frame(fmtCtx, packet);

                        if (res != ffmpeg.AVERROR_EOF)
                        {
                            if (res >= 0)
                                continue;

                            RawState = DecoderState.Ready;
                            Thread.Sleep(1);
                        }
                        else
                        {
                            // EOF
                            Logger.Log($"EOF, Looping: {Looping}, previous time: {lastDecodedFrameTime}");
                            if (Looping)
                                Seek(0);
                            else
                                RawState = DecoderState.EndOfStream;

                            return false;
                        }
                    } while (packet->stream_index != streamIndex);

                    res = ffmpeg.avcodec_send_packet(CodecCtx, packet);
                    if (res < 0)
                        Logger.Log($"Error {GetErrorMessage(res)} sending packet in {GetType().Name}");
                }
                finally
                {
                    ffmpeg.av_packet_unref(packet);
                }

                receiveError = ffmpeg.avcodec_receive_frame(CodecCtx, frame);
            } while (receiveError == ffmpeg.AVERROR(ffmpeg.EAGAIN) && receiveError != 0);

            return true;
        }

        private volatile float lastDecodedFrameTime;

        private AVFrame* outFrame; // TODO fix this

        private void decodeSingleFrame(AVFrame* frame)
        {
            if (!decodeNextFrame(frame))
                return;

            double frameTime = (frame->best_effort_timestamp - stream->start_time) * timebase * 1000;

            if (!skipOutputUntilTime.HasValue || skipOutputUntilTime.Value < frameTime)
            {
                skipOutputUntilTime = null;

                ffmpeg.sws_scale(swsCtx, frame->data, frame->linesize, 0, CodecCtx->height, convDstData, convDstLineSize);

                // Maybe this could be pooled too, but probably not worth the effort without benchmarking
                outFrame->data = new byte_ptrArray8();
                outFrame->linesize = new int_array8();
                outFrame->data.UpdateFrom(convDstData);
                outFrame->linesize.UpdateFrom(convDstLineSize);

                if (!availableTextures.TryDequeue(out Texture tex))
                {
                    // Create new textures as needed
                    tex = new Texture(new VideoTexture(CodecCtx->width, CodecCtx->height));
                }

                var upload = new VideoTextureUpload(outFrame);

                tex.SetData(upload);
                decodedFrames.Enqueue(new DecodedFrame { Time = frameTime, Texture = tex });
            }

            lastDecodedFrameTime = (float)frameTime;
        }

        private const int max_pending_frames = 3;

        private void frameDecodeCallback(object sender, EventArgs _)
        {
            if (!decodingTaskToken.IsCancellationRequested && sender != null && ((ConcurrentNotifyQueue<DecodedFrame>)sender).Count < max_pending_frames)
            {
                // Insert an action that decodes a frame into the queue
                decoderActions.Add(() => decodeSingleFrame(decodedFrame));
            }
        }

        private void decodingLoop()
        {
            outFrame = ffmpeg.av_frame_alloc();
            outFrame->format = (int)AVPixelFormat.AV_PIX_FMT_RGBA;
            outFrame->width = CodecCtx->width;
            outFrame->height = CodecCtx->height;

            decodedFrame = ffmpeg.av_frame_alloc();

            packet = ffmpeg.av_packet_alloc();

            try
            {
                decodedFrames.ItemRemoved += frameDecodeCallback;

                // Decode initial frames
                while (decodedFrames.Count < max_pending_frames)
                    decodeSingleFrame(decodedFrame);

                while (!decodingTaskToken.IsCancellationRequested)
                    decoderActions.Take(decodingTaskToken.Token)();
            }
            catch (OperationCanceledException e) when (e.CancellationToken == decodingTaskToken.Token)
            {
                // pass
            }
            finally
            {
                fixed (AVFrame** framePtr = &outFrame)
                    ffmpeg.av_frame_free(framePtr);

                fixed (AVFrame** framePtr = &decodedFrame)
                    ffmpeg.av_frame_free(framePtr);

                fixed (AVPacket** packetPtr = &packet)
                    ffmpeg.av_packet_free(packetPtr);

                if (RawState != DecoderState.Faulted)
                    RawState = DecoderState.Stopped;

                decodedFrames.ItemRemoved -= frameDecodeCallback;
            }
        }

        private AVFormatContext* fmtCtx;
        protected AVCodecContext* CodecCtx;
        private AVStream* stream;
        private double timebase;
        private AVPacket* packet;
        private AVFrame* decodedFrame;

        private SwsContext* swsCtx;

        private int streamIndex;

        private IntPtr conversionBuffer;
        private byte_ptrArray4 convDstData;
        private int_array4 convDstLineSize;

        private void prepareDecoding()
        {
            // TODO does this entire function leak massively when anything fails?

            const int buffer_size = 4096;

            // Allocate the format context
            fmtCtx = ffmpeg.avformat_alloc_context();

            // Allocate a new avio context to have the format context use custom (managed) I/O routines
            byte* avioBuffer = (byte*)ffmpeg.av_malloc(buffer_size);
            fmtCtx->pb = ffmpeg.avio_alloc_context(avioBuffer, buffer_size, 0, (void*)Handle.Handle, RawReadDelegate, null, RawSeekDelegate);

            // Open the input
            int res;
            fixed (AVFormatContext** ptr = &fmtCtx)
                res = ffmpeg.avformat_open_input(ptr, "dummy", null, null);

            if (res < 0)
                throw new InvalidOperationException($"Error opening file or stream: {GetErrorMessage(res)}");

            // Retrieve streams information
            res = ffmpeg.avformat_find_stream_info(fmtCtx, null);
            if (res < 0)
                throw new InvalidOperationException($"Error finding stream info: {GetErrorMessage(res)}");

            // Find the codec and index of the best video stream
            AVCodec* codec = null;

            streamIndex = ffmpeg.av_find_best_stream(fmtCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            if (streamIndex < 0)
                throw new InvalidOperationException($"Could not find valid stream: {GetErrorMessage(streamIndex)}");

            // Create a codec context used to decode the found video stream
            CodecCtx = ffmpeg.avcodec_alloc_context3(codec);

            stream = fmtCtx->streams[streamIndex];
            timebase = stream->time_base.GetValue();

            // Copy necessary information from the stream's info to the decoder context
            res = ffmpeg.avcodec_parameters_to_context(CodecCtx, stream->codecpar);
            if (res < 0)
                throw new InvalidOperationException($"Error filling codec context with parameters: {GetErrorMessage(res)}");

            // Open the codec context for decoding
            res = ffmpeg.avcodec_open2(CodecCtx, codec, null);
            if (res < 0)
                throw new InvalidOperationException($"Error trying to open codec with id {CodecCtx->codec_id}: {GetErrorMessage(res)}");
        }

        private void prepareFilters()
        {
            const AVPixelFormat dest_fmt = AVPixelFormat.AV_PIX_FMT_RGBA;
            int w = CodecCtx->width;
            int h = CodecCtx->height;

            Logger.Log($"Conversion is from {CodecCtx->pix_fmt} to {dest_fmt}");

            // 1 = SWS_FAST_BILINEAR
            // https://www.ffmpeg.org/doxygen/trunk/swscale_8h_source.html#l00056
            // TODO check input and output format support
            swsCtx = ffmpeg.sws_getContext(w, h, CodecCtx->pix_fmt, w, h,
                dest_fmt, 1, null, null, null);

            int bufferSize = ffmpeg.av_image_get_buffer_size(dest_fmt, w, h, 1);
            conversionBuffer = Marshal.AllocHGlobal(bufferSize);

            ffmpeg.av_image_fill_arrays(ref convDstData, ref convDstLineSize, (byte*)conversionBuffer, dest_fmt, w, h, 1);
        }
    }
}
