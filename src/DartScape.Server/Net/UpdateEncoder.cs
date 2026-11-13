using System;
using System.Collections.Generic;
using DartScape.Core.Protocol;
using DartScape.Game.Definitions;
using DartScape.Game;

namespace DartScape.Server.Net
{
    /// <summary>
    /// Builds the two periodic update packets: player synchronisation and NPC
    /// synchronisation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the most delicate code in the server. The client is a strict reader: it
    /// consumes a bit stream whose shape depends on what the server decided to send, so a
    /// single wrong bit does not produce an error message, it produces a client that
    /// silently misplaces every entity. Every write here mirrors a specific read in the
    /// client's <c>updatePlayers</c> / <c>updateNpcs</c>.
    /// </para>
    /// <para>
    /// Two rules keep it tractable:
    /// </para>
    /// <list type="bullet">
    /// <item>The local list only ever appends at the end, because the protocol can only
    /// remove entities from the tail. When an entity in the middle has to go, the whole
    /// list is resynchronised instead of being patched.</item>
    /// <item>An update block is only written for an entity that was explicitly marked
    /// "observed" while the movement section was being written. Writing a block for an
    /// entity the client did not expect desynchronises the packet.</item>
    /// </list>
    /// </remarks>
    public static class UpdateEncoder
    {
        /// <summary>Sentinel id that terminates the "new entities" list.</summary>
        private const int PlayerListTerminator = 2047;

        /// <summary>Sentinel id that terminates the NPC list.</summary>
        private const int NpcListTerminator = 16383;

        /// <summary>How far a player can see other entities, in tiles.</summary>
        public const int ViewDistance = 15;

        /// <summary>Sends this tick's player and NPC update packets to everyone.</summary>
        public static void SendUpdates(World world)
        {
            Player[] players = world.Players;

            for (int i = 0; i < players.Length; i++)
            {
                Player player = players[i];
                if (player == null || !player.Online || !player.RegionLoaded) continue;

                try
                {
                    SendPlayerUpdate(world, player);
                    SendNpcUpdate(world, player);
                }
                catch (Exception ex)
                {
                    Core.Diagnostics.Log.Exception("Update", ex, "Update failed for " + player.Name);
                }
            }
        }

        // -- players ----------------------------------------------------------

        private static void SendPlayerUpdate(World world, Player player)
        {
            PlayerView view = player.View;
            if (view == null) return;

            List<int> local = view.LocalPlayers;

            // Resolve appearance once, before the mask is inspected.
            if (player.AppearanceDirty)
            {
                player.UpdateMask |= PlayerUpdateFlags.Appearance;
                player.AppearanceDirty = false;
            }

            // --- work out the visible set ------------------------------------
            var visible = new List<int>(local.Count + 8);
            Player[] players = world.Players;

            for (int i = 0; i < players.Length; i++)
            {
                Player other = players[i];
                if (other == null || !other.Online || other == player) continue;
                if (other.Position.ChebyshevDistance(player.Position) > ViewDistance) continue;
                visible.Add(i);
            }

            // --- decide whether the list can be patched or must be rebuilt ----
            bool needResync = false;
            for (int i = 0; i < local.Count; i++)
            {
                if (visible.Contains(local[i])) continue;

                // Removing a middle entry would shift every index after it, so the
                // client would attribute movement flags to the wrong player. Rebuild.
                if (i != local.Count - 1) { needResync = true; break; }
            }

            var added = new List<int>(8);
            for (int i = 0; i < visible.Count; i++)
            {
                if (!local.Contains(visible[i])) added.Add(visible[i]);
            }

            if (needResync)
            {
                local.Clear();
                added.Clear();
                added.AddRange(visible);
            }
            else
            {
                // Trim the tail that is no longer visible.
                while (local.Count > 0 && !visible.Contains(local[local.Count - 1]))
                {
                    local.RemoveAt(local.Count - 1);
                }
            }

            // --- collect who needs an update block ---------------------------
            var observed = new List<int>(local.Count + added.Count);

            var buffer = new PacketBuffer(1024);
            buffer.StartBitAccess();

            // -- local player movement ---------------------------------------
            bool selfNeedsBlock = player.UpdateMask != 0;

            if (player.Teleported)
            {
                buffer.WriteBits(1, 1);
                buffer.WriteBits(2, 3);
                buffer.WriteBits(2, player.Position.Plane);
                buffer.WriteBits(1, 1);                       // clear waypoint queue
                buffer.WriteBits(1, selfNeedsBlock ? 1 : 0);
                if (selfNeedsBlock) observed.Add(player.Index);
                buffer.WriteBits(7, player.Position.Y);
                buffer.WriteBits(7, player.Position.X);
            }
            else if (player.StepsThisTick >= 2)
            {
                buffer.WriteBits(1, 1);
                buffer.WriteBits(2, 2);
                buffer.WriteBits(3, player.FirstStepDirection);
                buffer.WriteBits(3, player.SecondStepDirection);
                buffer.WriteBits(1, selfNeedsBlock ? 1 : 0);
                if (selfNeedsBlock) observed.Add(player.Index);
            }
            else if (player.StepsThisTick == 1)
            {
                buffer.WriteBits(1, 1);
                buffer.WriteBits(2, 1);
                buffer.WriteBits(3, player.FirstStepDirection);
                buffer.WriteBits(1, selfNeedsBlock ? 1 : 0);
                if (selfNeedsBlock) observed.Add(player.Index);
            }
            else if (selfNeedsBlock)
            {
                buffer.WriteBits(1, 1);
                buffer.WriteBits(2, 0);
                observed.Add(player.Index);
            }
            else
            {
                buffer.WriteBits(1, 0);
            }

            // -- other players ------------------------------------------------
            buffer.WriteBits(8, local.Count);

            for (int i = 0; i < local.Count; i++)
            {
                Player other = players[local[i]];
                if (other == null) { buffer.WriteBits(1, 0); continue; }

                bool block = other.UpdateMask != 0;

                if (other.StepsThisTick >= 2)
                {
                    buffer.WriteBits(1, 1);
                    buffer.WriteBits(2, 2);
                    buffer.WriteBits(3, other.FirstStepDirection);
                    buffer.WriteBits(3, other.SecondStepDirection);
                    buffer.WriteBits(1, block ? 1 : 0);
                    if (block) observed.Add(other.Index);
                }
                else if (other.StepsThisTick == 1)
                {
                    buffer.WriteBits(1, 1);
                    buffer.WriteBits(2, 1);
                    buffer.WriteBits(3, other.FirstStepDirection);
                    buffer.WriteBits(1, block ? 1 : 0);
                    if (block) observed.Add(other.Index);
                }
                else if (block)
                {
                    buffer.WriteBits(1, 1);
                    buffer.WriteBits(2, 0);
                    observed.Add(other.Index);
                }
                else
                {
                    buffer.WriteBits(1, 0);
                }
            }

            // -- newly visible players ----------------------------------------
            for (int i = 0; i < added.Count; i++)
            {
                Player other = players[added[i]];
                if (other == null) continue;

                local.Add(other.Index);

                buffer.WriteBits(11, other.Index);
                bool block = other.UpdateMask != 0;
                buffer.WriteBits(1, block ? 1 : 0);
                if (block) observed.Add(other.Index);

                buffer.WriteBits(1, 1);                       // teleport: place absolutely

                int dx = other.Position.X - player.Position.X;
                int dy = other.Position.Y - player.Position.Y;

                // The client reads the Y delta first, then the X delta.
                buffer.WriteBits(5, dy & 0x1F);
                buffer.WriteBits(5, dx & 0x1F);
            }

            buffer.WriteBits(11, PlayerListTerminator);
            buffer.FinishBitAccess();

            // -- update blocks -------------------------------------------------
            for (int i = 0; i < observed.Count; i++)
            {
                Player subject = observed[i] == player.Index ? player : players[observed[i]];
                if (subject == null) continue;
                WritePlayerBlock(buffer, subject);
            }

            player.Endpoint.Send(Protocol.OutPlayerUpdate, outBuffer => outBuffer.WriteBytes(buffer.ToArray()));
        }

        private static void WritePlayerBlock(PacketBuffer buffer, Player player)
        {
            int mask = player.UpdateMask;

            // Mask header: one byte, plus a second when anything above 0x3F is set.
            if ((mask & 0x40) != 0 || mask > 0xFF)
            {
                buffer.WriteByte(mask | 0x40);
                buffer.WriteByte(mask >> 8);
            }
            else
            {
                buffer.WriteByte(mask);
            }

            if ((mask & PlayerUpdateFlags.ForcedMovement) != 0)
            {
                buffer.WriteByteS(player.LastPosition.X);
                buffer.WriteByteS(player.LastPosition.Y);
                buffer.WriteByteS(player.Position.X);
                buffer.WriteByteS(player.Position.Y);
                buffer.WriteShortA(1);                       // start in one tick
                buffer.WriteShortA(1);                       // end in one tick
                buffer.WriteByteS(player.FaceDirection < 0 ? 0 : player.FaceDirection);
            }

            if ((mask & PlayerUpdateFlags.Graphic) != 0)
            {
                buffer.WriteShort(player.Graphic);
                buffer.WriteInt((player.GraphicHeight << 16) | (player.GraphicDelay & 0xFFFF));
            }

            if ((mask & PlayerUpdateFlags.Animation) != 0)
            {
                buffer.WriteShort(player.Animation);
                buffer.WriteByteC(player.AnimationDelay);
            }

            if ((mask & PlayerUpdateFlags.ForcedChat) != 0)
            {
                buffer.WriteString(player.ForcedChat ?? string.Empty);
            }

            if ((mask & PlayerUpdateFlags.Chat) != 0)
            {
                buffer.WriteShort((player.ChatColour << 8) | (player.ChatEffect & 0xFF));
                buffer.WriteByte(player.ChatRights);
                byte[] text = TextEncoding.GetBytes(player.ChatText ?? string.Empty);
                buffer.WriteByteC(text.Length);
                buffer.WriteBytes(text, 0, text.Length);
            }

            if ((mask & PlayerUpdateFlags.InteractingEntity) != 0)
            {
                int target = player.InteractingNpcIndex >= 0
                    ? player.InteractingNpcIndex
                    : (player.InteractingPlayerIndex >= 0 ? 32768 + player.InteractingPlayerIndex : 0xFFFF);
                buffer.WriteShort(target);
            }

            if ((mask & PlayerUpdateFlags.Appearance) != 0)
            {
                byte[] appearance = BuildAppearance(player);
                buffer.WriteByteC(appearance.Length);
                buffer.WriteBytes(appearance, 0, appearance.Length);
            }

            if ((mask & PlayerUpdateFlags.FaceCoordinate) != 0 && player.FaceTile.HasValue)
            {
                buffer.WriteShortA(player.FaceTile.Value.X * 2 + 1);
                buffer.WriteShort(player.FaceTile.Value.Y * 2 + 1);
            }

            if ((mask & PlayerUpdateFlags.HitUpdate) != 0)
            {
                buffer.WriteByte(player.DamageToShow);
                buffer.WriteByteA(player.DamageType);
                buffer.WriteByteC(Math.Max(0, player.Hitpoints));
                buffer.WriteByte(player.MaxHitpoints);
            }
            else if ((mask & PlayerUpdateFlags.HitUpdate2) != 0)
            {
                buffer.WriteByte(player.DamageToShow);
                buffer.WriteByteS(player.DamageType);
                buffer.WriteByte(Math.Max(0, player.Hitpoints));
                buffer.WriteByteC(player.MaxHitpoints);
            }
        }

        /// <summary>
        /// Serialises the appearance block: gender, head icon, twelve equipment visuals
        /// and five colours.
        /// </summary>
        private static byte[] BuildAppearance(Player player)
        {
            var buffer = new PacketBuffer(64);

            buffer.WriteByte(player.Gender);
            buffer.WriteByte(player.SkullTicks > 0 ? 1 : 0);
            buffer.WriteByte(player.HeadIcon < 0 ? 0 : player.HeadIcon);

            // Equipment visual slots, in the client's order.
            for (int i = 0; i < 12; i++)
            {
                ItemStack item = player.EquipmentVisual[i];
                buffer.WriteShort(item.IsEmpty ? 0 : item.Id + 1);
            }

            // Body colours: hair, torso, legs, feet, skin.
            for (int i = 0; i < 5; i++)
            {
                buffer.WriteByte(player.Appearance[7 + i]);
            }

            // Body part models: head, chest, arms, hands, legs, feet, beard.
            for (int i = 0; i < 7; i++)
            {
                buffer.WriteShort(player.Appearance[i]);
            }

            // Weapon model, taken from the equipped weapon.
            ItemStack weapon = player.Equipment[3];
            buffer.WriteShort(weapon.IsEmpty ? 0 : weapon.Id + 1);

            return buffer.ToArray();
        }

        // -- npcs -------------------------------------------------------------

        private static void SendNpcUpdate(World world, Player player)
        {
            PlayerView view = player.View;
            if (view == null) return;

            List<int> local = view.LocalNpcs;
            Npc[] npcs = world.Npcs;

            // Visible set: live NPCs within range.
            var visible = new List<int>(local.Count + 16);
            for (int i = 0; i < npcs.Length; i++)
            {
                Npc npc = npcs[i];
                if (npc == null || npc.IsDead) continue;
                if (npc.Position.ChebyshevDistance(player.Position) > ViewDistance) continue;
                visible.Add(i);
            }

            bool needResync = false;
            for (int i = 0; i < local.Count; i++)
            {
                if (visible.Contains(local[i])) continue;
                if (i != local.Count - 1) { needResync = true; break; }
            }

            var added = new List<int>(8);
            for (int i = 0; i < visible.Count; i++)
            {
                if (!local.Contains(visible[i])) added.Add(visible[i]);
            }

            if (needResync)
            {
                local.Clear();
                added.Clear();
                added.AddRange(visible);
            }
            else
            {
                while (local.Count > 0 && !visible.Contains(local[local.Count - 1]))
                {
                    local.RemoveAt(local.Count - 1);
                }
            }

            var observed = new List<int>(local.Count + added.Count);

            var buffer = new PacketBuffer(1024);
            buffer.StartBitAccess();

            buffer.WriteBits(8, local.Count);

            for (int i = 0; i < local.Count; i++)
            {
                Npc npc = npcs[local[i]];
                if (npc == null) { buffer.WriteBits(1, 0); continue; }

                bool block = npc.UpdateMask != 0;

                if (npc.MovedThisTick && npc.SecondStepDirection >= 0)
                {
                    buffer.WriteBits(1, 1);
                    buffer.WriteBits(2, 2);
                    buffer.WriteBits(3, npc.FirstStepDirection);
                    buffer.WriteBits(3, npc.SecondStepDirection);
                    buffer.WriteBits(1, block ? 1 : 0);
                    if (block) observed.Add(npc.Index);
                }
                else if (npc.MovedThisTick)
                {
                    buffer.WriteBits(1, 1);
                    buffer.WriteBits(2, 1);
                    buffer.WriteBits(3, npc.FirstStepDirection);
                    buffer.WriteBits(1, block ? 1 : 0);
                    if (block) observed.Add(npc.Index);
                }
                else if (block)
                {
                    buffer.WriteBits(1, 1);
                    buffer.WriteBits(2, 0);
                    observed.Add(npc.Index);
                }
                else
                {
                    buffer.WriteBits(1, 0);
                }
            }

            // New NPCs.
            for (int i = 0; i < added.Count; i++)
            {
                Npc npc = npcs[added[i]];
                if (npc == null) continue;

                local.Add(npc.Index);

                buffer.WriteBits(14, npc.Index);

                int dy = npc.Position.Y - player.Position.Y;
                int dx = npc.Position.X - player.Position.X;
                buffer.WriteBits(5, dy & 0x1F);
                buffer.WriteBits(5, dx & 0x1F);

                buffer.WriteBits(1, 1);                        // clear waypoint queue
                buffer.WriteBits(12, npc.DisplayedDefinitionId);

                bool block = npc.UpdateMask != 0;
                buffer.WriteBits(1, block ? 1 : 0);
                if (block) observed.Add(npc.Index);
            }

            buffer.WriteBits(14, NpcListTerminator);
            buffer.FinishBitAccess();

            for (int i = 0; i < observed.Count; i++)
            {
                Npc npc = npcs[observed[i]];
                if (npc == null) continue;
                WriteNpcBlock(buffer, npc);
            }

            player.Endpoint.Send(Protocol.OutNpcUpdate, outBuffer => outBuffer.WriteBytes(buffer.ToArray()));
        }

        private static void WriteNpcBlock(PacketBuffer buffer, Npc npc)
        {
            int mask = npc.UpdateMask;
            buffer.WriteByte(mask);

            if ((mask & NpcUpdateFlags.HitUpdate) != 0)
            {
                buffer.WriteByte(npc.DamageToShow);
                buffer.WriteByteA(npc.DamageType);
                buffer.WriteByteC(Math.Max(0, npc.Hitpoints));
                buffer.WriteByte(npc.MaxHitpoints);
            }

            if ((mask & NpcUpdateFlags.Animation) != 0)
            {
                buffer.WriteShort(npc.Animation);
                buffer.WriteByte(0);
            }

            if ((mask & NpcUpdateFlags.FaceCoordinate) != 0 && npc.FaceTile.HasValue)
            {
                buffer.WriteShortA(npc.FaceTile.Value.X * 2 + 1);
                buffer.WriteShort(npc.FaceTile.Value.Y * 2 + 1);
            }

            if ((mask & NpcUpdateFlags.Graphic) != 0)
            {
                buffer.WriteShort(npc.Graphic);
                buffer.WriteInt((npc.GraphicHeight << 16) | (npc.GraphicDelay & 0xFFFF));
            }

            if ((mask & NpcUpdateFlags.Chat) != 0)
            {
                buffer.WriteString(npc.ForcedChat ?? string.Empty);
            }

            if ((mask & NpcUpdateFlags.InteractingEntity) != 0)
            {
                buffer.WriteShort(npc.TargetPlayer >= 0 ? 32768 + npc.TargetPlayer : 0xFFFF);
            }

            if ((mask & NpcUpdateFlags.Transform) != 0)
            {
                buffer.WriteShort(npc.DisplayedDefinitionId);
            }

            if ((mask & NpcUpdateFlags.TurnToDirection) != 0)
            {
                buffer.WriteShort(npc.TurnDirection);
            }
        }
    }
}
