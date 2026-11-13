using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using DartScape.Core.Diagnostics;

namespace DartScape.Web.Http
{
    /// <summary>
    /// Serves the static files that make up a web interface.
    /// </summary>
    /// <remarks>
    /// Everything is read from disk rather than embedded as resources, so a deployment can
    /// be re-themed without a rebuild. The MIME table is an explicit whitelist: anything
    /// not listed is served as <c>application/octet-stream</c>, which is safe and makes an
    /// accidental <c>.config</c> download harmless.
    /// </remarks>
    public sealed class StaticFiles
    {
        private static readonly Dictionary<string, string> MimeTypes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { ".html", "text/html; charset=utf-8" },
                { ".htm", "text/html; charset=utf-8" },
                { ".css", "text/css; charset=utf-8" },
                { ".js", "application/javascript; charset=utf-8" },
                { ".json", "application/json; charset=utf-8" },
                { ".svg", "image/svg+xml" },
                { ".png", "image/png" },
                { ".gif", "image/gif" },
                { ".jpg", "image/jpeg" },
                { ".jpeg", "image/jpeg" },
                { ".ico", "image/x-icon" },
                { ".woff", "font/woff" },
                { ".woff2", "font/woff2" },
                { ".ttf", "font/ttf" },
                { ".txt", "text/plain; charset=utf-8" },
                { ".webmanifest", "application/manifest+json" }
            };

        private readonly string _root;
        private readonly string _rootFull;
        private readonly string _notFoundPage;

        public StaticFiles(string root, string notFoundPage = "404.html")
        {
            if (string.IsNullOrEmpty(root)) throw new ArgumentNullException("root");

            _root = root;
            _rootFull = Path.GetFullPath(root);

            if (!_rootFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                _rootFull += Path.DirectorySeparatorChar;
            }

            _notFoundPage = notFoundPage;
        }

        /// <summary>Directory being served.</summary>
        public string Root
        {
            get { return _rootFull; }
        }

        /// <summary>
        /// Serves <paramref name="path"/>. Returns <c>false</c> when the file does not
        /// exist, so the caller can fall through to an API handler.
        /// </summary>
        public bool TryServe(HttpRequest request, HttpResponse response, string path)
        {
            if (request == null || response == null) return false;
            if (request.Method != "GET" && request.Method != "HEAD") return false;

            string file = ResolveSafe(path);
            if (file == null) return false;

            if (Directory.Exists(file))
            {
                file = Path.Combine(file, "index.html");
            }

            if (!File.Exists(file)) return false;

            try
            {
                byte[] bytes = File.ReadAllBytes(file);
                DateTime modified = File.GetLastWriteTimeUtc(file);

                response.Status = 200;
                response.ContentType = MimeFor(file);
                response.Body = bytes;

                // Assets are cacheable; HTML shells never are, so a deploy can never
                // leave a stale page behind.
                if (path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
                {
                    response.SetHeader("Cache-Control", "public, max-age=300");
                }
                else
                {
                    response.SetHeader("Cache-Control", "no-cache, must-revalidate");
                }

                response.SetHeader("Last-Modified", modified.ToString("R", CultureInfo.InvariantCulture));

                // Conditional GET: answer 304 when the client already has this version.
                string since = request.Header("If-Modified-Since");
                if (since != null)
                {
                    DateTime sinceUtc;
                    if (DateTime.TryParse(since, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out sinceUtc))
                    {
                        if (modified <= sinceUtc.AddSeconds(1))
                        {
                            response.Status = 304;
                            response.Body = new byte[0];
                        }
                    }
                }

                return true;
            }
            catch (IOException ex)
            {
                Log.Warning("Web", "Could not read {0}: {1}", file, ex.Message);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Warning("Web", "Access denied reading {0}: {1}", file, ex.Message);
                return false;
            }
        }

        /// <summary>Serves the not-found page, if one exists.</summary>
        public bool TryServeNotFound(HttpResponse response)
        {
            if (string.IsNullOrEmpty(_notFoundPage)) return false;

            string file = ResolveSafe("/" + _notFoundPage);
            if (file == null || !File.Exists(file)) return false;

            try
            {
                response.Status = 404;
                response.ContentType = "text/html; charset=utf-8";
                response.Body = File.ReadAllBytes(file);
                response.SetHeader("Cache-Control", "no-cache");
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Maps a URL path to a file inside the root, refusing anything that escapes it.
        /// </summary>
        /// <remarks>
        /// The path has already been normalised by the request parser, but the check is
        /// repeated here on the resolved absolute path. Two independent checks cost nothing
        /// and mean a future change to the parser cannot turn into a directory traversal.
        /// </remarks>
        private string ResolveSafe(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            if (relative.IndexOf("..", StringComparison.Ordinal) >= 0) return null;

            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(_rootFull, relative));
            }
            catch (Exception)
            {
                return null;
            }

            if (!full.StartsWith(_rootFull, StringComparison.OrdinalIgnoreCase)) return null;

            return full;
        }

        private static string MimeFor(string file)
        {
            string extension = Path.GetExtension(file);
            string mime;
            return MimeTypes.TryGetValue(extension ?? string.Empty, out mime)
                ? mime
                : "application/octet-stream";
        }
    }
}
