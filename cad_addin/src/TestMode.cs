using System;
using System.IO;

namespace HvacAiCadAddin
{
    /// <summary>
    /// Test mode for scripted end-to-end tests (tests\Scripted). It is on only while the environment variable
    /// HVACAI_TEST is 1 in the CAD process; with it unset nothing below changes what a command does.
    /// In test mode every modal dialog of the commands is accepted automatically with the dialog's DEFAULT values,
    /// remembered settings are neither read nor written (so a test never touches %APPDATA%\HvacAiCadAddin and is
    /// reproducible), the template drawing comes from HVACAI_TEST_TEMPLATE, and HVACQTO saves its workbook in
    /// HVACAI_TEST_WORK instead of asking for a file name.
    /// Optional variables: HVACAI_TEST_SETTINGS (a folder with files named like the remembered ones, for example
    /// HVACREFRIG.settings, whose fields replace the defaults of that command) and HVACAI_TEST_LOG (a text file
    /// that receives what test mode did).
    /// </summary>
    internal static class TestMode
    {
        internal static bool Enabled
        {
            get { return Environment.GetEnvironmentVariable("HVACAI_TEST") == "1"; }
        }

        internal static string TemplatePath
        {
            get { return Environment.GetEnvironmentVariable("HVACAI_TEST_TEMPLATE"); }
        }

        /// <summary>A path in the folder HVACAI_TEST_WORK (the current folder when it is not set).</summary>
        internal static string OutputPath(string fileName)
        {
            string folder = Environment.GetEnvironmentVariable("HVACAI_TEST_WORK");
            if (string.IsNullOrEmpty(folder))
                folder = Environment.CurrentDirectory;
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, fileName);
        }

        /// <summary>
        /// The settings of a command in test mode: the file of that name in HVACAI_TEST_SETTINGS when there is one,
        /// otherwise the defaults. Never the user's remembered values.
        /// </summary>
        internal static T LoadSettings<T>(string fileName, Func<string, T> parse, T defaults)
        {
            string folder = Environment.GetEnvironmentVariable("HVACAI_TEST_SETTINGS");
            if (!string.IsNullOrEmpty(folder))
            {
                string path = Path.Combine(folder, fileName);
                if (File.Exists(path))
                {
                    Log("settings " + fileName + " from " + path);
                    return parse(File.ReadAllText(path));
                }
            }

            return defaults;
        }

        internal static void Log(string line)
        {
            string path = Environment.GetEnvironmentVariable("HVACAI_TEST_LOG");
            if (string.IsNullOrEmpty(path))
                return;
            try
            {
                File.AppendAllText(path, "TESTMODE " + line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
        }
    }
}
