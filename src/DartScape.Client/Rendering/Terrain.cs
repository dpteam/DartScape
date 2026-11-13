using System;
using DartScape.Game;

namespace DartScape.Client.Rendering
{
    /// <summary>
    /// Terrain colours and shading for the renderer.
    /// </summary>
    /// <remarks>
    /// The shape of the world — heights, terrain classes, where the trees are — lives in
    /// <see cref="WorldTerrain"/> in the game layer, because the server has to agree with the
    /// client about it. This class only decides what those facts look like, which is a
    /// rendering concern and belongs here.
    /// </remarks>
    public static class Terrain
    {
        public const int Water = WorldTerrain.Water;
        public const int Sand = WorldTerrain.Sand;
        public const int Grass = WorldTerrain.Grass;
        public const int Dirt = WorldTerrain.Dirt;
        public const int Rock = WorldTerrain.Rock;

        public const int MaxHeight = WorldTerrain.MaxHeight;

        /// <summary>Elevation of a grid corner.</summary>
        public static int CornerHeight(int cx, int cy)
        {
            return WorldTerrain.CornerHeight(cx, cy);
        }

        /// <summary>Height of the tile at (x, y).</summary>
        public static int HeightAt(int x, int y)
        {
            return WorldTerrain.HeightAt(x, y);
        }

        /// <summary>
        /// Elevation at a fractional world position, interpolated between tiles.
        /// </summary>
        /// <remarks>
        /// Tile heights are whole numbers, so sampling the height of the tile an entity is
        /// standing on makes it jump a whole height unit the instant it crosses a tile boundary
        /// on a slope. At <see cref="IsoProjection.HeightStep"/> pixels per unit that is a
        /// visible hop every step, and it is one of the reasons walking on hills looked jittery.
        /// Interpolating turns the hop into a slope.
        /// </remarks>
        public static double SmoothHeight(double x, double y)
        {
            int x0 = (int)Math.Floor(x);
            int y0 = (int)Math.Floor(y);

            double tx = x - x0;
            double ty = y - y0;

            double h00 = WorldTerrain.HeightAt(x0, y0);
            double h10 = WorldTerrain.HeightAt(x0 + 1, y0);
            double h01 = WorldTerrain.HeightAt(x0, y0 + 1);
            double h11 = WorldTerrain.HeightAt(x0 + 1, y0 + 1);

            double top = h00 + (h10 - h00) * tx;
            double bottom = h01 + (h11 - h01) * tx;

            return top + (bottom - top) * ty;
        }

        /// <summary>Terrain class for a tile.</summary>
        public static int TypeAt(int x, int y)
        {
            return WorldTerrain.TypeAt(x, y);
        }

        /// <summary>Elevation used for drawing.</summary>
        public static int RenderHeight(int x, int y)
        {
            return WorldTerrain.HeightAt(x, y);
        }

        /// <summary>True when a tree stands on this tile.</summary>
        public static bool HasTree(int x, int y)
        {
            return WorldTerrain.HasTree(x, y);
        }

        /// <summary>True when a boulder stands on this tile.</summary>
        public static bool HasBoulder(int x, int y)
        {
            return WorldTerrain.HasBoulder(x, y);
        }

        /// <summary>
        /// Colour for a tile, shaded by the slope of its surface.
        /// </summary>
        /// <remarks>
        /// The slope term is the whole reason the terrain reads as three-dimensional: a face
        /// turned away from the light is darker, so a hill gets a lit side and a shadowed side
        /// even though the base colour is identical.
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

            return Framebuffer.Rgb(r, g, b);
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

        /// <summary>Hashes a coordinate, used only for colour variation.</summary>
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
    /// keeps the projection itself trivial and makes the inverse (needed for click-to-walk) a
    /// two-line calculation.
    /// </para>
    /// </remarks>
    public struct IsoProjection
    {
        /// <summary>Half the on-screen width of a tile.</summary>
        public const int HalfWidth = 32;

        /// <summary>Half the on-screen height of a tile's top face.</summary>
        public const int HalfHeight = 16;

        /// <summary>
        /// Pixels of vertical lift per height unit.
        /// </summary>
        /// <remarks>
        /// This is the number that decides whether the terrain reads as hilly or flat. At 7
        /// pixels against a 32-pixel tile a four-unit step lifted a corner by 28 pixels, about
        /// one tile row, which was barely perceptible. Roughly a third of a tile per height
        /// unit is what looks right.
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
        public int ScreenY(double vx, double vy, double height)
        {
            return OriginY
                 + (int)Math.Round((vx - CameraX + (vy - CameraY)) * HalfHeight - height * HeightStep);
        }

        /// <summary>Projects a world point with elevation to a screen point.</summary>
        public void Project(double worldX, double worldY, double height, out int sx, out int sy)
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
