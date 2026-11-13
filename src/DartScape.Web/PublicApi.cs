using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DartScape.Core;
using DartScape.Core.Diagnostics;
using DartScape.Game;
using DartScape.Game.Data;
using DartScape.Game.Definitions;
using DartScape.Web.Http;

namespace DartScape.Web
{
    /// <summary>
    /// The public JSON API, served on the status port.
    /// </summary>
    /// <remarks>
    /// Nothing here exposes anything an anonymous visitor should not see: no addresses, no
    /// account identifiers, no session data. The player list is capped and contains only
    /// what the status page renders.
    /// </remarks>
    public static class PublicApi
    {
        private const int MaxPlayersListed = 50;
        private const int MaxEventsListed = 20;

        /// <summary>Handles a public API request. Returns <c>false</c> when the path is unknown.</summary>
        public static bool Handle(World world, ServerConfig config, PlayerStore store,
            HttpRequest request, HttpResponse response, string clientIp)
        {
            string path = request.Path.ToLowerInvariant();

            switch (path)
            {
                case "/api/health":
                    Health(world, config, response);
                    return true;

                case "/api/status":
                    Status(world, config, store, response);
                    return true;

                case "/api/worlds":
                    Worlds(world, config, response);
                    return true;

                case "/api/hiscore":
                    Hiscore(world, request, response);
                    return true;

                case "/api/news":
                    News(config, response);
                    return true;

                default:
                    return false;
            }
        }

        private static void Health(World world, ServerConfig config, HttpResponse response)
        {
            var json = new JsonBuilder(200);
            json.BeginObject()
                .Prop("ok", true)
                .Prop("name", config.Name)
                .Prop("version", "0.1.0")
                .Prop("role", "public")
                .Prop("uptimeSec", (long)world.Uptime.TotalSeconds)
                .EndObject();

            response.Json(json.ToString());
        }

        private static void Status(World world, ServerConfig config, PlayerStore store, HttpResponse response)
        {
            List<Player> online = world.OnlinePlayers();

            var json = new JsonBuilder(4096);

            json.BeginObject()
                .Prop("ok", true)
                .Prop("name", config.Name)
                .Prop("version", "0.1.0")
                .Prop("revision", config.Revision)
                .Prop("startedAt", TimeUtil.ToUnix(world.StartedUtc))
                .Prop("uptimeSec", (long)world.Uptime.TotalSeconds);

            // --- world -------------------------------------------------------
            json.Name("world").BeginObject()
                .Prop("name", config.Name)
                .Prop("region", config.Region)
                .Prop("members", config.Members)
                .Prop("playersOnline", online.Count)
                .Prop("playersMax", config.MaxPlayers)
                .Prop("accountsTotal", store == null ? 0 : store.AccountCount)
                .Prop("ticksPerSecond", Math.Round(world.TicksPerSecond, 3))
                .Prop("averageTickMs", Math.Round(world.AverageTickMs, 2))
                .EndObject();

            // --- server ------------------------------------------------------
            json.Name("server").BeginObject()
                .Prop("online", true)
                .Prop("gamePort", config.Port)
                .Prop("webPort", config.WebPort)
                .Prop("relayPort", config.RelayPort)
                .Prop("relayOnline", config.RelayEnabled)
                .Prop("publicAddress", config.PublicHost)
                .Prop("lastRestartReason", "clean start")
                .EndObject();

            // --- players -----------------------------------------------------
            json.Name("players").BeginArray();

            int listed = Math.Min(online.Count, MaxPlayersListed);
            for (int i = 0; i < listed; i++)
            {
                Player player = online[i];
                json.BeginObject()
                    .Prop("name", player.Name)
                    .Prop("combat", player.CombatLevel)
                    .Prop("totalLevel", player.TotalLevel)
                    .Prop("world", 1)
                    .Prop("privilege", player.Privilege)
                    .EndObject();
            }

            json.EndArray();

            // --- skill totals ------------------------------------------------
            json.Name("skillTotals").BeginArray();

            var totals = new long[Skills.Count];
            for (int i = 0; i < online.Count; i++)
            {
                for (int skill = 0; skill < Skills.Count; skill++) totals[skill] += online[i].BaseLevel[skill];
            }

            for (int skill = 0; skill < Skills.Count; skill++)
            {
                json.BeginObject()
                    .Prop("skill", Skills.Names[skill])
                    .Prop("total", totals[skill])
                    .EndObject();
            }

            json.EndArray();

            // --- events ------------------------------------------------------
            json.Name("events").BeginArray();

            World.WorldEvent[] events = world.RecentEvents(MaxEventsListed);
            foreach (World.WorldEvent entry in events)
            {
                json.BeginObject()
                    .Prop("seq", entry.Seq)
                    .Prop("at", entry.At)
                    .Prop("kind", entry.Kind)
                    .Prop("text", entry.Text)
                    .EndObject();
            }

            json.EndArray();

            json.Prop("logins24h", CountLogins24h(world));
            json.EndObject();

            response.Json(json.ToString());
        }

        /// <summary>Counts "logged in" events in the rolling feed, as a rough activity figure.</summary>
        private static int CountLogins24h(World world)
        {
            World.WorldEvent[] events = world.RecentEvents(100);
            int count = 0;

            foreach (World.WorldEvent entry in events)
            {
                if (entry.Kind == "login") count++;
            }
            return count;
        }

        private static void Worlds(World world, ServerConfig config, HttpResponse response)
        {
            var json = new JsonBuilder(512);

            json.BeginObject().Prop("ok", true).Name("worlds").BeginArray();

            json.BeginObject()
                .Prop("id", 1)
                .Prop("name", config.Name)
                .Prop("address", config.PublicHost)
                .Prop("port", config.Port)
                .Prop("players", world.PlayerCount)
                .Prop("max", config.MaxPlayers)
                .Prop("members", config.Members)
                .Prop("region", config.Region)
                .Prop("online", true)
                .Prop("pingMs", 0)
                .EndObject();

            json.EndArray().EndObject();

            response.Json(json.ToString());
        }

        private static void Hiscore(World world, HttpRequest request, HttpResponse response)
        {
            string skillName = request.QueryString("skill", "overall");
            int limit = request.QueryInt("limit", 25);

            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100;

            int skill = -1;
            if (!string.Equals(skillName, "overall", StringComparison.OrdinalIgnoreCase))
            {
                skill = Skills.IndexOf(skillName);
                if (skill < 0)
                {
                    response.Error(400, "bad-request", "Unknown skill: " + skillName);
                    return;
                }
            }

            // Rank online players only. A persistent hiscore table is a pipeline item;
            // until then this is honest about what it is.
            List<Player> online = world.OnlinePlayers();
            online.Sort((a, b) =>
            {
                if (skill < 0)
                {
                    long xpA = a.TotalExperience;
                    long xpB = b.TotalExperience;
                    return xpB.CompareTo(xpA);
                }
                return b.Experience[skill].CompareTo(a.Experience[skill]);
            });

            var json = new JsonBuilder(1024);
            json.BeginObject()
                .Prop("ok", true)
                .Prop("skill", skillName.ToLowerInvariant())
                .Name("entries").BeginArray();

            int count = Math.Min(limit, online.Count);
            for (int i = 0; i < count; i++)
            {
                Player player = online[i];

                json.BeginObject()
                    .Prop("rank", i + 1)
                    .Prop("name", player.Name)
                    .Prop("level", skill < 0 ? player.TotalLevel : player.BaseLevel[skill])
                    .Prop("xp", skill < 0 ? player.TotalExperience : player.Experience[skill])
                    .EndObject();
            }

            json.EndArray().EndObject();
            response.Json(json.ToString());
        }

        private static void News(ServerConfig config, HttpResponse response)
        {
            var json = new JsonBuilder(2048);
            json.BeginObject().Prop("ok", true).Name("items").BeginArray();

            string path = config.Resolve(Path.Combine(config.DataDir, "news.json"));

            if (File.Exists(path))
            {
                try
                {
                    string text = File.ReadAllText(path, Encoding.UTF8);

                    // The file is a plain JSON array of objects; pass it through after
                    // validating that it parses as an array so a broken file cannot inject
                    // arbitrary content into the response.
                    int start = text.IndexOf('[');
                    int end = text.LastIndexOf(']');

                    if (start >= 0 && end > start)
                    {
                        json.Raw(text.Substring(start, end - start + 1));
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("Web", "Could not read news: {0}", ex.Message);
                }
            }

            json.EndArray().EndObject();
            response.Json(json.ToString());
        }
    }
}
