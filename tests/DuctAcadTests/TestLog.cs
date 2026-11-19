using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DuctAcadTests
{
    /// <summary>
    /// accoreconsole writes UTF-16 to stdout, so results go to a file named by HVACDUCT_LOG.
    /// The runner reads the PASS/FAIL lines and the SUMMARY line.
    /// </summary>
    internal static class TestLog
    {
        public static int Passed;
        public static int Failed;

        private static string LogPath
        {
            get
            {
                string path = Environment.GetEnvironmentVariable("HVACDUCT_LOG");
                return string.IsNullOrEmpty(path)
                    ? Path.Combine(Path.GetTempPath(), "hvacduct.log")
                    : path;
            }
        }

        public static void Line(string text)
        {
            File.AppendAllText(LogPath, text + Environment.NewLine, new UTF8Encoding(false));
        }

        public static void Pass(string name)
        {
            Passed++;
            Line("PASS " + name);
        }

        public static void Fail(string name, string detail)
        {
            Failed++;
            Line("FAIL " + name + " :: " + detail);
        }

        public static void True(string name, bool condition, string detail = "")
        {
            if (condition)
                Pass(name);
            else
                Fail(name, detail);
        }

        public static void Near(string name, double expected, double actual, double tolerance = 1e-4)
        {
            if (Math.Abs(expected - actual) <= tolerance)
                Pass(name);
            else
                Fail(name, "expected " + F(expected) + " actual " + F(actual));
        }

        public static void Equal<T>(string name, T expected, T actual)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual))
                Pass(name);
            else
                Fail(name, "expected " + expected + " actual " + actual);
        }

        public static string F(double value)
        {
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }

        public static void Summary()
        {
            Line("SUMMARY passed=" + Passed + " failed=" + Failed);
        }
    }
}
