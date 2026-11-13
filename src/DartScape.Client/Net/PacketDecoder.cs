using System;
using System.Collections.Generic;
using DartScape.Core.Diagnostics;
using DartScape.Core.Protocol;

namespace DartScape.Client.Net
{
    /// <summary>
    /// Applies inbound packets to the local world model.
    /// </summary>
    /// <remarks>
    /// Every reader here mirrors a specific writer in the server's
    /// <c>UpdateEncoder</c>. When one side changes, the other must change with it вЂ” that
    /// pairing is the reason both are small, explicit and commented rather than clever.
    /// </remarks>
    public static class PacketDecoder
    {
        // Outbound opcodes, matching the server.
        private const int OutPlayerUpdate = 81;
        private const int OutNpcUpdate = 65;
        private const int OutMessage = 253;
        private const int OutSetItems = 53;
        private const int OutUpdateItem = 90;
        private const int OutSetSkills = 134;
        private const int OutUpdateSkill = 49;
        private const int OutPlayerPosition = 85;
        private const int OutGroundItemAdd = 44;
        private const int OutGroundItemRemove = 156;
        private const int OutGroundItemAmount = 84;
        private const int OutShowInterface = 97;
        private const int OutCloseInterface = 219;
        private const int OutAnimation = 45;

        /// <summary>Local player indices the client is tracking, in the server's order.</summary>
        private static readonly List<int> LocalPlayers = new List<int>(64);

        /// <summary>Local NPC indices the client is tracking.</summary>
        private static readonly List<int> LocalNpcs = new List<int>(128);

        public static void Handle(ClientWorld world, int opcode, PacketBuffer buffer)
        {
            switch (opcode)
            {
                case OutPlayerUpdate: DecodePlayerUpdate(world, buffer); break;
                case OutNpcUpdate: DecodeNpcUpdate(world, buffer); break;
                case OutMessage: DecodeMessage(world, buffer); break;
                case OutSetItems: break;                       // header only, payload follows
                case OutUpdateItem: DecodeContainer(world, buffer); break;
                case OutSetSkills: DecodeSkills(world, buffer); break;
                case OutUpdateSkill: DecodeSkill(world, buffer); break;
                case OutPlayerPosition: DecodePosition(world, buffer); break;
                case OutGroundItemAdd: DecodeGroundItemAdd(world, buffer); break;
                case OutGroundItemRemove: DecodeGroundItemRemove(world, buffer); break;
                case OutGroundItemAmount: DecodeGroundItemAmount(world, buffer); break;
                case OutShowInterface: world.OpenInterface = buffer.ReadUnsignedShort(); break;
                case OutCloseInterface: world.OpenInterface = -1; break;
                case OutAnimation: break;
                default:
                    Log.Debug("Client", "Unhandled opcode {0} ({1} bytes).", opcode, buffer.Remaining);
                    break;
            }
        }

        /// <summary>Resets the tracked entity lists, on (re)connect.</summary>
        public static void Reset()
        {
            LocalPlayers.Clear();
            LocalNpcs.Clear();
        }

        // -- simple packets ---------------------------------------------------

        private static void DecodeMessage(ClientWorld world, PacketBuffer buffer)
        {
            string text = buffer.ReadString();
            int type = buffer.Remaining > 0 ? buffer.ReadUnsignedByte() : 0;
            world.AddChatMessage(text, type);
        }

        private static void DecodeSkills(ClientWorld world, PacketBuffer buffer)
        {
            for (int i = 0; i < 21; i++)
            {
                int current = buffer.ReadUnsignedByte();
                int baseLevel = buffer.ReadUnsignedByteA();
                int experience = buffer.ReadInt();

                world.CurrentLevel[i] = current;
                world.BaseLevel[i] = baseLevel;
                world.Experience[i] = experience;
            }
        }

        private static void DecodeSkill(ClientWorld world, PacketBuffer buffer)
        {
            int skill = buffer.ReadUnsignedByte();
            if (skill < 0 || skill >= 21) return;

            int current = buffer.ReadUnsignedByteA();
            int baseLevel = buffer.ReadUnsignedByte();
            int experience = buffer.ReadInt();

            world.CurrentLevel[skill] = current;
            world.BaseLevel[skill] = baseLevel;
            world.Experience[skill] = experience;
        }

        private static void DecodePosition(ClientWorld world, PacketBuffer buffer)
        {
            int index = buffer.ReadUnsignedShort();
            int plane = buffer.ReadUnsignedByte();
            int x = buffer.ReadUnsignedShortLE();
            int y = buffer.ReadUnsignedShortLE();
            string name = buffer.ReadString();
            int privilege = buffer.ReadUnsignedByte();

            world.MyIndex = index;
            world.MyPrivilege = privilege;

            // The local player is an entry in the same list as everyone else, so that
            // movement and appearance decoding work identically for it.
            ClientPlayer me = world.Players[index];
            if (me == null)
            {
                me = new ClientPlayer { Index = index };
                world.Players[index] = me;
            }

            me.Name = name;
            me.Privilege = privilege;
            me.IsLocal = true;

            // The initial position packet is authoritative: it is the only place the server says
            // where the player is without a movement step. Forcing it also seeds the shadow
            // position that later reconciliations compare against.
            world.ForceLocalPosition(x, y, plane);
        }

        private static void DecodeContainer(ClientWorld world, PacketBuffer buffer)
        {
            int interfaceId = buffer.ReadUnsignedShort();
            int length = buffer.ReadUnsignedShort();

            for (int i = 0; i < world.Inventory.Length; i++) world.Inventory[i] = new ClientWorld.ItemSlot();

            var payload = new PacketBuffer(buffer.ReadBytes(Math.Min(length, buffer.Remaining)));

            while (payload.Remaining > 0)
            {
                int slot = payload.ReadSmart();
                int amount = payload.ReadSmart();
                if (amount == 0) amount = 255;
                if (payload.Remaining < 2) break;
                int itemId = payload.ReadUnsignedShort() - 1;

                if (slot >= 0 && slot < world.Inventory.Length)
                {
                    world.Inventory[slot] = new ClientWorld.ItemSlot { Id = itemId, Amount = amount };
                }
            }
        }

        private static void DecodeGroundItemAdd(ClientWorld world, PacketBuffer buffer)
        {
            buffer.ReadUnsignedByte();
            int itemId = buffer.ReadUnsignedShortA();
            int amount = buffer.ReadUnsignedShort();
            int plane = buffer.ReadUnsignedByte();
            int localX = buffer.ReadUnsignedShortLE();
            int localY = buffer.ReadUnsignedShortLE();

            int x = world.MyX - 8 + localX;
            int y = world.MyY - 8 + localY;

            var item = new ClientGroundItem { ItemId = itemId, Amount = amount, X = x, Y = y, Plane = plane };
            world.GroundItems.Add(item);
        }

        private static void DecodeGroundItemRemove(ClientWorld world, PacketBuffer buffer)
        {
            buffer.ReadUnsignedByteC();
            int itemId = buffer.ReadUnsignedShortA();
            int localX = buffer.ReadUnsignedShortLE();
            int localY = buffer.ReadUnsignedShortLE();

            int x = world.MyX - 8 + localX;
            int y = world.MyY - 8 + localY;

            for (int i = world.GroundItems.Count - 1; i >= 0; i--)
            {
                ClientGroundItem item = world.GroundItems[i];
                if (item.ItemId == itemId && item.X == x && item.Y == y)
                {
                    world.GroundItems.RemoveAt(i);
                    return;
                }
            }
        }

        private static void DecodeGroundItemAmount(ClientWorld world, PacketBuffer buffer)
        {
            buffer.ReadUnsignedByte();
            int itemId = buffer.ReadUnsignedShortA();
            int localX = buffer.ReadUnsignedShortLE();
            int localY = buffer.ReadUnsignedShortLE();
            int amount = buffer.ReadUnsignedShort();

            int x = world.MyX - 8 + localX;
            int y = world.MyY - 8 + localY;

            for (int i = 0; i < world.GroundItems.Count; i++)
            {
                ClientGroundItem item = world.GroundItems[i];
                if (item.ItemId == itemId && item.X == x && item.Y == y)
                {
                    item.Amount = amount;
                    return;
                }
            }
        }

        // -- player update ----------------------------------------------------

        private static void DecodePlayerUpdate(ClientWorld world, PacketBuffer buffer)
        {
            var observed = new List<int>(16);

            buffer.StartBitRead();

            // Local player movement.
            bool selfMoved = buffer.ReadBits(1) == 1;

            if (!selfMoved)
            {
                // The server is standing still. That is the moment its position is final, so it
                // is also the moment a disagreement is safe to correct.
                world.NoteServerStep(0, 0);
            }
            else
            {
                int type = buffer.ReadBits(2);

                if (type == 0)
                {
                    world.NoteServerStep(0, 0);
                    observed.Add(world.MyIndex);
                }
                else if (type == 1)
                {
                    NoteServerMove(world, buffer.ReadBits(3));
                    if (buffer.ReadBits(1) == 1) observed.Add(world.MyIndex);
                }
                else if (type == 2)
                {
                    NoteServerMove(world, buffer.ReadBits(3));
                    NoteServerMove(world, buffer.ReadBits(3));
                    if (buffer.ReadBits(1) == 1) observed.Add(world.MyIndex);
                }
                else
                {
                    int plane = buffer.ReadBits(2);
                    buffer.ReadBits(1);                       // clear waypoint queue
                    bool block = buffer.ReadBits(1) == 1;
                    int y = buffer.ReadBits(7);
                    int x = buffer.ReadBits(7);

                    // Type 3 is the server telling us we were moved absolutely: a teleport, a
                    // respawn, or a correction it decided on. That is authoritative, so the
                    // local prediction is discarded rather than reconciled.
                    world.ForceLocalPosition(x, y, plane);
                    if (block) observed.Add(world.MyIndex);
                }
            }

            // Other players already known.
            int count = buffer.ReadBits(8);
            for (int i = 0; i < count; i++)
            {
                int index = LocalPlayers[i];
                ClientPlayer player = world.Players[index];

                if (buffer.ReadBits(1) == 0) continue;

                int type = buffer.ReadBits(2);

                if (type == 0)
                {
                    observed.Add(index);
                }
                else if (type == 1)
                {
                    Move(player, buffer.ReadBits(3));
                    if (buffer.ReadBits(1) == 1) observed.Add(index);
                }
                else if (type == 2)
                {
                    Move(player, buffer.ReadBits(3));
                    Move(player, buffer.ReadBits(3));
                    if (buffer.ReadBits(1) == 1) observed.Add(index);
                }
            }

            // Newly visible players.
            while (true)
            {
                int index = buffer.ReadBits(11);
                if (index == 2047) break;

                ClientPlayer player = world.Players[index];
                if (player == null)
                {
                    player = new ClientPlayer { Index = index };
                    world.Players[index] = player;
                }

                LocalPlayers.Add(index);

                if (buffer.ReadBits(1) == 1) observed.Add(index);
                buffer.ReadBits(1);                            // teleport flag

                int dy = buffer.ReadBits(5);
                if (dy > 15) dy -= 32;
                int dx = buffer.ReadBits(5);
                if (dx > 15) dx -= 32;

                player.X = world.MyX + dx;
                player.Y = world.MyY + dy;
                player.LastX = player.X;
                player.LastY = player.Y;
                player.Plane = world.MyPlane;
            }

            buffer.FinishBitRead();

            for (int i = 0; i < observed.Count; i++)
            {
                int index = observed[i];
                ClientPlayer player = index == world.MyIndex ? world.Me : world.Players[index];
                if (player == null) continue;
                DecodePlayerBlock(player, buffer);
            }
        }

        /// <summary>
        /// Feeds one of the server's movement bits for the local player into the shadow
        /// position.
        /// </summary>
        /// <remarks>
        /// The direction comes off the wire and is turned into a delta here rather than in the
        /// world, so the world does not need to know the wire's direction numbering.
        /// </remarks>
        private static void NoteServerMove(ClientWorld world, int direction)
        {
            world.NoteServerStep(DirectionDeltaX(direction), DirectionDeltaY(direction));
        }

        private static void Move(ClientPlayer player, int direction)
        {
            if (player == null) return;

            player.LastX = player.X;
            player.LastY = player.Y;
            player.X += DirectionDeltaX(direction);
            player.Y += DirectionDeltaY(direction);

            // Restart this entity's interpolation from now, so it slides over the next tick
            // rather than snapping to the new tile.
            player.MoveStartedMs = DartScape.Core.TimeUtil.MonotonicMs();
        }

        /// <summary>Direction 0 = north, clockwise, matching the 317 encoding.</summary>
        private static int DirectionDeltaX(int direction)
        {
            switch (direction & 7)
            {
                case 1: case 2: case 3: return 1;
                case 5: case 6: case 7: return -1;
                default: return 0;
            }
        }

        private static int DirectionDeltaY(int direction)
        {
            switch (direction & 7)
            {
                case 0: case 1: case 7: return 1;
                case 3: case 4: case 5: return -1;
                default: return 0;
            }
        }

        private static void DecodePlayerBlock(ClientPlayer player, PacketBuffer buffer)
        {
            int mask = buffer.ReadUnsignedByte();
            if ((mask & 0x40) != 0) mask |= buffer.ReadUnsignedByte() << 8;

            if ((mask & 0x400) != 0)
            {
                buffer.ReadUnsignedByteS();                    // start x
                buffer.ReadUnsignedByteS();                    // start y
                int endX = buffer.ReadUnsignedByteS();
                int endY = buffer.ReadUnsignedByteS();
                buffer.ReadUnsignedShortA();                   // start tick
                buffer.ReadUnsignedShortA();                   // end tick
                buffer.ReadUnsignedByteS();                    // direction

                player.LastX = player.X;
                player.LastY = player.Y;
                player.X = endX;
                player.Y = endY;
            }

            if ((mask & 0x100) != 0)
            {
                player.Graphic = buffer.ReadUnsignedShort();
                buffer.ReadInt();
            }

            if ((mask & 0x8) != 0)
            {
                player.Animation = buffer.ReadUnsignedShort();
                buffer.ReadUnsignedByteC();
            }

            if ((mask & 0x4) != 0)
            {
                player.OverheadText = buffer.ReadString();
                player.OverheadUntilMs = DartScape.Core.TimeUtil.MonotonicMs() + 6000;
            }

            if ((mask & 0x80) != 0)
            {
                int colourAndEffect = buffer.ReadUnsignedShort();
                int rights = buffer.ReadUnsignedByte();
                int length = buffer.ReadUnsignedByteC();
                byte[] text = buffer.ReadBytes(Math.Min(length, buffer.Remaining));

                string message = TextEncoding.GetString(text, 0, text.Length);
                player.OverheadText = message;
                player.OverheadUntilMs = DartScape.Core.TimeUtil.MonotonicMs() + 6000;
                player.Privilege = rights;

                if (!string.IsNullOrEmpty(message))
                {
                    world_addChat(player.Name, message, rights);
                }
            }

            if ((mask & 0x1) != 0)
            {
                int target = buffer.ReadUnsignedShort();
                if (target == 0xFFFF) target = -1;
            }

            if ((mask & 0x10) != 0)
            {
                int size = buffer.ReadUnsignedByteC();
                byte[] appearance = buffer.ReadBytes(Math.Min(size, buffer.Remaining));
                ApplyAppearance(player, appearance);
            }

            if ((mask & 0x2) != 0)
            {
                buffer.ReadUnsignedShortA();
                buffer.ReadUnsignedShort();
            }

            if ((mask & 0x20) != 0)
            {
                player.DamageToShow = buffer.ReadUnsignedByte();
                buffer.ReadUnsignedByteA();
                player.Hitpoints = buffer.ReadUnsignedByteC();
                player.MaxHitpoints = buffer.ReadUnsignedByte();
            }
            else if ((mask & 0x200) != 0)
            {
                player.DamageToShow = buffer.ReadUnsignedByte();
                buffer.ReadUnsignedByteS();
                player.Hitpoints = buffer.ReadUnsignedByte();
                player.MaxHitpoints = buffer.ReadUnsignedByteC();
            }
        }

        /// <summary>Chat lines for other players are pushed through the same box as system messages.</summary>
        private static Action<string, string, int> _chatSink;

        /// <summary>Installs the callback used for other players' public chat.</summary>
        public static void SetChatSink(Action<string, string, int> sink)
        {
            _chatSink = sink;
        }

        private static void world_addChat(string name, string message, int rights)
        {
            Action<string, string, int> sink = _chatSink;
            if (sink != null)
            {
                try { sink(name, message, rights); } catch (Exception) { }
            }
        }

        private static void ApplyAppearance(ClientPlayer player, byte[] data)
        {
            if (data == null || data.Length < 3) return;

            var buffer = new PacketBuffer(data);
            player.Gender = buffer.ReadUnsignedByte();
            player.Skulled = buffer.ReadUnsignedByte() == 1;
            player.HeadIcon = buffer.ReadUnsignedByte();

            for (int i = 0; i < 12; i++) player.EquipmentIds[i] = Math.Max(0, buffer.ReadUnsignedShort() - 1);
            for (int i = 0; i < 5; i++) player.Colours[i] = buffer.ReadUnsignedByte();
            for (int i = 0; i < 7; i++) player.BodyParts[i] = buffer.ReadUnsignedShort();

            // Combat level is optional: an older server does not send it, and a missing
            // tail must not be read as zero damage or a truncated packet.
            if (buffer.Remaining >= 2) player.CombatLevel = buffer.ReadUnsignedShort();

            player.AppearanceReceived = true;
        }

        // -- npc update -------------------------------------------------------

        private static void DecodeNpcUpdate(ClientWorld world, PacketBuffer buffer)
        {
            var observed = new List<int>(16);

            buffer.StartBitRead();

            int count = buffer.ReadBits(8);
            for (int i = 0; i < count; i++)
            {
                int index = LocalNpcs[i];
                ClientNpc npc = world.Npcs[index];

                if (buffer.ReadBits(1) == 0) continue;

                int type = buffer.ReadBits(2);

                if (type == 0)
                {
                    observed.Add(index);
                }
                else if (type == 1)
                {
                    MoveNpc(npc, buffer.ReadBits(3));
                    if (buffer.ReadBits(1) == 1) observed.Add(index);
                }
                else if (type == 2)
                {
                    MoveNpc(npc, buffer.ReadBits(3));
                    MoveNpc(npc, buffer.ReadBits(3));
                    if (buffer.ReadBits(1) == 1) observed.Add(index);
                }
            }

            while (true)
            {
                int index = buffer.ReadBits(14);
                if (index == 16383) break;

                ClientNpc npc = world.Npcs[index];
                if (npc == null)
                {
                    npc = new ClientNpc { Index = index };
                    world.Npcs[index] = npc;
                }

                LocalNpcs.Add(index);

                int dy = buffer.ReadBits(5);
                if (dy > 15) dy -= 32;
                int dx = buffer.ReadBits(5);
                if (dx > 15) dx -= 32;

                buffer.ReadBits(1);                            // clear waypoint queue
                npc.DefinitionId = buffer.ReadBits(12);

                if (buffer.ReadBits(1) == 1) observed.Add(index);

                npc.X = world.MyX + dx;
                npc.Y = world.MyY + dy;
                npc.LastX = npc.X;
                npc.LastY = npc.Y;
                npc.Plane = world.MyPlane;
            }

            buffer.FinishBitRead();

            for (int i = 0; i < observed.Count; i++)
            {
                ClientNpc npc = world.Npcs[observed[i]];
                if (npc == null) continue;
                DecodeNpcBlock(npc, buffer);
            }
        }

        private static void MoveNpc(ClientNpc npc, int direction)
        {
            if (npc == null) return;

            npc.LastX = npc.X;
            npc.LastY = npc.Y;
            npc.X += DirectionDeltaX(direction);
            npc.Y += DirectionDeltaY(direction);

            npc.MoveStartedMs = DartScape.Core.TimeUtil.MonotonicMs();
        }

        private static void DecodeNpcBlock(ClientNpc npc, PacketBuffer buffer)
        {
            int mask = buffer.ReadUnsignedByte();

            if ((mask & 0x40) != 0)
            {
                npc.DamageToShow = buffer.ReadUnsignedByte();
                buffer.ReadUnsignedByteA();
                npc.Hitpoints = buffer.ReadUnsignedByteC();
                npc.MaxHitpoints = buffer.ReadUnsignedByte();
            }

            if ((mask & 0x10) != 0)
            {
                npc.Animation = buffer.ReadUnsignedShort();
                buffer.ReadUnsignedByte();
            }

            if ((mask & 0x8) != 0)
            {
                buffer.ReadUnsignedShortA();
                buffer.ReadUnsignedShort();
            }

            if ((mask & 0x20) != 0)
            {
                npc.Graphic = buffer.ReadUnsignedShort();
                buffer.ReadInt();
            }

            if ((mask & 0x80) != 0)
            {
                npc.OverheadText = buffer.ReadString();
                npc.OverheadUntilMs = DartScape.Core.TimeUtil.MonotonicMs() + 6000;
            }

            if ((mask & 0x1) != 0) buffer.ReadUnsignedShort();
            if ((mask & 0x4) != 0) npc.DefinitionId = buffer.ReadUnsignedShort();
            if ((mask & 0x2) != 0) buffer.ReadUnsignedShort();
        }
    }
}
