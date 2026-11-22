using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using HvacAiCadAddin.Commands;
using HvacAiCadAddin.Vrv;

[assembly: CommandClass(typeof(OduRiserAcadTests.OduRiserAcadTestCommands))]

namespace OduRiserAcadTests
{
    // Runs on a copy of a drawing (any drawing without the ODU blocks) and a copy of CAD\MEP_DWT_3.dwg.
    // The results go to the file named by HVACODU_LOG (accoreconsole's stdout is UTF-16).
    public class OduRiserAcadTestCommands
    {
        private static int passed, failed;

        private static void Log(string text)
        {
            string path = Environment.GetEnvironmentVariable("HVACODU_LOG") ?? Path.Combine(Path.GetTempPath(), "hvacodu.log");
            File.AppendAllText(path, text + Environment.NewLine, new UTF8Encoding(false));
        }

        private static void Check(string name, bool ok, string detail = "")
        {
            if (ok) { passed++; Log("PASS " + name); }
            else { failed++; Log("FAIL " + name + " :: " + detail); }
        }

        [CommandMethod("ODURISERTEST")]
        public void RunAll()
        {
            try
            {
                Body();
            }
            catch (System.Exception ex)
            {
                failed++;
                Log("FAIL ODURISERTEST crashed :: " + ex);
            }
            finally
            {
                Log("SUMMARY passed=" + passed + " failed=" + failed);
            }
        }

        private static void Body()
        {
            Database db = HostApplicationServices.WorkingDatabase;
            string templatePath = Environment.GetEnvironmentVariable("HVACODU_TEMPLATE");
            string error;
            TemplateSource template = TemplateSource.TryOpen(templatePath, out error);
            Check("template opens", template != null, error);
            if (template == null)
                return;

            using (template)
            {
                Check("ODU-12 HP not in the work drawing before", !DrawingHas(db, "ODU-12 HP"));

                // Indoor units: two tagged (HVACPLACEUNITS data), one by block name only, one valve (no capacity).
                List<string> missing;
                template.ImportBlocks(db, new[] { "FCU-CAS-2.8 kW", "FCU-CAS-5.6 kW", "HVAC_2 WAYS ELECTRICAL MODULATING VALVE" }, out missing);
                Check("indoor blocks imported", missing.Count == 0, string.Join(",", missing));
                List<ObjectId> inserted = new List<ObjectId>();
                using (Transaction transaction = db.TransactionManager.StartTransaction())
                {
                    VrvUnitReader.EnsureRegApp(transaction, db, IndoorCapacity.PlacedApp);
                    BlockTable table = (BlockTable)transaction.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord space = (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    inserted.Add(Insert(transaction, db, space, table["FCU-CAS-2.8 kW"], 1000, 1000, 25, 2.8, true));
                    inserted.Add(Insert(transaction, db, space, table["FCU-CAS-5.6 kW"], 3000, 1000, 50, 5.6, true));
                    inserted.Add(Insert(transaction, db, space, table["FCU-CAS-5.6 kW"], 5000, 1000, 50, 5.6, false));
                    inserted.Add(Insert(transaction, db, space, table["HVAC_2 WAYS ELECTRICAL MODULATING VALVE"], 7000, 1000, 0, 0, false));
                    transaction.Commit();
                }

                IndoorReadResult read = VrvUnitReader.Read(db, inserted);
                Check("three units read", read.Units.Count == 3, read.Units.Count.ToString());
                Check("valve skipped", read.Skipped.Count == 1, read.Skipped.Count.ToString());
                int total = OduPlanner.Sum(read.Units);
                Check("total index 125", total == 125, total.ToString());
                Check("sources", read.Units[0].Source == "placed" && read.Units[2].Source == "block",
                    read.Units[0].Source + "/" + read.Units[2].Source);

                // Plan, block resolution against the template (no ODU-5 HP there).
                OduPlan plan = OduPlanner.Plan(read.Units.Count, total, 0);
                Check("5 HP by default", plan.Hp == 5, plan.Hp.ToString());
                OduBlockChoice choice = OduPlanner.ResolveBlock(plan.Hp, name => template.HasBlock(name));
                Check("5 HP resolves to ODU-6 HP", choice.BlockName == "ODU-6 HP" && choice.Substitute && !choice.Fallback,
                    choice.BlockName);
                plan = OduPlanner.Plan(read.Units.Count, total, 6);
                OduBlockChoice exact = OduPlanner.ResolveBlock(6, name => template.HasBlock(name));
                Check("6 HP is exact", exact.BlockName == "ODU-6 HP" && !exact.Substitute, exact.BlockName);

                // First write.
                OduWriteRequest request = new OduWriteRequest
                {
                    Hp = plan.Hp, BlockName = exact.BlockName, X = 20000, Y = 5000, Rotation = 0, Plan = plan,
                    LabelText = OduPlanner.LabelText(plan, "RXYQ6T"), WriteLabel = true, TextHeight = 150
                };
                OduWriteResult first = OduWriter.Write(db, db.CurrentSpaceId, request, template);
                Check("first write has a handle", !string.IsNullOrEmpty(first.Handle));
                Check("ODU-6 HP now in the drawing", DrawingHas(db, "ODU-6 HP"));
                int entities = BlockEntityCount(db, "ODU-6 HP");
                Check("block is the template's", entities > 50, entities + " entities");
                int odu, labels;
                CountOdu(db, out odu, out labels);
                Check("one ODU and one label", odu == 1 && labels == 1, odu + "/" + labels);
                Check("ODU tag fields", TagOk(db, first.Handle, plan));
                Check("layers exist", LayerExists(db, OduSettings.UnitLayer) && LayerExists(db, OduSettings.LabelLayer));

                // Replace: pick the written block, write again elsewhere, rotated.
                ObjectId oldId = db.GetObjectId(false, new Handle(Convert.ToInt64(first.Handle, 16)), 0);
                OduPlan plan2 = OduPlanner.Plan(read.Units.Count, total, 8);
                OduBlockChoice choice8 = OduPlanner.ResolveBlock(8, name => template.HasBlock(name));
                request = new OduWriteRequest
                {
                    Hp = 8, BlockName = choice8.BlockName, X = 22000, Y = 5000, Rotation = Math.PI / 2, Plan = plan2,
                    LabelText = OduPlanner.LabelText(plan2, ""), WriteLabel = true, TextHeight = 150, ReplaceId = oldId
                };
                OduWriteResult second = OduWriter.Write(db, db.CurrentSpaceId, request, template);
                Check("replace reported", second.Replaced);
                CountOdu(db, out odu, out labels);
                Check("still one ODU and one label after replace", odu == 1 && labels == 1, odu + "/" + labels);
                Check("new handle", second.Handle != first.Handle);
                Check("replaced tag is 8 HP", TagOk(db, second.Handle, plan2));

                // Fallback block: an HP the template has no block for, drawn under its own name.
                OduPlan big = OduPlanner.Plan(30, 1000, 0);
                OduBlockChoice fb = OduPlanner.ResolveBlock(big.Hp, name => false);
                Check("fallback choice", fb.Fallback && fb.BlockName == "ODU-40 HP", fb.BlockName);
                request = new OduWriteRequest
                {
                    Hp = big.Hp, BlockName = fb.BlockName, Fallback = true, X = 30000, Y = 5000, Rotation = 0, Plan = big,
                    LabelText = OduPlanner.LabelText(big, ""), WriteLabel = true, TextHeight = 150
                };
                OduWriteResult third = OduWriter.Write(db, db.CurrentSpaceId, request, null);
                Check("fallback block drawn", DrawingHas(db, "ODU-40 HP") && BlockEntityCount(db, "ODU-40 HP") >= 4,
                    BlockEntityCount(db, "ODU-40 HP") + " entities");
                CountOdu(db, out odu, out labels);
                Check("two ODUs now", odu == 2 && labels == 2, odu + "/" + labels);
                Log("  notes: " + string.Join(" | ", third.Notes));

                // Riser.
                RiserSettings settings = new RiserSettings();
                List<RiserFloorInput> floors = new List<RiserFloorInput>
                {
                    new RiserFloorInput { Name = "Floor 3", UnitCount = 2, FloorIndex = 100, X = 1000, Y = 80000 },
                    new RiserFloorInput { Name = "Floor 2", UnitCount = 3, FloorIndex = 125, X = 1000, Y = 40000 },
                    new RiserFloorInput { Name = "Floor 1", UnitCount = 4, FloorIndex = 140, X = 1000, Y = 0 }
                };
                RiserPlan riser = RiserPlanner.Plan("R1", floors, 0, settings.FloorHeight, false);
                RiserWriteResult r1 = RiserWriter.Write(db, db.CurrentSpaceId, riser, settings, 5000, 90000);
                Check("3 markers", r1.Markers == 3, r1.Markers.ToString());
                Log("  schedule as table: " + r1.UsedTable + "  notes: " + string.Join(" | ", r1.Notes));
                int circles, texts, schedules;
                CountRiser(db, "R1", out circles, out texts, out schedules);
                Check("3 circles, 3 texts, 1 schedule", circles == 3 && texts == 3 && schedules == 1,
                    circles + "/" + texts + "/" + schedules);
                Check("riser layer", LayerExists(db, RiserSettings.Layer));
                Check("riser tag contents", RiserTagOk(db, "R1", "Floor 2", 1, 125, 225, "22.2/9.5"));
                Check("main segment tag", RiserTagOk(db, "R1", "Floor 1", 2, 140, 365, "28.6/12.7"));
                Check("marker text", AnyMText(db, "Riser R1 · Floor 2 · %%C22.2/9.5 · Σ225"));

                RiserWriteResult r2 = RiserWriter.Write(db, db.CurrentSpaceId, riser, settings, 5000, 90000);
                Check("rerun erases the earlier set", r2.Erased == 7, r2.Erased.ToString());
                CountRiser(db, "R1", out circles, out texts, out schedules);
                Check("rerun leaves one set", circles == 3 && texts == 3 && schedules == 1, circles + "/" + texts + "/" + schedules);

                RiserPlan other = RiserPlanner.Plan("R2", floors, 0, settings.FloorHeight, false);
                RiserWriter.Write(db, db.CurrentSpaceId, other, settings, 15000, 90000);
                CountRiser(db, "R1", out circles, out texts, out schedules);
                Check("other riser name keeps R1", circles == 3 && texts == 3 && schedules == 1, circles + "/" + texts + "/" + schedules);
                CountRiser(db, "R2", out circles, out texts, out schedules);
                Check("R2 written", circles == 3 && texts == 3 && schedules == 1, circles + "/" + texts + "/" + schedules);
            }
        }

        private static ObjectId Insert(
            Transaction transaction, Database db, BlockTableRecord space, ObjectId definition,
            double x, double y, int index, double kw, bool tag)
        {
            BlockReference reference = new BlockReference(new Point3d(x, y, 0), definition);
            reference.SetDatabaseDefaults(db);
            space.AppendEntity(reference);
            transaction.AddNewlyCreatedDBObject(reference, true);
            if (tag)
            {
                using (ResultBuffer buffer = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, IndoorCapacity.PlacedApp),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, "R1"),
                    new TypedValue((int)DxfCode.ExtendedDataInteger32, index),
                    new TypedValue((int)DxfCode.ExtendedDataReal, kw),
                    new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)1),
                    new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)1)))
                {
                    reference.XData = buffer;
                }
            }

            return reference.ObjectId;
        }

        private static bool DrawingHas(Database db, string block)
        {
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                bool has = ((BlockTable)transaction.GetObject(db.BlockTableId, OpenMode.ForRead)).Has(block);
                transaction.Commit();
                return has;
            }
        }

        private static bool LayerExists(Database db, string layer)
        {
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                bool has = ((LayerTable)transaction.GetObject(db.LayerTableId, OpenMode.ForRead)).Has(layer);
                transaction.Commit();
                return has;
            }
        }

        private static int BlockEntityCount(Database db, string block)
        {
            int count = 0;
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)transaction.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (table.Has(block))
                {
                    BlockTableRecord record = (BlockTableRecord)transaction.GetObject(table[block], OpenMode.ForRead);
                    foreach (ObjectId unused in record)
                        count++;
                }

                transaction.Commit();
            }

            return count;
        }

        private static void CountOdu(Database db, out int odu, out int labels)
        {
            odu = 0;
            labels = 0;
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    Entity entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
                    if (entity == null || entity.IsErased)
                        continue;
                    if (entity is BlockReference && VrvUnitReader.HasApp(entity, OduSettings.OduApp))
                        odu++;
                    else if (entity is MText && VrvUnitReader.HasApp(entity, OduSettings.LabelApp))
                        labels++;
                }

                transaction.Commit();
            }
        }

        private static bool TagOk(Database db, string handle, OduPlan plan)
        {
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                ObjectId id = db.GetObjectId(false, new Handle(Convert.ToInt64(handle, 16)), 0);
                BlockReference reference = (BlockReference)transaction.GetObject(id, OpenMode.ForRead);
                IList<object> v = VrvUnitReader.ReadValues(reference, OduSettings.OduApp);
                bool ok = reference.Layer == OduSettings.UnitLayer && v.Count == 6
                    && (short)v[1] == plan.Hp && (int)v[2] == plan.OduIndex && (int)v[3] == plan.IndoorTotal
                    && Math.Abs((double)v[4] - plan.Ratio) < 1e-6 && (short)v[5] == plan.UnitCount;
                if (!ok)
                    Log("  tag values: " + string.Join(",", v));
                transaction.Commit();
                return ok;
            }
        }

        private static void CountRiser(Database db, string name, out int circles, out int texts, out int schedules)
        {
            circles = 0;
            texts = 0;
            schedules = 0;
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    Entity entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
                    if (entity == null || entity.IsErased)
                        continue;
                    IList<object> v = VrvUnitReader.ReadValues(entity, RiserSettings.RiserApp);
                    if (v.Count < 7 || !string.Equals(v[1] as string, name, StringComparison.Ordinal))
                        continue;
                    if (entity.Layer != RiserSettings.Layer)
                        continue;
                    if ((string)v[2] == "SCHEDULE")
                        schedules++;
                    else if (entity is Circle)
                        circles++;
                    else if (entity is MText)
                        texts++;
                }

                transaction.Commit();
            }
        }

        private static bool RiserTagOk(Database db, string name, string floor, int order, int floorIndex, int cumulative, string pair)
        {
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    Entity entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
                    if (!(entity is Circle) || entity.IsErased)
                        continue;
                    IList<object> v = VrvUnitReader.ReadValues(entity, RiserSettings.RiserApp);
                    if (v.Count == 7 && (string)v[1] == name && (string)v[2] == floor)
                    {
                        bool ok = (short)v[3] == order && (int)v[4] == floorIndex && (int)v[5] == cumulative && (string)v[6] == pair;
                        if (!ok)
                            Log("  riser tag: " + string.Join(",", v));
                        transaction.Commit();
                        return ok;
                    }
                }

                transaction.Commit();
                return false;
            }
        }

        private static bool AnyMText(Database db, string contents)
        {
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    MText text = transaction.GetObject(id, OpenMode.ForRead) as MText;
                    if (text != null && !text.IsErased && text.Contents == contents)
                    {
                        transaction.Commit();
                        return true;
                    }
                }

                transaction.Commit();
                return false;
            }
        }
    }
}
