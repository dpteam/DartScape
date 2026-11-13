using System;

namespace DartScape.Game
{
    /// <summary>
    /// Runs an NPC's menu option.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dispatch is by the action's <em>name</em>, not by its index. The index is a position in
    /// the definition's own list and means different things per NPC: a man's first option is
    /// "Talk-to", a guard's first slot is empty and its "Attack" sits at index one. Treating
    /// index zero as "attack" is what made clicking a farmer answer "You can't attack that".
    /// </para>
    /// <para>
    /// This lives in the game layer rather than with the packet handlers because the world tick
    /// also needs it: a queued action runs on arrival, and the arrival is detected by the tick,
    /// not by a packet.
    /// </para>
    /// </remarks>
    public static class NpcActions
    {
        /// <summary>True when the player is close enough to act on an NPC.</summary>
        /// <remarks>
        /// One tile of separation, widened by the NPC's footprint. This is the same rule the
        /// client's pathfinder uses to stop next to its target, so the two agree about when the
        /// walk is finished.
        /// </remarks>
        public static bool IsInReach(Player player, Npc npc)
        {
            if (player == null || npc == null) return false;
            if (player.Position.Plane != npc.Position.Plane) return false;

            int size = npc.Definition != null ? npc.Definition.Size : 1;
            if (size < 1) size = 1;

            int dx = player.Position.X - npc.Position.X;
            int dy = player.Position.Y - npc.Position.Y;

            return dx >= -1 && dx <= size && dy >= -1 && dy <= size;
        }

        /// <summary>The action name at an option index, or <c>null</c>.</summary>
        public static string ActionName(Npc npc, int option)
        {
            if (npc == null || npc.Definition == null || npc.Definition.Actions == null) return null;
            if (option < 0 || option >= npc.Definition.Actions.Length) return null;

            return npc.Definition.Actions[option];
        }

        /// <summary>
        /// Runs an NPC menu option now that the player is close enough.
        /// </summary>
        public static void Run(World world, Player player, Npc npc, int option)
        {
            if (world == null || player == null || npc == null) return;

            player.FaceTile = npc.Position;

            string action = ActionName(npc, option);

            if (string.IsNullOrEmpty(action))
            {
                world.SendMessage(player, "Nothing interesting happens.");
                return;
            }

            if (Matches(action, "attack"))
            {
                if (npc.Definition != null && npc.Definition.CombatLevel > 0)
                {
                    player.CombatTargetNpc = npc.Index;
                    player.CombatTargetPlayer = -1;
                }
                else
                {
                    world.SendMessage(player, "You can't attack that.");
                }

                return;
            }

            if (Matches(action, "talk-to") || Matches(action, "talk to"))
            {
                world.SendMessage(player, npc.Name + ": " + Describe(npc));
                return;
            }

            if (Matches(action, "pickpocket"))
            {
                world.SendMessage(player, "You can't pick that pocket yet.");
                return;
            }

            if (Matches(action, "trade") || Matches(action, "bank"))
            {
                world.SendMessage(player, npc.Name + " has nothing to trade.");
                return;
            }

            // Examine and anything else the cache defines but the game does not implement yet.
            world.SendMessage(player, npc.Name + ": " + Describe(npc));
        }

        /// <summary>A one-line description, used for talk and examine.</summary>
        public static string Describe(Npc npc)
        {
            if (npc == null) return "nothing";

            if (npc.Definition != null && npc.Definition.CombatLevel > 0)
            {
                return "It looks like it could put up a fight (level " +
                       npc.Definition.CombatLevel + ").";
            }

            return "It has nothing to say.";
        }

        private static bool Matches(string action, string wanted)
        {
            return string.Equals(action, wanted, StringComparison.OrdinalIgnoreCase);
        }
    }
}
