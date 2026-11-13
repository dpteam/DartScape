using System;
using System.Globalization;
using System.IO;

namespace DartScape.Launcher
{
    /// <summary>
    /// Launcher preferences, persisted as a flat <c>key = value</c> file.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from the game client's config: the launcher is what writes
    /// the client's command line, so the client never needs to know these values, and a
    /// corrupted launcher file cannot break a hand-configured client.
    /// </remarks>
    public sealed class LauncherSettings
    {
        /// <summary>Launcher version, shown in the interface.</summary>
        public const string Version = "0.1.0";

        /// <summary>Default game port.</summary>
        public const int DefaultPort = 43594;

        /// <summary>Default server, matching the hosted instance.</summary>
        public const string DefaultServer = "rune.dartpower.xyz:43594";

        public string Name = "";
        public string Password = "";
        public string Server = DefaultServer;
        public string CacheDir = "";
        public string Theme = "light";
        public int Width = 765;
        public int Height = 503;
        public bool Sound = true;
        public bool Music;

        private string _path;

        /// <summary>Host part of <see cref="Server"/>.</summary>
        public string Host
        {
            get
            {
                string host;
                int port;
                return SplitAddress(Server, out host, out port) ? host : Server;
            }
        }

        /// <summary>Port part of <see cref="Server"/>.</summary>
        public int Port
        {
            get
            {
                string host;
                int port;
                return SplitAddress(Server, out host, out port) ? port : DefaultPort;
            }
        }

        /// <summary>Splits <c>host:port</c>, tolerating a missing port and stray spaces.</summary>
        public static bool SplitAddress(string text, out string host, out int port)
        {
            host = null;
            port = DefaultPort;

            if (string.IsNullOrEmpty(text)) return false;

            string trimmed = text.Trim();

            // Tolerate a full URL pasted in by mistake.
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed.Substring(7);
            if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed.Substring(8);

            int slash = trimmed.IndexOf('/');
            if (slash >= 0) trimmed = trimmed.Substring(0, slash);

            int colon = trimmed.LastIndexOf(':');
            if (colon > 0)
            {
                host = trimmed.Substring(0, colon).Trim();

                int parsed;
                if (int.TryParse(trimmed.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) &&
                    parsed > 0 && parsed < 65536)
                {
                    port = parsed;
                }
            }
            else
            {
                host = trimmed;
            }

            return !string.IsNullOrEmpty(host);
        }

        /// <summary>Directory holding settings, logs and the generated launcher page.</summary>
        public static string DataDirectory
        {
            get
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string data = Path.Combine(baseDir, "launcher-data");

                try
                {
                    Directory.CreateDirectory(data);
                }
                catch (Exception)
                {
                    // A read-only install directory should not stop the launcher; fall
                    // back to the user's application data.
                    data = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "DartScape");
                    Directory.CreateDirectory(data);
                }

                return data;
            }
        }

        /// <summary>Loads the settings file, or returns defaults when it is absent.</summary>
        public static LauncherSettings Load()
        {
            var settings = new LauncherSettings();
            settings._path = Path.Combine(DataDirectory, "launcher.ini");

            try
            {
                if (!File.Exists(settings._path))
                {
                    settings.Save();
                    return settings;
                }

                foreach (string raw in File.ReadAllLines(settings._path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "name": settings.Name = value; break;
                        case "password": settings.Password = value; break;
                        case "server": settings.Server = value; break;
                        case "cache": settings.CacheDir = value; break;
                        case "theme": settings.Theme = value; break;
                        case "width": settings.Width = ParseInt(value, 765); break;
                        case "height": settings.Height = ParseInt(value, 503); break;
                        case "sound": settings.Sound = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "music": settings.Music = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    }
                }
            }
            catch (Exception)
            {
                // A corrupt file must not stop the launcher; defaults are fine.
            }

            if (settings.Width < 640) settings.Width = 765;
            if (settings.Height < 480) settings.Height = 503;
            if (string.IsNullOrEmpty(settings.Server)) settings.Server = DefaultServer;
            if (settings.Theme != "light" && settings.Theme != "dark" && settings.Theme != "oled") settings.Theme = "light";

            return settings;
        }

        /// <summary>Writes the settings file atomically.</summary>
        public void Save()
        {
            try
            {
                if (string.IsNullOrEmpty(_path)) _path = Path.Combine(DataDirectory, "launcher.ini");

                var sb = new System.Text.StringBuilder(512);
                sb.AppendLine("# DartScape launcher settings");
                sb.AppendLine("# Delete this file to reset the launcher.");
                sb.AppendLine("name=" + (Name ?? string.Empty));
                sb.AppendLine("password=" + (Password ?? string.Empty));
                sb.AppendLine("server=" + (Server ?? DefaultServer));
                sb.AppendLine("cache=" + (CacheDir ?? string.Empty));
                sb.AppendLine("theme=" + (Theme ?? "light"));
                sb.AppendLine("width=" + Width.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("height=" + Height.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("sound=" + (Sound ? "1" : "0"));
                sb.AppendLine("music=" + (Music ? "1" : "0"));

                string temporary = _path + ".tmp";
                File.WriteAllText(temporary, sb.ToString(), new System.Text.UTF8Encoding(false));

                if (File.Exists(_path)) File.Delete(_path);
                File.Move(temporary, _path);
            }
            catch (Exception)
            {
                // Losing settings is annoying; failing to start is worse.
            }
        }

        /// <summary>
        /// Locates the client executable: next to the launcher, or one level up in a
        /// development tree.
        /// </summary>
        public static string FindClientExecutable()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            string[] candidates =
            {
                Path.Combine(baseDir, "DartScape.Client.exe"),
                Path.Combine(baseDir, @"..\DartScape.Client\DartScape.Client.exe"),
                Path.Combine(baseDir, @"..\..\DartScape.Client\DartScape.Client.exe")
            };

            foreach (string candidate in candidates)
            {
                string full = Path.GetFullPath(candidate);
                if (File.Exists(full)) return full;
            }

            return null;
        }

        private static int ParseInt(string text, int fallback)
        {
            int parsed;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }
    }
}
