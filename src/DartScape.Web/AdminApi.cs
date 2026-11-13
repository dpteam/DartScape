using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using DartScape.Core;
using DartScape.Core.Diagnostics;
using DartScape.Game;
using DartScape.Game.Commands;
using DartScape.Game.Data;
using DartScape.Web.Http;

namespace DartScape.Web
{
    /// <summary>
    /// The admin API. Only reachable on the admin port, from the local network, with a
    /// valid bearer token.
    /// </summary>
    /// <remarks>
    /// Every mutation goes through the world's input queue rather than touching state
    /// directly, because these handlers run on a thread-pool thread while the tick loop is
    /// running. Queuing is the difference between "an admin kicked someone" and "the world
    /// tick read a half-updated player".
    /// </remarks>
    public static class AdminApi
    {
        /// <summary>Handles an admin API request. Returns <c>false</c> when the path is unknown.</summary>
        public static bool Handle(World world, ServerConfig config, PlayerStore store,
            HttpRequest request, HttpResponse response, string clientIp)
        {
            string path = request.Path.ToLowerInvariant();
            string method = request.Method;

            switch (path)
            {
                case "/api/admin/overview":
                    if (!RequireMethod(request, response, "GET")) return true;
                    Overview(world, config, response);
                    return true;

                case "/api/admin/players":
                    if (!RequireMethod(request, response, "GET")) return true;
                    Players(world, response);
                    return true;

                case "/api/admin/kick":
                    if (!RequireMethod(request, response, "POST")) return true;
                    Kick(world, request, response, clientIp);
                    return true;

                case "/api/admin/broadcast":
                    if (!RequireMethod(request, response, "POST")) return true;
                    Broadcast(world, request, response, clientIp);
                    return true;

                case "/api/admin/command":
                    if (!RequireMethod(request, response, "POST")) return true;
                    Command(world, request, response, clientIp);
                    return true;

                case "/api/admin/logs":
                    if (!RequireMethod(request, response, "GET")) return true;
                    Logs(request, response);
                    return true;

                case "/api/admin/config":
                    if (!RequireMethod(request, response, "GET")) return true;
                    ConfigView(config, response);
                    return true;

                default:
                    return false;
            }
        }

        private static bool RequireMethod(HttpRequest request, HttpResponse response, string method)
        {
            if (string.Equals(request.Method, method, StringComparison.OrdinalIgnoreCase)) return true;

            response.Error(405, "bad-request", "This endpoint requires " + method + ".");
            return false;
        }

        // -- read -------------------------------------------------------------

        private static void Overview(World world, ServerConfig config, HttpResponse response)
        {
            Process process = Process.GetCurrentProcess();

            var json = new JsonBuilder(1024);

            json.BeginObject().Prop("ok", true);

            json.Name("server").BeginObject()
                .Prop("online", true)
                .Prop("uptimeSec", (long)world.Uptime.TotalSeconds)
                .Prop("tickRate", Math.Round(world.TicksPerSecond, 3))
                .Prop("averageTickMs", Math.Round(world.AverageTickMs, 2))
                .Prop("memoryMb", Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 1))
                .Prop("threads", process.Threads.Count)
                .Prop("gcCollections", GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2))
                .EndObject();

            json.Name("world").BeginObject()
                .Prop("playersOnline", world.PlayerCount)
                .Prop("playersMax", config.MaxPlayers)
                .Prop("npcs", world.NpcCount)
                .Prop("groundItems", world.GroundItemCount)
                .Prop("accountsTotal", GameServices.Store == null ? 0 : GameServices.Store.AccountCount)
                .EndObject();

            json.Name("process").BeginObject()
                .Prop("pid", process.Id)
                .Prop("startedAt", TimeUtil.ToUnix(world.StartedUtc))
                .Prop("restarts", 0)
                .Prop("lastError", Log.ErrorCount > 0 ? Log.ErrorCount + " error(s) logged" : (string)null)
                .Prop("logFile", Log.CurrentLogFile == null ? "" : Path.GetFileName(Log.CurrentLogFile))
                .EndObject();

            json.EndObject();
            response.Json(json.ToString());
        }

        private static void Players(World world, HttpResponse response)
        {
            var json = new JsonBuilder(4096);
            json.BeginObject().Prop("ok", true).Name("players").BeginArray();

            Player[] players = world.Players;
            long now = TimeUtil.UnixNow();

            for (int i = 0; i < players.Length; i++)
            {
                Player player = players[i];
                if (player == null || !player.Online) continue;

                json.BeginObject()
                    .Prop("name", player.Name)
                    .Prop("index", player.Index)
                    .Prop("combat", player.CombatLevel)
                    .Prop("totalLevel", player.TotalLevel)
                    .Prop("x", player.Position.X)
                    .Prop("y", player.Position.Y)
                    .Prop("plane", player.Position.Plane)
                    .Prop("sessionSec", now - TimeUtil.ToUnix(player.LoginTimeUtc))
                    .Prop("address", "hidden")
                    .Prop("privilege", player.Privilege)
                    .Prop("idleSec", (long)(DateTime.UtcNow - player.LastActivityUtc).TotalSeconds)
                    .EndObject();
            }

            json.EndArray().EndObject();
            response.Json(json.ToString());
        }

        private static void Logs(HttpRequest request, HttpResponse response)
        {
            long since = request.QueryInt("since", 0);
            int limit = request.QueryInt("limit", 200);

            if (limit < 1) limit = 1;
            if (limit > 500) limit = 500;

            LogRecord[] recent = Log.GetRecent(limit);
            long baseSeq = Log.ErrorCount;

            var json = new JsonBuilder(4096);
            json.BeginObject().Prop("ok", true);

            json.Name("lines").BeginArray();

            for (int i = 0; i < recent.Length; i++)
            {
                LogRecord record = recent[i];
                long seq = baseSeq - recent.Length + i + 1;
                if (seq <= since) continue;

                json.BeginObject()
                    .Prop("seq", seq)
                    .Prop("at", TimeUtil.ToUnix(record.Timestamp))
                    .Prop("level", LevelName(record.Level))
                    .Prop("category", record.Category)
                    .Prop("text", record.Message)
                    .EndObject();
            }

            json.EndArray();

            json.Prop("nextSince", baseSeq);
            json.EndObject();

            response.Json(json.ToString());
        }

        private static string LevelName(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return "debug";
                case LogLevel.Warning: return "warn";
                case LogLevel.Error: return "error";
                case LogLevel.Critical: return "critical";
                default: return "info";
            }
        }

        private static void ConfigView(ServerConfig config, HttpResponse response)
        {
            var json = new JsonBuilder(2048);
            json.BeginObject().Prop("ok", true).Name("sections").BeginArray();

            // Group the flat keys by their section prefix.
            var sections = new List<string>();
            var grouped = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);

            foreach (string key in config.Keys)
            {
                int dot = key.IndexOf('.');
                string section = dot > 0 ? key.Substring(0, dot) : "general";
                string leaf = dot > 0 ? key.Substring(dot + 1) : key;

                if (!grouped.ContainsKey(section))
                {
                    grouped[section] = new List<KeyValuePair<string, string>>();
                    sections.Add(section);
                }

                grouped[section].Add(new KeyValuePair<string, string>(leaf, Redact(key, config.Raw(key, ""))));
            }

            foreach (string section in sections)
            {
                json.BeginObject().Prop("name", section).Name("entries").BeginArray();

                foreach (KeyValuePair<string, string> entry in grouped[section])
                {
                    json.BeginObject()
                        .Prop("key", entry.Key)
                        .Prop("value", entry.Value)
                        .EndObject();
                }

                json.EndArray().EndObject();
            }

            json.EndArray().EndObject();
            response.Json(json.ToString());
        }

        /// <summary>Hides secret values while still showing that they are set.</summary>
        private static string Redact(string key, string value)
        {
            if (key.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 ||
                key.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
                key.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return string.IsNullOrEmpty(value) ? "(unset)" : "(set)";
            }
            return value;
        }

        // -- mutations --------------------------------------------------------

        private static void Kick(World world, HttpRequest request, HttpResponse response, string clientIp)
        {
            string name = JsonReader.GetString(request.Body, "name");
            string reason = JsonReader.GetString(request.Body, "reason", "Kicked by an administrator");

            if (string.IsNullOrEmpty(name))
            {
                response.Error(400, "bad-request", "A player name is required.");
                return;
            }

            Player target = world.FindPlayer(name);
            if (target == null)
            {
                response.Error(404, "not-found", "No player called " + name + " is online.");
                return;
            }

            Log.Info("Web", "Admin {0} kicked {1}: {2}", clientIp, target.Name, reason);

            world.Enqueue(() =>
            {
                world.SendMessage(target, reason);
                if (target.Endpoint != null) target.Endpoint.Disconnect(reason);
            });

            response.Json("{\"ok\":true}");
        }

        private static void Broadcast(World world, HttpRequest request, HttpResponse response, string clientIp)
        {
            string text = JsonReader.GetString(request.Body, "text");

            if (string.IsNullOrEmpty(text))
            {
                response.Error(400, "bad-request", "A message is required.");
                return;
            }

            if (text.Length > 240) text = text.Substring(0, 240);

            Log.Info("Web", "Admin {0} broadcast: {1}", clientIp, text);

            int delivered = 0;
            world.Enqueue(() =>
            {
                delivered = world.BroadcastMessage(text);
                world.AddEvent("broadcast", text);
            });

            response.Json("{\"ok\":true,\"delivered\":0}");
        }

        private static void Command(World world, HttpRequest request, HttpResponse response, string clientIp)
        {
            string text = JsonReader.GetString(request.Body, "text");

            if (string.IsNullOrEmpty(text))
            {
                response.Error(400, "bad-request", "A command is required.");
                return;
            }

            Log.Info("Web", "Admin {0} ran: {1}", clientIp, text);

            CommandDispatcher.Result result = null;
            var completed = new System.Threading.ManualResetEvent(false);

            world.Enqueue(() =>
            {
                try
                {
                    result = CommandDispatcher.Execute(world, null, text);
                }
                finally
                {
                    completed.Set();
                }
            });

            // Wait for the tick to run it, but never block a request forever.
            if (!completed.WaitOne(TimeSpan.FromSeconds(3)))
            {
                response.Error(503, "rate-limited", "The world did not process the command in time.");
                return;
            }

            var json = new JsonBuilder(512);
            json.BeginObject()
                .Prop("ok", result != null && result.Success)
                .Name("output").BeginArray();

            if (result != null)
            {
                foreach (string line in result.Lines) json.Value(line);
            }

            json.EndArray().EndObject();
            response.Json(json.ToString());
        }
    }
}
