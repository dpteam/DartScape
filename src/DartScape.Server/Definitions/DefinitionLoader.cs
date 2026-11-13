using System;
using System.Collections.Generic;
using DartScape.Core.Cache;
using DartScape.Core.Diagnostics;
using DartScape.Core.Protocol;

namespace DartScape.Server.Definitions
{
    /// <summary>An item as the game knows it: name, stackability, value, models.</summary>
    public sealed class ItemDefinition
    {
        public int Id;
        public string Name = "null";
        public string Description = "";
        public int Value;
        public bool Stackable;
        public bool Members;
        public bool Noted;
        public int NoteId = -1;
        public int NoteTemplateId = -1;
        public double Weight;
        public int Team;

        /// <summary>The five inventory actions, index 0..4 = option 1..5.</summary>
        public string[] InventoryActions = new string[5];

        /// <summary>The five ground actions, index 0..4 = option 1..5.</summary>
        public string[] GroundActions = new string[5];

        public int ModelId;
        public int ModelZoom = 2000;
        public int ModelRotationX;
        public int ModelRotationY;
        public int ModelRotationZ;
        public int OffsetX;
        public int OffsetY;

        public bool IsStackable(int amount)
        {
            return Stackable || amount < 0;
        }

        public override string ToString()
        {
            return "Item(" + Id + ", " + Name + ")";
        }
    }

    /// <summary>An NPC type: name, size, combat level, animations, drops.</summary>
    public sealed class NpcDefinition
    {
        public int Id;
        public string Name = "null";
        public string Description = "";
        public int Size = 1;
        public int CombatLevel;
        public int Hitpoints = 10;
        public int MaxHit;
        public int Attack;
        public int Defence;
        public int AttackSpeed = 4;
        public int AttackRange = 1;
        public int[] AttackTypes = new int[0];
        public bool Aggressive;
        public bool Poisonous;
        public bool Retreats;
        public int[] Drops = new int[0];
        public int StandAnimation = -1;
        public int WalkAnimation = -1;
        public int TurnAnimation = -1;
        public int TurnBackAnimation = -1;
        public int HeadIcon = -1;
        public int RespawnTicks = 25;

        public string[] Actions = new string[5];

        public override string ToString()
        {
            return "Npc(" + Id + ", " + Name + ", lvl " + CombatLevel + ")";
        }
    }

    /// <summary>
    /// Loads item and NPC definitions out of the cache config archive
    /// (<c>obj.dat</c>/<c>obj.idx</c> and <c>npc.dat</c>/<c>npc.idx</c>).
    /// </summary>
    /// <remarks>
    /// The <c>.idx</c> file holds one 2-byte offset per definition; the
    /// <c>.dat</c> file holds that definition's opcode stream, terminated by a
    /// zero byte. Unknown opcodes are skipped by length, which is what makes this
    /// loader survive cache revisions that added fields.
    /// </remarks>
    public static class DefinitionLoader
    {
        /// <summary>Item definitions indexed by item id. Never null after a successful load.</summary>
        public static ItemDefinition[] Items = new ItemDefinition[0];

        /// <summary>NPC definitions indexed by npc id. Never null after a successful load.</summary>
        public static NpcDefinition[] Npcs = new NpcDefinition[0];

        /// <summary>True when at least one item definition was loaded.</summary>
        public static bool HasItems
        {
            get { return Items.Length > 0; }
        }

        /// <summary>True when at least one NPC definition was loaded.</summary>
        public static bool HasNpcs
        {
            get { return Npcs.Length > 0; }
        }

        /// <summary>
        /// Loads both tables. Failures are logged and leave the affected table empty вЂ”
        /// the server still boots, which matters because a bad cache copy should not
        /// stop the world from starting.
        /// </summary>
        public static bool Load(FileStore store)
        {
            if (store == null) return false;

            Archive config = FindConfigArchive(store);
            if (config == null)
            {
                Log.Error("Defs", "Could not find the config archive in the cache.");
                return false;
            }

            bool ok = true;
            try
            {
                Items = LoadItems(config);
            }
            catch (Exception ex)
            {
                Log.Error("Defs", "Item definitions failed: {0}", ex.Message);
                ok = false;
            }

            try
            {
                Npcs = LoadNpcs(config);
            }
            catch (Exception ex)
            {
                Log.Error("Defs", "NPC definitions failed: {0}", ex.Message);
                ok = false;
            }

            return ok;
        }

        private static Archive FindConfigArchive(FileStore store)
        {
            // Store 0 is the config store; the config archive is the one that
            // contains "obj.dat" or "npc.dat". Search rather than assume an index,
            // because revisions differ.
            int count = store.GetFileCount(0);
            for (int i = 0; i < count; i++)
            {
                byte[] raw = store.Read(0, i);
                if (raw == null) continue;

                Archive archive = Archive.Parse(raw);
                if (archive == null) continue;

                if (archive.GetFile("obj.dat") != null || archive.GetFile("npc.dat") != null)
                {
                    Log.Info("Defs", "Using store 0 archive {0} as the config archive.", i);
                    return archive;
                }
            }
            return null;
        }

        // -- items ------------------------------------------------------------

        private static ItemDefinition[] LoadItems(Archive config)
        {
            byte[] dat = config.GetFile("obj.dat");
            byte[] idx = config.GetFile("obj.idx");

            if (dat == null || idx == null)
            {
                Log.Warning("Defs", "No obj.dat/obj.idx in the config archive; items are unavailable.");
                return new ItemDefinition[0];
            }

            int[] offsets = BuildOffsetTable(idx, out int count);
            var definitions = new ItemDefinition[count];

            for (int id = 0; id < count; id++)
            {
                definitions[id] = ParseItem(dat, offsets[id], id);
            }

            Log.Info("Defs", "Loaded {0} item definitions.", count);
            return definitions;
        }

        /// <summary>
        /// Converts a <c>.idx</c> file into per-definition offsets.
        /// </summary>
        /// <remarks>
        /// The index is a 2-byte definition count followed by one 2-byte *length* per
        /// definition. Offsets are cumulative and the first body starts immediately after
        /// the index header, i.e. at 2. Treating the entries as absolute offsets instead of
        /// lengths produces definitions whose first few fields are garbage and whose
        /// names carry the tail of the previous entry — which is exactly the symptom that
        /// makes this worth a dedicated, documented helper.
        /// </remarks>
        private static int[] BuildOffsetTable(byte[] idx, out int count)
        {
            if (idx == null || idx.Length < 2)
            {
                count = 0;
                return new int[0];
            }

            count = ((idx[0] & 0xFF) << 8) | (idx[1] & 0xFF);
            if (count <= 0 || 2 + count * 2 > idx.Length)
            {
                count = 0;
                return new int[0];
            }

            var offsets = new int[count];
            int running = 2;

            for (int i = 0; i < count; i++)
            {
                offsets[i] = running;
                int length = ((idx[2 + i * 2] & 0xFF) << 8) | (idx[3 + i * 2] & 0xFF);
                running += length;
            }

            return offsets;
        }

        private static ItemDefinition ParseItem(byte[] dat, int offset, int id)
        {
            var def = new ItemDefinition { Id = id };
            var buffer = new PacketBuffer(dat, offset, dat.Length - offset);

            while (true)
            {
                int opcode = buffer.ReadUnsignedByte();
                if (opcode == 0) break;

                switch (opcode)
                {
                    case 1: def.ModelId = buffer.ReadUnsignedShort(); break;
                    case 2: def.Name = buffer.ReadNewlineString(); break;
                    case 3: def.Description = buffer.ReadNewlineString(); break;
                    case 4: def.ModelZoom = buffer.ReadUnsignedShort(); break;
                    case 5: def.ModelRotationX = buffer.ReadUnsignedShort(); break;
                    case 6: def.ModelRotationY = buffer.ReadUnsignedShort(); break;
                    case 7:
                        // Signed 16-bit, stored as a "short A" in the original format.
                        def.OffsetX = (short)buffer.ReadUnsignedShortA();
                        break;
                    case 8:
                        def.OffsetY = (short)buffer.ReadUnsignedShortA();
                        break;
                    case 10: buffer.Skip(2); break;
                    case 11: def.Stackable = true; break;
                    case 12: def.Value = buffer.ReadInt(); break;
                    case 16: def.Members = true; break;
                    case 23:
                        buffer.Skip(2);
                        buffer.Skip(1);
                        break;
                    case 24: buffer.Skip(2); break;
                    case 25:
                        buffer.Skip(2);
                        buffer.Skip(1);
                        break;
                    case 26: buffer.Skip(2); break;
                    case 30: def.GroundActions[0] = ReadOptionalString(buffer); break;
                    case 31: def.GroundActions[1] = ReadOptionalString(buffer); break;
                    case 32: def.GroundActions[2] = ReadOptionalString(buffer); break;
                    case 33: def.GroundActions[3] = ReadOptionalString(buffer); break;
                    case 34: def.GroundActions[4] = ReadOptionalString(buffer); break;
                    case 35: def.InventoryActions[0] = ReadOptionalString(buffer); break;
                    case 36: def.InventoryActions[1] = ReadOptionalString(buffer); break;
                    case 37: def.InventoryActions[2] = ReadOptionalString(buffer); break;
                    case 38: def.InventoryActions[3] = ReadOptionalString(buffer); break;
                    case 39: def.InventoryActions[4] = ReadOptionalString(buffer); break;
                    case 40:
                    {
                        int replacements = buffer.ReadUnsignedByte();
                        for (int i = 0; i < replacements; i++)
                        {
                            buffer.Skip(2);   // original colour
                            buffer.Skip(2);   // replacement colour
                        }
                        break;
                    }
                    case 78: buffer.Skip(2); break;
                    case 90:
                    case 91:
                    case 92:
                    case 93:
                    case 95:
                        buffer.Skip(2);
                        break;
                    case 100:
                    case 101:
                    case 102:
                    case 103:
                    case 104:
                    case 105:
                    case 106:
                    case 107:
                    case 108:
                    case 109:
                        buffer.Skip(4);
                        break;
                    case 110:
                    case 111:
                    case 112:
                        buffer.Skip(2);
                        break;
                    case 113:
                    case 114:
                        buffer.Skip(1);
                        break;
                    case 115:
                        def.Team = buffer.ReadUnsignedByte();
                        break;
                    default:
                        // Unknown opcode: we cannot know its length, so stop here and
                        // keep everything parsed so far rather than desynchronise.
                        Log.Debug("Defs", "Item {0}: unknown opcode {1}, stopping early.", id, opcode);
                        return def;
                }
            }

            return def;
        }

        // -- npcs -------------------------------------------------------------

        private static NpcDefinition[] LoadNpcs(Archive config)
        {
            byte[] dat = config.GetFile("npc.dat");
            byte[] idx = config.GetFile("npc.idx");

            if (dat == null || idx == null)
            {
                Log.Warning("Defs", "No npc.dat/npc.idx in the config archive; NPCs are unavailable.");
                return new NpcDefinition[0];
            }

            int[] offsets = BuildOffsetTable(idx, out int count);
            var definitions = new NpcDefinition[count];

            for (int id = 0; id < count; id++)
            {
                definitions[id] = ParseNpc(dat, offsets[id], id);
            }

            Log.Info("Defs", "Loaded {0} NPC definitions.", count);
            return definitions;
        }

        private static NpcDefinition ParseNpc(byte[] dat, int offset, int id)
        {
            var def = new NpcDefinition { Id = id };
            var buffer = new PacketBuffer(dat, offset, dat.Length - offset);

            while (true)
            {
                int opcode = buffer.ReadUnsignedByte();
                if (opcode == 0) break;

                switch (opcode)
                {
                    case 1:
                    {
                        int models = buffer.ReadUnsignedByte();
                        for (int i = 0; i < models; i++) buffer.Skip(2);
                        break;
                    }
                    case 2: def.Name = buffer.ReadNewlineString(); break;
                    case 3: def.Description = buffer.ReadNewlineString(); break;
                    case 12: def.Size = buffer.ReadUnsignedByte(); break;
                    case 13: def.StandAnimation = buffer.ReadUnsignedShort(); break;
                    case 14: def.WalkAnimation = buffer.ReadUnsignedShort(); break;
                    case 17:
                        def.WalkAnimation = buffer.ReadUnsignedShort();
                        def.TurnBackAnimation = buffer.ReadUnsignedShort();
                        def.TurnAnimation = buffer.ReadUnsignedShort();
                        def.StandAnimation = buffer.ReadUnsignedShort();
                        buffer.Skip(4);
                        break;
                    case 30: def.Actions[0] = ReadOptionalString(buffer); break;
                    case 31: def.Actions[1] = ReadOptionalString(buffer); break;
                    case 32: def.Actions[2] = ReadOptionalString(buffer); break;
                    case 33: def.Actions[3] = ReadOptionalString(buffer); break;
                    case 34: def.Actions[4] = ReadOptionalString(buffer); break;
                    case 40:
                    {
                        int replacements = buffer.ReadUnsignedByte();
                        for (int i = 0; i < replacements; i++)
                        {
                            buffer.Skip(2);
                            buffer.Skip(2);
                        }
                        break;
                    }
                    case 60:
                    {
                        int models = buffer.ReadUnsignedByte();
                        for (int i = 0; i < models; i++) buffer.Skip(2);
                        break;
                    }
                    case 90:
                    case 91:
                    case 92:
                        buffer.Skip(2);
                        break;
                    case 95: def.CombatLevel = buffer.ReadUnsignedShort(); break;
                    case 97: buffer.Skip(2); break;
                    case 98: buffer.Skip(2); break;
                    case 99: buffer.Skip(1); break;
                    case 100: buffer.Skip(1); break;
                    case 101: buffer.Skip(1); break;
                    case 102: def.HeadIcon = buffer.ReadUnsignedShort(); break;
                    case 103: buffer.Skip(2); break;
                    case 106:
                    {
                        int count = buffer.ReadUnsignedByte();
                        for (int i = 0; i < count; i++)
                        {
                            buffer.Skip(2);   // varp id
                            buffer.Skip(2);   // child npc id
                        }
                        break;
                    }
                    case 107: buffer.Skip(1); break;
                    default:
                        Log.Debug("Defs", "NPC {0}: unknown opcode {1}, stopping early.", id, opcode);
                        return def;
                }
            }

            if (def.Size < 1) def.Size = 1;

            // The cache stores no hitpoints, attack or defence for NPCs — those lived in
            // the server's own spawn tables in the original game. Deriving them from the
            // combat level keeps every NPC fightable and is the documented gap in
            // docs/PIPELINE.md.
            if (def.Hitpoints < 1)
            {
                def.Hitpoints = Math.Max(3, Math.Min(255, def.CombatLevel + 1));
            }
            if (def.Attack < 1) def.Attack = Math.Max(1, def.CombatLevel / 2 + 1);
            if (def.Defence < 1) def.Defence = Math.Max(1, def.CombatLevel / 2 + 1);
            if (def.MaxHit < 1) def.MaxHit = Math.Max(1, def.CombatLevel / 8 + 1);
            if (def.AttackSpeed < 2) def.AttackSpeed = 4;
            if (def.RespawnTicks < 1) def.RespawnTicks = 25;

            return def;
        }

        /// <summary>
        /// Reads an optional action string. The cache stores "no action" as the literal
        /// text <c>hidden</c>, which the client treats as absent.
        /// </summary>
        private static string ReadOptionalString(PacketBuffer buffer)
        {
            string value = buffer.ReadNewlineString();
            if (value.Length == 0) return null;
            if (string.Equals(value, "hidden", StringComparison.OrdinalIgnoreCase)) return null;
            return value;
        }

        /// <summary>Convenience lookup that never throws.</summary>
        public static ItemDefinition Item(int id)
        {
            if (id < 0 || id >= Items.Length) return null;
            return Items[id];
        }

        /// <summary>Convenience lookup that never throws.</summary>
        public static NpcDefinition Npc(int id)
        {
            if (id < 0 || id >= Npcs.Length) return null;
            return Npcs[id];
        }
    }
}
