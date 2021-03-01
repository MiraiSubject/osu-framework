// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using JetBrains.Annotations;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Animations;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osuTK;
using AGffmpeg = FFmpeg.AutoGen.ffmpeg;

namespace osu.Framework.Graphics.Video
{
    /// <summary>
    /// Represents a composite that displays a video played back from a stream or a file.
    /// </summary>
    public unsafe class Video : AnimationClockComposite
    {
        /// <summary>
        /// Whether this video is in a buffering state, waiting on decoder or underlying stream.
        /// </summary>
        public bool Buffering { get; private set; }

        /// <summary>
        /// True if the video should loop after finishing its playback, false otherwise.
        /// </summary>
        public override bool Loop
        {
            get => base.Loop;
            set
            {
                if (decoder != null)
                    decoder.Looping = value;

                base.Loop = value;
            }
        }

        /// <summary>
        /// Whether the video decoding process has faulted.
        /// </summary>
        public bool IsFaulted => decoder?.IsFaulted ?? false;

        /// <summary>
        /// The current state of the <see cref="VideoDecoder"/>, as a bindable.
        /// </summary>
        public readonly IBindable<VideoDecoder.DecoderState> State = new Bindable<VideoDecoder.DecoderState>();

        internal double CurrentFrameTime => lastFrame?.Time ?? 0;

        internal int AvailableFrames => availableFrames.Count;

        private VideoDecoder decoder;

        private readonly Stream stream;

        private readonly Queue<DecodedFrame> availableFrames = new Queue<DecodedFrame>();

        private DecodedFrame lastFrame;

        /// <summary>
        /// The total number of frames processed by this instance.
        /// </summary>
        public int FramesProcessed { get; private set; }

        /// <summary>
        /// The length in milliseconds that the decoder can be out of sync before a seek is automatically performed.
        /// </summary>
        private const float lenience_before_seek = 2500;

        private bool isDisposed;

        internal VideoSprite Sprite;

        /// <summary>
        /// YUV->RGB conversion matrix based on the video colorspace
        /// </summary>
        public Matrix3 ConversionMatrix => decoder.GetConversionMatrix();

        /// <summary>
        /// Creates a new <see cref="Video"/>.
        /// </summary>
        /// <param name="filename">The video file.</param>
        /// <param name="startAtCurrentTime">Whether the current clock time should be assumed as the 0th video frame.</param>
        public Video(string filename, bool startAtCurrentTime = true)
            : this(File.OpenRead(filename), startAtCurrentTime)
        {
        }

        public override Drawable CreateContent() => Sprite = new VideoSprite(this) { RelativeSizeAxes = Axes.Both };

        private readonly FFmpegFuncs ffmpeg;

        /// <summary>
        /// Creates a new <see cref="Video"/>.
        /// </summary>
        /// <param name="stream">The video file stream.</param>
        /// <param name="startAtCurrentTime">Whether the current clock time should be assumed as the 0th video frame.</param>
        public Video([NotNull] Stream stream, bool startAtCurrentTime = true)
            : base(startAtCurrentTime)
        {
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
            ffmpeg = CreateFuncs();
            var type = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            var availableHWDecoders = new Dictionary<int, AVHWDeviceType>();
            var i = 0;
            while ((type = ffmpeg.av_hwdevice_iterate_types(type)) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            {
                availableHWDecoders.Add(i, type);
                Logger.Log($"{++i}. {type}");
            }
        }

        [BackgroundDependencyLoader]
        private void load(GameHost gameHost, ShaderManager shaders)
        {
            decoder = gameHost.CreateVideoDecoder(stream, Scheduler);
            decoder.Looping = Loop;
            State.BindTo(decoder.State);
            decoder.StartDecoding();

            Duration = decoder.Duration;
        }

        protected override void Update()
        {
            base.Update();

            var nextFrame = availableFrames.Count > 0 ? availableFrames.Peek() : null;

            if (nextFrame != null)
            {
                bool tooFarBehind = Math.Abs(PlaybackPosition - nextFrame.Time) > lenience_before_seek &&
                                    (!Loop ||
                                     (Math.Abs(PlaybackPosition - decoder.Duration - nextFrame.Time) > lenience_before_seek &&
                                      Math.Abs(PlaybackPosition + decoder.Duration - nextFrame.Time) > lenience_before_seek)
                                    );

                // we are too far ahead or too far behind
                if (tooFarBehind && decoder.CanSeek)
                {
                    Logger.Log($"Video too far out of sync ({nextFrame.Time}), seeking to {PlaybackPosition}");
                    decoder.Seek(PlaybackPosition);
                    decoder.ReturnFrames(availableFrames);
                    availableFrames.Clear();
                }
            }

            var frameTime = CurrentFrameTime;

            while (availableFrames.Count > 0 && checkNextFrameValid(availableFrames.Peek()))
            {
                if (lastFrame != null) decoder.ReturnFrames(new[] { lastFrame });
                lastFrame = availableFrames.Dequeue();

                var tex = lastFrame.Texture;

                // Check if the new frame has been uploaded so we don't display an old frame
                if ((tex?.TextureGL as VideoTexture)?.UploadComplete ?? false)
                {
                    Sprite.Texture = tex;
                    UpdateSizing();
                }
            }

            if (availableFrames.Count == 0)
            {
                foreach (var f in decoder.GetDecodedFrames())
                    availableFrames.Enqueue(f);
            }

            Buffering = decoder.IsRunning && availableFrames.Count == 0;

            if (frameTime != CurrentFrameTime)
                FramesProcessed++;
        }

        private bool checkNextFrameValid(DecodedFrame frame)
        {
            // in the case of looping, we may start a seek back to the beginning but still receive some lingering frames from the end of the last loop. these should be allowed to continue playing.
            if (Loop && Math.Abs((frame.Time - Duration) - PlaybackPosition) < lenience_before_seek)
                return true;

            return frame.Time <= PlaybackPosition && Math.Abs(frame.Time - PlaybackPosition) < lenience_before_seek;
        }

        protected override void Dispose(bool isDisposing)
        {
            if (isDisposed)
                return;

            base.Dispose(isDisposing);

            isDisposed = true;
            decoder?.Dispose();

            foreach (var f in availableFrames)
                f.Texture.Dispose();
        }

        protected override float GetFillAspectRatio() => Sprite.FillAspectRatio;

        protected override Vector2 GetCurrentDisplaySize() =>
            new Vector2(Sprite.Texture?.DisplayWidth ?? 0, Sprite.Texture?.DisplayHeight ?? 0);

        protected virtual FFmpegFuncs CreateFuncs()
        {
            // other frameworks should handle native libraries themselves
#if NET5_0
            AGffmpeg.GetOrLoadLibrary = name =>
            {
                int version = AGffmpeg.LibraryVersionMap[name];

                string libraryName = null;

                // "lib" prefix and extensions are resolved by .net core
                switch (RuntimeInfo.OS)
                {
                    case RuntimeInfo.Platform.MacOsx:
                        libraryName = $"{name}.{version}";
                        break;

                    case RuntimeInfo.Platform.Windows:
                        libraryName = $"{name}-{version}";
                        break;

                    case RuntimeInfo.Platform.Linux:
                        libraryName = name;
                        break;
                }

                return NativeLibrary.Load(libraryName, System.Reflection.Assembly.GetEntryAssembly(), DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
            };
#endif

            return new FFmpegFuncs
            {
                av_frame_alloc = AGffmpeg.av_frame_alloc,
                av_frame_free = AGffmpeg.av_frame_free,
                av_frame_unref = AGffmpeg.av_frame_unref,
                av_frame_get_buffer = AGffmpeg.av_frame_get_buffer,
                av_strdup = AGffmpeg.av_strdup,
                av_strerror = AGffmpeg.av_strerror,
                av_malloc = AGffmpeg.av_malloc,
                av_packet_alloc = AGffmpeg.av_packet_alloc,
                av_packet_unref = AGffmpeg.av_packet_unref,
                av_packet_free = AGffmpeg.av_packet_free,
                av_read_frame = AGffmpeg.av_read_frame,
                av_seek_frame = AGffmpeg.av_seek_frame,
                avcodec_find_decoder = AGffmpeg.avcodec_find_decoder,
                avcodec_open2 = AGffmpeg.avcodec_open2,
                avcodec_receive_frame = AGffmpeg.avcodec_receive_frame,
                avcodec_send_packet = AGffmpeg.avcodec_send_packet,
                avformat_alloc_context = AGffmpeg.avformat_alloc_context,
                avformat_close_input = AGffmpeg.avformat_close_input,
                avformat_find_stream_info = AGffmpeg.avformat_find_stream_info,
                avformat_open_input = AGffmpeg.avformat_open_input,
                avio_alloc_context = AGffmpeg.avio_alloc_context,
                sws_freeContext = AGffmpeg.sws_freeContext,
                sws_getContext = AGffmpeg.sws_getContext,
                sws_scale = AGffmpeg.sws_scale,
                av_hwdevice_iterate_types = AGffmpeg.av_hwdevice_iterate_types,
                av_hwdevice_ctx_create = AGffmpeg.av_hwdevice_ctx_create,
                av_find_best_stream = AGffmpeg.av_find_best_stream,
                avcodec_alloc_context3 = AGffmpeg.avcodec_alloc_context3,
                av_hwframe_transfer_data = AGffmpeg.av_hwframe_transfer_data,
                avcodec_parameters_to_context = AGffmpeg.avcodec_parameters_to_context,
                avcodec_flush_buffers = AGffmpeg.avcodec_flush_buffers
            };
        }
    }
}
