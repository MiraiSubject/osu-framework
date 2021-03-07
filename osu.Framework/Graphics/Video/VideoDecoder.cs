// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FFmpeg.AutoGen;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
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
            return new SoftwareVideoDecoder(stream, scheduler);
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
            Dispose();
        }

        #region Disposal

        protected bool Disposed { get; private set; }

        public virtual void Dispose()
        {
            Handle.Dispose();

            Disposed = true;
        }

        #endregion

        public abstract double Duration { get; }

        public virtual bool IsRunning => RawState == DecoderState.Running;

        public virtual bool IsFaulted => RawState == DecoderState.Faulted;

        public virtual bool CanSeek => Stream?.CanSeek == true;

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

        public abstract bool Looping { get; internal set; }

        public abstract void StartDecoding();

        public abstract void StopDecoding(bool wait);

        public abstract void Seek(double pos);

        public abstract void ReturnFrames(IEnumerable<DecodedFrame> frames);

        public abstract IEnumerable<DecodedFrame> GetDecodedFrames();

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
