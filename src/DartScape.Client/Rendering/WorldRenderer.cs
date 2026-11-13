using System;
using System.Collections.Generic;

namespace DartScape.Client.Rendering
{
    /// <summary>
    /// Draws the world as an isometric heightfield: tiles built from four corner heights,
    /// split into triangles and shaded by slope, with props and entities sorted back to
    /// front.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each tile is drawn from its four corners, not from a single height. That is what makes
    /// hills roll: a corner is shared between four tiles, so the ground is continuous and a
    /// slope is a real ramp rather than a staircase.
    /// </para>
    /// <para>
    /// Drawing order is the whole trick. Tiles are walked by increasing (viewX + viewY),
    /// which is exactly back to front for an isometric camera, and within a row from left to
    /// right. Entities are collected during the tile pass and drawn with the tile they stand
    /// on, so a character behind a hill is occluded by it.
    /// </para>
    /// </remarks>
    public sealed class WorldRenderer
    {private readonly FontRenderer _text;

        public WorldRenderer(FontRenderer text)
        {
            _text = text;
        }

        /// <summary>Draws the scene.</summary>
        public void Draw(Framebuffer target, ClientWorld world, int viewX, int viewY, int viewWidth, int viewHeight,
            double interpolation)
        {
            if (target == null || world == null) return;

            target.SetClip(viewX, viewY, viewWidth, viewHeight);

            // Sky. Only visible above the horizon where no terrain covers it.
            target.FillRect(viewX, viewY, viewWidth, viewHeight, Framebuffer.Rgb(30, 44, 64));

            // The camera follows the SMOOTH position, not the tile: snapping it to whole tiles
            // makes the entire world lurch once per tick even when the player is drawn smoothly.
            //
            // The fractional part of the camera position becomes a pixel offset on the origin,
            // while the projection itself works in whole tiles. Without this split the fraction
            // cancels out inside ScreenX and the world still snaps.
            double cameraX = world.MySmoothX + world.CameraOffsetX;
            double cameraY = world.MySmoothY + world.CameraOffsetY;

            double fracX = cameraX - Math.Floor(cameraX);
            double fracY = cameraY - Math.Floor(cameraY);

            int cameraTileX = (int)Math.Floor(cameraX);
            int cameraTileY = (int)Math.Floor(cameraY);

            double cameraHeight = Terrain.SmoothHeight(cameraX, cameraY);

            var projection = new IsoProjection
            {
                OriginX = viewX + viewWidth / 2
                          - (int)Math.Round((fracX - fracY) * IsoProjection.HalfWidth),
                OriginY = viewY + viewHeight / 2
                          - (int)Math.Round((fracX + fracY) * IsoProjection.HalfHeight)
                          + (int)Math.Round(cameraHeight * IsoProjection.HeightStep),
                CameraX = cameraTileX,
                CameraY = cameraTileY,
                Rotation = world.CameraRotation
            };

            List<EntityDrawable> entities = CollectEntities(world, interpolation);

            // --- visible tile range -------------------------------------------
            // A tile at view offset (vx, vy) lands at ((vx-vy)*HalfWidth, (vx+vy)*HalfHeight).
            // Inverting that against the viewport half-extents gives the exact range worth
            // walking, which at this size is about a tenth of a fixed radius.
            int halfW = viewWidth / 2;
            int halfH = viewHeight / 2;

            // Extra rows so tall terrain at the edge still has its walls drawn.
            int spanA = halfW / IsoProjection.HalfWidth + 2;          // |vx - vy|
            int spanB = halfH / IsoProjection.HalfHeight + 8;         // |vx + vy|

            // --- terrain pass, back to front ---------------------------------
            for (int sum = -spanB; sum <= spanB; sum++)
            {
                for (int vx = -spanA; vx <= spanA; vx++)
                {
                    int vy = sum - vx;
                    if (vy < -spanA || vy > spanA) continue;

                    DrawTile(target, world, projection, vx, vy, entities);
                }
            }

            target.ResetClip();
        }

        /// <summary>Collects players and NPCs with their interpolated world position.</summary>
        private static List<EntityDrawable> CollectEntities(ClientWorld world, double localInterpolation)
        {
            var list = new List<EntityDrawable>(48);
            long now = DartScape.Core.TimeUtil.MonotonicMs();

            for (int i = 0; i < world.Npcs.Length; i++)
            {
                ClientNpc npc = world.Npcs[i];
                if (npc == null || npc.Plane != world.MyPlane) continue;

                // Each entity interpolates against its own last move, not the local tick.
                // Interpolating a remote entity with our tick phase makes it slide backwards
                // whenever the two phases disagree, which is the jitter that was visible.
                double f = ClientWorld.EntityInterpolation(npc.MoveStartedMs, now);

                list.Add(new EntityDrawable
                {
                    Npc = npc,
                    WorldX = npc.LastX + (npc.X - npc.LastX) * f,
                    WorldY = npc.LastY + (npc.Y - npc.LastY) * f
                });
            }

            for (int i = 0; i < world.Players.Length; i++)
            {
                ClientPlayer player = world.Players[i];
                if (player == null || player.Plane != world.MyPlane) continue;

                // The local player uses the tick fraction supplied by the caller, because its
                // steps are driven locally and that fraction is in phase with them.
                double f = player.IsLocal
                    ? localInterpolation
                    : ClientWorld.EntityInterpolation(player.MoveStartedMs, now);

                list.Add(new EntityDrawable
                {
                    Player = player,
                    WorldX = player.LastX + (player.X - player.LastX) * f,
                    WorldY = player.LastY + (player.Y - player.LastY) * f
                });
            }

            return list;
        }

        private void DrawTile(Framebuffer target, ClientWorld world, IsoProjection projection,
            int vx, int vy, List<EntityDrawable> entities)
        {
            // The tile's own world coordinate, which is where its four corners meet.
            double wx0, wy0;
            IsoProjection.Unrotate(vx + projection.CameraX, vy + projection.CameraY,
                projection.Rotation, out wx0, out wy0);

            int tileX = (int)Math.Floor(wx0 + 0.5);
            int tileY = (int)Math.Floor(wy0 + 0.5);

            bool water = Terrain.IsWater(tileX, tileY);

            // Four corner heights, shared with the neighbouring tiles by construction.
            double h00 = Terrain.CornerHeight(tileX, tileY);
            double h10 = Terrain.CornerHeight(tileX + 1, tileY);
            double h01 = Terrain.CornerHeight(tileX, tileY + 1);
            double h11 = Terrain.CornerHeight(tileX + 1, tileY + 1);

            // Screen positions of the four corners. Corner (x, y) is the back one.
            // The loop walks offsets from the camera, but the projection wants absolute
            // view-space coordinates, so the camera is added back here.
            double avx = vx + projection.CameraX;
            double avy = vy + projection.CameraY;

            int x00 = projection.ScreenX(avx, avy);
            int y00 = projection.ScreenY(avx, avy, h00);

            int x10 = projection.ScreenX(avx + 1, avy);
            int y10 = projection.ScreenY(avx + 1, avy, h10);

            int x01 = projection.ScreenX(avx, avy + 1);
            int y01 = projection.ScreenY(avx, avy + 1, h01);

            int x11 = projection.ScreenX(avx + 1, avy + 1);
            int y11 = projection.ScreenY(avx + 1, avy + 1, h11);

            // Cull tiles entirely outside the viewport before any per-pixel work.
            int minX = Math.Min(Math.Min(x00, x10), Math.Min(x01, x11));
            int maxX = Math.Max(Math.Max(x00, x10), Math.Max(x01, x11));
            int minY = Math.Min(Math.Min(y00, y10), Math.Min(y01, y11));
            int maxY = Math.Max(Math.Max(y00, y10), Math.Max(y01, y11));

            // The cull has to compare against the clipped viewport, not the whole buffer:
            // the world view starts at (0,0) but the buffer is wider than it.
            if (maxX < 0 || minX > target.Width) return;
            if (maxY < 0 || minY > target.Height) return;

            int baseColour = Terrain.ColourAt(tileX, tileY);

            // Two triangles, each flat-shaded from its own slope. The split runs along the
            // diagonal that keeps the facet edges pointing the way the light does.
            int colourA = Terrain.Shade(baseColour, SlopeLight(h00, h10, h11));
            int colourB = Terrain.Shade(baseColour, SlopeLight(h00, h11, h01));

            target.FillTriangle(x00, y00, x10, y10, x11, y11, colourA);
            target.FillTriangle(x00, y00, x11, y11, x01, y01, colourB);

            // A thin darker line along the two far edges separates tiles without drawing a
            // visible grid. Water gets none: the sea reads better as one plane.
            if (!water)
            {
                int edge = Terrain.Shade(baseColour, -0.18);
                DrawLine(target, x00, y00, x10, y10, edge);
                DrawLine(target, x00, y00, x01, y01, edge);
            }

            int centreX = (x00 + x11) / 2;
            int centreY = (y00 + y11) / 2;

            // --- cliffs --------------------------------------------------------
            // A tile whose neighbour is lower has a visible vertical face between them. Without
            // it the gap shows the sky through the terrain, which reads as a hole in the world
            // rather than a cliff — and Lumbridge is built almost entirely on one.
            //
            // The face is drawn from this tile's edge down to the neighbour's height, and it is
            // drawn before the scenery so a tree at the bottom of the cliff is not covered.
            DrawCliffFaces(target, projection, tileX, tileY, avx, avy,
                x00, y00, x10, y10, x01, y01, x11, y11);

            // --- scenery -------------------------------------------------------
            // Drawn before the entities so a player standing in a doorway is not hidden by the
            // wall behind them.
            PropInfo prop;
            if (Terrain.TryGetProp(tileX, tileY, out prop))
            {
                switch (prop.Kind)
                {
                    case PropKind.Wall:
                        DrawWall(target, prop.Rotation, x00, y00, x10, y10, x01, y01, x11, y11);
                        break;

                    case PropKind.Tree:
                        DrawTree(target, centreX, centreY, tileX, tileY);
                        break;

                    case PropKind.Rock:
                        DrawBoulder(target, centreX, centreY, tileX, tileY);
                        break;
                }
            }

            // --- ground items --------------------------------------------------
            DrawGroundItems(target, world, tileX, tileY, centreX, centreY);

            // --- entities standing on this tile --------------------------------
            for (int i = 0; i < entities.Count; i++)
            {
                EntityDrawable entity = entities[i];

                if ((int)Math.Round(entity.WorldX) != tileX) continue;
                if ((int)Math.Round(entity.WorldY) != tileY) continue;

                int ex, ey;

                // The height is sampled at the entity's fractional position, not at the tile it
                // is standing on. A whole-tile height makes the sprite hop a height unit the
                // moment it crosses a tile boundary on a slope, which is very visible.
                projection.Project(entity.WorldX, entity.WorldY,
                    Terrain.SmoothHeight(entity.WorldX, entity.WorldY), out ex, out ey);

                if (entity.Player != null) DrawPlayer(target, entity.Player, ex, ey);
                else DrawNpc(target, entity.Npc, ex, ey);
            }
        }

        /// <summary>
        /// Draws the vertical faces where a tile drops to a lower neighbour.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Four edges are tested independently because a tile can be a cliff on one side and
        /// flush on another — a terrace, which is common on the Lumbridge ridge. Each face is a
        /// quad from this tile's edge down to the neighbour's height at the same corners.
        /// </para>
        /// <para>
        /// The faces are shaded darker than the top: they are vertical, so they catch less of
        /// the light. That contrast is what makes the terrain read as solid instead of painted.
        /// </para>
        /// </remarks>
        private void DrawCliffFaces(Framebuffer target, IsoProjection projection,
            int tileX, int tileY, double avx, double avy,
            int x00, int y00, int x10, int y10, int x01, int y01, int x11, int y11)
        {
            // West edge (x-1), south edge (y-1), east edge (x+1), north edge (y+1).
            DrawCliffFace(target, projection, avx, avy,
                tileX - 1, tileY, 0, 0, 0, 1, x00, y00, x01, y01, -0.34);

            DrawCliffFace(target, projection, avx, avy,
                tileX, tileY - 1, 0, 0, 1, 0, x00, y00, x10, y10, -0.46);

            DrawCliffFace(target, projection, avx, avy,
                tileX + 1, tileY, 1, 0, 1, 1, x10, y10, x11, y11, -0.28);

            DrawCliffFace(target, projection, avx, avy,
                tileX, tileY + 1, 0, 1, 1, 1, x01, y01, x11, y11, -0.40);
        }

        /// <summary>
        /// Draws one cliff face, if the neighbour really is lower.
        /// </summary>
        /// <param name="offsetAX">View offset of the face's first corner from the tile origin.</param>
        private void DrawCliffFace(Framebuffer target, IsoProjection projection, double avx, double avy,
            int neighbourX, int neighbourY,
            int offsetAX, int offsetAY, int offsetBX, int offsetBY,
            int ax, int ay, int bx, int by, double shade)
        {
            double neighbourHeight = Terrain.CornerHeight(neighbourX, neighbourY);

            int topAX = ay, topBX = by;
            int bottomAY = projection.ScreenY(avx + offsetAX, avy + offsetAY, neighbourHeight);
            int bottomBY = projection.ScreenY(avx + offsetBX, avy + offsetBY, neighbourHeight);

            // Nothing to draw when the neighbour is level with us or higher: its own face
            // handles the drop, and drawing here would double-shade the seam.
            if (bottomAY <= topAX && bottomBY <= topBX) return;

            int colour = Terrain.Shade(Terrain.ColourAt(neighbourX, neighbourY), shade);

            target.FillQuad(ax, topAX, bx, topBX, bx, bottomBY, ax, bottomAY, colour);
        }

        /// <summary>
        /// Draws a wall standing on one edge of a tile.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Walls are not billboards: they sit on a tile edge and have to be projected as a quad
        /// between two corners, otherwise they drift off the tile as the camera rotates. The
        /// face is picked by the object's rotation, which is the same value the collision map
        /// uses, so a wall you can see is a wall you cannot walk through.
        /// </para>
        /// <para>
        /// Until real object models are loaded this is a plain slab. The colour and outline are
        /// doing all the work of making it read as masonry rather than as a blank panel: a
        /// flat light fill with no edge is indistinguishable from a hole in the screen.
        /// </para>
        /// </remarks>
        private void DrawWall(Framebuffer target, int rotation,
            int x00, int y00, int x10, int y10, int x01, int y01, int x11, int y11)
        {
            // Wall height in pixels. Low on purpose: without object models a tall slab reads as
            // a floating panel rather than masonry, and it hides the floor behind it.
            const int wallPixels = 14;

            int ax, ay, bx, by;

            switch (rotation & 3)
            {
                case 0: ax = x00; ay = y00; bx = x01; by = y01; break;   // west edge
                case 1: ax = x00; ay = y00; bx = x10; by = y10; break;   // south edge
                case 2: ax = x10; ay = y10; bx = x11; by = y11; break;   // east edge
                default: ax = x01; ay = y01; bx = x11; by = y11; break;  // north edge
            }

            // Face lighting: the two edges facing the viewer are lit, the far ones are not.
            // The contrast between adjacent faces is what makes a corner read as a corner.
            int face = rotation & 3;
            double shade = face == 1 ? 0.0 : face == 3 ? -0.20 : -0.09;

            int colour = Terrain.Shade(0x7E7466, shade);
            int cap = Terrain.Shade(colour, 0.08);

            target.FillQuad(ax, ay, bx, by, bx, by - wallPixels, ax, ay - wallPixels, colour);

            // A slightly lighter cap along the top and a dark line at the base give the slab
            // thickness and ground it. The cap has to stay close to the face colour: a bright
            // one turns the whole wall into a white bar and the face disappears underneath it.
            DrawLine(target, ax, ay - wallPixels, bx, by - wallPixels, cap);
            DrawLine(target, ax, ay, bx, by, Terrain.Shade(colour, -0.34));

            // The vertical seams at each end stop two wall segments reading as one long slab.
            DrawLine(target, ax, ay, ax, ay - wallPixels, Terrain.Shade(colour, -0.26));
            DrawLine(target, bx, by, bx, by - wallPixels, Terrain.Shade(colour, -0.26));
        }

        /// <summary>
        /// Lighting term for one triangle, from the elevation of its three corners.
        /// </summary>
        private static double SlopeLight(double h0, double h1, double h2)
        {
            // Gradient across the triangle in tile space. The two edges share corner 0.
            double dh1 = h1 - h0;
            double dh2 = h2 - h0;

            // Gentle, and asymmetric on purpose: a face turned away from the light is shaded,
            // but nothing is ever brightened past the floor's own colour. The cache's colours
            // are already the "lit" values — the original rebuilds them through an HSL palette
            // at 96/128 lightness rather than adding light — so brightening here washes the
            // dark surfaces out to grey and leaves the walls looking like blank panels.
            double light = -(dh1 * 0.030) - (dh2 * 0.042);

            if (light > 0.02) light = 0.02;
            if (light < -0.20) light = -0.20;

            return light;
        }

        /// <summary>Draws a one-pixel line, used for tile edges and wall feet.</summary>
        private static void DrawLine(Framebuffer target, int x0, int y0, int x1, int y1, int argb)
        {
            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            while (true)
            {
                target.FillSpan(x0, x0 + 1, y0, argb);

                if (x0 == x1 && y0 == y1) break;

                int e2 = err * 2;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }

        private void DrawGroundItems(Framebuffer target, ClientWorld world,
            int tileX, int tileY, int cx, int cy)
        {
            for (int i = 0; i < world.GroundItems.Count; i++)
            {
                ClientGroundItem item = world.GroundItems[i];
                if (item.X != tileX || item.Y != tileY || item.Plane != world.MyPlane) continue;

                target.FillDiamond(cx, cy + 2, 6, 3, Framebuffer.Argb(100, 0, 0, 0));
                target.FillDiamond(cx, cy - 3, 5, 4, Framebuffer.Rgb(234, 198, 98));

                if (item.Amount > 1)
                {
                    _text.Draw(target, item.Amount.ToString(), cx + 7, cy - 16, unchecked((int)0xFFFFFF00),
                        FontRenderer.Size.Small);
                }
            }
        }

        /// <summary>
        /// A tree with a layered canopy, drawn so it reads as a volume rather than a stamp.
        /// </summary>
        private void DrawTree(Framebuffer target, int cx, int cy, int tileX, int tileY)
        {
            int variation = (tileX * 7 + tileY * 13) & 3;
            int trunkHeight = 18 + variation * 4;
            int canopyWidth = 17 + variation * 2;

            target.FillDiamond(cx, cy + 1, 11, 5, Framebuffer.Argb(95, 0, 0, 0));

            // Trunk with a lit left edge.
            int trunkTop = cy - trunkHeight;
            target.FillRect(cx - 3, trunkTop, 6, trunkHeight, Framebuffer.Rgb(76, 54, 32));
            target.FillRect(cx - 3, trunkTop, 2, trunkHeight, Framebuffer.Rgb(98, 72, 44));

            // Canopy: three overlapping masses, lightest at the top left.
            target.FillDiamond(cx, trunkTop - 2, canopyWidth, canopyWidth / 2 + 2, Framebuffer.Rgb(40, 72, 36));
            target.FillDiamond(cx - 2, trunkTop - 9, canopyWidth - 3, canopyWidth / 2, Framebuffer.Rgb(52, 92, 46));
            target.FillDiamond(cx - 3, trunkTop - 15, canopyWidth - 8, canopyWidth / 3 + 1, Framebuffer.Rgb(66, 112, 56));
        }

        private void DrawBoulder(Framebuffer target, int cx, int cy, int tileX, int tileY)
        {
            int variation = (tileX * 5 + tileY * 11) & 3;
            int size = 8 + variation * 2;

            target.FillDiamond(cx, cy + 1, size + 3, 4, Framebuffer.Argb(100, 0, 0, 0));
            target.FillDiamond(cx, cy - size / 2, size, size / 2 + 3, Framebuffer.Rgb(100, 100, 106));
            target.FillDiamond(cx - 2, cy - size / 2 - 3, size - 3, size / 3, Framebuffer.Rgb(134, 134, 140));
        }

        private void DrawPlayer(Framebuffer target, ClientPlayer player, int cx, int cy)
        {
            DrawHumanoid(target, cx, cy, player.Colours, player.EquipmentIds, player.Skulled, player.IsLocal);

            string label = player.Name;
            if (player.CombatLevel > 0) label = player.Name + " (" + player.CombatLevel + ")";

            int labelColour = player.Privilege >= 2 ? unchecked((int)0xFFFF6B6B)
                : player.Privilege == 1 ? unchecked((int)0xFF7BE07B)
                : unchecked((int)0xFFFFFFFF);

            _text.DrawCentred(target, label, cx - 70, cy - 78, 140, labelColour, FontRenderer.Size.Small);

            DrawOverhead(target, player.OverheadText, player.OverheadUntilMs, cx, cy - 94);
            DrawHitBar(target, player.Hitpoints, player.MaxHitpoints, player.DamageToShow, cx, cy - 64);
        }

        private void DrawNpc(Framebuffer target, ClientNpc npc, int cx, int cy)
        {
            int body = npc.CombatLevel <= 0 ? Framebuffer.Rgb(150, 160, 170)
                : npc.CombatLevel < 20 ? Framebuffer.Rgb(126, 168, 112)
                : npc.CombatLevel < 60 ? Framebuffer.Rgb(190, 160, 92)
                : Framebuffer.Rgb(190, 96, 84);

            DrawHumanoid(target, cx, cy, null, null, false, false, body);

            string label = npc.Name;
            if (npc.CombatLevel > 0) label = npc.Name + " (" + npc.CombatLevel + ")";

            _text.DrawCentred(target, label, cx - 70, cy - 78, 140, unchecked((int)0xFFFFFF66), FontRenderer.Size.Small);

            DrawOverhead(target, npc.OverheadText, npc.OverheadUntilMs, cx, cy - 94);
            DrawHitBar(target, npc.Hitpoints, npc.MaxHitpoints, npc.DamageToShow, cx, cy - 64);
        }

        /// <summary>
        /// A stylised humanoid: shadow, legs, torso, arms, head, with the weapon visible.
        /// </summary>
        /// <remarks>
        /// Without model data this is the honest option, and it carries what a player needs —
        /// who, friend or foe, what they are wearing — without pretending to be a 3D render.
        /// The shape is deliberately chunky so it reads at a glance in a crowded scene.
        /// </remarks>
        private void DrawHumanoid(Framebuffer target, int cx, int baseY, int[] colours, int[] equipment,
            bool skulled, bool isLocal, int? overrideBody = null)
        {
            const int height = 46;
            const int bodyWidth = 16;
            const int headRadius = 6;

            int top = baseY - height;

            target.FillDiamond(cx, baseY, 9, 4, Framebuffer.Argb(105, 0, 0, 0));

            int torso = overrideBody ?? (colours != null ? Palette.Part(Palette.Torso, colours[1]) : Framebuffer.Rgb(92, 112, 152));
            int legs = overrideBody ?? (colours != null ? Palette.Part(Palette.Legs, colours[2]) : Framebuffer.Rgb(68, 68, 88));
            int skin = colours != null ? Palette.Part(Palette.Skin, colours[4]) : Framebuffer.Rgb(226, 188, 150);
            int hair = colours != null ? Palette.Part(Palette.Hair, colours[0]) : Framebuffer.Rgb(82, 56, 36);

            // Legs, with a dark seam between them.
            target.FillRect(cx - 6, top + height - 16, 5, 16, legs);
            target.FillRect(cx + 1, top + height - 16, 5, 16, legs);
            target.FillRect(cx - 1, top + height - 16, 2, 16, Terrain.Shade(legs, -0.38));

            // Torso, with a darker right side so it reads as a cylinder.
            target.FillRect(cx - bodyWidth / 2, top + 15, bodyWidth, 21, torso);
            target.FillRect(cx + bodyWidth / 2 - 5, top + 15, 5, 21, Terrain.Shade(torso, -0.24));

            // Arms.
            target.FillRect(cx - bodyWidth / 2 - 4, top + 16, 4, 15, skin);
            target.FillRect(cx + bodyWidth / 2, top + 16, 4, 15, Terrain.Shade(skin, -0.16));

            // Head and hair.
            target.FillCircle(cx, top + 8, headRadius, skin);
            target.FillRect(cx - headRadius, top + 2, headRadius * 2, 5, hair);

            // Weapon at the right hand.
            if (equipment != null && equipment.Length > 3 && equipment[3] > 0)
            {
                target.FillRect(cx + bodyWidth / 2 + 3, top + 3, 2, 24, Framebuffer.Rgb(198, 204, 214));
                target.FillRect(cx + bodyWidth / 2 + 2, top + 22, 4, 3, Framebuffer.Rgb(118, 88, 48));
            }

            if (skulled)
            {
                target.FillRect(cx - 4, top + 5, 3, 3, Framebuffer.Rgb(222, 44, 44));
                target.FillRect(cx + 2, top + 5, 3, 3, Framebuffer.Rgb(222, 44, 44));
            }

            if (isLocal)
            {
                target.FillDiamond(cx, baseY + 1, 8, 3, Framebuffer.Argb(150, 255, 232, 120));
            }
        }

        private void DrawOverhead(Framebuffer target, string text, long untilMs, int cx, int y)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (DartScape.Core.TimeUtil.MonotonicMs() > untilMs) return;

            int width = (int)_text.Measure(text, FontRenderer.Size.Small).Width + 8;

            target.BlendRect(cx - width / 2, y - 2, width, 14, Framebuffer.Argb(190, 0, 0, 0));
            _text.DrawCentred(target, text, cx - width / 2, y, width, unchecked((int)0xFFFFFF00), FontRenderer.Size.Small);
        }

        private void DrawHitBar(Framebuffer target, int hitpoints, int maxHitpoints, int damage, int cx, int y)
        {
            if (maxHitpoints <= 0) return;
            if (hitpoints >= maxHitpoints && damage <= 0) return;

            const int width = 30;
            int filled = (int)((double)Math.Max(0, hitpoints) / maxHitpoints * width);

            target.FillRect(cx - width / 2, y, width, 4, Framebuffer.Argb(200, 60, 0, 0));
            target.FillRect(cx - width / 2, y, filled, 4, Framebuffer.Rgb(0, 200, 60));
            target.DrawRect(cx - width / 2, y, width, 4, Framebuffer.Argb(200, 0, 0, 0));

            if (damage > 0)
            {
                _text.DrawCentred(target, "-" + damage, cx - 20, y - 16, 40, unchecked((int)0xFFFF4040),
                    FontRenderer.Size.Bold);
            }
        }

        /// <summary>
        /// Draws the minimap: a square map of the surrounding tiles, rotated with the camera.
        /// </summary>
        /// <remarks>
        /// Square rather than circular, and sized in tiles so it scales with the screen. The
        /// circle looked tidier but wasted the corners, and on a 640x480 handheld every pixel
        /// of the map matters.
        /// </remarks>
        public void DrawMinimap(Framebuffer target, ClientWorld world, int x, int y, int size)
        {
            if (world == null) return;

            int pixelsPerTile = Math.Max(1, size / 72);
            int radius = size / 2 / pixelsPerTile;

            target.FillRect(x, y, size, size, Framebuffer.Rgb(16, 22, 16));

            int centre = size / 2;

            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    // The minimap rotates with the camera, so up on the map is always where
                    // the player is looking.
                    double wx, wy;
                    IsoProjection.Unrotate(world.MyX + dx, world.MyY + dy, world.CameraRotation, out wx, out wy);

                    int tileX = (int)Math.Round(wx);
                    int tileY = (int)Math.Round(wy);

                    // The minimap is drawn from the same terrain the world is, so it shows the
                    // real coastline and the real paths. Inventing a separate palette here is
                    // how a map ends up disagreeing with the ground it claims to describe.
                    int colour = Terrain.ColourAt(tileX, tileY);

                    target.FillRect(x + centre + dx * pixelsPerTile, y + centre + dy * pixelsPerTile,
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
                if (Math.Abs(dx) > radius || Math.Abs(dy) > radius) continue;

                target.FillRect(x + centre + dx * pixelsPerTile - 1, y + centre + dy * pixelsPerTile - 1,
                    3, 3, npc.CombatLevel > 0 ? Framebuffer.Rgb(222, 84, 62) : Framebuffer.Rgb(200, 200, 92));
            }

            // Other players.
            for (int i = 0; i < world.Players.Length; i++)
            {
                ClientPlayer player = world.Players[i];
                if (player == null || player.IsLocal || player.Plane != world.MyPlane) continue;

                int dx = player.X - world.MyX;
                int dy = player.Y - world.MyY;
                if (Math.Abs(dx) > radius || Math.Abs(dy) > radius) continue;

                target.FillRect(x + centre + dx * pixelsPerTile - 1, y + centre + dy * pixelsPerTile - 1,
                    3, 3, Framebuffer.Rgb(92, 200, 255));
            }

            // The local player, always dead centre.
            target.FillRect(x + centre - 2, y + centre - 2, 4, 4, Framebuffer.Rgb(255, 255, 255));

            // Compass: N points where the world's north is on screen after rotation.
            int nx = 0, ny = -1;
            for (int r = 0; r < (world.CameraRotation & 3); r++)
            {
                int tx = -ny;
                ny = nx;
                nx = tx;
            }

            _text.DrawCentred(target, "N", x + centre + nx * (centre - 8) - 6, y + centre + ny * (centre - 8) - 6,
                12, unchecked((int)0xFFFFFFFF), FontRenderer.Size.Small);

            target.DrawRect(x, y, size, size, Framebuffer.Rgb(170, 160, 120));
        }

        /// <summary>A player or NPC paired with the interpolated position it is drawn at.</summary>
        private sealed class EntityDrawable
        {
            public ClientPlayer Player;
            public ClientNpc Npc;
            public double WorldX;
            public double WorldY;
        }
    }
}
