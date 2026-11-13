using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using DartScape.Client.Rendering;
using DartScape.Core.Diagnostics;

namespace DartScape.Client
{
    /// <summary>
    /// Client entry point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client accepts the same positional argument the launcher passes to the game:
    /// <c>--server host:port --name NAME --cache PATH</c>. Anything absent falls back to
    /// <c>config.ini</c>, so a user can double-click the exe and still log in by typing.
    /// </para>
    /// <para>
    /// A single-instance guard prevents the classic double-launch, where a second copy
    /// connects and kicks the first one off the world.
    /// </para>
    /// </remarks>
    public static class Program
    {
        private static Mutex _instanceMutex;

        [STAThread]
        public static int Main(string[] args)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            Log.Configure("client", new LogOptions
            {
                Directory = Path.Combine(baseDir, "logs"),
                EchoToConsole = false,
                MinimumLevel = LogLevel.Information
            });
            Log.InstallCrashHandlers();

            try
            {
                return Run(args, baseDir);
            }
            catch (Exception ex)
            {
                Log.Exception("Client", ex, "Fatal client error");
                MessageBox.Show(
                    "DartScape could not start.\n\n" + ex.Message +
                    "\n\nDetails were written to the logs folder.",
                    "DartScape", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            finally
            {
                Log.Shutdown();
            }
        }

        private static int Run(string[] args, string baseDir)
        {
            // Single instance: a second client would fight the first for the session.
            bool created;
            _instanceMutex = new Mutex(true, "DartScape.Client.SingleInstance", out created);
            if (!created)
            {
                MessageBox.Show("DartScape is already running.", "DartScape",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            ClientConfig config = ClientConfig.Load(baseDir);
            ApplyArguments(args, config);

            LoadMap(config, baseDir);

            // Offline render mode: draw one frame and exit. Kept in the shipped binary
            // because it is the only way to inspect the renderer without a display, and
            // because CI uses it to prove the renderer does not throw.
            string shotPath = Environment.GetEnvironmentVariable("DARTSCAPE_SHOT");
            if (!string.IsNullOrEmpty(shotPath))
            {
                return RenderScreenshot(config, shotPath);
            }

            Log.Info("Client", "Starting against {0}:{1}.", config.Host, config.Port);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Keep an unhandled UI exception from killing the process silently.
            Application.ThreadException += (s, e) =>
            {
                Log.Exception("Client", e.Exception, "Unhandled UI exception");
                MessageBox.Show(
                    "An unexpected error occurred.\n\n" + e.Exception.Message +
                    "\n\nThe client will keep running; details are in the logs folder.",
                    "DartScape", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };

            using (var window = new GameWindow(config))
            {
                Application.Run(window);
            }

            Log.Info("Client", "Exited cleanly.");
            return 0;
        }

        /// <summary>
        /// Loads the world map from the cache, or leaves the procedural fallback in place.
        /// </summary>
        /// <remarks>
        /// The client reads the same cache the server does, from its own copy on disk. That is
        /// how both sides agree about where the walls are without the server sending map data:
        /// the map is part of the game, not part of the session.
        /// </remarks>
        private static void LoadMap(ClientConfig config, string baseDir)
        {
            string directory = string.IsNullOrEmpty(config.CacheDir)
                ? System.IO.Path.Combine(baseDir, "cache")
                : System.IO.Path.IsPathRooted(config.CacheDir)
                    ? config.CacheDir
                    : System.IO.Path.Combine(baseDir, config.CacheDir);

            try
            {
                DartScape.Game.WorldMap map = DartScape.Game.WorldMap.Open(directory);

                if (map == null)
                {
                    Log.Warning("Client", "No usable map in {0}; using procedural terrain.", directory);
                    return;
                }

                Rendering.Terrain.Map = map;

                Log.Info("Client", "Loaded {0} map regions from {1}.", map.RegionCount, directory);
            }
            catch (Exception ex)
            {
                Log.Exception("Client", ex, "Could not load the map from " + directory);
            }
        }

        /// <summary>
        /// Renders one frame of the world to a PNG and exits.
        /// </summary>
        /// <remarks>
        /// Uses a synthetic world rather than a live connection: the point is to exercise the
        /// renderer, so it needs terrain, props, entities and the interface all present, and
        /// it must work with no server running.
        /// </remarks>
        private static int RenderScreenshot(ClientConfig config, string path)
        {
            try
            {
                var world = new ClientWorld();
                var me = new ClientPlayer
                {
                    Index = 1,
                    Name = "DartPower",
                    IsLocal = true,
                    X = 3222,
                    Y = 3218,
                    LastX = 3222,
                    LastY = 3218,
                    CombatLevel = 42,
                    Hitpoints = 34,
                    MaxHitpoints = 43,
                    AppearanceReceived = true
                };

                me.Colours[0] = 2;
                me.Colours[1] = 10;
                me.Colours[2] = 21;
                me.Colours[4] = 3;
                me.EquipmentIds[3] = 1;

                world.Players[1] = me;
                world.MyIndex = 1;
                world.ForceLocalPosition(3222, 3218, 0);

                // A few NPCs at plausible offsets so the sorting and occlusion are exercised.
                int[] offsets = { 3, -2, -4, 5, 2, 6, -3, 4 };
                string[] names = { "Man", "Guard", "Chicken", "Goblin", "Farmer", "Woman", "Cow", "Guard" };
                int[] levels = { 0, 21, 1, 2, 0, 0, 0, 21 };

                for (int i = 0; i < offsets.Length; i++)
                {
                    var npc = new ClientNpc
                    {
                        Index = 100 + i,
                        Name = names[i],
                        CombatLevel = levels[i],
                        X = 3222 + offsets[i],
                        Y = 3218 + offsets[(i + 3) % offsets.Length],
                        LastX = 3222 + offsets[i],
                        LastY = 3218 + offsets[(i + 3) % offsets.Length],
                        Hitpoints = 10,
                        MaxHitpoints = 10
                    };

                    world.Npcs[npc.Index] = npc;
                }

                world.GroundItems.Add(new ClientGroundItem
                {
                    ItemId = 995, Amount = 12, X = 3220, Y = 3220, Plane = 0
                });

                for (int i = 0; i < 21; i++)
                {
                    world.BaseLevel[i] = 1;
                    world.CurrentLevel[i] = 1;
                }
                world.BaseLevel[3] = 43;
                world.BaseLevel[0] = 42;
                world.CurrentLevel[3] = 34;

                world.AddChatMessage("Welcome to DartScape.", 2);
                world.AddChatMessage("Type ::help for a list of commands.", 2);

                using (var framebuffer = new Framebuffer(GameWindow.CanvasWidth, GameWindow.CanvasHeight))
                using (var text = new FontRenderer())
                {
                    var renderer = new WorldRenderer(text);
                    var hud = new Hud(text);

                    int viewWidth = GameWindow.CanvasWidth - Hud.SidebarWidth;
                    int viewHeight = GameWindow.CanvasHeight - Hud.ChatHeight;

                    framebuffer.FillRect(0, 0, GameWindow.CanvasWidth, GameWindow.CanvasHeight,
                        Framebuffer.Rgb(20, 26, 20));

                    renderer.Draw(framebuffer, world, 0, 0, viewWidth, viewHeight, 0.5);
                    renderer.DrawMinimap(framebuffer, world,
                        viewWidth - 150, Hud.TopBarHeight + 6, 144);
                    hud.Draw(framebuffer, world, GameWindow.CanvasWidth, GameWindow.CanvasHeight, null);

                    framebuffer.SavePng(path);
                }

                Console.WriteLine("Rendered " + path);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Exception("Client", ex, "Screenshot render failed");
                Console.Error.WriteLine("Render failed: " + ex.Message);
                return 1;
            }
        }

        /// <summary>Applies <c>--flag value</c> pairs over the config file.</summary>
        private static void ApplyArguments(string[] args, ClientConfig config)
        {
            if (args == null) return;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string value = i + 1 < args.Length ? args[i + 1] : null;

                switch (arg.ToLowerInvariant())
                {
                    case "--server":
                    case "-s":
                        if (value == null) break;
                        int colon = value.LastIndexOf(':');
                        if (colon > 0)
                        {
                            config.Host = value.Substring(0, colon);
                            int port;
                            if (int.TryParse(value.Substring(colon + 1), out port)) config.Port = port;
                        }
                        else
                        {
                            config.Host = value;
                        }
                        i++;
                        break;

                    case "--name":
                    case "-n":
                        if (value != null) { config.Name = value; i++; }
                        break;

                    case "--password":
                    case "-p":
                        if (value != null) { config.Password = value; i++; }
                        break;

                    case "--cache":
                        if (value != null) { config.CacheDir = value; i++; }
                        break;

                    case "--width":
                        if (value != null) { int w; if (int.TryParse(value, out w)) config.Width = Math.Max(640, w); i++; }
                        break;

                    case "--height":
                        if (value != null) { int h; if (int.TryParse(value, out h)) config.Height = Math.Max(480, h); i++; }
                        break;

                    case "--smoke":
                        // Used by CI: start, draw one frame, exit. Never opens a visible window
                        // for longer than a moment.
                        Environment.SetEnvironmentVariable("DARTSCAPE_SMOKE", "1");
                        break;

                    case "--screenshot":
                        // Renders one frame offline and writes it to a PNG. Used to inspect the
                        // renderer without a display, and by CI to catch a renderer that throws.
                        if (value != null) { Environment.SetEnvironmentVariable("DARTSCAPE_SHOT", value); i++; }
                        break;

                    case "--version":
                        Console.WriteLine("DartScape client 0.1.0");
                        Environment.Exit(0);
                        break;
                }
            }
        }
    }
}
