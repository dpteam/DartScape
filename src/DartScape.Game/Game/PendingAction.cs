using System;

namespace DartScape.Game
{
    /// <summary>What a queued action is aimed at.</summary>
    public enum PendingActionKind
    {
        /// <summary>Nothing queued.</summary>
        None = 0,

        /// <summary>An NPC menu option, to run once the player is adjacent.</summary>
        Npc,

        /// <summary>A ground item pickup, to run once the player is on the tile.</summary>
        GroundItem
    }

    /// <summary>
    /// An action that waits for the player to walk into range.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Clicking something out of reach should walk there and then act, not do nothing. The
    /// original keeps this in four loose fields on the client; on the server it belongs with
    /// the entity, because the server is the side that knows when the player has actually
    /// arrived.
    /// </para>
    /// <para>
    /// Only one action can be pending. A second click replaces it, which is the behaviour
    /// players expect: the newest instruction wins.
    /// </para>
    /// </remarks>
    public struct PendingAction
    {
        /// <summary>What is queued.</summary>
        public PendingActionKind Kind;

        /// <summary>NPC index, for <see cref="PendingActionKind.Npc"/>.</summary>
        public int NpcIndex;

        /// <summary>Option index within the NPC's action list.</summary>
        public int Option;

        /// <summary>Ground item id, for <see cref="PendingActionKind.GroundItem"/>.</summary>
        public int ItemId;

        /// <summary>Tile the action is aimed at.</summary>
        public Position Target;

        /// <summary>True when this is a real queued action.</summary>
        public bool IsSet
        {
            get { return Kind != PendingActionKind.None; }
        }
    }
}
