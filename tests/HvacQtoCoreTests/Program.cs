using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace HvacQtoCoreTests
{
    /// <summary>
    /// Expected values come from the approved HVACQTO specification and from
    /// vrv.xlsx sheet 1, not from the implementation. Exit code 0 means every check passed.
    ///
    ///   (no arguments)   run all checks
    ///   --emit DIR       write sample workbooks to DIR for template and Excel checks
    ///   --probe          print results for inputs the specification leaves open; counts nothing
    /// </summary>
    internal static class Program
    {
        // vi-VN uses a decimal comma, tr-TR has the dotted and dotless I casing trap.
        private static readonly string[] Cultures = { "en-US", "vi-VN", "tr-TR" };

        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);

            if (args.Length == 2 && args[0] == "--emit")
            {
                Directory.CreateDirectory(args[1]);
                Fixtures.Emit(args[1]);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--probe")
            {
                Probes.Run();
                return 0;
            }

            string tempDirectory = Path.Combine(Path.GetTempPath(), "HvacQtoCoreTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            CultureInfo originalCulture = Thread.CurrentThread.CurrentCulture;
            try
            {
                foreach (string culture in Cultures)
                {
                    Thread.CurrentThread.CurrentCulture = new CultureInfo(culture);
                    Check.Scope = culture;
                    int passedBefore = Check.Passed;
                    int failedBefore = Check.Failed;

                    PipeTests.Run();
                    DuctTests.Run();
                    GaugeTests.Run();
                    EquipmentTests.Run();
                    WorkbookTests.Run(tempDirectory);
                    FormulaTests.Run(tempDirectory);

                    Console.WriteLine("-- {0}: {1} passed, {2} failed", culture,
                        Check.Passed - passedBefore, Check.Failed - failedBefore);
                }
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = originalCulture;
                Directory.Delete(tempDirectory, true);
            }

            Console.WriteLine("TOTAL: {0} passed, {1} failed", Check.Passed, Check.Failed);
            return Check.Failed == 0 ? 0 : 1;
        }
    }
}
