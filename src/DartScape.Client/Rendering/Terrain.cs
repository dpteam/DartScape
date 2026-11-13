using System;
using System.Collections.Generic;

namespace DartScape.Client.Rendering
{
    /// <summary>
    /// Procedural heightfield terrain: elevation sampled at grid <em>corners</em>, so hills
    /// slope smoothly instead of stepping tile by tile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the shape the original uses. A tile does not have one height; it has four,
    /// one per corner, and the tile is drawn as two triangles spanning them. That is what
    /// makes ground roll rather than look like a staircase, and it is why a hill in the
    /// original is a smooth ramp with a visible face rather than a block.
    /// </para>
    /// <para>
    /// Heights are quantised to whole units (the original uses eight per tile) so that flat
    /// ground is genuinely flat and slopes are straight, which is what gives the terrain its
    /// characteristic faceted look. Continuous noise underneath keeps the shapes organic.
    /// </para>
    /// <para>
    /// Everything is a pure function of the corner coordinate, so the renderer, the minimap
    /// and the click-to-walk inverse projection agree on the land without sharing data.
    /// </para>
    /// </remarks>
    public static class Terrain
    {
        /// <summary>Terrain classes.</summary>
        public const int Water = 0;
        public const int Sand = 1;
        public const int Grass = 2;
        public const int Dirt = 3;
        public const int Rock = 4;

        /// <summary>Highest elevation a corner can have, in height units.</summary>
        public const int MaxHeight = 12;

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
        /// Land is shaped so that low ground is common and high ground is rare, then
        /// quantised. The coastline is a hard zero, which gives the sea a flat plane to sit
        /// on and stops the water from rippling with the noise underneath it.
        /// </remarks>
        public static int CornerHeight(int cx, int cy)
        {
            double n = Fractal(cx, cy, 34);

            // Sea level: below this the corner is flat water.
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
        /// elevation and cross hills the way a real track would. The threshold is low enough
        /// that paths are thin and rare rather than covering the map.
        /// </remarks>
        public static int TypeAt(int x, int y)
        {
            int height = HeightAt(x, y);

            if (height <= 0) return Water;
            if (height <= 1) return Sand;
            if (height >= 10) return Rock;

            // Path channel: a different lattice so tracks do not follow the hills.
            double path = Fractal(x * 0.7 + 913, y * 0.7 + 371, 46);
            if (path > 0.545 && path < 0.565) return Dirt;

            return Grass;
        }

        /// <summary>Elevation used for drawing.</summary>
        public static int RenderHeight(int x, int y)
        {
            return HeightAt(x, y);
        }

        /// <summary>
        /// Colour for a tile, shaded by the slope of its surface.
        /// </summary>
        /// <remarks>
        /// The slope term is the whole reason the terrain reads as three-dimensional: a face
        /// turned away from the light is darker, so a hill gets a lit side and a shadowed
        /// side even though the base colour is identical.
        /// </remarks>
        public static int ColourFor(int type, int x, int y, int h00, int h10, int h01, int h11)
        {
            double jitter = (Hash(x * 7 + 11, y * 13 + 3) - 0.5) * 0.10;

            int r, g, b;

            switch (type)
            {
                case Water:
                    r = 44; g = 84; b = 126;
                    break;

                case Sand:
                    r = 190; g = 174; b = 120;
                    break;

                case Dirt:
                    r = 132; g = 112; b = 82;
                    break;

                case Rock:
                    if (h00 + h10 + h01 + h11 >= 42) { r = 176; g = 178; b = 184; }
                    else { r = 120; g = 120; b = 124; }
                    break;

                default:
                    r = 84; g = 124; b = 58;
                    break;
            }

            r = Clamp(r + (int)(jitter * 40));
            g = Clamp(g + (int)(jitter * 44));
            b = Clamp(b + (int)(jitter * 30));

            int baseColour = Framebuffer.Rgb(r, g, b);

            // Slope shading: how much this surface leans away from a light coming from the
            // upper left. The gradient across the tile is a good enough normal for flat
            // shading, and it is free.
            int dx = (h10 + h11) - (h00 + h01);
            int dy = (h01 + h11) - (h00 + h10);

            double light = 0.0 - dx * 0.030 - dy * 0.045;
            if (light > 0.22) light = 0.22;
            if (light < -0.30) light = -0.30;

            return Shade(baseColour, light);
        }

        /// <summary>Darkens (negative) or lightens (positive) a packed colour by a fraction.</summary>
        public static int Shade(int argb, double amount)
        {
            if (amount == 0) return argb;

            int r = (argb >> 16) & 0xFF;
            int g = (argb >> 8) & 0xFF;
            int b = argb & 0xFF;

            if (amount >= 0)
            {
                r = Clamp((int)(r + (255 - r) * amount));
                g = Clamp((int)(g + (255 - g) * amount));
                b = Clamp(b + (int)((255 - b) * amount));
            }
            else
            {
                double factor = 1.0 + amount;
                r = Clamp((int)(r * factor));
                g = Clamp((int)(g * factor));
                b = Clamp((int)(b * factor));
            }

            return Framebuffer.Rgb(r, g, b);
        }

        private static int Clamp(int value)
        {
            if (value < 0) return 0;
            if (value > 255) return 255;
            return value;
        }

        /// <summary>True when a tree stands on this tile. Decorative, stable, and off the spawn.</summary>
        public static bool HasTree(int x, int y)
        {
            int type = TypeAt(x, y);
            if (type != Grass) return false;

            if (NearSpawn(x, y)) return false;

            // Denser than the flat version was: a world without trees reads as a desert.
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
        private static bool NearSpawn(int x, int y)
        {
            int dx = x - 3222;
            int dy = y - 3218;
            return dx * dx + dy * dy < 36;
        }
    }

    /// <summary>
    /// Isometric projection: turns a world corner and an elevation into a screen position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A 2:1 isometric projection, the shape the original uses: tiles are diamonds twice as
    /// wide as they are tall, and elevation lifts a point straight up the screen. The camera
    /// is orthographic, so a tile's size never changes with distance — that is what makes the
    /// world read as the familiar isometric plane rather than a perspective corridor.
    /// </para>
    /// <para>
    /// The camera rotates in quarter turns. Rotation is applied before projection, which
    /// keeps the projection itself trivial and makes the inverse (needed for click-to-walk)
    /// a two-line calculation.
    /// </para>
    /// </remarks>
    public struct IsoProjection
    {
        /// <summary>Half the on-screen width of a tile.</summary>
        public const int HalfWidth = 32;

        /// <summary>Half the on-screen height of a tile.</summary>
        public const int HalfHeight = 16;

        /// <summary>Pixels of vertical lift per height unit.</summary>
        /// <remarks>
        /// This is the number that decides whether the terrain reads as hilly or flat. At 7
        /// pixels against a 32-pixel tile, a four-unit step lifted a corner by 28 pixels —
        /// about one tile row — which is barely perceptible. The original uses roughly a
        /// third of a tile per height unit, and it looks right.
        /// </remarks>
        public const int HeightStep = 16;

        /// <summary>Screen position the camera tile projects to.</summary>
        public int OriginX;
        public int OriginY;

        /// <summary>Camera position, already rotated into view space.</summary>
        public double CameraX;
        public double CameraY;

        /// <summary>Quarter turns, 0..3.</summary>
        public int Rotation;

        /// <summary>Rotates a world coordinate into view space.</summary>
        public static void Rotate(double x, double y, int rotation, out double vx, out double vy)
        {
            switch (rotation & 3)
            {
                case 1: vx = y; vy = -x; break;
                case 2: vx = -x; vy = -y; break;
                case 3: vx = -y; vy = x; break;
                default: vx = x; vy = y; break;
            }
        }

        /// <summary>Rotates a view coordinate back into world space.</summary>
        public static void Unrotate(double vx, double vy, int rotation, out double x, out double y)
        {
            switch (rotation & 3)
            {
                case 1: x = -vy; y = vx; break;
                case 2: x = -vx; y = -vy; break;
                case 3: x = vy; y = -vx; break;
                default: x = vx; y = vy; break;
            }
        }

        /// <summary>Converts a view-space direction to the world offset it corresponds to.</summary>
        public static void ViewDirectionToWorld(int rotation, int dvx, int dvy, out int dx, out int dy)
        {
            switch (rotation & 3)
            {
                case 1: dx = -dvy; dy = dvx; break;
                case 2: dx = -dvx; dy = -dvy; break;
                case 3: dx = dvy; dy = -dvx; break;
                default: dx = dvx; dy = dvy; break;
            }
        }

        /// <summary>Screen X of a view-space point.</summary>
        public int ScreenX(double vx, double vy)
        {
            return OriginX + (int)Math.Round((vx - CameraX - (vy - CameraY)) * HalfWidth);
        }

        /// <summary>Screen Y of a view-space point at the given elevation.</summary>
        public int ScreenY(double vx, double vy, int height)
        {
            return OriginY + (int)Math.Round((vx - CameraX + (vy - CameraY)) * HalfHeight) - height * HeightStep;
        }

        /// <summary>Projects a world point with elevation to a screen point.</summary>
        public void Project(double worldX, double worldY, int height, out int sx, out int sy)
        {
            double vx, vy;
            Rotate(worldX, worldY, Rotation, out vx, out vy);

            sx = ScreenX(vx, vy);
            sy = ScreenY(vx, vy, height);
        }

        /// <summary>
        /// Converts a screen point back to the world tile under it.
        /// </summary>
        /// <remarks>
        /// Elevation shifts a point up the screen, so a single unprojection lands on the tile
        /// that would be there at height zero. Two refinement passes against the real terrain
        /// height are enough for any click a player can make, because elevation is bounded.
        /// </remarks>
        public void ScreenToTile(int screenX, int screenY, out int tileX, out int tileY)
        {
            UnprojectRaw(screenX, screenY, 0, out tileX, out tileY);

            for (int pass = 0; pass < 2; pass++)
            {
                int height = Terrain.RenderHeight(tileX, tileY);
                if (height == 0) break;

                int rx, ry;
                UnprojectRaw(screenX, screenY, height, out rx, out ry);

                if (rx == tileX && ry == tileY) break;

                tileX = rx;
                tileY = ry;
            }
        }

        private void UnprojectRaw(int screenX, int screenY, int height, out int tileX, out int tileY)
        {
            // dx - dy = a, dx + dy = b  =>  dx = (a + b) / 2, dy = (b - a) / 2
            double a = (double)(screenX - OriginX) / HalfWidth;
            double b = (double)(screenY - OriginY + height * HeightStep) / HalfHeight;

            double vx = CameraX + (a + b) * 0.5;
            double vy = CameraY + (b - a) * 0.5;

            double wx, wy;
            Unrotate(vx, vy, Rotation, out wx, out wy);

            tileX = (int)Math.Floor(wx);
            tileY = (int)Math.Floor(wy);
        }
    }
}
