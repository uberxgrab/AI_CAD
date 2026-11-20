using System;
using System.Globalization;
using System.Text;
using System.Threading;

namespace PipeCoreTests
{
    /// <summary>
    /// Expected values come from the approved specification (AGENTS.md, "Drafting commands:
    /// HVACDUCT, HVACDUCTALIGN, HVACPIPESIZE, HVACREFRIG") and are worked out by hand, not taken from
    /// the implementation. Exit code 0 means every check passed under en-US, vi-VN and tr-TR.
    /// </summary>
    internal static class Program
    {
        // vi-VN uses a decimal comma, tr-TR has the dotted and dotless I casing trap.
        private static readonly string[] Cultures = { "en-US", "vi-VN", "tr-TR" };

        private static int Main()
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            CultureInfo originalCulture = Thread.CurrentThread.CurrentCulture;
            try
            {
                foreach (string culture in Cultures)
                {
                    Thread.CurrentThread.CurrentCulture = new CultureInfo(culture);
                    Thread.CurrentThread.CurrentUICulture = new CultureInfo(culture);
                    Check.Scope = culture;
                    int passedBefore = Check.Passed;
                    int failedBefore = Check.Failed;

                    CatalogTests.Run();
                    SettingsTests.Run();
                    SegmentTests.Run();
                    RunTests.Run();
                    SplitPlanTests.Run();
                    RefrigCleanTests.Run();
                    RefnetTests.Run();
                    ControlWireTests.Run();

                    Console.WriteLine("-- {0}: {1} passed, {2} failed", culture,
                        Check.Passed - passedBefore, Check.Failed - failedBefore);
                }
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = originalCulture;
            }

            Console.WriteLine("TOTAL: {0} passed, {1} failed", Check.Passed, Check.Failed);
            return Check.Failed == 0 ? 0 : 1;
        }
    }
}
