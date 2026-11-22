using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using HvacAiCadAddin.Commands;
using HvacAiCadAddin.Units;

[assembly: CommandClass(typeof(UnitAcadTests.UnitAcadTestCommands))]

namespace UnitAcadTests
{
    // Runs on a copy of a drawing that HVACROOMS wrote (closed HVAC-ROOM polylines with HVACAI_ROOM
    // xdata) and a copy of CAD\MEP_DWT_3.dwg. The results go to the file named by HVACUNITS_LOG
    // (accoreconsole's stdout is UTF-16).
    public class UnitAcadTestCommands
    {
        private static int passed, failed;

        private static void Log(string text)
        {
            string path = Environment.GetEnvironmentVariable("HVACUNITS_LOG") ?? Path.Combine(Path.GetTempPath(), "hvacunits.log");
            File.AppendAllText(path, text + Environment.NewLine, new UTF8Encoding(false));
        }

        private static void Check(string name, bool ok, string detail = "")
        {
            if (ok) { passed++; Log("PASS " + name); }
            else { failed++; Log("FAIL " + name + " :: " + detail); }
        }

        [CommandMethod("UNITTEST")]
        public void RunAll()
        {
            try
            {
                Body();
            }
            catch (System.Exception ex)
            {
                failed++;
                Log("FAIL UNITTEST crashed :: " + ex);
            }
            finally
            {
                Log("SUMMARY passed=" + passed + " failed=" + failed);
            }
        }

        private static void Body()
        {
            Database db = HostApplicationServices.WorkingDatabase;
            string templatePath = Environment.GetEnvironmentVariable("HVACUNITS_TEMPLATE");
            string error;
            TemplateSource template = TemplateSource.TryOpen(templatePath, out error);
            Check("template opens", template != null, error);
            if (template == null)
                return;

            // Rooms from the drawing, each with a fake load (kW = area * 0.15, so some rooms need splitting).
            List<string> ids = new List<string>();
            List<PlannedUnit> units = new List<PlannedUnit>();
            UnitSettings settings = new UnitSettings();
            UnitFamily family = UnitCatalog.Find(settings.Family);
            int rooms = 0, withoutLoad = 0;
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    Polyline polyline = transaction.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (polyline == null)
                        continue;
                    List<object> values = XData(polyline, UnitSettings.RoomApp);
                    RoomTags.RoomTag room = RoomTags.ParseRoom(values);
                    if (room == null || room.IsShaft)
                        continue;
                    rooms++;
                    double load = room.AreaM2 * 0.15;
                    UnitChoice choice = UnitSelector.Choose(load, family, settings, 0);
                    if (choice == null) { withoutLoad++; continue; }
                    double[][] polygon = new double[polyline.NumberOfVertices][];
                    for (int i = 0; i < polygon.Length; i++)
                    {
                        Point2d p = polyline.GetPoint2dAt(i);
                        polygon[i] = new[] { p.X, p.Y };
                    }

                    List<PlacedUnitPose> poses = UnitPlacer.Place(polygon, choice.Units, family.Width, family.Height, settings.Clearance);
                    ids.Add(room.Id);
                    for (int i = 0; i < poses.Count; i++)
                        units.Add(new PlannedUnit { RoomId = room.Id, Family = family, Model = choice.Model, Number = i + 1, OfCount = poses.Count, Pose = poses[i] });
                }

                transaction.Commit();
            }

            Check("rooms read", rooms > 0, "rooms=" + rooms);
            Check("units planned", units.Count > 0, "units=" + units.Count);
            Log("rooms=" + rooms + " units=" + units.Count);

            UnitWriteResult first = UnitPlacementWriter.Write(db, db.CurrentSpaceId, units, ids, template);
            Check("first run inserted all", first.Inserted == units.Count, first.Inserted + " of " + units.Count);
            Check("first run erased none", first.Erased == 0, first.Erased.ToString());
            foreach (string note in first.Notes)
                Log("  note: " + note);

            int tagged = CountTagged(db, out int onLayer, out int badXData, out int rotated);
            Check("tagged blocks present", tagged == units.Count, tagged + " vs " + units.Count);
            Check("all on the unit layer", onLayer == tagged, onLayer + " of " + tagged);
            Check("xdata complete", badXData == 0, badXData + " bad");
            Log("  rotated units: " + rotated);

            // Block definitions arrived from the template.
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)transaction.GetObject(db.BlockTableId, OpenMode.ForRead);
                HashSet<string> used = new HashSet<string>();
                foreach (PlannedUnit unit in units)
                    used.Add(unit.Model.BlockName);
                foreach (string name in used)
                {
                    Check("block " + name + " in drawing", table.Has(name));
                    if (table.Has(name))
                    {
                        BlockTableRecord record = (BlockTableRecord)transaction.GetObject(table[name], OpenMode.ForRead);
                        int count = 0;
                        foreach (ObjectId unused in record) count++;
                        // The template cassette has 160+ entities; the fallback only 5.
                        Check("block " + name + " is the template's", count > 50, count + " entities");
                    }
                }

                LayerTable layers = (LayerTable)transaction.GetObject(db.LayerTableId, OpenMode.ForRead);
                Check("unit layer exists", layers.Has(UnitSettings.UnitLayer));
                transaction.Commit();
            }

            // Rerun for the same rooms: the earlier units are replaced, not doubled.
            UnitWriteResult second = UnitPlacementWriter.Write(db, db.CurrentSpaceId, units, ids, template);
            Check("rerun erased the earlier units", second.Erased == units.Count, second.Erased + " vs " + units.Count);
            Check("rerun inserted again", second.Inserted == units.Count, second.Inserted.ToString());
            int again = CountTagged(db, out onLayer, out badXData, out rotated);
            Check("rerun leaves one set", again == units.Count, again + " vs " + units.Count);

            // A rerun for one room only touches that room.
            if (ids.Count > 1)
            {
                List<PlannedUnit> one = units.FindAll(u => u.RoomId == ids[0]);
                UnitWriteResult partial = UnitPlacementWriter.Write(db, db.CurrentSpaceId, one, new List<string> { ids[0] }, template);
                Check("partial rerun erased only that room", partial.Erased == one.Count, partial.Erased + " vs " + one.Count);
                Check("partial rerun keeps others", CountTagged(db, out onLayer, out badXData, out rotated) == units.Count);
            }

            // A block the template does not have becomes a simple fallback block.
            UnitModel odd = new UnitModel { Index = 999, Kw = 99.9, BlockName = "FCU-CAS-99.9 kW" };
            PlannedUnit fake = new PlannedUnit
            {
                RoomId = "ZZ", Family = family, Model = odd, Number = 1, OfCount = 1,
                Pose = new PlacedUnitPose { X = 5000, Y = 5000, Rotation = 0.3 }
            };
            UnitWriteResult fallback = UnitPlacementWriter.Write(db, db.CurrentSpaceId, new[] { fake }, new List<string> { "ZZ" }, template);
            Check("fallback inserted", fallback.Inserted == 1);
            Check("fallback noted", fallback.Notes.Count > 0);
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)transaction.GetObject(db.BlockTableId, OpenMode.ForRead);
                Check("fallback block exists", table.Has("FCU-CAS-99.9 kW"));
                transaction.Commit();
            }

            // Without a template the fallback is used too.
            odd = new UnitModel { Index = 998, Kw = 98.8, BlockName = "FCU-CAS-98.8 kW" };
            fake.Model = odd;
            fake.RoomId = "ZY";
            UnitWriteResult none = UnitPlacementWriter.Write(db, db.CurrentSpaceId, new[] { fake }, new List<string> { "ZY" }, null);
            Check("no-template run inserted", none.Inserted == 1);

            // The insertion point puts the body centre where planned.
            CheckInsertionGeometry(db, fake);
            template.Dispose();
        }

        private static void CheckInsertionGeometry(Database db, PlannedUnit unit)
        {
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)transaction.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                bool found = false;
                foreach (ObjectId id in space)
                {
                    BlockReference reference = transaction.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (reference == null || UnitPlacementWriter.PlacedRoomId(reference) != "ZY")
                        continue;
                    found = true;
                    // Fallback block body: square from y = -950 to 0 around x = 0, centre (0,-475).
                    Point3d centre = new Point3d(0, -475, 0).TransformBy(reference.BlockTransform);
                    Check("body centre where planned", Math.Abs(centre.X - 5000) < 1e-6 && Math.Abs(centre.Y - 5000) < 1e-6,
                        centre.ToString());
                }

                Check("found the ZY unit", found);
                transaction.Commit();
            }
        }

        private static int CountTagged(Database db, out int onLayer, out int bad, out int rotated)
        {
            int tagged = 0;
            onLayer = 0;
            bad = 0;
            rotated = 0;
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    BlockReference reference = transaction.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (reference == null || reference.IsErased)
                        continue;
                    string roomId = UnitPlacementWriter.PlacedRoomId(reference);
                    if (roomId == null || roomId == "ZZ" || roomId == "ZY")
                        continue;
                    tagged++;
                    if (reference.Layer == UnitSettings.UnitLayer)
                        onLayer++;
                    if (Math.Abs(reference.Rotation) > 1e-6)
                        rotated++;
                    List<object> values = XData(reference, UnitSettings.PlacedApp);
                    // app, room id, index, kW, number, count
                    if (values.Count != 6 || !(values[2] is int) || !(values[3] is double) || !(values[4] is short) || !(values[5] is short))
                        bad++;
                }

                transaction.Commit();
            }

            return tagged;
        }

        private static List<object> XData(Entity entity, string app)
        {
            List<object> values = new List<object>();
            using (ResultBuffer buffer = entity.GetXDataForApplication(app))
            {
                if (buffer == null)
                    return values;
                foreach (TypedValue value in buffer)
                    values.Add(value.Value);
            }

            return values;
        }
    }
}
