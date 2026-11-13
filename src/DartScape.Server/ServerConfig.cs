using System;
using System.Collections.Generic;
using System.IO;
using DartScape.Core.Configuration;
using DartScape.Core.Diagnostics;

namespace DartScape.Server
{
    /// <summary>
    /// Typed view over <c>config.ini</c>.
    /// </summary>
    /// <remarks>
    /// Every value has a compiled-in default, so a missing or half-edited file can
    /// never stop the server from starting — it just runs with defaults and logs what
    /// it did. That is the same policy the rest of the project follows: configuration
    /// is a convenience, not a dependency.
    /// </remarks>
    public sealed class ServerConfig
    {
        // -- [server] ---------------------------------------------------------

        /// <summary>TCP port the game protocol listens on.</summary>
        public int Port = 43594;

        /// <summary>Address to bind. Empty or "*" means all interfaces.</summary>
        public string Bind = "0.0.0.0";

        /// <summary>Listen backlog.</summary>
        public int Backlog = 128;

        /// <summary>Name shown in the launcher and on the status page.</summary>
        public string Name = "DartScape";

        /// <summary>Region tag shown in the world list.</summary>
        public string Region = "EU";

        /// <summary>Members-only world flag for the world list.</summary>
        public bool Members;

        /// <summary>Maximum simultaneous players.</summary>
        public int MaxPlayers = 2000;

        /// <summary>Server tick length in milliseconds. 600 ms is the 317 standard.</summary>
        public int TickMs = 600;

        /// <summary>Disconnect a connection that sends nothing for this long.</summary>
        public int IdleTimeoutMs = 30000;

        /// <summary>How long a login may sit half-finished before it is dropped.</summary>
        public int LoginTimeoutMs = 10000;

        /// <summary>Public host name clients should use (world list / relay).</summary>
        public string PublicHost = "rune.dartpower.xyz";

        /// <summary>Directory holding the cache.</summary>
        public string CacheDir = "cache";

        /// <summary>Directory holding accounts and characters.</summary>
        public string DataDir = "data";

        /// <summary>Path to the RSA key used by the login handshake.</summary>
        public string KeyFile = "keys/server.key";

        /// <summary>Revision clients must report. 317 for this protocol.</summary>
        public int Revision = 317;

        /// <summary>Allow new accounts to be created on first login.</summary>
        public bool AllowRegistration = true;

        /// <summary>Starting coordinates for a brand new character.</summary>
        public int StartX = 3222;
        public int StartY = 3218;
        public int StartPlane;

        /// <summary>Experience multiplier; 1.0 is the authentic rate.</summary>
        public double ExperienceRate = 1.0;

        /// <summary>Minimum milliseconds between two login attempts from one address.</summary>
        public int LoginThrottleMs = 1500;

        /// <summary>Maximum characters a chat message may contain.</summary>
        public int MaxChatLength = 80;

        // -- [admin] ----------------------------------------------------------

        /// <summary>Admin API token. Generated on first start when empty.</summary>
        public string AdminToken = "";

        /// <summary>Only accept admin requests from private IPv4 ranges.</summary>
        public bool AdminPrivateOnly = true;

        /// <summary>Port the admin panel is served on.</summary>
        public int AdminPort = 43596;

        // -- [web] ------------------------------------------------------------

        /// <summary>Port the public status site is served on.</summary>
        public int WebPort = 43595;

        /// <summary>Trust X-Forwarded-For from the proxy in front of us.</summary>
        public bool TrustProxyHeaders = true;

        /// <summary>Addresses allowed to set forwarded headers.</summary>
        public string[] TrustedProxies = new[] { "127.0.0.1", "::1", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" };

        // -- [relay] ----------------------------------------------------------

        /// <summary>Port the NAT-hairpin relay listens on.</summary>
        public int RelayPort = 43597;

        /// <summary>Enable the relay.</summary>
        public bool RelayEnabled = true;

        /// <summary>Maximum concurrent relay tunnels.</summary>
        public int RelayMaxTunnels = 64;

        /// <summary>Per-tunnel byte rate limit, bytes per second. Zero disables shaping.</summary>
        public int RelayRateLimit = 0;

        // -- [logging] --------------------------------------------------------

        /// <summary>Minimum level written to disk.</summary>
        public LogLevel LogLevel = LogLevel.Information;

        /// <summary>Directory for log files.</summary>
        public string LogDir = "logs";

        /// <summary>Mirror log records to the console.</summary>
        public bool LogToConsole = true;

        /// <summary>Path of the loaded config file, for the admin panel.</summary>
        public string ConfigPath { get; private set; }

        private IniConfig _ini;

        /// <summary>
        /// Loads <c>config.ini</c> from <paramref name="baseDir"/>, creating it with
        /// defaults when absent, and applies every recognised key.
        /// </summary>
        public static ServerConfig Load(string baseDir)
        {
            var config = new ServerConfig();
            string path = Path.Combine(baseDir, "config.ini");
            config.ConfigPath = path;

            config._ini = IniConfig.Load(path, Defaults());
            config.Apply();

            if (string.IsNullOrEmpty(config.AdminToken))
            {
                config.AdminToken = GenerateToken();
                config._ini.Set("admin.token", config.AdminToken);
                config._ini.Save();
            }

            return config;
        }

        private static Dictionary<string, string> Defaults()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "server.port", "43594" },
                { "server.bind", "0.0.0.0" },
                { "server.name", "DartScape" },
                { "server.region", "EU" },
                { "server.members", "false" },
                { "server.maxPlayers", "2000" },
                { "server.tickMs", "600" },
                { "server.publicHost", "rune.dartpower.xyz" },
                { "server.cacheDir", "cache" },
                { "server.dataDir", "data" },
                { "server.keyFile", "keys/server.key" },
                { "server.revision", "317" },
                { "server.allowRegistration", "true" },
                { "server.startX", "3222" },
                { "server.startY", "3218" },
                { "server.startPlane", "0" },
                { "server.experienceRate", "1.0" },
                { "admin.port", "43596" },
                { "admin.privateOnly", "true" },
                { "web.port", "43595" },
                { "web.trustProxyHeaders", "true" },
                { "relay.port", "43597" },
                { "relay.enabled", "true" },
                { "relay.maxTunnels", "64" },
                { "logging.level", "Information" },
                { "logging.dir", "logs" },
                { "logging.console", "true" }
            };
        }

        private void Apply()
        {
            Port = _ini.GetInt("server.port", Port);
            Bind = _ini.GetString("server.bind", Bind);
            Backlog = _ini.GetInt("server.backlog", Backlog);
            Name = _ini.GetString("server.name", Name);
            Region = _ini.GetString("server.region", Region);
            Members = _ini.GetBool("server.members", Members);
            MaxPlayers = _ini.GetInt("server.maxPlayers", MaxPlayers);
            TickMs = Math.Max(100, _ini.GetInt("server.tickMs", TickMs));
            IdleTimeoutMs = _ini.GetInt("server.idleTimeoutMs", IdleTimeoutMs);
            LoginTimeoutMs = _ini.GetInt("server.loginTimeoutMs", LoginTimeoutMs);
            PublicHost = _ini.GetString("server.publicHost", PublicHost);
            CacheDir = _ini.GetString("server.cacheDir", CacheDir);
            DataDir = _ini.GetString("server.dataDir", DataDir);
            KeyFile = _ini.GetString("server.keyFile", KeyFile);
            Revision = _ini.GetInt("server.revision", Revision);
            AllowRegistration = _ini.GetBool("server.allowRegistration", AllowRegistration);
            StartX = _ini.GetInt("server.startX", StartX);
            StartY = _ini.GetInt("server.startY", StartY);
            StartPlane = _ini.GetInt("server.startPlane", StartPlane);
            ExperienceRate = _ini.GetDouble("server.experienceRate", ExperienceRate);
            LoginThrottleMs = _ini.GetInt("server.loginThrottleMs", LoginThrottleMs);
            MaxChatLength = _ini.GetInt("server.maxChatLength", MaxChatLength);

            AdminToken = _ini.GetString("admin.token", AdminToken);
            AdminPrivateOnly = _ini.GetBool("admin.privateOnly", AdminPrivateOnly);
            AdminPort = _ini.GetInt("admin.port", AdminPort);

            WebPort = _ini.GetInt("web.port", WebPort);
            TrustProxyHeaders = _ini.GetBool("web.trustProxyHeaders", TrustProxyHeaders);
            TrustedProxies = _ini.GetList("web.trustedProxies", TrustedProxies);

            RelayPort = _ini.GetInt("relay.port", RelayPort);
            RelayEnabled = _ini.GetBool("relay.enabled", RelayEnabled);
            RelayMaxTunnels = _ini.GetInt("relay.maxTunnels", RelayMaxTunnels);
            RelayRateLimit = _ini.GetInt("relay.rateLimit", RelayRateLimit);

            LogLevel = ParseLevel(_ini.GetString("logging.level", "Information"));
            LogDir = _ini.GetString("logging.dir", LogDir);
            LogToConsole = _ini.GetBool("logging.console", LogToConsole);

            // Sanity clamps: a config file must not be able to produce a server that
            // cannot bind or that ticks at zero hertz.
            if (Port < 1 || Port > 65535) Port = 43594;
            if (WebPort < 1 || WebPort > 65535) WebPort = 43595;
            if (AdminPort < 1 || AdminPort > 65535) AdminPort = 43596;
            if (RelayPort < 1 || RelayPort > 65535) RelayPort = 43597;
            if (MaxPlayers < 1) MaxPlayers = 1;
            if (ExperienceRate <= 0) ExperienceRate = 1.0;
            if (MaxChatLength < 10) MaxChatLength = 80;
        }

        private static LogLevel ParseLevel(string text)
        {
            switch ((text ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "debug": return LogLevel.Debug;
                case "warn":
                case "warning": return LogLevel.Warning;
                case "error": return LogLevel.Error;
                case "critical": return LogLevel.Critical;
                default: return LogLevel.Information;
            }
        }

        /// <summary>Reads an arbitrary key for the admin config view.</summary>
        public string Raw(string key, string fallback)
        {
            return _ini == null ? fallback : _ini.GetString(key, fallback);
        }

        /// <summary>All keys known to the config, in file order.</summary>
        public IEnumerable<string> Keys
        {
            get
            {
                return new[]
                {
                    "server.port", "server.bind", "server.name", "server.region", "server.members",
                    "server.maxPlayers", "server.tickMs", "server.publicHost", "server.cacheDir",
                    "server.dataDir", "server.keyFile", "server.revision", "server.allowRegistration",
                    "server.startX", "server.startY", "server.startPlane", "server.experienceRate",
                    "admin.port", "admin.privateOnly", "web.port", "web.trustProxyHeaders",
                    "relay.port", "relay.enabled", "relay.maxTunnels", "logging.level",
                    "logging.dir", "logging.console"
                };
            }
        }

        private static string GenerateToken()
        {
            var bytes = new byte[24];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>Absolute path helper relative to the server's base directory.</summary>
        public string Resolve(string relativeOrAbsolute)
        {
            if (string.IsNullOrEmpty(relativeOrAbsolute)) return AppDomain.CurrentDomain.BaseDirectory;
            return Path.IsPathRooted(relativeOrAbsolute)
                ? relativeOrAbsolute
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativeOrAbsolute);
        }
    }
}
