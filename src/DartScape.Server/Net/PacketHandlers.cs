using System;
using DartScape.Core.Diagnostics;
using DartScape.Core.Protocol;
using DartScape.Game.Definitions;
using DartScape.Game;
using DartScape.Game.Commands;

namespace DartScape.Server.Net
{
    /// <summary>
    /// Turns inbound packets into world actions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Handlers run on the world tick thread, so they may mutate world state freely. They
    /// must stay fast: everything here runs between two ticks and delays the whole world.
    /// Anything expensive belongs in a queued job, not here.
    /// </para>
    /// <para>
    /// Every handler validates its own input. A client can send anything, and a handler
    /// that trusts a slot index or an NPC id is a crash waiting for a bored player.
    /// </para>
    /// </remarks>
    public static class PacketHandlers
    {
        /// <summary>Dispatches one packet.</summary>
        public static void Handle(World world, Player player, int opcode, PacketBuffer buffer)
        {
            switch (opcode)
            {
                case Protocol.InWalkMap:
                case Protocol.InWalkMinimap:
                    HandleWalk(world, player, buffer);
                    break;

                case Protocol.InChat:
                    HandleChat(world, player, buffer);
                    break;

                case Protocol.InCommand:
                    HandleCommand(world, player, buffer.ReadString());
                    break;

                case Protocol.InButton:
                    HandleButton(world, player, buffer);
                    break;

                case Protocol.InItemAction:
                case Protocol.InItemOption:
                    HandleItemAction(world, player, buffer);
                    break;

                case Protocol.InDropItem:
                    HandleDropItem(world, player, buffer);
                    break;

                case Protocol.InSwapItems:
                    HandleSwapItems(world, player, buffer);
                    break;

                case Protocol.InPickupItem:
                    HandlePickup(world, player, buffer);
                    break;

                case Protocol.InNpcAction:
                case Protocol.InNpcAction2:
                case Protocol.InNpcAction3:
                case Protocol.InNpcAction4:
                    HandleNpcAction(world, player, opcode, buffer);
                    break;

                case Protocol.InObjectAction:
                case Protocol.InObjectAction2:
                case Protocol.InObjectAction3:
                case Protocol.InObjectAction4:
                    HandleObjectAction(world, player, buffer);
                    break;

                case Protocol.InFollow:
                    HandleFollow(world, player, buffer);
                    break;

                case Protocol.InPlayerAction:
                case Protocol.InPlayerAction2:
                case Protocol.InPlayerAction3:
                case Protocol.InPlayerAction4:
                    HandlePlayerAction(world, player, opcode, buffer);
                    break;

                case Protocol.InItemOnItem:
                    HandleItemOnItem(world, player, buffer);
                    break;

                case Protocol.InRegionLoaded:
                case Protocol.InActivity:
                case Protocol.InIdle:
                case Protocol.InFocusChange:
                case Protocol.InMouseClick:
                case Protocol.InInterfaceClosed:
                    // Bookkeeping only; the tick loop already tracks activity.
                    break;

                default:
                    Log.Debug("Packets", "{0} sent unhandled {1} ({2} bytes).",
                        player.Name, Protocol.NameOf(opcode), buffer.Remaining);
                    break;
            }
        }

        // -- movement ---------------------------------------------------------

        /// <summary>
        /// Tells the client where the server actually thinks the player is.
        /// </summary>
        /// <remarks>
        /// The client walks itself, so a walk request the server refuses would otherwise leave
        /// the client walking alone and the two positions would never agree again. Forcing an
        /// absolute placement costs one update block and resynchronises them.
        /// </remarks>
        private static void ResyncClient(Player player)
        {
            player.StopWalking();
            player.Teleported = true;
        }

        /// <summary>
        /// Handles a walk request in the original's format: a path, not a destination.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Layout: <c>[len = 2n + 3][x: LEShortA][(dx,dy) pairs][y: LEShort][run: byte]</c>.
        /// The anchor is the first step and every delta is relative to it.
        /// </para>
        /// <para>
        /// The server does not trust the path. It re-paths from its own collision map to the
        /// final destination, so a modified client cannot walk through a wall by sending a path
        /// that crosses one. The client's path is only used to know <em>where</em> the player
        /// wants to go.
        /// </para>
        /// </remarks>
        private static void HandleWalk(World world, Player player, PacketBuffer buffer)
        {
            if (player.IsDead) return;

            // Layout: [len][x: LEShortA][(dx,dy) pairs][y: LEShort][run]
            // The trailing short is the ANCHOR's y, not the destination's: the original writes
            // the anchor x first, the deltas in between, and the anchor y last. Total payload is
            // 2n + 3 bytes for n waypoints.
            if (buffer.Remaining < 5)
            {
                Log.Debug("Packets", "{0} sent a truncated walk packet.", player.Name);
                ResyncClient(player);
                return;
            }

            int anchorX = buffer.ReadUnsignedShortLEA();

            int pairBytes = buffer.Remaining - 3;      // minus the y short and the run byte
            if (pairBytes < 0 || (pairBytes % 2) != 0)
            {
                Log.Debug("Packets", "{0} sent a malformed walk packet ({1} bytes).", player.Name, buffer.Remaining);
                ResyncClient(player);
                return;
            }

            int waypointCount = pairBytes / 2 + 1;
            if (waypointCount < 1 || waypointCount > PathFinder.MaxPathLength)
            {
                Log.Debug("Packets", "{0} sent {1} waypoints; refusing.", player.Name, waypointCount);
                ResyncClient(player);
                return;
            }

            int lastDeltaX = 0;
            int lastDeltaY = 0;

            for (int i = 1; i < waypointCount; i++)
            {
                lastDeltaX = buffer.ReadByte();
                lastDeltaY = buffer.ReadByte();
            }

            int anchorY = buffer.ReadUnsignedShortLE();
            int run = buffer.Remaining > 0 ? buffer.ReadUnsignedByteC() : 0;

            int destinationX = anchorX + lastDeltaX;
            int destinationY = anchorY + lastDeltaY;

            // A single request may not exceed the longest path the original allows. Without this
            // a modified client can ask to walk hundreds of tiles, which is a teleport with extra
            // steps.
            int dx = Math.Abs(destinationX - player.Position.X);
            int dy = Math.Abs(destinationY - player.Position.Y);

            if (Math.Max(dx, dy) > PathFinder.MaxPathLength)
            {
                Log.Debug("Packets", "{0} asked to walk {1} tiles; clamped.", player.Name, Math.Max(dx, dy));
                ResyncClient(player);
                return;
            }

            player.RunEnabled = run == 1;

            // The server re-paths from its own collision map. The client's path only says where
            // the player wants to end up; it never decides how they get there.
            player.WalkTo(new Position(destinationX, destinationY, player.Position.Plane), world.Paths);

            player.CombatTargetNpc = -1;
            player.CombatTargetPlayer = -1;
            player.TicksSinceAction = 0;
        }

        // -- chat -------------------------------------------------------------

        private static void HandleChat(World world, Player player, PacketBuffer buffer)
        {
            long now = Core.TimeUtil.UnixNowMs();

            // 600 ms between messages, which is the tick length: one message per tick.
            if (now - player.LastChatMs < world.Config.TickMs)
            {
                return;
            }

            int effects = buffer.ReadUnsignedByte();
            string message = buffer.ReadString();

            if (string.IsNullOrEmpty(message)) return;

            message = Sanitise(message, world.Config.MaxChatLength);
            if (message.Length == 0) return;

            player.LastChatMs = now;
            player.ChatText = message;
            player.ChatColour = (effects >> 8) & 0xFF;
            player.ChatEffect = effects & 0xFF;
            player.ChatRights = player.Privilege;
            player.UpdateMask |= PlayerUpdateFlags.Chat;

            // A leading '~' forces the message into the chat box for the sender.
            if (message.StartsWith("~", StringComparison.Ordinal))
            {
                world.SendMessage(player, message.Substring(1));
            }

            Log.Debug("Chat", "{0}: {1}", player.Name, message);
        }

        private static void HandleCommand(World world, Player player, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            long now = Core.TimeUtil.UnixNowMs();
            if (now - player.LastCommandMs < 500) return;
            player.LastCommandMs = now;

            CommandDispatcher.Execute(world, player, text);
        }

        /// <summary>Strips control characters and truncates, without mangling unicode.</summary>
        private static string Sanitise(string text, int maxLength)
        {
            var sb = new System.Text.StringBuilder(Math.Min(text.Length, maxLength));
            foreach (char c in text)
            {
                if (sb.Length >= maxLength) break;
                if (c < 32 || c == 127) continue;          // control characters
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        // -- interface --------------------------------------------------------

        private static void HandleButton(World world, Player player, PacketBuffer buffer)
        {
            int buttonId = buffer.ReadUnsignedShort();

            // The bank and logout buttons are the only ones that matter yet.
            if (buttonId == 5385 || buttonId == 5387)
            {
                return;
            }

            if (buttonId == 9154)
            {
                world.SendMessage(player, "Logging out...");
                if (player.Endpoint != null) player.Endpoint.Disconnect("logout button");
                return;
            }

            Log.Debug("Packets", "{0} clicked button {1}.", player.Name, buttonId);
        }

        // -- items ------------------------------------------------------------

        private static void HandleItemAction(World world, Player player, PacketBuffer buffer)
        {
            int slot = buffer.ReadUnsignedShortA();
            int itemId = buffer.ReadUnsignedShort();
            int interfaceId = buffer.ReadUnsignedShortA();

            if (slot < 0 || slot >= player.Inventory.Size) return;
            if (interfaceId != Player.InventoryInterface) return;

            ItemStack stack = player.Inventory[slot];
            if (stack.IsEmpty || stack.Id != itemId) return;

            ItemDefinition def = DefinitionLoader.Item(itemId);

            // Remember the selection so "use item with item" works.
            if (player.SelectedItemSlot >= 0 && player.SelectedItemSlot != slot)
            {
                TryCombine(world, player, player.SelectedItemSlot, slot);
                player.SelectedItemSlot = -1;
                return;
            }

            player.SelectedItemSlot = slot;
            player.SelectedItemId = itemId;

            // Eating is the one action that needs no interface.
            if (def != null && def.Name != null && IsFood(def.Name))
            {
                Eat(world, player, slot, def);
                return;
            }

            world.SendMessage(player, "You examine the " + (def == null ? "item" : def.Name) + ".");
        }

        private static bool IsFood(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("shrimp") || n.Contains("bread") || n.Contains("meat") ||
                   n.Contains("trout") || n.Contains("salmon") || n.Contains("lobster") ||
                   n.Contains("swordfish") || n.Contains("tuna") || n.Contains("cake") ||
                   n.Contains("banana") || n.Contains("apple");
        }

        private static void Eat(World world, Player player, int slot, ItemDefinition def)
        {
            if (player.Hitpoints >= player.MaxHitpoints)
            {
                world.SendMessage(player, "You are not hungry.");
                return;
            }

            int heal = 4;
            string n = def.Name.ToLowerInvariant();
            if (n.Contains("lobster")) heal = 12;
            else if (n.Contains("swordfish")) heal = 14;
            else if (n.Contains("salmon")) heal = 9;
            else if (n.Contains("trout")) heal = 7;
            else if (n.Contains("tuna")) heal = 10;
            else if (n.Contains("bread")) heal = 5;

            if (!world.ConsumeItem(player, slot)) return;

            player.Hitpoints = Math.Min(player.MaxHitpoints, player.Hitpoints + heal);
            world.PlayAnimation(player, 829);
            world.SendMessage(player, "You eat the " + def.Name + ".");
        }

        private static void HandleDropItem(World world, Player player, PacketBuffer buffer)
        {
            int itemId = buffer.ReadUnsignedShortA();
            int slot = buffer.ReadUnsignedShortA();

            if (slot < 0 || slot >= player.Inventory.Size) return;

            ItemStack stack = player.Inventory[slot];
            if (stack.IsEmpty || stack.Id != itemId) return;

            world.DropItem(player, slot, itemId, stack.Amount);
        }

        private static void HandleSwapItems(World world, Player player, PacketBuffer buffer)
        {
            int from = buffer.ReadUnsignedShortA();
            int to = buffer.ReadUnsignedShortA();

            if (from < 0 || from >= player.Inventory.Size) return;
            if (to < 0 || to >= player.Inventory.Size) return;

            if (!player.Inventory.Swap(from, to)) return;

            world.SendInventory(player);
        }

        private static void HandlePickup(World world, Player player, PacketBuffer buffer)
        {
            int itemId = buffer.ReadUnsignedShort();
            int y = buffer.ReadUnsignedShortA();
            int x = buffer.ReadUnsignedShortA();

            Position target = new Position(x, y, player.Position.Plane);

            // Picking something up means standing on its tile, so a click from a distance has
            // to walk first. Queueing the pickup rather than refusing it is what makes a single
            // click work, which is how the game behaves.
            if (!player.Position.Equals(target))
            {
                player.ClearPending();
                player.Pending = new PendingAction
                {
                    Kind = PendingActionKind.GroundItem,
                    ItemId = itemId,
                    Target = target
                };

                player.WalkTo(target, world.Paths);
                return;
            }

            world.PickupGroundItem(player, target, itemId);
        }

        private static void HandleItemOnItem(World world, Player player, PacketBuffer buffer)
        {
            int usedSlot = buffer.ReadUnsignedShortA();
            int usedItem = buffer.ReadUnsignedShort();
            int targetSlot = buffer.ReadUnsignedShortA();
            int targetItem = buffer.ReadUnsignedShort();

            if (usedSlot < 0 || usedSlot >= player.Inventory.Size) return;
            if (targetSlot < 0 || targetSlot >= player.Inventory.Size) return;

            if (player.Inventory[usedSlot].Id != usedItem) return;
            if (player.Inventory[targetSlot].Id != targetItem) return;

            TryCombine(world, player, usedSlot, targetSlot);
        }

        /// <summary>
        /// A deliberately tiny "use item on item" table. Real content lives in data
        /// files; this covers the cases a new player will actually try.
        /// </summary>
        private static void TryCombine(World world, Player player, int slotA, int slotB)
        {
            int a = player.Inventory[slotA].Id;
            int b = player.Inventory[slotB].Id;

            // Knife on logs -> arrow shafts.
            if (Matches(a, b, 946, 1511))
            {
                ConsumeBoth(world, player, slotA, slotB);
                player.Inventory.Add(52, 15);
                world.SendInventory(player);
                world.SendMessage(player, "You whittle the logs into arrow shafts.");
                player.AddExperience(Skills.Fletching, 5, world.Config.ExperienceRate);
                world.SendSkillUpdate(player, Skills.Fletching);
                return;
            }

            // Tinderbox on logs -> fire (no firemaking object yet, so it just burns).
            if (Matches(a, b, 590, 1511))
            {
                ConsumeBoth(world, player, slotA, slotB);
                player.Inventory.Add(1511, 0);
                world.SendInventory(player);
                world.PlayAnimation(player, 733);
                world.SendMessage(player, "The logs catch fire.");
                player.AddExperience(Skills.Firemaking, 40, world.Config.ExperienceRate);
                world.SendSkillUpdate(player, Skills.Firemaking);
                return;
            }

            world.SendMessage(player, "Nothing interesting happens.");
        }

        private static bool Matches(int a, int b, int first, int second)
        {
            return (a == first && b == second) || (a == second && b == first);
        }

        private static void ConsumeBoth(World world, Player player, int slotA, int slotB)
        {
            player.Inventory.RemoveFromSlot(slotA, 1);
            player.Inventory.RemoveFromSlot(slotB, 1);
        }

        // -- entities ---------------------------------------------------------

        private static void HandleNpcAction(World world, Player player, int opcode, PacketBuffer buffer)
        {
            int index = buffer.ReadUnsignedShort();
            Npc npc = world.NpcAt(index);

            if (npc == null || npc.IsDead) return;
            if (!player.Position.IsWithin(npc.Position, 12)) return;

            // The wire opcodes are not in option order, and they are not one-based either.
            // Mapping them to the cache's zero-based action array is the whole job here.
            int option = opcode == Protocol.InNpcAction ? 0
                : opcode == Protocol.InNpcAction2 ? 1
                : opcode == Protocol.InNpcAction3 ? 2
                : 3;

            string action = npc.Definition != null && npc.Definition.Actions != null
                            && option < npc.Definition.Actions.Length
                ? npc.Definition.Actions[option]
                : null;

            // Nothing queued survives a new click: the newest instruction wins.
            player.ClearPending();
            player.CombatTargetPlayer = -1;

            // Out of reach: walk there first, and remember what to do on arrival. The original
            // does this from the client; doing it on the server means a modified client cannot
            // skip the walk.
            if (!NpcActions.IsInReach(player, npc))
            {
                player.Pending = new PendingAction
                {
                    Kind = PendingActionKind.Npc,
                    NpcIndex = npc.Index,
                    Option = option,
                    Target = npc.Position
                };

                player.WalkTo(npc.Position, world.Paths);
                player.CombatTargetNpc = -1;
                return;
            }

            NpcActions.Run(world, player, npc, option);
        }

        private static void HandleObjectAction(World world, Player player, PacketBuffer buffer)
        {
            int x = buffer.ReadUnsignedShortA();
            int y = buffer.ReadUnsignedShort();
            int objectId = buffer.ReadUnsignedShort();

            Log.Debug("Packets", "{0} clicked object {1} at {2},{3}.", player.Name, objectId, x, y);

            // Object definitions come from the map data, which this cache revision does
            // not expose in a form the server reads yet. Acknowledge instead of ignoring
            // so the client does not sit waiting.
            world.SendMessage(player, "Nothing interesting happens.");
        }

        /// <summary>
        /// Handles a click on another player.
        /// </summary>
        /// <remarks>
        /// There are no player options in the cache, so option zero is always "Attack". The
        /// same walk-then-act rule applies as for NPCs: a click from out of range walks you in
        /// and then starts the fight.
        /// </remarks>
        private static void HandlePlayerAction(World world, Player player, int opcode, PacketBuffer buffer)
        {
            int index = buffer.ReadUnsignedShort();
            Player other = world.PlayerAt(index);

            if (other == null || other == player || other.IsDead) return;
            if (!player.Position.IsWithin(other.Position, 12)) return;

            player.ClearPending();
            player.CombatTargetNpc = -1;
            player.CombatTargetPlayer = index;
            player.FaceTile = other.Position;
        }

        private static void HandleFollow(World world, Player player, PacketBuffer buffer)
        {
            int index = buffer.ReadUnsignedShort();

            if (index >= 0 && index < World.MaxPlayers && world.PlayerAt(index) != null)
            {
                Player target = world.PlayerAt(index);
                if (target != null && target.Online)
                {
                    player.WalkTo(target.Position, world.Paths);
                    return;
                }
            }

            Npc npc = world.NpcAt(index);
            if (npc != null) player.WalkTo(npc.Position, world.Paths);
        }
    }
}
