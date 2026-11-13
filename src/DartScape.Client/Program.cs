using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
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

                    case "--version":
                        Console.WriteLine("DartScape client 0.1.0");
                        Environment.Exit(0);
                        break;
                }
            }
        }
    }
}
