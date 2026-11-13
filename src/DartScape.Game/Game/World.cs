using System;
using System.Collections.Generic;
using System.IO;
using DartScape.Core.Diagnostics;
using DartScape.Core.Protocol;
using DartScape.Game.Definitions;


namespace DartScape.Game
{
    /// <summary>An item stack on the ground with its owning tile.</summary>
    public sealed class WorldItem
    {
        public int ItemId;
        public int Amount;
        public Position Position;
        public string OwnerName;
        public long OwnerUntilMs;
        public int TicksRemaining;
    }

    /// <summary>
    /// The world: every entity, the tick loop, and the rules that connect them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is exactly one thread. The network layer accepts connections and parses
    /// packets on its own threads, but every mutation of world state happens inside
    /// <see cref="Tick"/>, from queued input. That removes an entire class of race
    /// conditions and makes the whole simulation reproducible from a packet log.
    /// </para>
    /// <para>
    /// The tick is 600 ms by default, matching the original game. Movement, combat,
    /// regeneration, NPC AI and ground item expiry all advance one step per tick.
    /// </para>
    /// </remarks>
    public sealed class World
    {
        /// <summary>Maximum number of player slots the client can address.</summary>
        public const int MaxPlayers = 2048;

        /// <summary>Maximum number of NPC slots.</summary>
        public const int MaxNpcs = 32768;

        private readonly ServerConfig _config;
        private readonly Random _random = new Random();

        private readonly Player[] _players = new Player[MaxPlayers];
        private readonly Npc[] _npcs = new Npc[MaxNpcs];
        private readonly Dictionary<int, List<WorldItem>> _groundItems = new Dictionary<int, List<WorldItem>>();
        private readonly object _groundLock = new object();

        private readonly List<WorldEvent> _events = new List<WorldEvent>();
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _inputs =
            new System.Collections.Concurrent.ConcurrentQueue<Action>();
        private long _eventSeq;
        private long _tickCount;
        private DateTime _startedUtc = DateTime.UtcNow;
        private double _lastTickMs = 40;
        private readonly double[] _tickHistory = new double[32];
        private int _tickHistoryPos;
        private int _tickHistoryCount;

        /// <summary>Raised when a player's state changes in a way the web layer cares about.</summary>
        public event Action PlayersChanged;

        /// <summary>
        /// Sends this tick's update packets. The networking layer installs this; the world
        /// deliberately knows nothing about packet encoding.
        /// </summary>
        public Action<World> UpdateSender;

        /// <summary>
        /// Collision for the area around the start point, and the pathfinder that uses it.
        /// </summary>
        /// <remarks>
        /// Rebuilt whenever a player moves far enough that the local 104x104 window no longer
        /// covers them. One map is enough while everyone is in the same part of the world,
        /// which is the case for a single small world; a per-region map is the next step when
        /// the landscape loader lands.
        /// </remarks>
        public CollisionMap Collision { get; private set; }

        /// <summary>The pathfinder over <see cref="Collision"/>.</summary>
        public PathFinder Paths { get; private set; }

        private int _collisionCentreX;
        private int _collisionCentreY;

        /// <summary>Rebuilds the collision window around a world position.</summary>
        public void RebuildCollision(int centreX, int centreY)
        {
            // The collision map is a fixed 104x104 window, so its base is the centre minus
            // half that. Keeping the player near the middle leaves room to walk in any
            // direction without immediately falling off the edge.
            int baseX = centreX - CollisionMap.Size / 2;
            int baseY = centreY - CollisionMap.Size / 2;

            var map = new CollisionMap(baseX, baseY);
            WorldTerrain.BuildCollision(map, centreX, centreY, CollisionMap.Size / 2);

            Collision = map;
            Paths = new PathFinder(map);
            _collisionCentreX = centreX;
            _collisionCentreY = centreY;

            Log.Debug("World", "Collision rebuilt around {0},{1}.", centreX, centreY);
        }

        /// <summary>
        /// Rebuilds collision if a player has walked far enough from its centre.
        /// </summary>
        private void EnsureCollisionCovers(Player player)
        {
            if (Collision == null)
            {
                RebuildCollision(player.Position.X, player.Position.Y);
                return;
            }

            int dx = player.Position.X - _collisionCentreX;
            int dy = player.Position.Y - _collisionCentreY;

            // Rebuild once the player is within a comfortable margin of the edge, so the
            // window always covers what they can see.
            if (Math.Abs(dx) > CollisionMap.Size / 2 - 20 || Math.Abs(dy) > CollisionMap.Size / 2 - 20)
            {
                RebuildCollision(player.Position.X, player.Position.Y);
            }
        }

        public World(ServerConfig config)
        {
            if (config == null) throw new ArgumentNullException("config");
            _config = config;
        }

        public ServerConfig Config
        {
            get { return _config; }
        }

        /// <summary>Monotonic tick counter since start.</summary>
        public long TickCount
        {
            get { return _tickCount; }
        }

        public DateTime StartedUtc
        {
            get { return _startedUtc; }
        }

        public TimeSpan Uptime
        {
            get { return DateTime.UtcNow - _startedUtc; }
        }

        /// <summary>Average tick duration in milliseconds over the last 32 ticks.</summary>
        public double AverageTickMs
        {
            get
            {
                if (_tickHistoryCount == 0) return _lastTickMs;
                double sum = 0;
                for (int i = 0; i < _tickHistoryCount; i++) sum += _tickHistory[i];
                return sum / _tickHistoryCount;
            }
        }

        public double TicksPerSecond
        {
            get { return 1000.0 / Math.Max(1, _config.TickMs); }
        }

        // -- players ----------------------------------------------------------

        /// <summary>All player slots. Entries are <c>null</c> when unused.</summary>
        public Player[] Players
        {
            get { return _players; }
        }

        /// <summary>All NPC slots. Entries are <c>null</c> when unused.</summary>
        public Npc[] Npcs
        {
            get { return _npcs; }
        }

        public int PlayerCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _players.Length; i++)
                {
                    Player p = _players[i];
                    if (p != null && p.Online) count++;
                }
                return count;
            }
        }

        /// <summary>Online players, ordered by combat level descending.</summary>
        public List<Player> OnlinePlayers()
        {
            var list = new List<Player>(PlayerCount);
            for (int i = 0; i < _players.Length; i++)
            {
                Player p = _players[i];
                if (p != null && p.Online) list.Add(p);
            }
            list.Sort((a, b) => b.CombatLevel.CompareTo(a.CombatLevel));
            return list;
        }

        /// <summary>Finds an online player by display name or key. Case-insensitive.</summary>
        public Player FindPlayer(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string key = Core.Protocol.TextEncoding.NormaliseName(name);

            for (int i = 0; i < _players.Length; i++)
            {
                Player p = _players[i];
                if (p == null || !p.Online) continue;
                if (p.NameKey == key) return p;
            }
            return null;
        }

        public Player PlayerAt(int index)
        {
            if (index < 0 || index >= _players.Length) return null;
            return _players[index];
        }

        /// <summary>
        /// Places a freshly authenticated player into the world.
        /// </summary>
        /// <returns><c>null</c> on success, otherwise the reason the login was refused.</returns>
        public string AddPlayer(Player player)
        {
            if (player == null) return "no player";

            // A duplicate login takes over the old session, which is what players
            // expect when they reconnect after a network drop.
            Player existing = FindPlayer(player.NameKey);
            if (existing != null)
            {
                Log.Info("World", "{0} logged in again; dropping the previous session.", existing.Name);
                RemovePlayer(existing, "replaced by a new login");
            }

            if (PlayerCount >= _config.MaxPlayers) return "The world is full.";

            int index = -1;
            for (int i = 1; i < _players.Length; i++)
            {
                if (_players[i] == null) { index = i; break; }
            }
            if (index < 0) return "The world is full.";

            player.Index = index;
            player.Online = true;
            player.RegionLoaded = false;
            player.LoginTimeUtc = DateTime.UtcNow;
            player.LastActivityUtc = DateTime.UtcNow;
            player.Position = new Position(_config.StartX, _config.StartY, _config.StartPlane);
            player.LastPosition = player.Position;
            player.RecalculateLevels();
            if (player.Hitpoints <= 0) player.Hitpoints = player.MaxHitpoints;

            if (player.View == null) player.View = new PlayerView();
            player.View.Clear();
            player.View.LastSentPosition = player.Position;

            // Force the appearance block out on the first tick, so the player sees their
            // own character and everyone else sees it too. Without this a new character
            // is drawn with default colours until something else marks it dirty.
            player.AppearanceDirty = true;

            _players[index] = player;

            // Give the new player a collision window before their first tick, so the very
            // first step they take is validated like every other.
            EnsureCollisionCovers(player);

            AddEvent("login", player.Name + " logged in");
            Log.Info("World", "{0} joined at {1} ({2} online).", player.Name, player.Position, PlayerCount);
            RaisePlayersChanged();
            return null;
        }

        /// <summary>Removes a player from the world, saving is the caller's job.</summary>
        public void RemovePlayer(Player player, string reason)
        {
            if (player == null) return;

            if (player.Index >= 0 && player.Index < _players.Length && _players[player.Index] == player)
            {
                _players[player.Index] = null;
            }

            if (player.Online)
            {
                player.Online = false;
                AddEvent("logout", player.Name + " left the world");
                Log.Info("World", "{0} left ({1}). {2} online.", player.Name, reason, PlayerCount);
            }

            // Tell everyone who could see them to drop them.
            for (int i = 0; i < _players.Length; i++)
            {
                Player other = _players[i];
                if (other == null || other.View == null) continue;
                other.View.LocalPlayers.Remove(player.Index);
            }

            RaisePlayersChanged();
        }

        // -- npcs -------------------------------------------------------------

        public Npc NpcAt(int index)
        {
            if (index < 0 || index >= _npcs.Length) return null;
            return _npcs[index];
        }

        /// <summary>Spawns an NPC of the given definition at a position.</summary>
        public Npc SpawnNpc(int definitionId, Position position)
        {
            NpcDefinition def = DefinitionLoader.Npc(definitionId);
            if (def == null)
            {
                Log.Warning("World", "No NPC definition {0}.", definitionId);
                return null;
            }

            int index = -1;
            for (int i = 1; i < _npcs.Length; i++)
            {
                if (_npcs[i] == null) { index = i; break; }
            }
            if (index < 0)
            {
                Log.Warning("World", "No free NPC slot for definition {0}.", definitionId);
                return null;
            }

            var npc = new Npc
            {
                Index = index,
                DefinitionId = definitionId,
                DisplayedDefinitionId = definitionId,
                Definition = def,
                Position = position,
                SpawnPosition = position,
                Hitpoints = def.Hitpoints,
                MaxHitpoints = def.Hitpoints,
                WanderCooldown = _random.Next(4, 16)
            };

            _npcs[index] = npc;
            return npc;
        }

        /// <summary>Removes an NPC from the world entirely.</summary>
        public void RemoveNpc(Npc npc)
        {
            if (npc == null) return;
            if (npc.Index >= 0 && npc.Index < _npcs.Length && _npcs[npc.Index] == npc) _npcs[npc.Index] = null;

            for (int i = 0; i < _players.Length; i++)
            {
                Player p = _players[i];
                if (p == null || p.View == null) continue;
                p.View.LocalNpcs.Remove(npc.Index);
            }
        }

        public int NpcCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _npcs.Length; i++) if (_npcs[i] != null) count++;
                return count;
            }
        }

        // -- ground items -----------------------------------------------------

        /// <summary>Drops an item on the ground.</summary>
        public void AddGroundItem(int itemId, int amount, Position position, string ownerName = null, int ownerMs = 60000, int ticks = 200)
        {
            if (itemId < 0 || amount <= 0) return;

            long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            var item = new WorldItem
            {
                ItemId = itemId,
                Amount = amount,
                Position = position,
                OwnerName = ownerName,
                OwnerUntilMs = ownerName == null ? 0 : now + ownerMs,
                TicksRemaining = ticks
            };

            lock (_groundLock)
            {
                int key = position.Pack();
                List<WorldItem> list;
                if (!_groundItems.TryGetValue(key, out list))
                {
                    list = new List<WorldItem>(4);
                    _groundItems[key] = list;
                }

                // Merge with an existing stack of the same item when possible.
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].ItemId == itemId && list[i].OwnerName == ownerName)
                    {
                        list[i].Amount += amount;
                        list[i].TicksRemaining = ticks;
                        BroadcastGroundItemAdd(position, itemId, list[i].Amount);
                        return;
                    }
                }

                list.Add(item);
            }

            BroadcastGroundItemAdd(position, itemId, amount);
        }

        /// <summary>Returns the items visible on a tile for a player.</summary>
        public List<WorldItem> ItemsAt(Position position)
        {
            lock (_groundLock)
            {
                List<WorldItem> list;
                if (!_groundItems.TryGetValue(position.Pack(), out list)) return new List<WorldItem>(0);
                return new List<WorldItem>(list);
            }
        }

        /// <summary>All ground items, flattened. Used by the admin panel.</summary>
        public int GroundItemCount
        {
            get
            {
                lock (_groundLock)
                {
                    int total = 0;
                    foreach (KeyValuePair<int, List<WorldItem>> kv in _groundItems) total += kv.Value.Count;
                    return total;
                }
            }
        }

        private bool RemoveGroundItem(Position position, int itemId, int amount)
        {
            lock (_groundLock)
            {
                int key = position.Pack();
                List<WorldItem> list;
                if (!_groundItems.TryGetValue(key, out list)) return false;

                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].ItemId != itemId) continue;

                    if (list[i].Amount > amount)
                    {
                        list[i].Amount -= amount;
                        BroadcastGroundItemAmount(position, itemId, list[i].Amount);
                    }
                    else
                    {
                        list.RemoveAt(i);
                        BroadcastGroundItemRemove(position, itemId);
                    }
                    return true;
                }
            }
            return false;
        }

        // -- events (for the web status page) --------------------------------

        public sealed class WorldEvent
        {
            public long Seq;
            public long At;
            public string Kind;
            public string Text;
        }

        /// <summary>Adds an entry to the rolling world event feed.</summary>
        public void AddEvent(string kind, string text)
        {
            lock (_events)
            {
                _events.Add(new WorldEvent
                {
                    Seq = ++_eventSeq,
                    At = Core.TimeUtil.UnixNow(),
                    Kind = kind,
                    Text = text
                });

                while (_events.Count > 100) _events.RemoveAt(0);
            }
        }

        /// <summary>The most recent world events, oldest first.</summary>
        public WorldEvent[] RecentEvents(int max)
        {
            lock (_events)
            {
                int take = Math.Min(max, _events.Count);
                var result = new WorldEvent[take];
                _events.CopyTo(_events.Count - take, result, 0, take);
                return result;
            }
        }

        public long EventSeq
        {
            get { lock (_events) return _eventSeq; }
        }

        private void RaisePlayersChanged()
        {
            Action handler = PlayersChanged;
            if (handler != null)
            {
                try { handler(); }
                catch (Exception ex) { Log.Warning("World", "PlayersChanged handler failed: {0}", ex.Message); }
            }
        }

        // -- the tick ---------------------------------------------------------

        /// <summary>
        /// Queues an action to run at the start of the next tick, on the world thread.
        /// </summary>
        /// <remarks>
        /// Connection threads never touch world state directly. They hand a closure to
        /// this queue, and the tick loop executes it with exclusive access. That is the
        /// whole concurrency model, and it is why nothing in the game logic needs a lock.
        /// </remarks>
        public void Enqueue(Action action)
        {
            if (action == null) return;
            _inputs.Enqueue(action);
        }

        /// <summary>Number of actions waiting for the next tick.</summary>
        public int PendingInputs
        {
            get { return _inputs.Count; }
        }

        private void DrainInputs()
        {
            Action action;
            int budget = 4096;      // bound the work per tick so a flood cannot stall it

            while (budget-- > 0 && _inputs.TryDequeue(out action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Exception("World", ex, "Queued input failed");
                }
            }
        }

        /// <summary>
        /// Advances the world by one step: movement, combat, regeneration, NPC AI,
        /// ground item decay, then the outgoing update packets.
        /// </summary>
        public void Tick()
        {
            _tickCount++;
            DateTime started = DateTime.UtcNow;

            try
            {
                DrainInputs();
                TickPlayers();
                TickNpcs();
                TickGroundItems();

                Action<World> sender = UpdateSender;
                if (sender != null) sender(this);
            }
            catch (Exception ex)
            {
                // A fault in one tick must not kill the server; the supervisor will
                // only step in if this keeps happening.
                Log.Exception("World", ex, "Tick " + _tickCount + " failed");
            }

            _lastTickMs = (DateTime.UtcNow - started).TotalMilliseconds;
            _tickHistory[_tickHistoryPos] = _lastTickMs;
            _tickHistoryPos = (_tickHistoryPos + 1) % _tickHistory.Length;
            if (_tickHistoryCount < _tickHistory.Length) _tickHistoryCount++;
        }

        private void TickPlayers()
        {
            for (int i = 0; i < _players.Length; i++)
            {
                Player player = _players[i];
                if (player == null || !player.Online) continue;

                player.TicksSinceAction++;
                player.ClearUpdateFlags();

                if (player.IsDead)
                {
                    TickDeath(player);
                    continue;
                }

                TickRegeneration(player);
                TickMovement(player);
                TickCombat(player);

                // Level-up dialogue is queued by AddExperience.
                if (player.PendingLevelUps.Count > 0)
                {
                    foreach (int skill in player.PendingLevelUps)
                    {
                        SendLevelUp(player, skill);
                        AddEvent("levelup", player.Name + " reached level " + player.BaseLevel[skill] +
                                           " " + Skills.Names[skill]);
                    }
                    player.PendingLevelUps.Clear();
                }
            }
        }

        private void TickDeath(Player player)
        {
            player.DeathTicks++;

            if (player.DeathTicks == 1)
            {
                // Drop everything except the three most valuable items, which is the
                // classic behaviour players expect.
                DropInventoryOnDeath(player);
                player.Endpoint.SendMessage("Oh dear, you are dead!");
            }

            if (player.DeathTicks >= 3)
            {
                player.Position = new Position(_config.StartX, _config.StartY, _config.StartPlane);
                player.LastPosition = player.Position;
                player.Teleported = true;
                player.DeathTicks = 0;
                player.RestoreStats();
                player.WalkTo(new Position(player.Position.X, player.Position.Y, player.Position.Plane), Paths);
                player.Waypoints.Clear();
                player.CombatTargetNpc = -1;
                player.CombatTargetPlayer = -1;
                player.View.Clear();
            }
        }

        private void DropInventoryOnDeath(Player player)
        {
            // Keep the three most valuable stacks.
            var slots = new List<int>();
            for (int i = 0; i < player.Inventory.Size; i++)
            {
                if (!player.Inventory[i].IsEmpty) slots.Add(i);
            }

            slots.Sort((a, b) =>
            {
                ItemDefinition da = DefinitionLoader.Item(player.Inventory[a].Id);
                ItemDefinition db = DefinitionLoader.Item(player.Inventory[b].Id);
                int va = da == null ? 0 : da.Value * Math.Max(1, player.Inventory[a].Amount);
                int vb = db == null ? 0 : db.Value * Math.Max(1, player.Inventory[b].Amount);
                return vb.CompareTo(va);
            });

            for (int i = 3; i < slots.Count; i++)
            {
                ItemStack stack = player.Inventory[slots[i]];
                AddGroundItem(stack.Id, stack.Amount, player.Position, null, 0, 200);
                player.Inventory.Set(slots[i], -1, 0);
            }

            SendInventory(player);
        }

        private void TickRegeneration(Player player)
        {
            if (player.Hitpoints < player.MaxHitpoints)
            {
                // One hitpoint every 100 ticks (a minute at 600 ms), as in the original.
                if (_tickCount % 100 == 0) player.Hitpoints++;
            }

            if (player.RunEnergy < 10000 && _tickCount % 6 == 0) player.RunEnergy += 100;

            if (player.PoisonTicks > 0)
            {
                player.PoisonTicks--;
                if (player.PoisonTicks % 30 == 0)
                {
                    ApplyDamageToPlayer(player, player.PoisonDamage, 2);
                    player.PoisonDamage--;
                    if (player.PoisonDamage <= 0) player.PoisonTicks = 0;
                }
            }

            if (player.SkullTicks > 0) player.SkullTicks--;
        }

        private void TickMovement(Player player)
        {
            if (player.Waypoints.Count == 0) return;

            EnsureCollisionCovers(player);

            int steps = player.RunEnabled && player.RunEnergy > 0 ? 2 : 1;

            for (int step = 0; step < steps && player.Waypoints.Count > 0; step++)
            {
                Position next = player.Waypoints[0];

                // Every step is validated against the server's own collision map, even
                // though the client already pathfound. The client's map is a copy; a client
                // that lies about its path must not be able to walk through a wall, and this
                // is the only place that can be enforced.
                if (Collision != null && !Collision.CanStep(player.Position.X, player.Position.Y, next.X, next.Y))
                {
                    Log.Debug("World", "{0} tried to step into a blocked tile at {1}.", player.Name, next);
                    player.Waypoints.Clear();
                    break;
                }

                player.Waypoints.RemoveAt(0);

                int direction = Direction.Between(player.Position, next);
                if (direction < 0) continue;

                player.LastPosition = player.Position;
                player.Position = next;

                if (player.StepsThisTick == 0) player.FirstStepDirection = direction;
                else player.SecondStepDirection = direction;

                player.StepsThisTick++;
                player.MovedThisTick = true;

                if (player.RunEnabled)
                {
                    player.RunEnergy = Math.Max(0, player.RunEnergy - 100);
                }
            }

            if (player.MovedThisTick) player.LastActivityUtc = DateTime.UtcNow;
        }

        private void TickCombat(Player player)
        {
            if (player.AttackDelay > 0) player.AttackDelay--;

            if (player.CombatTargetNpc >= 0)
            {
                Npc target = NpcAt(player.CombatTargetNpc);
                if (target == null || target.IsDead)
                {
                    player.CombatTargetNpc = -1;
                    return;
                }

                if (!player.IsAdjacentTo(target.Position))
                {
                    // Walk towards the target rather than giving up immediately.
                    if (player.Waypoints.Count == 0) player.WalkTo(target.Position, Paths);
                    return;
                }

                player.FaceTile = target.Position;
                if (player.AttackDelay <= 0) Combat.PlayerAttacksNpc(this, player, target);
            }
            else if (player.CombatTargetPlayer >= 0)
            {
                Player target = PlayerAt(player.CombatTargetPlayer);
                if (target == null || !target.Online || target.IsDead)
                {
                    player.CombatTargetPlayer = -1;
                    return;
                }

                if (!player.IsAdjacentTo(target.Position))
                {
                    if (player.Waypoints.Count == 0) player.WalkTo(target.Position, Paths);
                    return;
                }

                player.FaceTile = target.Position;
                if (player.AttackDelay <= 0) Combat.PlayerAttacksPlayer(this, player, target);
            }
        }

        private void TickNpcs()
        {
            for (int i = 0; i < _npcs.Length; i++)
            {
                Npc npc = _npcs[i];
                if (npc == null) continue;

                npc.ClearUpdateFlags();

                if (npc.Dead)
                {
                    npc.TicksUntilRespawn--;
                    if (npc.TicksUntilRespawn <= 0) npc.Respawn();
                    continue;
                }

                if (npc.AttackDelay > 0) npc.AttackDelay--;

                if (npc.TargetPlayer >= 0)
                {
                    Player target = PlayerAt(npc.TargetPlayer);
                    if (target == null || !target.Online || target.IsDead)
                    {
                        npc.TargetPlayer = -1;
                    }
                    else if (npc.IsAdjacentTo(target.Position))
                    {
                        npc.FaceTile = target.Position;
                        if (npc.AttackDelay <= 0) Combat.NpcAttacksPlayer(this, npc, target);
                    }
                    else
                    {
                        StepNpcTowards(npc, target.Position);
                    }
                    continue;
                }

                // Idle wandering.
                if (npc.WanderCooldown > 0)
                {
                    npc.WanderCooldown--;
                    continue;
                }

                if (npc.Definition != null && npc.Definition.Aggressive)
                {
                    Player nearby = FindNearestPlayer(npc.Position, 6);
                    if (nearby != null && !nearby.IsDead)
                    {
                        npc.TargetPlayer = nearby.Index;
                        continue;
                    }
                }

                npc.WanderCooldown = _random.Next(8, 30);
                int dx = _random.Next(-1, 2);
                int dy = _random.Next(-1, 2);
                if (dx == 0 && dy == 0) continue;

                Position next = new Position(npc.Position.X + dx, npc.Position.Y + dy, npc.Position.Plane);
                if (next.ChebyshevDistance(npc.SpawnPosition) > 6) continue;

                int direction = Direction.Between(npc.Position, next);
                if (direction < 0) continue;

                npc.Position = next;
                npc.MovedThisTick = true;
                npc.FirstStepDirection = direction;
                npc.FaceDirection = direction;
            }
        }

        private void StepNpcTowards(Npc npc, Position target)
        {
            int dx = Math.Sign(target.X - npc.Position.X);
            int dy = Math.Sign(target.Y - npc.Position.Y);
            if (dx == 0 && dy == 0) return;

            Position next = new Position(npc.Position.X + dx, npc.Position.Y + dy, npc.Position.Plane);
            int direction = Direction.Between(npc.Position, next);
            if (direction < 0) return;

            npc.Position = next;
            npc.MovedThisTick = true;
            npc.FirstStepDirection = direction;
            npc.FaceDirection = direction;
        }

        /// <summary>Finds the closest live player within <paramref name="radius"/> tiles.</summary>
        public Player FindNearestPlayer(Position position, int radius)
        {
            Player best = null;
            int bestDistance = int.MaxValue;

            for (int i = 0; i < _players.Length; i++)
            {
                Player p = _players[i];
                if (p == null || !p.Online || p.IsDead) continue;

                int distance = p.Position.ChebyshevDistance(position);
                if (distance > radius) continue;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = p;
                }
            }
            return best;
        }

        private void TickGroundItems()
        {
            var expired = new List<KeyValuePair<int, WorldItem>>();

            lock (_groundLock)
            {
                foreach (KeyValuePair<int, List<WorldItem>> kv in _groundItems)
                {
                    for (int i = kv.Value.Count - 1; i >= 0; i--)
                    {
                        WorldItem item = kv.Value[i];
                        if (item.TicksRemaining < 0) continue;

                        item.TicksRemaining--;
                        if (item.TicksRemaining <= 0)
                        {
                            expired.Add(new KeyValuePair<int, WorldItem>(kv.Key, item));
                            kv.Value.RemoveAt(i);
                        }
                    }
                }
            }

            foreach (KeyValuePair<int, WorldItem> entry in expired)
            {
                Position position = Position.Unpack(entry.Key);
                BroadcastGroundItemRemove(position, entry.Value.ItemId);
            }
        }

        // -- damage -----------------------------------------------------------

        /// <summary>Applies damage to a player and queues the hit for the update block.</summary>
        public void ApplyDamageToPlayer(Player player, int damage, int type)
        {
            if (player == null || damage <= 0) return;

            player.Hitpoints -= damage;
            if (player.Hitpoints < 0) player.Hitpoints = 0;

            // Keep the skill array in step: the client renders the hitpoints skill from
            // CurrentLevel, and the death check reads it too.
            player.CurrentLevel[Skills.Hitpoints] = player.Hitpoints;

            player.DamageToShow = damage;
            player.DamageType = type;
            player.UpdateMask |= PlayerUpdateFlags.HitUpdate;

            if (player.Hitpoints <= 0)
            {
                player.DeathTicks = 0;
                AddEvent("death", player.Name + " died");
            }
        }

        /// <summary>Applies damage to an NPC and queues the hit for the update block.</summary>
        public void ApplyDamageToNpc(Npc npc, int damage, int type)
        {
            if (npc == null || npc.IsDead || damage <= 0) return;

            npc.Hitpoints -= damage;
            npc.DamageToShow = damage;
            npc.DamageType = type;
            npc.UpdateMask |= NpcUpdateFlags.HitUpdate;

            if (npc.Hitpoints <= 0)
            {
                npc.Hitpoints = 0;
                npc.Dead = true;
                npc.TicksUntilRespawn = npc.Definition == null ? 25 : npc.Definition.RespawnTicks;
                AddEvent("npc-death", npc.Name + " was defeated");
            }
        }

        // -- outgoing packets -------------------------------------------------

        /// <summary>Sends a chat-box message to one player.</summary>
        public void SendMessage(Player player, string text)
        {
            if (player == null || player.Endpoint == null) return;
            player.Endpoint.SendMessage(text);
        }

        /// <summary>Sends a system message to everyone online.</summary>
        public int BroadcastMessage(string text)
        {
            int delivered = 0;
            for (int i = 0; i < _players.Length; i++)
            {
                Player p = _players[i];
                if (p == null || !p.Online || p.Endpoint == null) continue;
                p.Endpoint.SendMessage(text);
                delivered++;
            }
            return delivered;
        }

        /// <summary>Sends the player's whole inventory to the client.</summary>
        public void SendInventory(Player player)
        {
            if (player == null || player.Endpoint == null) return;

            ItemContainer inventory = player.Inventory;
            player.Endpoint.Send(Protocol.OutSetItems, buffer =>
            {
                buffer.WriteShort(Protocol.OutSetItems);            // placeholder, fixed by writer
                buffer.WriteShort(inventory.InterfaceId);
                buffer.WriteShort(inventory.Size);
            });

            // The item payload needs a two-pass length, so it is built separately.
            SendContainerContents(player, inventory);
        }

        private void SendContainerContents(Player player, ItemContainer container)
        {
            if (player == null || player.Endpoint == null) return;

            var payload = new PacketBuffer(128);
            for (int i = 0; i < container.Size; i++)
            {
                ItemStack stack = container[i];
                if (stack.IsEmpty) continue;

                int amount = stack.Amount;
                payload.WriteSmart(i);
                if (amount > 254) payload.WriteByte(255);
                payload.WriteSmart(amount > 254 ? 0 : amount);
                payload.WriteShort(stack.Id + 1);
            }

            byte[] bytes = payload.ToArray();
            player.Endpoint.Send(Protocol.OutUpdateItem, buffer =>
            {
                buffer.WriteShort(container.InterfaceId);
                buffer.WriteShort(bytes.Length);
                buffer.WriteBytes(bytes);
            });
        }

        /// <summary>Sends the full skill block.</summary>
        public void SendSkills(Player player)
        {
            if (player == null || player.Endpoint == null) return;

            player.Endpoint.Send(Protocol.OutSetSkills, buffer =>
            {
                for (int i = 0; i < Skills.Count; i++)
                {
                    int level = player.CurrentLevel[i];
                    int baseLevel = player.BaseLevel[i];
                    int xp = player.Experience[i];

                    buffer.WriteByte(i == Skills.Hitpoints ? level : baseLevel);
                    buffer.WriteByteA(baseLevel);
                    buffer.WriteInt(xp);
                }
            });
        }

        /// <summary>Sends one skill's update, for cheap experience gains.</summary>
        public void SendSkillUpdate(Player player, int skill)
        {
            if (player == null || player.Endpoint == null) return;
            if (skill < 0 || skill >= Skills.Count) return;

            player.Endpoint.Send(Protocol.OutUpdateSkill, buffer =>
            {
                buffer.WriteByte(skill);
                buffer.WriteByteA(player.CurrentLevel[skill]);
                buffer.WriteByte(player.BaseLevel[skill]);
                buffer.WriteInt(player.Experience[skill]);
            });
        }

        private void SendLevelUp(Player player, int skill)
        {
            if (player == null || player.Endpoint == null) return;

            player.Endpoint.Send(Protocol.OutMessage, buffer =>
            {
                buffer.WriteString("Congratulations, you just advanced a " + Skills.Names[skill] + " level.");
                buffer.WriteByte(0);
            });

            player.Endpoint.Send(Protocol.OutMessage, buffer =>
            {
                buffer.WriteString("Your " + Skills.Names[skill] + " level is now " + player.BaseLevel[skill] + ".");
                buffer.WriteByte(0);
            });

            // The level-up dialogue interface, as in the original client.
            player.Endpoint.Send(Protocol.OutShowInterface, buffer => buffer.WriteShort(6248 + skill * 3));
            SendSkillUpdate(player, skill);
        }

        /// <summary>Sends a player's animation to everyone who can see them.</summary>
        public void PlayAnimation(Player player, int animationId, int delay = 0)
        {
            if (player == null) return;
            player.Animation = animationId;
            player.AnimationDelay = delay;
            player.UpdateMask |= PlayerUpdateFlags.Animation;
        }

        /// <summary>Sends a graphic to everyone who can see a player.</summary>
        public void PlayGraphic(Player player, int graphicId, int height = 0, int delay = 0)
        {
            if (player == null) return;
            player.Graphic = graphicId;
            player.GraphicHeight = height;
            player.GraphicDelay = delay;
            player.UpdateMask |= PlayerUpdateFlags.Graphic;
        }

        // -- ground item broadcasts -------------------------------------------

        private void BroadcastGroundItemAdd(Position position, int itemId, int amount)
        {
            for (int i = 0; i < _players.Length; i++)
            {
                Player p = _players[i];
                if (p == null || !p.Online || !p.RegionLoaded || p.Endpoint == null) continue;
                if (!p.Position.IsWithin(position, 15)) continue;

                p.Endpoint.Send(Protocol.OutGroundItemAdd, buffer =>
                {
                    buffer.WriteByte(0);
                    buffer.WriteShortA(itemId);
                    buffer.WriteShort(amount);
                    buffer.WriteByte(position.Plane);
                    buffer.WriteShortLE(position.X - (p.Position.X - 8));
                    buffer.WriteShortLE(position.Y - (p.Position.Y - 8));
                });
            }
        }

        private void BroadcastGroundItemRemove(Position position, int itemId)
        {
            for (int i = 0; i < _players.Length; i++)
            {
                Player p = _players[i];
                if (p == null || !p.Online || !p.RegionLoaded || p.Endpoint == null) continue;
                if (!p.Position.IsWithin(position, 15)) continue;

                p.Endpoint.Send(Protocol.OutGroundItemRemove, buffer =>
                {
                    buffer.WriteByteC(0);
                    buffer.WriteShortA(itemId);
                    buffer.WriteShortLE(position.X - (p.Position.X - 8));
                    buffer.WriteShortLE(position.Y - (p.Position.Y - 8));
                });
            }
        }

        private void BroadcastGroundItemAmount(Position position, int itemId, int amount)
        {
            for (int i = 0; i < _players.Length; i++)
            {
                Player p = _players[i];
                if (p == null || !p.Online || !p.RegionLoaded || p.Endpoint == null) continue;
                if (!p.Position.IsWithin(position, 15)) continue;

                p.Endpoint.Send(Protocol.OutGroundItemAmount, buffer =>
                {
                    buffer.WriteByte(0);
                    buffer.WriteShortA(itemId);
                    buffer.WriteShortLE(position.X - (p.Position.X - 8));
                    buffer.WriteShortLE(position.Y - (p.Position.Y - 8));
                    buffer.WriteShort(amount);
                });
            }
        }

        // -- interaction ------------------------------------------------------

        /// <summary>Attempts to pick an item up off the ground.</summary>
        public bool PickupGroundItem(Player player, Position position, int itemId)
        {
            if (player == null || player.IsDead) return false;
            if (!player.Position.IsWithin(position, 2)) return false;

            List<WorldItem> items = ItemsAt(position);
            WorldItem found = null;
            long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].ItemId != itemId) continue;
                if (items[i].OwnerName != null && items[i].OwnerName != player.NameKey && now < items[i].OwnerUntilMs) continue;
                found = items[i];
                break;
            }

            if (found == null) return false;

            if (player.Inventory.IsFull)
            {
                SendMessage(player, "You don't have enough inventory space.");
                return false;
            }

            int added = player.Inventory.Add(itemId, found.Amount);
            if (added <= 0) return false;

            RemoveGroundItem(position, itemId, added);
            SendInventory(player);
            SendMessage(player, "You pick up the " + Player.ItemName(itemId) + ".");
            return true;
        }

        /// <summary>Drops an item from the inventory onto the ground.</summary>
        public bool DropItem(Player player, int slot, int itemId, int amount)
        {
            if (player == null || player.IsDead) return false;
            if (slot < 0 || slot >= player.Inventory.Size) return false;

            ItemStack stack = player.Inventory[slot];
            if (stack.IsEmpty || stack.Id != itemId) return false;

            int dropAmount = amount <= 0 ? stack.Amount : Math.Min(amount, stack.Amount);
            if (!player.Inventory.RemoveFromSlot(slot, dropAmount)) return false;

            AddGroundItem(itemId, dropAmount, player.Position, player.NameKey, 60000, 200);
            SendInventory(player);
            return true;
        }

        /// <summary>Consumes one of an item from the inventory (eating, drinking).</summary>
        public bool ConsumeItem(Player player, int slot)
        {
            if (player == null || slot < 0 || slot >= player.Inventory.Size) return false;
            ItemStack stack = player.Inventory[slot];
            if (stack.IsEmpty) return false;

            if (!player.Inventory.RemoveFromSlot(slot, 1)) return false;
            SendInventory(player);
            return true;
        }
    }

    /// <summary>Player update block bits, matching the client's decoder.</summary>
    public static class PlayerUpdateFlags
    {
        public const int None = 0;
        public const int ForcedChat = 0x4;
        public const int Animation = 0x8;
        public const int Appearance = 0x10;
        public const int FaceCoordinate = 0x2;
        public const int HitUpdate = 0x20;
        public const int HitUpdate2 = 0x200;
        public const int Chat = 0x80;
        public const int InteractingEntity = 0x1;
        public const int Graphic = 0x100;
        public const int ForcedMovement = 0x400;
    }

    /// <summary>NPC update block bits, matching the client's decoder.</summary>
    public static class NpcUpdateFlags
    {
        public const int None = 0;
        public const int HitUpdate = 0x40;
        public const int Animation = 0x10;
        public const int FaceCoordinate = 0x8;
        public const int Chat = 0x80;
        public const int Graphic = 0x20;
        public const int InteractingEntity = 0x1;
        public const int TurnToDirection = 0x2;
        public const int Transform = 0x4;
    }
}
