using System;
using System.Collections.Generic;
using DartScape.Server.Definitions;

namespace DartScape.Server.Game
{
    /// <summary>A non-player character: one live instance of an <see cref="NpcDefinition"/>.</summary>
    public sealed class Npc
    {
        /// <summary>Index in the world's NPC table (0..32767).</summary>
        public int Index = -1;

        /// <summary>Definition id from the cache.</summary>
        public int DefinitionId;

        public NpcDefinition Definition;

        public Position Position;
        public Position SpawnPosition;

        /// <summary>Current hitpoints. Zero means dead and awaiting respawn.</summary>
        public int Hitpoints;

        public int MaxHitpoints;

        /// <summary>Direction the NPC faces, 0..7, or -1.</summary>
        public int FaceDirection = -1;

        /// <summary>Tile the NPC is turning towards, or <c>null</c>.</summary>
        public Position? FaceTile;

        /// <summary>Player index this NPC is targeting, or -1.</summary>
        public int TargetPlayer = -1;

        /// <summary>Player index that last attacked this NPC, or -1.</summary>
        public int LastAttacker = -1;

        public int AttackDelay;
        public int TicksUntilRespawn;
        public bool Dead;

        /// <summary>Wandering step cooldown so NPCs do not vibrate on the spot.</summary>
        public int WanderCooldown;

        // -- per-tick update state -------------------------------------------

        public int UpdateMask;
        public bool MovedThisTick;
        public int FirstStepDirection = -1;
        public int SecondStepDirection = -1;
        public int Animation = -1;
        public int Graphic = -1;
        public int GraphicHeight;
        public int GraphicDelay;
        public string ForcedChat;
        public int DamageToShow;
        public int DamageType;
        public int TurnDirection = -1;

        /// <summary>Definition id the client is currently showing, for transform support.</summary>
        public int DisplayedDefinitionId;

        public int Size
        {
            get { return Definition == null ? 1 : Definition.Size; }
        }

        public string Name
        {
            get { return Definition == null ? "null" : Definition.Name; }
        }

        public bool IsDead
        {
            get { return Dead || Hitpoints <= 0; }
        }

        /// <summary>True when <paramref name="other"/> is within one tile, accounting for size.</summary>
        public bool IsAdjacentTo(Position other)
        {
            if (Position.Plane != other.Plane) return false;

            int reach = Size;
            int dx = Math.Abs(Position.X - other.X);
            int dy = Math.Abs(Position.Y - other.Y);

            return dx <= reach && dy <= reach;
        }

        public void ClearUpdateFlags()
        {
            UpdateMask = 0;
            MovedThisTick = false;
            FirstStepDirection = -1;
            SecondStepDirection = -1;
            Animation = -1;
            Graphic = -1;
            ForcedChat = null;
            DamageToShow = 0;
            TurnDirection = -1;
            FaceTile = null;
        }

        /// <summary>Resets a dead NPC at its spawn point with full health.</summary>
        public void Respawn()
        {
            Position = SpawnPosition;
            Hitpoints = MaxHitpoints;
            Dead = false;
            TicksUntilRespawn = 0;
            TargetPlayer = -1;
            LastAttacker = -1;
            AttackDelay = 0;
            WanderCooldown = 0;
            FaceDirection = -1;
            FaceTile = null;
            ClearUpdateFlags();
        }

        public override string ToString()
        {
            return Name + "#" + Index + " (" + DefinitionId + ")";
        }
    }

    /// <summary>A stack of items lying on a tile.</summary>
    public sealed class GroundItem
    {
        public int ItemId;
        public int Amount;
        public Position Position;
        public DateTime DroppedAtUtc;
        public string OwnerName;
        public long OwnerUntilMs;

        /// <summary>Ticks remaining before the item vanishes. -1 means permanent.</summary>
        public int TicksRemaining = -1;

        public bool IsVisibleTo(string nameKey, long nowMs)
        {
            if (string.IsNullOrEmpty(OwnerName)) return true;
            if (OwnerName == nameKey) return true;
            return nowMs >= OwnerUntilMs;
        }
    }
}
