using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.GraphicsInterface;
using ZwSoft.ZwCAD.Runtime;

// Native ZWCAD code (not mapped from AutoCAD): loaded into the ZWCAD test instance next to the
// add-in under test. Environment (set by run-zw-tests.ps1):
//   HVACZW_LOG       result file (UTF-8; PASS/FAIL/INFO lines and a SUMMARY line)
//   HVACZW_TEMPLATE  copy of CAD\MEP_DWT_3.dwg
//   HVACZW_ADDIN     full path of the add-in DLL that was NETLOADed (copy)
//   HVACZW_DONE      marker file HVACZWFINISH writes before it asks ZWCAD to quit
//   HVACZW_WORKDWG   drawing HVACZWUSEWORKDWG reads into a side database and makes the working database
[assembly: CommandClass(typeof(ZwSmokeTests.SmokeCommands))]

namespace ZwSmokeTests
{
    public class SmokeCommands
    {
        private static int passed, failed;

        private static void Log(string text)
        {
            string path = Environment.GetEnvironmentVariable("HVACZW_LOG") ?? Path.Combine(Path.GetTempPath(), "hvaczw.log");
            File.AppendAllText(path, text + Environment.NewLine, new UTF8Encoding(false));
        }

        private static void Check(string name, bool ok, string detail = "")
        {
            if (ok) { passed++; Log("PASS " + name + (detail.Length > 0 ? " :: " + detail : "")); }
            else { failed++; Log("FAIL " + name + " :: " + detail); }
        }

        [CommandMethod("HVACZWSMOKE")]
        public void Smoke()
        {
            try
            {
                Body();
            }
            catch (System.Exception ex)
            {
                failed++;
                Log("FAIL HVACZWSMOKE crashed :: " + ex);
            }
            finally
            {
                Log("SUMMARY passed=" + passed + " failed=" + failed);
            }
        }

        // Last command of every run: marks the run as finished, then leaves ZWCAD without a save prompt.
        [CommandMethod("HVACZWFINISH", CommandFlags.Session)]
        public void Finish()
        {
            string done = Environment.GetEnvironmentVariable("HVACZW_DONE");
            try
            {
                if (!string.IsNullOrEmpty(done))
                    File.WriteAllText(done, DateTime.Now.ToString("o"));
            }
            catch (System.Exception ex)
            {
                Log("INFO could not write the done file: " + ex.Message);
            }

            try { HostApplicationServices.WorkingDatabase = Application.DocumentManager.MdiActiveDocument.Database; } catch { }
            if (sideDatabase != null) { try { sideDatabase.Dispose(); } catch { } sideDatabase = null; }

            // Discard every open drawing so that ZWCAD has nothing to ask about, then quit. Only this
            // instance (the one the runner started) runs this command.
            try
            {
                foreach (Document doc in Application.DocumentManager.Cast<Document>().ToList())
                {
                    try { doc.CloseAndDiscard(); }
                    catch (System.Exception ex) { Log("INFO CloseAndDiscard " + doc.Name + ": " + ex.Message); }
                }
            }
            catch (System.Exception ex)
            {
                Log("INFO discarding the documents failed: " + ex.Message);
            }

            try { Application.Quit(); }
            catch (System.Exception ex) { Log("INFO Application.Quit failed: " + ex.Message); }
        }

        // ---------------------------------------------------------------------------------------
        // Drawing providers. The suites work on HostApplicationServices.WorkingDatabase. ZWCAD opens a
        // drawing that has missing SHX fonts (the template has) with a blocking "Missing SHX Files"
        // dialog, so the drawing is read into a side database instead, which asks nothing, and that
        // database becomes the working database. HVACZWFINISH puts the active document's database back.
        private static Database sideDatabase;

        [CommandMethod("HVACZWUSEWORKDWG")]
        public void UseWorkDwg()
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("HVACZW_WORKDWG");
                Database db = new Database(false, true);
                db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                db.CloseInput(true);
                sideDatabase = db;
                HostApplicationServices.WorkingDatabase = db;
                Log("INFO working database is a side database read from " + path);
            }
            catch (System.Exception ex)
            {
                Log("FAIL HVACZWUSEWORKDWG :: " + ex);
            }
        }

        // Rooms as HVACROOMS writes them: closed LWPOLYLINEs on HVAC-ROOM with HVACAI_ROOM xdata
        // [id][kind][name][area m2], in a new side database (HVACPLACEUNITS test input).
        [CommandMethod("HVACZWMAKEROOMS")]
        public void MakeRooms()
        {
            try
            {
                Database db = new Database(true, true);
                double[][][] shapes =
                {
                    new[] { new[] { 0.0, 0 }, new[] { 4000.0, 0 }, new[] { 4000.0, 3000 }, new[] { 0.0, 3000 } },
                    new[] { new[] { 5000.0, 0 }, new[] { 10000.0, 0 }, new[] { 10000.0, 6000 }, new[] { 5000.0, 6000 } },
                    new[] { new[] { 0.0, 7000 }, new[] { 8000.0, 7000 }, new[] { 8000.0, 17000 }, new[] { 0.0, 17000 } },
                    new[] { new[] { 11000.0, 0 }, new[] { 23000.0, 0 }, new[] { 23000.0, 12000 }, new[] { 11000.0, 12000 } },
                    new[] { new[] { 9000.0, 13000 }, new[] { 15000.0, 13000 }, new[] { 15000.0, 16000 }, new[] { 12000.0, 16000 }, new[] { 12000.0, 19000 }, new[] { 9000.0, 19000 } },
                    new[] { new[] { 24000.0, 0 }, new[] { 25000.0, 0 }, new[] { 25000.0, 1000 }, new[] { 24000.0, 1000 } },
                };
                string[] names = { "Room 1", "Meeting", "Office", "Open office", "L room", "Shaft" };
                using (Transaction transaction = db.TransactionManager.StartTransaction())
                {
                    RegAppTable apps = (RegAppTable)transaction.GetObject(db.RegAppTableId, OpenMode.ForWrite);
                    if (!apps.Has("HVACAI_ROOM"))
                    {
                        RegAppTableRecord app = new RegAppTableRecord { Name = "HVACAI_ROOM" };
                        apps.Add(app);
                        transaction.AddNewlyCreatedDBObject(app, true);
                    }

                    LayerTable layers = (LayerTable)transaction.GetObject(db.LayerTableId, OpenMode.ForWrite);
                    if (!layers.Has("HVAC-ROOM"))
                    {
                        LayerTableRecord layer = new LayerTableRecord { Name = "HVAC-ROOM" };
                        layers.Add(layer);
                        transaction.AddNewlyCreatedDBObject(layer, true);
                    }

                    BlockTable table = (BlockTable)transaction.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord model = (BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    for (int i = 0; i < shapes.Length; i++)
                    {
                        ZwSoft.ZwCAD.DatabaseServices.Polyline polyline = new ZwSoft.ZwCAD.DatabaseServices.Polyline();
                        polyline.SetDatabaseDefaults(db);
                        for (int v = 0; v < shapes[i].Length; v++)
                            polyline.AddVertexAt(v, new Point2d(shapes[i][v][0], shapes[i][v][1]), 0, 0, 0);
                        polyline.Closed = true;
                        polyline.LayerId = layers["HVAC-ROOM"];   // by id: a name would resolve against the active document
                        double area = Math.Abs(polyline.Area) / 1e6;
                        bool shaft = names[i] == "Shaft";
                        model.AppendEntity(polyline);
                        transaction.AddNewlyCreatedDBObject(polyline, true);
                        // After AppendEntity: the application name resolves against the entity's own database.
                        polyline.XData = new ResultBuffer(
                            new TypedValue((int)DxfCode.ExtendedDataRegAppName, "HVACAI_ROOM"),
                            new TypedValue((int)DxfCode.ExtendedDataAsciiString, "R" + (i + 1)),
                            new TypedValue((int)DxfCode.ExtendedDataAsciiString, shaft ? "shaft" : "room"),
                            new TypedValue((int)DxfCode.ExtendedDataAsciiString, names[i]),
                            new TypedValue((int)DxfCode.ExtendedDataReal, area));
                    }

                    transaction.Commit();
                }

                sideDatabase = db;
                HostApplicationServices.WorkingDatabase = db;
                Log("INFO working database is a new side database with " + shapes.Length + " rooms (one shaft)");
            }
            catch (System.Exception ex)
            {
                Log("FAIL HVACZWMAKEROOMS :: " + ex);
            }
        }
        private static void Body()
        {
            Log("INFO started " + DateTime.Now.ToString("o"));
            Log("INFO clr " + Environment.Version + " / " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            Check("runs on .NET Framework 4.x",
                System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework"),
                System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            Check("64-bit process", Environment.Is64BitProcess);

            Log("INFO Application.Version " + Application.Version);
            string vernum = null;
            try { vernum = Convert.ToString(Application.GetSystemVariable("VERNUM")); } catch (System.Exception ex) { vernum = "n/a (" + ex.Message + ")"; }
            Log("INFO VERNUM " + vernum);
            string acadver = null;
            try { acadver = Convert.ToString(Application.GetSystemVariable("ACADVER")); } catch (System.Exception ex) { acadver = "n/a (" + ex.Message + ")"; }
            Log("INFO ACADVER " + acadver);
            Check("Application.Version is 26.x", Application.Version != null && Application.Version.Major == 26, Convert.ToString(Application.Version));

            // The add-in under test.
            string expected = Environment.GetEnvironmentVariable("HVACZW_ADDIN");
            Assembly[] copies = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "HvacAiCadAddin").ToArray();
            Check("add-in assembly loaded exactly once", copies.Length == 1,
                copies.Length + " copies: " + string.Join("; ", copies.Select(a => a.Location)));
            if (copies.Length >= 1)
            {
                Assembly addin = copies[0];
                Log("INFO add-in " + addin.Location + " " + addin.GetName().Version);
                if (!string.IsNullOrEmpty(expected))
                    Check("loaded add-in is the NETLOADed copy",
                        string.Equals(Path.GetFullPath(addin.Location), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase),
                        addin.Location + " vs " + expected);

                Type[] types;
                try { types = addin.GetTypes(); }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                    Check("all add-in types load", false, string.Join(" | ", ex.LoaderExceptions.Take(3).Select(e => e.Message)));
                }

                List<string> names = new List<string>();
                foreach (Type type in types)
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        foreach (CommandMethodAttribute attribute in method.GetCustomAttributes(typeof(CommandMethodAttribute), false))
                            names.Add(attribute.GlobalName);
                Check("add-in declares commands", names.Count > 0, names.Count.ToString());
                Log("INFO commands " + names.Count + ": " + string.Join(", ", names));

                // Registration proof. Utils.IsCommandDefined / IsCommandNameInUse answer False even for LINE in
                // ZWCAD 2026, so they are useless here. Instead run HVACCOUNT (non-interactive: it only totals
                // curve lengths on '!x' layers and prints) through Editor.Command: an unregistered command throws.
                // The other commands ask for input, so they are only checked by reflection (name, public,
                // parameterless instance method of a public class), which is what ZWCAD registers from.
                List<string> badShape = new List<string>();
                foreach (Type type in types)
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (method.GetCustomAttributes(typeof(CommandMethodAttribute), false).Length > 0
                            && (!type.IsPublic || !method.IsPublic || method.GetParameters().Length != 0))
                            badShape.Add(type.Name + "." + method.Name);
                Check("every [CommandMethod] is a public parameterless method of a public class", badShape.Count == 0, string.Join(", ", badShape));
                Check("command names are unique", names.Count == names.Distinct().Count(), string.Join(", ", names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key)));

                string commandResult = "ok";
                try { Application.DocumentManager.MdiActiveDocument.Editor.Command("_.HVACCOUNT"); }
                catch (System.Exception ex) { commandResult = ex.GetType().Name + ": " + ex.Message; }
                Check("HVACCOUNT runs through Editor.Command (production command registered and executes)", commandResult == "ok", commandResult);
            }
            // Facts the add-in relies on.
            string table = null;
            try { table = RXObject.GetClass(typeof(Table)).DxfName; } catch (System.Exception ex) { table = "error " + ex.Message; }
            Log("INFO Table DXF name '" + table + "' (RiserWriter tests for ACAD_TABLE)");
            Check("Table DXF name is ACAD_TABLE", table == "ACAD_TABLE", table);

            TransientManager transient = null;
            try { transient = TransientManager.CurrentTransientManager; } catch (System.Exception ex) { Log("INFO transient manager threw " + ex.Message); }
            Check("TransientManager.CurrentTransientManager is not null", transient != null);

            Check("Document.SendStringToExecute exists", typeof(Document).GetMethod("SendStringToExecute") != null);
            Document active = Application.DocumentManager.MdiActiveDocument;
            Check("a document is active", active != null);
            if (active != null)
                Log("INFO active document " + active.Name + " DBMOD " + Convert.ToString(Application.GetSystemVariable("DBMOD")));

            // The template, read into a side database.
            string template = Environment.GetEnvironmentVariable("HVACZW_TEMPLATE");
            Check("template copy given", !string.IsNullOrEmpty(template) && File.Exists(template), template);
            if (!string.IsNullOrEmpty(template) && File.Exists(template))
            {
                using (Database side = new Database(false, true))
                {
                    side.ReadDwgFile(template, FileOpenMode.OpenForReadAndAllShare, true, "");
                    side.CloseInput(true);
                    using (Transaction transaction = side.TransactionManager.StartTransaction())
                    {
                        BlockTable blocks = (BlockTable)transaction.GetObject(side.BlockTableId, OpenMode.ForRead);
                        bool has = blocks.Has("REC_600x300");
                        Check("template has block REC_600x300", has);
                        if (has)
                        {
                            BlockTableRecord record = (BlockTableRecord)transaction.GetObject(blocks["REC_600x300"], OpenMode.ForRead);
                            Check("REC_600x300 IsDynamicBlock", record.IsDynamicBlock);
                            Log("INFO REC_600x300 name " + record.Name + " blocks " + blocks.Cast<ObjectId>().Count());
                        }

                        transaction.Commit();
                    }
                }
            }
        }
    }
}
