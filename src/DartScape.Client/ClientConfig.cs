using System;
using System.Collections.Generic;

namespace DartScape.Client
{
    /// <summary>Client-side configuration, loaded from <c>config.ini</c> next to the exe.</summary>
    public sealed class ClientConfig
    {
        public string Host = "127.0.0.1";
        public int Port = 43594;
        public string Name = "";
        public string Password = "";
        public string CacheDir = "cache";
        public int Width = 765;
        public int Height = 503;
        public int Revision = 317;
        public bool MusicEnabled;
        public bool SoundEnabled = true;
        public string Theme = "light";

        public static ClientConfig Load(string baseDir)
        {
            var config = new ClientConfig();
            string path = System.IO.Path.Combine(baseDir, "config.ini");

            var ini = Core.Configuration.IniConfig.Load(path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "client.host", "127.0.0.1" },
                { "client.port", "43594" },
                { "client.cacheDir", "cache" },
                { "client.width", "765" },
                { "client.height", "503" },
                { "client.music", "false" },
                { "client.sound", "true" },
                { "client.theme", "light" }
            });

            config.Host = ini.GetString("client.host", config.Host);
            config.Port = ini.GetInt("client.port", config.Port);
            config.CacheDir = ini.GetString("client.cacheDir", config.CacheDir);
            config.Width = Math.Max(640, ini.GetInt("client.width", config.Width));
            config.Height = Math.Max(480, ini.GetInt("client.height", config.Height));
            config.MusicEnabled = ini.GetBool("client.music", config.MusicEnabled);
            config.SoundEnabled = ini.GetBool("client.sound", config.SoundEnabled);
            config.Theme = ini.GetString("client.theme", config.Theme);
            config.Revision = ini.GetInt("client.revision", config.Revision);

            return config;
        }
    }
}
