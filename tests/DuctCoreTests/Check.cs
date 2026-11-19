using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DuctCoreTests
{
    /// <summary>Minimal named-check recorder. Every failure prints expected and actual.</summary>
    internal static class Check
    {
        public static string Scope = "";
        public static int Passed;
        public static int Failed;

        /// <summary>Runs a group of checks. An exception is one failed check, not a crash.</summary>
        public static void Case(string name, Action body)
        {
            try
            {
                body();
            }
            catch (Exception exception)
            {
                Fail(name, "no exception", exception.GetType().Name + ": " + exception.Message);
            }
        }

        public static void Equal<T>(string name, T expected, T actual)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual))
                Passed++;
            else
                Fail(name, Show(expected), Show(actual));
        }

        public static void Near(string name, double expected, double actual, double tolerance = 1e-6)
        {
            if (Math.Abs(expected - actual) <= tolerance)
                Passed++;
            else
                Fail(name, Show(expected), Show(actual));
        }

        public static void True(string name, bool condition, string actual = "false")
        {
            if (condition)
                Passed++;
            else
                Fail(name, "true", actual);
        }

        public static void False(string name, bool condition, string actual = "true")
        {
            if (!condition)
                Passed++;
            else
                Fail(name, "false", actual);
        }

        public static void Contains(string name, string text, string fragment)
        {
            if (text != null && text.IndexOf(fragment, StringComparison.Ordinal) >= 0)
                Passed++;
            else
                Fail(name, "text containing " + Show(fragment), Show(text));
        }

        public static void Fail(string name, string expected, string actual)
        {
            Failed++;
            Console.WriteLine("FAIL [{0}] {1}", Scope, name);
            Console.WriteLine("     expected: {0}", expected);
            Console.WriteLine("     actual:   {0}", actual);
        }

        public static string Show(object value)
        {
            if (value == null)
                return "(null)";
            if (value is string)
                return "\"" + ((string)value).Replace("\r", "\r").Replace("\n", "\n") + "\"";
            if (value is double)
                return ((double)value).ToString("R", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}
