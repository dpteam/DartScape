using System;
using System.Collections.Generic;
using DartScape.Game;

namespace DartScape.Tests
{
    /// <summary>
    /// Tests for collision flags and the BFS pathfinder.
    /// </summary>
    /// <remarks>
    /// These encode the original's rules rather than whatever the implementation happens to do:
    /// an orthogonal step needs the destination free for that direction, a diagonal step
    /// additionally needs both orthogonal neighbours free, and an unreachable target falls back
    /// to the closest reachable tile. The diagonal rule is the one that stops characters cutting
    /// through wall corners, and it is the most visible difference between a real pathfinder and
    /// a straight line.
    /// </remarks>
    public static class CollisionTests
    {
        private const int BaseX = 3200;
        private const int BaseY = 3200;

        public static void Run()
        {
            Harness.Section("Collision map");
            CollisionMapTests();

            Harness.Section("Pathfinding");
            PathFinderTests();
        }

        /// <summary>
        /// A map with a floor everywhere, i.e. open ground. A freshly reset map is all void, so
        /// the tests have to clear that bit before anything can move.
        /// </summary>
        private static CollisionMap EmptyMap()
        {
            var map = new CollisionMap(BaseX, BaseY);

            for (int y = BaseY; y < BaseY + CollisionMap.Size; y++)
            {
                for (int x = BaseX; x < BaseX + CollisionMap.Size; x++)
                {
                    map.MarkFloor(x, y);
                }
            }

            return map;
        }

        private static void CollisionMapTests()
        {
            Harness.Test("an empty map allows every step", () =>
            {
                CollisionMap map = EmptyMap();

                Harness.True(map.CanStep(3210, 3210, 3211, 3210), "east");
                Harness.True(map.CanStep(3210, 3210, 3209, 3210), "west");
                Harness.True(map.CanStep(3210, 3210, 3210, 3211), "north");
                Harness.True(map.CanStep(3210, 3210, 3210, 3209), "south");
                Harness.True(map.CanStep(3210, 3210, 3211, 3211), "diagonal");
            });

            Harness.Test("a fresh map is void, so nothing moves until a floor is marked", () =>
            {
                // A reset collision map has no floor, and nothing is walkable until the loader
                // clears the void bit per tile. A map that only marks obstacles produces a world
                // where nobody can move at all.
                var map = new CollisionMap(BaseX, BaseY);

                Harness.False(map.CanStep(3210, 3210, 3211, 3210), "void blocks movement");

                map.MarkFloor(3210, 3210);
                map.MarkFloor(3211, 3210);

                Harness.True(map.CanStep(3210, 3210, 3211, 3210), "walkable once both tiles have a floor");
            });

            Harness.Test("a solid tile cannot be entered from any side", () =>
            {
                CollisionMap map = EmptyMap();
                map.MarkSolid(3211, 3210);

                Harness.False(map.CanStep(3210, 3210, 3211, 3210), "from the west");
                Harness.False(map.CanStep(3212, 3210, 3211, 3210), "from the east");
                Harness.False(map.CanStep(3211, 3211, 3211, 3210), "from the north");
                Harness.False(map.CanStep(3211, 3209, 3211, 3210), "from the south");
            });

            Harness.Test("a straight wall blocks one direction but not the other", () =>
            {
                CollisionMap map = EmptyMap();

                // Rotation 0 puts the wall on the west edge of the tile: movement east into the
                // tile is blocked, and movement west out of it is blocked from the other side.
                map.MarkWall(3210, 3210, 0, 0, false);

                Harness.False(map.CanStep(3209, 3210, 3210, 3210), "cannot move east through it");
                Harness.False(map.CanStep(3210, 3210, 3209, 3210), "cannot move west back through it");
                Harness.True(map.CanStep(3210, 3210, 3210, 3211), "can still walk north");
            });

            Harness.Test("a diagonal step needs both orthogonal neighbours free", () =>
            {
                CollisionMap map = EmptyMap();

                // Block the tile directly east of the start. A diagonal north-east step would cut
                // the corner of that block, which must be refused.
                map.MarkSolid(3211, 3210);

                Harness.False(map.CanStep(3210, 3210, 3211, 3211),
                    "diagonal past a blocked orthogonal neighbour");
            });

            Harness.Test("a diagonal step into a blocked tile is refused", () =>
            {
                CollisionMap map = EmptyMap();
                map.MarkSolid(3211, 3211);

                Harness.False(map.CanStep(3210, 3210, 3211, 3211), "into a solid tile");
            });

            Harness.Test("a rotated object swaps its footprint", () =>
            {
                CollisionMap map = EmptyMap();

                // Two tiles wide, one tall, rotated: it must occupy one wide and two tall.
                map.MarkObject(3210, 3210, 2, 1, 1, false);

                Harness.True((map.FlagsAt(3210, 3210) & CollisionMap.Solid) != 0, "first tile");
                Harness.True((map.FlagsAt(3210, 3211) & CollisionMap.Solid) != 0, "second tile north");
                Harness.True((map.FlagsAt(3211, 3210) & CollisionMap.Solid) == 0, "east tile is free");
            });

            Harness.Test("reading outside the map is treated as solid", () =>
            {
                CollisionMap map = EmptyMap();

                Harness.True((map.FlagsAt(BaseX - 1, BaseY) & CollisionMap.Solid) != 0, "west of the map");
                Harness.True((map.FlagsAt(BaseX, BaseY + 9999) & CollisionMap.Solid) != 0, "north of the map");
            });

            Harness.Test("non-adjacent steps are refused", () =>
            {
                CollisionMap map = EmptyMap();

                Harness.False(map.CanStep(3210, 3210, 3212, 3210), "two tiles east");
                Harness.False(map.CanStep(3210, 3210, 3210, 3210), "same tile");
            });

            Harness.Test("standing next to a footprint counts as reaching it", () =>
            {
                CollisionMap map = EmptyMap();

                // A 2x2 object at (3210, 3210).
                Harness.True(map.ReachedFacingObject(3210, 3210, 3210, 3210, 2, 2, 0), "inside");
                Harness.True(map.ReachedFacingObject(3211, 3211, 3210, 3210, 2, 2, 0), "inside far corner");
                Harness.True(map.ReachedFacingObject(3209, 3210, 3210, 3210, 2, 2, 0), "west of it");
                Harness.True(map.ReachedFacingObject(3212, 3211, 3210, 3210, 2, 2, 0), "east of it");
                Harness.True(map.ReachedFacingObject(3210, 3209, 3210, 3210, 2, 2, 0), "south of it");
                Harness.True(map.ReachedFacingObject(3211, 3212, 3210, 3210, 2, 2, 0), "north of it");
                Harness.False(map.ReachedFacingObject(3208, 3210, 3210, 3210, 2, 2, 0), "two tiles west");
            });
        }

        private static void PathFinderTests()
        {
            Harness.Test("a clear straight path is produced", () =>
            {
                var finder = new PathFinder(EmptyMap());

                List<Position> path = finder.Find(new Position(3210, 3210, 0), new Position(3215, 3210, 0));

                Harness.Equal(5, path.Count, "five steps");
                Harness.True(path[path.Count - 1] == new Position(3215, 3210, 0), "ends at the target");
            });

            Harness.Test("the path does not include the starting tile", () =>
            {
                var finder = new PathFinder(EmptyMap());

                List<Position> path = finder.Find(new Position(3210, 3210, 0), new Position(3211, 3210, 0));

                Harness.Equal(1, path.Count, "one step");
                Harness.True(path[0] == new Position(3211, 3210, 0), "the step, not the origin");
            });

            Harness.Test("every consecutive pair of waypoints is adjacent", () =>
            {
                // The single most useful invariant: a path with a gap in it teleports the
                // player, and the gap is invisible unless it is asserted.
                var finder = new PathFinder(EmptyMap());

                List<Position> path = finder.Find(new Position(3210, 3210, 0), new Position(3220, 3218, 0));

                Position previous = new Position(3210, 3210, 0);
                foreach (Position step in path)
                {
                    int dx = Math.Abs(step.X - previous.X);
                    int dy = Math.Abs(step.Y - previous.Y);

                    Harness.True(dx <= 1 && dy <= 1 && (dx + dy) > 0,
                        "step from " + previous + " to " + step + " is a single tile");

                    previous = step;
                }
            });

            Harness.Test("a wall is walked around, not through", () =>
            {
                var map = EmptyMap();

                // A vertical wall from (3213, 3208) to (3213, 3214), with the ends open.
                for (int y = 3208; y <= 3214; y++) map.MarkSolid(3213, y);

                var finder = new PathFinder(map);
                List<Position> path = finder.Find(new Position(3210, 3210, 0), new Position(3216, 3210, 0));

                Harness.True(path.Count > 0, "a path exists");

                Position previous = new Position(3210, 3210, 0);
                foreach (Position step in path)
                {
                    Harness.True(map.CanStep(previous.X, previous.Y, step.X, step.Y),
                        "step from " + previous + " to " + step + " is legal");
                    Harness.True((map.FlagsAt(step.X, step.Y) & CollisionMap.Solid) == 0,
                        "step " + step + " is not inside the wall");
                    previous = step;
                }

                Harness.True(previous == new Position(3216, 3210, 0), "reaches the target");
            });

            Harness.Test("an unreachable target falls back to the closest reachable tile", () =>
            {
                var map = EmptyMap();

                // Box the target in completely.
                map.MarkSolid(3219, 3220);
                map.MarkSolid(3221, 3220);
                map.MarkSolid(3220, 3219);
                map.MarkSolid(3220, 3221);

                var finder = new PathFinder(map);
                List<Position> path = finder.Find(new Position(3210, 3220, 0), new Position(3220, 3220, 0));

                Harness.True(path.Count > 0, "walks as far as it can");
                Harness.True(finder.LastTargetUnreachable, "reports that it stopped short");

                Position last = path[path.Count - 1];
                Harness.True(last != new Position(3220, 3220, 0), "does not reach the boxed tile");

                int distance = last.ChebyshevDistance(new Position(3220, 3220, 0));
                Harness.True(distance <= 2, "stops near the target, got distance " + distance);
            });

            Harness.Test("the path is capped at the original's maximum", () =>
            {
                var finder = new PathFinder(EmptyMap());

                List<Position> path = finder.Find(new Position(3210, 3210, 0), new Position(3210, 3290, 0));

                Harness.True(path.Count <= PathFinder.MaxPathLength,
                    "capped at " + PathFinder.MaxPathLength + ", got " + path.Count);
            });

            Harness.Test("walking to the current tile produces nothing", () =>
            {
                var finder = new PathFinder(EmptyMap());

                List<Position> path = finder.Find(new Position(3210, 3210, 0), new Position(3210, 3210, 0));

                Harness.Equal(0, path.Count, "no steps");
            });

            Harness.Test("a different plane is refused", () =>
            {
                var finder = new PathFinder(EmptyMap());

                List<Position> path = finder.Find(new Position(3210, 3210, 0), new Position(3211, 3210, 1));

                Harness.Equal(0, path.Count, "no steps across planes");
            });

            Harness.Test("a fully enclosed player cannot move", () =>
            {
                var map = EmptyMap();

                map.MarkSolid(3211, 3210);
                map.MarkSolid(3209, 3210);
                map.MarkSolid(3210, 3211);
                map.MarkSolid(3210, 3209);

                var finder = new PathFinder(map);
                List<Position> path = finder.Find(new Position(3210, 3210, 0), new Position(3215, 3215, 0));

                Harness.Equal(0, path.Count, "nowhere to go");
            });

            Harness.Test("a diagonal path is taken when the ground is open", () =>
            {
                var finder = new PathFinder(EmptyMap());

                List<Position> path = finder.Find(new Position(3210, 3210, 0), new Position(3215, 3215, 0));

                // A BFS on an open field moves diagonally, so this is five steps, not ten.
                Harness.Equal(5, path.Count, "diagonal steps, got " + path.Count);
            });

            Harness.Test("an adjacent search stops next to the target, not on it", () =>
            {
                var finder = new PathFinder(EmptyMap());

                // A 1x1 "NPC" at (3215, 3210): the path should end on one of its eight
                // neighbours, never on the tile itself.
                List<Position> path = finder.FindAdjacent(
                    new Position(3210, 3210, 0), new Position(3215, 3210, 0), 1, 1);

                Harness.True(path.Count > 0, "a path exists");

                Position last = path[path.Count - 1];
                Harness.True(last != new Position(3215, 3210, 0), "does not stand on the target");
                Harness.True(last.ChebyshevDistance(new Position(3215, 3210, 0)) == 1,
                    "ends adjacent to it, got " + last);
            });
        }
    }
}
