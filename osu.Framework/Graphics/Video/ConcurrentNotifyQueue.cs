// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;

namespace osu.Framework.Graphics.Video
{
    public class ConcurrentNotifyQueue<T> : ConcurrentQueue<T>
    {
        public event EventHandler ItemRemoved;

        public new bool TryDequeue(out T result)
        {
            bool success = base.TryDequeue(out result);

            if (success)
            {
                ItemRemoved?.Invoke(this, EventArgs.Empty);
            }

            return success;
        }
    }
}
