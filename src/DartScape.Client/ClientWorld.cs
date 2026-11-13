using System;
using System.Collections.Generic;
using DartScape.Game;

namespace DartScape.Client
{
    /// <summary>A player as the client knows it.</summary>
    public sealed class ClientPlayer
    {
        public int Index;
        public string Name = "";
        public int X;
        public int Y;
        public int Plane;

        /// <summary>Previous tile, for smooth interpolation between ticks.</summary>
        public int LastX;
        public int LastY;

        /// <summary>When this entity last started moving, for its own interpolation phase.</summary>
        public long MoveStartedMs;

        public int CombatLevel;
        public int Privilege;
        public int Animation = -1;
        public int Graphic = -1;
        public string OverheadText;
        public long OverheadUntilMs;
        public int DamageToShow;
        public int Hitpoints;
        public int MaxHitpoints;

        /// <summary>Appearance: 12 equipment visuals then 5 colours then 7 body parts.</summary>
        public int[] EquipmentIds = new int[12];
        public int[] Colours = new int[5];
        public int[] BodyParts = new int[7];
        public int Gender;
        public int HeadIcon = -1;
        public bool Skulled;
        public bool AppearanceReceived;

        public bool IsLocal;
    }

    /// <summary>An NPC as the client knows it.</summary>
    public sealed class ClientNpc
    {
        public int Index;
        public int DefinitionId;
        public string Name = "";
        public int CombatLevel;
        public int Size = 1;

        /// <summary>
        /// Menu options from the cache, in the order the game shows them.
        /// </summary>
        /// <remarks>
        /// Filled in from the local copy of <c>npc.dat</c> the first time the NPC is drawn, so
        /// a click can send the option the player actually means. Without it the client has to
        /// guess, and the guess is always "attack" — which is why clicking a farmer produced
        /// "You can't attack that" instead of a conversation.
        /// </remarks>
        public string[] Actions;

        /// <summary>The first option that exists, or -1.</summary>
        public int FirstActionIndex
        {
            get
            {
                if (Actions == null) return -1;

                for (int i = 0; i < Actions.Length; i++)
                {
                    if (!string.IsNullOrEmpty(Actions[i])) return i;
                }

                return -1;
            }
        }

        public int X;
        public int Y;
        public int Plane;
        public int LastX;
        public int LastY;

        /// <summary>When this entity last started moving, for its own interpolation phase.</summary>
        public long MoveStartedMs;
        public int Animation = -1;
        public int Graphic = -1;
        public int DamageToShow;
        public int Hitpoints;
        public int MaxHitpoints;
        public string OverheadText;
        public long OverheadUntilMs;
    }

    /// <summary>A stack of items on the ground.</summary>
    public sealed class ClientGroundItem
    {
        public int ItemId;
        public int Amount;
        public int X;
        public int Y;
        public int Plane;
        public string Name = "";
    }

    /// <summary>A line in the chat box.</summary>
    public sealed class ChatLine
    {
        public string Text;
        public int Type;
        public int Colour;
        public long AtMs;
    }

    /// <summary>
    /// Everything the client knows about the world, mutated by incoming packets and read
    /// by the renderer.
    /// </summary>
    public sealed class ClientWorld
    {
        /// <summary>Maximum player and NPC slots, matching the server.</summary>
        public const int MaxPlayers = 2048;
        public const int MaxNpcs = 32768;

        public readonly ClientPlayer[] Players = new ClientPlayer[MaxPlayers];
        public readonly ClientNpc[] Npcs = new ClientNpc[MaxNpcs];

        /// <summary>
        /// The cache the client loaded, for definitions the server does not send.
        /// </summary>
        /// <remarks>
        /// The map is loaded before login and lives here so the renderer, the pathfinder and
        /// the menu all read the same copy. It is <c>null</c> when the client runs without a
        /// cache, in which case NPCs simply have no menu options beyond "attack if hostile".
        /// </remarks>
        public static DartScape.Game.WorldMap Definitions;

        /// <summary>
        /// Fills in an NPC's menu options from the cache, if they are not known yet.
        /// </summary>
        /// <remarks>
        /// Done lazily rather than when the NPC arrives, because most NPCs on screen are never
        /// clicked. The definition set is already open for the map, so this is a plain array
        /// index.
        /// </remarks>
        public void EnsureNpcActions(ClientNpc npc)
        {
            if (npc == null || npc.Actions != null) return;

            DartScape.Game.Definitions.NpcDefinition definition =
                DartScape.Game.Definitions.DefinitionLoader.Npc(npc.DefinitionId);

            if (definition == null) return;

            npc.Actions = definition.Actions;

            // The server's name wins when it has one, because it is authoritative about what it
            // actually spawned; the cache fills the gap when it does not.
            if (string.IsNullOrEmpty(npc.Name) && definition.Name != null) npc.Name = definition.Name;
        }
        public readonly List<ClientGroundItem> GroundItems = new List<ClientGroundItem>();

        public readonly int[] Experience = new int[21];
        public readonly int[] BaseLevel = new int[21];
        public readonly int[] CurrentLevel = new int[21];

        public readonly ItemSlot[] Inventory = new ItemSlot[28];
        public int MyIndex = -1;
        public int MyPrivilege;
        public int RunEnergy = 10000;

        /// <summary>True when the player is running. Toggled by the run orb or a key.</summary>
        public bool RunEnabled;

        // -- local movement prediction ----------------------------------------
        //
        // The client moves its own player, and the server is told what happened rather than
        // the other way round. This is what the original does, and it is the only way the
        // local player can feel responsive: waiting for a round trip per tile at 600 ms a
        // tile means every step arrives a tick late and lands as a jump.
        //
        // The server still validates every step, so a modified client cannot walk through a
        // wall — it just gets corrected back.

        /// <summary>Length of one movement tick, matching the server.</summary>
        public const int TickMs = 600;

        /// <summary>Tiles queued for the local player, in order.</summary>
        public readonly List<Position> LocalPath = new List<Position>();

        private long _stepStartedMs;
        private bool _stepping;

        /// <summary>
        /// How far through the current step we are, in 0..1.
        /// </summary>
        /// <remarks>
        /// Cached rather than recomputed from the wall clock, because two things read it: the
        /// renderer, for the local player and the camera, and the tests. A getter that samples
        /// the clock on every read makes the camera fraction disagree with the fraction the
        /// player was drawn with in the same frame, and it makes the whole thing untestable.
        /// </remarks>
        private double _fraction = 1.0;

        /// <summary>
        /// Where the server thinks the local player is, tracked from its movement bits.
        /// </summary>
        /// <remarks>
        /// The client moves itself and does not render the server's copy, but the server is
        /// still the authority on the real position, and the two drift apart whenever a path is
        /// truncated at 25 tiles or a step is refused. Keeping a shadow position lets a
        /// disagreement be corrected at the one moment it is invisible: while the player is
        /// standing still and the server has stopped moving too.
        /// </remarks>
        private int _serverX;
        private int _serverY;
        private bool _serverKnown;

        /// <summary>
        /// Applies one of the server's movement bits for the local player, and reconciles once
        /// both sides have stopped.
        /// </summary>
        /// <returns>True when the local position was corrected.</returns>
        public bool NoteServerStep(int dx, int dy)
        {
            if (!_serverKnown) return false;

            _serverX += dx;
            _serverY += dy;

            ClientPlayer me = Me;
            if (me == null) return false;

            // While the client is still walking, the server is legitimately behind it, and
            // correcting now would yank the player backwards mid-stride.
            if (_stepping) return false;

            // The server is still moving too, so its position is not final yet.
            if (dx != 0 || dy != 0) return false;

            if (_serverX == me.X && _serverY == me.Y) return false;

            ForceLocalPosition(_serverX, _serverY, me.Plane);
            return true;
        }

        /// <summary>
        /// Replaces the local path and starts stepping immediately.
        /// </summary>
        /// <remarks>
        /// Starting on the same frame as the click is the whole point: if the first step waited
        /// for the next tick boundary, every click would feel like it was ignored for up to
        /// 600 ms before anything happened.
        /// </remarks>
        public void SetLocalPath(List<Position> path, long nowMs)
        {
            LocalPath.Clear();

            if (path == null || path.Count == 0)
            {
                _stepping = false;
                _fraction = 1.0;
                return;
            }

            LocalPath.AddRange(path);

            // If we were already walking, keep the current step's phase so a second click does
            // not reset the animation mid-stride. Otherwise take the first step immediately:
            // waiting up to 600 ms before moving makes every click feel ignored.
            if (!_stepping)
            {
                _stepStartedMs = nowMs;
                _stepping = true;
                _fraction = 0.0;
                StepLocal();
            }
        }

        /// <summary>
        /// Advances the local player by however many ticks have elapsed.
        /// </summary>
        /// <returns>
        /// Interpolation between the previous tile and the current one, in 0..1, for smooth
        /// rendering between ticks.
        /// </returns>
        public double AdvanceLocal(long nowMs)
        {
            if (!_stepping)
            {
                _fraction = 1.0;
                return 1.0;
            }

            // Drain whole ticks. A long pause (alt-tab, a stall) must not teleport the player
            // through their path, so the loop is bounded.
            int guard = 0;

            while (_stepping && nowMs - _stepStartedMs >= TickMs && guard++ < 4)
            {
                _stepStartedMs += TickMs;
                StepLocal();
            }

            if (!_stepping)
            {
                _fraction = 1.0;
                return 1.0;
            }

            double fraction = (double)(nowMs - _stepStartedMs) / TickMs;

            if (fraction < 0) fraction = 0;
            if (fraction > 1) fraction = 1;

            _fraction = fraction;

            return fraction;
        }

        /// <summary>Takes one tile of the local path.</summary>
        /// <remarks>
        /// When the path runs out the entity keeps stepping for one more tick. That is not a
        /// quirk: the final step still has to be interpolated, and stopping the moment the last
        /// tile is popped makes the player snap the final tile instead of gliding onto it.
        /// </remarks>
        private void StepLocal()
        {
            ClientPlayer me = Me;
            if (me == null)
            {
                _stepping = false;
                _fraction = 1.0;
                return;
            }

            if (LocalPath.Count == 0)
            {
                _stepping = false;
                _fraction = 1.0;
                return;
            }

            Position next = LocalPath[0];
            LocalPath.RemoveAt(0);

            me.LastX = me.X;
            me.LastY = me.Y;
            me.X = next.X;
            me.Y = next.Y;

            MyX = next.X;
            MyY = next.Y;
        }

        /// <summary>
        /// Moves the local player absolutely, discarding any predicted path.
        /// </summary>
        /// <remarks>
        /// Used when the server says the player was moved by something other than walking: a
        /// teleport, a respawn after death, or a correction. That is authoritative, so the local
        /// prediction is dropped rather than reconciled — continuing to walk after a teleport
        /// would drag the player back towards a path that no longer applies.
        /// </remarks>
        public void ForceLocalPosition(int x, int y, int plane)
        {
            MyX = x;
            MyY = y;
            MyPlane = plane;

            _serverX = x;
            _serverY = y;
            _serverKnown = true;

            ClientPlayer me = Me;
            if (me != null)
            {
                me.X = x;
                me.Y = y;
                me.LastX = x;
                me.LastY = y;
                me.Plane = plane;
            }

            LocalPath.Clear();
            _stepping = false;
            _fraction = 1.0;
        }

        /// <summary>True while the local player is walking.</summary>
        public bool IsLocalWalking
        {
            get { return _stepping; }
        }

        /// <summary>
        /// The server's view of the local player's x, or -1 when it is not known yet.
        /// </summary>
        /// <remarks>
        /// Exposed so a desync can be seen without a packet dump: the client walks itself, and
        /// the only way to tell that it has drifted is to compare its position with the one the
        /// server has been reporting.
        /// </remarks>
        public int ServerX
        {
            get { return _serverKnown ? _serverX : -1; }
        }

        /// <summary>The server's view of the local player's y, or -1 when unknown.</summary>
        public int ServerY
        {
            get { return _serverKnown ? _serverY : -1; }
        }

        /// <summary>
        /// Interpolation fraction for an entity that is not the local player, in 0..1.
        /// </summary>
        /// <remarks>
        /// Other players and NPCs move when a server packet says so, which arrives at an
        /// arbitrary phase relative to our own tick. Interpolating them with the local tick's
        /// fraction makes them slide backwards for a frame whenever the two phases disagree,
        /// which reads as jitter. Using the time since their own last move keeps each entity's
        /// motion self-consistent, which is what matters visually.
        /// </remarks>
        public static double EntityInterpolation(long moveStartedMs, long nowMs)
        {
            if (moveStartedMs == 0) return 1.0;

            double fraction = (double)(nowMs - moveStartedMs) / TickMs;

            if (fraction < 0) return 0;
            if (fraction > 1) return 1;

            return fraction;
        }

        /// <summary>
        /// Reconciles the local prediction with the position the server reports.
        /// </summary>
        /// <remarks>
        /// The local player is usually a tile or two ahead of the server, which is correct and
        /// must not be undone. A correction is only applied when the difference is larger than
        /// the client could legitimately have walked, which means the server refused a step or
        /// the player was teleported.
        /// </remarks>
        public bool ReconcileLocal(int serverX, int serverY, int serverPlane)
        {
            ClientPlayer me = Me;
            if (me == null) return false;

            int dx = Math.Abs(serverX - me.X);
            int dy = Math.Abs(serverY - me.Y);

            // Up to two tiles of lead is normal (running). Anything beyond that is a real
            // disagreement: a blocked step, a teleport, or a region change.
            if (dx <= 2 && dy <= 2 && serverPlane == me.Plane) return false;

            me.LastX = serverX;
            me.LastY = serverY;
            me.X = serverX;
            me.Y = serverY;
            me.Plane = serverPlane;

            MyX = serverX;
            MyY = serverY;
            MyPlane = serverPlane;

            _serverX = serverX;
            _serverY = serverY;
            _serverKnown = true;

            LocalPath.Clear();
            _stepping = false;
            _fraction = 1.0;

            return true;
        }

        public readonly List<ChatLine> Chat = new List<ChatLine>(100);
        public readonly List<string> ChatBuffer = new List<string>(8);

        /// <summary>Current player's tile, in world coordinates.</summary>
        public int MyX;
        public int MyY;
        public int MyPlane;

        /// <summary>
        /// The player's position between tiles, for the camera.
        /// </summary>
        /// <remarks>
        /// The camera must follow the smooth position, not the tile. Snapping it to whole tiles
        /// makes the whole world lurch once per tick even when the player themselves is drawn
        /// smoothly, which is the most noticeable kind of movement stutter.
        /// </remarks>
        public double MySmoothX
        {
            get
            {
                ClientPlayer me = Me;
                if (me == null) return MyX;

                double f = _fraction;

                return me.LastX + (me.X - me.LastX) * f;
            }
        }

        /// <summary>The player's position between tiles, vertically.</summary>
        public double MySmoothY
        {
            get
            {
                ClientPlayer me = Me;
                if (me == null) return MyY;

                double f = _fraction;

                return me.LastY + (me.Y - me.LastY) * f;
            }
        }

        /// <summary>Camera offset in tiles, adjusted by arrow keys / drag.</summary>
        public int CameraOffsetX;
        public int CameraOffsetY;

        /// <summary>Camera rotation in quarter turns, 0..3. RuneScape's compass.</summary>
        public int CameraRotation;

        /// <summary>Region base used to convert local map coordinates to world ones.</summary>
        public int BaseX = 3200;
        public int BaseY = 3200;

        /// <summary>Open interface id, or -1.</summary>
        public int OpenInterface = -1;

        /// <summary>Selected sidebar tab: 0 skills, 1 quests, 2 inventory, 3 equipment, 4 prayer, 5 magic.</summary>
        public int Tab;

        /// <summary>Currently selected inventory slot, or -1.</summary>
        public int SelectedSlot = -1;

        /// <summary>Server tick counter, used for animation timing.</summary>
        public int Tick;

        public struct ItemSlot
        {
            public int Id;
            public int Amount;

            public bool IsEmpty
            {
                get { return Id <= 0 || Amount <= 0; }
            }
        }

        public ClientPlayer Me
        {
            get { return MyIndex >= 0 && MyIndex < MaxPlayers ? Players[MyIndex] : null; }
        }

        /// <summary>Adds a chat line, keeping the buffer bounded.</summary>
        public void AddChatMessage(string text, int type = 0)
        {
            if (string.IsNullOrEmpty(text)) return;

            Chat.Add(new ChatLine
            {
                Text = text,
                Type = type,
                AtMs = DartScape.Core.TimeUtil.MonotonicMs()
            });

            while (Chat.Count > 200) Chat.RemoveAt(0);
        }

        /// <summary>Clears everything on disconnect.</summary>
        public void Reset()
        {
            for (int i = 0; i < Players.Length; i++) Players[i] = null;
            for (int i = 0; i < Npcs.Length; i++) Npcs[i] = null;
            GroundItems.Clear();
            MyIndex = -1;
            OpenInterface = -1;
            SelectedSlot = -1;
            for (int i = 0; i < Inventory.Length; i++) Inventory[i] = new ItemSlot();
        }

        /// <summary>Finds the nearest ground item to a world tile.</summary>
        public ClientGroundItem GroundItemAt(int x, int y, int plane)
        {
            for (int i = 0; i < GroundItems.Count; i++)
            {
                ClientGroundItem item = GroundItems[i];
                if (item.X == x && item.Y == y && item.Plane == plane) return item;
            }
            return null;
        }

        /// <summary>Finds an NPC at a tile, accounting for its size.</summary>
        public ClientNpc NpcAt(int x, int y, int plane)
        {
            for (int i = 0; i < Npcs.Length; i++)
            {
                ClientNpc npc = Npcs[i];
                if (npc == null || npc.Plane != plane) continue;
                if (x >= npc.X && x < npc.X + npc.Size && y >= npc.Y && y < npc.Y + npc.Size) return npc;
            }
            return null;
        }

        /// <summary>
        /// The entity whose drawn sprite covers a screen point, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Clicking an entity cannot be done by unprojecting the pixel to a tile. An entity is
        /// drawn as a sprite standing <em>up</em> from its tile, so on a slope the sprite's head
        /// is many pixels above the tile it belongs to — clicking a guard on a hill lands on the
        /// tile behind him and the click walks instead of attacking.
        /// </para>
        /// <para>
        /// The renderer records the rectangle each entity occupied, and this tests the click
        /// against those rectangles in reverse draw order, so the topmost thing under the cursor
        /// wins. That is the same ordering the original's menu uses.
        /// </para>
        /// </remarks>
        public object EntityAt(int screenX, int screenY)
        {
            if (_hits == null) return null;

            for (int i = _hits.Count - 1; i >= 0; i--)
            {
                EntityHit hit = _hits[i];

                if (screenX < hit.X || screenX >= hit.X + hit.Width) continue;
                if (screenY < hit.Y || screenY >= hit.Y + hit.Height) continue;

                if (hit.Npc != null && Npcs[hit.Npc.Index] == hit.Npc) return hit.Npc;
                if (hit.Player != null && Players[hit.Player.Index] == hit.Player) return hit.Player;
            }

            return null;
        }

        /// <summary>One entity's screen rectangle, recorded while drawing.</summary>
        public struct EntityHit
        {
            public ClientNpc Npc;
            public ClientPlayer Player;
            public int X;
            public int Y;
            public int Width;
            public int Height;
        }

        private readonly List<EntityHit> _hits = new List<EntityHit>(64);

        /// <summary>Clears the recorded rectangles. Called once per frame, before drawing.</summary>
        public void BeginHitTest()
        {
            _hits.Clear();
        }

        /// <summary>Records where an entity was drawn.</summary>
        public void AddHit(EntityHit hit)
        {
            _hits.Add(hit);
        }

        /// <summary>Finds a player at a tile.</summary>
        public ClientPlayer PlayerAt(int x, int y, int plane)
        {
            for (int i = 0; i < Players.Length; i++)
            {
                ClientPlayer player = Players[i];
                if (player == null || player.Plane != plane) continue;
                if (player.X == x && player.Y == y) return player;
            }
            return null;
        }
    }
}
