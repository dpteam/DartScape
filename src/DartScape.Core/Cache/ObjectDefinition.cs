using System;
using DartScape.Core.Diagnostics;
using System.Collections.Generic;

namespace DartScape.Core.Cache
{
    /// <summary>
    /// One object definition, from the cache's <c>loc.dat</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what turns "the landscape places object 1281 at this tile, rotation 2" into
    /// something the collision map can use: how many tiles the object covers, whether it can
    /// be walked through, and whether it has a menu action at all.
    /// </para>
    /// <para>
    /// The defaults are the trap. An object is solid and walkable unless the definition says
    /// otherwise, so a parser that forgets to apply opcode 17 turns every flower and every
    /// patch of grass into a wall. That single mistake makes the whole world impassable, and
    /// it looks like a pathfinding bug rather than a config bug.
    /// </para>
    /// </remarks>
    public sealed class ObjectDefinition
    {
        /// <summary>Definition id.</summary>
        public int Id;

        /// <summary>Width of the footprint, in tiles.</summary>
        public int SizeX = 1;

        /// <summary>Depth of the footprint, in tiles.</summary>
        public int SizeY = 1;

        /// <summary>True when the object blocks movement.</summary>
        public bool Solid = true;

        /// <summary>
        /// True when the object is "walkable" in the original's sense, which is about
        /// projectiles rather than footsteps.
        /// </summary>
        public bool Walkable = true;

        /// <summary>True when the object offers at least one menu action.</summary>
        public bool HasActions;

        /// <summary>Name shown in the menu.</summary>
        public string Name;

        /// <summary>Up to five menu options, or <c>null</c>.</summary>
        public string[] Actions;

        /// <summary>Animation played on the object, or -1.</summary>
        public int AnimationId = -1;
    }

    /// <summary>
    /// Reads <c>loc.dat</c> and <c>loc.idx</c> and answers "what is object id N".
    /// </summary>
    /// <remarks>
    /// The definitions are stored as a stream of self-delimiting opcode records, with the
    /// offsets in <c>loc.idx</c>. Every opcode must consume exactly its payload: one wrong
    /// length and the parser silently starts reading a definition in the middle of another,
    /// which produces objects with impossible sizes rather than an error.
    /// </remarks>
    public sealed class ObjectDefinitionSet
    {
        private readonly ObjectDefinition[] _definitions;

        private ObjectDefinitionSet(ObjectDefinition[] definitions)
        {
            _definitions = definitions;
        }

        /// <summary>Number of definitions.</summary>
        public int Count
        {
            get { return _definitions.Length; }
        }

        /// <summary>An empty set, used when no cache is available.</summary>
        public static ObjectDefinitionSet Empty
        {
            get { return new ObjectDefinitionSet(new ObjectDefinition[0]); }
        }

        /// <summary>Returns the definition for an id, or <c>null</c> when it is not in the cache.</summary>
        public ObjectDefinition Get(int id)
        {
            if (id < 0 || id >= _definitions.Length) return null;
            return _definitions[id];
        }

        /// <summary>Parses <c>loc.dat</c> using the offsets in <c>loc.idx</c>.</summary>
        public static ObjectDefinitionSet Parse(byte[] indexData, byte[] definitionData)
        {
            if (indexData == null || definitionData == null) return Empty;

            var indexBuffer = new CacheBuffer(indexData);
            int count = indexBuffer.GetUnsignedShort();

            var offsets = new int[count];
            int offset = 2;

            for (int i = 0; i < count; i++)
            {
                offsets[i] = offset;
                offset += indexBuffer.GetUnsignedShort();
            }

            var definitions = new ObjectDefinition[count];
            var buffer = new CacheBuffer(definitionData);

            int malformed = 0;

            for (int i = 0; i < count; i++)
            {
                var definition = new ObjectDefinition { Id = i };
                definitions[i] = definition;

                if (offsets[i] >= definitionData.Length)
                {
                    // Truncated cache: keep the defaults rather than throwing, because one
                    // missing definition must not make the world unloadable.
                    continue;
                }

                buffer.Position = offsets[i];

                try
                {
                    ReadDefinition(buffer, definition);
                }
                catch (Exception)
                {
                    malformed++;
                }
            }

            if (malformed > 0)
            {
                Log.Warning("Cache", "loc.dat: {0} of {1} definitions were malformed.", malformed, count);
            }

            return new ObjectDefinitionSet(definitions);
        }

        private static void ReadDefinition(CacheBuffer buffer, ObjectDefinition definition)
        {
            bool hasModels = false;
            int firstModelType = -1;
            int actionsFlag = -1;
            bool unwalkableSolid = false;

            while (true)
            {
                int opcode = buffer.GetUnsignedByte();
                if (opcode == 0) break;

                switch (opcode)
                {
                    case 1:
                    {
                        int modelCount = buffer.GetUnsignedByte();
                        for (int i = 0; i < modelCount; i++)
                        {
                            buffer.GetUnsignedShort();                  // model id
                            int type = buffer.GetUnsignedByte();        // model type
                            if (i == 0) firstModelType = type;
                        }

                        if (modelCount > 0) hasModels = true;
                        break;
                    }

                    case 2:
                        definition.Name = buffer.GetString();
                        break;

                    case 3:
                        buffer.GetString();                             // description
                        break;

                    case 5:
                    {
                        int modelCount = buffer.GetUnsignedByte();
                        for (int i = 0; i < modelCount; i++) buffer.GetUnsignedShort();

                        if (modelCount > 0) hasModels = true;
                        break;
                    }

                    case 14:
                        definition.SizeX = buffer.GetUnsignedByte();
                        break;

                    case 15:
                        definition.SizeY = buffer.GetUnsignedByte();
                        break;

                    case 17:
                        definition.Solid = false;
                        break;

                    case 18:
                        definition.Walkable = false;
                        break;

                    case 19:
                        actionsFlag = buffer.GetUnsignedByte();
                        break;

                    case 21:
                    case 22:
                    case 23:
                    case 62:
                    case 64:
                    case 73:
                        // Flag opcodes with no payload.
                        break;

                    case 24:
                    {
                        int animation = buffer.GetUnsignedShort();
                        definition.AnimationId = animation == 65535 ? -1 : animation;
                        break;
                    }

                    case 28:
                    case 29:
                    case 39:
                    case 69:
                    case 75:
                        buffer.GetUnsignedByte();
                        break;

                    case 40:
                    {
                        int colourCount = buffer.GetUnsignedByte();
                        for (int i = 0; i < colourCount; i++)
                        {
                            buffer.GetUnsignedShort();
                            buffer.GetUnsignedShort();
                        }

                        break;
                    }

                    case 60:
                    case 65:
                    case 66:
                    case 67:
                    case 68:
                        buffer.GetUnsignedShort();
                        break;

                    case 70:
                    case 71:
                    case 72:
                        buffer.GetShort();
                        break;

                    case 74:
                        // "Solid but not walkable": both flags are cleared at the end.
                        unwalkableSolid = true;
                        break;

                    case 77:
                    {
                        buffer.GetUnsignedShort();                      // varBit id
                        buffer.GetUnsignedShort();                      // config id
                        int childCount = buffer.GetUnsignedByte();
                        for (int i = 0; i <= childCount; i++) buffer.GetUnsignedShort();
                        break;
                    }

                    default:
                        if (opcode >= 30 && opcode < 39)
                        {
                            if (definition.Actions == null) definition.Actions = new string[5];

                            string action = buffer.GetString();
                            definition.Actions[opcode - 30] =
                                string.Equals(action, "hidden", StringComparison.OrdinalIgnoreCase)
                                    ? null
                                    : action;
                        }
                        else
                        {
                            Log.Warning("Cache", "loc.dat: unrecognised opcode {0} on object {1}.",
                                opcode, definition.Id);

                            // The payload length is unknown, so the rest of this definition is
                            // unreadable. The defaults are safer than guessing.
                            return;
                        }

                        break;
                }
            }

            // The original decides "has actions" only after reading everything: an object with
            // no explicit opcode 19 falls back to "it has a model of the standard type".
            if (actionsFlag == -1)
            {
                definition.HasActions = hasModels && (firstModelType == -1 || firstModelType == 10);
            }
            else
            {
                definition.HasActions = actionsFlag == 1;
            }

            if (definition.Actions != null)
            {
                foreach (string action in definition.Actions)
                {
                    if (!string.IsNullOrEmpty(action))
                    {
                        definition.HasActions = true;
                        break;
                    }
                }
            }

            if (unwalkableSolid)
            {
                definition.Solid = false;
                definition.Walkable = false;
            }
        }
    }
}
