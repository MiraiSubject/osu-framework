// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Textures;
using osu.Framework.Graphics.Video.Decoders;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Threading;

namespace osu.Framework.Graphics.Video
{
    /// <summary>
    /// Represents a common interface for different video decoders that convert video streams into textures
    /// </summary>
    public abstract unsafe class VideoDecoder : IDisposable
    {
        public static VideoDecoder CreateVideoDecoder(Stream stream, Scheduler scheduler, AVHWDeviceType hwDevice = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        {
            // TODO test all versions
            // TODO dynamically detect features
            switch (RuntimeInfo.OS)
            {
                case RuntimeInfo.Platform.Windows:
                    return new HardwareVideoDecoder(stream, scheduler, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2);

                case RuntimeInfo.Platform.Linux:
                    return new HardwareVideoDecoder(stream, scheduler, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI);

                case RuntimeInfo.Platform.macOS:
                    return new HardwareVideoDecoder(stream, scheduler, AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX);

                case RuntimeInfo.Platform.iOS:
                case RuntimeInfo.Platform.Android:
                default:
                    return new SoftwareVideoDecoder(stream, scheduler);
            }
        }

#if NET5_0
        static VideoDecoder()
        {
            ffmpeg.GetOrLoadLibrary = name =>
            {
                int version = ffmpeg.LibraryVersionMap[name];

                // "lib" prefix and extensions are resolved by .net core
                string libraryName = RuntimeInfo.OS switch
                {
                    RuntimeInfo.Platform.macOS => $"{name}.{version}",
                    RuntimeInfo.Platform.Windows => $"{name}-{version}",
                    RuntimeInfo.Platform.Linux => name,
                    _ => null
                };

                if (libraryName == null)
                {
                    throw new NotSupportedException($"FFmpeg loading is not supported on {RuntimeInfo.OS} and .NET 5.0");
                }

                var assembly = System.Reflection.Assembly.GetEntryAssembly();

                if (assembly == null)
                {
                    throw new NotSupportedException("FFmpeg must not be loaded through native code");
                }

                return NativeLibrary.Load(libraryName, assembly, DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
            };
        }
#endif

        protected Stream Stream;
        protected readonly Scheduler Scheduler;

        protected VideoDecoder(Stream stream, Scheduler scheduler)
        {
            Stream = stream;
            Scheduler = scheduler;

            if (!Stream.CanRead)
            {
                throw new InvalidOperationException($"The given stream does not support reading. A stream used for a {nameof(VideoDecoder)} must support reading.");
            }

            RawState = DecoderState.Ready;

            Handle = new ObjectHandle<VideoDecoder>(this, GCHandleType.Normal);
        }

        ~VideoDecoder()
        {
            Dispose(false);
        }

        #region Disposal

        protected bool Disposed { get; private set; }

        public void Dispose()
        {
            Dispose(true);

            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool called)
        {
            if (Disposed)
                return;

            Disposed = true;

            StopDecoding(true);

            if (fmtCtx != null)
            {
                fixed (AVFormatContext** ptr = &fmtCtx)
                    ffmpeg.avformat_close_input(ptr);
            }

            if (codecCtx != null)
            {
                CodecContextCleanup(codecCtx);
                fixed (AVCodecContext** ptr = &codecCtx)
                    ffmpeg.avcodec_free_context(ptr);
            }

            Stream.Dispose();
            Stream = null;

            if (swsCtx != null)
                ffmpeg.sws_freeContext(swsCtx);

            while (availableTextures.TryDequeue(out var t))
                t.Dispose();

            while (DecodedFrames.TryDequeue(out var f))
                f.Texture.Dispose();

            Handle.Dispose();
            seekEvent.Dispose();

            DecoderActions.Dispose();
            DecoderActions = null;
        }

        #endregion

        public virtual double Duration => stream == null ? 0 : (stream->duration <= 0 ? fmtCtx->duration : stream->duration) * timebase * 1000;

        public virtual bool IsRunning => RawState == DecoderState.Running;

        public virtual bool IsFaulted => RawState == DecoderState.Faulted;

        public virtual bool CanSeek => Stream?.CanSeek == true;

        public virtual int MaxPendingFrames => 3;

        public virtual bool Looping { get; set; }

        /// <summary>
        /// The current state of the <see cref="VideoDecoder"/>, as a bindable
        /// </summary>
        public IBindable<DecoderState> State => bindableState;

        private readonly Bindable<DecoderState> bindableState = new Bindable<DecoderState>();
        private volatile DecoderState volatileState;

        protected DecoderState RawState
        {
            get => volatileState;
            set
            {
                if (volatileState == value)
                    return;

                Scheduler?.Add(() => bindableState.Value = value);
                volatileState = value;
            }
        }

        protected bool Running { get; private set; }

        /// <summary>
        /// Cancellation token used to cancel the asynchronous decoding task
        /// </summary>
        private CancellationTokenSource decodingTaskToken = new CancellationTokenSource(); // TODO used in a few non-epic ways atm

        /// <summary>
        /// Separate thread which performs the actual video decoding
        /// </summary>
        private Task decodingTask;

        /// <summary>
        /// Called to prepare the decoder for decoding.
        /// </summary>
        public void StartDecoding()
        {
            if (Running)
                throw new InvalidOperationException($"Attempted to start {GetType().Name} which was already started");

            try
            {
                prepareDecoding();
                prepareFilters();

                // Run decoding loop on a separate thread
                decodingTask = Task.Factory.StartNew(
                    () => decodingLoop(decodingTaskToken.Token),
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

            Running = true;

            Logger.Log($"Started {GetType().Name}");
        }

        /// <summary>
        /// Called when decoding is requrested to stop.
        /// </summary>
        /// <param name="wait"></param>
        public void StopDecoding(bool wait)
        {
            if (!Running)
                throw new InvalidOperationException($"Attemted to stop a {GetType().Name} which is not running");

            decodingTaskToken.Cancel();

            if (wait)
                decodingTask.Wait();

            // No need to dispose:
            // https://devblogs.microsoft.com/pfxteam/do-i-need-to-dispose-of-tasks/
            decodingTask = null;

            decodingTaskToken.Dispose();
            decodingTaskToken = new CancellationTokenSource();

            RawState = DecoderState.Ready;

            Running = false;
        }

        /// <summary>
        /// Queue which contains all buffered decoded frames
        /// </summary>
        protected ConcurrentNotifyQueue<DecodedFrame> DecodedFrames { get; } = new ConcurrentNotifyQueue<DecodedFrame>();

        /// <summary>
        /// Pool which contains all textures ussed by the decoder
        /// </summary>
        private readonly ConcurrentQueue<Texture> availableTextures = new ConcurrentQueue<Texture>();

        /// <summary>
        /// Queue of actions to be performed on the decoder thread
        /// </summary>
        protected BlockingCollection<Action> DecoderActions { get; private set; } = new BlockingCollection<Action>();

        /// <summary>
        /// Event to pause frame retrieval while a seek is occuring
        /// </summary>
        private readonly ManualResetEventSlim seekEvent = new ManualResetEventSlim(true);

        private double? skipOutputUntilTime;

        /// <summary>
        /// Seeks to a given position inside the video stream
        /// </summary>
        /// <param name="pos">Position in seconds to seek to</param>
        public void Seek(double pos)
        {
            if (!CanSeek)
                throw new InvalidOperationException("This decoder cannot seek because the underlying stream used to decode the video does not support seeking.");

            seekEvent.Reset();
            DecoderActions.Add(() =>
            {
                // Don't queue more frames
                DecodedFrames.BlockNotifications = true;

                // Seek
                ffmpeg.av_seek_frame(fmtCtx, stream->index, (long)(pos / timebase / 1000.0), ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(codecCtx);
                skipOutputUntilTime = pos;

                // Discard current frames
                DecodedFrames.Clear();
                DecodedFrames.BlockNotifications = false;

                // Decode until we're at the right position
                // Needs to be a while loop because the first few frames decoded after the seek may not be at the correct time,
                // so just skip until pos is reached
                while (DecodedFrames.Count < MaxPendingFrames)
                {
                    decodeSingleFrame(decodedFrame);
                }

                // Allow waiting GetDecodedFrames calls to continue
                seekEvent.Set();
            });
        }

        /// <summary>
        /// Called just after the <see cref="AVCodecContext"/> was created to perform decoder-specific setup
        /// </summary>
        protected virtual void CodecContextSetup(AVCodecContext* ctx) { }

        /// <summary>
        /// Called just before the <see cref="AVCodecContext"/> is freed to perform decoder-specific cleanup
        /// </summary>
        protected virtual void CodecContextCleanup(AVCodecContext* ctx) { }

        /// <summary>
        /// The expected pixel format for raw frames.
        /// </summary>
        protected virtual AVPixelFormat OutputPixelFormat => codecCtx->pix_fmt;

        /// <summary>
        /// Called just after decoding a frame to apply decoder-specific changes
        /// </summary>
        protected virtual AVFrame* TransformDecodedFrame(AVFrame* frame) => frame;

        private AVFormatContext* fmtCtx;
        private AVCodecContext* codecCtx;
        private AVStream* stream;
        private double timebase;
        private AVPacket* packet;
        private AVFrame* decodedFrame;
        private AVFrame* outFrame;

        private SwsContext* swsCtx;

        private int streamIndex;

        private IntPtr conversionBuffer;
        private byte_ptrArray4 convDstData;
        private int_array4 convDstLineSize;

        private volatile float lastDecodedFrameTime;

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

                    res = ffmpeg.avcodec_send_packet(codecCtx, packet);
                    if (res < 0)
                        Logger.Log($"Error {GetErrorMessage(res)} sending packet in {GetType().Name}");
                }
                finally
                {
                    ffmpeg.av_packet_unref(packet);
                }

                receiveError = ffmpeg.avcodec_receive_frame(codecCtx, frame);
            } while (receiveError == ffmpeg.AVERROR(ffmpeg.EAGAIN) && receiveError != 0);

            return true;
        }

        private void decodeSingleFrame(AVFrame* frame)
        {
            if (!decodeNextFrame(frame))
                return;

            frame = TransformDecodedFrame(frame);

            double frameTime = (frame->best_effort_timestamp - stream->start_time) * timebase * 1000;

            if (!skipOutputUntilTime.HasValue || skipOutputUntilTime.Value < frameTime)
            {
                skipOutputUntilTime = null;

                ffmpeg.sws_scale(swsCtx, frame->data, frame->linesize, 0, codecCtx->height, convDstData, convDstLineSize);

                // Maybe this could be pooled too, but probably not worth the effort without benchmarking
                outFrame->data = new byte_ptrArray8();
                outFrame->linesize = new int_array8();
                outFrame->data.UpdateFrom(convDstData);
                outFrame->linesize.UpdateFrom(convDstLineSize);

                if (!availableTextures.TryDequeue(out Texture tex))
                {
                    // Create new textures as needed
                    tex = new Texture(new VideoTexture(codecCtx->width, codecCtx->height));
                }

                var upload = new VideoTextureUpload(outFrame);

                tex.SetData(upload);
                DecodedFrames.Enqueue(new DecodedFrame { Time = frameTime, Texture = tex });
            }

            lastDecodedFrameTime = (float)frameTime;
        }

        private void decodingLoop(CancellationToken token)
        {
            outFrame = ffmpeg.av_frame_alloc();
            outFrame->format = (int)AVPixelFormat.AV_PIX_FMT_RGBA;
            outFrame->width = codecCtx->width;
            outFrame->height = codecCtx->height;

            decodedFrame = ffmpeg.av_frame_alloc();

            packet = ffmpeg.av_packet_alloc();

            void frameDecodeCallback(object sender, EventArgs _)
            {
                if (!token.IsCancellationRequested && sender != null && ((ConcurrentNotifyQueue<DecodedFrame>)sender).Count < MaxPendingFrames)
                {
                    // Insert an action that decodes a frame into the queue
                    DecoderActions.Add(() => decodeSingleFrame(decodedFrame));
                }
            }

            try
            {
                DecodedFrames.ItemRemoved += frameDecodeCallback;

                // Decode initial frames
                while (DecodedFrames.Count < MaxPendingFrames)
                    decodeSingleFrame(decodedFrame);

                while (!token.IsCancellationRequested)
                    DecoderActions.Take(token)();
            }
            catch (OperationCanceledException e) when (e.CancellationToken == token)
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

                DecodedFrames.ItemRemoved -= frameDecodeCallback;
            }
        }

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
            codecCtx = ffmpeg.avcodec_alloc_context3(codec);

            CodecContextSetup(codecCtx);

            stream = fmtCtx->streams[streamIndex];
            timebase = stream->time_base.GetValue();

            // Copy necessary information from the stream's info to the decoder context
            res = ffmpeg.avcodec_parameters_to_context(codecCtx, stream->codecpar);
            if (res < 0)
                throw new InvalidOperationException($"Error filling codec context with parameters: {GetErrorMessage(res)}");

            // Open the codec context for decoding
            res = ffmpeg.avcodec_open2(codecCtx, codec, null);
            if (res < 0)
                throw new InvalidOperationException($"Error trying to open codec with id {codecCtx->codec_id}: {GetErrorMessage(res)}");
        }

        private void prepareFilters()
        {
            const AVPixelFormat dest_fmt = AVPixelFormat.AV_PIX_FMT_RGBA;
            AVPixelFormat srcFmt = OutputPixelFormat;
            int w = codecCtx->width;
            int h = codecCtx->height;

            Logger.Log($"Conversion is from {srcFmt} to {dest_fmt}");

            // 1 = SWS_FAST_BILINEAR
            // https://www.ffmpeg.org/doxygen/trunk/swscale_8h_source.html#l00056
            // TODO check input and output format support
            swsCtx = ffmpeg.sws_getContext(w, h, srcFmt, w, h,
                dest_fmt, 1, null, null, null);

            int bufferSize = ffmpeg.av_image_get_buffer_size(dest_fmt, w, h, 1);
            conversionBuffer = Marshal.AllocHGlobal(bufferSize);

            ffmpeg.av_image_fill_arrays(ref convDstData, ref convDstLineSize, (byte*)conversionBuffer, dest_fmt, w, h, 1);
        }

        public void ReturnFrames(IEnumerable<DecodedFrame> frames)
        {
            foreach (var f in frames)
            {
                ((VideoTexture)f.Texture.TextureGL).FlushUploads();
                availableTextures.Enqueue(f.Texture);
            }
        }

        public IEnumerable<DecodedFrame> GetDecodedFrames()
        {
            // Wait for any pending seek to finish
            seekEvent.Wait();

            var frames = new List<DecodedFrame>(DecodedFrames.Count);

            while (DecodedFrames.TryDequeue(out var df))
                frames.Add(df);

            return frames;
        }

        /// <summary>
        /// Handle that can be passed to unmanaged code to maintain a reference to this object
        /// </summary>
        protected ObjectHandle<VideoDecoder> Handle { get; }

        private byte[] managedReadBuffer = new byte[4096];

        /// <summary>
        /// Provides a callback for AVIO to read data from a managed stream.
        /// </summary>
        [MonoPInvokeCallback(typeof(avio_alloc_context_read_packet))]
        protected static int RawRead(void* opaque, byte* buf, int bufSize)
        {
            var handle = new ObjectHandle<VideoDecoder>((IntPtr)opaque);

            if (!handle.GetTarget(out VideoDecoder decoder))
                return 0;

            if (bufSize > decoder.managedReadBuffer.Length)
            {
                Logger.Log($"Reallocating managed read buffer: {decoder.managedReadBuffer.Length} -> {bufSize}");
                decoder.managedReadBuffer = new byte[bufSize];
            }

            int read = decoder.Stream.Read(decoder.managedReadBuffer, 0, bufSize);
            Marshal.Copy(decoder.managedReadBuffer, 0, (IntPtr)buf, read);
            return read;
        }

        protected readonly avio_alloc_context_read_packet RawReadDelegate = RawRead;

        /// <summary>
        /// Provides a callback for AVIO to seek a managed stream with.
        /// </summary>
        [MonoPInvokeCallback(typeof(avio_alloc_context_seek))]
        protected static long RawSeek(void* opaque, long offset, int whence)
        {
            var handle = new ObjectHandle<VideoDecoder>((IntPtr)opaque);
            if (!handle.GetTarget(out VideoDecoder decoder))
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

        protected readonly avio_alloc_context_seek RawSeekDelegate = RawSeek;

        /// <summary>
        /// Formats an error message from an error code.
        /// </summary>
        /// <param name="code">The error code to format a string for</param>
        /// <returns>String representation of the error code.</returns>
        protected static string GetErrorMessage(int code)
        {
            byte[] buffer = new byte[ffmpeg.AV_ERROR_MAX_STRING_SIZE];

            int strErrorCode;

            fixed (byte* bufPtr = buffer)
            {
                strErrorCode = ffmpeg.av_strerror(code, bufPtr, (ulong)buffer.Length);
            }

            if (strErrorCode < 0)
                return $"{code} (av_strerror failed with code {strErrorCode})";

            var messageLength = Math.Max(0, Array.IndexOf(buffer, (byte)0));
            return Encoding.UTF8.GetString(buffer[..messageLength]);
        }

        /// <summary>
        /// Represents the possible states the decoder can be in.
        /// </summary>
        public enum DecoderState
        {
            /// <summary>
            /// The decoder is ready to begin decoding. This is the default state before the decoder starts operations.
            /// </summary>
            Ready = 0,

            /// <summary>
            /// The decoder is currently running and decoding frames.
            /// </summary>
            Running = 1,

            /// <summary>
            /// The decoder has faulted with an exception.
            /// </summary>
            Faulted = 2,

            /// <summary>
            /// The decoder has reached the end of the video data.
            /// </summary>
            EndOfStream = 3,

            /// <summary>
            /// The decoder has been completely stopped and cannot be resumed.
            /// </summary>
            Stopped = 4,
        }
    }
}
