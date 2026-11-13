using System;
using DartScape.Game;

namespace DartScape.Client.Rendering
{
    /// <summary>What kind of scenery stands on a tile.</summary>
    /// <remarks>
    /// This is a rendering classification, not a game one: the cache describes objects by
    /// definition id and name, and the renderer only needs to know which placeholder shape to
    /// draw until real object models are loaded.
    /// </remarks>
    public enum PropKind
    {
        None = 0,
        Tree,
        Rock,
        Wall
    }

    /// <summary>One piece of scenery, as the renderer needs it.</summary>
    public struct PropInfo
    {
        public PropKind Kind;

        /// <summary>Wall face, 0..3. Unused for trees and rocks.</summary>
        public int Rotation;

        /// <summary>Footprint width, in tiles.</summary>
        public int SizeX;

        /// <summary>Footprint depth, in tiles.</summary>
        public int SizeY;
    }

    /// <summary>
    /// Terrain and scenery as the renderer sees them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape of the world comes from the cache through <see cref="Map"/>, and this class
    /// turns it into the two things drawing needs: a height in render units and a colour.
    /// </para>
    /// <para>
    /// Heights are converted once, here. The cache stores elevation in its own units, where sea
    /// level is 0 and a hill is around 60; <see cref="IsoProjection.HeightStep"/> pixels per
    /// unit is calibrated so those numbers land where the original client would put them. The
    /// divisor is not arbitrary: the cache's own vertex heights are eight times its elevation,
    /// and matching the original's proportions means eight elevation units per height step.
    /// </para>
    /// </remarks>
    public static class Terrain
    {
        /// <summary>Elevation units per render height step.</summary>
        public const double UnitsPerHeightStep = 8.0;

        /// <summary>Terrain classes of the procedural fallback.</summary>
        public const int Water = WorldTerrain.Water;
        public const int Sand = WorldTerrain.Sand;
        public const int Grass = WorldTerrain.Grass;
        public const int Dirt = WorldTerrain.Dirt;
        public const int Rock = WorldTerrain.Rock;

        public const int MaxHeight = WorldTerrain.MaxHeight;

        /// <summary>
        /// The real world map, or <c>null</c> when no cache is available.
        /// </summary>
        /// <remarks>
        /// Set once at startup. When it is null everything falls back to the procedural
        /// heightfield, which keeps the client usable without a cache instead of showing a
        /// blank screen.
        /// </remarks>
        public static WorldMap Map;

        private const int SeaColour = 0x2E5C8A;
        private const int UnknownColour = 0x6E6E64;

        /// <summary>True when the real map is loaded.</summary>
        public static bool HasRealMap
        {
            get { return Map != null; }
        }

        /// <summary>
        /// Height of a grid corner, in render units.
        /// </summary>
        /// <remarks>
        /// Corners are shared by up to four tiles, which is what makes the ground continuous
        /// instead of a staircase.
        /// </remarks>
        public static double CornerHeight(int cx, int cy)
        {
            if (Map == null) return WorldTerrain.CornerHeight(cx, cy);

            return Map.CornerHeight(cx, cy) / UnitsPerHeightStep;
        }

        /// <summary>Height of a tile's south-west corner, in render units.</summary>
        public static double HeightAt(int x, int y)
        {
            return CornerHeight(x, y);
        }

        /// <summary>
        /// Elevation at a fractional world position, interpolated between corners.
        /// </summary>
        /// <remarks>
        /// Sampling the height of the tile an entity stands on makes it jump a whole height
        /// unit the instant it crosses a tile boundary on a slope, which is very visible.
        /// </remarks>
        public static double SmoothHeight(double x, double y)
        {
            int x0 = (int)Math.Floor(x);
            int y0 = (int)Math.Floor(y);

            double tx = x - x0;
            double ty = y - y0;

            double h00 = CornerHeight(x0, y0);
            double h10 = CornerHeight(x0 + 1, y0);
            double h01 = CornerHeight(x0, y0 + 1);
            double h11 = CornerHeight(x0 + 1, y0 + 1);

            double top = h00 + (h10 - h00) * tx;
            double bottom = h01 + (h11 - h01) * tx;

            return top + (bottom - top) * ty;
        }

        /// <summary>Terrain class of a tile, used only by the procedural fallback.</summary>
        public static int TypeAt(int x, int y)
        {
            return WorldTerrain.TypeAt(x, y);
        }

        /// <summary>True when the tile is water.</summary>
        public static bool IsWater(int x, int y)
        {
            if (Map == null) return WorldTerrain.TypeAt(x, y) == WorldTerrain.Water;

            return Map.GetTile(x, y).Water;
        }

        /// <summary>
        /// Packed colour of a tile.
        /// </summary>
        /// <remarks>
        /// The cache does not store a colour per tile. It stores two floor ids, and each floor
        /// is one of a hundred-odd definitions with a colour. That is why the world reads as
        /// coherent: there are only a handful of distinct surfaces, and they repeat.
        /// </remarks>
        public static int ColourAt(int x, int y)
        {
            if (Map == null)
            {
                return ColourFor(WorldTerrain.TypeAt(x, y), x, y,
                    WorldTerrain.CornerHeight(x, y), WorldTerrain.CornerHeight(x + 1, y),
                    WorldTerrain.CornerHeight(x, y + 1), WorldTerrain.CornerHeight(x + 1, y + 1));
            }

            TileInfo tile = Map.GetTile(x, y);

            if (!tile.Exists) return Palette.TerrainColour(UnknownColour);

            if (tile.Water) return Shade(Palette.TerrainColour(SeaColour), Jitter(x, y, 0.05));

            int colour = FloorColour(tile.Overlay);
            if (colour == 0) colour = FloorColour(tile.Underlay);
            if (colour == 0) colour = Palette.TerrainColour(UnknownColour);

            return Shade(colour, Jitter(x, y, 0.04));
        }

        /// <summary>
        /// Colour of one floor definition, already put through the original's palette.
        /// </summary>
        /// <remarks>
        /// The cache stores a floor as plain <c>0xRRGGBB</c>, but the client does not draw that
        /// value: it converts it to HSL, scales the lightness to three-quarters, and looks the
        /// result up in a gamma-corrected palette. Doing the same is what makes the ground read
        /// as RuneScape rather than as flat blocks — the gamma lifts the midtones and the
        /// lightness scaling keeps the floor behind the scenery instead of competing with it.
        /// </remarks>
        private static int FloorColour(int floorId)
        {
            if (floorId <= 0 || Map == null) return 0;

            // Tile floor ids are ONE-based into flo.dat: the client looks them up as
            // Flo.cache[id - 1], and id 0 means "no floor". Reading them as zero-based shifts
            // every surface by one entry, which is invisible until it turns the grey road into
            // the black dungeon stone — the two happen to sit next to each other in the table.
            Core.Cache.FloorDefinition floor = Map.Floors.Get(floorId - 1);
            if (floor == null) return 0;

            if (floor.IsWater) return Palette.TerrainColour(SeaColour);

            if (floor.Colour != 0) return Palette.TerrainColour(floor.Colour);

            // A handful of floors are texture-only and carry no colour, because the client
            // paints them with a bitmap. Without the texture loader, a plausible surface
            // colour derived from the texture id is better than black.
            if (floor.TextureId >= 0)
            {
                int shade = 74 + (floor.TextureId * 37 % 48);
                return Palette.TerrainColour((shade << 16) | ((shade - 6) << 8) | (shade - 18));
            }

            return Palette.TerrainColour(UnknownColour);
        }

        /// <summary>
        /// Scenery on a tile, if the renderer has a placeholder for it.
        /// </summary>
        /// <remarks>
        /// The cache gives every object a name, and that is enough to tell a tree from a rock
        /// without loading a single model. Walls are recognised by their config type instead,
        /// because they have no useful name and their shape is what matters.
        /// </remarks>
        public static bool TryGetProp(int x, int y, out PropInfo prop)
        {
            prop = new PropInfo();

            if (Map == null)
            {
                if (WorldTerrain.HasTree(x, y))
                {
                    prop.Kind = PropKind.Tree;
                    prop.SizeX = 1;
                    prop.SizeY = 1;
                    return true;
                }

                if (WorldTerrain.HasBoulder(x, y))
                {
                    prop.Kind = PropKind.Rock;
                    prop.SizeX = 1;
                    prop.SizeY = 1;
                    return true;
                }

                return false;
            }

            System.Collections.Generic.List<Core.Cache.LandscapeObject> objects = Map.ObjectsAt(x, y);
            if (objects == null) return false;

            PropKind best = PropKind.None;
            int bestRotation = 0;
            int bestSizeX = 1, bestSizeY = 1;

            for (int i = 0; i < objects.Count; i++)
            {
                Core.Cache.LandscapeObject placed = objects[i];
                if (placed.Plane != 0) continue;

                PropKind kind = Classify(placed);

                // Trees and rocks beat walls, because a tree's canopy covers the wall behind
                // it. Within the same kind the first placement wins, which is the cache's own
                // ordering.
                if (kind == PropKind.None) continue;
                if (best != PropKind.None && kind != PropKind.Tree && kind != PropKind.Rock) continue;
                if (best == PropKind.Tree || best == PropKind.Rock) continue;

                best = kind;
                bestRotation = placed.Rotation;

                Core.Cache.ObjectDefinition definition = Map.Objects.Get(placed.Id);
                if (definition != null)
                {
                    bestSizeX = definition.SizeX;
                    bestSizeY = definition.SizeY;
                }
            }

            if (best == PropKind.None) return false;

            prop.Kind = best;
            prop.Rotation = bestRotation;
            prop.SizeX = bestSizeX;
            prop.SizeY = bestSizeY;
            return true;
        }

        private static PropKind Classify(Core.Cache.LandscapeObject placed)
        {
            if (placed.Type >= 0 && placed.Type <= 3) return PropKind.Wall;
            if (placed.Type == 4 || placed.Type == 5 || placed.Type == 6 ||
                placed.Type == 7 || placed.Type == 8) return PropKind.None;

            Core.Cache.ObjectDefinition definition = Map.Objects.Get(placed.Id);
            if (definition == null || definition.Name == null) return PropKind.None;

            string name = definition.Name;

            if (Contains(name, "tree") || Contains(name, "oak") || Contains(name, "willow") ||
                Contains(name, "maple") || Contains(name, "yew") || Contains(name, "magic"))
            {
                return PropKind.Tree;
            }

            if (Contains(name, "rock") || Contains(name, "boulder") || Contains(name, "ore") ||
                Contains(name, "rocks"))
            {
                return PropKind.Rock;
            }

            return PropKind.None;
        }

        private static bool Contains(string text, string what)
        {
            return text.IndexOf(what, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Colour for a procedural tile, used when there is no cache.</summary>
        public static int ColourFor(int type, int x, int y, int h00, int h10, int h01, int h11)
        {
            int r, g, b;

            switch (type)
            {
                case WorldTerrain.Water: r = 44; g = 84; b = 126; break;
                case WorldTerrain.Sand: r = 190; g = 174; b = 120; break;
                case WorldTerrain.Dirt: r = 132; g = 112; b = 82; break;
                case WorldTerrain.Rock:
                    if (h00 + h10 + h01 + h11 >= 42) { r = 176; g = 178; b = 184; }
                    else { r = 120; g = 120; b = 124; }
                    break;
                default: r = 84; g = 124; b = 58; break;
            }

            double jitter = (Hash(x * 7 + 11, y * 13 + 3) - 0.5) * 0.10;

            r = Clamp(r + (int)(jitter * 40));
            g = Clamp(g + (int)(jitter * 44));
            b = Clamp(b + (int)(jitter * 30));

            return Framebuffer.Rgb(r, g, b);
        }

        /// <summary>Darkens (negative) or lightens (positive) a packed colour by a fraction.</summary>
        /// <remarks>
        /// The result is always opaque ARGB, including when the amount is zero. Returning the
        /// input unchanged in that case looks like a harmless optimisation and is not: a caller
        /// that passes a bare <c>0xRRGGBB</c> gets a pixel with a zero alpha byte back, which the
        /// framebuffer stores as fully transparent. In a saved PNG that is white, so a wall that
        /// skipped shading appeared as a blank white bar with no error anywhere.
        /// </remarks>
        public static int Shade(int argb, double amount)
        {
            int r = (argb >> 16) & 0xFF;
            int g = (argb >> 8) & 0xFF;
            int b = argb & 0xFF;

            if (amount > 0)
            {
                r = Clamp((int)(r + (255 - r) * amount));
                g = Clamp((int)(g + (255 - g) * amount));
                b = Clamp(b + (int)((255 - b) * amount));
            }
            else if (amount < 0)
            {
                double factor = 1.0 + amount;
                r = Clamp((int)(r * factor));
                g = Clamp((int)(g * factor));
                b = Clamp((int)(b * factor));
            }

            return Framebuffer.Rgb(r, g, b);
        }

        private static double Jitter(int x, int y, double amount)
        {
            return (Hash(x * 3 + 17, y * 5 + 29) - 0.5) * amount;
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
        /// One render height unit is eight cache elevation units, and at sixteen pixels per
        /// unit a hill of elevation 60 lifts by 120 pixels — a little under four tile heights,
        /// which is the proportion the original client gives it.
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
                double height = Terrain.HeightAt(tileX, tileY);
                if (height == 0) break;

                int rx, ry;
                UnprojectRaw(screenX, screenY, height, out rx, out ry);

                if (rx == tileX && ry == tileY) break;

                tileX = rx;
                tileY = ry;
            }
        }

        private void UnprojectRaw(int screenX, int screenY, double height, out int tileX, out int tileY)
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
