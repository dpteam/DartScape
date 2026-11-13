using System;
using System.Collections.Generic;
using DartScape.Core.Diagnostics;

namespace DartScape.Core.Cache
{
    /// <summary>One object placement from a landscape's object block.</summary>
    /// <remarks>
    /// <see cref="Type"/> is the object's <em>config type</em> — wall, wall decoration, solid
    /// object, ground decoration — and it decides which collision rule applies.
    /// <see cref="Rotation"/> is the face, 0..3.
    /// </remarks>
    public struct LandscapeObject
    {
        /// <summary>World x of the object's south-west tile.</summary>
        public int X;

        /// <summary>World y of the object's south-west tile.</summary>
        public int Y;

        /// <summary>Plane the object stands on.</summary>
        public int Plane;

        /// <summary>Object definition id.</summary>
        public int Id;

        /// <summary>Config type, 0..22.</summary>
        public int Type;

        /// <summary>Rotation, 0..3.</summary>
        public int Rotation;
    }

    /// <summary>
    /// The terrain and objects of one 64x64 map region.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the actual world geometry, taken from the cache. Everything the renderer and the
    /// collision map need is here: the elevation of every tile on every plane, which floor
    /// texture is under and over it, and every object placed on it.
    /// </para>
    /// <para>
    /// Heights are stored as <em>elevation</em>, not as the original's vertex heights. The
    /// original stores <c>-elevation * 8</c> because its projection wants an offset, and
    /// carrying that negation around means every consumer has to remember to flip it. The
    /// conversion happens once, here.
    /// </para>
    /// </remarks>
    public sealed class Landscape
    {
        /// <summary>Tiles along one side of a region.</summary>
        public const int Size = 64;

        /// <summary>Planes in a region.</summary>
        public const int Planes = 4;

        private const int TilesPerPlane = Size * Size;
        private const int TotalTiles = TilesPerPlane * Planes;

        /// <summary>Region column, in 64-tile blocks.</summary>
        public int RegionX;

        /// <summary>Region row, in 64-tile blocks.</summary>
        public int RegionY;

        /// <summary>Elevation per tile, indexed <c>plane * 4096 + x * 64 + y</c>.</summary>
        public readonly int[] Height = new int[TotalTiles];

        /// <summary>Underlay floor id per tile, 0 for none.</summary>
        public readonly byte[] Underlay = new byte[TotalTiles];

        /// <summary>Overlay floor id per tile, 0 for none.</summary>
        public readonly byte[] Overlay = new byte[TotalTiles];

        /// <summary>Render rule flags per tile.</summary>
        public readonly byte[] Flags = new byte[TotalTiles];

        /// <summary>Objects placed in this region.</summary>
        public readonly List<LandscapeObject> Objects = new List<LandscapeObject>(256);

        private Dictionary<int, List<LandscapeObject>> _byTile;

        /// <summary>
        /// Objects anchored on one local tile, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// The index is built on first use rather than while parsing: most callers only need
        /// the flat object list (collision does), and building a dictionary per region for a
        /// few hundred objects costs more than it saves when nobody asks.
        /// </remarks>
        public List<LandscapeObject> ObjectsAtLocal(int localX, int localY)
        {
            if (localX < 0 || localY < 0 || localX >= Size || localY >= Size) return null;

            if (_byTile == null)
            {
                var index = new Dictionary<int, List<LandscapeObject>>(Objects.Count);

                foreach (LandscapeObject placed in Objects)
                {
                    int x = placed.X - BaseX;
                    int y = placed.Y - BaseY;

                    if (x < 0 || y < 0 || x >= Size || y >= Size) continue;

                    int key = (placed.Plane << 12) | (x << 6) | y;

                    List<LandscapeObject> bucket;
                    if (!index.TryGetValue(key, out bucket))
                    {
                        bucket = new List<LandscapeObject>(1);
                        index[key] = bucket;
                    }

                    bucket.Add(placed);
                }

                _byTile = index;
            }

            List<LandscapeObject> result;
            _byTile.TryGetValue((localX << 6) | localY, out result);
            return result;
        }

        /// <summary>World x of the region's south-west corner.</summary>
        public int BaseX
        {
            get { return RegionX * Size; }
        }

        /// <summary>World y of the region's south-west corner.</summary>
        public int BaseY
        {
            get { return RegionY * Size; }
        }

        private static int Index(int plane, int x, int y)
        {
            return plane * TilesPerPlane + x * Size + y;
        }

        /// <summary>Elevation of a local tile.</summary>
        public int GetHeight(int plane, int x, int y)
        {
            return Height[Index(plane, x, y)];
        }

        /// <summary>Underlay floor id of a local tile, 0 for none.</summary>
        public int GetUnderlay(int plane, int x, int y)
        {
            return Underlay[Index(plane, x, y)];
        }

        /// <summary>Overlay floor id of a local tile, 0 for none.</summary>
        public int GetOverlay(int plane, int x, int y)
        {
            return Overlay[Index(plane, x, y)];
        }

        /// <summary>Render rule flags of a local tile.</summary>
        public int GetFlags(int plane, int x, int y)
        {
            return Flags[Index(plane, x, y)];
        }

        // -- terrain noise -----------------------------------------------------
        //
        // Tiles with no explicit height get one from a small deterministic noise function. It
        // has to match the original exactly, because it is the height of every default tile in
        // the world: a different function gives smooth-looking hills in the wrong places and
        // makes the terrain disagree with the cache's own idea of the coastline.

        private static int Noise(int x, int seed)
        {
            unchecked
            {
                int n = x + seed * 57;
                n = (n << 13) ^ n;
                int value = n * (n * n * 15731 + 0xC0AE5) + 0x5208DD0D & 0x7FFFFFFF;
                return value >> 19 & 0xFF;
            }
        }

        private static int NoiseSum(int x, int y)
        {
            int diagonal = Noise(x - 1, y - 1) + Noise(x + 1, y - 1)
                         + Noise(x - 1, y + 1) + Noise(x + 1, y + 1);

            int orthogonal = Noise(x - 1, y) + Noise(x + 1, y)
                           + Noise(x, y - 1) + Noise(x, y + 1);

            int local = Noise(x, y);

            return diagonal / 16 + orthogonal / 8 + local / 4;
        }

        private static int Interpolate(int from, int to, int delta, int scale)
        {
            return from + (to - from) * delta / scale;
        }

        private static int NoiseLayer(int x, int y, int scale)
        {
            int cellX = x / scale;
            int deltaX = x & (scale - 1);
            int cellY = y / scale;
            int deltaY = y & (scale - 1);

            int southWest = NoiseSum(cellX, cellY);
            int southEast = NoiseSum(cellX + 1, cellY);
            int northWest = NoiseSum(cellX, cellY + 1);
            int northEast = NoiseSum(cellX + 1, cellY + 1);

            int bottom = Interpolate(southWest, southEast, deltaX, scale);
            int top = Interpolate(northWest, northEast, deltaX, scale);

            return Interpolate(bottom, top, deltaY, scale);
        }

        /// <summary>
        /// The elevation the original gives a tile that carries no explicit height.
        /// </summary>
        /// <remarks>
        /// Three noise layers at scales 4, 2 and 1, each contributing less than the last. It is
        /// a pure function of world coordinates: the original passes scene-local coordinates
        /// plus the scene base, and those sum to the world coordinate. That is what makes it
        /// safe to compute per region instead of per 104x104 scene.
        /// </remarks>
        public static int DefaultHeight(int worldX, int worldY)
        {
            int x = 0xE3B7B + worldX;
            int y = 0x87CCE + worldY;

            int value = (NoiseLayer(x + 45365, y + 0x16713, 4) - 128)
                      + ((NoiseLayer(x + 10294, y + 37821, 2) - 128) >> 1)
                      + ((NoiseLayer(x, y, 1) - 128) >> 2);

            value = (int)(value * 0.3) + 35;

            if (value < 10) value = 10;
            if (value > 60) value = 60;

            return value;
        }

        /// <summary>
        /// Parses a region from its two cache files.
        /// </summary>
        /// <param name="terrainData">Plain (already decompressed) terrain block.</param>
        /// <param name="objectData">Plain (already decompressed) object block.</param>
        /// <param name="regionX">Region column.</param>
        /// <param name="regionY">Region row.</param>
        public static Landscape Parse(byte[] terrainData, byte[] objectData, int regionX, int regionY)
        {
            if (terrainData == null) return null;

            var landscape = new Landscape { RegionX = regionX, RegionY = regionY };

            try
            {
                landscape.ReadTerrain(terrainData);
            }
            catch (Exception ex)
            {
                Log.Warning("Cache", "Terrain for region {0},{1} is malformed: {2}",
                    regionX, regionY, ex.Message);
                return null;
            }

            if (objectData != null)
            {
                try
                {
                    landscape.ReadObjects(objectData);
                }
                catch (Exception ex)
                {
                    // A broken object block costs the objects, not the terrain. Terrain alone
                    // still renders and still gives a walkable world.
                    Log.Warning("Cache", "Objects for region {0},{1} are malformed: {2}",
                        regionX, regionY, ex.Message);
                }
            }

            return landscape;
        }

        private void ReadTerrain(byte[] data)
        {
            var buffer = new CacheBuffer(data);

            for (int plane = 0; plane < Planes; plane++)
            {
                for (int x = 0; x < Size; x++)
                {
                    for (int y = 0; y < Size; y++)
                    {
                        ReadTile(buffer, plane, x, y);
                    }
                }
            }
        }

        /// <summary>
        /// Reads one tile's settings.
        /// </summary>
        /// <remarks>
        /// A tile is a sequence of settings bytes terminated by zero, and each byte means
        /// something different depending on its range. The ranges are contiguous and none of
        /// them are intuitive, so they are matched exactly rather than reinterpreted.
        /// </remarks>
        private void ReadTile(CacheBuffer buffer, int plane, int x, int y)
        {
            int index = Index(plane, x, y);

            while (true)
            {
                int setting = buffer.GetUnsignedByte();

                if (setting == 0)
                {
                    // No explicit height. Plane 0 falls back to the noise function; higher
                    // planes sit one storey above the plane below.
                    Height[index] = plane == 0
                        ? DefaultHeight(BaseX + x, BaseY + y)
                        : Height[Index(plane - 1, x, y)] + 30;

                    return;
                }

                if (setting == 1)
                {
                    int height = buffer.GetUnsignedByte();
                    if (height == 1) height = 0;

                    Height[index] = plane == 0
                        ? height
                        : Height[Index(plane - 1, x, y)] + height;

                    return;
                }

                if (setting <= 49)
                {
                    Overlay[index] = (byte)buffer.GetUnsignedByte();
                    // The remaining two bits of the setting carry the clipping path and the
                    // rotation. Neither affects walking, so they are read and dropped.
                }
                else if (setting <= 81)
                {
                    Flags[index] = (byte)(setting - 49);
                }
                else
                {
                    Underlay[index] = (byte)(setting - 81);
                }
            }
        }

        /// <summary>
        /// Reads the object block.
        /// </summary>
        /// <remarks>
        /// Objects are stored as two nested delta streams: one for the object id, one for the
        /// position. A zero in either ends the current level, which is why the loops look
        /// inverted — the outer stream repeats an id, the inner stream repeats positions for
        /// that id. Both use <see cref="CacheBuffer.GetSmart"/>, not the model-delta variant.
        /// </remarks>
        private void ReadObjects(byte[] data)
        {
            var buffer = new CacheBuffer(data);

            int objectId = -1;

            while (true)
            {
                int idOffset = buffer.GetSmart();
                if (idOffset == 0) break;

                objectId += idOffset;

                int position = 0;

                while (true)
                {
                    int positionOffset = buffer.GetSmart();
                    if (positionOffset == 0) break;

                    position += positionOffset - 1;

                    int localY = position & 0x3F;
                    int localX = (position >> 6) & 0x3F;
                    int plane = position >> 12;

                    int packed = buffer.GetUnsignedByte();

                    Objects.Add(new LandscapeObject
                    {
                        X = BaseX + localX,
                        Y = BaseY + localY,
                        Plane = plane,
                        Id = objectId,
                        Type = packed >> 2,
                        Rotation = packed & 3
                    });
                }
            }
        }
    }
}
