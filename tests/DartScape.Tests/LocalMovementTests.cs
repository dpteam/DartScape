using System;
using System.Collections.Generic;
using DartScape.Client;
using DartScape.Game;

namespace DartScape.Tests
{
    /// <summary>
    /// Tests for client-side movement prediction.
    /// </summary>
    /// <remarks>
    /// The client moves its own player and the server validates, which is the only way a step
    /// can start on the click instead of a round trip later. That makes the timing rules worth
    /// testing on their own: the first step must happen immediately, one step per tick after
    /// that, the interpolation fraction must be in phase with the steps, and the local position
    /// must survive the server reporting a slightly older one.
    /// </remarks>
    public static class LocalMovementTests
    {
        public static void Run()
        {
            Harness.Section("Local movement prediction");

            Harness.Test("the first step happens immediately, not on the next tick", () =>
            {
                ClientWorld world = NewWorld();
                ClientPlayer me = world.Me;

                long now = 1000;
                world.SetLocalPath(Path(me.X, me.Y, 3), now);

                Harness.Equal(3223, me.X, "moved one tile east on the click");
                Harness.Equal(3222, me.LastX, "the previous tile is kept for interpolation");
                Harness.True(world.IsLocalWalking, "still walking");
            });

            Harness.Test("one step per tick, not per frame", () =>
            {
                ClientWorld world = NewWorld();
                ClientPlayer me = world.Me;

                long now = 1000;
                world.SetLocalPath(Path(me.X, me.Y, 3), now);

                // First step already taken. Half a tick later, still the same tile.
                world.AdvanceLocal(now + ClientWorld.TickMs / 2);
                Harness.Equal(3223, me.X, "still on the first tile");

                // A full tick later, the second step has happened.
                world.AdvanceLocal(now + ClientWorld.TickMs);
                Harness.Equal(3224, me.X, "second tile");
            });

            Harness.Test("the interpolation fraction is in phase with the steps", () =>
            {
                ClientWorld world = NewWorld();
                long now = 1000;

                world.SetLocalPath(Path(world.Me.X, world.Me.Y, 3), now);

                Harness.Near(0.0, world.AdvanceLocal(now), 0.02, "start of the tick");
                Harness.Near(0.5, world.AdvanceLocal(now + ClientWorld.TickMs / 2), 0.02, "half way");
                Harness.Near(0.99, world.AdvanceLocal(now + ClientWorld.TickMs - 6), 0.02, "almost the end");

                // At the tick boundary the fraction wraps to zero and the next tile is taken.
                // It does not sit at 1: the position is continuous, the phase is not.
                Harness.Near(0.0, world.AdvanceLocal(now + ClientWorld.TickMs), 0.02, "next tick");
                Harness.Equal(3224, world.Me.X, "the second step happened");
            });

            Harness.Test("the camera position is smooth, not snapped to tiles", () =>
            {
                ClientWorld world = NewWorld();
                long now = 1000;

                world.SetLocalPath(Path(world.Me.X, world.Me.Y, 3), now);

                double atStart = world.MySmoothX;

                world.AdvanceLocal(now + ClientWorld.TickMs / 2);
                double midTick = world.MySmoothX;

                Harness.Near(3222.0, atStart, 0.01, "the camera starts on the old tile");
                Harness.Near(3222.5, midTick, 0.01, "half way between the tiles");

                // The whole point: the camera is between tiles for most of the tick, so the
                // world slides instead of lurching one tile at a time.
                Harness.True(Math.Abs(midTick - Math.Round(midTick)) > 0.1,
                    "the camera is between tiles, got " + midTick);
            });

            Harness.Test("the final step is interpolated, not snapped", () =>
            {
                // When the path runs out the last step still needs its tick of travel. Stopping
                // the moment the last tile is popped makes the player jump the final tile.
                ClientWorld world = NewWorld();
                long now = 1000;

                world.SetLocalPath(Path(world.Me.X, world.Me.Y, 2), now);

                world.AdvanceLocal(now + ClientWorld.TickMs);
                Harness.Equal(3224, world.Me.X, "the last step was taken");
                Harness.True(world.IsLocalWalking, "still gliding onto the final tile");

                world.AdvanceLocal(now + ClientWorld.TickMs + ClientWorld.TickMs / 2);
                Harness.Near(3223.5, world.MySmoothX, 0.01, "half way onto the final tile");

                world.AdvanceLocal(now + ClientWorld.TickMs * 2);
                Harness.False(world.IsLocalWalking, "stopped once the glide finished");
                Harness.Near(3224.0, world.MySmoothX, 0.01, "resting on the final tile");
            });

            Harness.Test("walking stops when the path runs out", () =>
            {
                ClientWorld world = NewWorld();
                ClientPlayer me = world.Me;
                long now = 1000;

                world.SetLocalPath(Path(me.X, me.Y, 2), now);

                Harness.True(world.IsLocalWalking, "walking after the first step");

                world.AdvanceLocal(now + ClientWorld.TickMs * 2);

                Harness.False(world.IsLocalWalking, "stopped");
                Harness.Equal(3224, me.X, "reached the destination");
            });

            Harness.Test("a server position one tile behind is not a correction", () =>
            {
                // The client is normally ahead of the server. Snapping back to the server's tile
                // every packet is exactly the stutter this prediction exists to remove.
                ClientWorld world = NewWorld();
                ClientPlayer me = world.Me;

                world.SetLocalPath(Path(me.X, me.Y, 3), 1000);
                Harness.Equal(3223, me.X, "one step taken");

                bool corrected = world.ReconcileLocal(3222, 3218, 0);

                Harness.False(corrected, "no correction");
                Harness.Equal(3223, me.X, "the local position is kept");
            });

            Harness.Test("a large disagreement is a correction", () =>
            {
                ClientWorld world = NewWorld();
                ClientPlayer me = world.Me;

                world.SetLocalPath(Path(me.X, me.Y, 3), 1000);

                bool corrected = world.ReconcileLocal(3250, 3250, 0);

                Harness.True(corrected, "corrected");
                Harness.Equal(3250, me.X, "snapped to the server");
                Harness.False(world.IsLocalWalking, "the predicted path was dropped");
            });

            Harness.Test("a teleport clears the predicted path", () =>
            {
                ClientWorld world = NewWorld();
                ClientPlayer me = world.Me;

                world.SetLocalPath(Path(me.X, me.Y, 5), 1000);
                Harness.True(world.IsLocalWalking, "walking");

                world.ForceLocalPosition(3000, 3000, 0);

                Harness.False(world.IsLocalWalking, "the path was dropped");
                Harness.Equal(3000, me.X, "moved to the teleport destination");
                Harness.Equal(3000, world.MyX, "world position follows");
            });

            Harness.Test("an empty path is not walking", () =>
            {
                ClientWorld world = NewWorld();

                world.SetLocalPath(new List<Position>(), 1000);

                Harness.False(world.IsLocalWalking, "not walking");
                Harness.Near(1.0, world.AdvanceLocal(1000), 0.001, "fully interpolated when idle");
            });

            Harness.Test("the camera never jumps and never goes backwards", () =>
            {
                // This is the stutter test, and it is the one that matters. Sampling the camera
                // once per frame over a whole walk must produce a monotonic sequence of small
                // steps: no frame where it stands still and no frame where it leaps a tile.
                // Endpoint-only tests pass happily while the motion is visibly jerky.
                ClientWorld world = NewWorld();
                long start = 1000;
                const int tiles = 5;

                world.SetLocalPath(Path(world.Me.X, world.Me.Y, tiles), start);

                const double frameMs = 16.0;
                const double totalMs = tiles * ClientWorld.TickMs + ClientWorld.TickMs;

                double previous = world.MySmoothX;
                double travelled = 0.0;
                double largestStep = 0.0;
                int backwards = 0;
                int stalls = 0;

                for (double t = start; t <= start + totalMs; t += frameMs)
                {
                    world.AdvanceLocal((long)Math.Round(t));

                    double now = world.MySmoothX;
                    double step = now - previous;

                    if (step < -0.0001) backwards++;
                    if (step < 0.0001 && travelled < tiles - 0.5) stalls++;

                    if (step > largestStep) largestStep = step;

                    travelled += step;
                    previous = now;
                }

                Harness.Equal(0, backwards, "frames that moved backwards");

                // A frame may never advance more than one frame's worth of a tile, plus a little
                // slack for rounding and for the tick that lands inside a frame.
                double allowed = frameMs / ClientWorld.TickMs * 1.5;

                Harness.True(largestStep <= allowed,
                    "the largest single-frame jump is " + largestStep.ToString("0.###") +
                    " tiles, allowed " + allowed.ToString("0.###"));

                Harness.Near(tiles, travelled, 0.02, "total distance travelled");

                // Stalls are only acceptable on the very first frames, before the first step.
                Harness.True(stalls <= 2, "frames where nothing moved: " + stalls);
            });

            Harness.Section("Server reconciliation");

            Harness.Test("the server's movement bits do not move the local player", () =>
            {
                // The client already stepped this tile; applying the server's copy as well would
                // move the player twice per tile, which is the ping-pong this design removes.
                ClientWorld world = NewWorld();
                ClientPlayer me = world.Me;

                world.SetLocalPath(Path(me.X, me.Y, 3), 1000);
                Harness.Equal(3223, me.X, "one step taken locally");

                world.NoteServerStep(1, 0);

                Harness.Equal(3223, me.X, "the client position is unchanged");
            });

            Harness.Test("drift is corrected once both sides have stopped", () =>
            {
                ClientWorld world = NewWorld();
                ClientPlayer me = world.Me;

                // The client walks three tiles; the server only manages one before it stops.
                world.SetLocalPath(Path(me.X, me.Y, 3), 1000);
                world.AdvanceLocal(1000 + ClientWorld.TickMs * 4);
                Harness.Equal(3225, me.X, "the client finished its path");

                world.NoteServerStep(1, 0);

                // Still walking? No: idle. The server has stopped too, so its position is final.
                bool corrected = world.NoteServerStep(0, 0);

                Harness.True(corrected, "corrected");
                Harness.Equal(3223, me.X, "snapped to where the server says");
            });

            Harness.Test("a correction is deferred while the client is still walking", () =>
            {
                ClientWorld world = NewWorld();
                ClientPlayer me = world.Me;

                world.SetLocalPath(Path(me.X, me.Y, 3), 1000);
                world.NoteServerStep(1, 0);

                // The client is mid-path; yanking it back now would be visible.
                Harness.False(world.NoteServerStep(0, 0), "not corrected mid-walk");
                Harness.Equal(3223, me.X, "still on the predicted tile");
            });

            Harness.Test("agreement is not a correction", () =>
            {
                ClientWorld world = NewWorld();
                ClientPlayer me = world.Me;

                world.SetLocalPath(Path(me.X, me.Y, 1), 1000);
                world.AdvanceLocal(1000 + ClientWorld.TickMs * 2);

                world.NoteServerStep(1, 0);

                Harness.False(world.NoteServerStep(0, 0), "no correction needed");
                Harness.Equal(3223, me.X, "position kept");
            });
        }

        /// <summary>A world with a local player standing at the spawn point.</summary>
        private static ClientWorld NewWorld()
        {
            var world = new ClientWorld();

            var me = new ClientPlayer
            {
                Index = 1,
                Name = "Probe",
                IsLocal = true,
                X = 3222,
                Y = 3218,
                LastX = 3222,
                LastY = 3218
            };

            world.Players[1] = me;
            world.MyIndex = 1;

            // Seeding through ForceLocalPosition is what the login packet does, and it also
            // sets up the shadow position the reconciliation tests compare against.
            world.ForceLocalPosition(3222, 3218, 0);

            return world;
        }

        /// <summary>A straight path of <paramref name="steps"/> tiles east.</summary>
        private static List<Position> Path(int startX, int startY, int steps)
        {
            var path = new List<Position>(steps);

            for (int i = 1; i <= steps; i++)
            {
                path.Add(new Position(startX + i, startY, 0));
            }

            return path;
        }
    }
}
