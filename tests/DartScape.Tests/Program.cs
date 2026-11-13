using System;
using System.Diagnostics;
using System.IO;
using DartScape.Core.Diagnostics;

namespace DartScape.Tests
{
    /// <summary>
    /// Test runner entry point.
    /// </summary>
    /// <remarks>
    /// Exits non-zero when any test fails, so CI can rely on the process exit code alone.
    /// Pass <c>--cache PATH</c> to additionally run the cache integration tests against a
    /// real cache directory; without it those tests are skipped and reported as such.
    /// </remarks>
    public static class Program
    {
        public static int Main(string[] args)
        {
            string cachePath = null;
            string serverAddress = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--cache", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    cachePath = args[i + 1];
                    i++;
                }
                else if (string.Equals(args[i], "--live", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    serverAddress = args[i + 1];
                    i++;
                }
            }

            Console.WriteLine("DartScape test runner");
            Console.WriteLine("CLR " + Environment.Version + " on " + Environment.OSVersion.VersionString);
            Console.WriteLine();

            var stopwatch = Stopwatch.StartNew();

            // Logging is used by several tests, so give it a quiet sink first.
            Log.Configure("tests", new LogOptions
            {
                EchoToConsole = false,
                MinimumLevel = LogLevel.Warning,
                Directory = Path.Combine(Path.GetTempPath(), "dartscape-test-logs")
            });

            try
            {
                CoreTests.Run();
                GameTests.Run();
                CollisionTests.Run();
        LocalMovementTests.Run();
        CacheTests.Run();
                InfrastructureTests.Run();

                if (cachePath != null)
                {
                    CacheIntegrationTests.Run(cachePath);
                }
                else
                {
                    Harness.Section("Cache integration");
                    Console.WriteLine("  SKIP  no --cache path given");
                }

                if (serverAddress != null)
                {
                    LiveServerTests.Run(serverAddress);
                    ClientConnectionTests.Run(serverAddress);
                }
                else
                {
                    Harness.Section("Live server");
                    Console.WriteLine("  SKIP  no --live address given");
                }
            }
            finally
            {
                stopwatch.Stop();
            }

            Console.WriteLine();
            Console.WriteLine("Finished in " + stopwatch.ElapsedMilliseconds + " ms.");

            int exitCode = Harness.Report();
            Log.Shutdown();
            return exitCode;
        }
    }
}
