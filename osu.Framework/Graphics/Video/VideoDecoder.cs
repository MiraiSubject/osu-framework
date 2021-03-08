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
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Threading;

namespace osu.Framework.Graphics.Video
{
    /// <summary>
    /// Decodes a video stream into individual frames
    /// </summary>
    public unsafe class VideoDecoder : IDisposable
    {
        public static VideoDecoder CreateVideoDecoder(Stream stream, Scheduler scheduler, AVHWDeviceType hwDevice = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        {
            // TODO test all versions
            // TODO dynamically detect features
            switch (RuntimeInfo.OS)
            {
                case RuntimeInfo.Platform.Windows:
                    return new VideoDecoder(stream, scheduler, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2);

                case RuntimeInfo.Platform.Linux:
                    return new VideoDecoder(stream, scheduler, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI);

                case RuntimeInfo.Platform.macOS:
                case RuntimeInfo.Platform.iOS:
                    return new VideoDecoder(stream, scheduler, AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX);

                case RuntimeInfo.Platform.Android:
                default:
                    return new VideoDecoder(stream, scheduler);
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

        private Stream stream;
        private readonly Scheduler scheduler;

        protected VideoDecoder(Stream stream, Scheduler scheduler)
        {
            this.stream = stream;
            this.scheduler = scheduler;

            if (!this.stream.CanRead)
            {
                throw new InvalidOperationException($"The given stream does not support reading. A stream used for a {nameof(VideoDecoder)} must support reading.");
            }

            rawState = DecoderState.Ready;

            Handle = new ObjectHandle<VideoDecoder>(this, GCHandleType.Normal);
        }

        private VideoDecoder(Stream stream, Scheduler scheduler, AVHWDeviceType hwDevice)
            : this(stream, scheduler)
        {
            this.hwDevice = hwDevice;
        }

        ~VideoDecoder()
        {
            Dispose();
        }

        #region Disposal

        protected bool Disposed { get; private set; }

        public void Dispose()
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
                fixed (AVCodecContext** ptr = &codecCtx)
                    ffmpeg.avcodec_free_context(ptr);
            }

            stream.Dispose();
            stream = null;

            if (swsCtx != null)
                ffmpeg.sws_freeContext(swsCtx);

            while (availableTextures.TryDequeue(out var t))
                t.Dispose();

            while (decodedFrames.TryDequeue(out var f))
                f.Texture.Dispose();

            Handle.Dispose();
            seekEvent.Dispose();

            decoderActions.Dispose();

            GC.SuppressFinalize(this);
        }

        #endregion

        public double Duration => selectedStream == null ? 0 : (selectedStream->duration <= 0 ? fmtCtx->duration : selectedStream->duration) * timebase * 1000;

        public bool IsRunning => rawState == DecoderState.Running;

        public bool IsFaulted => rawState == DecoderState.Faulted;

        public bool CanSeek => stream?.CanSeek == true;

        public bool Looping { get; set; }

        public bool Running { get; private set; }

        public bool IsHardwareDecoder => hwDevice != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;

        public bool IsUsingHardwareDecoder => codecCtx->hwaccel != null;

        private const int max_pending_frames = 3;

        /// <summary>
        /// The current state of the <see cref="VideoDecoder"/>, as a bindable
        /// </summary>
        public IBindable<DecoderState> State => bindableState;

        private readonly Bindable<DecoderState> bindableState = new Bindable<DecoderState>();
        private volatile DecoderState volatileState;

        private DecoderState rawState
        {
            get => volatileState;
            set
            {
                if (volatileState == value)
                    return;

                scheduler?.Add(() => bindableState.Value = value);
                volatileState = value;
            }
        }

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
                rawState = DecoderState.Faulted;
            }
            catch (DllNotFoundException e)
            {
                Logger.Log($"ffmpeg DLL not found: {e}");
                rawState = DecoderState.Faulted;
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

            rawState = DecoderState.Ready;

            Running = false;
        }

        /// <summary>
        /// Queue which contains all buffered decoded frames
        /// </summary>
        private readonly ConcurrentNotifyQueue<DecodedFrame> decodedFrames = new ConcurrentNotifyQueue<DecodedFrame>();

        /// <summary>
        /// Pool which contains all textures ussed by the decoder
        /// </summary>
        private readonly ConcurrentQueue<Texture> availableTextures = new ConcurrentQueue<Texture>();

        /// <summary>
        /// Queue of actions to be performed on the decoder thread
        /// </summary>
        private readonly BlockingCollection<Action> decoderActions = new BlockingCollection<Action>();

        /// <summary>
        /// Event to pause frame retrieval while a seek is occuring
        /// </summary>
        private readonly ManualResetEventSlim seekEvent = new ManualResetEventSlim(true);

        /// <summary>
        /// Seeks to a given position inside the video stream
        /// </summary>
        /// <param name="pos">Position in seconds to seek to</param>
        public void Seek(double pos)
        {
            if (!CanSeek)
                throw new InvalidOperationException("This decoder cannot seek because the underlying stream used to decode the video does not support seeking.");

            seekEvent.Reset();
            decoderActions.Add(() =>
            {
                // Don't queue more frames
                decodedFrames.BlockNotifications = true;

                // Seek
                ffmpeg.av_seek_frame(fmtCtx, selectedStream->index, (long)(pos / timebase / 1000.0), ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(codecCtx);
                skipOutputUntilTime = pos;

                // Discard current frames
                decodedFrames.Clear();
                decodedFrames.BlockNotifications = false;

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

        /// <summary>
        /// The expected pixel format for raw frames.
        /// </summary>
        public AVPixelFormat OutputPixelFormat => IsUsingHardwareDecoder ? AVPixelFormat.AV_PIX_FMT_NV12 : codecCtx->pix_fmt;

        private readonly AVHWDeviceType hwDevice = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;

        private double? skipOutputUntilTime;

        private AVFormatContext* fmtCtx;
        private AVCodecContext* codecCtx;
        private AVStream* selectedStream;
        private double timebase;
        private AVPacket* packet;
        private AVFrame* decodedFrame;
        private AVFrame* outFrame;
        private AVFrame* hwFrame;

        private SwsContext* swsCtx;

        private int streamIndex;

        private IntPtr conversionBuffer;
        private byte_ptrArray4 convDstData;
        private int_array4 convDstLineSize;

        private volatile float lastDecodedFrameTime;

        private bool decodeNextFrame(AVFrame* frame)
        {
            ffmpeg.av_frame_unref(frame);
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

                            rawState = DecoderState.Ready;
                            Thread.Sleep(1);
                        }
                        else
                        {
                            // EOF
                            Logger.Log($"EOF, Looping: {Looping}, previous time: {lastDecodedFrameTime}");
                            if (Looping)
                                Seek(0);
                            else
                                rawState = DecoderState.EndOfStream;

                            return false;
                        }
                    } while (packet->stream_index != streamIndex);

                    res = ffmpeg.avcodec_send_packet(codecCtx, packet);
                    if (res < 0)
                        Logger.Log($"Error {res} ({GetErrorMessage(res)}) while sending packet in {GetType().Name}");
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

            // Filters and hardware frame are lazy-initialized
            if (swsCtx == null)
            {
                prepareFilters();

                // Check if fallback was used
                if (IsUsingHardwareDecoder)
                {
                    Logger.Log($"Using {Marshal.PtrToStringAnsi((IntPtr)codecCtx->hwaccel->name)} for {Marshal.PtrToStringAnsi((IntPtr)codecCtx->codec->name)}");
                    hwFrame = ffmpeg.av_frame_alloc();
                }
                else if (IsHardwareDecoder && !IsUsingHardwareDecoder)
                {
                    Logger.Log(
                        $"Falling back to software decoder for {Marshal.PtrToStringAnsi((IntPtr)codecCtx->codec->name)}");
                }
            }

            if (IsUsingHardwareDecoder)
            {
                int res = ffmpeg.av_hwframe_transfer_data(hwFrame, frame, 0);
                if (res < 0)
                    throw new InvalidOperationException("Failed to transfer data from hardware device");

                // Hardware frame has negative long as best effort timestamp so copy it from the original frame.
                hwFrame->best_effort_timestamp = frame->best_effort_timestamp;

                ffmpeg.av_frame_unref(frame);
                frame = hwFrame;
            }

            double frameTime = (frame->best_effort_timestamp - selectedStream->start_time) * timebase * 1000;

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
                decodedFrames.Enqueue(new DecodedFrame { Time = frameTime, Texture = tex });
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
                if (!token.IsCancellationRequested && sender != null && ((ConcurrentNotifyQueue<DecodedFrame>)sender).Count < max_pending_frames)
                {
                    // Insert an action that decodes a frame into the queue
                    decoderActions.Add(() => decodeSingleFrame(decodedFrame), CancellationToken.None);
                }
            }

            try
            {
                decodedFrames.ItemRemoved += frameDecodeCallback;

                // Decode initial frames
                while (decodedFrames.Count < max_pending_frames)
                    decodeSingleFrame(decodedFrame);

                while (!token.IsCancellationRequested)
                    decoderActions.Take(token)();
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

                if (IsUsingHardwareDecoder)
                {
                    fixed (AVFrame** framePtr = &hwFrame)
                        ffmpeg.av_frame_free(framePtr);
                }

                if (rawState != DecoderState.Faulted)
                    rawState = DecoderState.Stopped;

                decodedFrames.ItemRemoved -= frameDecodeCallback;
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

            if (IsHardwareDecoder)
            {
                res = ffmpeg.av_hwdevice_ctx_create(&codecCtx->hw_device_ctx, hwDevice, null, null, 0);
                if (res != 0)
                    throw new InvalidOperationException($"Error creating hardware device context: {GetErrorMessage(res)}");
            }

            selectedStream = fmtCtx->streams[streamIndex];
            timebase = selectedStream->time_base.GetValue();

            // Copy necessary information from the stream's info to the decoder context
            res = ffmpeg.avcodec_parameters_to_context(codecCtx, selectedStream->codecpar);
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
            int w = codecCtx->width;
            int h = codecCtx->height;

            Logger.Log($"Conversion is from {OutputPixelFormat} to {dest_fmt}");

            // 1 = SWS_FAST_BILINEAR
            // https://www.ffmpeg.org/doxygen/trunk/swscale_8h_source.html#l00056
            // TODO check input and output format support
            swsCtx = ffmpeg.sws_getContext(w, h, OutputPixelFormat, w, h,
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

            var frames = new List<DecodedFrame>(decodedFrames.Count);

            while (decodedFrames.TryDequeue(out var df))
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

            int read = decoder.stream.Read(decoder.managedReadBuffer, 0, bufSize);
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

            if (!decoder.stream.CanSeek)
                throw new InvalidOperationException("Tried seeking on a video sourced by a non-seekable stream.");

            switch (whence)
            {
                case StdIo.SEEK_CUR:
                    decoder.stream.Seek(offset, SeekOrigin.Current);
                    break;

                case StdIo.SEEK_END:
                    decoder.stream.Seek(offset, SeekOrigin.End);
                    break;

                case StdIo.SEEK_SET:
                    decoder.stream.Seek(offset, SeekOrigin.Begin);
                    break;

                case ffmpeg.AVSEEK_SIZE:
                    return decoder.stream.Length;

                default:
                    return -1;
            }

            return decoder.stream.Position;
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
