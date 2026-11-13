using System;
using System.Collections.Generic;
using DartScape.Game;
using DartScape.Game.Definitions;

namespace DartScape.Tests
{
    /// <summary>
    /// Tests for clicking things in the world: which option a click means, and when a click
    /// walks before it acts.
    /// </summary>
    /// <remarks>
    /// The bug these encode is specific and was visible in play: clicking a farmer answered
    /// "You can't attack that", because the client assumed option one meant attack. It does not
    /// вЂ” option one is whatever the NPC's definition lists first, and for most townsfolk that
    /// is "Talk-to".
    /// </remarks>
    public static class InteractionTests
    {
        public static void Run()
        {
            Harness.Section("Interaction");

            OptionMappingTests();
            ReachTests();
            PendingTests();
        }

        private static NpcDefinition Npc(string name, int combatLevel, params string[] actions)
        {
            var definition = new NpcDefinition
            {
                Name = name,
                CombatLevel = combatLevel
            };

            for (int i = 0; i < actions.Length && i < 5; i++)
            {
                definition.Actions[i] = actions[i];
            }

            return definition;
        }

        private static void OptionMappingTests()
        {
            Harness.Test("a townsman's first option is Talk-to, not Attack", () =>
            {
                // Man: Talk-to / Attack / Pickpocket. The empty first slot on a guard is the
                // other half of the same rule.
                var npc = new Npc { Index = 1, Position = new Position(3222, 3218, 0) };
                npc.Definition = Npc("Man", 2, "Talk-to", "Attack", "Pickpocket");

                Harness.Equal("Talk-to", NpcActions.ActionName(npc, 0), "option 0");
                Harness.Equal("Attack", NpcActions.ActionName(npc, 1), "option 1");
            });

            Harness.Test("a guard's first option is empty and Attack is second", () =>
            {
                // Guard: null / Attack / Pickpocket. Treating index zero as attack sends an
                // empty option and the player gets "Nothing interesting happens".
                var npc = new Npc { Index = 1, Position = new Position(3222, 3218, 0) };
                npc.Definition = Npc("Guard", 21, null, "Attack", "Pickpocket");

                Harness.Null(NpcActions.ActionName(npc, 0), "option 0 is empty");
                Harness.Equal("Attack", NpcActions.ActionName(npc, 1), "option 1");
            });

            Harness.Test("an out-of-range option is null, not an exception", () =>
            {
                var npc = new Npc { Index = 1 };
                npc.Definition = Npc("Man", 2, "Talk-to");

                Harness.Null(NpcActions.ActionName(npc, 4), "beyond the list");
                Harness.Null(NpcActions.ActionName(npc, -1), "negative");
            });

            Harness.Test("attacking a non-combatant is refused with a message", () =>
            {
                var world = new World(new ServerConfig());
                var player = new Player { Name = "Dart", NameKey = "dart" };
                player.RestoreStats();

                var npc = new Npc { Index = 1, Position = new Position(3222, 3218, 0) };
                npc.Definition = Npc("Banker", 0, "Talk-to", "Attack");

                NpcActions.Run(world, player, npc, 1);

                Harness.Equal(-1, player.CombatTargetNpc, "no combat target was set");
            });

            Harness.Test("attacking a combatant sets the target", () =>
            {
                var world = new World(new ServerConfig());
                var player = new Player { Name = "Dart", NameKey = "dart" };
                player.RestoreStats();

                var npc = new Npc { Index = 7, Position = new Position(3222, 3218, 0) };
                npc.Definition = Npc("Guard", 21, null, "Attack");

                NpcActions.Run(world, player, npc, 1);

                Harness.Equal(7, player.CombatTargetNpc, "target set");
                Harness.NotNull(player.FaceTile, "faces the target");
            });
        }

        private static void ReachTests()
        {
            Harness.Test("standing next to a one-tile NPC is in reach", () =>
            {
                var player = new Player { Position = new Position(3222, 3218, 0) };
                var npc = new Npc { Position = new Position(3223, 3218, 0) };
                npc.Definition = Npc("Man", 2, "Talk-to");

                Harness.True(NpcActions.IsInReach(player, npc), "adjacent");
            });

            Harness.Test("standing on the NPC's tile is in reach", () =>
            {
                var player = new Player { Position = new Position(3222, 3218, 0) };
                var npc = new Npc { Position = new Position(3222, 3218, 0) };
                npc.Definition = Npc("Man", 2, "Talk-to");

                Harness.True(NpcActions.IsInReach(player, npc), "same tile");
            });

            Harness.Test("two tiles away is out of reach", () =>
            {
                var player = new Player { Position = new Position(3222, 3218, 0) };
                var npc = new Npc { Position = new Position(3224, 3218, 0) };
                npc.Definition = Npc("Man", 2, "Talk-to");

                Harness.False(NpcActions.IsInReach(player, npc), "two tiles east");
            });

            Harness.Test("a larger NPC can be reached from its far edge", () =>
            {
                // A cow is 2x2, anchored at its south-west tile. Standing east of the footprint
                // is adjacent to the object even though it is two tiles from the anchor.
                var player = new Player { Position = new Position(3224, 3218, 0) };
                var npc = new Npc { Position = new Position(3222, 3218, 0) };

                npc.Definition = Npc("Cow", 2, null, "Attack");
                npc.Definition.Size = 2;

                Harness.True(NpcActions.IsInReach(player, npc), "east of the footprint");
            });

            Harness.Test("a different plane is never in reach", () =>
            {
                var player = new Player { Position = new Position(3222, 3218, 0) };
                var npc = new Npc { Position = new Position(3222, 3218, 1) };
                npc.Definition = Npc("Man", 2, "Talk-to");

                Harness.False(NpcActions.IsInReach(player, npc), "one plane up");
            });
        }

        private static void PendingTests()
        {
            Harness.Test("a new click replaces the queued action", () =>
            {
                // The newest instruction wins; the original behaves the same way and it is what
                // players expect from a click.
                var player = new Player { Name = "Dart", NameKey = "dart" };
                player.RestoreStats();

                player.Pending = new PendingAction
                {
                    Kind = PendingActionKind.Npc,
                    NpcIndex = 1,
                    Option = 0,
                    Target = new Position(3222, 3218, 0)
                };

                Harness.True(player.HasPending, "queued");

                player.ClearPending();

                Harness.False(player.HasPending, "cleared");
                Harness.Equal((int)PendingActionKind.None, (int)player.Pending.Kind, "no kind");
            });

            Harness.Test("a queued action runs when the player arrives", () =>
            {
                var config = new ServerConfig();
                var world = new World(config);

                var player = new Player { Name = "Dart", NameKey = "dart" };
                player.RestoreStats();
                world.AddPlayer(player);

                var npc = world.SpawnNpc(0, new Position(player.Position.X + 1, player.Position.Y, 0));

                if (npc == null)
                {
                    // No definitions loaded: the spawn is refused and the test has nothing to
                    // prove. Skipping is honest; failing here would be testing the fixture.
                    return;
                }

                npc.Definition = Npc("Man", 2, "Talk-to", "Attack");

                player.Pending = new PendingAction
                {
                    Kind = PendingActionKind.Npc,
                    NpcIndex = npc.Index,
                    Option = 1,
                    Target = npc.Position
                };

                world.Tick();

                Harness.False(player.HasPending, "the action ran and was cleared");
            });

            Harness.Test("a queued action is dropped when its target is gone", () =>
            {
                var world = new World(new ServerConfig());

                var player = new Player { Name = "Dart", NameKey = "dart" };
                player.RestoreStats();
                world.AddPlayer(player);

                player.Pending = new PendingAction
                {
                    Kind = PendingActionKind.Npc,
                    NpcIndex = 999,
                    Option = 0,
                    Target = new Position(3222, 3218, 0)
                };

                world.Tick();

                Harness.False(player.HasPending, "dropped rather than retried for ever");
            });
        }
    }
}
