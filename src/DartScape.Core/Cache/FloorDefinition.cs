using System;
using DartScape.Core.Diagnostics;
using System.Collections.Generic;

namespace DartScape.Core.Cache
{
    /// <summary>
    /// A floor or ground texture definition, from the cache's <c>flo.dat</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Terrain in RuneScape is not textured per tile with an image. Each tile names an
    /// <em>underlay</em> (the base ground) and optionally an <em>overlay</em> (a patch painted
    /// on top, such as a path, a floor or water), and each of those is one of these
    /// definitions. So a handful of colours describe the whole world, which is why the terrain
    /// reads as coherent instead of noisy.
    /// </para>
    /// <para>
    /// The colour is packed <c>0xRRGGBB</c>, not ARGB, and there is a separate minimap colour
    /// because the map is drawn with a deliberately flattened palette.
    /// </para>
    /// </remarks>
    public sealed class FloorDefinition
    {
        /// <summary>Definition id.</summary>
        public int Id;

        /// <summary>Packed <c>0xRRGGBB</c> colour.</summary>
        public int Colour;

        /// <summary>Texture id, or -1 for a flat colour.</summary>
        public int TextureId = -1;

        /// <summary>Minimap colour, packed <c>0xRRGGBB</c>.</summary>
        public int MinimapColour;

        /// <summary>True when the tile hides whatever is behind it.</summary>
        public bool Occlude = true;

        /// <summary>Name, as it appears in the cache. Usually null.</summary>
        public string Name;

        /// <summary>
        /// True when this floor is water.
        /// </summary>
        /// <remarks>
        /// There is no flag for this in the cache, so it is derived from the two signals that
        /// actually distinguish water: the definition is named after it, and it carries one of
        /// the animated water textures. Colour alone cannot work — the sea and a blue-painted
        /// floor are the same RGB — and the texture ids are not contiguous, so they are listed.
        /// </remarks>
        public bool IsWater;
    }

    /// <summary>
    /// Reads <c>flo.dat</c> and answers "what does floor id N look like".
    /// </summary>
    /// <remarks>
    /// The format is a count followed by a stream of self-delimiting entries: opcode 0 ends an
    /// entry, and each opcode that carries data consumes exactly as many bytes as its payload.
    /// A parser that does not consume every payload byte lands in the middle of the next entry
    /// and produces plausible nonsense, so the opcode table is reproduced exactly.
    /// </remarks>
    public sealed class FloorDefinitionSet
    {
        /// <summary>
        /// Texture ids the client uses for animated water.
        /// </summary>
        /// <remarks>
        /// The two in the stock 317 cache are the sea and the sewer water. They are not
        /// contiguous and not derivable from the colour, so they are listed.
        /// </remarks>
        private static readonly int[] WaterTextures = { 1, 25 };

        private readonly FloorDefinition[] _definitions;

        private FloorDefinitionSet(FloorDefinition[] definitions)
        {
            _definitions = definitions;
        }

        /// <summary>Number of definitions.</summary>
        public int Count
        {
            get { return _definitions.Length; }
        }

        /// <summary>An empty set, used when no cache is available.</summary>
        public static FloorDefinitionSet Empty
        {
            get { return new FloorDefinitionSet(new FloorDefinition[0]); }
        }

        /// <summary>Returns the definition for an id, or <c>null</c>.</summary>
        public FloorDefinition Get(int id)
        {
            if (id < 0 || id >= _definitions.Length) return null;
            return _definitions[id];
        }

        /// <summary>Parses <c>flo.dat</c>.</summary>
        public static FloorDefinitionSet Parse(byte[] data)
        {
            if (data == null || data.Length < 2) return Empty;

            var buffer = new CacheBuffer(data);

            int count = buffer.GetUnsignedShort();
            var definitions = new FloorDefinition[count];

            for (int i = 0; i < count; i++)
            {
                var definition = new FloorDefinition { Id = i };
                definitions[i] = definition;

                while (true)
                {
                    int opcode = buffer.GetUnsignedByte();

                    if (opcode == 0) break;

                    switch (opcode)
                    {
                        case 1:
                            definition.Colour = buffer.Get3Bytes();
                            break;

                        case 2:
                            definition.TextureId = buffer.GetUnsignedByte();
                            break;

                        case 3:
                            // No payload. Present in the format and means nothing.
                            break;

                        case 5:
                            definition.Occlude = false;
                            break;

                        case 6:
                            definition.Name = buffer.GetString();
                            break;

                        case 7:
                            definition.MinimapColour = buffer.Get3Bytes();
                            break;

                        default:
                            // An unknown opcode has an unknown payload length, so the rest of
                            // the file is unreadable. Stop rather than emit garbage.
                            Log.Warning("Cache", "flo.dat: unrecognised opcode {0} at definition {1}.",
                                opcode, i);
                            return new FloorDefinitionSet(definitions);
                    }
                }

                definition.IsWater = IsWaterDefinition(definition);

                if (definition.MinimapColour == 0) definition.MinimapColour = definition.Colour;
            }

            return new FloorDefinitionSet(definitions);
        }

        private static bool IsWaterDefinition(FloorDefinition definition)
        {
            if (definition.Name != null &&
                definition.Name.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            foreach (int texture in WaterTextures)
            {
                if (definition.TextureId == texture) return true;
            }

            return false;
        }
    }
}
