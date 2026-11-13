using System;
using System.Drawing;
using System.Windows.Forms;
using DartScape.Client.Net;
using DartScape.Client.Rendering;
using DartScape.Core.Diagnostics;


namespace DartScape.Client
{
    /// <summary>
    /// The game window: owns the framebuffer, the render loop and all input.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rendering runs on a timer at a fixed rate and always draws a complete frame into
    /// the framebuffer before blitting. There is no partial invalidation and no
    /// double-buffering subtlety: the window is repainted from scratch, which at 765x503
    /// is a couple of milliseconds and removes an entire category of flicker bugs.
    /// </para>
    /// <para>
    /// The client is fixed-layout: the interface is always drawn at 765x503 and scaled to
    /// whatever the window is. That keeps every hit test in one coordinate space and means
    /// resizing can never break the interface.
    /// </para>
    /// </remarks>
    public sealed class GameWindow : Form
    {
        /// <summary>The design resolution every coordinate is expressed in.</summary>
        public const int CanvasWidth = 765;
        public const int CanvasHeight = 503;

        private readonly ClientConfig _config;
        private readonly ClientWorld _world = new ClientWorld();
        private readonly GameClient _client;

        private Framebuffer _framebuffer;
        private FontRenderer _text;
        private WorldRenderer _worldRenderer;
        private Hud _hud;

        private readonly Timer _timer;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private double _lastFrameSeconds;

        // -- login state ------------------------------------------------------
        private bool _loginMode = true;
        private string _nameText = "";
        private string _passwordText = "";
        private bool _passwordFocused;
        private string _loginStatus = "";

        // -- chat input -------------------------------------------------------
        private bool _chatMode;
        private string _chatText = "";

        // -- camera -----------------------------------------------------------
        private bool _dragging;
        private Point _dragStart;
        private int _dragCameraX;
        private int _dragCameraY;

        /// <summary>Latest frames-per-second measurement.</summary>
        public double Fps { get; private set; }

        public GameWindow(ClientConfig config)
        {
            _config = config;

            Text = "DartScape";
            ClientSize = new Size(config.Width, config.Height);
            MinimumSize = new Size(640, 480);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            KeyPreview = true;
            BackColor = Color.Black;

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception)
            {
                // No icon resource; not worth failing over.
            }

            _framebuffer = new Framebuffer(CanvasWidth, CanvasHeight);
            _text = new FontRenderer();
            _worldRenderer = new WorldRenderer(_text);
            _hud = new Hud(_text);

            _client = new GameClient(config, _world);
            _client.StateChanged += OnConnectionStateChanged;

            PacketDecoder.SetChatSink((name, message, rights) =>
            {
                string prefix = rights >= 2 ? "[Admin] " : rights == 1 ? "[Mod] " : "";
                _world.AddChatMessage(prefix + name + ": " + message, 1);
            });

            _nameText = config.Name ?? string.Empty;
            _passwordText = config.Password ?? string.Empty;

            _timer = new Timer { Interval = 15 };
            _timer.Tick += OnTick;
            _timer.Start();

            KeyDown += OnKeyDown;
            KeyPress += OnKeyPress;
            MouseDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseUp += OnMouseUp;
            MouseWheel += OnMouseWheel;
            Resize += (s, e) => Invalidate();
        }

        private void OnConnectionStateChanged(ConnectionState state)
        {
            if (IsDisposed) return;

            try
            {
                BeginInvoke((MethodInvoker)(() =>
                {
                    switch (state)
                    {
                        case ConnectionState.Connected:
                            _loginMode = false;
                            _loginStatus = "";
                            PacketDecoder.Reset();
                            _world.AddChatMessage("Connected to " + _config.Host + ":" + _config.Port + ".", 2);
                            break;

                        case ConnectionState.Failed:
                            _loginStatus = _client.LastError ?? "Connection failed.";
                            _loginMode = true;
                            break;

                        case ConnectionState.Disconnected:
                            if (!_loginMode)
                            {
                                _world.AddChatMessage("Connection closed.", 3);
                                _loginMode = true;
                                _loginStatus = "Disconnected from the server.";
                            }
                            break;
                    }
                }));
            }
            catch (Exception)
            {
                // The window is going away.
            }
        }

        // -- frame ------------------------------------------------------------

        private void OnTick(object sender, EventArgs e)
        {
            double now = _clock.Elapsed.TotalSeconds;
            double delta = now - _lastFrameSeconds;
            _lastFrameSeconds = now;

            if (delta > 0.0001) Fps = Fps * 0.9 + (1.0 / delta) * 0.1;

            // The server ticks at 600 ms; interpolate entity positions across that window.
            double interpolation = (now * 1000.0 % 600.0) / 600.0;

            Render(interpolation);
            Invalidate();
        }

        private void Render(double interpolation)
        {
            if (_loginMode)
            {
                _hud.DrawLogin(_framebuffer, CanvasWidth, CanvasHeight, _nameText, _passwordText,
                    _loginStatus, _client.State == ConnectionState.Connecting || _client.State == ConnectionState.LoggingIn);
                return;
            }

            int viewWidth = CanvasWidth - Hud.SidebarWidth;
            int viewHeight = CanvasHeight - Hud.ChatHeight;

            _framebuffer.FillRect(0, 0, CanvasWidth, CanvasHeight, Framebuffer.Rgb(20, 26, 20));

            _worldRenderer.Draw(_framebuffer, _world, 0, 0, viewWidth, viewHeight, interpolation);

            // Minimap tucked into the top-right corner of the world view, square, below the
            // status bar and with a small margin so its border never touches the frame edge.
            const int minimapSize = 144;
            const int minimapMargin = 6;
            _worldRenderer.DrawMinimap(_framebuffer, _world,
                viewWidth - minimapSize - minimapMargin,
                Hud.TopBarHeight + minimapMargin,
                minimapSize);

            _hud.Draw(_framebuffer, _world, CanvasWidth, CanvasHeight, _chatMode ? "Chat: " + _chatText + "_" : null);

            // FPS readout, small and out of the way.
            _text.Draw(_framebuffer, ((int)Fps) + " fps", 6, CanvasHeight - Hud.ChatHeight - 14,
                Framebuffer.Argb(150, 255, 255, 255), FontRenderer.Size.Small);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // The framebuffer is presented here rather than in the timer so that a
            // repaint caused by the OS (move, uncover) never shows a blank window.
            base.OnPaint(e);

            if (_framebuffer == null || e.Graphics == null) return;

            _framebuffer.Present(e.Graphics, ClientSize.Width, ClientSize.Height);
        }

        // -- coordinate mapping ----------------------------------------------

        /// <summary>Converts a window point to canvas coordinates.</summary>
        private Point ToCanvas(Point client)
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return Point.Empty;

            int x = (int)((double)client.X * CanvasWidth / ClientSize.Width);
            int y = (int)((double)client.Y * CanvasHeight / ClientSize.Height);
            return new Point(x, y);
        }

        // -- input ------------------------------------------------------------

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (_loginMode)
            {
                if (e.KeyCode == Keys.Enter) { AttemptLogin(); e.Handled = true; e.SuppressKeyPress = true; return; }
                if (e.KeyCode == Keys.Tab)
                {
                    _passwordFocused = !_passwordFocused;
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return;
                }
                if (e.KeyCode == Keys.Back)
                {
                    if (_passwordFocused)
                    {
                        if (_passwordText.Length > 0) _passwordText = _passwordText.Substring(0, _passwordText.Length - 1);
                    }
                    else if (_nameText.Length > 0)
                    {
                        _nameText = _nameText.Substring(0, _nameText.Length - 1);
                    }
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                return;
            }

            if (_chatMode)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    SubmitChat();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    _chatMode = false;
                    _chatText = "";
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Back)
                {
                    if (_chatText.Length > 0) _chatText = _chatText.Substring(0, _chatText.Length - 1);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                return;
            }

            switch (e.KeyCode)
            {
                case Keys.Enter:
                    _chatMode = true;
                    _chatText = "";
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    break;

                case Keys.Up: _world.CameraOffsetY = Clamp(_world.CameraOffsetY + 1, -14, 14); e.Handled = true; break;
                case Keys.Down: _world.CameraOffsetY = Clamp(_world.CameraOffsetY - 1, -14, 14); e.Handled = true; break;
                case Keys.Left: _world.CameraOffsetX = Clamp(_world.CameraOffsetX + 1, -14, 14); e.Handled = true; break;
                case Keys.Right: _world.CameraOffsetX = Clamp(_world.CameraOffsetX - 1, -14, 14); e.Handled = true; break;

                // Camera rotation, the compass. Q turns one way, E the other, which is the
                // same muscle memory as the arrow-key camera in the original.
                case Keys.Q: _world.CameraRotation = (_world.CameraRotation + 3) & 3; e.Handled = true; break;
                case Keys.E: _world.CameraRotation = (_world.CameraRotation + 1) & 3; e.Handled = true; break;

                // Recentre: drop the camera offset so the player is in the middle again.
                case Keys.C: _world.CameraOffsetX = 0; _world.CameraOffsetY = 0; e.Handled = true; break;

                case Keys.F1: _world.Tab = 0; e.Handled = true; break;
                case Keys.F2: _world.Tab = 1; e.Handled = true; break;
                case Keys.F3: _world.Tab = 2; e.Handled = true; break;
                case Keys.F4: _world.Tab = 3; e.Handled = true; break;
                case Keys.F5: _world.Tab = 4; e.Handled = true; break;
                case Keys.F6: _world.Tab = 5; e.Handled = true; break;

                case Keys.Escape:
                    if (_world.SelectedSlot >= 0) { _world.SelectedSlot = -1; e.Handled = true; }
                    break;
            }
        }

        private void OnKeyPress(object sender, KeyPressEventArgs e)
        {
            if (_loginMode)
            {
                if (char.IsControl(e.KeyChar)) return;

                if (_passwordFocused)
                {
                    if (_passwordText.Length < 32) _passwordText += e.KeyChar;
                }
                else
                {
                    if (_nameText.Length < 12 && (char.IsLetterOrDigit(e.KeyChar) || e.KeyChar == ' ' ||
                                                  e.KeyChar == '-' || e.KeyChar == '_'))
                    {
                        _nameText += e.KeyChar;
                    }
                }
                e.Handled = true;
                return;
            }

            if (!_chatMode) return;
            if (char.IsControl(e.KeyChar)) return;

            if (_chatText.Length < 80) _chatText += e.KeyChar;
            e.Handled = true;
        }

        /// <summary>
        /// Routes a click on the login screen: focus a field, or press the button.
        /// </summary>
        /// <remarks>
        /// Without this the login screen was keyboard-only, which reads as "the mouse is
        /// broken" to anyone who does not already know the Tab trick.
        /// </remarks>
        private void HandleLoginClick(Point canvas)
        {
            LoginLayout layout = LoginLayout.For(CanvasWidth, CanvasHeight);

            if (layout.NameField.Contains(canvas.X, canvas.Y))
            {
                _passwordFocused = false;
                return;
            }

            if (layout.PasswordField.Contains(canvas.X, canvas.Y))
            {
                _passwordFocused = true;
                return;
            }

            if (layout.Button.Contains(canvas.X, canvas.Y))
            {
                AttemptLogin();
            }
        }

        /// <summary>True when the pointer is over something clickable on the login screen.</summary>
        private bool IsOverLoginControl(Point canvas)
        {
            LoginLayout layout = LoginLayout.For(CanvasWidth, CanvasHeight);

            return layout.NameField.Contains(canvas.X, canvas.Y)
                || layout.PasswordField.Contains(canvas.X, canvas.Y)
                || layout.Button.Contains(canvas.X, canvas.Y);
        }

        private void AttemptLogin()
        {
            if (string.IsNullOrEmpty(_nameText))
            {
                _loginStatus = "Enter a username.";
                return;
            }

            if (string.IsNullOrEmpty(_passwordText))
            {
                _loginStatus = "Enter a password.";
                return;
            }

            _loginStatus = "";
            _client.Connect(_config.Host, _config.Port, _nameText, _passwordText);
        }

        private void SubmitChat()
        {
            string text = _chatText.Trim();
            _chatMode = false;
            _chatText = "";

            if (text.Length == 0) return;

            if (text.StartsWith("::", StringComparison.Ordinal))
            {
                _client.SendCommand(text);
                return;
            }

            _client.SendChat(text);
        }

        // -- mouse ------------------------------------------------------------

        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            Point canvas = ToCanvas(e.Location);

            if (_loginMode)
            {
                if (e.Button == MouseButtons.Left) HandleLoginClick(canvas);
                return;
            }

            // Right-drag rotates the camera, like the original.
            if (e.Button == MouseButtons.Right)
            {
                _dragging = true;
                _dragStart = canvas;
                _dragCameraX = _world.CameraOffsetX;
                _dragCameraY = _world.CameraOffsetY;
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            int viewWidth = CanvasWidth - Hud.SidebarWidth;
            int viewHeight = CanvasHeight - Hud.ChatHeight;

            // Sidebar tabs.
            if (canvas.X >= viewWidth)
            {
                int tabWidth = Hud.SidebarWidth / Hud.Tabs.Length;
                int tab = (canvas.X - viewWidth) / tabWidth;
                if (canvas.Y < 22 && tab >= 0 && tab < Hud.Tabs.Length) _world.Tab = tab;
                else if (_world.Tab == 1) ClickInventory(canvas, viewWidth);
                return;
            }

            // Minimap.
            if (canvas.X >= viewWidth - 152 && canvas.X <= viewWidth - 8 && canvas.Y >= 8 && canvas.Y <= 152)
            {
                return;
            }

            if (canvas.Y > viewHeight) return;      // chat box

            HandleWorldClick(canvas, viewWidth, viewHeight);
        }

        private void ClickInventory(Point canvas, int viewWidth)
        {
            const int cell = 34;
            const int columns = 4;
            int gridWidth = columns * cell;
            int startX = viewWidth + (Hud.SidebarWidth - gridWidth) / 2;
            int startY = 26;

            int column = (canvas.X - startX) / cell;
            int row = (canvas.Y - startY) / cell;

            if (column < 0 || column >= columns || row < 0 || row >= 7) return;

            int slot = row * columns + column;
            if (slot < 0 || slot >= 28) return;

            ClientWorld.ItemSlot item = _world.Inventory[slot];
            if (item.IsEmpty)
            {
                _world.SelectedSlot = -1;
                return;
            }

            _world.SelectedSlot = slot;
            _client.SendItemAction(slot, item.Id);
        }

        private void HandleWorldClick(Point canvas, int viewWidth, int viewHeight)
        {
            // Convert the screen point back to a world tile through the same projection the
            // renderer uses, including camera rotation and terrain elevation.
            var projection = new IsoProjection
            {
                OriginX = viewWidth / 2,
                OriginY = viewHeight / 2 + Terrain.RenderHeight(_world.MyX, _world.MyY) * IsoProjection.HeightStep,
                CameraX = _world.MyX + _world.CameraOffsetX,
                CameraY = _world.MyY + _world.CameraOffsetY,
                Rotation = _world.CameraRotation
            };

            int tileX, tileY;
            projection.ScreenToTile(canvas.X, canvas.Y, out tileX, out tileY);

            // Prefer an entity at the clicked tile over a plain walk.
            ClientNpc npc = _world.NpcAt(tileX, tileY, _world.MyPlane);
            if (npc != null)
            {
                _client.SendNpcAction(npc.Index, 1);
                return;
            }

            ClientGroundItem ground = _world.GroundItemAt(tileX, tileY, _world.MyPlane);
            if (ground != null)
            {
                _client.SendPickup(ground.ItemId, tileX, tileY);
                return;
            }

            _client.WalkTo(tileX, tileY);
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            Point canvas = ToCanvas(e.Location);

            // A hand cursor is the only affordance that says "this is clickable" on a
            // screen with no hover states.
            if (_loginMode)
            {
                Cursor = IsOverLoginControl(canvas) ? Cursors.Hand : Cursors.Default;
                return;
            }

            if (!_dragging) return;

            // Drag in screen space and convert back through the projection, so panning
            // feels correct whatever the camera rotation is.
            int dxPixels = canvas.X - _dragStart.X;
            int dyPixels = canvas.Y - _dragStart.Y;

            // Inverse of the isometric projection for a zero-height plane.
            double a = (double)dxPixels / IsoProjection.HalfWidth;
            double b = (double)dyPixels / IsoProjection.HalfHeight;

            double vdx = (a + b) * 0.5;
            double vdy = (b - a) * 0.5;

            int wdx, wdy;
            IsoProjection.ViewDirectionToWorld(_world.CameraRotation,
                (int)Math.Round(vdx), (int)Math.Round(vdy), out wdx, out wdy);

            _world.CameraOffsetX = Clamp(_dragCameraX - wdx, -14, 14);
            _world.CameraOffsetY = Clamp(_dragCameraY - wdy, -14, 14);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) _dragging = false;
        }

        private void OnMouseWheel(object sender, MouseEventArgs e)
        {
            // Zoom is expressed as camera distance; kept simple and clamped.
            if (e.Delta > 0) _world.CameraOffsetY = Math.Min(8, _world.CameraOffsetY + 1);
            else _world.CameraOffsetY = Math.Max(-8, _world.CameraOffsetY - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _timer.Stop(); } catch (Exception) { }
                try { _timer.Dispose(); } catch (Exception) { }
                try { _client.Disconnect(); } catch (Exception) { }
                try { if (_framebuffer != null) _framebuffer.Dispose(); } catch (Exception) { }
                try { if (_text != null) _text.Dispose(); } catch (Exception) { }
            }
            base.Dispose(disposing);
        }
    }
}
