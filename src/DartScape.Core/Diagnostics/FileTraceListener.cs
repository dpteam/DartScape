using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace DartScape.Core.Diagnostics
{
    /// <summary>
    /// A <see cref="TraceListener"/> that appends to <c>logs\&lt;name&gt;_&lt;yyyyMMdd_HHmmss&gt;.log</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything is configured from code вЂ” there is deliberately no App.config or
    /// app.exe.config requirement, because on a Windows XP box the config file is the
    /// first thing to get lost or hand-edited into an unparseable state.
    /// </para>
    /// <para>
    /// Writing is done by a single background thread fed from a bounded queue. Game
    /// threads never block on disk I/O: if the disk stalls, entries are dropped and a
    /// counter records how many, which is far better than stalling the tick loop.
    /// The queue is drained on <see cref="Flush"/> and in <see cref="Dispose"/>.
    /// </para>
    /// </remarks>
    public sealed class FileTraceListener : TraceListener
    {
        private readonly BlockingCollection<string> _queue;
        private readonly Thread _writer;
        private readonly string _directory;
        private readonly string _baseName;
        private readonly long _maxBytesPerFile;
        private readonly object _rotateLock = new object();

        private volatile string _currentPath;
        private FileStream _stream;
        private StreamWriter _writerStream;
        private long _written;
        private int _dropped;
        private int _rotations;
        private volatile bool _shuttingDown;

        /// <summary>
        /// Creates a listener writing into <paramref name="directory"/>.
        /// </summary>
        /// <param name="name">
        /// Logical name of the log; becomes the file name prefix. Typically the
        /// application role, e.g. <c>server</c>, <c>client</c>, <c>web</c>, <c>relay</c>.
        /// </param>
        /// <param name="directory">Target directory. Created when missing.</param>
        /// <param name="maxBytesPerFile">
        /// Roll over to a new timestamped file once the current one exceeds this size.
        /// Zero disables size based rotation (a day still produces a new file because
        /// the timestamp is part of the name).
        /// </param>
        public FileTraceListener(string name, string directory, long maxBytesPerFile = 8L * 1024 * 1024)
            : base("DartScapeFile")
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException("name");
            if (string.IsNullOrEmpty(directory)) throw new ArgumentNullException("directory");

            _baseName = SanitiseFileName(name);
            _directory = Path.GetFullPath(directory);
            _maxBytesPerFile = maxBytesPerFile < 0 ? 0 : maxBytesPerFile;

            Directory.CreateDirectory(_directory);
            _queue = new BlockingCollection<string>(new ConcurrentQueue<string>(), 64 * 1024);

            _writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "DartScape.LogWriter"
            };
            _writer.Start();

            OpenNewFile();
        }

        /// <summary>Full path of the file currently being appended to.</summary>
        public string CurrentPath
        {
            get { return _currentPath; }
        }

        /// <summary>Number of entries discarded because the queue was saturated.</summary>
        public int DroppedEntries
        {
            get { return _dropped; }
        }

        /// <summary>Number of times the log rolled over to a fresh file.</summary>
        public int Rotations
        {
            get { return _rotations; }
        }

        public override void Write(string message)
        {
            if (message == null) return;
            Enqueue(message);
        }

        public override void WriteLine(string message)
        {
            Enqueue(message ?? string.Empty);
        }

        /// <summary>
        /// Queues one already-formatted line. Kept separate from
        /// <see cref="TraceListener.WriteLine(string)"/> so that callers that build a
        /// whole record (timestamp, level, thread, category) can bypass the
        /// <see cref="Trace"/> indirection entirely.
        /// </summary>
        public void Enqueue(string line)
        {
            if (_shuttingDown) return;

            // Non-blocking: never let logging stall a game tick.
            if (!_queue.TryAdd(line))
            {
                Interlocked.Increment(ref _dropped);
            }
        }

        public override void Flush()
        {
            // Drain whatever is queued, then push the StreamWriter buffer to disk.
            SpinWaitUntilEmpty(TimeSpan.FromSeconds(2));
            lock (_rotateLock)
            {
                if (_writerStream != null)
                {
                    try { _writerStream.Flush(); }
                    catch (IOException) { /* disk gone; nothing sensible to do */ }
                    catch (ObjectDisposedException) { }
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_shuttingDown)
            {
                _shuttingDown = true;
                try { _queue.CompleteAdding(); } catch (ObjectDisposedException) { }

                // Give the writer a moment to finish, but never hang shutdown forever.
                if (_writer != null && !_writer.Join(TimeSpan.FromSeconds(5)))
                {
                    try { _writer.Abort(); } catch (Exception) { }
                }

                lock (_rotateLock)
                {
                    CloseStream();
                }

                _queue.Dispose();
            }

            base.Dispose(disposing);
        }

        private void SpinWaitUntilEmpty(TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (_queue.Count > 0 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(2);
            }
        }

        private void WriterLoop()
        {
            try
            {
                foreach (string line in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        lock (_rotateLock)
                        {
                            if (_writerStream == null) OpenNewFile();

                            if (_maxBytesPerFile > 0 && _written >= _maxBytesPerFile)
                            {
                                RotateLocked();
                            }

                            _writerStream.WriteLine(line);
                            _written += line.Length + Environment.NewLine.Length;
                        }
                    }
                    catch (IOException)
                    {
                        // Transient disk problem: drop the line, keep the process alive.
                        Interlocked.Increment(ref _dropped);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        Interlocked.Increment(ref _dropped);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Queue was disposed during shutdown.
            }
            catch (InvalidOperationException)
            {
                // CompleteAdding raced with Dispose.
            }
            finally
            {
                lock (_rotateLock)
                {
                    CloseStream();
                }
            }
        }

        private void RotateLocked()
        {
            CloseStream();
            Interlocked.Increment(ref _rotations);
            OpenNewFile();
        }

        private void OpenNewFile()
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(_directory, string.Format(CultureInfo.InvariantCulture, "{0}_{1}.log", _baseName, stamp));

            // Two rotations inside the same second would collide.
            int suffix = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(_directory, string.Format(
                    CultureInfo.InvariantCulture, "{0}_{1}_{2}.log", _baseName, stamp, suffix++));
            }

            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite, 32 * 1024, FileOptions.None);
            _writerStream = new StreamWriter(_stream, new UTF8Encoding(false));
            _writerStream.AutoFlush = false;
            _written = 0;
            _currentPath = path;

            _writerStream.WriteLine("# DartScape log started " +
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                "  pid=" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) +
                "  os=" + Environment.OSVersion.VersionString +
                "  clr=" + Environment.Version);
            _writerStream.Flush();
        }

        private void CloseStream()
        {
            if (_writerStream != null)
            {
                try { _writerStream.Flush(); } catch (Exception) { }
                try { _writerStream.Dispose(); } catch (Exception) { }
                _writerStream = null;
            }
            if (_stream != null)
            {
                try { _stream.Dispose(); } catch (Exception) { }
                _stream = null;
            }
        }

        private static string SanitiseFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }
            return sb.ToString().Trim();
        }
    }
}
