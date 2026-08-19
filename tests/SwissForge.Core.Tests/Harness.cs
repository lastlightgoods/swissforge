using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace SwissForge.Tests
{
    /// <summary>
    /// A dependency-free test harness.
    /// <para>
    /// SwissForge ships with no NuGet references anywhere in the tree — including tests —
    /// so the whole solution builds and verifies on a machine with no package feed, which
    /// is the normal state of a locked-down shop-floor PC.
    /// </para>
    /// </summary>
    public static class Check
    {
        private static readonly List<string> Failures = new List<string>();
        private static int _passed;
        private static string _suite = "";

        public static void Suite(string name)
        {
            _suite = name;
            Console.WriteLine();
            Console.WriteLine("== " + name + " " + new string('=', Math.Max(0, 62 - name.Length)));
        }

        private static void Pass(string what)
        {
            _passed++;
            Console.WriteLine("  ok   " + what);
        }

        private static void Fail(string what, string detail)
        {
            Failures.Add(_suite + " / " + what + ": " + detail);
            Console.WriteLine("  FAIL " + what);
            Console.WriteLine("       " + detail);
        }

        public static void True(bool condition, string what)
        {
            if (condition) Pass(what); else Fail(what, "expected true");
        }

        public static void False(bool condition, string what)
        {
            if (!condition) Pass(what); else Fail(what, "expected false");
        }

        public static void Equal(object expected, object actual, string what)
        {
            if (Equals(expected, actual)) Pass(what);
            else Fail(what, $"expected <{expected}> but got <{actual}>");
        }

        public static void Near(double expected, double actual, double tolerance, string what)
        {
            if (Math.Abs(expected - actual) <= tolerance) Pass(what);
            else Fail(what, $"expected {expected.ToString("G6", CultureInfo.InvariantCulture)} " +
                            $"+/- {tolerance.ToString("G4", CultureInfo.InvariantCulture)} but got " +
                            $"{actual.ToString("G6", CultureInfo.InvariantCulture)}");
        }

        public static void Greater(double actual, double bound, string what)
        {
            if (actual > bound) Pass(what);
            else Fail(what, $"expected > {bound} but got {actual}");
        }

        public static void Less(double actual, double bound, string what)
        {
            if (actual < bound) Pass(what);
            else Fail(what, $"expected < {bound} but got {actual}");
        }

        public static void Between(double actual, double low, double high, string what)
        {
            if (actual >= low && actual <= high) Pass(what);
            else Fail(what, $"expected between {low} and {high} but got {actual}");
        }

        public static void Contains(string haystack, string needle, string what)
        {
            if (haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) Pass(what);
            else Fail(what, $"expected to find \"{needle}\" in \"{Truncate(haystack)}\"");
        }

        public static void Throws<TException>(Action action, string what) where TException : Exception
        {
            try
            {
                action();
                Fail(what, "expected " + typeof(TException).Name + " but nothing was thrown");
            }
            catch (TException) { Pass(what); }
            catch (Exception ex) { Fail(what, "expected " + typeof(TException).Name + " but got " + ex.GetType().Name); }
        }

        private static string Truncate(string s) =>
            s == null ? "<null>" : s.Length <= 120 ? s : s.Substring(0, 120) + "...";

        public static int Report(Stopwatch sw)
        {
            Console.WriteLine();
            Console.WriteLine(new string('-', 68));
            if (Failures.Count == 0)
            {
                Console.WriteLine($"PASS  {_passed} checks in {sw.ElapsedMilliseconds} ms");
                return 0;
            }

            Console.WriteLine($"FAIL  {Failures.Count} of {_passed + Failures.Count} checks failed:");
            foreach (var f in Failures) Console.WriteLine("  - " + f);
            return 1;
        }
    }
}
