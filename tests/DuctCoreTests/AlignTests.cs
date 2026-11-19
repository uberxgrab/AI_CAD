using System;
using HvacAiCadAddin.Ducts;

namespace DuctCoreTests
{
    /// <summary>HVACDUCTALIGN maths: parallel test, translation, and the join decision.</summary>
    internal static class AlignTests
    {
        private static readonly FittingSettings F = new FittingSettings();

        // A: start (0,0), axis +X, 2000 long, 600 wide. Walls y = +-300. Reference point on the left wall.
        private static DuctGeometry A()
        {
            return new DuctGeometry(new Vec2(0, 0), new Vec2(1, 0), 2000, 600);
        }

        private static readonly Vec2 RefPoint = new Vec2(1000, 300);

        public static void Run()
        {
            Parallel();
            Translation();
            DirectJoin();
            EccentricJoin();
            NoJoin();
        }

        private static Vec2 Deg(double degrees)
        {
            return Vec2.FromAngle(degrees * Math.PI / 180.0);
        }

        private static void Parallel()
        {
            Check.Case("parallel within 1 degree", () =>
            {
                Check.True("0.9 degrees", DuctAlignMath.IsParallel(new Vec2(1, 0), Deg(0.9), 1.0));
                Check.False("1.1 degrees", DuctAlignMath.IsParallel(new Vec2(1, 0), Deg(1.1), 1.0));
                Check.True("anti-parallel", DuctAlignMath.IsParallel(new Vec2(1, 0), Deg(180.5), 1.0));
                Check.False("perpendicular", DuctAlignMath.IsParallel(new Vec2(1, 0), new Vec2(0, 1), 1.0));
                Check.False("zero vector", DuctAlignMath.IsParallel(new Vec2(0, 0), new Vec2(1, 0), 1.0));
                Check.Near("angle between 3 and 183 degrees", 0, DuctAlignMath.AngleBetweenLines(Deg(3), Deg(183)), 1e-9);
                Check.Near("angle between 0 and 100 degrees", 80, DuctAlignMath.AngleBetweenLines(Deg(0), Deg(100)), 1e-9);
            });
        }

        private static void Translation()
        {
            Check.Case("translation is perpendicular to the reference edge", () =>
            {
                Vec2 t = DuctAlignMath.AlignTranslation(new Vec2(0, 300), new Vec2(1000, 300), new Vec2(500, 250));
                Check.Near("x", 0, t.X, 1e-9);
                Check.Near("y", 50, t.Y, 1e-9);

                Vec2 reversed = DuctAlignMath.AlignTranslation(new Vec2(1000, 300), new Vec2(0, 300), new Vec2(500, 250));
                Check.Near("reversed reference x", 0, reversed.X, 1e-9);
                Check.Near("reversed reference y", 50, reversed.Y, 1e-9);

                Vec2 down = DuctAlignMath.AlignTranslation(new Vec2(0, 300), new Vec2(1000, 300), new Vec2(500, 340));
                Check.Near("moves down", -40, down.Y, 1e-9);

                // Reference along the diagonal; the point (2,0) is 1.414 from it.
                Vec2 slanted = DuctAlignMath.AlignTranslation(new Vec2(0, 0), new Vec2(1, 1), new Vec2(2, 0));
                Check.Near("diagonal x", -1, slanted.X, 1e-9);
                Check.Near("diagonal y", 1, slanted.Y, 1e-9);

                Vec2 already = DuctAlignMath.AlignTranslation(new Vec2(0, 300), new Vec2(1000, 300), new Vec2(700, 300));
                Check.Near("already aligned", 0, already.Length, 1e-9);
            });
        }

        private static void DirectJoin()
        {
            Check.Case("equal widths: B beyond A's end, same direction, moves Position", () =>
            {
                DuctGeometry b = new DuctGeometry(new Vec2(3500, 0), new Vec2(1, 0), 2000, 600);
                JoinPlan plan = DuctAlignMath.PlanJoin(A(), b, RefPoint, F);
                Check.Equal("direct", JoinKind.Direct, plan.Kind);
                Check.True("position moves", plan.PositionMoves);
                Check.Near("new start x", 2000, plan.NewStart.X);
                Check.Near("new start y", 0, plan.NewStart.Y);
                Check.Near("new length (5500 - 2000)", 3500, plan.NewLength);
                Check.True("no transition", plan.Transition == null);
            });

            Check.Case("equal widths: B beyond A's end, opposite direction, Position stays", () =>
            {
                DuctGeometry b = new DuctGeometry(new Vec2(5500, 0), new Vec2(-1, 0), 2000, 600);
                JoinPlan plan = DuctAlignMath.PlanJoin(A(), b, RefPoint, F);
                Check.Equal("direct", JoinKind.Direct, plan.Kind);
                Check.False("position stays", plan.PositionMoves);
                Check.Near("start x", 5500, plan.NewStart.X);
                Check.Near("new length", 3500, plan.NewLength);
            });

            Check.Case("equal widths: B before A's start, same direction, Position stays", () =>
            {
                DuctGeometry b = new DuctGeometry(new Vec2(-3000, 0), new Vec2(1, 0), 1000, 600);
                JoinPlan plan = DuctAlignMath.PlanJoin(A(), b, RefPoint, F);
                Check.Equal("direct", JoinKind.Direct, plan.Kind);
                Check.False("position stays", plan.PositionMoves);
                Check.Near("start x", -3000, plan.NewStart.X);
                Check.Near("B is extended to reach x = 0", 3000, plan.NewLength);
            });

            Check.Case("equal widths: B before A's start, opposite direction, moves Position", () =>
            {
                DuctGeometry b = new DuctGeometry(new Vec2(-2000, 0), new Vec2(-1, 0), 1000, 600);
                JoinPlan plan = DuctAlignMath.PlanJoin(A(), b, RefPoint, F);
                Check.Equal("direct", JoinKind.Direct, plan.Kind);
                Check.True("position moves", plan.PositionMoves);
                Check.Near("new start x", 0, plan.NewStart.X);
                Check.Near("new length (-2000 - -3000 plus 2000)", 3000, plan.NewLength);
            });

            Check.Case("B overlapping A's end has no facing ends", () =>
            {
                // B covers [1900 .. 6000] against A ending at 2000: overlap by 100 > tolerance -> no join.
                DuctGeometry overlap = new DuctGeometry(new Vec2(1900, 0), new Vec2(1, 0), 4100, 600);
                JoinPlan plan = DuctAlignMath.PlanJoin(A(), overlap, RefPoint, F);
                Check.Equal("overlapping ends are not facing", JoinKind.None, plan.Kind);
                Check.Contains("reason", plan.Reason, "facing");
            });

            Check.Case("B touching A's end joins with no gap", () =>
            {
                DuctGeometry b = new DuctGeometry(new Vec2(2000, 0), new Vec2(1, 0), 1500, 600);
                JoinPlan plan = DuctAlignMath.PlanJoin(A(), b, RefPoint, F);
                Check.Equal("direct", JoinKind.Direct, plan.Kind);
                Check.Near("length unchanged", 1500, plan.NewLength);
            });

            Check.Case("join on the right wall", () =>
            {
                DuctGeometry b = new DuctGeometry(new Vec2(3500, 0), new Vec2(1, 0), 2000, 600);
                JoinPlan plan = DuctAlignMath.PlanJoin(A(), b, new Vec2(1000, -300), F);
                Check.Equal("direct", JoinKind.Direct, plan.Kind);
                Check.Equal("flat side", -1, plan.FlatSide);
            });
        }

        private static void EccentricJoin()
        {
            Check.Case("different widths: eccentric transition with the flat side on the aligned (left) edge", () =>
            {
                // B is 400 wide with its left wall on y = 300: centre y = 100.
                DuctGeometry b = new DuctGeometry(new Vec2(3500, 100), new Vec2(1, 0), 2000, 400);
                JoinPlan plan = DuctAlignMath.PlanJoin(A(), b, RefPoint, F);
                Check.Equal("eccentric", JoinKind.Eccentric, plan.Kind);
                Check.Equal("flat side left", 1, plan.FlatSide);
                // L = max(300, 1.732 * 200) = 346.4
                Check.Near("transition length", 346.4, plan.TransitionLength, 1e-9);
                Check.True("position moves", plan.PositionMoves);
                Check.Near("new start x", 2346.4, plan.NewStart.X, 1e-9);
                Check.Near("new start y", 100, plan.NewStart.Y, 1e-9);
                Check.Near("new length (5500 - 2346.4)", 3153.6, plan.NewLength, 1e-9);
                ShapeTests.Vertex("flat start", plan.Transition, 0, 2000, 300, 0);
                ShapeTests.Vertex("far start", plan.Transition, 1, 2000, -300, 0);
                ShapeTests.Vertex("far end", plan.Transition, 2, 2346.4, -100, 0);
                ShapeTests.Vertex("flat end", plan.Transition, 3, 2346.4, 300, 0);
            });

            Check.Case("different widths: B is wider than A", () =>
            {
                // B is 800 wide with its left wall on y = 300: centre y = -100.
                DuctGeometry b = new DuctGeometry(new Vec2(4000, -100), new Vec2(1, 0), 2000, 800);
                JoinPlan plan = DuctAlignMath.PlanJoin(A(), b, RefPoint, F);
                Check.Equal("eccentric", JoinKind.Eccentric, plan.Kind);
                // L = max(400, 1.732 * 200 = 346.4) = 400
                Check.Near("transition length", 400, plan.TransitionLength, 1e-9);
                Check.Near("new start x", 2400, plan.NewStart.X, 1e-9);
                ShapeTests.Vertex("flat end", plan.Transition, 3, 2400, 300, 0);
                ShapeTests.Vertex("far end", plan.Transition, 2, 2400, -500, 0);
            });

            Check.Case("eccentric join before A's start, B pointing the same way", () =>
            {
                DuctGeometry b = new DuctGeometry(new Vec2(-4000, 100), new Vec2(1, 0), 2000, 400);
                JoinPlan plan = DuctAlignMath.PlanJoin(A(), b, RefPoint, F);
                Check.Equal("eccentric", JoinKind.Eccentric, plan.Kind);
                Check.False("position stays", plan.PositionMoves);
                // Transition occupies [-346.4, 0]; B must end there: from -4000 to -346.4.
                Check.Near("new length", 4000 - 346.4, plan.NewLength, 1e-9);
                ShapeTests.Vertex("flat start", plan.Transition, 0, 0, 300, 0);
                ShapeTests.Vertex("flat end", plan.Transition, 3, -346.4, 300, 0);
                ShapeTests.Vertex("far end", plan.Transition, 2, -346.4, -100, 0);
            });

            Check.Case("B too short once the transition is added", () =>
            {
                // B covers [2050 .. 2400]; the transition would end at 2346.4, leaving 53.6.
                DuctGeometry b = new DuctGeometry(new Vec2(2050, 100), new Vec2(1, 0), 350, 400);
                JoinPlan plan = DuctAlignMath.PlanJoin(A(), b, RefPoint, F);
                Check.Equal("no join", JoinKind.None, plan.Kind);
                Check.Contains("reason names the limit", plan.Reason, "100");
            });
        }

        private static void NoJoin()
        {
            Check.Case("B would end up shorter than 100", () =>
            {
                DuctGeometry b = new DuctGeometry(new Vec2(2050, 0), new Vec2(1, 0), 30, 600);
                JoinPlan plan = DuctAlignMath.PlanJoin(A(), b, RefPoint, F);
                Check.Equal("no join", JoinKind.None, plan.Kind);
                Check.Contains("reason", plan.Reason, "shorter than 100");
            });

            Check.Case("exactly 100 is allowed", () =>
            {
                DuctGeometry b = new DuctGeometry(new Vec2(2050, 0), new Vec2(1, 0), 150, 600);
                JoinPlan plan = DuctAlignMath.PlanJoin(A(), b, RefPoint, F);
                Check.Equal("direct", JoinKind.Direct, plan.Kind);
                Check.Near("length", 200, plan.NewLength);
            });

            Check.Case("axes not parallel", () =>
            {
                DuctGeometry b = new DuctGeometry(new Vec2(3500, 0), Deg(5), 2000, 600);
                JoinPlan plan = DuctAlignMath.PlanJoin(A(), b, RefPoint, F);
                Check.Equal("no join", JoinKind.None, plan.Kind);
                Check.Contains("reason", plan.Reason, "not parallel");
            });

            Check.Case("reference point is not a side wall of A", () =>
            {
                DuctGeometry b = new DuctGeometry(new Vec2(3500, 0), new Vec2(1, 0), 2000, 600);
                JoinPlan plan = DuctAlignMath.PlanJoin(A(), b, new Vec2(1000, 0), F);
                Check.Equal("no join", JoinKind.None, plan.Kind);
                Check.Contains("reason", plan.Reason, "side wall");
            });

            Check.Case("B's body lies on the other side of the aligned edge", () =>
            {
                // B (400 wide) centred at y = -100 has walls at y = 100 and y = -300; neither
                // is A's left wall at y = 300.
                DuctGeometry b = new DuctGeometry(new Vec2(3500, -100), new Vec2(1, 0), 2000, 400);
                JoinPlan plan = DuctAlignMath.PlanJoin(A(), b, RefPoint, F);
                Check.Equal("no join", JoinKind.None, plan.Kind);
                Check.Contains("reason", plan.Reason, "opposite sides");
            });

            Check.Case("B beside A, not beyond an end", () =>
            {
                DuctGeometry b = new DuctGeometry(new Vec2(500, 600), new Vec2(1, 0), 1000, 600);
                JoinPlan plan = DuctAlignMath.PlanJoin(A(), b, RefPoint, F);
                Check.Equal("no join", JoinKind.None, plan.Kind);
            });
        }
    }
}
