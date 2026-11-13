using System;
using System.IO;
using System.Windows.Forms;
using DartScape.Core.Diagnostics;

namespace DartScape.Launcher
{
    /// <summary>
    /// Launcher entry point.
    /// </summary>
    /// <remarks>
    /// Two things happen before the window appears, and both matter on old machines:
    /// the MSHTML document-mode override is written to the registry so the interface is
    /// not rendered by IE7's engine, and the browser emulation value is set per executable
    /// name so it does not affect anything else on the system.
    /// </remarks>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Log.Configure("launcher", new LogOptions
            {
                Directory = Path.Combine(LauncherSettings.DataDirectory, "logs"),
                EchoToConsole = false,
                MinimumLevel = LogLevel.Information
            });
            Log.InstallCrashHandlers();

            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                Log.Exception("Launcher", ex, "Fatal launcher error");
                MessageBox.Show(
                    "The DartScape launcher could not start.\n\n" + ex.Message,
                    "DartScape", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            finally
            {
                Log.Shutdown();
            }
        }

        private static int Run(string[] args)
        {
            if (args != null)
            {
                foreach (string arg in args)
                {
                    if (string.Equals(arg, "--version", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("DartScape launcher " + LauncherSettings.Version);
                        return 0;
                    }
                }
            }

            EnableModernDocumentMode();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += (s, e) =>
            {
                Log.Exception("Launcher", e.Exception, "Unhandled launcher exception");
                MessageBox.Show(
                    "An unexpected error occurred in the launcher.\n\n" + e.Exception.Message,
                    "DartScape", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };

            Log.Info("Launcher", "Starting, version {0}.", LauncherSettings.Version);

            using (var form = new LauncherForm())
            {
                Application.Run(form);
            }

            Log.Info("Launcher", "Exited cleanly.");
            return 0;
        }

        /// <summary>
        /// Asks MSHTML to render the interface with its newest available engine.
        /// </summary>
        /// <remarks>
        /// Without this the page is rendered in IE7 mode, which breaks the CSS badly.
        /// The value is per executable name under the current user, so it is harmless to
        /// set on every launch and never requires administrator rights.
        /// </remarks>
        private static void EnableModernDocumentMode()
        {
            const string keyPath =
                @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION";

            try
            {
                string exeName = Path.GetFileName(Application.ExecutablePath);

                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath))
                {
                    if (key == null) return;

                    // 11001 = IE11 edge mode. Older systems ignore the value and use the
                    // newest engine they have, which is exactly what we want.
                    key.SetValue(exeName, 11001, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                // Locked-down machines may forbid this. The launcher still works, it just
                // looks older.
                Log.Debug("Launcher", "Could not set the document mode: {0}", ex.Message);
            }
        }
    }
}
