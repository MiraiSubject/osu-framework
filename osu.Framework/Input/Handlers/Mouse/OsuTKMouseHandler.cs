﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Drawing;
using osu.Framework.Platform;
using osu.Framework.Threading;
using osuTK;

namespace osu.Framework.Input.Handlers.Mouse
{
    internal class OsuTKMouseHandler : OsuTKMouseHandlerBase
    {
        private ScheduledDelegate scheduled;

        private OsuTKMouseState lastPollState;
        private OsuTKMouseState lastEventState;

        public override bool Initialize(GameHost host)
        {
            base.Initialize(host);

            Enabled.BindValueChanged(e =>
            {
                if (e.NewValue)
                {
                    host.Window.MouseMove += handleMouseEvent;
                    host.Window.MouseDown += handleMouseEvent;
                    host.Window.MouseUp += handleMouseEvent;
                    host.Window.MouseWheel += handleMouseEvent;

                    // polling is used to keep a valid mouse position when we aren't receiving events.
                    osuTK.Input.MouseState? lastCursorState = null;
                    host.InputThread.Scheduler.Add(scheduled = new ScheduledDelegate(delegate
                    {
                        // we should be getting events if the mouse is inside the window.
                        if (MouseInWindow || !host.Window.Visible || host.Window.WindowState == osuTK.WindowState.Minimized) return;

                        var cursorState = osuTK.Input.Mouse.GetCursorState();

                        if (cursorState.Equals(lastCursorState)) return;

                        lastCursorState = cursorState;

                        var mapped = host.Window.PointToClient(new Point(cursorState.X, cursorState.Y));

                        var newState = new OsuTKPollMouseState(cursorState, host.IsActive.Value, new Vector2(mapped.X, mapped.Y));
                        HandleState(newState, lastPollState, true);
                        lastPollState = newState;
                    }, 0, 1000.0 / 60));
                }
                else
                {
                    scheduled?.Cancel();

                    host.Window.MouseMove -= handleMouseEvent;
                    host.Window.MouseDown -= handleMouseEvent;
                    host.Window.MouseUp -= handleMouseEvent;
                    host.Window.MouseWheel -= handleMouseEvent;

                    lastPollState = null;
                    lastEventState = null;
                }
            }, true);

            return true;
        }

        private void handleMouseEvent(object sender, osuTK.Input.MouseEventArgs e)
        {
            if (!MouseInWindow)
                return;

            if (e.Mouse.X < 0 || e.Mouse.Y < 0)
                // todo: investigate further why we are getting negative values from osuTK events
                // on windows when crossing centre screen boundaries (width/2 or height/2).
                return;

            var newState = new OsuTKEventMouseState(e.Mouse, Host.IsActive.Value, null);
            HandleState(newState, lastEventState, true);
            lastEventState = newState;
        }
    }
}
