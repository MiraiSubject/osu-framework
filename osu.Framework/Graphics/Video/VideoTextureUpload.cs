// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.Textures;
using osuTK.Graphics.ES30;
using FFmpeg.AutoGen;

namespace osu.Framework.Graphics.Video
{
    public unsafe class VideoTextureUpload : ArrayPoolTextureUpload
    {
        public AVFrame* Frame;
        public override PixelFormat Format => PixelFormat.Rgba;

        /// <summary>
        /// Sets the frame cotaining the data to be uploaded
        /// </summary>
        /// <param name="frame">The libav frame to upload.</param>
        public VideoTextureUpload(AVFrame* frame)
            : base(0, 0)
        {
            Frame = frame;
        }
    }
}
