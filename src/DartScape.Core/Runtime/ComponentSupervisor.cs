using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using DartScape.Core.Diagnostics;

namespace DartScape.Core.Runtime
{
    /// <summary>What the supervisor decided to do after a component faulted.</summary>
    public enum RestartDecision
    {
        /// <summary>The fault is not survivable in-process; let the supervisor restart the whole thing.</summary>
        GiveUp = 0,
        /// <summary>Restart the failed component immediately.</summary>
        Restart = 1,
        /// <summary>Wait for the back-off delay, then restart.</summary>
        RestartAfterDelay = 2
    }

    /// <summary>
    /// A supervised long-running unit of work. Implementations are expected to be
    /// restartable from scratch: they must not assume any state survives a fault.
    /// </summary>
    public interface ISupervisedComponent
    {
        /// <summary>Short name used in logs and the admin panel.</summary>
        string Name { get; }

        /// <summary>
        /// Runs until <paramref name="stop"/> is signalled or the component throws.
        /// Returning normally counts as a clean shutdown.
        /// </summary>
        void Run(CancellationToken stop);
    }

    /// <summary>
    /// Runs one <see cref="ISupervisedComponent"/> on its own thread and restarts it
    /// with exponential back-off when it throws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the "dynamic fault tolerance" contract for the whole project: a broken
    /// world tick, a dropped listener socket or a crashing relay never takes the
    /// process down. The component is torn down, logged, and started again after a
    /// delay that doubles up to a ceiling. Only after
    /// <see cref="MaxConsecutiveFailures"/> consecutive failures in a row does the
    /// supervisor give up and let the outer watchdog restart the process.
    /// </para>
    /// <para>
    /// A component that stays healthy for <see cref="StablePeriod"/> has its failure
    /// counter reset, so a rare crash once an hour never escalates.
    /// </para>
    /// </remarks>
    public sealed class ComponentSupervisor
    {
        private readonly ISupervisedComponent _component;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly object _sync = new object();

        private Thread _thread;
        private int _failures;
        private int _restarts;
        private int _totalFailures;
        private DateTime _lastFaultUtc = DateTime.MinValue;
        private string _lastError;
        private volatile bool _running;

        /// <summary>Delay before the first restart. Doubles on each consecutive failure.</summary>
        public TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);

        /// <summary>Ceiling for the back-off delay.</summary>
        public TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

        /// <summary>How long a component must run cleanly before its failure count resets.</summary>
        public TimeSpan StablePeriod = TimeSpan.FromSeconds(60);

        /// <summary>Consecutive failures tolerated before the supervisor gives up.</summary>
        public int MaxConsecutiveFailures = 10;

        public ComponentSupervisor(ISupervisedComponent component)
        {
            if (component == null) throw new ArgumentNullException("component");
            _component = component;
        }

        /// <summary>Component name, for logging.</summary>
        public string Name
        {
            get { return _component.Name; }
        }

        /// <summary>True while the supervised thread is alive.</summary>
        public bool IsRunning
        {
            get { return _running; }
        }

        /// <summary>Total number of automatic restarts performed.</summary>
        public int Restarts
        {
            get { lock (_sync) return _restarts; }
        }

        /// <summary>Total number of faults seen, including ones that did not restart.</summary>
        public int TotalFailures
        {
            get { lock (_sync) return _totalFailures; }
        }

        /// <summary>Message of the most recent fault.</summary>
        public string LastError
        {
            get { lock (_sync) return _lastError; }
        }

        /// <summary>UTC timestamp of the most recent fault.</summary>
        public DateTime LastFaultUtc
        {
            get { lock (_sync) return _lastFaultUtc; }
        }

        /// <summary>Starts the supervised thread. Safe to call once.</summary>
        public void Start()
        {
            lock (_sync)
            {
                if (_thread != null) return;
                _thread = new Thread(Loop)
                {
                    IsBackground = true,
                    Name = "DartScape." + _component.Name
                };
                _thread.Start();
            }
        }

        /// <summary>Requests a clean stop and waits up to <paramref name="timeout"/>.</summary>
        public bool Stop(TimeSpan timeout)
        {
            try { _stop.Cancel(); }
            catch (ObjectDisposedException) { }

            Thread t;
            lock (_sync) { t = _thread; }
            if (t == null) return true;

            bool joined = t.Join(timeout);
            if (!joined)
            {
                Log.Warning("Supervisor", "{0} did not stop within {1:0.0}s; abandoning thread.", Name, timeout.TotalSeconds);
            }
            return joined;
        }

        private void Loop()
        {
            _running = true;
            TimeSpan delay = InitialBackoff;

            while (!_stop.IsCancellationRequested)
            {
                DateTime startedUtc = DateTime.UtcNow;
                try
                {
                    Log.Info("Supervisor", "{0} starting.", _component.Name);
                    _component.Run(_stop.Token);

                    if (_stop.IsCancellationRequested)
                    {
                        Log.Info("Supervisor", "{0} stopped cleanly.", _component.Name);
                        break;
                    }

                    // Returned without being asked to: treat as a fault, but a mild one.
                    Fault("returned unexpectedly");
                }
                catch (OperationCanceledException)
                {
                    Log.Info("Supervisor", "{0} cancelled.", _component.Name);
                    break;
                }
                catch (Exception ex)
                {
                    Fault(ex.ToString());
                }

                if (_stop.IsCancellationRequested) break;

                TimeSpan alive = DateTime.UtcNow - startedUtc;
                int failures;
                lock (_sync)
                {
                    _totalFailures++;
                    if (alive >= StablePeriod)
                    {
                        // It ran fine for a long while: this is a fresh incident.
                        _failures = 0;
                        delay = InitialBackoff;
                    }
                    _failures++;
                    failures = _failures;
                }

                if (failures > MaxConsecutiveFailures)
                {
                    Log.Critical("Supervisor",
                        "{0} failed {1} times in a row (last uptime {2:0.0}s). Giving up; the outer watchdog should restart the process.",
                        _component.Name, failures, alive.TotalSeconds);
                    break;
                }

                Log.Warning("Supervisor", "{0} restarting in {1:0.0}s (attempt {2}/{3}).",
                    _component.Name, delay.TotalSeconds, failures, MaxConsecutiveFailures);

                // Sleep in slices so a shutdown request is honoured quickly.
                DateTime wakeUtc = DateTime.UtcNow + delay;
                while (DateTime.UtcNow < wakeUtc && !_stop.IsCancellationRequested)
                {
                    Thread.Sleep(50);
                }

                lock (_sync)
                {
                    _restarts++;
                }

                // Exponential back-off, capped.
                double next = Math.Min(delay.TotalMilliseconds * 2, MaxBackoff.TotalMilliseconds);
                delay = TimeSpan.FromMilliseconds(next);
            }

            _running = false;
            Log.Info("Supervisor", "{0} supervisor exiting.", _component.Name);
        }

        private void Fault(string detail)
        {
            lock (_sync)
            {
                _lastError = detail;
                _lastFaultUtc = DateTime.UtcNow;
            }
            Log.Error("Supervisor", "{0} faulted: {1}", _component.Name, detail);
        }
    }

    /// <summary>
    /// Outer watchdog: launches the real work in a child process and relaunches it if
    /// it dies, with rate limiting so a permanently broken build cannot spin the CPU
    /// forever. This is what "dynamic fault tolerance" means at the process level.
    /// </summary>
    public static class ProcessWatchdog
    {
        /// <summary>
        /// Supervises <paramref name="exePath"/> until <paramref name="stopAfter"/> of
        /// clean uptime or until it crashes <paramref name="maxRapidCrashes"/> times in
        /// a row. Returns the last exit code.
        /// </summary>
        public static int Run(string exePath, string arguments, TimeSpan minimumUptime, int maxRapidCrashes)
        {
            if (string.IsNullOrEmpty(exePath)) throw new ArgumentNullException("exePath");

            int rapidCrashes = 0;

            while (true)
            {
                DateTime startedUtc = DateTime.UtcNow;
                int exitCode;

                try
                {
                    var psi = new ProcessStartInfo(exePath, arguments ?? string.Empty)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = System.IO.Path.GetDirectoryName(exePath) ?? "."
                    };

                    using (Process p = Process.Start(psi))
                    {
                        if (p == null)
                        {
                            Log.Critical("Watchdog", "Could not start {0}.", exePath);
                            return -1;
                        }

                        Log.Info("Watchdog", "Started {0} (pid {1}).", exePath, p.Id);
                        p.WaitForExit();
                        exitCode = p.ExitCode;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Watchdog", "Failed to run {0}: {1}", exePath, ex.Message);
                    return -1;
                }

                TimeSpan uptime = DateTime.UtcNow - startedUtc;
                Log.Warning("Watchdog", "{0} exited with code {1} after {2:0.0}s.",
                    System.IO.Path.GetFileName(exePath), exitCode, uptime.TotalSeconds);

                // A clean exit code 0 after a decent run means "the operator stopped it".
                if (exitCode == 0 && uptime >= minimumUptime)
                {
                    Log.Info("Watchdog", "Clean shutdown; watchdog stopping.");
                    return 0;
                }

                if (uptime >= minimumUptime)
                {
                    rapidCrashes = 0;
                }
                else
                {
                    rapidCrashes++;
                }

                if (rapidCrashes > maxRapidCrashes)
                {
                    Log.Critical("Watchdog",
                        "{0} crashed {1} times in under {2:0}s each time. Not restarting.",
                        System.IO.Path.GetFileName(exePath), rapidCrashes, minimumUptime.TotalSeconds);
                    return exitCode;
                }

                int backoffSeconds = Math.Min(30, 2 * Math.Max(1, rapidCrashes));
                Log.Info("Watchdog", "Restarting in {0}s.", backoffSeconds);

                for (int i = 0; i < backoffSeconds * 10; i++)
                {
                    Thread.Sleep(100);
                }
            }
        }

        /// <summary>Formats a duration for human consumption in logs.</summary>
        public static string Describe(TimeSpan span)
        {
            if (span.TotalDays >= 1) return span.TotalDays.ToString("0.0", CultureInfo.InvariantCulture) + "d";
            if (span.TotalHours >= 1) return span.TotalHours.ToString("0.0", CultureInfo.InvariantCulture) + "h";
            if (span.TotalMinutes >= 1) return span.TotalMinutes.ToString("0.0", CultureInfo.InvariantCulture) + "m";
            return span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }
    }
}
