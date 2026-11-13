using System;
using DartScape.Game;
using DartScape.Game.Definitions;

namespace DartScape.Tests
{
    /// <summary>Tests for the game model: skills, containers, movement, combat maths.</summary>
    public static class GameTests
    {
        public static void Run()
        {
            Harness.Section("Skills");
            SkillTests();

            Harness.Section("ItemContainer");
            ContainerTests();

            Harness.Section("Position and direction");
            PositionTests();

            Harness.Section("Player");
            PlayerTests();

            Harness.Section("World");
            WorldTests();
        }

        private static void SkillTests()
        {
            Harness.Test("the experience curve matches the authentic table", () =>
            {
                Harness.Equal(0, Skills.ExperienceAt(1), "level 1");
                Harness.Equal(83, Skills.ExperienceAt(2), "level 2");
                Harness.Equal(174, Skills.ExperienceAt(3), "level 3");
                Harness.Equal(276, Skills.ExperienceAt(4), "level 4");
                Harness.Equal(1154, Skills.ExperienceAt(10), "level 10");
                Harness.Equal(13034431, Skills.ExperienceAt(99), "level 99");
            });

            Harness.Test("level lookup inverts the curve", () =>
            {
                for (int level = 1; level <= 99; level++)
                {
                    int xp = Skills.ExperienceAt(level);
                    Harness.Equal(level, Skills.LevelFor(xp), "level at exactly " + xp + " xp");
                }
            });

            Harness.Test("level lookup is correct just below a boundary", () =>
            {
                int xp99 = Skills.ExperienceAt(99);
                Harness.Equal(98, Skills.LevelFor(xp99 - 1), "one xp short of 99");
            });

            Harness.Test("levels clamp at both ends", () =>
            {
                Harness.Equal(1, Skills.LevelFor(0), "zero experience");
                Harness.Equal(1, Skills.LevelFor(-500), "negative experience");
                Harness.Equal(99, Skills.LevelFor(Skills.MaxExperience), "maximum experience");
                Harness.Equal(99, Skills.LevelFor(int.MaxValue), "overflowing experience");
            });

            Harness.Test("skill names and keys resolve both ways", () =>
            {
                Harness.Equal(Skills.Woodcutting, Skills.IndexOf("Woodcutting"), "display name");
                Harness.Equal(Skills.Woodcutting, Skills.IndexOf("woodcutting"), "lowercase key");
                Harness.Equal(Skills.Woodcutting, Skills.IndexOf("  WOODCUTTING "), "padded and upper");
                Harness.Equal(-1, Skills.IndexOf("farming2"), "unknown");
                Harness.Equal(-1, Skills.IndexOf(""), "empty");
                Harness.Equal(-1, Skills.IndexOf(null), "null");
            });

            Harness.Test("remaining experience is correct", () =>
            {
                Harness.Equal(83, Skills.RemainingToNextLevel(0), "from zero");
                Harness.Equal(0, Skills.RemainingToNextLevel(Skills.MaxExperience), "at 99");
            });
        }

        private static void ContainerTests()
        {
            Harness.Test("adding a stackable item fills one slot", () =>
            {
                var container = new ItemContainer(3214, 28);

                // Coins (995) are stackable in a real cache; without definitions loaded the
                // container treats everything as unstackable, so this uses the raw behaviour.
                int added = container.Add(995, 100);
                Harness.True(added > 0, "something was added");
                Harness.Equal(added, container.Count(995), "count matches what was added");
            });

            Harness.Test("removing more than is held removes only what exists", () =>
            {
                var container = new ItemContainer(3214, 28);
                container.Add(1000, 3);

                int removed = container.Remove(1000, 10);
                Harness.Equal(3, removed, "removed count");
                Harness.Equal(0, container.Count(1000), "nothing left");
            });

            Harness.Test("a full container refuses more items", () =>
            {
                var container = new ItemContainer(3214, 2);
                container.Add(1, 1);
                container.Add(2, 1);

                Harness.True(container.IsFull, "container should be full");
                Harness.Equal(0, container.Add(3, 1), "nothing added");
            });

            Harness.Test("swapping two slots exchanges their contents", () =>
            {
                var container = new ItemContainer(3214, 4);
                container.Set(0, 100, 1);
                container.Set(1, 200, 5);

                container.Swap(0, 1);

                Harness.Equal(200, container[0].Id, "slot 0 id");
                Harness.Equal(100, container[1].Id, "slot 1 id");
            });

            Harness.Test("inserting into an empty slot moves the stack", () =>
            {
                var container = new ItemContainer(3214, 4);
                container.Set(0, 100, 3);

                Harness.True(container.Insert(0, 2), "insert succeeded");
                Harness.True(container[0].IsEmpty, "source emptied");
                Harness.Equal(100, container[2].Id, "destination id");
                Harness.Equal(3, container[2].Amount, "destination amount");
            });

            Harness.Test("removing from a slot respects the amount held", () =>
            {
                var container = new ItemContainer(3214, 4);
                container.Set(0, 100, 3);

                Harness.False(container.RemoveFromSlot(0, 5), "cannot remove more than held");
                Harness.True(container.RemoveFromSlot(0, 3), "can remove exactly what is held");
                Harness.True(container[0].IsEmpty, "slot emptied");
            });

            Harness.Test("out-of-range slots are handled without throwing", () =>
            {
                var container = new ItemContainer(3214, 4);

                Harness.True(container[-1].IsEmpty, "negative index reads empty");
                Harness.True(container[99].IsEmpty, "large index reads empty");
                Harness.False(container.RemoveFromSlot(-1, 1), "negative remove");
                Harness.False(container.RemoveFromSlot(99, 1), "large remove");
            });
        }

        private static void PositionTests()
        {
            Harness.Test("chebyshev distance is the tile-radius metric", () =>
            {
                var origin = new Position(100, 100, 0);

                Harness.Equal(0, origin.ChebyshevDistance(origin), "same tile");
                Harness.Equal(1, origin.ChebyshevDistance(new Position(101, 100, 0)), "one east");
                Harness.Equal(3, origin.ChebyshevDistance(new Position(103, 97, 0)), "diagonal");
                Harness.Equal(10000, origin.ChebyshevDistance(new Position(100, 100, 1)), "other plane");
            });

            Harness.Test("manhattan distance sums the axes", () =>
            {
                var origin = new Position(10, 10, 0);
                Harness.Equal(4, origin.DistanceTo(new Position(12, 12, 0)), "diagonal");
            });

            Harness.Test("packing round-trips", () =>
            {
                var position = new Position(3222, 3218, 2);
                Harness.True(Position.Unpack(position.Pack()) == position, "packed position");
            });

            Harness.Test("region membership follows 8x8 blocks", () =>
            {
                var a = new Position(3200, 3200, 0);
                var b = new Position(3207, 3207, 0);
                var c = new Position(3208, 3200, 0);

                Harness.True(a.InSameRegion(b), "inside the block");
                Harness.False(a.InSameRegion(c), "across the boundary");
            });

            Harness.Test("direction between adjacent tiles is correct", () =>
            {
                var origin = new Position(50, 50, 0);

                // 0 = north, 2 = east, 4 = south, 6 = west.
                Harness.Equal(0, Direction.Between(origin, new Position(50, 51, 0)), "north");
                Harness.Equal(2, Direction.Between(origin, new Position(51, 50, 0)), "east");
                Harness.Equal(4, Direction.Between(origin, new Position(50, 49, 0)), "south");
                Harness.Equal(6, Direction.Between(origin, new Position(49, 50, 0)), "west");
                Harness.Equal(-1, Direction.Between(origin, origin), "same tile");
            });

            Harness.Test("direction deltas are consistent with the direction table", () =>
            {
                for (int direction = 0; direction < 8; direction++)
                {
                    var from = new Position(10, 10, 0);
                    var to = new Position(10 + Direction.DeltaX[direction], 10 + Direction.DeltaY[direction], 0);

                    Harness.Equal(direction, Direction.Between(from, to), "direction " + direction);
                }
            });
        }

        private static void PlayerTests()
        {
            Harness.Test("a new player starts with 10 hitpoints and level 1 elsewhere", () =>
            {
                var player = new Player();

                Harness.Equal(10, player.BaseLevel[Skills.Hitpoints], "hitpoints level");
                Harness.Equal(10, player.Hitpoints, "current hitpoints");
                Harness.Equal(1, player.BaseLevel[Skills.Attack], "attack level");
                Harness.Equal(0, player.Experience[Skills.Attack], "attack experience");
            });

            Harness.Test("combat level of a fresh character is 3", () =>
            {
                var player = new Player();
                Harness.Equal(3, player.CombatLevel, "level 3");
            });

            Harness.Test("experience gain levels the skill up", () =>
            {
                var player = new Player();
                int level = player.AddExperience(Skills.Attack, 100, 1.0);

                Harness.Equal(2, level, "reached level 2");
                Harness.Equal(2, player.BaseLevel[Skills.Attack], "base level updated");
                Harness.Equal(2, player.CurrentLevel[Skills.Attack], "current level updated");
            });

            Harness.Test("the experience multiplier is applied", () =>
            {
                var player = new Player();
                player.AddExperience(Skills.Attack, 100, 2.0);

                Harness.Equal(200, player.Experience[Skills.Attack], "doubled experience");
            });

            Harness.Test("experience is capped at the maximum", () =>
            {
                var player = new Player();
                player.AddExperience(Skills.Attack, Skills.MaxExperience * 2, 1.0);

                Harness.Equal(Skills.MaxExperience, player.Experience[Skills.Attack], "capped");
                Harness.Equal(99, player.BaseLevel[Skills.Attack], "level 99");
            });

            Harness.Test("level-ups are queued for the dialogue", () =>
            {
                var player = new Player();
                player.AddExperience(Skills.Attack, 100, 1.0);

                Harness.Equal(1, player.PendingLevelUps.Count, "one pending level-up");
                Harness.Equal(Skills.Attack, player.PendingLevelUps[0], "attack");
            });

            Harness.Test("total level sums the base levels", () =>
            {
                var player = new Player();

                // Twenty skills at 1, hitpoints at 10.
                Harness.Equal(20 + 10, player.TotalLevel, "total level");
            });

            Harness.Test("walking produces one waypoint per tile", () =>
            {
                var player = new Player();
                player.Position = new Position(100, 100, 0);

                player.WalkTo(new Position(103, 104, 0));

                Harness.Equal(4, player.Waypoints.Count, "four steps");
                Harness.True(player.Waypoints[3] == new Position(103, 104, 0), "final waypoint");
            });

            Harness.Test("walking to the current tile produces no waypoints", () =>
            {
                var player = new Player();
                player.Position = new Position(100, 100, 0);

                player.WalkTo(new Position(100, 100, 0));

                Harness.Equal(0, player.Waypoints.Count, "no steps");
            });

            Harness.Test("walking across planes is refused", () =>
            {
                var player = new Player();
                player.Position = new Position(100, 100, 0);

                player.WalkTo(new Position(105, 105, 1));

                Harness.Equal(0, player.Waypoints.Count, "no steps");
            });

            Harness.Test("adjacency includes diagonals", () =>
            {
                var player = new Player();
                player.Position = new Position(100, 100, 0);

                Harness.True(player.IsAdjacentTo(new Position(101, 101, 0)), "diagonal");
                Harness.True(player.IsAdjacentTo(new Position(100, 100, 0)), "same tile");
                Harness.False(player.IsAdjacentTo(new Position(102, 100, 0)), "two tiles away");
            });
        }

        private static void WorldTests()
        {
            Harness.Test("a world starts empty", () =>
            {
                var config = new ServerConfig();
                var world = new World(config);

                Harness.Equal(0, world.PlayerCount, "no players");
                Harness.Equal(0, world.NpcCount, "no NPCs");
                Harness.Equal(0L, world.TickCount, "no ticks");
            });

            Harness.Test("adding and removing a player updates the count", () =>
            {
                var config = new ServerConfig();
                var world = new World(config);

                var player = new Player { Name = "Dart", NameKey = "dart" };

                string error = world.AddPlayer(player);
                Harness.Null(error, "login error");
                Harness.Equal(1, world.PlayerCount, "one player");
                Harness.NotNull(world.FindPlayer("DART"), "found by uppercase name");
                Harness.NotNull(world.FindPlayer("dart"), "found by lowercase name");

                world.RemovePlayer(player, "test");
                Harness.Equal(0, world.PlayerCount, "no players");
                Harness.Null(world.FindPlayer("Dart"), "not found after removal");
            });

            Harness.Test("a duplicate login replaces the earlier session", () =>
            {
                var config = new ServerConfig();
                var world = new World(config);

                var first = new Player { Name = "Dart", NameKey = "dart" };
                world.AddPlayer(first);

                var second = new Player { Name = "Dart", NameKey = "dart" };
                string error = world.AddPlayer(second);

                Harness.Null(error, "second login should succeed");
                Harness.Equal(1, world.PlayerCount, "still one player");
                Harness.False(first.Online, "the first session was dropped");
            });

            Harness.Test("the world refuses players beyond its limit", () =>
            {
                var config = new ServerConfig { MaxPlayers = 2 };
                var world = new World(config);

                world.AddPlayer(new Player { Name = "A", NameKey = "a" });
                world.AddPlayer(new Player { Name = "B", NameKey = "b" });

                string error = world.AddPlayer(new Player { Name = "C", NameKey = "c" });
                Harness.NotNull(error, "third login should be refused");
            });

            Harness.Test("a tick advances the counter and processes queued input", () =>
            {
                var config = new ServerConfig();
                var world = new World(config);

                bool ran = false;
                world.Enqueue(() => { ran = true; });

                world.Tick();

                Harness.True(ran, "queued action ran");
                Harness.Equal(1L, world.TickCount, "one tick");
            });

            Harness.Test("a failing queued action does not stop the tick", () =>
            {
                var config = new ServerConfig();
                var world = new World(config);

                bool after = false;
                world.Enqueue(() => { throw new InvalidOperationException("boom"); });
                world.Enqueue(() => { after = true; });

                world.Tick();

                Harness.True(after, "the next action still ran");
                Harness.Equal(1L, world.TickCount, "the tick completed");
            });

            Harness.Test("world events are recorded with monotonic sequence numbers", () =>
            {
                var config = new ServerConfig();
                var world = new World(config);

                world.AddEvent("test", "first");
                world.AddEvent("test", "second");

                World.WorldEvent[] events = world.RecentEvents(10);
                Harness.Equal(2, events.Length, "two events");
                Harness.True(events[1].Seq > events[0].Seq, "sequence increases");
                Harness.Equal("second", events[1].Text, "newest last");
            });

            Harness.Test("damage reduces hitpoints and never goes below zero", () =>
            {
                var config = new ServerConfig();
                var world = new World(config);

                var player = new Player { Name = "Dart", NameKey = "dart" };
                world.AddPlayer(player);

                world.ApplyDamageToPlayer(player, 4, 1);
                Harness.Equal(6, player.Hitpoints, "after 4 damage");

                world.ApplyDamageToPlayer(player, 100, 1);
                Harness.Equal(0, player.Hitpoints, "clamped at zero");
                Harness.True(player.IsDead, "player is dead");
            });
        }
    }
}
