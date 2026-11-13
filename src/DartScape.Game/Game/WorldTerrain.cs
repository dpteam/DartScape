using System;

namespace DartScape.Game
{
    /// <summary>
    /// Procedural world shape: elevation and terrain class for any tile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This lives in the game layer, not the renderer, because the server needs the same
    /// answers as the client. Collision, pathfinding and the client's drawing all have to
    /// agree on where the water is and where the ground steps up; if they each invented
    /// their own noise, the client would draw a hill where the server had a wall.
    /// </para>
    /// <para>
    /// Heights are sampled at grid <em>corners</em>, not per tile, which is what the original
    /// does: a corner is shared by four tiles, so the ground is continuous and a slope is a
    /// real ramp rather than a staircase.
    /// </para>
    /// <para>
    /// It is all a pure function of (x, y), so nothing has to be generated ahead of time or
    /// synchronised between the two processes.
    /// </para>
    /// </remarks>
    public static class WorldTerrain
    {
        /// <summary>Terrain classes.</summary>
        public const int Water = 0;
        public const int Sand = 1;
        public const int Grass = 2;
        public const int Dirt = 3;
        public const int Rock = 4;

        /// <summary>Highest elevation a corner can have, in height units.</summary>
        public const int MaxHeight = 12;

        /// <summary>Elevation at or above which ground is impassable.</summary>
        public const int ImpassableHeight = 10;

        /// <summary>Hashes a corner coordinate to a stable value in [0, 1).</summary>
        private static double Hash(int x, int y)
        {
            unchecked
            {
                uint h = (uint)x * 374761393u + (uint)y * 668265263u + 0x9E3779B9u;
                h ^= h >> 13;
                h *= 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777216.0;
            }
        }

        /// <summary>Smoothly interpolated value noise on a lattice of the given scale.</summary>
        private static double ValueNoise(double x, double y, int scale)
        {
            double fx = x / scale;
            double fy = y / scale;

            int x0 = (int)Math.Floor(fx);
            int y0 = (int)Math.Floor(fy);

            double tx = fx - x0;
            double ty = fy - y0;

            // Smoothstep removes the lattice grid that linear interpolation leaves visible.
            tx = tx * tx * (3 - 2 * tx);
            ty = ty * ty * (3 - 2 * ty);

            double a = Hash(x0, y0);
            double b = Hash(x0 + 1, y0);
            double c = Hash(x0, y0 + 1);
            double d = Hash(x0 + 1, y0 + 1);

            double top = a + (b - a) * tx;
            double bottom = c + (d - c) * tx;

            return top + (bottom - top) * ty;
        }

        /// <summary>Fractal noise: broad shapes, medium slopes, fine detail.</summary>
        private static double Fractal(double x, double y, int baseScale)
        {
            return ValueNoise(x, y, baseScale) * 0.55
                 + ValueNoise(x, y, baseScale / 2) * 0.28
                 + ValueNoise(x, y, Math.Max(3, baseScale / 5)) * 0.17;
        }

        /// <summary>
        /// Elevation of a grid corner, in height units.
        /// </summary>
        /// <remarks>
        /// Land is shaped so low ground is common and high ground is rare, then quantised.
        /// The coastline is a hard zero, which gives the sea a flat plane to sit on.
        /// </remarks>
        public static int CornerHeight(int cx, int cy)
        {
            double n = Fractal(cx, cy, 34);

            const double seaLevel = 0.30;

            if (n < seaLevel) return 0;

            double land = (n - seaLevel) / (1.0 - seaLevel);
            land = Math.Pow(land, 1.25);

            int height = (int)Math.Round(land * MaxHeight);

            if (height < 1) height = 1;
            if (height > MaxHeight) height = MaxHeight;

            return height;
        }

        /// <summary>Height of the tile at (x, y), from its corner average.</summary>
        public static int HeightAt(int x, int y)
        {
            int h00 = CornerHeight(x, y);
            int h10 = CornerHeight(x + 1, y);
            int h01 = CornerHeight(x, y + 1);
            int h11 = CornerHeight(x + 1, y + 1);

            return (h00 + h10 + h01 + h11) / 4;
        }

        /// <summary>
        /// Terrain class for a tile.
        /// </summary>
        /// <remarks>
        /// Dirt paths come from a separate noise channel, so they wander independently of
        /// elevation and cross hills the way a real track would.
        /// </remarks>
        public static int TypeAt(int x, int y)
        {
            int height = HeightAt(x, y);

            if (height <= 0) return Water;
            if (height <= 1) return Sand;
            if (height >= ImpassableHeight) return Rock;

            double path = Fractal(x * 0.7 + 913, y * 0.7 + 371, 46);
            if (path > 0.545 && path < 0.565) return Dirt;

            return Grass;
        }

        /// <summary>True when a player can stand on this tile.</summary>
        public static bool IsWalkable(int x, int y)
        {
            int type = TypeAt(x, y);
            return type != Water && type != Rock;
        }

        /// <summary>True when a tree stands on this tile. Decorative and stable.</summary>
        public static bool HasTree(int x, int y)
        {
            if (TypeAt(x, y) != Grass) return false;
            if (NearSpawn(x, y)) return false;

            return Hash(x * 31 + 7, y * 17 + 5) < 0.075;
        }

        /// <summary>True when a boulder stands on this tile.</summary>
        public static bool HasBoulder(int x, int y)
        {
            if (TypeAt(x, y) != Rock) return false;
            if (NearSpawn(x, y)) return false;

            return Hash(x * 19 + 3, y * 23 + 11) < 0.08;
        }

        /// <summary>Keeps the spawn point clear so a new player is never buried in scenery.</summary>
        public static bool NearSpawn(int x, int y)
        {
            int dx = x - 3222;
            int dy = y - 3218;
            return dx * dx + dy * dy < 36;
        }

        /// <summary>
        /// Fills a collision map from the terrain, for the given square of world tiles.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is a placeholder for real landscape data. It has to do two things the original
        /// loader also does: clear the void bit on every tile that has a floor, and then mark the
        /// tiles that are occupied.
        /// </para>
        /// <para>
        /// Clearing the void bit first is not optional. A freshly reset collision map has every
        /// interior tile marked "no floor", and nothing is walkable until that bit is cleared —
        /// so a map that only marks obstacles produces a world where nobody can move at all.
        /// </para>
        /// </remarks>
        public static void BuildCollision(CollisionMap map, int centreX, int centreY, int radius)
        {
            if (map == null) return;

            for (int y = centreY - radius; y <= centreY + radius; y++)
            {
                for (int x = centreX - radius; x <= centreX + radius; x++)
                {
                    // Every tile inside the loaded area has a floor, walkable or not.
                    map.MarkFloor(x, y);

                    if (!IsWalkable(x, y))
                    {
                        map.MarkSolid(x, y);
                        continue;
                    }

                    // A tree or boulder occupies its tile, so the player walks around it. That
                    // is the visible difference between "there is a forest" and "there is a
                    // texture of a forest".
                    if (HasTree(x, y) || HasBoulder(x, y))
                    {
                        map.MarkSolid(x, y);
                    }
                }
            }
        }
    }
}
