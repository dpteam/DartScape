using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;
using DartScape.Core.Diagnostics;

namespace DartScape.Launcher
{
    /// <summary>
    /// The launcher window: an MSHTML host that renders the interface from embedded
    /// assets and talks to the game through a cancelled private-scheme navigation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The choice of <see cref="WebBrowser"/> is not laziness — it is the only embedded
    /// browser that exists on Windows XP without a download. WebView2 has no XP build, and
    /// bundling Chromium would multiply the installer size by thirty. MSHTML is already
    /// installed on every machine this launcher targets.
    /// </para>
    /// <para>
    /// The bridge deliberately avoids <c>ObjectForScripting</c> and <c>window.external</c>:
    /// both behave differently depending on the document's zone and on which MSHTML build
    /// is present, and both fail silently. A navigation that the host cancels is a single,
    /// well-defined mechanism that works everywhere.
    /// </para>
    /// </remarks>
    public sealed class LauncherForm : Form
    {
        /// <summary>Private scheme used by the page to call into the host.</summary>
        private const string BridgeScheme = "dartscape://";

        private readonly WebBrowser _browser;
        private readonly LauncherSettings _settings;
        private Process _game;

        public LauncherForm()
        {
            _settings = LauncherSettings.Load();

            Text = "DartScape";
            ClientSize = new System.Drawing.Size(520, 620);
            MinimumSize = new System.Drawing.Size(480, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = System.Drawing.Color.FromArgb(223, 230, 238);

            try
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception)
            {
                // Missing icon resource is not worth failing over.
            }

            _browser = new WebBrowser
            {
                Dock = DockStyle.Fill,
                IsWebBrowserContextMenuEnabled = false,
                AllowWebBrowserDrop = false,
                WebBrowserShortcutsEnabled = true,
                ScriptErrorsSuppressed = true
            };

            _browser.Navigating += OnNavigating;
            _browser.DocumentCompleted += OnDocumentCompleted;

            Controls.Add(_browser);

            LoadPage();
        }

        // -- page loading -----------------------------------------------------

        /// <summary>
        /// Assembles the three embedded assets into one document and writes it to disk.
        /// </summary>
        /// <remarks>
        /// Loading from a real file rather than assigning <c>DocumentText</c> matters: a
        /// document created from a string lives at <c>about:blank</c>, and MSHTML does not
        /// consistently treat that origin as trusted, which breaks the bridge on some
        /// Windows versions. A file in the local machine zone always works.
        /// </remarks>
        private void LoadPage()
        {
            try
            {
                string directory = Path.Combine(LauncherSettings.DataDirectory, "webview");
                Directory.CreateDirectory(directory);

                string path = Path.Combine(directory, "launcher.html");
                File.WriteAllText(path, AssemblePage(), new UTF8Encoding(false));

                _browser.Navigate(path);
            }
            catch (Exception ex)
            {
                Log.Exception("Launcher", ex, "Could not load the interface");
                MessageBox.Show(this,
                    "The launcher interface could not be loaded.\n\n" + ex.Message,
                    "DartScape", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string AssemblePage()
        {
            string html = ReadAsset("ui.html");
            html = html.Replace("/*__STYLE__*/", ReadAsset("theme.css"));
            html = html.Replace("/*__SCRIPT__*/", ReadAsset("app.js"));
            return html;
        }

        private static string ReadAsset(string name)
        {
            System.Reflection.Assembly assembly = typeof(LauncherForm).Assembly;

            using (Stream stream = assembly.GetManifestResourceStream("DartScape.Launcher.Assets." + name))
            {
                if (stream == null) throw new InvalidOperationException("Missing embedded asset: " + name);

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        // -- bridge -----------------------------------------------------------

        private void OnDocumentCompleted(object sender, WebBrowserDocumentCompletedEventArgs e)
        {
            // Push the saved settings once the page is ready to receive them.
            SendSettings();
        }

        private void OnNavigating(object sender, WebBrowserNavigatingEventArgs e)
        {
            string url = e.Url == null ? string.Empty : e.Url.AbsoluteUri;

            if (!url.StartsWith(BridgeScheme, StringComparison.OrdinalIgnoreCase)) return;

            e.Cancel = true;

            string verb = url.Substring(BridgeScheme.Length).Trim('/');

            int query = verb.IndexOf('?');
            if (query >= 0) verb = verb.Substring(0, query);

            string payload = TakePayload();

            try
            {
                HandleVerb(verb, payload);
            }
            catch (Exception ex)
            {
                Log.Exception("Launcher", ex, "Bridge verb '" + verb + "' failed");
                Send("error", "Launcher error: " + ex.Message);
            }
        }

        private string TakePayload()
        {
            if (_browser.Document == null) return string.Empty;

            try
            {
                object result = _browser.Document.InvokeScript("dartscapeTakePayload");
                return Convert.ToString(result, CultureInfo.InvariantCulture) ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>Pushes a verb and payload into the page.</summary>
        public void Send(string verb, string payload)
        {
            if (_browser.Document == null) return;

            try
            {
                _browser.Document.InvokeScript("dartscapeReceive",
                    new object[] { verb ?? string.Empty, payload ?? string.Empty });
            }
            catch (Exception)
            {
                // The document is navigating or has been replaced.
            }
        }

        /// <summary>
        /// Dispatches a bridge verb.
        /// </summary>
        /// <remarks>
        /// The comparison is case-insensitive on purpose: MSHTML lower-cases the authority
        /// part of a URI, so <c>dartscape://saveSettings</c> arrives as
        /// <c>savesettings</c>. A case-sensitive switch silently ignores every camel-case
        /// verb, which is a genuinely nasty bug to chase.
        /// </remarks>
        private void HandleVerb(string verb, string payload)
        {
            string name = (verb ?? string.Empty).Trim().ToLowerInvariant();
            Log.Debug("Launcher", "verb {0} ({1} chars)", name, payload == null ? 0 : payload.Length);

            switch (name)
            {
                case "ready":
                    SendSettings();
                    break;

                case "play":
                    OnPlay(payload);
                    break;

                case "ping":
                    OnPing(payload);
                    break;

                case "worlds":
                    OnWorlds();
                    break;

                case "savesettings":
                    OnSaveSettings(payload);
                    break;

                case "themesave":
                    _settings.Theme = payload;
                    _settings.Save();
                    break;

                case "openlogs":
                    OpenLogs();
                    break;

                default:
                    Log.Debug("Launcher", "unknown verb '{0}' ignored", name);
                    break;
            }
        }

        // -- verbs ------------------------------------------------------------

        private void SendSettings()
        {
            var json = new StringBuilder(256);
            json.Append('{');
            json.Append("\"name\":").Append(JsonQuote(_settings.Name)).Append(',');
            json.Append("\"password\":").Append(JsonQuote(_settings.Password)).Append(',');
            json.Append("\"server\":").Append(JsonQuote(_settings.Server)).Append(',');
            json.Append("\"width\":").Append(_settings.Width.ToString(CultureInfo.InvariantCulture)).Append(',');
            json.Append("\"height\":").Append(_settings.Height.ToString(CultureInfo.InvariantCulture)).Append(',');
            json.Append("\"cache\":").Append(JsonQuote(_settings.CacheDir)).Append(',');
            json.Append("\"sound\":").Append(_settings.Sound ? "true" : "false").Append(',');
            json.Append("\"music\":").Append(_settings.Music ? "true" : "false").Append(',');
            json.Append("\"theme\":").Append(JsonQuote(_settings.Theme)).Append(',');
            json.Append("\"version\":").Append(JsonQuote(LauncherSettings.Version));
            json.Append('}');

            Send("settings", json.ToString());
        }

        private void OnSaveSettings(string payload)
        {
            Dictionary<string, string> values = ParseFlatJson(payload);
            if (values == null) return;

            _settings.Name = Get(values, "name", _settings.Name);
            _settings.Password = Get(values, "password", _settings.Password);
            _settings.Server = Get(values, "server", _settings.Server);
            _settings.CacheDir = Get(values, "cache", _settings.CacheDir);
            _settings.Theme = Get(values, "theme", _settings.Theme);
            _settings.Width = GetInt(values, "width", _settings.Width);
            _settings.Height = GetInt(values, "height", _settings.Height);
            _settings.Sound = GetBool(values, "sound", _settings.Sound);
            _settings.Music = GetBool(values, "music", _settings.Music);

            _settings.Save();
            Send("status", "Settings saved.");
        }

        private void OnPlay(string payload)
        {
            Dictionary<string, string> values = ParseFlatJson(payload);
            if (values == null)
            {
                Send("error", "The launcher could not read the form.");
                return;
            }

            _settings.Name = Get(values, "name", _settings.Name);
            _settings.Password = Get(values, "password", _settings.Password);
            _settings.Server = Get(values, "server", _settings.Server);
            _settings.CacheDir = Get(values, "cache", _settings.CacheDir);
            _settings.Width = GetInt(values, "width", _settings.Width);
            _settings.Height = GetInt(values, "height", _settings.Height);
            _settings.Sound = GetBool(values, "sound", _settings.Sound);
            _settings.Music = GetBool(values, "music", _settings.Music);
            _settings.Save();

            LaunchGame();
        }

        private void LaunchGame()
        {
            if (_game != null && !_game.HasExited)
            {
                Send("status", "DartScape is already running.");
                return;
            }

            string exe = LauncherSettings.FindClientExecutable();
            if (exe == null)
            {
                Send("error", "DartScape.Client.exe was not found next to the launcher.");
                return;
            }

            string host = _settings.Host;
            int port = _settings.Port;

            // A player on the same network as the server cannot reach it by the public
            // name: the router will not loop the connection back inside. Fall back to the
            // loopback address, which is correct when the launcher runs on the server box,
            // and fails harmlessly (in under a second) when it does not.
            string reachableHost;
            int reachablePort;

            if (TryResolveReachable(host, port, out reachableHost, out reachablePort))
            {
                if (reachableHost != host)
                {
                    Send("status", "Using the local server at " + reachableHost + ":" + reachablePort + ".");
                    Log.Info("Launcher", "Public address unreachable; using {0}:{1}.", reachableHost, reachablePort);
                }

                host = reachableHost;
                port = reachablePort;
            }

            var arguments = new StringBuilder(256);
            arguments.Append("--server ").Append(Quote(host + ":" + port.ToString(CultureInfo.InvariantCulture)));
            arguments.Append(" --name ").Append(Quote(_settings.Name));
            arguments.Append(" --password ").Append(Quote(_settings.Password));
            arguments.Append(" --width ").Append(_settings.Width.ToString(CultureInfo.InvariantCulture));
            arguments.Append(" --height ").Append(_settings.Height.ToString(CultureInfo.InvariantCulture));

            if (!string.IsNullOrEmpty(_settings.CacheDir))
            {
                arguments.Append(" --cache ").Append(Quote(_settings.CacheDir));
            }

            try
            {
                var startInfo = new ProcessStartInfo(exe, arguments.ToString())
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? "."
                };

                // Told to the client so it does not pause on exit waiting for a key.
                startInfo.EnvironmentVariables["DARTSCAPE_LAUNCHED"] = "1";

                _game = Process.Start(startInfo);

                if (_game == null)
                {
                    Send("error", "The client did not start.");
                    return;
                }

                _game.EnableRaisingEvents = true;
                _game.Exited += OnGameExited;

                Log.Info("Launcher", "Started {0} (pid {1}) for {2}.", exe, _game.Id, _settings.Name);
                Send("launched", "DartScape is starting. Have fun, " + _settings.Name + "!");

                // Hide the launcher while playing; the taskbar button brings it back.
                WindowState = FormWindowState.Minimized;
            }
            catch (Exception ex)
            {
                Log.Exception("Launcher", ex, "Could not start the client");
                Send("error", "Could not start DartScape: " + ex.Message);
            }
        }

        /// <summary>
        /// Finds an address that actually accepts a connection.
        /// </summary>
        /// <remarks>
        /// Tries the configured address first, then loopback. The loopback attempt is what
        /// makes the launcher work on the machine that also hosts the server: NAT hairpin
        /// blocks the public name from inside the network, but 127.0.0.1 never leaves the
        /// machine. When the launcher runs elsewhere the loopback attempt is refused
        /// instantly, so the cost is negligible.
        /// </remarks>
        private static bool TryResolveReachable(string host, int port, out string reachableHost, out int reachablePort)
        {
            reachableHost = host;
            reachablePort = port;

            if (CanConnect(host, port)) return true;

            if (host != "127.0.0.1" && host != "localhost" && CanConnect("127.0.0.1", port))
            {
                reachableHost = "127.0.0.1";
                reachablePort = port;
                return true;
            }

            return false;
        }

        /// <summary>Quick TCP reachability probe with a short timeout.</summary>
        private static bool CanConnect(string host, int port)
        {
            if (string.IsNullOrEmpty(host) || port < 1 || port > 65535) return false;

            try
            {
                using (var client = new TcpClient())
                {
                    IAsyncResult result = client.BeginConnect(host, port, null, null);
                    if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2))) return false;
                    if (!client.Connected) return false;

                    client.EndConnect(result);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void OnGameExited(object sender, EventArgs e)
        {
            try
            {
                BeginInvoke((MethodInvoker)(() =>
                {
                    int code = -1;
                    try { code = _game == null ? -1 : _game.ExitCode; } catch (Exception) { }

                    WindowState = FormWindowState.Normal;
                    Activate();

                    if (code == 0)
                    {
                        Send("status", "DartScape closed.");
                    }
                    else
                    {
                        Send("error", "DartScape closed unexpectedly (code " + code + "). See the logs folder.");
                    }
                }));
            }
            catch (Exception)
            {
                // Window is closing.
            }
        }

        private void OnPing(string serverText)
        {
            string host;
            int port;
            if (!LauncherSettings.SplitAddress(serverText, out host, out port))
            {
                Send("ping", "{\"ok\":false,\"ms\":0,\"why\":\"That address is not valid. Use host:port.\"}");
                return;
            }

            // Try the configured address, then loopback: the launcher may be running on
            // the same machine as the server, where the public name does not hairpin.
            string reachableHost;
            int reachablePort;
            bool found = TryResolveReachable(host, port, out reachableHost, out reachablePort);

            var stopwatch = Stopwatch.StartNew();
            bool ok = found && CanConnect(reachableHost, reachablePort);
            stopwatch.Stop();

            string why = null;

            if (!ok)
            {
                // Say which of the two likely causes it is, because "unreachable" tells
                // nobody anything useful.
                if (CanConnect("127.0.0.1", port))
                {
                    why = "The server is running on this machine but " + host +
                          " does not answer. Use 127.0.0.1:" + port + " or the LAN address.";
                }
                else
                {
                    why = "Nothing is listening on " + host + ":" + port +
                          ". Start DartScape.Server.exe, and make sure the port is forwarded.";
                }
            }

            var json = new StringBuilder(160);
            json.Append("{\"ok\":").Append(ok ? "true" : "false");
            json.Append(",\"ms\":").Append(ok ? stopwatch.ElapsedMilliseconds : 0);
            json.Append(",\"via\":").Append(JsonQuote(reachableHost + ":" + reachablePort));
            if (why != null) json.Append(",\"why\":").Append(JsonQuote(why));
            json.Append('}');

            Send("ping", json.ToString());
        }

        private void OnWorlds()
        {
            string host = _settings.Host;
            int port = _settings.Port;

            // The world list comes from the public web service, which lives one port
            // above the game port by convention.
            int webPort = port + 1;

            // Same hairpin fallback as Play: on the server box, the public name is
            // unreachable from inside the network but loopback works.
            string reachableHost;
            int reachableWebPort;
            TryResolveReachable(host, webPort, out reachableHost, out reachableWebPort);

            string json = null;
            try
            {
                json = FetchWorlds(reachableHost, reachableWebPort);
            }
            catch (Exception ex)
            {
                Log.Warning("Launcher", "World list failed: {0}", ex.Message);
            }

            if (string.IsNullOrEmpty(json))
            {
                // Fall back to the single server the user configured, so the tab is never
                // empty and the "Join" button still does something useful.
                json = "[{\"name\":\"DartScape\",\"address\":" + JsonQuote(host) +
                       ",\"port\":" + port.ToString(CultureInfo.InvariantCulture) +
                       ",\"players\":0,\"max\":0,\"region\":\"EU\",\"online\":true}]";
            }

            Send("worlds", json);
        }

        /// <summary>Fetches the world list over plain HTTP from the web service.</summary>
        private static string FetchWorlds(string host, int port)
        {
            using (var client = new TcpClient())
            {
                IAsyncResult connect = client.BeginConnect(host, port, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(4)) || !client.Connected)
                {
                    return null;
                }
                client.EndConnect(connect);

                using (NetworkStream stream = client.GetStream())
                {
                    stream.ReadTimeout = 4000;

                    byte[] request = Encoding.ASCII.GetBytes(
                        "GET /api/worlds HTTP/1.1\r\nHost: " + host + "\r\nConnection: close\r\n\r\n");
                    stream.Write(request, 0, request.Length);

                    var buffer = new byte[16384];
                    var response = new StringBuilder(4096);
                    int read;

                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        response.Append(Encoding.UTF8.GetString(buffer, 0, read));
                        if (response.Length > 64 * 1024) break;
                    }

                    string text = response.ToString();
                    int body = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (body < 0) return null;

                    string payload = text.Substring(body + 4).Trim();

                    // Extract just the "worlds" array so the page gets what it expects.
                    int start = payload.IndexOf('[');
                    int end = payload.LastIndexOf(']');
                    if (start < 0 || end <= start) return null;

                    return payload.Substring(start, end - start + 1);
                }
            }
        }

        private void OpenLogs()
        {
            try
            {
                string logs = Path.Combine(LauncherSettings.DataDirectory, "logs");
                Directory.CreateDirectory(logs);
                Process.Start("explorer.exe", logs);
            }
            catch (Exception ex)
            {
                Log.Warning("Launcher", "Could not open the logs folder: {0}", ex.Message);
            }
        }

        // -- tiny JSON helpers ------------------------------------------------

        private static string JsonQuote(string value)
        {
            if (value == null) return "\"\"";

            var sb = new StringBuilder(value.Length + 8);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// Parses the flat <c>{"key":"value",...}</c> objects the page sends.
        /// </summary>
        /// <remarks>
        /// A full JSON parser is unnecessary here: the page sends one shape, and this is
        /// the only place in the launcher that needs to read JSON at all. Writing a
        /// general parser would be more code to get wrong.
        /// </remarks>
        private static Dictionary<string, string> ParseFlatJson(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            int i = text.IndexOf('{');
            if (i < 0) return null;
            i++;

            while (i < text.Length)
            {
                while (i < text.Length && (text[i] == ',' || char.IsWhiteSpace(text[i]))) i++;
                if (i >= text.Length || text[i] == '}') break;

                if (text[i] != '"') break;
                int keyStart = ++i;
                while (i < text.Length && text[i] != '"') i++;
                string key = text.Substring(keyStart, i - keyStart);
                i++;

                while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ':')) i++;
                if (i >= text.Length) break;

                string value;
                if (text[i] == '"')
                {
                    int valueStart = ++i;
                    var sb = new StringBuilder(32);
                    while (i < text.Length && text[i] != '"')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length)
                        {
                            i++;
                            switch (text[i])
                            {
                                case 'n': sb.Append('\n'); break;
                                case 'r': sb.Append('\r'); break;
                                case 't': sb.Append('\t'); break;
                                case '"': sb.Append('"'); break;
                                case '\\': sb.Append('\\'); break;
                                default: sb.Append(text[i]); break;
                            }
                        }
                        else
                        {
                            sb.Append(text[i]);
                        }
                        i++;
                    }
                    value = sb.ToString();
                    i++;
                }
                else
                {
                    int valueStart = i;
                    while (i < text.Length && text[i] != ',' && text[i] != '}') i++;
                    value = text.Substring(valueStart, i - valueStart).Trim();
                }

                result[key] = value;
            }

            return result;
        }

        private static string Get(Dictionary<string, string> values, string key, string fallback)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : fallback;
        }

        private static int GetInt(Dictionary<string, string> values, string key, int fallback)
        {
            string value;
            if (!values.TryGetValue(key, out value)) return fallback;

            int parsed;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed : fallback;
        }

        private static bool GetBool(Dictionary<string, string> values, string key, bool fallback)
        {
            string value;
            if (!values.TryGetValue(key, out value)) return fallback;

            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            _settings.Save();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_browser != null)
                {
                    _browser.Navigating -= OnNavigating;
                    _browser.DocumentCompleted -= OnDocumentCompleted;
                    _browser.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
