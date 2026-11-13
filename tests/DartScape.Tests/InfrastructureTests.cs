using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DartScape.Core.Configuration;
using DartScape.Core.Diagnostics;
using DartScape.Web;
using DartScape.Web.Http;

namespace DartScape.Tests
{
    /// <summary>Tests for configuration, JSON, HTTP parsing and address handling.</summary>
    public static class InfrastructureTests
    {
        public static void Run()
        {
            Harness.Section("Configuration");
            ConfigTests();

            Harness.Section("JSON");
            JsonTests();

            Harness.Section("HTTP path handling");
            PathTests();

            Harness.Section("CIDR");
            CidrTests();

            Harness.Section("Logging");
            LoggingTests();
        }

        private static void ConfigTests()
        {
            Harness.Test("missing keys fall back to defaults", () =>
            {
                string path = TempFile("ini");

                try
                {
                    var config = IniConfig.Load(path, new Dictionary<string, string>
                    {
                        { "server.port", "43594" },
                        { "server.name", "DartScape" }
                    });

                    Harness.Equal(43594, config.GetInt("server.port", 0), "default port");
                    Harness.Equal("DartScape", config.GetString("server.name", ""), "default name");
                    Harness.Equal(7, config.GetInt("server.missing", 7), "unknown key");
                }
                finally
                {
                    SafeDelete(path);
                }
            });

            Harness.Test("values survive a save and reload", () =>
            {
                string path = TempFile("ini");

                try
                {
                    var config = IniConfig.Load(path);
                    config.Set("server.port", "12345");
                    config.Set("server.name", "Test World");
                    config.Set("relay.enabled", "false");
                    config.Save();

                    var reloaded = IniConfig.Load(path);

                    Harness.Equal(12345, reloaded.GetInt("server.port", 0), "port");
                    Harness.Equal("Test World", reloaded.GetString("server.name", ""), "name");
                    Harness.False(reloaded.GetBool("relay.enabled", true), "boolean");
                }
                finally
                {
                    SafeDelete(path);
                }
            });

            Harness.Test("boolean spellings are all accepted", () =>
            {
                string path = TempFile("ini");

                try
                {
                    File.WriteAllText(path,
                        "a = 1\n" +
                        "b = true\n" +
                        "c = yes\n" +
                        "d = on\n" +
                        "e = 0\n" +
                        "f = false\n" +
                        "g = no\n" +
                        "h = off\n");

                    var config = IniConfig.Load(path);

                    Harness.True(config.GetBool("a", false), "1");
                    Harness.True(config.GetBool("b", false), "true");
                    Harness.True(config.GetBool("c", false), "yes");
                    Harness.True(config.GetBool("d", false), "on");
                    Harness.False(config.GetBool("e", true), "0");
                    Harness.False(config.GetBool("f", true), "false");
                    Harness.False(config.GetBool("g", true), "no");
                    Harness.False(config.GetBool("h", true), "off");
                }
                finally
                {
                    SafeDelete(path);
                }
            });

            Harness.Test("a malformed value falls back instead of throwing", () =>
            {
                string path = TempFile("ini");

                try
                {
                    File.WriteAllText(path, "server.port = not-a-number\n");

                    var config = IniConfig.Load(path);
                    Harness.Equal(43594, config.GetInt("server.port", 43594), "fallback");
                }
                finally
                {
                    SafeDelete(path);
                }
            });

            Harness.Test("comments and blank lines are ignored", () =>
            {
                string path = TempFile("ini");

                try
                {
                    File.WriteAllText(path,
                        "# a comment\n" +
                        "; another\n" +
                        "\n" +
                        "[server]\n" +
                        "port = 1234\n");

                    var config = IniConfig.Load(path);
                    Harness.Equal(1234, config.GetInt("server.port", 0), "value after a section header");
                }
                finally
                {
                    SafeDelete(path);
                }
            });

            Harness.Test("lists split on commas and trim", () =>
            {
                string path = TempFile("ini");

                try
                {
                    File.WriteAllText(path, "web.trustedProxies = 127.0.0.1, 10.0.0.0/8 ,192.168.0.0/16\n");

                    var config = IniConfig.Load(path);
                    string[] list = config.GetList("web.trustedProxies");

                    Harness.Equal(3, list.Length, "three entries");
                    Harness.Equal("10.0.0.0/8", list[1], "trimmed");
                }
                finally
                {
                    SafeDelete(path);
                }
            });
        }

        private static void JsonTests()
        {
            Harness.Test("the builder produces valid, comma-correct output", () =>
            {
                var json = new JsonBuilder();
                json.BeginObject()
                    .Prop("ok", true)
                    .Prop("count", 3)
                    .Name("items").BeginArray();

                for (int i = 0; i < 3; i++)
                {
                    json.BeginObject().Prop("id", i).EndObject();
                }

                json.EndArray().EndObject();

                string text = json.ToString();

                Harness.Equal("{\"ok\":true,\"count\":3,\"items\":[{\"id\":0},{\"id\":1},{\"id\":2}]}",
                    text, "serialised");
            });

            Harness.Test("strings are escaped correctly", () =>
            {
                var json = new JsonBuilder();
                json.BeginObject().Prop("text", "quote\" slash\\ newline\n tab\t").EndObject();

                Harness.Equal("{\"text\":\"quote\\\" slash\\\\ newline\\n tab\\t\"}", json.ToString(), "escaped");
            });

            Harness.Test("null values are written as null, not as empty strings", () =>
            {
                var json = new JsonBuilder();
                json.BeginObject().Prop("value", (string)null).EndObject();

                Harness.Equal("{\"value\":null}", json.ToString(), "null");
            });

            Harness.Test("numbers use invariant formatting", () =>
            {
                var json = new JsonBuilder();
                json.BeginObject().Prop("a", 1.5).Prop("b", 42).EndObject();

                Harness.Equal("{\"a\":1.5,\"b\":42}", json.ToString(), "numbers");
            });

            Harness.Test("reading a string field works", () =>
            {
                string json = "{\"name\":\"Dart\",\"reason\":\"AFK\"}";

                Harness.Equal("Dart", JsonReader.GetString(json, "name"), "name");
                Harness.Equal("AFK", JsonReader.GetString(json, "reason"), "reason");
                Harness.Equal("fallback", JsonReader.GetString(json, "missing", "fallback"), "missing");
            });

            Harness.Test("reading escaped strings works", () =>
            {
                string json = "{\"text\":\"line one\\nline two \\\"quoted\\\"\"}";

                Harness.Equal("line one\nline two \"quoted\"", JsonReader.GetString(json, "text"), "unescaped");
            });

            Harness.Test("reading numbers and booleans works", () =>
            {
                string json = "{\"count\":42,\"flag\":true}";

                Harness.Equal(42, JsonReader.GetInt(json, "count", 0), "int");
                Harness.True(JsonReader.GetBool(json, "flag", false), "bool");
                Harness.Equal(7, JsonReader.GetInt(json, "missing", 7), "missing int");
            });

            Harness.Test("reading from malformed input does not throw", () =>
            {
                Harness.Null(JsonReader.GetString("", "a"), "empty");
                Harness.Null(JsonReader.GetString(null, "a"), "null");
                Harness.Null(JsonReader.GetString("not json at all", "a"), "garbage");
            });
        }

        private static void PathTests()
        {
            Harness.Test("normal paths are left alone", () =>
            {
                Harness.Equal("/api/status", HttpServer.NormalisePath("/api/status"), "simple");
                Harness.Equal("/", HttpServer.NormalisePath("/"), "root");
                Harness.Equal("/", HttpServer.NormalisePath(""), "empty");
            });

            Harness.Test("duplicate slashes are collapsed", () =>
            {
                Harness.Equal("/api/status", HttpServer.NormalisePath("//api///status"), "collapsed");
            });

            Harness.Test("dot segments are resolved", () =>
            {
                Harness.Equal("/api/status", HttpServer.NormalisePath("/api/./status"), "single dot");
                Harness.Equal("/status", HttpServer.NormalisePath("/api/../status"), "double dot");
            });

            Harness.Test("escaping above the root is clamped", () =>
            {
                Harness.Equal("/etc/passwd", HttpServer.NormalisePath("/../../etc/passwd"), "clamped at root");
            });

            Harness.Test("encoded traversal is decoded before normalising", () =>
            {
                Harness.Equal("/etc/passwd", HttpServer.NormalisePath("/%2e%2e/%2e%2e/etc/passwd"), "percent-encoded");
            });

            Harness.Test("a NUL byte is rejected", () =>
            {
                Harness.Null(HttpServer.NormalisePath("/a%00b"), "NUL");
            });
        }

        private static void CidrTests()
        {
            Harness.Test("exact addresses match", () =>
            {
                Harness.True(Cidr.Contains("127.0.0.1", "127.0.0.1"), "exact");
                Harness.False(Cidr.Contains("127.0.0.1", "127.0.0.2"), "different address");
            });

            Harness.Test("ranges match their members", () =>
            {
                Harness.True(Cidr.Contains("192.168.0.0/16", "192.168.1.50"), "inside /16");
                Harness.True(Cidr.Contains("192.168.0.0/16", "192.168.255.255"), "upper bound");
                Harness.False(Cidr.Contains("192.168.0.0/16", "192.169.0.1"), "outside /16");
                Harness.True(Cidr.Contains("10.0.0.0/8", "10.255.1.1"), "inside /8");
                Harness.False(Cidr.Contains("10.0.0.0/8", "11.0.0.1"), "outside /8");
            });

            Harness.Test("narrow ranges are exact", () =>
            {
                Harness.True(Cidr.Contains("192.168.1.0/24", "192.168.1.255"), "inside /24");
                Harness.False(Cidr.Contains("192.168.1.0/24", "192.168.2.1"), "outside /24");
                Harness.True(Cidr.Contains("172.16.0.0/12", "172.31.255.255"), "inside /12");
                Harness.False(Cidr.Contains("172.16.0.0/12", "172.32.0.1"), "outside /12");
            });

            Harness.Test("private ranges are recognised", () =>
            {
                Harness.True(Cidr.IsPrivate("127.0.0.1"), "loopback");
                Harness.True(Cidr.IsPrivate("192.168.1.10"), "home LAN");
                Harness.True(Cidr.IsPrivate("10.1.2.3"), "corporate LAN");
                Harness.True(Cidr.IsPrivate("172.16.5.5"), "docker LAN");
                Harness.False(Cidr.IsPrivate("8.8.8.8"), "public DNS");
                Harness.False(Cidr.IsPrivate("203.0.113.7"), "public");
            });

            Harness.Test("malformed input fails closed", () =>
            {
                Harness.False(Cidr.Contains("", "10.0.0.1"), "empty range");
                Harness.False(Cidr.Contains("10.0.0.0/8", ""), "empty address");
                Harness.False(Cidr.Contains("not-an-ip/8", "10.0.0.1"), "bad range");
                Harness.False(Cidr.Contains("10.0.0.0/abc", "10.0.0.1"), "bad prefix");
            });
        }

        private static void LoggingTests()
        {
            Harness.Test("the file listener writes a timestamped file", () =>
            {
                string directory = Path.Combine(Path.GetTempPath(), "dartscape-log-" + Guid.NewGuid().ToString("N"));

                try
                {
                    using (var listener = new FileTraceListener("test", directory))
                    {
                        listener.WriteLine("first line");
                        listener.WriteLine("second line");
                        listener.Flush();

                        Harness.NotNull(listener.CurrentPath, "current path");
                        Harness.True(File.Exists(listener.CurrentPath), "file exists");
                        Harness.True(Path.GetFileName(listener.CurrentPath).StartsWith("test_"),
                            "file name starts with the log name");

                        // The listener keeps the file open for appending, so it has to be
                        // read with a sharing mode that permits a concurrent writer.
                        string content;
                        using (var stream = new FileStream(listener.CurrentPath, FileMode.Open,
                                   FileAccess.Read, FileShare.ReadWrite))
                        using (var reader = new StreamReader(stream))
                        {
                            content = reader.ReadToEnd();
                        }

                        Harness.True(content.Contains("first line"), "first line written");
                        Harness.True(content.Contains("second line"), "second line written");
                    }
                }
                finally
                {
                    try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (Exception) { }
                }
            });

            Harness.Test("the ring buffer keeps the most recent records", () =>
            {
                var options = new LogOptions { EchoToConsole = false, RecentCapacity = 5 };
                Log.Configure("test", options);

                for (int i = 0; i < 20; i++) Log.Info("Test", "message " + i);

                LogRecord[] recent = Log.GetRecent(3);
                Harness.Equal(3, recent.Length, "three records");
                Harness.Equal("message 19", recent[2].Message, "newest last");
                Harness.Equal("message 17", recent[0].Message, "oldest of the three first");
            });
        }

        private static string TempFile(string extension)
        {
            return Path.Combine(Path.GetTempPath(),
                "dartscape-test-" + Guid.NewGuid().ToString("N") + "." + extension);
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }
    }
}
