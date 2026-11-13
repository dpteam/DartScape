using System;
using System.Collections.Generic;

namespace DartScape.Core.Cache
{
    /// <summary>
    /// One row of the cache's <c>map_index</c>: which files hold a given 64x64 region.
    /// </summary>
    /// <remarks>
    /// A region is identified by its position in a grid of 64-tile blocks, not by world
    /// coordinates. World tile <c>x</c> is in region <c>x / 64</c>, and the region's
    /// south-west corner is at <c>regionX * 64</c>.
    /// </remarks>
    public sealed class MapIndexEntry
    {
        /// <summary>Region column, in 64-tile blocks.</summary>
        public int RegionX;

        /// <summary>Region row, in 64-tile blocks.</summary>
        public int RegionY;

        /// <summary>Store slot holding the terrain (heights and floors).</summary>
        public int TerrainFileId;

        /// <summary>Store slot holding the object placements.</summary>
        public int ObjectFileId;

        /// <summary>True for a members-only region.</summary>
        public bool Members;

        /// <summary>Packed identity, <c>(regionX &lt;&lt; 8) | regionY</c>.</summary>
        public int Packed
        {
            get { return (RegionX << 8) | (RegionY & 0xFF); }
        }
    }

    /// <summary>
    /// The cache's table of contents for the world map.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this table the landscape files are unusable: they are numbered by slot, and
    /// nothing in a landscape file says which part of the world it describes. This maps
    /// region coordinates to the two slots that hold that region.
    /// </para>
    /// <para>
    /// It lives in the cache's config archive as <c>map_index</c>, seven bytes per row:
    /// a big-endian packed coordinate, the terrain slot, the object slot, and a members flag.
    /// </para>
    /// </remarks>
    public sealed class MapIndex
    {
        private readonly Dictionary<int, MapIndexEntry> _byPacked = new Dictionary<int, MapIndexEntry>(1024);

        /// <summary>Number of regions in the table.</summary>
        public int Count
        {
            get { return _byPacked.Count; }
        }

        /// <summary>Parses a <c>map_index</c> payload.</summary>
        public static MapIndex Parse(byte[] data)
        {
            var index = new MapIndex();

            if (data == null || data.Length < 7) return index;

            var buffer = new CacheBuffer(data);

            while (buffer.Remaining >= 7)
            {
                int packed = buffer.GetUnsignedShort();

                var entry = new MapIndexEntry
                {
                    RegionX = packed >> 8,
                    RegionY = packed & 0xFF,
                    TerrainFileId = buffer.GetUnsignedShort(),
                    ObjectFileId = buffer.GetUnsignedShort(),
                    Members = buffer.GetUnsignedByte() != 0
                };

                index._byPacked[packed] = entry;
            }

            return index;
        }

        /// <summary>Looks up a region by its grid position.</summary>
        public bool TryGet(int regionX, int regionY, out MapIndexEntry entry)
        {
            return _byPacked.TryGetValue((regionX << 8) | (regionY & 0xFF), out entry);
        }

        /// <summary>Every region in the table, in no particular order.</summary>
        public IEnumerable<MapIndexEntry> Entries
        {
            get { return _byPacked.Values; }
        }
    }
}
