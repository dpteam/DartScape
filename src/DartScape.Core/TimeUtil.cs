using System;

namespace DartScape.Core
{
    /// <summary>
    /// Small time helpers, because .NET Framework 4.0 predates
    /// <c>DateTimeOffset.ToUnixTimeSeconds()</c> and every service needs the same
    /// conversion.
    /// </summary>
    public static class TimeUtil
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Current time as Unix seconds.</summary>
        public static long UnixNow()
        {
            return (long)(DateTime.UtcNow - Epoch).TotalSeconds;
        }

        /// <summary>Converts a UTC <see cref="DateTime"/> to Unix seconds.</summary>
        public static long ToUnix(DateTime utc)
        {
            return (long)(utc.ToUniversalTime() - Epoch).TotalSeconds;
        }

        /// <summary>Converts Unix seconds back to a UTC <see cref="DateTime"/>.</summary>
        public static DateTime FromUnix(long seconds)
        {
            return Epoch.AddSeconds(seconds);
        }

        /// <summary>Current time as Unix milliseconds, for lease and cooldown maths.</summary>
        public static long UnixNowMs()
        {
            return (long)(DateTime.UtcNow - Epoch).TotalMilliseconds;
        }

        /// <summary>Monotonic milliseconds since process start; immune to clock changes.</summary>
        public static long UptimeMs()
        {
            return Environment.TickCount & 0x7FFFFFFFL;
        }

        /// <summary>Formats a duration as "3d 4h 12m".</summary>
        public static string Describe(TimeSpan span)
        {
            if (span.TotalDays >= 1)
            {
                return string.Format("{0}d {1}h {2}m", (int)span.TotalDays, span.Hours, span.Minutes);
            }
            if (span.TotalHours >= 1)
            {
                return string.Format("{0}h {1}m", (int)span.TotalHours, span.Minutes);
            }
            if (span.TotalMinutes >= 1)
            {
                return string.Format("{0}m {1}s", (int)span.TotalMinutes, span.Seconds);
            }
            return string.Format("{0}s", (int)span.TotalSeconds);
        }
    }
}
