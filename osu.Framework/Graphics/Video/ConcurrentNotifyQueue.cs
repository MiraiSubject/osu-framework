// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using osu.Framework.Logging;

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

        public new void Clear()
        {
            int old_count = Count;
            base.Clear();

            // Invoke once for every item that used to be in the queue
            for (int i = 0; i < old_count; ++i)
            {
                ItemRemoved?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
