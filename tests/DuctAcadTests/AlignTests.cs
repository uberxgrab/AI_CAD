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

            // --- Direct join, B beyond A's end, same direction, 40 mm off
            ObjectId a = Insert(db, W600, 300000, 100000, 0, false, 2000);
            ObjectId b = Insert(db, W600, 303500, 100040, 0, false, 2000);
            AlignPlan plan = PlanFor(db, a, 300500, 100300, b, 304000, 100340);
            TestLog.True("plan has no error", plan.Error == null, plan.Error);
            TestLog.Near("translation x", 0, plan.Translation.X, 1e-6);
            TestLog.Near("translation y", -40, plan.Translation.Y, 1e-6);
            TestLog.Equal("join is direct", JoinKind.Direct, plan.Join.Kind);
            TestLog.True("apply with join", DuctAligner.Apply(db, plan, true, out message), message);
            DuctInfo joined = Read(db, b);
            TestLog.Near("B start x meets A's end", 302000, joined.Geometry.Start.X, 1e-6);
            TestLog.Near("B start y aligned", 100000, joined.Geometry.Start.Y, 1e-6);
            TestLog.Near("B length", 3500, joined.Geometry.Length, 1e-6);
            AssertMatchesDrawing("joined B", db, b);

            // --- Alignment only (no join)
            ObjectId b2 = Insert(db, W600, 303500, 100040, 0, false, 2000);
            plan = PlanFor(db, a, 300500, 100300, b2, 304000, 100340);
            TestLog.True("apply without join", DuctAligner.Apply(db, plan, false, out message), message);
            joined = Read(db, b2);
            TestLog.Near("B moved only sideways: x", 303500, joined.Geometry.Start.X, 1e-6);
            TestLog.Near("B moved only sideways: y", 100000, joined.Geometry.Start.Y, 1e-6);
            TestLog.Near("B length unchanged", 2000, joined.Geometry.Length, 1e-6);

            // --- B turned round (rotation 180): Position is its far end, so it must not move.
            ObjectId reversed = Insert(db, W600, 305500, 100040, 180, false, 2000);
            plan = PlanFor(db, a, 300500, 100300, reversed, 304500, 100340);
            TestLog.True("reversed plan", plan.Error == null && plan.Join.Kind == JoinKind.Direct, plan.Error ?? plan.Note);
            TestLog.True("position stays", !plan.Join.PositionMoves);
            DuctAligner.Apply(db, plan, true, out message);
            joined = Read(db, reversed);
            TestLog.Near("reversed B start x unchanged", 305500, joined.Geometry.Start.X, 1e-6);
            TestLog.Near("reversed B start y aligned", 100000, joined.Geometry.Start.Y, 1e-6);
            TestLog.Near("reversed B length reaches A", 3500, joined.Geometry.Length, 1e-6);
            AssertMatchesDrawing("reversed B", db, reversed);

            // --- B mirrored (X scale -1): axis -X as well, through the nested transform.
            ObjectId mirrored = Insert(db, W600, 305500, 100040, 0, true, 2000);
            plan = PlanFor(db, a, 300500, 100300, mirrored, 304500, 100340);
            TestLog.True("mirrored plan", plan.Error == null && plan.Join.Kind == JoinKind.Direct, plan.Error ?? plan.Note);
            DuctAligner.Apply(db, plan, true, out message);
            joined = Read(db, mirrored);
            TestLog.Near("mirrored B length reaches A", 3500, joined.Geometry.Length, 1e-6);
            TestLog.Near("mirrored B aligned", 100000, joined.Geometry.Start.Y, 1e-6);
            AssertMatchesDrawing("mirrored B", db, mirrored);

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

            // --- Refusals
            // A duct at right angles to A: its end face is parallel to the reference edge.
            ObjectId endFaceB = Insert(db, W600, 303500, 99000, 90, false, 2000);
            plan = PlanFor(db, a, 300500, 100300, endFaceB, 303500, 99000);
            TestLog.True("an end face is refused", plan.Error != null && plan.Error.Contains("end face"), plan.Error);
            TestLog.True("and perpendicular ducts are named as out of scope", plan.Error != null && plan.Error.Contains("Perpendicular"), plan.Error);

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
