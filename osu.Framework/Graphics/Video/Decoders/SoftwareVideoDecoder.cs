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
using osu.Framework.Allocation;
using osu.Framework.Graphics.Textures;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Threading;

namespace osu.Framework.Graphics.Video.Decoders
{
    public unsafe class SoftwareVideoDecoder : VideoDecoder
    {
        private CancellationTokenSource decodingTaskCancellationTokenSource = new CancellationTokenSource();
        private Task decodingTask;

        private readonly ConcurrentQueue<Texture> availableTextures = new ConcurrentQueue<Texture>();
        private readonly ConcurrentNotifyQueue<DecodedFrame> decodedFrames = new ConcurrentNotifyQueue<DecodedFrame>();
        private readonly BlockingCollection<Action> decoderActions = new BlockingCollection<Action>();

        private readonly ManualResetEventSlim seekEvent = new ManualResetEventSlim(true);

        private ObjectHandle<SoftwareVideoDecoder> handle;

        private double? skipOutputUntilTime;

        public SoftwareVideoDecoder(Stream stream, Scheduler scheduler)
            : base(stream, scheduler)
        {
            handle = new ObjectHandle<SoftwareVideoDecoder>(this, GCHandleType.Normal);
        }

        #region Disposal

        private bool disposed;

        public override void Dispose()
        {
            if (disposed)
                return;

            disposed = true;

            StopDecoding(true);
            decoderActions.Dispose();

            if (fmtCtx != null && opened)
            {
                fixed (AVFormatContext** ptr = &fmtCtx)
                {
                    ffmpeg.avformat_close_input(ptr);
                }
            }

            seekCallback = null;
            readPacketCallback = null;
            managedCtxBuffer = null;

            Stream.Dispose();
            Stream = null;

            buffer = null;

            if (swsCtx != null)
                ffmpeg.sws_freeContext(swsCtx);

            if (packet != null)
            {
                fixed (AVPacket** ptr = &packet)
                    ffmpeg.av_packet_free(ptr);
            }

            if (frame != null)
            {
                fixed (AVFrame** ptr = &frame)
                    ffmpeg.av_frame_free(ptr);
            }

            if (receivedFrame != null)
            {
                fixed (AVFrame** ptr = &receivedFrame)
                    ffmpeg.av_frame_free(ptr);
            }

            while (decodedFrames.TryDequeue(out var f))
                f.Texture.Dispose();

            while (availableTextures.TryDequeue(out var t))
                t.Dispose();

            handle.Dispose();

            seekEvent.Dispose();
        }

        #endregion

        public override double Duration => stream == null ? 0 : duration * timebase * 1000;

        public override bool Looping { get; internal set; }

        public override void StartDecoding()
        {
            Logger.Log("Starting SoftwareVideoDecoder");

            if (fmtCtx == null)
            {
                try
                {
                    prepareDecoding();
                }
                catch (InvalidOperationException e)
                {
                    Logger.Log($"SoftwareVideoDecoder setup faulted: {e}");
                    RawState = DecoderState.Faulted;
                    return;
                }
                catch (DllNotFoundException e)
                {
                    Logger.Log($"ffmpeg DLL not found: {e}");
                    RawState = DecoderState.Faulted;
                    return;
                }
            }

            decodingTask = Task.Factory.StartNew(
                () => decodingLoop(decodingTaskCancellationTokenSource.Token),
                decodingTaskCancellationTokenSource.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            Logger.Log("Started SoftwareVideoDecoder");
        }

        public override void StopDecoding(bool wait)
        {
            if (decodingTask == null)
            {
                return;
            }

            decodingTaskCancellationTokenSource.Cancel();

            if (wait)
            {
                decodingTask.Wait();
            }

            decodingTask = null;
            decodingTaskCancellationTokenSource.Dispose();
            decodingTaskCancellationTokenSource = new CancellationTokenSource();

            RawState = DecoderState.Ready;
        }

        public override void Seek(double pos)
        {
            if (!CanSeek)
            {
                throw new InvalidOperationException("This decoder cannot seek because the underlying stream used to decode the video does not support seeking.");
            }

            seekEvent.Reset();
            decoderActions.Add(() =>
            {
                ffmpeg.av_seek_frame(fmtCtx, stream->index, (long)(pos / timebase / 1000.0), ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(CodecCtx);
                skipOutputUntilTime = pos;
                decodedFrames.Clear();
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
            seekEvent.Wait();

            var frames = new List<DecodedFrame>(decodedFrames.Count);
            while (decodedFrames.TryDequeue(out var df))
                frames.Add(df);

            return frames;
        }

        protected virtual AVFrame* decodeNextFrame()
        {
            ffmpeg.av_frame_unref(frame);
            ffmpeg.av_frame_unref(receivedFrame);

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

                            return null;
                        }
                    } while (packet->stream_index != streamIndex);

                    res = ffmpeg.avcodec_send_packet(CodecCtx, packet);
                    if (res < 0)
                        Logger.Log($"Error {GetErrorMessage(res)} sending packet in VideoDecoder");
                }
                finally
                {
                    // TODO finally?
                    ffmpeg.av_packet_unref(packet);
                }

                receiveError = ffmpeg.avcodec_receive_frame(CodecCtx, frame);
            } while (receiveError == ffmpeg.AVERROR(ffmpeg.EAGAIN) && receiveError != 0);

            return frame;
        }

        private volatile float lastDecodedFrameTime;

        private AVFrame* outFrame; // TODO fix this

        private void decodeSingleFrame()
        {
            AVFrame* newFrame = decodeNextFrame();

            if (newFrame == null)
                return;

            double frameTime = (newFrame->best_effort_timestamp - stream->start_time) * timebase * 1000;

            if (!skipOutputUntilTime.HasValue || skipOutputUntilTime.Value < frameTime)
            {
                skipOutputUntilTime = null;

                ffmpeg.sws_scale(swsCtx, newFrame->data, newFrame->linesize, 0, CodecCtx->height, convDstData, convDstLineSize);

                // Maybe this could be pooled too, but probably not worth the effort without benchmarking
                outFrame->data = new byte_ptrArray8();
                outFrame->linesize = new int_array8();
                outFrame->data.UpdateFrom(convDstData);
                outFrame->linesize.UpdateFrom(convDstLineSize);

                if (!availableTextures.TryDequeue(out Texture tex))
                {
                    // Create new textures as needed
                    tex = new Texture(new VideoTexture(codecParams->width, codecParams->height));
                }

                var upload = new VideoTextureUpload(outFrame);

                tex.SetData(upload);
                decodedFrames.Enqueue(new DecodedFrame { Time = frameTime, Texture = tex });
            }

            lastDecodedFrameTime = (float)frameTime;
        }

        private void decodingLoop(CancellationToken token)
        {
            const int max_pending_frames = 3;
            outFrame = ffmpeg.av_frame_alloc(); // TODO what if exception
            outFrame->format = (int)AVPixelFormat.AV_PIX_FMT_RGBA;
            outFrame->width = CodecCtx->width;
            outFrame->height = CodecCtx->height;

            try
            {
                decodedFrames.ItemRemoved += (sender, _) =>
                {
                    if (!token.IsCancellationRequested && sender != null && ((ConcurrentNotifyQueue<DecodedFrame>)sender).Count < max_pending_frames)
                    {
                        // Insert an action that decodes a frame into the queue
                        decoderActions.Add(decodeSingleFrame);
                    }
                };

                // Decode initial frames
                for (int i = 0; i < max_pending_frames; ++i)
                {
                    decodeSingleFrame();
                }

                while (!token.IsCancellationRequested)
                {
                    Action cmd = decoderActions.Take(token);

                    cmd();
                }
            }
            catch (OperationCanceledException e) when (e.CancellationToken == token)
            {
                // pass
            }
            finally
            {
                fixed (AVFrame** framePtr = &outFrame)
                    ffmpeg.av_frame_free(framePtr);

                if (RawState != DecoderState.Faulted)
                    RawState = DecoderState.Stopped;
            }
        }

        private AVFormatContext* fmtCtx;
        private byte* buffer;
        private AVFrame* receivedFrame;
        protected AVCodecContext* CodecCtx;
        private AVStream* stream;
        private long duration;
        private double timebase;
        private AVCodecParameters* codecParams;
        private AVFrame* frame;
        private AVPacket* packet;

        private SwsContext* swsCtx;

        private byte[] managedCtxBuffer;

        private avio_alloc_context_read_packet readPacketCallback;
        private avio_alloc_context_seek seekCallback;

        private bool opened;

        private int streamIndex;

        private IntPtr conversionBuffer;
        private byte_ptrArray4 convDstData;
        private int_array4 convDstLineSize;

        protected virtual void prepareDecoding()
        {
            const int buffer_size = 4096;

            AVFormatContext* ctxPtr = ffmpeg.avformat_alloc_context();
            fmtCtx = ctxPtr;
            buffer = (byte*)ffmpeg.av_malloc(buffer_size);
            managedCtxBuffer = new byte[buffer_size];

            readPacketCallback = readPacket;
            seekCallback = seek;

            fmtCtx->pb = ffmpeg.avio_alloc_context(buffer, buffer_size, 0, (void*)handle.Handle, readPacketCallback, null, seekCallback);

            receivedFrame = ffmpeg.av_frame_alloc();

            int res = ffmpeg.avformat_open_input(&ctxPtr, "dummy", null, null);

            if (res < 0)
                throw new InvalidOperationException($"Error opening file or stream: {GetErrorMessage(res)}");

            res = ffmpeg.avformat_find_stream_info(fmtCtx, null);
            if (res < 0)
                throw new InvalidOperationException($"Error finding stream info: {GetErrorMessage(res)}");

            opened = true;

            AVCodec* codec = null;
            streamIndex = ffmpeg.av_find_best_stream(fmtCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);

            if (streamIndex < 0)
                throw new InvalidOperationException($"Could not find valid stream: {GetErrorMessage(streamIndex)}");

            CodecCtx = ffmpeg.avcodec_alloc_context3(codec);

            stream = fmtCtx->streams[streamIndex];
            duration = stream->duration <= 0 ? fmtCtx->duration : stream->duration;
            timebase = stream->time_base.GetValue();

            res = ffmpeg.avcodec_parameters_to_context(CodecCtx, stream->codecpar);

            if (res < 0)
                throw new InvalidOperationException($"Error filling codec context with parameters: {GetErrorMessage(res)}");

            codecParams = stream->codecpar;

            res = ffmpeg.avcodec_open2(CodecCtx, codec, null);

            if (res < 0)
                throw new InvalidOperationException($"Error trying to open codec with id {codecParams->codec_id}: {GetErrorMessage(res)}");

            prepareFilters();

            packet = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
        }

        private void prepareFilters()
        {
            const AVPixelFormat dest_fmt = AVPixelFormat.AV_PIX_FMT_RGBA;
            int w = CodecCtx->width;
            int h = CodecCtx->height;

            Logger.Log($"From {CodecCtx->pix_fmt} to {dest_fmt}");

            // 1 =  SWS_FAST_BILINEAR
            // https://www.ffmpeg.org/doxygen/trunk/swscale_8h_source.html#l00056
            // TODO check input and output format support
            swsCtx = ffmpeg.sws_getContext(w, h, CodecCtx->pix_fmt, w, h,
                dest_fmt, 1, null, null, null);

            int bufferSize = ffmpeg.av_image_get_buffer_size(dest_fmt, w, h, 1);
            conversionBuffer = Marshal.AllocHGlobal(bufferSize);

            ffmpeg.av_image_fill_arrays(ref convDstData, ref convDstLineSize, (byte*)conversionBuffer, dest_fmt, w, h, 1);
        }

        [MonoPInvokeCallback(typeof(avio_alloc_context_read_packet))]
        private static int readPacket(void* opaque, byte* buf, int bufSize)
        {
            var handle = new ObjectHandle<SoftwareVideoDecoder>((IntPtr)opaque);

            if (!handle.GetTarget(out SoftwareVideoDecoder decoder))
                return 0;

            if (bufSize > decoder.managedCtxBuffer.Length)
            {
                Logger.Log($"Reallocating managed context buffer: {decoder.managedCtxBuffer.Length} -> {bufSize}");
                decoder.managedCtxBuffer = new byte[bufSize];
            }

            int read = decoder.Stream.Read(decoder.managedCtxBuffer, 0, bufSize);
            Marshal.Copy(decoder.managedCtxBuffer, 0, (IntPtr)buf, read);
            return read;
        }

        [MonoPInvokeCallback(typeof(avio_alloc_context_seek))]
        private static long seek(void* opaque, long offset, int whence)
        {
            var handle = new ObjectHandle<SoftwareVideoDecoder>((IntPtr)opaque);
            if (!handle.GetTarget(out SoftwareVideoDecoder decoder))
                return -1;

            if (!decoder.Stream.CanSeek)
                throw new InvalidOperationException("Tried seeking on a video sourced by a non-seekable stream.");

            switch (whence)
            {
                case StdIo.SEEK_CUR:
                    decoder.Stream.Seek(offset, SeekOrigin.Current);
                    break;

                case StdIo.SEEK_END:
                    decoder.Stream.Seek(offset, SeekOrigin.End);
                    break;

                case StdIo.SEEK_SET:
                    decoder.Stream.Seek(offset, SeekOrigin.Begin);
                    break;

                case ffmpeg.AVSEEK_SIZE:
                    return decoder.Stream.Length;

                default:
                    return -1;
            }

            return decoder.Stream.Position;
        }
    }
}
