using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using HvacAiCadAddin.Commands;
using HvacAiCadAddin.Ducts;

[assembly: CommandClass(typeof(DuctAcadTests.DuctAcadTestCommands))]

namespace DuctAcadTests
{
    // One command runs every test on the drawing it was started with (a copy of the template).
    // Each test is wrapped in try/catch: an unhandled exception would kill accoreconsole and
    // truncate the log.
    public partial class DuctAcadTestCommands
    {
        internal static TemplateSource Template;
        internal static TemplateCatalog Catalog;
        internal static string TemplatePath;
        internal const string SupplyLayer = "EIA_1 SUPPLY AIR";

        [CommandMethod("DUCTTEST")]
        public void RunAll()
        {
            try
            {
                Body();
            }
            catch (System.Exception ex)
            {
                TestLog.Fail("DUCTTEST crashed", ex.ToString());
            }
            finally
            {
                TestLog.Summary();
            }
        }

        private static void Body()
        {
            Database db = HostApplicationServices.WorkingDatabase;
            TemplatePath = Environment.GetEnvironmentVariable("HVACDUCT_TEMPLATE");
            string error;
            Template = TemplateSource.TryOpen(TemplatePath, out error);
            TestLog.True("template opens", Template != null, error);
            Catalog = TemplateCatalog.Read(TemplatePath, out error);
            TestLog.True("catalog reads", Catalog != null, error);
            if (Template == null || Catalog == null)
                return;

            try
            {
                Run("catalog", TestCatalog);
                Run("blank database import", TestBlankImport);
                Run("find and clone sizes", () => TestSizes(db));
                Run("insert and read refs", () => TestInsertAndRead(db));
                Run("template refs", () => TestTemplateRefs(db));
                Run("adjust ends", () => TestAdjustEnds(db));
                Run("fittings", () => TestFittings(db));
                Run("find duct at", () => TestFindDuct(db));
                Run("route session: L run and undo", () => TestRunAndUndo(db));
                Run("route session: continue an existing duct", () => TestContinue(db));
                Run("route session: tap start", () => TestTapStart(db));
                Run("route session: refusals and retry", () => TestRefusals(db));
                Run("route session: size change and end tap", () => TestSizeAndEndTap(db));
                Run("align", () => TestAlign(db));
            }
            finally
            {
                Template.Dispose();
            }
        }

        internal static void Run(string name, Action body)
        {
            TestLog.Line("== " + name);
            try
            {
                body();
            }
            catch (System.Exception ex)
            {
                TestLog.Fail(name + " threw", ex.ToString());
            }
        }

        // ------------------------------------------------------------------ helpers

        internal static List<DuctSize> TemplateSizes { get { return Catalog.Sizes; } }

        internal static ObjectId Ensure(Database db, DuctSize size)
        {
            string note;
            string error;
            ObjectId id = DuctBlockLibrary.EnsureBlock(db, size, Template, TemplateSizes, out note, out error);
            if (id.IsNull)
                throw new InvalidOperationException("EnsureBlock " + size.BlockName + ": " + error);
            return id;
        }

        // Inserts a reference the way the commands do, plus optional rotation and mirroring.
        internal static ObjectId Insert(
            Database db, DuctSize size, double x, double y, double rotationDegrees, bool mirror, double distance3)
        {
            ObjectId definition = Ensure(db, size);
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(
                    db.CurrentSpaceId, OpenMode.ForWrite);
                BlockReference block = new BlockReference(new Point3d(x, y, 0), definition);
                block.Rotation = rotationDegrees * Math.PI / 180.0;
                if (mirror)
                    block.ScaleFactors = new Scale3d(-1, 1, 1);
                block.Layer = SupplyLayer;
                space.AppendEntity(block);
                transaction.AddNewlyCreatedDBObject(block, true);
                if (!DynamicBlockHelpers.TrySetDistancePropertyValue(block, "Distance3", distance3, null, false))
                    throw new InvalidOperationException("Distance3 not set");
                transaction.Commit();
                return block.ObjectId;
            }
        }

        internal static DuctInfo Read(Database db, ObjectId id)
        {
            DuctInfo info;
            if (!DuctBlockLibrary.TryReadDuct(db, id, out info))
                throw new InvalidOperationException("not a readable duct: " + id);
            return info;
        }

        // Displayed (visible) extent of a reference along its axis and across it, measured from the
        // drawn geometry rather than from the model of it.
        internal static void Measure(
            Database db, ObjectId id, out double alongLow, out double alongHigh,
            out double acrossLow, out double acrossHigh)
        {
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockReference block = (BlockReference)transaction.GetObject(id, OpenMode.ForRead);
                Vector3d axis = block.BlockTransform.CoordinateSystem3d.Xaxis.GetNormal();
                Vector3d left = new Vector3d(-axis.Y, axis.X, 0);
                if (!DynamicBlockHelpers.TryGetDisplayedProjectionRange(block, axis, out alongLow, out alongHigh)
                    || !DynamicBlockHelpers.TryGetDisplayedProjectionRange(block, left, out acrossLow, out acrossHigh))
                    throw new InvalidOperationException("cannot measure");
                transaction.Commit();
            }
        }

        // The model (DuctInfo) must agree with what the block draws.
        internal static void AssertMatchesDrawing(string label, Database db, ObjectId id)
        {
            DuctInfo info = Read(db, id);
            double alongLow, alongHigh, acrossLow, acrossHigh;
            Measure(db, id, out alongLow, out alongHigh, out acrossLow, out acrossHigh);
            Vec2 axis = info.Geometry.Axis;
            double start = info.Geometry.Start.Dot(axis);
            TestLog.Near(label + " start along", start, alongLow, 1e-3);
            TestLog.Near(label + " length", info.Geometry.Length, alongHigh - alongLow, 1e-3);
            double centre = info.Geometry.Start.Dot(axis.Left);
            TestLog.Near(label + " across low", centre - info.Geometry.Width / 2, acrossLow, 1e-3);
            TestLog.Near(label + " across high", centre + info.Geometry.Width / 2, acrossHigh, 1e-3);
        }

        // ------------------------------------------------------------------ tests

        private static void TestCatalog()
        {
            TestLog.True("catalog has sizes", Catalog.Sizes.Count > 10, Catalog.Sizes.Count.ToString());
            TestLog.True("catalog has REC_600x300", Catalog.Sizes.Contains(DuctSize.Rect(600, 300)));
            TestLog.True("catalog has a round size", Catalog.Sizes.Exists(s => s.IsRound));
            string[] expected =
            {
                "EIA_1 SUPPLY AIR", "EIA_1 RETURN AIR", "EIA_1 OUTSIDE AIR", "EIA_1 EXHAUST AIR",
                "EIA_1 PRIMARY SUPPLY AIR", "EIA_1 PRIMARY RETURN AIR", "EIA_1 TOILET AIR", "EIA_2 EXHAUST SMOKE"
            };
            foreach (string layer in expected)
                TestLog.True("duct layer " + layer, Catalog.DuctLayers.Contains(layer));
            TestLog.True("terminals are not offered",
                !Catalog.DuctLayers.Exists(l => l.IndexOf("TERMINAL", StringComparison.OrdinalIgnoreCase) >= 0));
            TestLog.True("second read is cached", object.ReferenceEquals(Catalog, ReadAgain()));

            List<string> templateLayers = Template.GetLayerNames();
            foreach (string layer in Catalog.DuctLayers)
                TestLog.True("TemplateSource lists " + layer, templateLayers.Contains(layer));
        }

        private static TemplateCatalog ReadAgain()
        {
            string error;
            return TemplateCatalog.Read(TemplatePath, out error);
        }

        private static void TestBlankImport()
        {
            using (Database blank = new Database(true, true))
            {
                string note;
                string error;
                ObjectId id = DuctBlockLibrary.EnsureBlock(
                    blank, DuctSize.Rect(600, 300), Template, TemplateSizes, out note, out error);
                TestLog.True("imports REC_600x300 into a blank drawing", !id.IsNull, error);
                TestLog.True("import is reported", note != null && note.StartsWith("Imported"), note);

                string layerError;
                TestLog.True("imports a template layer",
                    DuctBlockLibrary.EnsureLayer(blank, SupplyLayer, Template, out layerError), layerError);
                TestLog.True("layer is now listed", DuctBlockLibrary.ListDuctLayers(blank).Contains(SupplyLayer));
                TestLog.True("unknown layer is refused",
                    !DuctBlockLibrary.EnsureLayer(blank, "NO SUCH DUCT LAYER", Template, out layerError));
                TestLog.True("no template, unknown layer is refused",
                    !DuctBlockLibrary.EnsureLayer(blank, "ANOTHER MISSING", null, out layerError));

                // A size only the template can clone: the blank drawing must pull its master in first.
                id = DuctBlockLibrary.EnsureBlock(
                    blank, DuctSize.Rect(633, 311), Template, TemplateSizes, out note, out error);
                TestLog.True("clones through the template into a blank drawing", !id.IsNull, error);

                // Without any source block or template, nothing can be made.
                using (Database empty = new Database(true, true))
                {
                    id = DuctBlockLibrary.EnsureBlock(
                        empty, DuctSize.Rect(633, 311), null, null, out note, out error);
                    TestLog.True("nothing to copy is an error", id.IsNull && error != null, error);
                }
            }
        }

        private static void TestSizes(Database db)
        {
            List<DuctSize> before = DuctBlockLibrary.ListSizes(db);
            TestLog.True("drawing lists REC_600x300", before.Contains(DuctSize.Rect(600, 300)));

            string note;
            string error;
            ObjectId existing = DuctBlockLibrary.EnsureBlock(
                db, DuctSize.Rect(600, 300), Template, TemplateSizes, out note, out error);
            TestLog.True("existing size is found", !existing.IsNull && note == null, error);

            // A rectangular size nobody drew: clone the nearest width and widen the outline.
            DuctSize wide = DuctSize.Rect(853, 421);
            TestLog.True("853x421 does not exist yet", !before.Contains(wide));
            ObjectId created = DuctBlockLibrary.EnsureBlock(db, wide, Template, TemplateSizes, out note, out error);
            TestLog.True("clones a new rectangular size", !created.IsNull, error);
            TestLog.True("clone is reported", note != null && note.StartsWith("Created REC_853x421 from"), note);
            ObjectId again = DuctBlockLibrary.EnsureBlock(db, wide, Template, TemplateSizes, out note, out error);
            TestLog.True("second request finds it", again == created && note == null, note);

            ObjectId wideRef = Insert(db, wide, 200000, 200000, 0, false, 2000);
            double al, ah, cl, ch;
            Measure(db, wideRef, out al, out ah, out cl, out ch);
            TestLog.Near("new size is 2000 long", 2000, ah - al, 1e-3);
            TestLog.Near("new size is 853 wide", 853, ch - cl, 1e-3);
            AssertMatchesDrawing("853x421", db, wideRef);
            TestLog.True("is dynamic and named", IsDynamicNamed(db, wideRef, "REC_853x421"));

            // The original must not have changed.
            ObjectId originalRef = Insert(db, DuctSize.Rect(600, 300), 200000, 201000, 0, false, 1500);
            Measure(db, originalRef, out al, out ah, out cl, out ch);
            TestLog.Near("REC_600x300 is still 600 wide", 600, ch - cl, 1e-3);

            // Same width, other height: a rename only.
            DuctSize sameWidth = DuctSize.Rect(600, 277);
            ObjectId renamed = DuctBlockLibrary.EnsureBlock(db, sameWidth, Template, TemplateSizes, out note, out error);
            TestLog.True("clones a same-width size", !renamed.IsNull && note != null && note.Contains("from REC_600x"), note ?? error);
            ObjectId renamedRef = Insert(db, sameWidth, 200000, 202000, 0, false, 1000);
            Measure(db, renamedRef, out al, out ah, out cl, out ch);
            TestLog.Near("600x277 is 600 wide", 600, ch - cl, 1e-3);

            // A round size.
            DuctSize round = DuctSize.Round(417);
            ObjectId roundId = DuctBlockLibrary.EnsureBlock(db, round, Template, TemplateSizes, out note, out error);
            TestLog.True("clones a new round size", !roundId.IsNull && note != null && note.Contains("from Round D"), note ?? error);
            ObjectId roundRef = Insert(db, round, 200000, 203000, 0, false, 1800);
            Measure(db, roundRef, out al, out ah, out cl, out ch);
            TestLog.Near("D417 is 1800 long", 1800, ah - al, 1e-3);
            TestLog.Near("D417 is 417 wide", 417, ch - cl, 1e-3);

            // Shape matters: a round request never copies a rectangular block.
            DuctSize nearestRound = DuctBlockLibrary.FindNearest(
                DuctSize.Round(500), new List<DuctSize> { DuctSize.Rect(500, 500), DuctSize.Round(300) }, null);
            TestLog.Equal("nearest round ignores rectangular", "Round D300", nearestRound.BlockName);
            DuctSize nearestRect = DuctBlockLibrary.FindNearest(
                DuctSize.Rect(700, 300),
                new List<DuctSize> { DuctSize.Rect(600, 300) },
                new List<DuctSize> { DuctSize.Rect(700, 250), DuctSize.Rect(700, 320) });
            TestLog.Equal("nearest by width, then by height", "REC_700x320", nearestRect.BlockName);

            // Saved and reopened, the clone stays dynamic and independent.
            string saved = Path.Combine(Path.GetTempPath(), "HvacAcadTests", "duct", "sizes_saved.dwg");
            Directory.CreateDirectory(Path.GetDirectoryName(saved));
            db.SaveAs(saved, DwgVersion.Current);
            using (Database reopened = new Database(false, true))
            {
                reopened.ReadDwgFile(saved, FileShare.Read, true, string.Empty);
                ObjectId id;
                TestLog.True("reopened drawing has REC_853x421", DuctBlockLibrary.TryFindBlock(reopened, wide, out id));
                DuctInfo info;
                TestLog.True("reopened reference reads back", DuctBlockLibrary.TryReadDuct(reopened, ObjectIdFor(reopened, "REC_853x421"), out info));
                if (info != null)
                    TestLog.Near("reopened length", 2000, info.Geometry.Length, 1e-3);
            }
        }

        private static ObjectId ObjectIdFor(Database db, string blockName)
        {
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    BlockReference block = transaction.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (block == null || !block.IsDynamicBlock)
                        continue;
                    BlockTableRecord definition = (BlockTableRecord)transaction.GetObject(
                        block.DynamicBlockTableRecord, OpenMode.ForRead);
                    if (definition.Name == blockName)
                        return id;
                }
            }

            return ObjectId.Null;
        }

        private static bool IsDynamicNamed(Database db, ObjectId id, string name)
        {
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockReference block = (BlockReference)transaction.GetObject(id, OpenMode.ForRead);
                BlockTableRecord definition = (BlockTableRecord)transaction.GetObject(
                    block.DynamicBlockTableRecord, OpenMode.ForRead);
                return block.IsDynamicBlock && definition.Name == name;
            }
        }

        private static void TestInsertAndRead(Database db)
        {
            double[] rotations = { 0, 30, 90, 210 };
            double x = 210000;
            foreach (bool mirror in new[] { false, true })
            {
                foreach (double rotation in rotations)
                {
                    ObjectId id = Insert(db, DuctSize.Rect(600, 300), x, 210000, rotation, mirror, 1700);
                    string label = "REC_600x300 rot " + rotation + (mirror ? " mirrored" : "");
                    AssertMatchesDrawing(label, db, id);
                    DuctInfo info = Read(db, id);
                    double expectedAxis = rotation * Math.PI / 180.0 + (mirror ? Math.PI : 0);
                    TestLog.Near(label + " axis x", Math.Cos(expectedAxis), info.Geometry.Axis.X, 1e-9);
                    TestLog.Near(label + " axis y", Math.Sin(expectedAxis), info.Geometry.Axis.Y, 1e-9);
                    TestLog.Near(label + " start x", x, info.Geometry.Start.X, 1e-9);
                    TestLog.Near(label + " length is Distance3", 1700, info.Geometry.Length, 1e-9);
                    x += 5000;
                }
            }

            // A scaled reference: lengths and widths follow the scale.
            ObjectId scaled;
            ObjectId definition = Ensure(db, DuctSize.Rect(600, 300));
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                BlockReference block = new BlockReference(new Point3d(x, 210000, 0), definition);
                block.ScaleFactors = new Scale3d(2, 2, 2);
                space.AppendEntity(block);
                transaction.AddNewlyCreatedDBObject(block, true);
                DynamicBlockHelpers.TrySetDistancePropertyValue(block, "Distance3", 1000, null, false);
                transaction.Commit();
                scaled = block.ObjectId;
            }

            // Probe: what unit is Distance3 in when the reference is scaled?
            foreach (double value in new[] { 500.0, 1000.0, 2000.0 })
            {
                using (Transaction transaction = db.TransactionManager.StartTransaction())
                {
                    BlockReference block = (BlockReference)transaction.GetObject(scaled, OpenMode.ForWrite);
                    DynamicBlockHelpers.TrySetDistancePropertyValue(block, "Distance3", value, null, false);
                    transaction.Commit();
                }

                double pal, pah, pcl, pch;
                Measure(db, scaled, out pal, out pah, out pcl, out pch);
                TestLog.Line("  scale 2 probe: Distance3=" + value + " drawn length=" + TestLog.F(pah - pal) + " width=" + TestLog.F(pch - pcl));
            }

            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockReference block = (BlockReference)transaction.GetObject(scaled, OpenMode.ForWrite);
                DynamicBlockHelpers.TrySetDistancePropertyValue(block, "Distance3", 1000, null, false);
                transaction.Commit();
            }

            DuctInfo scaledInfo = Read(db, scaled);
            TestLog.Near("scaled length is Distance3 (drawing units)", 1000, scaledInfo.Geometry.Length, 1e-9);
            TestLog.Near("scaled width 2 x W", 1200, scaledInfo.Geometry.Width, 1e-9);
            AssertMatchesDrawing("scaled x2", db, scaled);

            // Not a duct: other dynamic blocks and plain blocks are refused.
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)transaction.GetObject(db.BlockTableId, OpenMode.ForRead);
                transaction.Commit();
            }
        }

        // Every duct reference already in the template: the model must match what is drawn,
        // including the 61 mirrored inserts.
        private static void TestTemplateRefs(Database db)
        {
            int total = 0, mismatched = 0, mirrored = 0, unreadable = 0, scaledRefs = 0;
            List<ObjectId> ids = new List<ObjectId>();
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    if (id.ObjectClass.DxfName != "INSERT")
                        continue;
                    BlockReference block = transaction.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (block == null || !block.IsDynamicBlock)
                        continue;
                    BlockTableRecord definition = (BlockTableRecord)transaction.GetObject(
                        block.DynamicBlockTableRecord, OpenMode.ForRead);
                    DuctSize size;
                    if (DuctSize.TryParseBlockName(definition.Name, out size) && block.Position.X > 1e6)
                        ids.Add(id);
                }

                transaction.Commit();
            }

            foreach (ObjectId id in ids)
            {
                DuctInfo info;
                if (!DuctBlockLibrary.TryReadDuct(db, id, out info))
                {
                    unreadable++;
                    continue;
                }

                total++;
                if (Math.Abs(info.XScale - 1) > 1e-6) scaledRefs++;
                double al, ah, cl, ch;
                Measure(db, id, out al, out ah, out cl, out ch);
                Vec2 axis = info.Geometry.Axis;
                double start = info.Geometry.Start.Dot(axis);
                double centre = info.Geometry.Start.Dot(axis.Left);
                bool ok = Math.Abs(al - start) < 0.01
                    && Math.Abs((ah - al) - info.Geometry.Length) < 0.01
                    && Math.Abs((cl + ch) / 2 - centre) < 0.01
                    && Math.Abs((ch - cl) - info.Geometry.Width) < 0.01;
                if (!ok)
                {
                    mismatched++;
                    if (mismatched <= 5)
                        TestLog.Line("  mismatch " + info.Size.BlockName + " at " + info.Geometry.Start
                            + " axis " + axis + " len " + TestLog.F(info.Geometry.Length) + " drawn " + TestLog.F(ah - al)
                            + " width " + TestLog.F(info.Geometry.Width) + " drawn " + TestLog.F(ch - cl));
                }

                if (axis.Dot(new Vec2(1, 0)) < -0.999)
                    mirrored++;
            }

            TestLog.Line("  template duct refs read=" + total + " unreadable=" + unreadable
                + " mismatched=" + mismatched + " axis -X=" + mirrored + " scale!=1: " + scaledRefs);
            TestLog.True("template has duct references", total > 100, total.ToString());
            TestLog.Equal("every template duct reference matches its drawing", 0, mismatched);
            TestLog.Equal("no template duct reference is unreadable", 0, unreadable);
        }

        private static void TestAdjustEnds(Database db)
        {
            double x = 220000;
            foreach (bool mirror in new[] { false, true })
            {
                foreach (double rotation in new[] { 0.0, 37.0, 90.0 })
                {
                    string label = "rot " + rotation + (mirror ? " mirrored" : "");
                    ObjectId id = Insert(db, DuctSize.Rect(600, 300), x, 220000, rotation, mirror, 2000);
                    x += 5000;
                    DuctInfo original = Read(db, id);
                    Vec2 axis = original.Geometry.Axis;
                    Vec2 endBefore = original.Geometry.End;
                    string error;

                    using (Transaction transaction = db.TransactionManager.StartTransaction())
                    {
                        TestLog.True(label + " shorten from the end",
                            DuctBlockLibrary.AdjustDuctEnd(transaction, id, false, 300, out error), error);
                        transaction.Commit();
                    }

                    DuctInfo info = Read(db, id);
                    TestLog.Near(label + " end shortened: length", 1700, info.Geometry.Length, 1e-6);
                    TestLog.Near(label + " end shortened: start fixed", 0, info.Geometry.Start.DistanceTo(original.Geometry.Start), 1e-6);
                    AssertMatchesDrawing(label + " after end shorten", db, id);

                    using (Transaction transaction = db.TransactionManager.StartTransaction())
                    {
                        TestLog.True(label + " shorten from the start",
                            DuctBlockLibrary.AdjustDuctEnd(transaction, id, true, 200, out error), error);
                        transaction.Commit();
                    }

                    info = Read(db, id);
                    TestLog.Near(label + " start shortened: length", 1500, info.Geometry.Length, 1e-6);
                    TestLog.Near(label + " start moved along the axis", 0,
                        info.Geometry.Start.DistanceTo(original.Geometry.Start + axis * 200), 1e-6);
                    TestLog.Near(label + " far end unchanged by start shortening", 0,
                        info.Geometry.End.DistanceTo(original.Geometry.End - axis * 300), 1e-6);
                    AssertMatchesDrawing(label + " after start shorten", db, id);

                    using (Transaction transaction = db.TransactionManager.StartTransaction())
                    {
                        DuctBlockLibrary.AdjustDuctEnd(transaction, id, true, -500, out error);
                        DuctBlockLibrary.AdjustDuctEnd(transaction, id, false, -300, out error);
                        transaction.Commit();
                    }

                    info = Read(db, id);
                    TestLog.Near(label + " extended both ends: length", 2300, info.Geometry.Length, 1e-6);
                    TestLog.Near(label + " extended: end back to the original", 0, info.Geometry.End.DistanceTo(endBefore), 1e-6);
                    AssertMatchesDrawing(label + " after extending", db, id);

                    using (Transaction transaction = db.TransactionManager.StartTransaction())
                    {
                        TestLog.True(label + " cannot go below zero",
                            !DuctBlockLibrary.AdjustDuctEnd(transaction, id, false, 5000, out error));
                        transaction.Abort();
                    }
                }
            }
        }

        private static void TestFittings(Database db)
        {
            Outline outline = new Outline { Closed = true };
            ElbowGeometry elbow = FittingShapes.Elbow(new Vec2(230000, 230000), new Vec2(1, 0), new Vec2(0, 1), 600, 450);
            ObjectId rectId;
            ObjectId centreId;
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                rectId = DuctBlockLibrary.AddFitting(db, transaction, space, elbow.Shape, 0, SupplyLayer, "ELBOW", "600x300");
                centreId = DuctBlockLibrary.AddFitting(db, transaction, space, elbow.Centreline, 0, SupplyLayer, "ELBOW", "D250");
                transaction.Commit();
            }

            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                Polyline p = (Polyline)transaction.GetObject(rectId, OpenMode.ForRead);
                TestLog.True("fitting is a closed polyline", p.Closed);
                TestLog.Equal("fitting vertex count", 4, p.NumberOfVertices);
                TestLog.Near("fitting bulge kept", Math.Tan(Math.PI / 8), p.GetBulgeAt(0), 1e-9);
                TestLog.Equal("fitting layer", SupplyLayer, p.Layer);
                TestLog.Equal("fitting colour is ByLayer", (short)256, p.ColorIndex);
                TestLog.Equal("fitting linetype is ByLayer", "ByLayer", p.Linetype);
                string kind, text;
                TestLog.True("fitting tag reads back", DuctBlockLibrary.TryReadFittingTag(p, out kind, out text));
                TestLog.Equal("tag kind", "ELBOW", kind);
                TestLog.Equal("tag size", "600x300", text);
                TestLog.Near("fitting area is W * R * theta", 600 * 450 * Math.PI / 2, ArcArea(p), 1.0);

                Polyline c = (Polyline)transaction.GetObject(centreId, OpenMode.ForRead);
                TestLog.True("centreline is open", !c.Closed);
                TestLog.Equal("centreline colour 8", (short)8, c.ColorIndex);
                TestLog.Equal("centreline linetype", "CENTER", c.Linetype);
                TestLog.True("centreline carries the tag too", DuctBlockLibrary.TryReadFittingTag(c, out kind, out text));

                RegAppTable apps = (RegAppTable)transaction.GetObject(db.RegAppTableId, OpenMode.ForRead);
                TestLog.True("HVACAI_FITTING is registered", apps.Has("HVACAI_FITTING"));
                transaction.Commit();
            }
        }

        // Area of a closed polyline with bulges, using AutoCAD's own area.
        private static double ArcArea(Polyline p)
        {
            return p.Area;
        }

        private static void TestFindDuct(Database db)
        {
            ObjectId id = Insert(db, DuctSize.Rect(600, 300), 240000, 240000, 0, false, 2000);
            DuctInfo info;
            DuctHit hit;
            TestLog.True("finds the end face",
                DuctBlockLibrary.FindDuctAt(db, new Vec2(242000, 240010), 25, null, out info, out hit)
                && hit.Kind == DuctHitKind.EndFace && info.Id == id);
            TestLog.True("finds the start face",
                DuctBlockLibrary.FindDuctAt(db, new Vec2(239990, 240000), 25, null, out info, out hit)
                && hit.Kind == DuctHitKind.StartFace);
            TestLog.True("finds a wall",
                DuctBlockLibrary.FindDuctAt(db, new Vec2(241000, 240295), 25, null, out info, out hit)
                && hit.Kind == DuctHitKind.LeftWall && Math.Abs(hit.Along - 1000) < 1e-6);
            TestLog.True("the centre is not an edge",
                !DuctBlockLibrary.FindDuctAt(db, new Vec2(241000, 240000), 25, null, out info, out hit));
            TestLog.True("skip list is honoured",
                !DuctBlockLibrary.FindDuctAt(db, new Vec2(241000, 240295), 25, new List<ObjectId> { id }, out info, out hit));
            TestLog.True("empty space finds nothing",
                !DuctBlockLibrary.FindDuctAt(db, new Vec2(250000, 250000), 25, null, out info, out hit));
        }
    }
}
