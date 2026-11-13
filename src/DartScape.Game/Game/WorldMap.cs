using System;
using System.Collections.Generic;
using DartScape.Core.Cache;
using DartScape.Core.Diagnostics;

namespace DartScape.Game
{
    /// <summary>
    /// What one tile of the world looks like, as far as the game is concerned.
    /// </summary>
    public struct TileInfo
    {
        /// <summary>True when the cache has a region covering this tile.</summary>
        public bool Exists;

        /// <summary>Elevation of the tile's south-west corner, in the cache's units.</summary>
        public int Height;

        /// <summary>Underlay floor id, 0 for none.</summary>
        public int Underlay;

        /// <summary>Overlay floor id, 0 for none.</summary>
        public int Overlay;

        /// <summary>Render rule flags.</summary>
        public int Flags;

        /// <summary>True when the tile is water and therefore not walkable.</summary>
        public bool Water;
    }

    /// <summary>
    /// The real world, loaded from the cache.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This replaces the procedural heightfield as the source of truth for terrain, floors and
    /// obstacles. Both the client and the server use it, each with its own copy, so they agree
    /// about where the walls are without exchanging map data.
    /// </para>
    /// <para>
    /// Heights are exposed in the cache's own units. A tile's elevation is roughly 0 to 60,
    /// where 0 is sea level; the renderer converts that to pixels, and nothing else needs to
    /// care about the scale.
    /// </para>
    /// </remarks>
    public sealed class WorldMap
    {
        /// <summary>Tiles along one side of a map region.</summary>
        public const int RegionSize = 64;

        private readonly GameCache _cache;

        /// <summary>
        /// Opens the cache in <paramref name="directory"/>.
        /// </summary>
        /// <returns><c>null</c> when there is no usable cache.</returns>
        public static WorldMap Open(string directory)
        {
            GameCache cache = GameCache.Open(directory);
            if (cache == null) return null;

            var map = new WorldMap(cache);

            if (!cache.HasMap)
            {
                Log.Warning("World", "The cache has no map index; falling back to procedural terrain.");
                cache.Dispose();
                return null;
            }

            return map;
        }

        private WorldMap(GameCache cache)
        {
            _cache = cache;
        }

        /// <summary>
        /// Floor definitions, for colouring the ground.
        /// </summary>
        public FloorDefinitionSet Floors
        {
            get { return _cache.Floors; }
        }

        /// <summary>
        /// The underlying file store, so the game layer's definition loader can read the
        /// config archives directly.
        /// </summary>
        public FileStore Store
        {
            get { return _cache.Store; }
        }

        /// <summary>Object definitions, for sizes and names.</summary>
        public ObjectDefinitionSet Objects
        {
            get { return _cache.Objects; }
        }

        /// <summary>Number of regions in the cache's map table.</summary>
        public int RegionCount
        {
            get { return _cache.Map.Count; }
        }

        /// <summary>Region column containing a world tile.</summary>
        public static int RegionOf(int worldCoordinate)
        {
            return worldCoordinate >> 6;
        }

        /// <summary>
        /// Reads the tile at a world coordinate on plane 0.
        /// </summary>
        public TileInfo GetTile(int worldX, int worldY)
        {
            var info = new TileInfo();

            Landscape landscape = _cache.TryGetRegion(RegionOf(worldX), RegionOf(worldY));
            if (landscape == null) return info;

            int localX = worldX - landscape.BaseX;
            int localY = worldY - landscape.BaseY;

            if (localX < 0 || localY < 0 || localX >= RegionSize || localY >= RegionSize) return info;

            info.Exists = true;
            info.Height = landscape.GetHeight(0, localX, localY);
            info.Underlay = landscape.GetUnderlay(0, localX, localY);
            info.Overlay = landscape.GetOverlay(0, localX, localY);
            info.Flags = landscape.GetFlags(0, localX, localY);
            info.Water = IsWater(info.Overlay, info.Underlay);

            return info;
        }

        private bool IsWater(int overlayId, int underlayId)
        {
            // Floor ids are one-based into flo.dat, so id 0 means "no floor".
            FloorDefinition floor = _cache.Floors.Get(overlayId - 1);
            if (floor != null && floor.IsWater) return true;

            // A water tile usually has the water overlay. Some, mostly the open sea, carry it
            // as the underlay instead.
            floor = _cache.Floors.Get(underlayId - 1);
            return floor != null && floor.IsWater;
        }

        /// <summary>
        /// Elevation of the corner at a world coordinate, in cache units.
        /// </summary>
        /// <remarks>
        /// Corners are shared between up to four tiles, so this is the value the renderer
        /// samples four times per tile. A tile with no region is given the noise function's
        /// default, which keeps the sea flat rather than punching a hole in the world.
        /// </remarks>
        public int CornerHeight(int worldX, int worldY)
        {
            Landscape landscape = _cache.TryGetRegion(RegionOf(worldX), RegionOf(worldY));
            if (landscape == null) return 0;

            int localX = worldX - landscape.BaseX;
            int localY = worldY - landscape.BaseY;

            if (localX < 0 || localY < 0 || localX >= RegionSize || localY >= RegionSize) return 0;

            return landscape.GetHeight(0, localX, localY);
        }

        /// <summary>
        /// The objects standing on a tile, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// Only the object's own tile is indexed, not its whole footprint. For drawing that is
        /// enough: a four-tile-wide object is anchored at its south-west tile, which is where a
        /// placeholder sprite belongs.
        /// </remarks>
        public List<LandscapeObject> ObjectsAt(int worldX, int worldY)
        {
            Landscape landscape = _cache.TryGetRegion(RegionOf(worldX), RegionOf(worldY));
            if (landscape == null) return null;

            return landscape.ObjectsAtLocal(worldX - landscape.BaseX, worldY - landscape.BaseY);
        }

        /// <summary>
        /// Fills a collision map from the real landscape.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two passes, in this order. First every tile that exists gets a floor, which clears
        /// the void bit; then water and objects are marked as blockers. Doing it the other way
        /// round would leave the obstacle flags set on tiles that are still void, which is
        /// harmless but hides a mistake — and if the floor pass is forgotten entirely, the whole
        /// world becomes unwalkable and it looks like a pathfinding failure.
        /// </para>
        /// <para>
        /// Objects are applied by their config type, because that is what decides which
        /// collision rule the original uses. A wall blocks one edge; a solid object blocks its
        /// whole footprint; a decoration blocks nothing at all.
        /// </para>
        /// </remarks>
        public void BuildCollision(CollisionMap map, int centreX, int centreY, int radius)
        {
            if (map == null) return;

            int minX = centreX - radius;
            int maxX = centreX + radius;
            int minY = centreY - radius;
            int maxY = centreY + radius;

            // Pass one: floors and water.
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    TileInfo tile = GetTile(x, y);
                    if (!tile.Exists) continue;

                    map.MarkFloor(x, y);

                    if (tile.Water) map.MarkSolid(x, y);
                }
            }

            // Pass two: objects. Regions are walked rather than tiles, because a large object's
            // footprint extends beyond the tile it is anchored on and a tile-by-tile scan would
            // apply the same object several times.
            var visited = new HashSet<int>();

            for (int regionY = RegionOf(minY); regionY <= RegionOf(maxY); regionY++)
            {
                for (int regionX = RegionOf(minX); regionX <= RegionOf(maxX); regionX++)
                {
                    if (!visited.Add((regionX << 16) | regionY)) continue;

                    Landscape landscape = _cache.TryGetRegion(regionX, regionY);
                    if (landscape == null) continue;

                    foreach (LandscapeObject placed in landscape.Objects)
                    {
                        if (placed.Plane != 0) continue;

                        ObjectDefinition definition = _cache.Objects.Get(placed.Id);
                        if (definition == null) continue;

                        ApplyObject(map, placed, definition);
                    }
                }
            }
        }

        /// <summary>Applies one object's collision, following the original's rules per type.</summary>
        private static void ApplyObject(CollisionMap map, LandscapeObject placed, ObjectDefinition definition)
        {
            switch (placed.Type)
            {
                // Straight wall, diagonal walls, and the two-sided wall.
                case 0:
                case 1:
                case 2:
                case 3:
                    if (definition.Solid)
                    {
                        map.MarkWall(placed.X, placed.Y, placed.Type, placed.Rotation, false);
                    }

                    break;

                // Ground decoration: only blocks when it is something you can act on, such as
                // a trapdoor. Flowers and grass are type 22 as well and must stay walkable.
                case 22:
                    if (definition.Solid && definition.HasActions) map.MarkBlocked(placed.X, placed.Y);
                    break;

                // Wall decorations and scenery that hangs off a wall: no collision at all.
                case 4:
                case 5:
                case 6:
                case 7:
                case 8:
                    break;

                // Solid objects: roofs, rocks, trees, furniture.
                default:
                    if (definition.Solid)
                    {
                        map.MarkObject(placed.X, placed.Y, definition.SizeX, definition.SizeY,
                            placed.Rotation, false);
                    }

                    break;
            }
        }
    }
}
