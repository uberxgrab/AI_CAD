using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using HvacAiCadAddin.Ducts;

namespace DuctAcadTests
{
    // Route sessions: the same object HVACDUCT drives, without prompts or graphics.
    public partial class DuctAcadTestCommands
    {
        private static readonly DuctSize W600 = DuctSize.Rect(600, 300);

        private static DuctRouteSession NewSession(Database db, DuctSize size, FittingSettings settings = null)
        {
            return new DuctRouteSession(
                db, Template, TemplateSizes, settings ?? new FittingSettings(), SupplyLayer, size);
        }

        private static bool Add(DuctRouteSession session, double x, double y, out string error)
        {
            List<string> notes;
            return session.TryAddPoint(new Vec2(x, y), out error, out notes);
        }

        private sealed class Snapshot
        {
            public readonly List<DuctInfo> Ducts = new List<DuctInfo>();
            public readonly List<Polyline> FittingKinds = new List<Polyline>();
            public readonly List<string> Kinds = new List<string>();
            public readonly List<string> Sizes = new List<string>();
            public readonly List<ObjectId> FittingIds = new List<ObjectId>();
        }

        // Live (not erased) ducts and tagged fittings the session created, in creation order.
        private static Snapshot Look(Database db, DuctRouteSession session)
        {
            Snapshot snap = new Snapshot();
            List<ObjectId> ids = new List<ObjectId>(session.OwnIds);
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    if (id.IsErased)
                        continue;
                    Entity entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                    BlockReference block = entity as BlockReference;
                    if (block != null)
                    {
                        DuctInfo info;
                        if (DuctBlockLibrary.TryReadDuct(transaction, block, out info))
                            snap.Ducts.Add(info);
                        continue;
                    }

                    string kind, size;
                    if (DuctBlockLibrary.TryReadFittingTag(entity, out kind, out size))
                    {
                        snap.Kinds.Add(kind);
                        snap.Sizes.Add(size);
                        snap.FittingIds.Add(id);
                    }
                }

                transaction.Commit();
            }

            snap.Ducts.Sort((a, b) => a.Id.Handle.Value.CompareTo(b.Id.Handle.Value));
            return snap;
        }

        private static int CountKind(Snapshot snap, string kind)
        {
            int count = 0;
            foreach (string k in snap.Kinds)
            {
                if (k == kind)
                    count++;
            }

            return count;
        }

        private static void TestRunAndUndo(Database db)
        {
            DuctRouteSession session = NewSession(db, W600);
            session.StartFree(new Vec2(100000, 100000), 0);
            string error;

            TestLog.True("first point", Add(session, 103000, 100000, out error), error);
            Snapshot s = Look(db, session);
            TestLog.Equal("one duct after one point", 1, s.Ducts.Count);
            TestLog.Near("first duct length", 3000, s.Ducts[0].Geometry.Length, 1e-6);
            TestLog.Equal("no fittings yet", 0, s.Kinds.Count);

            TestLog.True("second point", Add(session, 103000, 102000, out error), error);
            s = Look(db, session);
            TestLog.Equal("two ducts", 2, s.Ducts.Count);
            TestLog.Near("first duct shortened by the elbow", 2550, s.Ducts[0].Geometry.Length, 1e-6);
            TestLog.Near("second duct starts after the elbow x", 103000, s.Ducts[1].Geometry.Start.X, 1e-6);
            TestLog.Near("second duct starts after the elbow y", 100450, s.Ducts[1].Geometry.Start.Y, 1e-6);
            TestLog.Near("second duct length", 1550, s.Ducts[1].Geometry.Length, 1e-6);
            TestLog.Equal("one elbow", 1, CountKind(s, "ELBOW"));
            TestLog.Equal("elbow size text", "600x300", s.Sizes[0]);
            TestLog.Equal("ducts are on the system layer", SupplyLayer, s.Ducts[0].Layer);
            AssertMatchesDrawing("run duct 1", db, s.Ducts[0].Id);
            AssertMatchesDrawing("run duct 2", db, s.Ducts[1].Id);

            TestLog.True("third point", Add(session, 100000, 102000, out error), error);
            s = Look(db, session);
            TestLog.Equal("three ducts", 3, s.Ducts.Count);
            TestLog.Near("middle duct loses 450 at each end", 1100, s.Ducts[1].Geometry.Length, 1e-6);
            TestLog.Near("last duct start x", 102550, s.Ducts[2].Geometry.Start.X, 1e-6);
            TestLog.Near("last duct length", 2550, s.Ducts[2].Geometry.Length, 1e-6);
            TestLog.Equal("two elbows", 2, CountKind(s, "ELBOW"));
            TestLog.Near("last duct runs -X", -1, s.Ducts[2].Geometry.Axis.X, 1e-9);
            AssertMatchesDrawing("run duct 3", db, s.Ducts[2].Id);

            string message;
            TestLog.True("undo the third segment", session.Undo(out message), message);
            s = Look(db, session);
            TestLog.Equal("two ducts after undo", 2, s.Ducts.Count);
            TestLog.Near("middle duct restored", 1550, s.Ducts[1].Geometry.Length, 1e-6);
            TestLog.Equal("one elbow after undo", 1, CountKind(s, "ELBOW"));
            TestLog.Equal("segment count", 2, session.SegmentCount);
            AssertMatchesDrawing("run duct 2 after undo", db, s.Ducts[1].Id);

            TestLog.True("undo the second", session.Undo(out message), message);
            TestLog.True("undo the first", session.Undo(out message), message);
            s = Look(db, session);
            TestLog.Equal("nothing left", 0, s.Ducts.Count + s.Kinds.Count);
            TestLog.True("a fourth undo has nothing to do", !session.Undo(out message));
            TestLog.True("cursor back at the start", session.CurrentPoint.DistanceTo(new Vec2(100000, 100000)) < 1e-9);
        }

        private static void TestContinue(Database db)
        {
            string error;
            // Continue from the end face.
            ObjectId existing = Insert(db, W600, 110000, 100000, 0, false, 2000);
            DuctRouteSession session = NewSession(db, W600);
            DuctInfo info = Read(db, existing);
            TestLog.True("start at the end face", session.StartContinue(info, true, out error), error);
            TestLog.True("turn left", Add(session, 112000, 101500, out error), error);
            DuctInfo after = Read(db, existing);
            TestLog.Near("existing duct shortened by the elbow", 1550, after.Geometry.Length, 1e-6);
            TestLog.Near("existing duct start fixed", 110000, after.Geometry.Start.X, 1e-9);
            Snapshot s = Look(db, session);
            TestLog.Near("new leg length", 1050, s.Ducts[0].Geometry.Length, 1e-6);
            TestLog.Near("new leg starts after the elbow", 100450, s.Ducts[0].Geometry.Start.Y, 1e-6);
            TestLog.Equal("elbow drawn", 1, CountKind(s, "ELBOW"));
            string message;
            TestLog.True("undo", session.Undo(out message), message);
            after = Read(db, existing);
            TestLog.Near("existing duct restored", 2000, after.Geometry.Length, 1e-6);

            // Continue from the start face of a mirrored duct: Position moves when the start shortens.
            ObjectId mirrored = Insert(db, W600, 120000, 100000, 0, true, 2000);
            info = Read(db, mirrored);
            TestLog.Near("mirrored duct axis is -X", -1, info.Geometry.Axis.X, 1e-9);
            session = NewSession(db, W600);
            TestLog.True("start at the start face", session.StartContinue(info, false, out error), error);
            TestLog.Near("run starts at Position", 120000, session.CurrentPoint.X, 1e-9);
            TestLog.True("turn left (+X then +Y)", Add(session, 120000, 101500, out error), error);
            after = Read(db, mirrored);
            TestLog.Near("start shortened: length", 1550, after.Geometry.Length, 1e-6);
            TestLog.Near("start shortened: Position moved along the axis", 119550, after.Geometry.Start.X, 1e-6);
            TestLog.Near("start shortened: far end fixed", 118000, after.Geometry.End.X, 1e-6);
            AssertMatchesDrawing("shortened mirrored duct", db, mirrored);
            TestLog.True("undo restores Position", session.Undo(out message), message);
            after = Read(db, mirrored);
            TestLog.Near("Position restored", 120000, after.Geometry.Start.X, 1e-6);
            TestLog.Near("length restored", 2000, after.Geometry.Length, 1e-6);
            AssertMatchesDrawing("restored mirrored duct", db, mirrored);

            // A short existing duct falls back to R = W/2.
            ObjectId shortDuct = Insert(db, W600, 130000, 100000, 0, false, 400);
            session = NewSession(db, W600);
            session.StartContinue(Read(db, shortDuct), true, out error);
            List<string> notes;
            TestLog.True("short duct: elbow with R = W/2",
                session.TryAddPoint(new Vec2(130400, 101500), out error, out notes), error);
            TestLog.Near("short duct shortened by 300", 100, Read(db, shortDuct).Geometry.Length, 1e-6);

            // Shorter than R = W/2: refused, nothing changed.
            ObjectId tiny = Insert(db, W600, 140000, 100000, 0, false, 250);
            session = NewSession(db, W600);
            session.StartContinue(Read(db, tiny), true, out error);
            TestLog.True("tiny duct: refused", !session.TryAddPoint(new Vec2(140250, 101500), out error, out notes));
            TestLog.True("tiny duct: reason given", error != null && error.Contains("existing duct"), error);
            TestLog.Near("tiny duct untouched", 250, Read(db, tiny).Geometry.Length, 1e-9);
            TestLog.Equal("nothing was drawn", 0, session.OwnIds.Count);

            // A duct that exactly fills the elbow takeup disappears (it would have zero length).
            ObjectId exact = Insert(db, W600, 150000, 100000, 0, false, 450);
            session = NewSession(db, W600);
            session.StartContinue(Read(db, exact), true, out error);
            TestLog.True("exact-fit duct accepted", session.TryAddPoint(new Vec2(150450, 101500), out error, out notes), error);
            TestLog.True("zero-length duct is erased", exact.IsErased);
            TestLog.True("undo brings it back", session.Undo(out message) && !exact.IsErased, message);
            TestLog.Near("and restores its length", 450, Read(db, exact).Geometry.Length, 1e-6);
        }

        private static void TestTapStart(Database db)
        {
            ObjectId mainId = Insert(db, W600, 160000, 100000, 0, false, 3000);
            DuctInfo main = Read(db, mainId);
            DuctHit hit = DuctHitTest.Classify(main.Geometry, new Vec2(161000, 100305), 25);
            TestLog.True("hit is the left wall", hit != null && hit.Kind == DuctHitKind.LeftWall && Math.Abs(hit.Along - 1000) < 1e-9);

            DuctSize branch = DuctSize.Rect(350, 250);
            DuctRouteSession session = NewSession(db, branch);
            string error;
            TestLog.True("tap start accepted", session.StartTap(main, hit, out error), error);
            TestLog.True("branch direction is forced", session.ForcedDirection.HasValue
                && Math.Abs(session.ForcedDirection.Value.Y - 1) < 1e-9);
            Vec2 constrained = session.Constrain(new Vec2(161300, 102000));
            TestLog.Near("cursor is projected onto the branch axis x", 161000, constrained.X, 1e-9);
            TestLog.Near("cursor is projected onto the branch axis y", 102000, constrained.Y, 1e-9);

            PreviewShapes preview = session.Preview(constrained);
            TestLog.True("preview has no error", preview.Error == null, preview.Error);
            TestLog.Equal("preview shows the branch and the tap", 1, preview.Ducts.Count);
            TestLog.Equal("preview tap", 1, preview.Fittings.Count);

            TestLog.True("first branch point", Add(session, constrained.X, constrained.Y, out error), error);
            Snapshot s = Look(db, session);
            TestLog.Equal("tap fitting drawn", 1, CountKind(s, "TAP"));
            TestLog.Equal("tap size text", "350x250", s.Sizes[0]);
            TestLog.Equal("one branch duct", 1, s.Ducts.Count);
            TestLog.Near("branch starts L = 175 from the wall", 100475, s.Ducts[0].Geometry.Start.Y, 1e-6);
            TestLog.Near("branch length 1700 - 175", 1525, s.Ducts[0].Geometry.Length, 1e-6);
            TestLog.Near("branch runs +Y", 1, s.Ducts[0].Geometry.Axis.Y, 1e-9);
            TestLog.Near("main untouched", 3000, Read(db, mainId).Geometry.Length, 1e-9);

            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                Polyline tap = (Polyline)transaction.GetObject(s.FittingIds[0], OpenMode.ForRead);
                TestLog.Equal("tap has four vertices", 4, tap.NumberOfVertices);
                TestLog.Near("opening starts 350 upstream", 160650, tap.GetPoint2dAt(0).X, 1e-6);
                TestLog.Near("downstream side is square", 161175, tap.GetPoint2dAt(1).X, 1e-6);
                TestLog.Near("lead-in top x", 160825, tap.GetPoint2dAt(3).X, 1e-6);
                TestLog.Near("tap height 175", 100475, tap.GetPoint2dAt(3).Y, 1e-6);
                transaction.Commit();
            }

            string message;
            TestLog.True("undo removes the tap and the branch", session.Undo(out message), message);
            s = Look(db, session);
            TestLog.Equal("nothing left after undo", 0, s.Ducts.Count + s.Kinds.Count);

            // A round branch gets the conical tap.
            DuctRouteSession roundSession = NewSession(db, DuctSize.Round(250));
            hit = DuctHitTest.Classify(main.Geometry, new Vec2(162000, 99700), 25);
            TestLog.True("right wall", hit.Kind == DuctHitKind.RightWall);
            TestLog.True("round tap start", roundSession.StartTap(main, hit, out error), error);
            TestLog.True("round branch point", Add(roundSession, 162000, 98000, out error), error);
            s = Look(db, roundSession);
            TestLog.Near("round branch starts 203 from the wall", 99500 - 3, s.Ducts[0].Geometry.Start.Y, 1e-6);
            TestLog.Near("round branch runs -Y", -1, s.Ducts[0].Geometry.Axis.Y, 1e-9);
            TestLog.Near("round branch length 1700 - 203", 1497, s.Ducts[0].Geometry.Length, 1e-6);
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                Polyline cone = (Polyline)transaction.GetObject(s.FittingIds[0], OpenMode.ForRead);
                TestLog.Equal("conical tap has six vertices", 6, cone.NumberOfVertices);
                transaction.Commit();
            }

            // A tap near the main's start does not fit.
            hit = DuctHitTest.Classify(main.Geometry, new Vec2(160100, 100305), 25);
            DuctRouteSession nearStart = NewSession(db, branch);
            TestLog.True("a tap too close to the start is refused", !nearStart.StartTap(main, hit, out error));
            TestLog.True("reason mentions the main", error != null && error.Contains("main"), error);
        }

        private static void TestRefusals(Database db)
        {
            DuctRouteSession session = NewSession(db, W600);
            session.StartFree(new Vec2(170000, 100000), 0);
            string error;
            Add(session, 173000, 100000, out error);
            TestLog.True("second point", Add(session, 173000, 100800, out error), error);
            Snapshot before = Look(db, session);
            TestLog.Near("middle duct 800 - 450", 350, before.Ducts[1].Geometry.Length, 1e-6);

            // 800 - 450 (frozen corner) - 300 (new corner falls back to W/2) = 50
            List<string> notes;
            TestLog.True("retry with R = W/2 succeeds", session.TryAddPoint(new Vec2(170000, 100800), out error, out notes), error);
            TestLog.True("the fallback is reported", notes.Exists(n => n.Contains("R = W/2")), string.Join("|", notes));
            Snapshot after = Look(db, session);
            TestLog.Near("middle duct is 50 long", 50, after.Ducts[1].Geometry.Length, 1e-6);
            TestLog.Equal("two elbows", 2, CountKind(after, "ELBOW"));

            // An 800 mm leg can take R = W/2, but the next leg at 500 cannot.
            DuctRouteSession session2 = NewSession(db, W600);
            session2.StartFree(new Vec2(180000, 100000), 0);
            Add(session2, 183000, 100000, out error);
            Add(session2, 183000, 100500, out error);
            Snapshot s1 = Look(db, session2);
            int ownBefore = session2.OwnIds.Count;
            TestLog.True("refuses a segment that is too short",
                !session2.TryAddPoint(new Vec2(180000, 100500), out error, out notes));
            TestLog.True("the refusal says why", error != null && error.Contains("too short"), error);
            Snapshot s2 = Look(db, session2);
            TestLog.Equal("nothing new was drawn", ownBefore, session2.OwnIds.Count);
            TestLog.Near("the middle duct is unchanged", s1.Ducts[1].Geometry.Length, s2.Ducts[1].Geometry.Length, 1e-9);
            TestLog.Equal("same segment count", 2, session2.SegmentCount);

            TestLog.True("zero-length segment refused",
                !session2.TryAddPoint(session2.CurrentPoint, out error, out notes));
            TestLog.True("reversal refused", !session2.TryAddPoint(new Vec2(183000, 100000), out error, out notes));
        }

        private static void TestSizeAndEndTap(Database db)
        {
            DuctSize big = DuctSize.Rect(1000, 300);
            DuctSize small = DuctSize.Rect(500, 300);
            DuctRouteSession session = NewSession(db, big);
            session.StartFree(new Vec2(190000, 100000), 0);
            string error;
            TestLog.True("first run in 1000x300", Add(session, 192000, 100000, out error), error);
            session.CurrentSize = small;
            TestLog.True("size change at the vertex", Add(session, 195000, 100000, out error), error);
            Snapshot s = Look(db, session);
            TestLog.Equal("two ducts", 2, s.Ducts.Count);
            TestLog.Equal("one transition", 1, CountKind(s, "TRANSITION"));
            TestLog.Equal("transition text", "1000x300>500x300", s.Sizes[0]);
            TestLog.Near("first duct is full length", 2000, s.Ducts[0].Geometry.Length, 1e-6);
            TestLog.Near("second duct starts after a 603.5 transition", 192603.5, s.Ducts[1].Geometry.Start.X, 1e-6);
            TestLog.Near("second duct length", 2396.5, s.Ducts[1].Geometry.Length, 1e-6);
            TestLog.Equal("second duct is 500 wide", 500.0, s.Ducts[1].Size.Width);
            AssertMatchesDrawing("500x300 after the transition", db, s.Ducts[1].Id);
            string message;
            session.Undo(out message);
            TestLog.True("undo returns to the previous size", session.CurrentSize.Equals(big));

            // A run that ends on the side wall of another duct finishes with a tap.
            ObjectId mainId = Insert(db, W600, 200000, 100000, 0, false, 3000);
            DuctInfo main = Read(db, mainId);
            DuctSize branch = DuctSize.Rect(350, 250);
            DuctRouteSession run = NewSession(db, branch);
            run.StartFree(new Vec2(201000, 102000), 0);
            DuctHit hit = DuctHitTest.Classify(main.Geometry, new Vec2(201000, 100303), 25);
            DuctInfo found;
            DuctHit foundHit;
            TestLog.True("the wall is found away from the run's own ducts",
                DuctBlockLibrary.FindDuctAt(db, new Vec2(201000, 100303), 25, run.OwnIds, out found, out foundHit)
                && found.Id == mainId && foundHit.Kind == DuctHitKind.LeftWall);
            List<string> notes;
            TestLog.True("finish with a tap", run.TryFinishOnWall(main, hit, out error, out notes), error);
            s = Look(db, run);
            TestLog.Equal("tap at the end", 1, CountKind(s, "TAP"));
            TestLog.Near("run ends 175 before the wall: length 1700 - 175", 1525, s.Ducts[0].Geometry.Length, 1e-6);
            TestLog.Near("run starts at the first point", 102000, s.Ducts[0].Geometry.Start.Y, 1e-6);

            // A run not at right angles to the wall gets no tap.
            DuctRouteSession slanted = NewSession(db, branch);
            slanted.StartFree(new Vec2(203000, 102000), 0);
            DuctHit hit2 = DuctHitTest.Classify(main.Geometry, new Vec2(202000, 100303), 25);
            TestLog.True("slanted approach is refused", !slanted.TryFinishOnWall(main, hit2, out error, out notes));
            TestLog.True("and says so", error != null && error.Contains("right angles"), error);
        }
    }
}
