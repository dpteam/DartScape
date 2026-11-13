using System;
using System.Collections.Generic;
using DartScape.Game.Definitions;

namespace DartScape.Game
{
    /// <summary>
    /// The network-facing half of a player, implemented by the connection layer.
    /// </summary>
    /// <remarks>
    /// The world simulation must not know about sockets, encryption or packet sizes.
    /// It talks to this interface instead, which keeps <see cref="Player"/> testable and
    /// stops a networking change from rippling through the combat and skill code.
    /// </remarks>
    public interface IPlayerEndpoint
    {
        /// <summary>Queues one packet for delivery at the end of this tick.</summary>
        void Send(int opcode, Action<Core.Protocol.PacketBuffer> writer);

        /// <summary>Sends a plain chat-box message.</summary>
        void SendMessage(string text);

        /// <summary>Closes the connection with an optional reason shown to the player.</summary>
        void Disconnect(string reason);
    }

    /// <summary>What a player is currently doing, used to gate conflicting actions.</summary>
    public enum ActionState
    {
        Idle = 0,
        Walking = 1,
        Combat = 2,
        Busy = 3,
        Dead = 4
    }

    /// <summary>
    /// A player character: identity, position, skills, containers and per-tick state.
    /// </summary>
    /// <remarks>
    /// Everything mutable here is touched only from the world tick thread. The network
    /// layer hands packets over through a queue and reads nothing directly, so there is
    /// exactly one writer and no locking in the hot path.
    /// </remarks>
    public sealed class Player
    {
        /// <summary>Number of inventory slots.</summary>
        public const int InventorySize = 28;

        /// <summary>Number of worn equipment slots.</summary>
        public const int EquipmentSize = 14;

        /// <summary>Number of bank slots.</summary>
        public const int BankSize = 352;

        /// <summary>Client interface id for the inventory.</summary>
        public const int InventoryInterface = 3214;

        /// <summary>Client interface id for the worn equipment.</summary>
        public const int EquipmentInterface = 1688;

        /// <summary>Client interface id for the bank.</summary>
        public const int BankInterface = 5382;

        // -- identity ---------------------------------------------------------

        /// <summary>Display name, already normalised for display.</summary>
        public string Name = "";

        /// <summary>Lowercase name used for lookups and file names.</summary>
        public string NameKey = "";

        /// <summary>World index (0..2047). Assigned on login.</summary>
        public int Index = -1;

        /// <summary>0 = player, 1 = moderator, 2 = administrator.</summary>
        public int Privilege;

        /// <summary>True while the player is connected and past login.</summary>
        public bool Online;

        /// <summary>True once the client has confirmed the region is loaded.</summary>
        public bool RegionLoaded;

        /// <summary>The connection this player arrived on, or <c>null</c> after a drop.</summary>
        public IPlayerEndpoint Endpoint;

        // -- position and movement -------------------------------------------

        public Position Position;

        /// <summary>Where the player stood before this tick, used for the update block.</summary>
        public Position LastPosition;

        /// <summary>Queued walking steps, in order. Cleared when a new destination arrives.</summary>
        public readonly List<Position> Waypoints = new List<Position>();

        /// <summary>
        /// Where the player asked to go, kept so a path that was truncated can be continued.
        /// </summary>
        /// <remarks>
        /// A path is capped at 25 tiles. Without remembering the destination, a request that
        /// ends exactly at the cap leaves the player one tile short, and because the client
        /// walks itself it stays one tile ahead of the server for ever. Re-pathing when the
        /// waypoint queue empties keeps the server walking until it actually arrives.
        /// </remarks>
        public Position WalkDestination;

        /// <summary>True while <see cref="WalkDestination"/> is still being walked towards.</summary>
        public bool HasWalkDestination;

        /// <summary>Cancels walking and forgets where the player was heading.</summary>
        public void StopWalking()
        {
            Waypoints.Clear();
            HasWalkDestination = false;
        }

        /// <summary>Direction the player is facing (0..7) or -1.</summary>
        public int FaceDirection = -1;

        /// <summary>Tile the player is turning towards, or <c>null</c>.</summary>
        public Position? FaceTile;

        /// <summary>Entity the player is facing or interacting with, or -1.</summary>
        public int InteractingNpcIndex = -1;
        public int InteractingPlayerIndex = -1;

        /// <summary>Running costs energy; walking does not.</summary>
        public bool RunEnabled;

        /// <summary>Run energy, 0..10000 (the client shows energy/100 as a percentage).</summary>
        public int RunEnergy = 10000;

        /// <summary>True when the player moved this tick, and how far.</summary>
        public bool MovedThisTick;
        public int StepsThisTick;
        public int FirstStepDirection = -1;
        public int SecondStepDirection = -1;

        /// <summary>Set when the client must be told the absolute position instead of a delta.</summary>
        public bool Teleported;

        // -- appearance -------------------------------------------------------

        public int Gender;              // 0 = male, 1 = female
        public int HeadIcon = -1;
        public int NpcTransform = -1;   // set by the "transform" command
        public readonly int[] Appearance = new int[12];   // 0..6 = body parts, 7..11 = colours
        public readonly ItemStack[] EquipmentVisual = new ItemStack[12];

        // -- skills -----------------------------------------------------------

        public readonly int[] Experience = new int[Skills.Count];
        public readonly int[] BaseLevel = new int[Skills.Count];
        public readonly int[] CurrentLevel = new int[Skills.Count];

        // -- containers -------------------------------------------------------

        public readonly ItemContainer Inventory = new ItemContainer(InventoryInterface, InventorySize);
        public readonly ItemContainer Equipment = new ItemContainer(EquipmentInterface, EquipmentSize);
        public readonly ItemContainer Bank = new ItemContainer(BankInterface, BankSize);

        /// <summary>Item currently "used" with another item, or -1.</summary>
        public int SelectedItemSlot = -1;
        public int SelectedItemId = -1;

        /// <summary>Item currently used on an interface (noting, alching), or -1.</summary>
        public int InterfaceItemId = -1;
        public int InterfaceItemSlot = -1;

        // -- health and combat ------------------------------------------------

        public int Hitpoints;
        public int MaxHitpoints;
        public int PoisonDamage;
        public int PoisonTicks;
        public int SkullTicks;
        public int CombatTargetNpc = -1;

        /// <summary>
        /// An action to run once the player is next to its target.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the original's <c>crossX/crossY/crossType/crossIndex</c> idea, reduced to the
        /// one case that matters here: clicking something out of reach walks you to it and then
        /// does what you asked. Without it a click on a distant tree or NPC does nothing, and
        /// the player has to click again on arrival — which is not how the game behaves.
        /// </para>
        /// <para>
        /// The action is cancelled by any new click, because the last click always wins.
        /// </para>
        /// </remarks>
        public PendingAction Pending;

        /// <summary>True while a queued action is waiting for the player to arrive.</summary>
        public bool HasPending
        {
            get { return Pending.Kind != PendingActionKind.None; }
        }

        /// <summary>Forgets any queued action.</summary>
        public void ClearPending()
        {
            Pending = new PendingAction();
        }
        public int CombatTargetPlayer = -1;
        public int AttackDelay;
        public int LastDamageTaken;
        public int LastDamageDealt;
        public int DeathTicks;

        /// <summary>Recent attackers, newest last, used for the "you were killed by" message.</summary>
        public readonly List<string> RecentAttackers = new List<string>();

        // -- per-tick update flags -------------------------------------------

        /// <summary>Bitmask of update blocks to send for this player this tick.</summary>
        public int UpdateMask;

        /// <summary>Animation to play, or -1.</summary>
        public int Animation = -1;
        public int AnimationDelay;

        /// <summary>Graphic (spot animation) to play, or -1.</summary>
        public int Graphic = -1;
        public int GraphicHeight;
        public int GraphicDelay;

        /// <summary>Forced overhead text (from the <c>~</c> prefix in chat).</summary>
        public string ForcedChat;

        /// <summary>Public chat message to broadcast this tick, or <c>null</c>.</summary>
        public string ChatText;
        public int ChatColour;
        public int ChatEffect;
        public int ChatRights;

        /// <summary>Damage to display above the head this tick, or 0.</summary>
        public int DamageToShow;
        public int DamageType;

        /// <summary>True when the appearance block must be rebuilt and sent.</summary>
        public bool AppearanceDirty;

        // -- bookkeeping ------------------------------------------------------

        public DateTime LoginTimeUtc = DateTime.UtcNow;
        public DateTime LastActivityUtc = DateTime.UtcNow;
        public long LastChatMs;
        public long LastCommandMs;
        public int TicksSinceAction;
        public bool NewAccount;

        /// <summary>Skills whose level-up dialogue should be shown, queued for this tick.</summary>
        public readonly List<int> PendingLevelUps = new List<int>();

        /// <summary>What this player's client currently knows about.</summary>
        public PlayerView View = new PlayerView();

        // -- derived ----------------------------------------------------------

        public bool IsDead
        {
            get { return CurrentLevel[Skills.Hitpoints] <= 0 || Hitpoints <= 0; }
        }

        public int CombatLevel
        {
            get
            {
                int attack = BaseLevel[Skills.Attack];
                int defence = BaseLevel[Skills.Defence];
                int strength = BaseLevel[Skills.Strength];
                int hitpoints = BaseLevel[Skills.Hitpoints];
                int prayer = BaseLevel[Skills.Prayer];
                int ranged = BaseLevel[Skills.Ranged];
                int magic = BaseLevel[Skills.Magic];

                double baseLevel = 0.25 * (defence + hitpoints + Math.Floor(prayer / 2.0));
                double melee = 0.325 * (attack + strength);
                double range = 0.325 * Math.Floor(ranged * 1.5);
                double mage = 0.325 * Math.Floor(magic * 1.5);

                double highest = Math.Max(melee, Math.Max(range, mage));
                return (int)Math.Floor(baseLevel + highest);
            }
        }

        public int TotalLevel
        {
            get
            {
                int total = 0;
                for (int i = 0; i < Skills.Count; i++) total += BaseLevel[i];
                return total;
            }
        }

        public long TotalExperience
        {
            get
            {
                long total = 0;
                for (int i = 0; i < Skills.Count; i++) total += Experience[i];
                return total;
            }
        }

        // -- construction and skills -----------------------------------------

        public Player()
        {
            for (int i = 0; i < Skills.Count; i++)
            {
                Experience[i] = 0;
                BaseLevel[i] = 1;
                CurrentLevel[i] = 1;
            }
            BaseLevel[Skills.Hitpoints] = 10;
            CurrentLevel[Skills.Hitpoints] = 10;
            Experience[Skills.Hitpoints] = Skills.ExperienceAt(10);
            Hitpoints = 10;
            MaxHitpoints = 10;
        }

        /// <summary>Recomputes base levels from experience. Called after loading and after XP gain.</summary>
        public void RecalculateLevels()
        {
            for (int i = 0; i < Skills.Count; i++)
            {
                int level = Skills.LevelFor(Experience[i]);
                if (i == Skills.Hitpoints && level < 10) level = 10;
                BaseLevel[i] = level;
            }

            MaxHitpoints = BaseLevel[Skills.Hitpoints];
            if (Hitpoints <= 0) Hitpoints = MaxHitpoints;
            if (Hitpoints > MaxHitpoints) Hitpoints = MaxHitpoints;
        }

        /// <summary>
        /// Grants experience and reports level-ups.
        /// </summary>
        /// <returns>The new level if the player levelled up, otherwise -1.</returns>
        public int AddExperience(int skill, int amount, double rate)
        {
            if (skill < 0 || skill >= Skills.Count || amount <= 0) return -1;

            int gained = (int)Math.Round(amount * rate);
            if (gained <= 0) gained = 1;

            int before = BaseLevel[skill];
            long total = (long)Experience[skill] + gained;
            if (total > Skills.MaxExperience) total = Skills.MaxExperience;
            Experience[skill] = (int)total;

            int after = Skills.LevelFor(Experience[skill]);
            if (skill == Skills.Hitpoints && after < 10) after = 10;

            if (after > before)
            {
                BaseLevel[skill] = after;
                // A level-up also tops the current level up, exactly like the original.
                CurrentLevel[skill] = after;
                if (skill == Skills.Hitpoints)
                {
                    MaxHitpoints = after;
                    Hitpoints += after - before;
                    if (Hitpoints > MaxHitpoints) Hitpoints = MaxHitpoints;
                }
                PendingLevelUps.Add(skill);
                return after;
            }

            BaseLevel[skill] = after;
            if (CurrentLevel[skill] > after) CurrentLevel[skill] = after;
            return -1;
        }

        /// <summary>Restores every stat to its base level (used on respawn).</summary>
        public void RestoreStats()
        {
            for (int i = 0; i < Skills.Count; i++) CurrentLevel[i] = BaseLevel[i];
            Hitpoints = MaxHitpoints;
            PoisonTicks = 0;
            PoisonDamage = 0;
        }

        // -- helpers ----------------------------------------------------------

        public void ClearUpdateFlags()
        {
            UpdateMask = 0;
            Animation = -1;
            Graphic = -1;
            ForcedChat = null;
            ChatText = null;
            DamageToShow = 0;
            MovedThisTick = false;
            StepsThisTick = 0;
            FirstStepDirection = -1;
            SecondStepDirection = -1;
            Teleported = false;
            FaceTile = null;
        }

        /// <summary>
        /// Queues a walk to <paramref name="destination"/>, replacing any existing path.
        /// </summary>
        /// <remarks>
        /// The path comes from <see cref="PathFinder"/>, not from a straight line. That is the
        /// difference between walking around a tree and walking through it: a straight line
        /// toward the target is fine on open ground and wrong the moment anything is in the
        /// way, and the world has trees on almost every tile.
        ///
        /// When no path exists the player still walks as far as they can, which is what the
        /// original does — clicking the far side of a wall should bring you to the wall, not
        /// leave you standing still.
        /// </remarks>
        public void WalkTo(Position destination, PathFinder pathFinder = null)
        {
            Waypoints.Clear();

            if (destination.Plane != Position.Plane)
            {
                HasWalkDestination = false;
                return;
            }

            if (destination.Equals(Position))
            {
                HasWalkDestination = false;
                return;
            }

            WalkDestination = destination;
            HasWalkDestination = true;

            if (pathFinder != null)
            {
                List<Position> path = pathFinder.Find(Position, destination);
                if (path.Count > 0)
                {
                    Waypoints.AddRange(path);
                    return;
                }
            }

            // No pathfinder, or nothing reachable: fall back to stepping directly toward the
            // target so the player is never completely stuck.
            WalkDirect(destination);
        }

        /// <summary>Straight-line fallback used when no path can be found.</summary>
        private void WalkDirect(Position destination)
        {
            int dx = destination.X - Position.X;
            int dy = destination.Y - Position.Y;

            int steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (steps <= 0) return;
            if (steps > 104) steps = 104;

            int x = Position.X;
            int y = Position.Y;

            for (int i = 0; i < steps; i++)
            {
                if (x != destination.X) x += Math.Sign(destination.X - x);
                if (y != destination.Y) y += Math.Sign(destination.Y - y);
                Waypoints.Add(new Position(x, y, Position.Plane));
            }
        }

        /// <summary>True when the player can reach the given tile with one more step.</summary>
        public bool IsAdjacentTo(Position other)
        {
            return Position.Plane == other.Plane && Position.ChebyshevDistance(other) <= 1;
        }

        /// <summary>Item name for messages, falling back to the id when unknown.</summary>
        public static string ItemName(int itemId)
        {
            ItemDefinition def = DefinitionLoader.Item(itemId);
            return def == null ? "item " + itemId : def.Name;
        }

        public override string ToString()
        {
            return Name + "(" + Index + ")";
        }
    }
}
