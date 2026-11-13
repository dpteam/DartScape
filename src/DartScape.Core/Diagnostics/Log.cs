using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace DartScape.Core.Diagnostics
{
    /// <summary>Severity of a log record.</summary>
    public enum LogLevel
    {
        Debug = 0,
        Information = 1,
        Warning = 2,
        Error = 3,
        Critical = 4
    }

    /// <summary>Options for <see cref="Log.Configure(string,LogOptions)"/>.</summary>
    public sealed class LogOptions
    {
        /// <summary>Directory that receives the log files. Defaults to <c>&lt;exe&gt;\logs</c>.</summary>
        public string Directory;

        /// <summary>Minimum level actually written. Defaults to <see cref="LogLevel.Information"/>.</summary>
        public LogLevel MinimumLevel = LogLevel.Information;

        /// <summary>Mirror records to the console (useful for the console-hosted server).</summary>
        public bool EchoToConsole = true;

        /// <summary>Size at which a new timestamped file is started. Zero disables it.</summary>
        public long MaxBytesPerFile = 8L * 1024 * 1024;

        /// <summary>Number of recent records kept in memory for the admin panel.</summary>
        public int RecentCapacity = 500;
    }

    /// <summary>One record kept in the in-memory tail buffer.</summary>
    public sealed class LogRecord
    {
        public DateTime Timestamp;
        public LogLevel Level;
        public string Category;
        public string Message;
        public string Exception;

        public override string ToString()
        {
            var sb = new StringBuilder(160);
            sb.Append(Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
            sb.Append(' ').Append(Log.LevelTag(Level));
            sb.Append(" [").Append(Category).Append("] ").Append(Message);
            if (!string.IsNullOrEmpty(Exception)) sb.Append(" | ").Append(Exception);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Process-wide logging front end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Records travel through <see cref="Trace"/> вЂ” the same channel the rest of the
    /// .NET Framework uses вЂ” so any listener added by a host (debugger, console,
    /// custom) sees them too. Registration happens in code, never through App.config.
    /// </para>
    /// <para>
    /// A bounded ring buffer of recent records is retained so the admin web panel can
    /// show a live tail without reading files off disk.
    /// </para>
    /// </remarks>
    public static class Log
    {
        private static readonly object Sync = new object();
        private static readonly Queue<LogRecord> Recent = new Queue<LogRecord>(512);

        private static FileTraceListener _file;
        private static TextWriterTraceListener _console;
        private static LogOptions _options = new LogOptions();
        private static string _role = "dartscape";
        private static int _initialised;
        private static long _errorCount;

        /// <summary>True once <see cref="Configure(string,LogOptions)"/> has run.</summary>
        public static bool IsConfigured
        {
            get { return _initialised != 0; }
        }

        /// <summary>Role name used as the log file prefix (server, client, web, ...).</summary>
        public static string Role
        {
            get { return _role; }
        }

        /// <summary>Full path of the active log file, or <c>null</c> before configuration.</summary>
        public static string CurrentLogFile
        {
            get
            {
                FileTraceListener f = _file;
                return f == null ? null : f.CurrentPath;
            }
        }

        /// <summary>Total number of error and critical records written since start.</summary>
        public static long ErrorCount
        {
            get { return Interlocked.Read(ref _errorCount); }
        }

        /// <summary>
        /// Installs the file listener (and optionally the console listener) and hooks
        /// the last-chance exception handlers. Safe to call more than once; later calls
        /// only raise the level / change nothing else.
        /// </summary>
        public static FileTraceListener Configure(string role, LogOptions options)
        {
            if (string.IsNullOrEmpty(role)) role = "dartscape";
            if (options == null) options = new LogOptions();

            lock (Sync)
            {
                if (_initialised != 0)
                {
                    _options = options;
                    if (_file != null) _file.Flush();
                    return _file;
                }

                _role = role;
                _options = options;

                if (string.IsNullOrEmpty(options.Directory))
                {
                    options.Directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                }

                Trace.AutoFlush = false;
                Trace.IndentSize = 2;

                _file = new FileTraceListener(role, options.Directory, options.MaxBytesPerFile);
                Trace.Listeners.Add(_file);

                if (options.EchoToConsole)
                {
                    _console = new TextWriterTraceListener(Console.Out, "DartScapeConsole") { TraceOutputOptions = TraceOptions.None };
                    Trace.Listeners.Add(_console);
                }

                if (options.RecentCapacity > 0)
                {
                    Recent.Clear();
                }

                _initialised = 1;

                // Banner: the first thing anyone should see in a bug report.
                Info("Log", "DartScape {0} starting | pid={1} | {2} | CLR {3}",
                    role,
                    Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture),
                    Environment.OSVersion.VersionString,
                    Environment.Version);
                Info("Log", "Log file: {0}", _file.CurrentPath);

                return _file;
            }
        }

        /// <summary>
        /// Registers handlers for unhandled exceptions on every thread plus
        /// non-fatal <see cref="AppDomain"/> failures, so a crash always leaves a trace
        /// on disk instead of vanishing behind a Windows error dialog.
        /// </summary>
        public static void InstallCrashHandlers()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Critical("Crash", "Unhandled exception (terminating={0})", e.IsTerminating);
                WriteException(LogLevel.Critical, "Crash", e.ExceptionObject as Exception);
                Flush();
            };

            AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
            {
                // Debug level only: first-chance fires for every handled exception too.
                if (_options != null && _options.MinimumLevel <= LogLevel.Debug)
                {
                    Debug("Crash", "First-chance: {0}: {1}", e.Exception.GetType().Name, e.Exception.Message);
                }
            };

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Warning("Crash", "Unobserved task exception: {0}", e.Exception.GetBaseException().Message);
                e.SetObserved();
            };

            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                Info("Log", "Process exit");
                Flush();
            };
        }

        /// <summary>Flushes every listener; call before a controlled shutdown.</summary>
        public static void Flush()
        {
            try
            {
                lock (Sync)
                {
                    if (_file != null) _file.Flush();
                }
                Trace.Flush();
            }
            catch (Exception)
            {
                // Flush must never throw вЂ” we may already be unwinding a crash.
            }
        }

        /// <summary>Flushes and removes the listeners.</summary>
        public static void Shutdown()
        {
            lock (Sync)
            {
                if (_initialised == 0) return;

                Info("Log", "Logging shutting down ({0} errors recorded)", Interlocked.Read(ref _errorCount));
                Flush();

                if (_file != null)
                {
                    Trace.Listeners.Remove(_file);
                    _file.Dispose();
                    _file = null;
                }
                if (_console != null)
                {
                    Trace.Listeners.Remove(_console);
                    _console.Dispose();
                    _console = null;
                }

                _initialised = 0;
            }
        }

        /// <summary>Snapshot of the most recent records, oldest first.</summary>
        public static LogRecord[] GetRecent(int max = 200)
        {
            if (max <= 0) return new LogRecord[0];
            lock (Sync)
            {
                if (Recent.Count == 0) return new LogRecord[0];
                int take = Math.Min(max, Recent.Count);
                var all = Recent.ToArray();
                if (take == all.Length) return all;
                var slice = new LogRecord[take];
                Array.Copy(all, all.Length - take, slice, 0, take);
                return slice;
            }
        }

        /// <summary>Text form of <see cref="GetRecent"/> for the admin panel.</summary>
        public static string[] GetRecentLines(int max = 200)
        {
            LogRecord[] records = GetRecent(max);
            var lines = new string[records.Length];
            for (int i = 0; i < records.Length; i++) lines[i] = records[i].ToString();
            return lines;
        }

        /// <summary>Maps a level to its four/five character tag used in the log text.</summary>
        public static string LevelTag(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return "DBG ";
                case LogLevel.Information: return "INFO";
                case LogLevel.Warning: return "WARN";
                case LogLevel.Error: return "ERR ";
                case LogLevel.Critical: return "CRIT";
                default: return "????";
            }
        }

        // -- level helpers ----------------------------------------------------

        public static void Debug(string category, string format, params object[] args)
        {
            Write(LogLevel.Debug, category, format, args);
        }

        public static void Info(string category, string format, params object[] args)
        {
            Write(LogLevel.Information, category, format, args);
        }

        public static void Warning(string category, string format, params object[] args)
        {
            Write(LogLevel.Warning, category, format, args);
        }

        public static void Error(string category, string format, params object[] args)
        {
            Write(LogLevel.Error, category, format, args);
        }

        public static void Critical(string category, string format, params object[] args)
        {
            Write(LogLevel.Critical, category, format, args);
        }

        /// <summary>Logs an exception with its type, message and stack trace.</summary>
        public static void Exception(string category, Exception ex, string context = null)
        {
            if (ex == null) return;
            string message = string.IsNullOrEmpty(context) ? ex.Message : context + ": " + ex.Message;
            Write(LogLevel.Error, category, "{0} ({1})", message, ex.GetType().FullName);
            WriteException(LogLevel.Error, category, ex);
        }

        private static void WriteException(LogLevel level, string category, Exception ex)
        {
            if (ex == null) return;

            var sb = new StringBuilder(1024);
            int depth = 0;
            while (ex != null && depth < 8)
            {
                sb.Append(depth == 0 ? "  " : "  ---> ");
                sb.Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
                if (!string.IsNullOrEmpty(ex.StackTrace)) sb.Append(Environment.NewLine).Append(ex.StackTrace);
                ex = ex.InnerException;
                depth++;
            }
            Write(level, category, sb.ToString());
        }

        private static void Write(LogLevel level, string category, string format, params object[] args)
        {
            LogOptions options = _options;
            if (options != null && level < options.MinimumLevel) return;

            string message;
            if (args == null || args.Length == 0)
            {
                message = format ?? string.Empty;
            }
            else
            {
                try
                {
                    message = string.Format(CultureInfo.InvariantCulture, format, args);
                }
                catch (FormatException)
                {
                    // A malformed format string must not take the process down.
                    message = format + " " + string.Join(", ", Array.ConvertAll(args, a => a == null ? "<null>" : a.ToString()));
                }
            }

            if (level >= LogLevel.Error) Interlocked.Increment(ref _errorCount);

            var record = new LogRecord
            {
                Timestamp = DateTime.Now,
                Level = level,
                Category = string.IsNullOrEmpty(category) ? _role : category,
                Message = message
            };

            if (options != null && options.RecentCapacity > 0)
            {
                lock (Sync)
                {
                    Recent.Enqueue(record);
                    while (Recent.Count > options.RecentCapacity) Recent.Dequeue();
                }
            }

            if (!IsConfigured) return;

            string line = string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} [{2}] {3}",
                record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                LevelTag(level),
                record.Category,
                record.Message);

            try
            {
                Trace.WriteLine(line);
            }
            catch (Exception)
            {
                // Never let logging break the caller.
            }
        }
    }
}
