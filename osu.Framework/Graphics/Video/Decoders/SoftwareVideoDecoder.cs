// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using osu.Framework.Threading;

namespace osu.Framework.Graphics.Video.Decoders
{
    /// <summary>
    /// Basic video decoder that uses software decoding.
    /// </summary>
    public class SoftwareVideoDecoder : VideoDecoder
    {
        public SoftwareVideoDecoder(Stream stream, Scheduler scheduler)
            : base(stream, scheduler)
        {
        }
    }
}
