using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DartScape.Tests
{
    /// <summary>
    /// A tiny test harness.
    /// </summary>
    /// <remarks>
    /// No test framework: the project takes no third-party dependencies, and the built-in
    /// options either need packages (NUnit, xUnit) or only exist in later frameworks
    /// (MSTest v2). A hundred lines of assertion helpers covers everything here, runs on
    /// .NET 4.0, and produces output a human can read in CI.
    /// </remarks>
    public static class Harness
    {
        private sealed class Failure
        {
            public string Test;
            public string Message;
        }

        private static readonly List<Failure> Failures = new List<Failure>();
        private static string _current = "";
        private static int _passed;
        private static int _failed;

        /// <summary>Runs one named test. Any exception is recorded as a failure.</summary>
        public static void Test(string name, Action body)
        {
            _current = name;

            try
            {
                body();
                _passed++;
                Console.WriteLine("  PASS  " + name);
            }
            catch (Exception ex)
            {
                _failed++;
                string message = ex is AssertException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
                Failures.Add(new Failure { Test = name, Message = message });
                Console.WriteLine("  FAIL  " + name);
                Console.WriteLine("        " + message);
            }
        }

        /// <summary>Prints a section heading.</summary>
        public static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("== " + title + " ==");
        }

        /// <summary>Prints a summary and returns the process exit code.</summary>
        public static int Report()
        {
            Console.WriteLine();
            Console.WriteLine(new string('-', 60));
            Console.WriteLine(string.Format("{0} passed, {1} failed", _passed, _failed));

            if (_failed == 0) return 0;

            Console.WriteLine();
            Console.WriteLine("Failures:");

            foreach (Failure failure in Failures)
            {
                Console.WriteLine("  * " + failure.Test);
                Console.WriteLine("    " + failure.Message);
            }

            return 1;
        }

        // -- assertions -------------------------------------------------------

        public static void True(bool condition, string message)
        {
            if (!condition) throw new AssertException(message);
        }

        public static void False(bool condition, string message)
        {
            if (condition) throw new AssertException(message);
        }

        public static void Equal(int expected, int actual, string what)
        {
            if (expected != actual)
            {
                throw new AssertException(string.Format(
                    "{0}: expected {1}, got {2}", what, expected, actual));
            }
        }

        public static void Equal(long expected, long actual, string what)
        {
            if (expected != actual)
            {
                throw new AssertException(string.Format(
                    "{0}: expected {1}, got {2}", what, expected, actual));
            }
        }

        public static void Equal(string expected, string actual, string what)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new AssertException(string.Format(
                    "{0}: expected '{1}', got '{2}'", what, expected, actual));
            }
        }

        public static void Equal(byte[] expected, byte[] actual, string what)
        {
            if (expected == null || actual == null)
            {
                throw new AssertException(what + ": one side was null");
            }

            if (expected.Length != actual.Length)
            {
                throw new AssertException(string.Format(
                    "{0}: expected {1} bytes, got {2}", what, expected.Length, actual.Length));
            }

            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    throw new AssertException(string.Format(
                        "{0}: byte {1} expected 0x{2:X2}, got 0x{3:X2}", what, i, expected[i], actual[i]));
                }
            }
        }

        public static void Near(double expected, double actual, double tolerance, string what)
        {
            if (Math.Abs(expected - actual) > tolerance)
            {
                throw new AssertException(string.Format(
                    "{0}: expected {1} +/- {2}, got {3}", what, expected, tolerance, actual));
            }
        }

        public static void NotNull(object value, string what)
        {
            if (value == null) throw new AssertException(what + " was null");
        }

        public static void Null(object value, string what)
        {
            if (value != null) throw new AssertException(what + " was expected to be null");
        }

        /// <summary>Asserts that <paramref name="action"/> throws.</summary>
        public static void Throws(Action action, string what)
        {
            try
            {
                action();
            }
            catch (Exception)
            {
                return;
            }

            throw new AssertException(what + " did not throw");
        }
    }

    /// <summary>Thrown by an assertion, as opposed to a bug in the code under test.</summary>
    public sealed class AssertException : Exception
    {
        public AssertException(string message) : base(message) { }
    }
}
