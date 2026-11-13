using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using DartScape.Core.Cache;
using DartScape.Core.Crypto;
using DartScape.Core.Diagnostics;
using DartScape.Core.Runtime;
using DartScape.Game.Data;
using DartScape.Game.Definitions;
using DartScape.Game;
using DartScape.Game.Commands;
using DartScape.Server.Net;

namespace DartScape.Server
{
    /// <summary>
    /// Entry point and process-level wiring for the game server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Startup order matters: logging first (so everything after it is recorded), then
    /// configuration, then the RSA key, then the cache, then the world, then the socket.
    /// Each step that can fail is allowed to fail in a way that either degrades the
    /// server (no definitions) or stops it with a clear message (cannot bind), never in a
    /// way that leaves a half-started process.
    /// </para>
    /// <para>
    /// The accept loop and the tick loop run on separate threads and share nothing except
    /// the world's input queue. Everything else is single threaded by construction.
    /// </para>
    /// </remarks>
    public static class Program
    {
        /// <summary>The loaded cache, exposed for the ::reload command.</summary>
        public static FileStore Cache;

        /// <summary>The player store, exposed for the ::save command.</summary>
        public static PlayerStore Store;

        /// <summary>The world instance.</summary>
        public static World World;

        /// <summary>The effective configuration.</summary>
        public static ServerConfig Config;

        /// <summary>The web service (public site + admin panel), or <c>null</c>.</summary>
        public static Web.WebHost Web;

        /// <summary>The NAT-hairpin relay, or <c>null</c>.</summary>
        public static Relay.RelayServer Relay;

        private static readonly List<GameSession> Sessions = new List<GameSession>();
        private static readonly object SessionLock = new object();
        private static readonly ManualResetEventSlim Shutdown = new ManualResetEventSlim(false);
        private static TcpListener _listener;

        public static int Main(string[] args)
        {
            // Logging comes first so that even a configuration failure is recorded.
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            Log.Configure("server", new LogOptions
            {
                Directory = Path.Combine(baseDir, "logs"),
                EchoToConsole = true,
                MinimumLevel = LogLevel.Information
            });
            Log.InstallCrashHandlers();

            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                Log.Exception("Server", ex, "Fatal error during startup");
                return 1;
            }
            finally
            {
                Log.Flush();
            }
        }

        private static int Run(string[] args)
        {
            Config = ServerConfig.Load(AppDomain.CurrentDomain.BaseDirectory);

            // Re-apply logging settings now that the config is known.
            Log.Configure("server", new LogOptions
            {
                Directory = Config.Resolve(Config.LogDir),
                EchoToConsole = Config.LogToConsole,
                MinimumLevel = Config.LogLevel
            });

            Log.Info("Server", "DartScape {0} ({1}) starting on port {2}.",
                Config.Name, Config.Region, Config.Port);

            // --- RSA key -------------------------------------------------------
            string keyPath = Config.Resolve(Config.KeyFile);
            RsaKeyPair key = RsaKeyPair.LoadOrCreate(keyPath);
            Log.Info("Server", "Login key ready ({0} bits).", key.ModulusBytes.Length * 8);

            // --- cache ---------------------------------------------------------
            Cache = FileStore.Open(Config.Resolve(Config.CacheDir));
            if (Cache == null)
            {
                Log.Warning("Server", "No cache at {0}; item and NPC data will be unavailable.",
                    Config.Resolve(Config.CacheDir));
            }
            else
            {
                DefinitionLoader.Load(Cache);
            }

            if (!DefinitionLoader.HasItems)
            {
                Log.Warning("Server", "No item definitions loaded. Items will not be usable.");
            }

            // --- player store --------------------------------------------------
            Store = new PlayerStore(Config);
            Store.SetAllowRegistration(Config.AllowRegistration);

            // --- world ---------------------------------------------------------
            World = new World(Config);

            // Load the real landscape from the same cache the client reads. Collision, and
            // therefore pathfinding, comes from it: without this the server would be
            // validating steps against invented terrain and disagreeing with what the player
            // can see.
            WorldMap map = WorldMap.Open(Config.Resolve(Config.CacheDir));
            World.UseMap(map);

            SpawnWorldNpcs(World);

            // Publish the long-lived services so the game layer and the web layer can
            // reach them without threading three references through every constructor.
            GameServices.Cache = Cache;
            GameServices.Store = Store;
            GameServices.World = World;
            GameServices.Config = Config;

            // The world must not know about packet encoding, so the encoder is injected.
            World.UpdateSender = UpdateEncoder.SendUpdates;

            // --- listener ------------------------------------------------------
            IPAddress bindAddress = ResolveBindAddress(Config.Bind);
            try
            {
                _listener = new TcpListener(bindAddress, Config.Port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start(Config.Backlog);
            }
            catch (SocketException ex)
            {
                Log.Critical("Server", "Could not bind {0}:{1} - {2}", Config.Bind, Config.Port, ex.Message);
                Log.Critical("Server", "Is another copy already running, or is the port blocked?");
                return 2;
            }

            Log.Info("Server", "Listening on {0}:{1}.", Config.Bind, Config.Port);
            Log.Info("Server", "Admin token: {0}", Config.AdminToken);

            // --- web service ---------------------------------------------------
            try
            {
                Web = new Web.WebHost(World, Config, Store);
                Web.Start();
            }
            catch (Exception ex)
            {
                Log.Error("Server", "The web service could not start: {0}", ex.Message);
                Web = null;
            }

            // --- relay ---------------------------------------------------------
            if (Config.RelayEnabled)
            {
                try
                {
                    Relay = new Relay.RelayServer(Config.RelayPort, Config.RelayMaxTunnels, Config.RelayRateLimit);
                    Relay.Start();
                }
                catch (Exception ex)
                {
                    Log.Error("Server", "The relay could not start on port {0}: {1}", Config.RelayPort, ex.Message);
                    Relay = null;
                }
            }

            Log.Info("Server", "Press Ctrl+C to stop.");

            var loginService = new LoginService(key, Config.Revision);

            var acceptThread = new Thread(() => AcceptLoop(loginService))
            {
                IsBackground = true,
                Name = "DartScape.Accept"
            };
            acceptThread.Start();

            var tickThread = new Thread(TickLoop)
            {
                IsBackground = true,
                Name = "DartScape.Tick"
            };
            tickThread.Start();

            var consoleThread = new Thread(ConsoleLoop)
            {
                IsBackground = true,
                Name = "DartScape.Console"
            };
            consoleThread.Start();

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Shutdown.Set();
            };

            Shutdown.Wait();

            Log.Info("Server", "Shutting down...");
            Stop();

            int saved = Store.SaveAll(World);
            Log.Info("Server", "Saved {0} character(s).", saved);
            Log.Shutdown();
            return 0;
        }

        private static void Stop()
        {
            try { if (_listener != null) _listener.Stop(); } catch (Exception) { }

            try { if (Web != null) Web.Dispose(); } catch (Exception) { }
            try { if (Relay != null) Relay.Dispose(); } catch (Exception) { }

            List<GameSession> sessions;
            lock (SessionLock)
            {
                sessions = new List<GameSession>(Sessions);
            }

            foreach (GameSession session in sessions)
            {
                try { session.Disconnect("server shutting down"); } catch (Exception) { }
            }

            if (Cache != null)
            {
                try { Cache.Dispose(); } catch (Exception) { }
                Cache = null;
            }
        }

        private static IPAddress ResolveBindAddress(string text)
        {
            if (string.IsNullOrEmpty(text) || text == "*" || text == "+" || text == "0.0.0.0")
            {
                return IPAddress.Any;
            }

            if (string.Equals(text, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return IPAddress.Loopback;
            }

            IPAddress parsed;
            if (IPAddress.TryParse(text, out parsed)) return parsed;

            Log.Warning("Server", "Could not parse bind address '{0}'; using all interfaces.", text);
            return IPAddress.Any;
        }

        private static void AcceptLoop(LoginService loginService)
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException ex)
                {
                    if (Shutdown.IsSet) return;
                    Log.Warning("Server", "Accept failed: {0}", ex.SocketErrorCode);
                    Thread.Sleep(250);
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                int online;
                lock (SessionLock) { online = Sessions.Count; }

                if (online >= Config.MaxPlayers)
                {
                    Log.Warning("Server", "Refusing connection: world full ({0}).", online);
                    try
                    {
                        client.GetStream().WriteByte(7);        // world full
                        client.Close();
                    }
                    catch (Exception) { }
                    continue;
                }

                var session = new GameSession(client.Client, World, Config, loginService, Store);

                lock (SessionLock)
                {
                    Sessions.Add(session);
                }

                var thread = new Thread(() =>
                {
                    try
                    {
                        session.Run();
                    }
                    finally
                    {
                        lock (SessionLock) { Sessions.Remove(session); }
                    }
                })
                {
                    IsBackground = true,
                    Name = "DartScape.Session"
                };
                thread.Start();
            }
        }

        private static void TickLoop()
        {
            var stopwatch = new Stopwatch();
            int tickMs = Config.TickMs;

            while (!Shutdown.IsSet)
            {
                stopwatch.Restart();

                World.Tick();

                long elapsed = stopwatch.ElapsedMilliseconds;
                long sleep = tickMs - elapsed;

                if (sleep > 0)
                {
                    // Sleep in slices so shutdown is responsive.
                    long remaining = sleep;
                    while (remaining > 0 && !Shutdown.IsSet)
                    {
                        int slice = (int)Math.Min(50, remaining);
                        if (Shutdown.Wait(slice)) break;
                        remaining -= slice;
                    }
                }
                else if (elapsed > tickMs * 4)
                {
                    Log.Warning("Server", "Tick took {0} ms (budget {1} ms). The world is falling behind.",
                        elapsed, tickMs);
                }
            }
        }

        private static void ConsoleLoop()
        {
            while (!Shutdown.IsSet)
            {
                string line;
                try
                {
                    line = Console.ReadLine();
                }
                catch (Exception)
                {
                    return;
                }

                if (line == null) return;                  // stdin closed
                line = line.Trim();
                if (line.Length == 0) continue;

                if (string.Equals(line, "exit", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(line, "quit", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(line, "stop", StringComparison.OrdinalIgnoreCase))
                {
                    Shutdown.Set();
                    return;
                }

                if (string.Equals(line, "status", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Players: {0}/{1}  NPCs: {2}  Ticks: {3}  Avg tick: {4:0.0} ms  Uptime: {5}",
                        World.PlayerCount, Config.MaxPlayers, World.NpcCount, World.TickCount,
                        World.AverageTickMs, Core.TimeUtil.Describe(World.Uptime));
                    continue;
                }

                if (string.Equals(line, "save", StringComparison.OrdinalIgnoreCase))
                {
                    int saved = Store.SaveAll(World);
                    Console.WriteLine("Saved {0} character(s).", saved);
                    continue;
                }

                // Anything else is a command run with console (full) privileges.
                CommandDispatcher.Result result =
                    CommandDispatcher.Execute(World, null, line);

                foreach (string output in result.Lines) Console.WriteLine(output);
            }
        }

        /// <summary>
        /// Spawns the starting population of NPCs.
        /// </summary>
        /// <remarks>
        /// A real server reads spawn tables from the map data. This cache revision does not
        /// expose object/NPC spawn data in a form the server parses yet, so the world is
        /// populated from a small curated table around the starting area. That is enough to
        /// make the world feel alive and to exercise every NPC code path; the TODO is
        /// documented in docs/PIPELINE.md.
        /// </remarks>
        private static void SpawnWorldNpcs(World world)
        {
            if (!DefinitionLoader.HasNpcs) return;

            // Find usable definitions by name rather than by hard-coded id, because ids
            // differ between cache revisions.
            var chosen = new List<int>();

            string[] wanted = { "man", "woman", "guard", "goblin", "chicken", "cow", "rat", "wizard", "farmer" };

            foreach (string name in wanted)
            {
                for (int id = 0; id < DefinitionLoader.Npcs.Length; id++)
                {
                    NpcDefinition def = DefinitionLoader.Npcs[id];
                    if (def == null || def.Name == null) continue;
                    if (!string.Equals(def.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (def.CombatLevel < 0) continue;

                    chosen.Add(id);
                    break;
                }
            }

            if (chosen.Count == 0)
            {
                Log.Info("Server", "No matching NPC definitions found; the world starts empty.");
                return;
            }

            int spawned = 0;
            var random = new Random(1337);

            for (int i = 0; i < 60; i++)
            {
                int definitionId = chosen[random.Next(chosen.Count)];

                int x = world.Config.StartX + random.Next(-12, 13);
                int y = world.Config.StartY + random.Next(-12, 13);

                if (world.SpawnNpc(definitionId, new Position(x, y, world.Config.StartPlane)) != null)
                {
                    spawned++;
                }
            }

            Log.Info("Server", "Spawned {0} NPCs from {1} distinct definitions.", spawned, chosen.Count);
        }
    }
}
