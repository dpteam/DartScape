using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using DartScape.Core.Diagnostics;

namespace DartScape.Web.Http
{
    /// <summary>One parsed HTTP request.</summary>
    public sealed class HttpRequest
    {
        public string Method = "GET";
        public string RawPath = "/";
        public string Path = "/";
        public string Query = "";
        public string Version = "HTTP/1.1";
        public string Body = "";
        public byte[] BodyBytes = new byte[0];
        public string RemoteAddress = "0.0.0.0";
        public bool KeepAlive = true;

        private readonly Dictionary<string, string> _headers =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Sets a header. First value wins, matching what proxies expect.</summary>
        public void SetHeader(string name, string value)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (!_headers.ContainsKey(name)) _headers[name] = value;
        }

        /// <summary>Returns a header value, or <c>null</c>.</summary>
        public string Header(string name)
        {
            string value;
            return _headers.TryGetValue(name, out value) ? value : null;
        }

        /// <summary>Reads a query-string parameter, or <paramref name="fallback"/>.</summary>
        public string QueryString(string name, string fallback = null)
        {
            if (string.IsNullOrEmpty(Query)) return fallback;

            foreach (string pair in Query.Split('&'))
            {
                if (pair.Length == 0) continue;

                int eq = pair.IndexOf('=');
                string key = eq < 0 ? pair : pair.Substring(0, eq);

                if (!string.Equals(Uri.UnescapeDataString(key), name, StringComparison.OrdinalIgnoreCase)) continue;

                string value = eq < 0 ? string.Empty : pair.Substring(eq + 1);
                return Uri.UnescapeDataString(value.Replace('+', ' '));
            }

            return fallback;
        }

        /// <summary>Reads a query-string parameter as an integer.</summary>
        public int QueryInt(string name, int fallback)
        {
            string raw = QueryString(name, null);
            if (raw == null) return fallback;

            int parsed;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }
    }

    /// <summary>One HTTP response under construction.</summary>
    public sealed class HttpResponse
    {
        public int Status = 200;
        public string ContentType = "text/plain; charset=utf-8";
        public byte[] Body = new byte[0];
        public bool KeepAlive = true;

        private readonly Dictionary<string, string> _headers =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public void SetHeader(string name, string value)
        {
            _headers[name] = value;
        }

        public IEnumerable<KeyValuePair<string, string>> Headers
        {
            get { return _headers; }
        }

        /// <summary>Sets a JSON body.</summary>
        public void Json(string json, int status = 200)
        {
            Status = status;
            ContentType = "application/json; charset=utf-8";
            Body = Encoding.UTF8.GetBytes(json ?? "{}");
        }

        /// <summary>Sets a plain text body.</summary>
        public void Text(string text, int status = 200)
        {
            Status = status;
            ContentType = "text/plain; charset=utf-8";
            Body = Encoding.UTF8.GetBytes(text ?? string.Empty);
        }

        /// <summary>Sets an HTML body.</summary>
        public void Html(string html, int status = 200)
        {
            Status = status;
            ContentType = "text/html; charset=utf-8";
            Body = Encoding.UTF8.GetBytes(html ?? string.Empty);
        }

        /// <summary>Emits the standard error object.</summary>
        public void Error(int status, string code, string message)
        {
            var json = new JsonBuilder(160);
            json.BeginObject()
                .Prop("ok", false)
                .Prop("error", code)
                .Prop("message", message)
                .EndObject();

            Json(json.ToString(), status);
        }
    }

    /// <summary>
    /// A small HTTP/1.1 server built directly on <see cref="TcpListener"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="HttpListener"/> is not used, for two concrete reasons: on Windows it
    /// requires a URL reservation (administrator rights) for anything other than
    /// localhost, and its behaviour differs between XP and later versions in ways that
    /// matter when a reverse proxy is in front. A hand-written parser over a plain socket
    /// behaves identically everywhere.
    /// </para>
    /// <para>
    /// The parser implements the subset of HTTP/1.1 that browsers and Nginx Proxy Manager
    /// actually send: request line, headers, <c>Content-Length</c> bodies, chunked
    /// bodies, <c>Expect: 100-continue</c>, and keep-alive.
    /// </para>
    /// </remarks>
    public sealed class HttpServer : IDisposable
    {
        private const int MaxRequestLine = 8192;
        private const int MaxHeaders = 32 * 1024;
        private const int MaxBody = 2 * 1024 * 1024;
        private const int MaxRequestsPerConnection = 100;

        private readonly string _name;
        private readonly IPAddress _bind;
        private readonly int _port;
        private readonly Func<HttpRequest, HttpResponse, bool> _handler;

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        /// <summary>Raised when a request has been fully read.</summary>
        public HttpServer(string name, IPAddress bind, int port, Func<HttpRequest, HttpResponse, bool> handler)
        {
            if (handler == null) throw new ArgumentNullException("handler");

            _name = name ?? "http";
            _bind = bind ?? IPAddress.Any;
            _port = port;
            _handler = handler;
        }

        /// <summary>The port actually bound, which may differ from the requested one.</summary>
        public int Port { get; private set; }

        /// <summary>True while the accept loop is running.</summary>
        public bool IsRunning
        {
            get { return _running; }
        }

        /// <summary>Number of connections currently being served.</summary>
        public int ActiveConnections
        {
            get { return _activeConnections; }
        }

        private int _activeConnections;

        /// <summary>Starts listening. Throws when the port cannot be bound.</summary>
        public void Start()
        {
            _listener = new TcpListener(_bind, _port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start(64);

            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;

            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "DartScape.Web." + _name
            };
            _acceptThread.Start();

            Log.Info("Web", "{0} listening on {1}:{2}.", _name, _bind, Port);
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;

                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    if (!_running) return;
                    Thread.Sleep(100);
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                Interlocked.Increment(ref _activeConnections);
                ThreadPool.QueueUserWorkItem(state => ServeConnection((TcpClient)state), client);
            }
        }

        private void ServeConnection(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 15000;
                client.SendTimeout = 15000;

                using (NetworkStream stream = client.GetStream())
                {
                    for (int served = 0; served < MaxRequestsPerConnection; served++)
                    {
                        HttpRequest request = ReadRequest(stream, client);
                        if (request == null) return;

                        var response = new HttpResponse { KeepAlive = request.KeepAlive };

                        bool handled;
                        try
                        {
                            handled = _handler(request, response);
                        }
                        catch (Exception ex)
                        {
                            Log.Exception("Web", ex, "Handler failed for " + request.Path);
                            response = new HttpResponse { KeepAlive = false };
                            response.Error(500, "internal-error", "The server hit an unexpected error.");
                            handled = true;
                        }

                        if (!handled)
                        {
                            response = new HttpResponse { KeepAlive = request.KeepAlive };
                            response.Error(404, "unknown-endpoint", "No such endpoint.");
                        }

                        WriteResponse(stream, request, response);

                        if (!response.KeepAlive || !request.KeepAlive) return;
                    }
                }
            }
            catch (IOException)
            {
                // Client went away mid-request; entirely normal.
            }
            catch (SocketException)
            {
                // Same.
            }
            catch (Exception ex)
            {
                Log.Exception("Web", ex, "Connection failed");
            }
            finally
            {
                Interlocked.Decrement(ref _activeConnections);
                try { client.Close(); } catch (Exception) { }
            }
        }

        // -- request parsing --------------------------------------------------

        private static HttpRequest ReadRequest(NetworkStream stream, TcpClient client)
        {
            var request = new HttpRequest();

            string requestLine = ReadLine(stream, MaxRequestLine);
            if (requestLine == null) return null;

            // Skip leading blank lines, which some clients send after a keep-alive.
            int guard = 0;
            while (requestLine.Length == 0)
            {
                if (++guard > 4) return null;
                requestLine = ReadLine(stream, MaxRequestLine);
                if (requestLine == null) return null;
            }

            string[] parts = requestLine.Split(' ');
            if (parts.Length < 3) return null;

            request.Method = parts[0].ToUpperInvariant();
            request.Version = parts[2];

            string target = parts[1];
            int queryIndex = target.IndexOf('?');
            if (queryIndex >= 0)
            {
                request.RawPath = target.Substring(0, queryIndex);
                request.Query = target.Substring(queryIndex + 1);
            }
            else
            {
                request.RawPath = target;
            }

            request.Path = NormalisePath(request.RawPath);

            try
            {
                IPEndPoint remote = client.Client.RemoteEndPoint as IPEndPoint;
                request.RemoteAddress = remote == null ? "0.0.0.0" : remote.Address.ToString();
            }
            catch (Exception)
            {
                request.RemoteAddress = "0.0.0.0";
            }

            int headerBytes = 0;

            while (true)
            {
                string line = ReadLine(stream, MaxHeaders);
                if (line == null) return null;
                if (line.Length == 0) break;

                headerBytes += line.Length;
                if (headerBytes > MaxHeaders) return null;

                int colon = line.IndexOf(':');
                if (colon <= 0) continue;

                request.SetHeader(line.Substring(0, colon).Trim(), line.Substring(colon + 1).Trim());
            }

            string connection = request.Header("Connection");
            request.KeepAlive = connection == null ||
                connection.IndexOf("close", StringComparison.OrdinalIgnoreCase) < 0;

            if (request.Version == "HTTP/1.0" && connection == null) request.KeepAlive = false;

            // 100-continue: some clients wait for it before sending the body.
            string expect = request.Header("Expect");
            if (expect != null && expect.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                byte[] interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                stream.Write(interim, 0, interim.Length);
                stream.Flush();
            }

            if (request.Method == "POST" || request.Method == "PUT")
            {
                request.BodyBytes = ReadBody(stream, request);
                if (request.BodyBytes.Length > 0)
                {
                    request.Body = Encoding.UTF8.GetString(request.BodyBytes);
                }
            }

            return request;
        }

        private static byte[] ReadBody(NetworkStream stream, HttpRequest request)
        {
            string transferEncoding = request.Header("Transfer-Encoding");

            if (transferEncoding != null &&
                transferEncoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ReadChunked(stream);
            }

            string contentLength = request.Header("Content-Length");
            if (contentLength == null) return new byte[0];

            int length;
            if (!int.TryParse(contentLength, NumberStyles.Integer, CultureInfo.InvariantCulture, out length)) return new byte[0];
            if (length <= 0) return new byte[0];
            if (length > MaxBody) return new byte[0];

            var buffer = new byte[length];
            int offset = 0;

            while (offset < length)
            {
                int read = stream.Read(buffer, offset, length - offset);
                if (read <= 0) break;
                offset += read;
            }

            return buffer;
        }

        private static byte[] ReadChunked(NetworkStream stream)
        {
            var body = new MemoryStream(1024);

            while (true)
            {
                string sizeLine = ReadLine(stream, 128);
                if (sizeLine == null) break;

                int semicolon = sizeLine.IndexOf(';');
                if (semicolon >= 0) sizeLine = sizeLine.Substring(0, semicolon);

                int size;
                if (!int.TryParse(sizeLine.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out size)) break;
                if (size <= 0) break;
                if (body.Length + size > MaxBody) break;

                var chunk = new byte[size];
                int offset = 0;

                while (offset < size)
                {
                    int read = stream.Read(chunk, offset, size - offset);
                    if (read <= 0) return body.ToArray();
                    offset += read;
                }

                body.Write(chunk, 0, size);
                ReadLine(stream, 8);      // trailing CRLF
            }

            return body.ToArray();
        }

        /// <summary>Reads one CRLF-terminated line as ASCII.</summary>
        private static string ReadLine(NetworkStream stream, int maxLength)
        {
            var bytes = new List<byte>(128);

            while (bytes.Count <= maxLength)
            {
                int value = stream.ReadByte();
                if (value < 0) return null;

                if (value == '\n')
                {
                    int count = bytes.Count;
                    if (count > 0 && bytes[count - 1] == '\r') count--;
                    return Encoding.ASCII.GetString(bytes.GetRange(0, count).ToArray());
                }

                bytes.Add((byte)value);
            }

            return null;
        }

        /// <summary>Normalises a path: collapses slashes, drops dot segments.</summary>
        public static string NormalisePath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath)) return "/";

            string path = rawPath.Replace('\\', '/');

            // Decode percent escapes before normalising so "%2e%2e" cannot sneak past.
            try
            {
                path = Uri.UnescapeDataString(path);
            }
            catch (Exception)
            {
                // Malformed escape: treat it as a bad request.
                return null;
            }

            if (path.IndexOf('\0') >= 0) return null;

            var segments = new List<string>();
            foreach (string segment in path.Split('/'))
            {
                if (segment.Length == 0 || segment == ".") continue;

                if (segment == "..")
                {
                    if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                    continue;
                }

                segments.Add(segment);
            }

            return "/" + string.Join("/", segments.ToArray());
        }

        // -- response ---------------------------------------------------------

        private static void WriteResponse(NetworkStream stream, HttpRequest request, HttpResponse response)
        {
            var head = new StringBuilder(256);
            head.Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(ReasonPhrase(response.Status)).Append("\r\n");
            head.Append("Date: ").Append(DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
            head.Append("Server: DartScape\r\n");
            head.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");

            bool bodyAllowed = request.Method != "HEAD" && response.Status != 204 && response.Status != 304;

            head.Append("Content-Length: ").Append(bodyAllowed ? response.Body.Length : 0).Append("\r\n");

            foreach (KeyValuePair<string, string> header in response.Headers)
            {
                head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }

            head.Append("Connection: ").Append(response.KeepAlive ? "keep-alive" : "close").Append("\r\n");
            head.Append("\r\n");

            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);

            if (bodyAllowed && response.Body.Length > 0)
            {
                stream.Write(response.Body, 0, response.Body.Length);
            }

            stream.Flush();
        }

        private static string ReasonPhrase(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 301: return "Moved Permanently";
                case 302: return "Found";
                case 304: return "Not Modified";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 413: return "Payload Too Large";
                case 429: return "Too Many Requests";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                default: return "OK";
            }
        }

        public void Stop()
        {
            _running = false;

            try { if (_listener != null) _listener.Stop(); } catch (Exception) { }

            Thread thread = _acceptThread;
            if (thread != null) thread.Join(TimeSpan.FromSeconds(2));
        }

        public void Dispose()
        {
            Stop();

            try { if (_listener != null) _listener.Server.Close(); } catch (Exception) { }
            _listener = null;
        }
    }
}
