// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using FFmpeg.AutoGen;
using osu.Framework.Logging;
using osu.Framework.Threading;

namespace osu.Framework.Graphics.Video.Decoders
{
    public unsafe class HardwareVideoDecoder : VideoDecoder
    {
        private readonly AVHWDeviceType deviceType;

        public HardwareVideoDecoder(Stream stream, Scheduler scheduler, AVHWDeviceType kind)
            : base(stream, scheduler)
        {
            deviceType = kind;
        }

        protected override void Dispose(bool called)
        {
            base.Dispose(called);

            fixed (AVFrame** framePtr = &receivedFrame)
                ffmpeg.av_frame_free(framePtr);
        }

        protected override AVPixelFormat OutputPixelFormat => AVPixelFormat.AV_PIX_FMT_NV12;

        protected override void CodecContextSetup(AVCodecContext* ctx)
        {
            int res = ffmpeg.av_hwdevice_ctx_create(&ctx->hw_device_ctx, deviceType, null, null, 0);
            if (res != 0)
                throw new InvalidOperationException($"Error creating hardware device context: {GetErrorMessage(res)}");

            receivedFrame = ffmpeg.av_frame_alloc();

            Logger.Log("Initialised hardware device context.");
        }

        private AVFrame* receivedFrame;

        protected override AVFrame* TransformDecodedFrame(AVFrame* frame)
        {
            int res = ffmpeg.av_hwframe_transfer_data(receivedFrame, frame, 0);
            if (res < 0)
                throw new InvalidOperationException("Failed to transfer data from hardware device");

            // Hardware frame has negative long as best effort timestamp so copy it from the original frame.
            receivedFrame->best_effort_timestamp = frame->best_effort_timestamp;

            ffmpeg.av_frame_unref(frame);
            return receivedFrame;
        }
    }
}
