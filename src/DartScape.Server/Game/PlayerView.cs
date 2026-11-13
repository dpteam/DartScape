using System;
using System.Collections.Generic;

namespace DartScape.Server.Game
{
    /// <summary>
    /// Per-player record of what that player's client currently knows about.
    /// </summary>
    /// <remarks>
    /// The 317 update protocol is stateful: the server sends *deltas* against the list
    /// of entities the client already has. Getting this wrong produces the classic
    /// "player flickers in and out" bug, so the list lives here, explicitly, rather than
    /// being recomputed from scratch each tick.
    /// </remarks>
    public sealed class PlayerView
    {
        /// <summary>Player indices the client currently knows about, in list order.</summary>
        public readonly List<int> LocalPlayers = new List<int>(64);

        /// <summary>NPC indices the client currently knows about.</summary>
        public readonly List<int> LocalNpcs = new List<int>(128);

        /// <summary>Last position the client was told about, for the per-tick delta.</summary>
        public Position LastSentPosition;

        /// <summary>True once the initial region packet has been sent.</summary>
        public bool Initialised;

        public void Clear()
        {
            LocalPlayers.Clear();
            LocalNpcs.Clear();
            Initialised = false;
        }
    }
}
