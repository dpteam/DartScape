using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DartScape.Core;
using DartScape.Core.Diagnostics;
using DartScape.Core.Security;
using DartScape.Game;
using DartScape.Game.Data;
using DartScape.Web.Http;

namespace DartScape.Web
{
    /// <summary>
    /// Hosts both web surfaces: the public status site and the LAN-only admin panel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// They run in the same process as the game server, on two ports. That is a deliberate
    /// choice over a separate service: the admin panel needs live world state (players,
    /// tick timings, log tail) and a second process would need an IPC protocol to expose
    /// it, which is more moving parts for no benefit on a single-machine deployment.
    /// </para>
    /// <para>
    /// The separation that matters is not process separation, it is <em>reachability</em>:
    /// the admin port is bound to the LAN and never forwarded, and every admin route is
    /// absent from the public port вЂ” not merely unauthorised, but answering exactly like a
    /// typo, so its shape cannot be probed.
    /// </para>
    /// </remarks>
    public sealed class WebHost : IDisposable
    {
        private readonly World _world;
        private readonly ServerConfig _config;
        private readonly PlayerStore _store;

        private HttpServer _public;
        private HttpServer _admin;

        private readonly StaticFiles _publicFiles;
        private readonly StaticFiles _adminFiles;

        private readonly AdminAuth _auth;

        public WebHost(World world, ServerConfig config, PlayerStore store)
        {
            if (world == null) throw new ArgumentNullException("world");
            if (config == null) throw new ArgumentNullException("config");

            _world = world;
            _config = config;
            _store = store;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            _publicFiles = new StaticFiles(Path.Combine(baseDir, "public"));
            _adminFiles = new StaticFiles(Path.Combine(baseDir, "admin"));

            _auth = new AdminAuth(config.AdminToken, config.AdminPrivateOnly);
        }

        /// <summary>Starts both listeners.</summary>
        public void Start()
        {
            _public = new HttpServer("public", System.Net.IPAddress.Any, _config.WebPort, HandlePublic);
            _admin = new HttpServer("admin", System.Net.IPAddress.Any, _config.AdminPort, HandleAdmin);

            try
            {
                _public.Start();
            }
            catch (Exception ex)
            {
                Log.Error("Web", "Public site could not bind port {0}: {1}", _config.WebPort, ex.Message);
                _public = null;
            }

            try
            {
                _admin.Start();
                Log.Info("Web", "Admin panel on port {0} (LAN only).", _admin.Port);
            }
            catch (Exception ex)
            {
                Log.Error("Web", "Admin panel could not bind port {0}: {1}", _config.AdminPort, ex.Message);
                _admin = null;
            }
        }

        // -- public -----------------------------------------------------------

        private bool HandlePublic(HttpRequest request, HttpResponse response)
        {
            ApplySecurityHeaders(response, false);

            string path = request.Path ?? "/";

            // Admin routes do not exist here. Answering 404 (rather than 403) means an
            // attacker learns nothing about whether the admin surface is enabled.
            if (path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase))
            {
                Log.Info("Web", "Admin API probed on the public port from {0}.", ClientIp(request));
                response.Error(404, "unknown-endpoint", "No such endpoint.");
                return true;
            }

            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                return PublicApi.Handle(_world, _config, _store, request, response, ClientIp(request));
            }

            if (path == "/" || path.Length == 0)
            {
                return _publicFiles.TryServe(request, response, "/index.html");
            }

            if (_publicFiles.TryServe(request, response, path)) return true;

            // A path without an extension is treated as a client-side route.
            if (Path.GetExtension(path) == string.Empty)
            {
                if (_publicFiles.TryServe(request, response, "/index.html")) return true;
            }

            if (_publicFiles.TryServeNotFound(response)) return true;

            response.Error(404, "not-found", "No such file.");
            return true;
        }

        // -- admin ------------------------------------------------------------

        private bool HandleAdmin(HttpRequest request, HttpResponse response)
        {
            ApplySecurityHeaders(response, true);

            string path = request.Path ?? "/";
            string clientIp = ClientIp(request);

            // Refuse non-private addresses before touching authentication at all.
            if (!_auth.IsAddressAllowed(clientIp))
            {
                Log.Warning("Web", "Admin request from non-private address {0} refused.", clientIp);
                response.Error(403, "forbidden", "The admin panel is available from the local network only.");
                return true;
            }

            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                if (!_auth.Authenticate(request, clientIp, response)) return true;

                return AdminApi.Handle(_world, _config, _store, request, response, clientIp);
            }

            if (path == "/" || path.Length == 0)
            {
                return _adminFiles.TryServe(request, response, "/index.html");
            }

            if (_adminFiles.TryServe(request, response, path)) return true;
            if (_adminFiles.TryServeNotFound(response)) return true;

            response.Error(404, "not-found", "No such file.");
            return true;
        }

        // -- helpers ----------------------------------------------------------

        /// <summary>
        /// Resolves the client address, honouring proxy headers only from trusted sources.
        /// </summary>
        /// <remarks>
        /// Nginx Proxy Manager sits in front of the public site, so the socket address is
        /// always the proxy. The header chain is walked right to left and the first address
        /// that is not itself a trusted proxy wins вЂ” the standard, correct algorithm. A
        /// stranger's <c>X-Forwarded-For</c> is ignored entirely.
        /// </remarks>
        private string ClientIp(HttpRequest request)
        {
            string peer = request.RemoteAddress ?? "0.0.0.0";

            if (!_config.TrustProxyHeaders) return peer;
            if (!IsTrustedProxy(peer)) return peer;

            string chain = request.Header("X-Forwarded-For") ?? request.Header("X-Real-IP");
            if (string.IsNullOrEmpty(chain)) return peer;

            string[] parts = chain.Split(',');
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                string candidate = StripPort(parts[i].Trim());
                if (candidate.Length == 0) continue;
                if (!IsTrustedProxy(candidate)) return candidate;
            }

            return peer;
        }

        private static string StripPort(string address)
        {
            if (string.IsNullOrEmpty(address)) return string.Empty;

            // IPv6 literals arrive as [::1]:1234.
            if (address[0] == '[')
            {
                int close = address.IndexOf(']');
                return close > 0 ? address.Substring(1, close - 1) : address;
            }

            int colon = address.LastIndexOf(':');
            if (colon > 0 && address.IndexOf(':') == colon) return address.Substring(0, colon);
            return address;
        }

        private bool IsTrustedProxy(string address)
        {
            foreach (string entry in _config.TrustedProxies)
            {
                if (string.Equals(entry, address, StringComparison.OrdinalIgnoreCase)) return true;

                if (entry.IndexOf('/') > 0 && Cidr.Contains(entry, address)) return true;
            }
            return false;
        }

        /// <summary>Stops both listeners.</summary>
        public void Dispose()
        {
            try { if (_public != null) _public.Dispose(); } catch (Exception) { }
            try { if (_admin != null) _admin.Dispose(); } catch (Exception) { }

            _public = null;
            _admin = null;
        }

        private static void ApplySecurityHeaders(HttpResponse response, bool isAdmin)        {
            response.SetHeader("X-Content-Type-Options", "nosniff");
            response.SetHeader("X-Frame-Options", "SAMEORIGIN");
            response.SetHeader("Referrer-Policy", "no-referrer");

            if (isAdmin)
            {
                response.SetHeader("X-Robots-Tag", "noindex, nofollow");
                response.SetHeader("Cache-Control", "no-store");
            }

            // Font Awesome comes from cdnjs; nothing else is allowed to load remotely.
            response.SetHeader("Content-Security-Policy",
                "default-src 'self'; " +
                "style-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com; " +
                "font-src 'self' https://cdnjs.cloudflare.com data:; " +
                "img-src 'self' data:; " +
                "script-src 'self'; " +
                "connect-src 'self'; " +
                "form-action 'self'; " +
                "base-uri 'self'");
        }
    }

    /// <summary>IPv4/IPv6 CIDR membership test.</summary>
    public static class Cidr
    {
        /// <summary>True when <paramref name="address"/> falls inside <paramref name="range"/>.</summary>
        public static bool Contains(string range, string address)
        {
            if (string.IsNullOrEmpty(range) || string.IsNullOrEmpty(address)) return false;

            int slash = range.IndexOf('/');
            if (slash <= 0) return string.Equals(range, address, StringComparison.OrdinalIgnoreCase);

            string networkText = range.Substring(0, slash);
            int bits;

            if (!int.TryParse(range.Substring(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out bits)) return false;

            System.Net.IPAddress networkAddress;
            System.Net.IPAddress candidateAddress;

            if (!System.Net.IPAddress.TryParse(networkText, out networkAddress)) return false;
            if (!System.Net.IPAddress.TryParse(address, out candidateAddress)) return false;

            byte[] networkBytes = networkAddress.GetAddressBytes();
            byte[] candidateBytes = candidateAddress.GetAddressBytes();

            if (networkBytes.Length != candidateBytes.Length) return false;

            int totalBits = networkBytes.Length * 8;
            if (bits < 0) bits = 0;
            if (bits > totalBits) bits = totalBits;

            int fullBytes = bits / 8;
            int remainingBits = bits % 8;

            for (int i = 0; i < fullBytes; i++)
            {
                if (networkBytes[i] != candidateBytes[i]) return false;
            }

            if (remainingBits == 0) return true;

            int mask = 0xFF << (8 - remainingBits);
            return (networkBytes[fullBytes] & mask) == (candidateBytes[fullBytes] & mask);
        }

        /// <summary>True for RFC1918 / loopback / link-local addresses.</summary>
        public static bool IsPrivate(string address)
        {
            return Contains("127.0.0.0/8", address) ||
                   Contains("10.0.0.0/8", address) ||
                   Contains("172.16.0.0/12", address) ||
                   Contains("192.168.0.0/16", address) ||
                   Contains("169.254.0.0/16", address) ||
                   Contains("100.64.0.0/10", address) ||
                   address == "::1" ||
                   address == "0.0.0.0" ||
                   address == "::ffff:127.0.0.1";
        }
    }

    /// <summary>
    /// Bearer-token authentication for the admin API, with a per-address lockout.
    /// </summary>
    /// <remarks>
    /// The token is compared in constant time, and repeated failures from one address
    /// produce a temporary block. That is the minimum needed to make an online guessing
    /// attack pointless without adding a login flow the operator has to manage.
    /// </remarks>
    public sealed class AdminAuth
    {
        private const int MaxFailures = 10;
        private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(5);

        private readonly string _token;
        private readonly bool _privateOnly;

        private readonly Dictionary<string, FailureRecord> _failures =
            new Dictionary<string, FailureRecord>(StringComparer.OrdinalIgnoreCase);

        private readonly object _sync = new object();

        private sealed class FailureRecord
        {
            public int Count;
            public DateTime FirstUtc;
            public DateTime BlockedUntilUtc;
        }

        public AdminAuth(string token, bool privateOnly)
        {
            _token = token ?? string.Empty;
            _privateOnly = privateOnly;
        }

        /// <summary>True when <paramref name="address"/> may talk to the admin surface at all.</summary>
        public bool IsAddressAllowed(string address)
        {
            if (!_privateOnly) return true;
            return Cidr.IsPrivate(address ?? string.Empty);
        }

        /// <summary>
        /// Checks the bearer token. Writes an error response and returns <c>false</c> when
        /// the request must not proceed.
        /// </summary>
        public bool Authenticate(HttpRequest request, string clientIp, HttpResponse response)
        {
            if (string.IsNullOrEmpty(_token))
            {
                response.Error(503, "not-configured", "No admin token is configured.");
                return false;
            }

            if (IsBlocked(clientIp))
            {
                response.Error(429, "rate-limited", "Too many failed attempts. Try again in a few minutes.");
                return false;
            }

            string header = request.Header("Authorization");
            if (header == null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                RegisterFailure(clientIp);
                response.Error(401, "unauthorized", "An admin token is required.");
                return false;
            }

            string presented = header.Substring(7).Trim();

            if (!PasswordHasher.FixedEquals(presented, _token))
            {
                RegisterFailure(clientIp);
                Log.Warning("Web", "Bad admin token from {0}.", clientIp);
                response.Error(401, "unauthorized", "The admin token is not valid.");
                return false;
            }

            ClearFailures(clientIp);
            return true;
        }

        private bool IsBlocked(string address)
        {
            lock (_sync)
            {
                FailureRecord record;
                if (!_failures.TryGetValue(address, out record)) return false;

                if (record.BlockedUntilUtc > DateTime.UtcNow) return true;

                if (record.BlockedUntilUtc != DateTime.MinValue)
                {
                    _failures.Remove(address);
                }

                return false;
            }
        }

        private void RegisterFailure(string address)
        {
            lock (_sync)
            {
                FailureRecord record;
                if (!_failures.TryGetValue(address, out record))
                {
                    record = new FailureRecord { FirstUtc = DateTime.UtcNow };
                    _failures[address] = record;
                }

                if (DateTime.UtcNow - record.FirstUtc > FailureWindow)
                {
                    record.Count = 0;
                    record.FirstUtc = DateTime.UtcNow;
                }

                record.Count++;

                if (record.Count >= MaxFailures)
                {
                    record.BlockedUntilUtc = DateTime.UtcNow + BlockDuration;
                    Log.Warning("Web", "Admin access from {0} blocked for {1} minutes.", address, BlockDuration.TotalMinutes);
                }
            }
        }

        private void ClearFailures(string address)
        {
            lock (_sync)
            {
                _failures.Remove(address);
            }
        }
    }
}
