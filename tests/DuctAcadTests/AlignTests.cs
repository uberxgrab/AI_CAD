using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using HvacAiCadAddin.Ducts;

namespace DuctAcadTests
{
    // HVACDUCTALIGN without prompts: edges come from the entity inside the block (the same call the
    // nested-pick command makes), then Plan and Apply.
    public partial class DuctAcadTestCommands
    {
        // Picks the visible outline's segment nearest a world point, as GetNestedEntity would.
        private static bool PickEdge(
            Database db, ObjectId id, double px, double py, out Vec2 from, out Vec2 to, out string error)
        {
            from = new Vec2();
            to = new Vec2();
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockReference block = (BlockReference)transaction.GetObject(id, OpenMode.ForRead);
                BlockTableRecord record = (BlockTableRecord)transaction.GetObject(
                    block.BlockTableRecord, OpenMode.ForRead);
                foreach (ObjectId entityId in record)
                {
                    Polyline outline = transaction.GetObject(entityId, OpenMode.ForRead) as Polyline;
                    if (outline == null || !outline.Visible || !outline.Closed)
                        continue;
                    bool ok = DuctAligner.TryGetEdge(
                        transaction, entityId, block.BlockTransform, new Point3d(px, py, 0),
                        out from, out to, out error);
                    transaction.Commit();
                    return ok;
                }
            }

            error = "no outline";
            return false;
        }

        private static AlignPlan PlanFor(
            Database db, ObjectId aId, double ax, double ay, ObjectId bId, double bx, double by)
        {
            Vec2 refFrom, refTo, edgeFrom, edgeTo;
            string error;
            if (!PickEdge(db, aId, ax, ay, out refFrom, out refTo, out error))
                throw new InvalidOperationException("reference pick: " + error);
            if (!PickEdge(db, bId, bx, by, out edgeFrom, out edgeTo, out error))
                throw new InvalidOperationException("edge pick: " + error);
            return DuctAligner.Plan(
                refFrom, refTo, Read(db, aId), Read(db, bId), edgeFrom, edgeTo, new FittingSettings());
        }

        private static void TestAlign(Database db)
        {
            string message;

            // --- Edge extraction on a rotated and a mirrored duct
            ObjectId rotated = Insert(db, W600, 300000, 130000, 90, false, 2000);
            Vec2 from, to;
            string error;
            // A is at x = 300000 running +Y (rotation 90): its walls are x = 299700 and 300300.
            TestLog.True("edge of a rotated duct", PickEdge(db, rotated, 300302, 131000, out from, out to, out error), error);
            TestLog.Near("rotated wall is x = 300300 (from)", 300300, from.X, 1e-6);
            TestLog.Near("rotated wall is x = 300300 (to)", 300300, to.X, 1e-6);
            TestLog.Near("rotated wall runs along the duct", 2000, Math.Abs(to.Y - from.Y), 1e-6);

            // A stays 300000..302000 at y = 100000 for the cases below that only read it. Each
            // same-size join gets its own A, because a merge stretches A over B and erases B.
            ObjectId a = Insert(db, W600, 300000, 100000, 0, false, 2000);

            // --- Direct join, B beyond A's end, same direction, 40 mm off: one block 300000..305500
            ObjectId a1 = Insert(db, W600, 300000, 140000, 0, false, 2000);
            ObjectId b = Insert(db, W600, 303500, 140040, 0, false, 2000);
            AlignPlan plan = PlanFor(db, a1, 300500, 140300, b, 304000, 140340);
            TestLog.True("plan has no error", plan.Error == null, plan.Error);
            TestLog.Near("translation x", 0, plan.Translation.X, 1e-6);
            TestLog.Near("translation y", -40, plan.Translation.Y, 1e-6);
            TestLog.Equal("join is direct", JoinKind.Direct, plan.Join.Kind);
            TestLog.True("same size merges", plan.Merge);
            TestLog.True("apply with join", DuctAligner.Apply(db, plan, true, out message), message);
            TestLog.True("B is erased", b.IsErased);
            DuctInfo joined = Read(db, a1);
            TestLog.Near("merged start x is A's start", 300000, joined.Geometry.Start.X, 1e-6);
            TestLog.Near("merged start y", 140000, joined.Geometry.Start.Y, 1e-6);
            TestLog.Near("merged length reaches B's end", 5500, joined.Geometry.Length, 1e-6);
            AssertMatchesDrawing("merged A", db, a1);

            // --- Alignment only (no join)
            ObjectId b2 = Insert(db, W600, 303500, 100040, 0, false, 2000);
            plan = PlanFor(db, a, 300500, 100300, b2, 304000, 100340);
            TestLog.True("apply without join", DuctAligner.Apply(db, plan, false, out message), message);
            joined = Read(db, b2);
            TestLog.Near("B moved only sideways: x", 303500, joined.Geometry.Start.X, 1e-6);
            TestLog.Near("B moved only sideways: y", 100000, joined.Geometry.Start.Y, 1e-6);
            TestLog.Near("B length unchanged", 2000, joined.Geometry.Length, 1e-6);

            // --- B turned round (rotation 180, Position is its far end): still one block from A's start.
            ObjectId a2 = Insert(db, W600, 300000, 150000, 0, false, 2000);
            ObjectId reversed = Insert(db, W600, 305500, 150040, 180, false, 2000);
            plan = PlanFor(db, a2, 300500, 150300, reversed, 304500, 150340);
            TestLog.True("reversed plan", plan.Error == null && plan.Merge, plan.Error ?? plan.Note);
            DuctAligner.Apply(db, plan, true, out message);
            TestLog.True("reversed B is erased", reversed.IsErased);
            joined = Read(db, a2);
            TestLog.Near("reversed merge start x", 300000, joined.Geometry.Start.X, 1e-6);
            TestLog.Near("reversed merge length", 5500, joined.Geometry.Length, 1e-6);
            AssertMatchesDrawing("reversed merge", db, a2);

            // --- B mirrored (X scale -1): axis -X as well, through the nested transform.
            ObjectId a3 = Insert(db, W600, 300000, 160000, 0, false, 2000);
            ObjectId mirrored = Insert(db, W600, 305500, 160040, 0, true, 2000);
            plan = PlanFor(db, a3, 300500, 160300, mirrored, 304500, 160340);
            TestLog.True("mirrored plan", plan.Error == null && plan.Merge, plan.Error ?? plan.Note);
            DuctAligner.Apply(db, plan, true, out message);
            TestLog.True("mirrored B is erased", mirrored.IsErased);
            joined = Read(db, a3);
            TestLog.Near("mirrored merge length", 5500, joined.Geometry.Length, 1e-6);
            TestLog.Near("mirrored merge y", 160000, joined.Geometry.Start.Y, 1e-6);
            AssertMatchesDrawing("mirrored merge", db, a3);

            // --- B before A's start: the merged block starts at B's far end (296000).
            ObjectId a4 = Insert(db, W600, 300000, 170000, 0, false, 2000);
            ObjectId before = Insert(db, W600, 296000, 170040, 0, false, 2000);
            plan = PlanFor(db, a4, 300500, 170300, before, 297000, 170340);
            TestLog.True("before plan", plan.Error == null && plan.Merge, plan.Error ?? plan.Note);
            DuctAligner.Apply(db, plan, true, out message);
            TestLog.True("before B is erased", before.IsErased);
            joined = Read(db, a4);
            TestLog.Near("before merge start x", 296000, joined.Geometry.Start.X, 1e-6);
            TestLog.Near("before merge length", 6000, joined.Geometry.Length, 1e-6);
            AssertMatchesDrawing("before merge", db, a4);

            // --- Different widths: an eccentric transition with the flat side on the aligned edge
            ObjectId narrow = Insert(db, DuctSize.Rect(400, 300), 303500, 100150, 0, false, 2000);
            plan = PlanFor(db, a, 300500, 100300, narrow, 304000, 100350);
            TestLog.True("eccentric plan", plan.Error == null && plan.Join.Kind == JoinKind.Eccentric, plan.Error ?? plan.Note);
            TestLog.Near("translation y", -50, plan.Translation.Y, 1e-6);
            TestLog.True("apply eccentric", DuctAligner.Apply(db, plan, true, out message), message);
            TestLog.True("message mentions the transition", message.Contains("eccentric transition"), message);
            joined = Read(db, narrow);
            TestLog.Near("narrow B starts after the transition", 302346.4, joined.Geometry.Start.X, 1e-6);
            TestLog.Near("narrow B centre y", 100100, joined.Geometry.Start.Y, 1e-6);
            TestLog.Near("narrow B length", 3153.6, joined.Geometry.Length, 1e-6);
            AssertMatchesDrawing("narrow B", db, narrow);
            FindTransition(db, 302000, 302346.4, 100300);

            // --- A perpendicular branch: reference = A's left wall (y = 100300), edge = B's start face.
            // Lead = max(600/2, 102) = 300, so B starts at y = 100600 and keeps its far end (103500).
            ObjectId branch = Insert(db, W600, 301000, 101500, 90, false, 2000);
            plan = PlanFor(db, a, 300500, 100300, branch, 301000, 101500);
            TestLog.True("branch plan", plan.Error == null && plan.Join.Kind == JoinKind.Tap, plan.Error ?? plan.Join.Reason);
            TestLog.True("apply branch without a join answer", DuctAligner.Apply(db, plan, false, out message), message);
            TestLog.True("message mentions the tap", message.Contains("tap added"), message);
            joined = Read(db, branch);
            TestLog.Near("branch start x", 301000, joined.Geometry.Start.X, 1e-6);
            TestLog.Near("branch start y", 100600, joined.Geometry.Start.Y, 1e-6);
            TestLog.Near("branch length", 2900, joined.Geometry.Length, 1e-6);
            AssertMatchesDrawing("branch", db, branch);

            // --- Refusals
            // A duct at right angles beyond A's end: no room for a tap on A.
            ObjectId endFaceB = Insert(db, W600, 303500, 99000, 90, false, 2000);
            plan = PlanFor(db, a, 300500, 100300, endFaceB, 303500, 99000);
            TestLog.True("a tap off the end of A is refused", plan.Error != null && plan.Error.Contains("No tap"), plan.Error);

            ObjectId slanted = Insert(db, W600, 303500, 100040, 5, false, 2000);
            plan = PlanFor(db, a, 300500, 100300, slanted, 304000, 100390);
            TestLog.True("a 5 degree edge is refused", plan.Error != null && plan.Error.Contains("degrees off"), plan.Error);

            // --- B would be too short after the join
            ObjectId stub = Insert(db, W600, 302050, 100040, 0, false, 30);
            plan = PlanFor(db, a, 300500, 100300, stub, 302070, 100340);
            TestLog.Equal("no join for a stub", JoinKind.None, plan.Join.Kind);
            TestLog.True("the stub reason is given", plan.Note != null && plan.Note.Contains("shorter than 100"), plan.Note);

            // --- A plain LINE as the reference: align only
            ObjectId lineId;
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                Line line = new Line(new Point3d(299000, 100300, 0), new Point3d(310000, 100300, 0));
                space.AppendEntity(line);
                transaction.AddNewlyCreatedDBObject(line, true);
                lineId = line.ObjectId;
                transaction.Commit();
            }

            ObjectId b3 = Insert(db, W600, 307000, 100090, 0, false, 2000);
            Vec2 lineFrom, lineTo, edgeFrom, edgeTo;
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                DuctAligner.TryGetEdge(transaction, lineId, Matrix3d.Identity, new Point3d(305000, 100300, 0),
                    out lineFrom, out lineTo, out error);
                transaction.Commit();
            }

            PickEdge(db, b3, 308000, 100390, out edgeFrom, out edgeTo, out error);
            plan = DuctAligner.Plan(lineFrom, lineTo, null, Read(db, b3), edgeFrom, edgeTo, new FittingSettings());
            TestLog.True("line reference plans", plan.Error == null && plan.Join.Kind == JoinKind.None, plan.Error);
            TestLog.Near("line reference translation", -90, plan.Translation.Y, 1e-6);
            TestLog.True("line reference applies", DuctAligner.Apply(db, plan, false, out message), message);
            TestLog.Near("line reference result", 100000, Read(db, b3).Geometry.Start.Y, 1e-6);
        }

        private static void FindTransition(Database db, double x0, double x1, double flatY)
        {
            bool found = false;
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    Polyline p = transaction.GetObject(id, OpenMode.ForRead) as Polyline;
                    string kind, text;
                    if (p == null || p.NumberOfVertices != 4 || !DuctBlockLibrary.TryReadFittingTag(p, out kind, out text)
                        || kind != "TRANSITION")
                        continue;
                    if (Math.Abs(p.GetPoint2dAt(0).X - x0) > 1e-6)
                        continue;
                    found = true;
                    TestLog.Equal("transition text", "600x300>400x300", text);
                    TestLog.Near("flat start y", flatY, p.GetPoint2dAt(0).Y, 1e-6);
                    TestLog.Near("far start y", flatY - 600, p.GetPoint2dAt(1).Y, 1e-6);
                    TestLog.Near("far end x", x1, p.GetPoint2dAt(2).X, 1e-6);
                    TestLog.Near("far end y", flatY - 400, p.GetPoint2dAt(2).Y, 1e-6);
                    TestLog.Near("flat end y", flatY, p.GetPoint2dAt(3).Y, 1e-6);
                    TestLog.Equal("transition is on A's layer", SupplyLayer, p.Layer);
                }

                transaction.Commit();
            }

            TestLog.True("the eccentric transition polyline exists", found);
        }
    }
}
