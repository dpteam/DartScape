using System;
using System.Collections.Generic;
using DartScape.Client.Rendering;

namespace DartScape.Client.Rendering
{
    /// <summary>
    /// Draws the world: terrain, entities, ground items and overhead text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The view is a top-down tile grid with billboarded entities. That is a deliberate
    /// simplification of the original's 3D scene: it needs no model data, no texture
    /// archive and no camera maths, so it runs on any XP-era machine and it is honest
    /// about what it is. The scene pipeline in <c>docs/PIPELINE.md</c> describes what has
    /// to be built to replace it.
    /// </para>
    /// <para>
    /// Terrain colour is derived from a deterministic hash of the tile coordinate, which
    /// produces stable, plausible grass/dirt/water patches without shipping a map file.
    /// The same hash is used by the minimap, so the two always agree.
    /// </para>
    /// </remarks>
    public sealed class WorldRenderer
    {
        /// <summary>Pixels per tile.</summary>
        public const int TileSize = 32;

        private readonly FontRenderer _text;

        private static readonly int GrassDark = Framebuffer.Rgb(58, 94, 42);
        private static readonly int GrassLight = Framebuffer.Rgb(74, 116, 52);
        private static readonly int Dirt = Framebuffer.Rgb(120, 98, 62);
        private static readonly int DirtDark = Framebuffer.Rgb(100, 80, 50);
        private static readonly int Water = Framebuffer.Rgb(44, 78, 122);
        private static readonly int WaterLight = Framebuffer.Rgb(58, 96, 146);
        private static readonly int Stone = Framebuffer.Rgb(112, 112, 118);
        private static readonly int Sand = Framebuffer.Rgb(178, 160, 108);

        public WorldRenderer(FontRenderer text)
        {
            _text = text;
        }

        /// <summary>Terrain type at a world tile. Stable for a given coordinate.</summary>
        public enum Terrain
        {
            Grass = 0,
            Dirt = 1,
            Water = 2,
            Stone = 3,
            Sand = 4
        }

        /// <summary>Deterministic terrain lookup, shared by the world view and the minimap.</summary>
        public static Terrain TerrainAt(int x, int y)
        {
            // A cheap integer hash with a low-frequency component so patches are large
            // enough to look like terrain rather than noise.
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663);
            h ^= h >> 13;
            h *= unchecked((int)0x5bd1e995);
            h ^= h >> 15;

            int coarse = (int)(h & 0xFF);

            // Water along the edges of a large region, so the map reads as a place.
            int regionX = ((x % 128) + 128) % 128;
            int regionY = ((y % 128) + 128) % 128;
            if (regionX < 6 || regionY < 6 || regionX > 121 || regionY > 121) return Terrain.Water;

            if (coarse < 12) return Terrain.Dirt;
            if (coarse < 18) return Terrain.Stone;
            if (coarse < 22) return Terrain.Sand;
            return Terrain.Grass;
        }

        private static int ColourFor(Terrain terrain, int x, int y)
        {
            // A per-tile shade wobble keeps large flat areas from looking like a texture bug.
            int wobble = (int)(((x * 31 + y * 17) & 3) - 1) * 4;

            switch (terrain)
            {
                case Terrain.Dirt:
                    return Shift(Dirt, wobble);
                case Terrain.Water:
                    return Shift(WaterLight, wobble / 2);
                case Terrain.Stone:
                    return Shift(Stone, wobble);
                case Terrain.Sand:
                    return Shift(Sand, wobble);
                default:
                    return Shift((x + y) % 2 == 0 ? GrassDark : GrassLight, wobble);
            }
        }

        private static int Shift(int argb, int amount)
        {
            int r = Math.Max(0, Math.Min(255, ((argb >> 16) & 0xFF) + amount));
            int g = Math.Max(0, Math.Min(255, ((argb >> 8) & 0xFF) + amount));
            int b = Math.Max(0, Math.Min(255, (argb & 0xFF) + amount));
            return Framebuffer.Rgb(r, g, b);
        }

        /// <summary>Draws the world view into <paramref name="target"/>.</summary>
        public void Draw(Framebuffer target, ClientWorld world, int viewX, int viewY, int viewWidth, int viewHeight,
            double interpolation)
        {
            if (target == null || world == null) return;

            // Centre the camera on the player, offset by the camera nudge.
            double cameraX = world.MyX + world.CameraOffsetX;
            double cameraY = world.MyY + world.CameraOffsetY;

            int tilesX = viewWidth / TileSize + 3;
            int tilesY = viewHeight / TileSize + 3;

            int originTileX = (int)Math.Floor(cameraX) - tilesX / 2;
            int originTileY = (int)Math.Floor(cameraY) - tilesY / 2;

            // The fractional part of the camera position becomes a sub-tile offset, which
            // is what makes walking look smooth instead of one tile per 600 ms.
            double fracX = cameraX - Math.Floor(cameraX);
            double fracY = cameraY - Math.Floor(cameraY);

            int originPixelX = viewX - (int)(fracX * TileSize);
            int originPixelY = viewY - (int)(fracY * TileSize);

            // --- terrain ------------------------------------------------------
            for (int ty = 0; ty < tilesY; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    int worldX = originTileX + tx;
                    int worldY = originTileY + ty;

                    Terrain terrain = TerrainAt(worldX, worldY);
                    int colour = ColourFor(terrain, worldX, worldY);

                    int px = originPixelX + tx * TileSize;
                    int py = originPixelY + ty * TileSize;

                    if (px > viewX + viewWidth || py > viewY + viewHeight) continue;
                    if (px + TileSize < viewX || py + TileSize < viewY) continue;

                    target.FillRect(px, py, TileSize, TileSize, colour);

                    // A one-pixel darker edge gives the grid definition without looking
                    // like a debug overlay.
                    if (terrain == Terrain.Water)
                    {
                        target.FillRect(px, py, TileSize, 1, Shift(colour, 12));
                    }
                    else
                    {
                        target.FillRect(px, py + TileSize - 1, TileSize, 1, Shift(colour, -10));
                    }
                }
            }

            // --- ground items -------------------------------------------------
            for (int i = 0; i < world.GroundItems.Count; i++)
            {
                ClientGroundItem item = world.GroundItems[i];
                if (item.Plane != world.MyPlane) continue;

                int px = WorldToScreenX(item.X, item.Y, originTileX, originTileY, originPixelX, originPixelY);
                int py = WorldToScreenY(item.X, item.Y, originTileX, originTileY, originPixelX, originPixelY);

                if (!Visible(px, py, viewX, viewY, viewWidth, viewHeight, 40)) continue;

                // A small gold diamond, readable at a glance and obviously "an item".
                int cx = px + TileSize / 2;
                int cy = py + TileSize / 2;

                target.FillCircle(cx, cy + 6, 4, Framebuffer.Argb(90, 0, 0, 0));
                DrawDiamond(target, cx, cy, 6, Framebuffer.Rgb(226, 190, 90));

                if (item.Amount > 1)
                {
                    _text.Draw(target, item.Amount.ToString(), cx + 6, cy - 6, unchecked((int)0xFFFFFF00), FontRenderer.Size.Small);
                }
            }

            // --- entities -----------------------------------------------------
            // Drawn in Y order so a character standing "behind" another overlaps correctly.
            var drawables = new List<Drawable>(64);

            for (int i = 0; i < world.Npcs.Length; i++)
            {
                ClientNpc npc = world.Npcs[i];
                if (npc == null || npc.Plane != world.MyPlane) continue;
                drawables.Add(new Drawable { Npc = npc, SortY = npc.Y * 1000 + npc.X });
            }

            for (int i = 0; i < world.Players.Length; i++)
            {
                ClientPlayer player = world.Players[i];
                if (player == null || player.Plane != world.MyPlane) continue;
                drawables.Add(new Drawable { Player = player, SortY = player.Y * 1000 + player.X });
            }

            drawables.Sort((a, b) => a.SortY.CompareTo(b.SortY));

            foreach (Drawable drawable in drawables)
            {
                if (drawable.Player != null)
                {
                    DrawPlayer(target, world, drawable.Player, originTileX, originTileY,
                        originPixelX, originPixelY, viewX, viewY, viewWidth, viewHeight, interpolation);
                }
                else
                {
                    DrawNpc(target, world, drawable.Npc, originTileX, originTileY,
                        originPixelX, originPixelY, viewX, viewY, viewWidth, viewHeight, interpolation);
                }
            }
        }

        private sealed class Drawable
        {
            public ClientPlayer Player;
            public ClientNpc Npc;
            public int SortY;
        }

        private static bool Visible(int px, int py, int viewX, int viewY, int viewWidth, int viewHeight, int margin)
        {
            return px >= viewX - margin && px <= viewX + viewWidth + margin &&
                   py >= viewY - margin && py <= viewY + viewHeight + margin;
        }

        private static int WorldToScreenX(int x, int y, int originTileX, int originTileY, int originPixelX, int originPixelY)
        {
            return originPixelX + (x - originTileX) * TileSize;
        }

        private static int WorldToScreenY(int x, int y, int originTileX, int originTileY, int originPixelX, int originPixelY)
        {
            return originPixelY + (y - originTileY) * TileSize;
        }

        private static void DrawDiamond(Framebuffer target, int cx, int cy, int radius, int argb)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                int span = radius - Math.Abs(dy);
                target.FillRect(cx - span, cy + dy, span * 2 + 1, 1, argb);
            }
        }

        private void DrawPlayer(Framebuffer target, ClientWorld world, ClientPlayer player,
            int originTileX, int originTileY, int originPixelX, int originPixelY,
            int viewX, int viewY, int viewWidth, int viewHeight, double interpolation)
        {
            // Interpolate between the previous tile and the current one so movement is
            // smooth rather than a jump every tick.
            double px = player.LastX + (player.X - player.LastX) * interpolation;
            double py = player.LastY + (player.Y - player.LastY) * interpolation;

            int screenX = originPixelX + (int)((px - originTileX) * TileSize);
            int screenY = originPixelY + (int)((py - originTileY) * TileSize);

            if (!Visible(screenX, screenY, viewX, viewY, viewWidth, viewHeight, 60)) return;

            int cx = screenX + TileSize / 2;
            int baseY = screenY + TileSize - 2;

            DrawHumanoid(target, cx, baseY, player.Colours, player.EquipmentIds, player.Skulled, player.IsLocal);

            // Name and combat level.
            string label = player.Name;
            if (player.CombatLevel > 0) label = player.Name + " (" + player.CombatLevel + ")";

            int labelColour = player.Privilege >= 2 ? unchecked((int)0xFFFF5555)
                : player.Privilege == 1 ? unchecked((int)0xFF55FF55)
                : unchecked((int)0xFFFFFFFF);

            _text.DrawCentred(target, label, cx - 60, baseY - 56, 120, labelColour, FontRenderer.Size.Small);

            DrawOverhead(target, player.OverheadText, player.OverheadUntilMs, cx, baseY - 70);
            DrawHitBar(target, player.Hitpoints, player.MaxHitpoints, player.DamageToShow, cx, baseY - 46);
        }

        private void DrawNpc(Framebuffer target, ClientWorld world, ClientNpc npc,
            int originTileX, int originTileY, int originPixelX, int originPixelY,
            int viewX, int viewY, int viewWidth, int viewHeight, double interpolation)
        {
            double px = npc.LastX + (npc.X - npc.LastX) * interpolation;
            double py = npc.LastY + (npc.Y - npc.LastY) * interpolation;

            int screenX = originPixelX + (int)((px - originTileX) * TileSize);
            int screenY = originPixelY + (int)((py - originTileY) * TileSize);

            if (!Visible(screenX, screenY, viewX, viewY, viewWidth, viewHeight, 60)) return;

            int width = npc.Size * TileSize;
            int cx = screenX + width / 2;
            int baseY = screenY + width - 2;

            // NPCs get a simple silhouette tinted by their combat level: harmless things
            // are calm greens, dangerous things are warm reds.
            int body = npc.CombatLevel <= 0 ? Framebuffer.Rgb(150, 160, 170)
                : npc.CombatLevel < 20 ? Framebuffer.Rgb(120, 170, 110)
                : npc.CombatLevel < 60 ? Framebuffer.Rgb(190, 160, 90)
                : Framebuffer.Rgb(190, 90, 80);

            DrawHumanoid(target, cx, baseY, null, null, false, false, body);

            string label = npc.Name;
            if (npc.CombatLevel > 0) label = npc.Name + " (" + npc.CombatLevel + ")";

            _text.DrawCentred(target, label, cx - 60, baseY - 56, 120, unchecked((int)0xFFFFFF66), FontRenderer.Size.Small);

            DrawOverhead(target, npc.OverheadText, npc.OverheadUntilMs, cx, baseY - 70);
            DrawHitBar(target, npc.Hitpoints, npc.MaxHitpoints, npc.DamageToShow, cx, baseY - 46);
        }

        /// <summary>
        /// A small stylised humanoid: shadow, legs, torso, arms, head.
        /// </summary>
        /// <remarks>
        /// Without model data this is the honest option, and it reads well: silhouette and
        /// colour carry the information the player needs (who, friend or foe, what they
        /// are wearing) without pretending to be a 3D render.
        /// </remarks>
        private void DrawHumanoid(Framebuffer target, int cx, int baseY, int[] colours, int[] equipment,
            bool skulled, bool isLocal, int? overrideBody = null)
        {
            int height = 46;
            int headRadius = 7;
            int bodyWidth = 16;

            int top = baseY - height;

            // Shadow.
            target.FillCircle(cx, baseY, 9, Framebuffer.Argb(80, 0, 0, 0));

            int torso = overrideBody ?? (colours != null ? ShirtColour(colours) : Framebuffer.Rgb(90, 110, 150));
            int legs = overrideBody ?? (colours != null ? TrouserColour(colours) : Framebuffer.Rgb(70, 70, 90));
            int skin = colours != null ? SkinColour(colours) : Framebuffer.Rgb(224, 186, 148);
            int hair = colours != null ? HairColour(colours) : Framebuffer.Rgb(80, 55, 35);

            // Legs.
            target.FillRect(cx - 6, top + height - 16, 5, 16, legs);
            target.FillRect(cx + 1, top + height - 16, 5, 16, legs);

            // Torso.
            target.FillRect(cx - bodyWidth / 2, top + 16, bodyWidth, 20, torso);

            // Arms.
            target.FillRect(cx - bodyWidth / 2 - 4, top + 17, 4, 15, skin);
            target.FillRect(cx + bodyWidth / 2, top + 17, 4, 15, skin);

            // Head and hair.
            target.FillCircle(cx, top + 9, headRadius, skin);
            target.FillRect(cx - headRadius, top + 3, headRadius * 2, 5, hair);

            // Weapon, if one is worn: a short blade at the right hand.
            if (equipment != null && equipment.Length > 3 && equipment[3] > 0)
            {
                target.FillRect(cx + bodyWidth / 2 + 3, top + 6, 2, 22, Framebuffer.Rgb(200, 205, 215));
                target.FillRect(cx + bodyWidth / 2 + 2, top + 24, 4, 3, Framebuffer.Rgb(120, 90, 50));
            }

            if (skulled)
            {
                target.FillRect(cx - 4, top + 6, 3, 3, Framebuffer.Rgb(220, 40, 40));
                target.FillRect(cx + 2, top + 6, 3, 3, Framebuffer.Rgb(220, 40, 40));
            }

            if (isLocal)
            {
                // A subtle marker under the local player so they are never lost in a crowd.
                target.FillRect(cx - 7, baseY - 1, 14, 2, Framebuffer.Argb(160, 255, 230, 120));
            }
        }

        private static int ShirtColour(int[] colours)
        {
            return FromRsColour(colours[1]);
        }

        private static int TrouserColour(int[] colours)
        {
            return FromRsColour(colours[2]);
        }

        private static int SkinColour(int[] colours)
        {
            return FromRsColour(colours[4]);
        }

        private static int HairColour(int[] colours)
        {
            return FromRsColour(colours[0]);
        }

        /// <summary>
        /// Maps a RuneScape palette index to an RGB colour.
        /// </summary>
        /// <remarks>
        /// The real palette lives in the cache's media archive. This is a compact
        /// approximation covering the common values, which is enough for avatars to look
        /// distinct; the exact palette is a pipeline item.
        /// </remarks>
        private static int FromRsColour(int index)
        {
            int[] palette =
            {
                unchecked((int)0xFF8C5A28), unchecked((int)0xFFB08442), unchecked((int)0xFFD4B063),
                unchecked((int)0xFFFFFFFF), unchecked((int)0xFF8C6B4A), unchecked((int)0xFF6B4A2A),
                unchecked((int)0xFF3A2A1A), unchecked((int)0xFF8C3A28), unchecked((int)0xFFB04A28),
                unchecked((int)0xFFD47A42), unchecked((int)0xFF2A3A6B), unchecked((int)0xFF3A4A8C),
                unchecked((int)0xFF4A6BB0), unchecked((int)0xFF6B8CD4), unchecked((int)0xFF1A2A1A),
                unchecked((int)0xFF2A4A2A), unchecked((int)0xFF3A6B3A), unchecked((int)0xFF4A8C4A),
                unchecked((int)0xFF6BB06B), unchecked((int)0xFF8CD48C), unchecked((int)0xFF1A1A1A),
                unchecked((int)0xFF2A2A2A), unchecked((int)0xFF3A3A3A), unchecked((int)0xFF6B6B6B),
                unchecked((int)0xFF8C8C8C), unchecked((int)0xFFB0B0B0), unchecked((int)0xFFD4D4D4),
                unchecked((int)0xFFFFFFFF), unchecked((int)0xFF4A1A1A), unchecked((int)0xFF6B2A2A),
                unchecked((int)0xFF8C3A3A), unchecked((int)0xFFB04A4A)
            };

            int i = index % palette.Length;
            if (i < 0) i += palette.Length;
            return palette[i];
        }

        private void DrawOverhead(Framebuffer target, string text, int untilMs, int cx, int y)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (Environment.TickCount > untilMs) return;

            int width = (int)_text.Measure(text, FontRenderer.Size.Small).Width + 8;

            target.BlendRect(cx - width / 2, y - 2, width, 14, Framebuffer.Argb(180, 0, 0, 0));
            _text.DrawCentred(target, text, cx - width / 2, y, width, unchecked((int)0xFFFFFF00), FontRenderer.Size.Small);
        }

        private void DrawHitBar(Framebuffer target, int hitpoints, int maxHitpoints, int damage, int cx, int y)
        {
            if (maxHitpoints <= 0) return;

            // Only show a bar for something that is actually in a fight.
            if (hitpoints >= maxHitpoints && damage <= 0) return;

            int width = 30;
            int filled = (int)((double)Math.Max(0, hitpoints) / maxHitpoints * width);

            target.FillRect(cx - width / 2, y, width, 4, Framebuffer.Argb(200, 60, 0, 0));
            target.FillRect(cx - width / 2, y, filled, 4, Framebuffer.Rgb(0, 200, 60));
            target.DrawRect(cx - width / 2, y, width, 4, Framebuffer.Argb(200, 0, 0, 0));

            if (damage > 0)
            {
                _text.DrawCentred(target, "-" + damage, cx - 20, y - 16, 40, unchecked((int)0xFFFF4040), FontRenderer.Size.Bold);
            }
        }

        /// <summary>Draws the minimap into a square area.</summary>
        public void DrawMinimap(Framebuffer target, ClientWorld world, int x, int y, int size)
        {
            if (world == null) return;

            target.FillRect(x, y, size, size, Framebuffer.Rgb(20, 26, 20));

            int radius = size / 2 / 2;          // 2 pixels per tile
            int pixelsPerTile = 2;

            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dy * dy > radius * radius) continue;

                    int worldX = world.MyX + dx;
                    int worldY = world.MyY + dy;

                    Terrain terrain = TerrainAt(worldX, worldY);
                    int colour = terrain == Terrain.Water ? Framebuffer.Rgb(40, 70, 110)
                        : terrain == Terrain.Dirt ? Framebuffer.Rgb(110, 90, 60)
                        : terrain == Terrain.Stone ? Framebuffer.Rgb(100, 100, 105)
                        : terrain == Terrain.Sand ? Framebuffer.Rgb(170, 155, 105)
                        : Framebuffer.Rgb(60, 100, 50);

                    target.FillRect(x + size / 2 + dx * pixelsPerTile, y + size / 2 + dy * pixelsPerTile,
                        pixelsPerTile, pixelsPerTile, colour);
                }
            }

            // NPCs.
            for (int i = 0; i < world.Npcs.Length; i++)
            {
                ClientNpc npc = world.Npcs[i];
                if (npc == null || npc.Plane != world.MyPlane) continue;

                int dx = npc.X - world.MyX;
                int dy = npc.Y - world.MyY;
                if (dx * dx + dy * dy > radius * radius) continue;

                target.FillRect(x + size / 2 + dx * pixelsPerTile - 1, y + size / 2 + dy * pixelsPerTile - 1,
                    3, 3, npc.CombatLevel > 0 ? Framebuffer.Rgb(220, 80, 60) : Framebuffer.Rgb(200, 200, 90));
            }

            // Other players.
            for (int i = 0; i < world.Players.Length; i++)
            {
                ClientPlayer player = world.Players[i];
                if (player == null || player.IsLocal || player.Plane != world.MyPlane) continue;

                int dx = player.X - world.MyX;
                int dy = player.Y - world.MyY;
                if (dx * dx + dy * dy > radius * radius) continue;

                target.FillRect(x + size / 2 + dx * pixelsPerTile - 1, y + size / 2 + dy * pixelsPerTile - 1,
                    3, 3, Framebuffer.Rgb(90, 200, 255));
            }

            // The local player, always in the centre.
            target.FillRect(x + size / 2 - 2, y + size / 2 - 2, 4, 4, Framebuffer.Rgb(255, 255, 255));

            // Compass rose.
            _text.DrawCentred(target, "N", x, y + 2, size, unchecked((int)0xFFFFFFFF), FontRenderer.Size.Small);
            target.DrawRect(x, y, size, size, Framebuffer.Rgb(180, 170, 130));
        }
    }
}
