using System;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using HvacAiCadAddin.Commands;

[assembly: CommandClass(typeof(ScriptedTests.ScriptedCommands))]

namespace ScriptedTests
{
    // Commands of the scripted end-to-end tests (tests\Scripted). Environment (set by the runners):
    //   HVACTEST_WORK      work folder: drawings\<scenario>.dwg are built here, dump\<scenario>.<cad>.json written here
    //   HVACTEST_CAD       "acad" or "zwcad": the tag in the dump file name
    //   HVACTEST_LOG       result file (UTF-8): INFO / FAIL / DUMPED lines
    //   HVACAI_TEST_TEMPLATE  a COPY of CAD\MEP_DWT_3.dwg
    //   HVACTEST_ONLY      optional, comma-separated scenario names to build (default: all)
    // The test DLL needs only AcDbMgd and AcCoreMgd, so accoreconsole can load it; commands never prompt, so scripts
    // can run them without feeding input.
    public class ScriptedCommands
    {
        private static void Log(string line)
        {
            string path = Environment.GetEnvironmentVariable("HVACTEST_LOG");
            if (string.IsNullOrEmpty(path))
                path = Path.Combine(Path.GetTempPath(), "hvactest.log");
            File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
        }

        private static string Work
        {
            get { return Environment.GetEnvironmentVariable("HVACTEST_WORK") ?? Path.GetTempPath(); }
        }

        // Builds every scenario drawing (or those named in HVACTEST_ONLY) and saves it as a DWG.
        [CommandMethod("HVACTESTBUILD")]
        public void Build()
        {
            try
            {
                string only = Environment.GetEnvironmentVariable("HVACTEST_ONLY");
                string[] wanted = string.IsNullOrEmpty(only) ? null : only.Split(',');
                string template = Environment.GetEnvironmentVariable("HVACAI_TEST_TEMPLATE");
                string error;
                using (TemplateSource source = TemplateSource.TryOpen(template, out error))
                {
                    if (source == null)
                    {
                        Log("FAIL build: template " + error);
                        return;
                    }

                    string folder = Path.Combine(Work, "drawings");
                    Directory.CreateDirectory(folder);
                    foreach (Scenario scenario in Scenarios.All)
                    {
                        if (wanted != null && Array.IndexOf(wanted, scenario.Name) < 0)
                            continue;
                        try
                        {
                            using (Database db = new Database(true, true))
                            {
                                scenario.Build(new ScenarioBuilder(db, source));
                                string path = Path.Combine(folder, scenario.Name + ".dwg");
                                if (File.Exists(path))
                                    File.Delete(path);
                                db.SaveAs(path, DwgVersion.Current);
                                Log("BUILT " + scenario.Name + " " + path);
                            }
                        }
                        catch (System.Exception ex)
                        {
                            Log("FAIL build " + scenario.Name + " :: " + ex);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                Log("FAIL HVACTESTBUILD crashed :: " + ex);
            }
        }

        // Dumps the model space of the active drawing; the scenario name is the drawing's file name.
        [CommandMethod("HVACTESTDUMP")]
        public void Dump()
        {
            try
            {
                Database db = HostApplicationServices.WorkingDatabase;
                string scenario = Path.GetFileNameWithoutExtension(db.Filename);
                string cad = Environment.GetEnvironmentVariable("HVACTEST_CAD") ?? "cad";
                string version = Environment.GetEnvironmentVariable("HVACTEST_CAD_VERSION") ?? "";
                string path = Path.Combine(Work, "dump", scenario + "." + cad + ".json");
                DrawingDump.Write(db, scenario, cad, version, path);
                Log("DUMPED " + scenario + " " + path);

                // The HVACQTO workbook (test mode saves it in the work folder) belongs to this scenario.
                string workbook = Path.Combine(Work, "HVAC_Quantity_Takeoff.xml");
                if (File.Exists(workbook))
                {
                    File.Copy(workbook, Path.Combine(Work, "dump", scenario + "." + cad + ".xml"), true);
                    File.Delete(workbook);
                    Log("DUMPED workbook of " + scenario);
                }
            }
            catch (System.Exception ex)
            {
                Log("FAIL HVACTESTDUMP crashed :: " + ex);
            }
        }

        // HVACTESTDEFER, then a script line with the path of a text file (one script line per line): after the
        // next command that is not a test command has ended, the lines of that file are queued as typed input.
        // HVACSTRETCH needs this: it queues native STRETCH with SendStringToExecute, and a CAD runs a queued string
        // only when the script has ended, so the rest of the scenario (the second point, the dump, the quit) must
        // be queued behind it. The queue is first in, first out, so the text lands after the command's own string.
        [CommandMethod("HVACTESTDEFER")]
        public void Defer()
        {
            try
            {
                Document document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
                PromptStringOptions options = new PromptStringOptions("\nFile with the deferred lines: ") { AllowSpaces = true };
                PromptResult result = document.Editor.GetString(options);
                if (result.Status != PromptStatus.OK)
                    return;
                string text = string.Join("\n", File.ReadAllLines(result.StringResult.Trim('"'))) + "\n";
                CommandEventHandler handler = null;
                handler = (s, e) =>
                {
                    if (e.GlobalCommandName.StartsWith("HVACTEST", StringComparison.OrdinalIgnoreCase))
                        return;
                    document.CommandEnded -= handler;
                    document.CommandCancelled -= handler;
                    document.CommandFailed -= handler;
                    Log("TRACE deferred input queued after " + e.GlobalCommandName);
                    document.SendStringToExecute(text, true, false, false);
                };
                document.CommandEnded += handler;
                // A command cancelled from the script (HVACDUCT, whose loop only ends on Cancel) ends the script in ZWCAD.
                document.CommandCancelled += handler;
                document.CommandFailed += handler;
            }
            catch (System.Exception ex)
            {
                Log("FAIL HVACTESTDEFER :: " + ex);
            }
        }

        // A script's typed coordinates are overridden by the running object snaps (OSNAPCOORD 2 means "keyboard entry
        // wins except in scripts"), and OSMODE lives in the user's profile. HVACTESTSAFE sets OSMODE 0 and writes the
        // original value to <work>\osmode.orig; HVACTESTRESTORE (last command of every script) puts it back and
        // deletes the file. If a run was killed, the runner finds the file and repairs the value.
        private static object originalOsmode;

        private static string OsmodeFile
        {
            get { return Path.Combine(Work, "osmode.orig"); }
        }

        [CommandMethod("HVACTESTSAFE")]
        public void Safe()
        {
            try
            {
                if (originalOsmode == null)
                {
                    originalOsmode = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("OSMODE");
                    File.WriteAllText(OsmodeFile, Convert.ToString(originalOsmode, System.Globalization.CultureInfo.InvariantCulture));
                }

                Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("OSMODE", (short)0);
            }
            catch (System.Exception ex)
            {
                Log("FAIL HVACTESTSAFE :: " + ex);
            }
        }

        [CommandMethod("HVACTESTRESTORE")]
        public void Restore()
        {
            try
            {
                object value = originalOsmode;
                if (value == null && File.Exists(OsmodeFile))
                    value = short.Parse(File.ReadAllText(OsmodeFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                if (value != null)
                    Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("OSMODE", Convert.ToInt16(value));
                originalOsmode = null;
                if (File.Exists(OsmodeFile))
                    File.Delete(OsmodeFile);
            }
            catch (System.Exception ex)
            {
                Log("FAIL HVACTESTRESTORE :: " + ex);
            }
        }

        // A trace handler must never throw: an exception in an editor event aborts the command being traced.
        private static void Safe(Action action)
        {
            try { action(); } catch (System.Exception) { }
        }

        private static string Line(string text)
        {
            return (text ?? "").Replace("\n", " ").Trim();
        }

        // Logs the start, end, cancel and failure of every command of the active document (TRACE lines): the
        // transcript of a ZWCAD run, which has no console.
        [CommandMethod("HVACTESTTRACE")]
        public void Trace()
        {
            try
            {
                Document document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
                document.CommandWillStart += (s, e) => Log("TRACE start " + e.GlobalCommandName);
                document.CommandEnded += (s, e) => Log("TRACE end " + e.GlobalCommandName);
                document.CommandCancelled += (s, e) => Log("TRACE cancelled " + e.GlobalCommandName);
                document.CommandFailed += (s, e) => Log("TRACE failed " + e.GlobalCommandName);
                document.UnknownCommand += (s, e) => Log("TRACE unknown command " + e.GlobalCommandName);
                Editor editor = document.Editor;
                editor.PromptingForPoint += (s, e) => Safe(() => Log("TRACE ask point: " + Line(e.Options.Message)));
                editor.PromptedForPoint += (s, e) => Safe(() => Log("TRACE got point: " + e.Result.Status + " " + e.Result.StringResult
                    + (e.Result.Status == PromptStatus.OK ? " " + e.Result.Value.X + "," + e.Result.Value.Y : "")));
                editor.PromptingForSelection += (s, e) => Safe(() => Log("TRACE ask selection: " + Line(e.Options.MessageForAdding)));
                editor.PromptedForSelection += (s, e) => Safe(() => Log("TRACE got selection: " + e.Result.Status
                    + (e.Result.Value != null ? " " + e.Result.Value.Count : "")));
                editor.PromptingForEntity += (s, e) => Safe(() => Log("TRACE ask entity: " + Line(e.Options.Message)));
                editor.PromptedForEntity += (s, e) => Safe(() => Log("TRACE got entity: " + e.Result.Status));
            }
            catch (System.Exception ex)
            {
                Log("FAIL HVACTESTTRACE :: " + ex);
            }
        }

        // Which add-in assemblies are loaded, and whether test mode is on (a smoke check of the run).
        [CommandMethod("HVACTESTINFO")]
        public void Info()
        {
            try
            {
                Log("INFO HVACAI_TEST=" + Environment.GetEnvironmentVariable("HVACAI_TEST"));
                foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name == "HvacAiCadAddin")
                        Log("INFO add-in assembly loaded from " + assembly.Location);
                }

                Database db = HostApplicationServices.WorkingDatabase;
                Log("INFO drawing " + db.Filename);
            }
            catch (System.Exception ex)
            {
                Log("FAIL HVACTESTINFO :: " + ex);
            }
        }
    }
}
