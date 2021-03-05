// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FFmpeg.AutoGen;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Video.Decoders;
using osu.Framework.Threading;
using osuTK;

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
        }

        ~VideoDecoder()
        {
            Dispose();
        }

        public abstract void Dispose();

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

        protected virtual FFmpegFuncs FFmpeg { get; } = loadFFmpeg();

        protected string GetErrorMessage(int code)
        {
            const ulong buffer_size = 256;
            byte[] buffer = new byte[buffer_size];

            int strErrorCode;

            fixed (byte* bufPtr = buffer)
            {
                strErrorCode = FFmpeg.av_strerror(code, bufPtr, buffer_size);
            }

            if (strErrorCode < 0)
                return $"{code} (av_strerror failed with code {strErrorCode})";

            var messageLength = Math.Max(0, Array.IndexOf(buffer, (byte)0));
            return Encoding.ASCII.GetString(buffer[..messageLength]);
        }

        private static FFmpegFuncs loadFFmpeg()
        {
#if NET5_0
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

                return NativeLibrary.Load(libraryName, System.Reflection.Assembly.GetEntryAssembly(), DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
            };
#endif

            return new FFmpegFuncs
            {
                av_frame_alloc = ffmpeg.av_frame_alloc,
                av_frame_free = ffmpeg.av_frame_free,
                av_frame_unref = ffmpeg.av_frame_unref,
                av_frame_get_buffer = ffmpeg.av_frame_get_buffer,
                av_strdup = ffmpeg.av_strdup,
                av_strerror = ffmpeg.av_strerror,
                av_malloc = ffmpeg.av_malloc,
                av_packet_alloc = ffmpeg.av_packet_alloc,
                av_packet_unref = ffmpeg.av_packet_unref,
                av_packet_free = ffmpeg.av_packet_free,
                av_read_frame = ffmpeg.av_read_frame,
                av_seek_frame = ffmpeg.av_seek_frame,
                avcodec_find_decoder = ffmpeg.avcodec_find_decoder,
                avcodec_open2 = ffmpeg.avcodec_open2,
                avcodec_receive_frame = ffmpeg.avcodec_receive_frame,
                avcodec_send_packet = ffmpeg.avcodec_send_packet,
                avformat_alloc_context = ffmpeg.avformat_alloc_context,
                avformat_close_input = ffmpeg.avformat_close_input,
                avformat_find_stream_info = ffmpeg.avformat_find_stream_info,
                avformat_open_input = ffmpeg.avformat_open_input,
                avio_alloc_context = ffmpeg.avio_alloc_context,
                sws_freeContext = ffmpeg.sws_freeContext,
                sws_getContext = ffmpeg.sws_getContext,
                sws_scale = ffmpeg.sws_scale,
                av_hwdevice_iterate_types = ffmpeg.av_hwdevice_iterate_types,
                av_hwdevice_ctx_create = ffmpeg.av_hwdevice_ctx_create,
                av_find_best_stream = ffmpeg.av_find_best_stream,
                avcodec_alloc_context3 = ffmpeg.avcodec_alloc_context3,
                av_hwframe_transfer_data = ffmpeg.av_hwframe_transfer_data,
                avcodec_parameters_to_context = ffmpeg.avcodec_parameters_to_context,
                avcodec_flush_buffers = ffmpeg.avcodec_flush_buffers
            };
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
