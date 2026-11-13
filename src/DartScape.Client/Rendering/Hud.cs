using System;
using System.Collections.Generic;
using DartScape.Client.Rendering;

namespace DartScape.Client.Rendering
{
    /// <summary>
    /// Draws the interface: chat box, sidebar tabs, skill list, inventory and the
    /// connection overlay.
    /// </summary>
    /// <remarks>
    /// The layout follows the original game's proportions вЂ” a fixed 765x503 canvas with
    /// the sidebar on the right and the chat box bottom-left вЂ” because that is the shape
    /// people recognise, and because a fixed layout means the interface never reflows into
    /// something unreadable at an odd window size. The framebuffer is scaled to the window
    /// instead.
    /// </remarks>
    public sealed class Hud
    {
        /// <summary>Width of the sidebar.</summary>
        public const int SidebarWidth = 190;

        /// <summary>Height of the chat box.</summary>
        public const int ChatHeight = 152;

        private readonly FontRenderer _text;

        private static readonly int PanelBg = Framebuffer.Argb(228, 40, 44, 52);
        private static readonly int PanelEdge = Framebuffer.Argb(255, 78, 84, 96);
        private static readonly int HeaderBg = Framebuffer.Argb(255, 54, 60, 72);
        private static readonly int TextColour = unchecked((int)0xFFE8E8E8);
        private static readonly int MutedColour = unchecked((int)0xFFA0A8B4);
        private static readonly int AccentColour = unchecked((int)0xFFFFD24A);

        /// <summary>Names of the sidebar tabs, in display order.</summary>
        public static readonly string[] Tabs = { "Skills", "Inventory", "Equipment", "Magic", "Prayer", "Options" };

        /// <summary>Height of the status bar drawn across the top of the world view.</summary>
        /// <remarks>
        /// The bar is drawn over the world, not beside it, so anything else placed in the
        /// top-left of the view has to start below this. The minimap did not, which is why
        /// the bar appeared to overlap it.
        /// </remarks>
        public const int TopBarHeight = 22;

        public Hud(FontRenderer text)
        {
            _text = text;
        }

        /// <summary>Draws the whole interface.</summary>
        public void Draw(Framebuffer target, ClientWorld world, int width, int height, string statusLine)
        {
            DrawChatBox(target, world, 0, height - ChatHeight, width - SidebarWidth, ChatHeight);
            DrawSidebar(target, world, width - SidebarWidth, 0, SidebarWidth, height);
            DrawTopBar(target, world, 0, 0, width - SidebarWidth, TopBarHeight, statusLine);
        }

        private void DrawTopBar(Framebuffer target, ClientWorld world, int x, int y, int width, int height, string status)
        {
            target.FillRect(x, y, width, height, HeaderBg);
            target.FillRect(x, y + height - 1, width, 1, PanelEdge);

            _text.Draw(target, "DartScape", x + 8, y + 3, AccentColour, FontRenderer.Size.Bold);

            if (world.Me != null)
            {
                ClientPlayer me = world.Me;
                string info = me.X + ", " + me.Y + "  |  combat " + me.CombatLevel;
                _text.DrawRight(target, info, x + width - 8, y + 3, MutedColour, FontRenderer.Size.Small);
            }

            if (!string.IsNullOrEmpty(status))
            {
                _text.DrawCentred(target, status, x, y + 3, width, unchecked((int)0xFFFF8080), FontRenderer.Size.Small);
            }
        }

        // -- chat -------------------------------------------------------------

        private void DrawChatBox(Framebuffer target, ClientWorld world, int x, int y, int width, int height)
        {
            target.BlendRect(x, y, width, height, PanelBg);
            target.FillRect(x, y, width, 1, PanelEdge);
            target.FillRect(x, y, 1, height, PanelEdge);
            target.FillRect(x + width - 1, y, 1, height, PanelEdge);

            const int lineHeight = 14;
            int visibleLines = (height - 8) / lineHeight;

            int start = Math.Max(0, world.Chat.Count - visibleLines);
            int drawY = y + height - 6 - lineHeight;

            for (int i = world.Chat.Count - 1; i >= start; i--)
            {
                ChatLine line = world.Chat[i];

                int colour = TextColour;
                if (line.Type == 1) colour = unchecked((int)0xFF9AD8FF);        // other players
                else if (line.Type == 2) colour = unchecked((int)0xFFFFD24A);   // system
                else if (line.Type == 3) colour = unchecked((int)0xFFFF9A9A);   // errors

                // Fade older lines slightly so the newest message reads first.
                int age = world.Chat.Count - 1 - i;
                if (age > visibleLines / 2) colour = Framebuffer.Lerp(colour, PanelBg, 0.35);

                _text.Draw(target, line.Text, x + 6, drawY, colour, FontRenderer.Size.Small);
                drawY -= lineHeight;
            }

            if (world.Chat.Count == 0)
            {
                _text.Draw(target, "Welcome to DartScape. Click the world to walk, or press Enter to chat.",
                    x + 6, y + height - 20, MutedColour, FontRenderer.Size.Small);
            }
        }

        // -- sidebar ----------------------------------------------------------

        private void DrawSidebar(Framebuffer target, ClientWorld world, int x, int y, int width, int height)
        {
            target.FillRect(x, y, width, height, PanelBg);
            target.FillRect(x, y, 1, height, PanelEdge);

            // Tab strip.
            int tabWidth = width / Tabs.Length;
            for (int i = 0; i < Tabs.Length; i++)
            {
                bool active = world.Tab == i;
                int tx = x + i * tabWidth;

                target.FillRect(tx, y, tabWidth - 1, 22, active ? PanelEdge : HeaderBg);
                _text.DrawCentred(target, Tabs[i].Substring(0, Math.Min(3, Tabs[i].Length)),
                    tx, y + 4, tabWidth - 1, active ? AccentColour : MutedColour, FontRenderer.Size.Small);
            }

            int contentY = y + 26;

            switch (world.Tab)
            {
                case 0: DrawSkills(target, world, x, contentY, width); break;
                case 1: DrawInventory(target, world, x, contentY, width); break;
                case 2: DrawEquipment(target, world, x, contentY, width); break;
                default: DrawPlaceholder(target, x, contentY, width, Tabs[world.Tab]); break;
            }
        }

        private void DrawSkills(Framebuffer target, ClientWorld world, int x, int y, int width)
        {
            _text.Draw(target, "Total level: " + TotalLevel(world), x + 8, y, AccentColour, FontRenderer.Size.Small);
            y += 18;

            for (int i = 0; i < 21; i++)
            {
                int column = i % 2;
                int row = i / 2;

                int sx = x + 8 + column * (width / 2 - 6);
                int sy = y + row * 16;

                int level = world.BaseLevel[i];
                int current = world.CurrentLevel[i];

                int colour = current < level ? unchecked((int)0xFFFF7070) : TextColour;

                string name = SkillNames[i];
                _text.Draw(target, name, sx, sy, MutedColour, FontRenderer.Size.Small);
                _text.DrawRight(target, level.ToString(), sx + width / 2 - 20, sy, colour, FontRenderer.Size.Small);
            }
        }

        private static string[] SkillNames =
        {
            "Attack", "Defence", "Strength", "Hitpoints", "Ranged", "Prayer", "Magic",
            "Cooking", "Woodcutting", "Fletching", "Fishing", "Firemaking", "Crafting",
            "Smithing", "Mining", "Herblore", "Agility", "Thieving", "Slayer", "Farming",
            "Runecraft"
        };

        private static int TotalLevel(ClientWorld world)
        {
            int total = 0;
            for (int i = 0; i < 21; i++) total += world.BaseLevel[i];
            return total;
        }

        private void DrawInventory(Framebuffer target, ClientWorld world, int x, int y, int width)
        {
            const int cell = 34;
            const int columns = 4;

            int gridWidth = columns * cell;
            int startX = x + (width - gridWidth) / 2;

            for (int slot = 0; slot < 28; slot++)
            {
                int column = slot % columns;
                int row = slot / columns;

                int cx = startX + column * cell;
                int cy = y + row * cell;

                bool selected = world.SelectedSlot == slot;

                target.FillRect(cx, cy, cell - 2, cell - 2, selected ? PanelEdge : HeaderBg);
                target.DrawRect(cx, cy, cell - 2, cell - 2, Framebuffer.Argb(120, 90, 96, 108));

                ClientWorld.ItemSlot item = world.Inventory[slot];
                if (item.IsEmpty) continue;

                // Without item sprites, a colour-coded token plus the amount is the honest
                // representation: it shows at a glance that something is there and which
                // slot it occupies.
                int tokenColour = ItemColour(item.Id);
                target.FillCircle(cx + cell / 2 - 1, cy + cell / 2 - 3, 8, tokenColour);

                if (item.Amount > 1)
                {
                    _text.Draw(target, item.Amount > 99999 ? (item.Amount / 1000) + "K" : item.Amount.ToString(),
                        cx + cell - 12, cy + cell - 14, unchecked((int)0xFFFFFF00), FontRenderer.Size.Small);
                }
            }

            _text.Draw(target, "Inventory", x + 8, y + 7 * cell + 4, MutedColour, FontRenderer.Size.Small);
        }

        /// <summary>A stable, readable colour per item id, so stacks are distinguishable.</summary>
        private static int ItemColour(int itemId)
        {
            uint h = (uint)itemId * 2654435761u;
            h ^= h >> 15;

            int r = 90 + (int)(h & 0x7F);
            int g = 90 + (int)((h >> 8) & 0x7F);
            int b = 90 + (int)((h >> 16) & 0x7F);

            return Framebuffer.Rgb(r, g, b);
        }

        private void DrawEquipment(Framebuffer target, ClientWorld world, int x, int y, int width)
        {
            string[] slots = { "Head", "Cape", "Neck", "Weapon", "Body", "Shield", "-", "Legs", "-", "Hands", "Feet", "-" };

            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == "-") continue;

                int sy = y + i * 20;
                _text.Draw(target, slots[i], x + 10, sy, MutedColour, FontRenderer.Size.Small);

                int itemId = world.Me != null && world.Me.EquipmentIds.Length > i ? world.Me.EquipmentIds[i] : 0;
                _text.DrawRight(target, itemId > 0 ? "item " + itemId : "empty", x + width - 10, sy,
                    itemId > 0 ? TextColour : Framebuffer.Lerp(TextColour, PanelBg, 0.5), FontRenderer.Size.Small);
            }
        }

        private void DrawPlaceholder(Framebuffer target, int x, int y, int width, string tab)
        {
            _text.DrawCentred(target, tab, x, y + 40, width, MutedColour, FontRenderer.Size.Normal);
            _text.DrawCentred(target, "Not implemented yet", x, y + 60, width, Framebuffer.Lerp(MutedColour, PanelBg, 0.4),
                FontRenderer.Size.Small);
        }

        /// <summary>Draws the login screen.</summary>
        public void DrawLogin(Framebuffer target, int width, int height, string name, string password,
            string status, bool busy)
        {
            target.VerticalGradient(0, 0, width, height, Framebuffer.Rgb(12, 22, 38), Framebuffer.Rgb(30, 58, 92));

            _text.DrawCentred(target, "DartScape", 0, height / 2 - 150, width, AccentColour, FontRenderer.Size.Title);

            // The panel geometry lives in LoginLayout so the mouse hit test and the
            // renderer cannot disagree about where a field is.
            LoginLayout layout = LoginLayout.For(width, height);

            int panelWidth = layout.PanelWidth;
            int panelX = layout.PanelX;
            int panelY = layout.PanelY;

            target.BlendRect(panelX - 16, panelY - 16, panelWidth + 32, 210, Framebuffer.Argb(210, 16, 24, 36));
            target.DrawRect(panelX - 16, panelY - 16, panelWidth + 32, 210, Framebuffer.Argb(180, 200, 170, 74));

            _text.Draw(target, "Username", panelX, panelY, MutedColour, FontRenderer.Size.Small);
            target.FillRect(panelX, panelY + 16, panelWidth, 24, Framebuffer.Argb(255, 28, 36, 50));
            target.DrawRect(panelX, panelY + 16, panelWidth, 24, Framebuffer.Argb(255, 90, 100, 118));
            _text.Draw(target, name ?? string.Empty, panelX + 6, panelY + 20, TextColour);

            _text.Draw(target, "Password", panelX, panelY + 52, MutedColour, FontRenderer.Size.Small);
            target.FillRect(panelX, panelY + 68, panelWidth, 24, Framebuffer.Argb(255, 28, 36, 50));
            target.DrawRect(panelX, panelY + 68, panelWidth, 24, Framebuffer.Argb(255, 90, 100, 118));
            _text.Draw(target, new string('*', (password ?? string.Empty).Length), panelX + 6, panelY + 72, TextColour);

            int buttonY = layout.Button.Y;
            target.FillRect(panelX, buttonY, panelWidth, 32, busy ? Framebuffer.Rgb(60, 66, 78) : Framebuffer.Rgb(58, 92, 140));
            target.DrawRect(panelX, buttonY, panelWidth, 32, Framebuffer.Argb(255, 200, 170, 74));
            _text.DrawCentred(target, busy ? "Connecting..." : "Log in", panelX, buttonY + 8, panelWidth,
                busy ? MutedColour : unchecked((int)0xFFFFFFFF), FontRenderer.Size.Bold);

            if (!string.IsNullOrEmpty(status))
            {
                // Wrapped: a real error message is longer than one line, and clipping it
                // turns a useful message into a useless one.
                int statusY = layout.Button.Bottom + 14;

                foreach (string line in _text.Wrap(status, width - 80, FontRenderer.Size.Small))
                {
                    _text.DrawCentred(target, line, 0, statusY, width, unchecked((int)0xFFFF9A9A), FontRenderer.Size.Small);
                    statusY += 15;
                }
            }

            _text.DrawCentred(target, "Enter to log in  |  Tab to switch field  |  click a field to focus it",
                0, height - 40, width,
                Framebuffer.Lerp(MutedColour, Framebuffer.Rgb(12, 22, 38), 0.2), FontRenderer.Size.Small);
        }
    }

    /// <summary>A rectangle in canvas coordinates.</summary>
    public struct LoginRect
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;

        /// <summary>Y coordinate of the first row below the rectangle.</summary>
        public int Bottom
        {
            get { return Y + Height; }
        }

        public bool Contains(int x, int y)
        {
            return x >= X && x < X + Width && y >= Y && y < Y + Height;
        }
    }

    /// <summary>
    /// Geometry of the login screen.
    /// </summary>
    /// <remarks>
    /// The renderer and the hit test both read this. Drawing a field in one place and
    /// making it clickable in another is a bug you only find when a user reports "the
    /// mouse does not work", so the two must share one definition.
    /// </remarks>
    public struct LoginLayout
    {
        public int PanelX;
        public int PanelY;
        public int PanelWidth;
        public LoginRect NameField;
        public LoginRect PasswordField;
        public LoginRect Button;

        /// <summary>Computes the layout for a canvas of the given size.</summary>
        public static LoginLayout For(int width, int height)
        {
            const int panelWidth = 320;

            int panelX = (width - panelWidth) / 2;
            int panelY = height / 2 - 60;

            return new LoginLayout
            {
                PanelX = panelX,
                PanelY = panelY,
                PanelWidth = panelWidth,
                NameField = new LoginRect { X = panelX, Y = panelY + 16, Width = panelWidth, Height = 24 },
                PasswordField = new LoginRect { X = panelX, Y = panelY + 68, Width = panelWidth, Height = 24 },
                Button = new LoginRect { X = panelX, Y = panelY + 112, Width = panelWidth, Height = 32 }
            };
        }
    }
}
