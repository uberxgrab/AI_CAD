using System;
using System.Collections.Generic;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Pipes;
using static PipeCoreTests.Support;

namespace PipeCoreTests
{
    /// <summary>
    /// Where curves are cut to separate a run from the rest, and how the pieces are told apart
    /// (AGENTS.md, "HVACPIPESIZE", behaviour step 3).
    /// </summary>
    internal static class SplitPlanTests
    {
        public static void Run()
        {
            WholeLine();
            RunEndsAtATeeOnAPolyline();
            MiddlePieceOfALine();
            ClosedPolylineAllInRun();
            ClosedPolylinePartlyInRun();
            RunAcrossTwoCurves();
            TinySegmentIsBridged();
            ArcCutPoint();
            OnlyTheRunsCurvesArePlanned();
        }

        private static List<CurveSplitPlan> Plan(PipeNetwork net, int curve, double x, double y)
        {
            int edge = PipeRunFinder.FindEdge(net, curve, new Vec2(x, y));
            PipeRun run = PipeRunFinder.Find(net, edge);
            return PipeRunFinder.PlanSplits(net, run);
        }

        private static PipeNetwork Net(double weld, params PipeCurve[] curves)
        {
            return PipeNetwork.Build(new List<PipeCurve>(curves), weld);
        }

        private static void WholeLine()
        {
            Check.Case("a line wholly in the run needs no cut", () =>
            {
                List<CurveSplitPlan> plans = Plan(Net(1, Line(0, Layer, 0, 0, 1000, 0)), 0, 500, 0);
                Check.Equal("plans", 1, plans.Count);
                Check.True("whole", plans[0].WholeCurve);
                Check.Equal("cuts", 0, plans[0].CutPoints.Count);
                Check.True("any point is in the run", plans[0].IsRunAt(new Vec2(300, 0)));
            });
        }

        private static void RunEndsAtATeeOnAPolyline()
        {
            Check.Case("cut where the run meets a T", () =>
            {
                PipeNetwork net = Net(1,
                    Poly(0, Layer, false, 0, 0, 2000, 0, 2000, 1000),
                    Line(1, Layer, 1000, 0, 1000, -800));
                List<CurveSplitPlan> plans = Plan(net, 0, 2000, 500);
                Check.Equal("plans: the branch is not in the run", 1, plans.Count);
                CurveSplitPlan plan = plans[0];
                Check.False("not whole", plan.WholeCurve);
                Check.Equal("one cut", 1, plan.CutKeys.Count);
                Check.Near("cut key: segment 0, half way", 0.5, plan.CutKeys[0]);
                Check.True("cut point", Near(new Vec2(1000, 0), plan.CutPoints[0]));
                Check.True("right of the cut is the run", plan.IsRunAt(new Vec2(1500, 0)));
                Check.True("the second segment is the run", plan.IsRunAt(new Vec2(2000, 500)));
                Check.False("left of the cut is not", plan.IsRunAt(new Vec2(500, 0)));
            });
        }

        private static void MiddlePieceOfALine()
        {
            Check.Case("the middle piece between two T nodes", () =>
            {
                PipeNetwork net = Net(1,
                    Line(0, Layer, 0, 0, 3000, 0),
                    Line(1, Layer, 1000, 0, 1000, 500),
                    Line(2, Layer, 2000, 0, 2000, 500));
                List<CurveSplitPlan> plans = Plan(net, 0, 1500, 0);
                Check.Equal("plans", 1, plans.Count);
                CurveSplitPlan plan = plans[0];
                Check.Equal("two cuts", 2, plan.CutKeys.Count);
                Check.Near("first cut", 1.0 / 3, plan.CutKeys[0], 1e-9);
                Check.Near("second cut", 2.0 / 3, plan.CutKeys[1], 1e-9);
                Check.True("first point", Near(new Vec2(1000, 0), plan.CutPoints[0], 1e-6));
                Check.True("second point", Near(new Vec2(2000, 0), plan.CutPoints[1], 1e-6));
                Check.True("middle is the run", plan.IsRunAt(new Vec2(1500, 0)));
                Check.False("left end is not", plan.IsRunAt(new Vec2(500, 0)));
                Check.False("right end is not", plan.IsRunAt(new Vec2(2500, 0)));
            });
        }

        private static void ClosedPolylineAllInRun()
        {
            Check.Case("a closed polyline the run goes right round", () =>
            {
                // A branch touches the right side. The run leaves both ends of the T and meets itself.
                PipeNetwork net = Net(1,
                    Poly(0, Layer, true, 0, 0, 1000, 0, 1000, 1000, 0, 1000),
                    Line(1, Layer, 1000, 500, 2000, 500));
                List<CurveSplitPlan> plans = Plan(net, 0, 0, 500);
                Check.Equal("plans", 1, plans.Count);
                Check.True("whole: no cut is needed", plans[0].WholeCurve);
            });
        }

        private static void ClosedPolylinePartlyInRun()
        {
            Check.Case("a closed polyline cut at two places", () =>
            {
                PipeNetwork net = Net(1,
                    Poly(0, Layer, true, 0, 0, 1000, 0, 1000, 1000, 0, 1000),
                    Line(1, Layer, 1000, 500, 2000, 500),
                    Line(2, Layer, 0, 500, -1000, 500));
                // Pick the bottom side: the run goes from the left T, along the bottom, up to the right T.
                List<CurveSplitPlan> plans = Plan(net, 0, 500, 0);
                Check.Equal("plans", 1, plans.Count);
                CurveSplitPlan plan = plans[0];
                Check.False("not whole", plan.WholeCurve);
                Check.Equal("two cuts", 2, plan.CutKeys.Count);
                Check.Near("first cut: half way up the right side", 1.5, plan.CutKeys[0], 1e-9);
                Check.Near("second cut: half way down the left side", 3.5, plan.CutKeys[1], 1e-9);
                Check.True("first point", Near(new Vec2(1000, 500), plan.CutPoints[0]));
                Check.True("second point", Near(new Vec2(0, 500), plan.CutPoints[1]));
                Check.True("bottom side", plan.IsRunAt(new Vec2(500, 0)));
                Check.True("lower right side, past the origin of the polyline", plan.IsRunAt(new Vec2(1000, 250)));
                Check.True("lower left side, wrapping round the closing segment", plan.IsRunAt(new Vec2(0, 250)));
                Check.False("top side", plan.IsRunAt(new Vec2(500, 1000)));
                Check.False("upper right side", plan.IsRunAt(new Vec2(1000, 750)));
                Check.False("upper left side", plan.IsRunAt(new Vec2(0, 750)));
            });
        }

        private static void RunAcrossTwoCurves()
        {
            Check.Case("a run over two curves plans both", () =>
            {
                PipeNetwork net = Net(1, Line(0, Layer, 0, 0, 1000, 0), Line(1, Layer, 1000, 0, 1000, 700));
                List<CurveSplitPlan> plans = Plan(net, 0, 500, 0);
                Check.Equal("plans", 2, plans.Count);
                Check.True("first whole", plans[0].WholeCurve);
                Check.True("second whole", plans[1].WholeCurve);
                Check.Equal("curve order", 0, plans[0].CurveIndex);
                Check.Equal("curve order", 1, plans[1].CurveIndex);
            });
        }

        private static void TinySegmentIsBridged()
        {
            Check.Case("a tiny segment inside the run does not split it", () =>
            {
                PipeNetwork net = Net(1, Poly(0, Layer, false, 0, 0, 1000, 0, 1000.3, 0, 2000, 0));
                List<CurveSplitPlan> plans = Plan(net, 0, 500, 0);
                Check.Equal("plans", 1, plans.Count);
                Check.True("whole, with no stray cuts", plans[0].WholeCurve);
            });
        }

        private static void ArcCutPoint()
        {
            Check.Case("a cut on an arc lies on the arc", () =>
            {
                // A quarter circle polyline (1000,0) to (0,1000) about the origin, a branch meeting it half way.
                double bulge = Math.Tan(Math.PI / 8);
                double half = 1000 * Math.Sqrt(0.5);
                PipeNetwork net = Net(1,
                    PolyBulge(0, Layer, false, 1000, 0, bulge, 0, 1000, 0),
                    Line(1, Layer, half, half, half * 2, half * 2));
                List<CurveSplitPlan> plans = Plan(net, 0, 1000, 10);
                Check.Equal("plans", 1, plans.Count);
                CurveSplitPlan plan = plans[0];
                Check.Equal("one cut", 1, plan.CutKeys.Count);
                Check.Near("cut key", 0.5, plan.CutKeys[0], 1e-6);
                Check.Near("cut point is on the circle", 1000, plan.CutPoints[0].Length, 1e-6);
                Check.True("the first half is the run", plan.IsRunAt(new Vec2(1000 * Math.Cos(0.3), 1000 * Math.Sin(0.3))));
                Check.False("the second half is not", plan.IsRunAt(new Vec2(1000 * Math.Cos(1.2), 1000 * Math.Sin(1.2))));
            });
        }

        private static void OnlyTheRunsCurvesArePlanned()
        {
            Check.Case("curves outside the run are not planned", () =>
            {
                PipeNetwork net = Net(1,
                    Line(0, Layer, 0, 0, 1000, 0),
                    Line(1, Layer, 0, 500, 1000, 500));
                List<CurveSplitPlan> plans = Plan(net, 0, 500, 0);
                Check.Equal("plans", 1, plans.Count);
                Check.Equal("the planned curve", 0, plans[0].CurveIndex);
            });
        }
    }
}
