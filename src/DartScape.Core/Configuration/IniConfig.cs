using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DartScape.Core.Diagnostics;

namespace DartScape.Core.Configuration
{
    /// <summary>
    /// A small, dependency-free configuration store backed by a plain
    /// <c>key = value</c> text file with <c>[section]</c> headers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why not JSON/XML? Because every .NET XML parser on XP-era machines has been
    /// a source of pain at some point, and hand-editing a config at 3am should not
    /// require remembering whether a trailing comma is legal. INI is boring, and
    /// boring is exactly what a config file should be.
    /// </para>
    /// <para>
    /// Values are looked up as <c>section.key</c>. Missing files are not an error:
    /// every setting has a compiled-in default, and <see cref="Save"/> writes the
    /// current state back out so the file documents itself.
    /// </para>
    /// </remarks>
    public sealed class IniConfig
    {
        private readonly Dictionary<string, string> _values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly List<string> _order = new List<string>();
        private readonly string _path;
        private readonly object _sync = new object();

        /// <summary>Full path of the backing file.</summary>
        public string Path
        {
            get { return _path; }
        }

        private IniConfig(string path)
        {
            _path = path;
        }

        /// <summary>
        /// Creates an empty configuration bound to <paramref name="path"/> without reading
        /// or writing the file.
        /// </summary>
        /// <remarks>
        /// Used when a caller is about to build a file from scratch: loading first would
        /// create an empty file on disk and log a misleading "no config, writing defaults"
        /// message.
        /// </remarks>
        public static IniConfig Create(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            return new IniConfig(System.IO.Path.GetFullPath(path));
        }

        /// <summary>
        /// Loads <paramref name="path"/>, creating it with the supplied defaults when
        /// it does not exist yet. Never throws for I/O problems — it logs and
        /// continues with defaults, because a game server that refuses to boot
        /// because a config file is read-only is a worse outcome than one running
        /// with defaults.
        /// </summary>
        public static IniConfig Load(string path, IDictionary<string, string> defaults = null)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");

            var config = new IniConfig(System.IO.Path.GetFullPath(path));

            if (defaults != null)
            {
                foreach (KeyValuePair<string, string> kv in defaults)
                {
                    config.SetRaw(kv.Key, kv.Value);
                }
            }

            if (File.Exists(config._path))
            {
                try
                {
                    config.Parse(File.ReadAllLines(config._path, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    Log.Warning("Config", "Could not read {0}: {1}. Using defaults.", config._path, ex.Message);
                }
            }
            else
            {
                Log.Info("Config", "No config at {0}; writing defaults.", config._path);
                config.Save();
            }

            return config;
        }

        private void Parse(string[] lines)
        {
            string section = string.Empty;
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();

                // Strip a single pair of surrounding quotes, keep everything else verbatim.
                if (value.Length >= 2 &&
                    ((value[0] == '"' && value[value.Length - 1] == '"') ||
                     (value[0] == '\'' && value[value.Length - 1] == '\'')))
                {
                    value = value.Substring(1, value.Length - 2);
                }

                string full = section.Length == 0 ? key : section + "." + key;
                SetRaw(full, value);
            }
        }

        private void SetRaw(string key, string value)
        {
            if (!_values.ContainsKey(key)) _order.Add(key);
            _values[key] = value;
        }

        // -- typed getters ----------------------------------------------------

        public string GetString(string key, string fallback)
        {
            string v;
            lock (_sync)
            {
                return _values.TryGetValue(key, out v) && v != null ? v : fallback;
            }
        }

        public int GetInt(string key, int fallback)
        {
            string raw = GetString(key, null);
            if (raw == null) return fallback;

            int parsed;
            if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed;

            Log.Warning("Config", "{0} = '{1}' is not an integer; using {2}.", key, raw, fallback);
            return fallback;
        }

        public long GetLong(string key, long fallback)
        {
            string raw = GetString(key, null);
            if (raw == null) return fallback;

            long parsed;
            if (long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed;

            Log.Warning("Config", "{0} = '{1}' is not a number; using {2}.", key, raw, fallback);
            return fallback;
        }

        public bool GetBool(string key, bool fallback)
        {
            string raw = GetString(key, null);
            if (raw == null) return fallback;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":
                case "y":
                    return true;
                case "0":
                case "false":
                case "no":
                case "off":
                case "n":
                    return false;
                default:
                    Log.Warning("Config", "{0} = '{1}' is not a boolean; using {2}.", key, raw, fallback);
                    return fallback;
            }
        }

        public double GetDouble(string key, double fallback)
        {
            string raw = GetString(key, null);
            if (raw == null) return fallback;

            double parsed;
            if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;

            Log.Warning("Config", "{0} = '{1}' is not a number; using {2}.", key, raw, fallback);
            return fallback;
        }

        /// <summary>Reads a comma separated list, trimming each element and dropping empties.</summary>
        public string[] GetList(string key, params string[] fallback)
        {
            string raw = GetString(key, null);
            if (raw == null) return fallback ?? new string[0];

            string[] parts = raw.Split(',');
            var result = new List<string>(parts.Length);
            foreach (string p in parts)
            {
                string t = p.Trim();
                if (t.Length != 0) result.Add(t);
            }
            return result.Count == 0 ? (fallback ?? new string[0]) : result.ToArray();
        }

        /// <summary>Sets a value in memory only; call <see cref="Save"/> to persist.</summary>
        public void Set(string key, string value)
        {
            lock (_sync)
            {
                SetRaw(key, value ?? string.Empty);
            }
        }

        /// <summary>
        /// Writes the whole store back out grouped by section, in insertion order.
        /// </summary>
        public bool Save()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var sb = new StringBuilder(4096);
                sb.AppendLine("# DartScape configuration");
                sb.AppendLine("# Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                sb.AppendLine("# Format: [section] header, then key = value. # and ; start comments.");
                sb.AppendLine();

                string currentSection = null;

                lock (_sync)
                {
                    foreach (string key in _order)
                    {
                        string section = string.Empty;
                        string leaf = key;

                        int dot = key.LastIndexOf('.');
                        if (dot > 0)
                        {
                            section = key.Substring(0, dot);
                            leaf = key.Substring(dot + 1);
                        }

                        if (!string.Equals(section, currentSection, StringComparison.OrdinalIgnoreCase))
                        {
                            if (currentSection != null) sb.AppendLine();
                            if (section.Length != 0) sb.AppendLine("[" + section + "]");
                            currentSection = section;
                        }

                        string value = _values[key] ?? string.Empty;
                        bool needsQuotes = value.IndexOf('#') >= 0 || value.IndexOf(';') >= 0 ||
                                           (value.Length > 0 && (value[0] == ' ' || value[value.Length - 1] == ' '));
                        sb.Append(leaf).Append(" = ");
                        sb.AppendLine(needsQuotes ? "\"" + value + "\"" : value);
                    }
                }

                File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("Config", "Could not save {0}: {1}", _path, ex.Message);
                return false;
            }
        }
    }
}
