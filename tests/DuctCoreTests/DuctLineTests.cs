using System.Collections.Generic;
using System.Linq;
using HvacAiCadAddin.Ducts;

namespace DuctCoreTests
{
    /// <summary>HVACDUCTLINE: single lines to duct runs (straightening, joining, tree, runs, taps).</summary>
    internal static class DuctLineTests
    {
        private static readonly DuctSize S600 = DuctSize.Rect(600, 300);
        private static readonly DuctSize S400 = DuctSize.Rect(400, 300);
        private static readonly DuctSize S300 = DuctSize.Rect(300, 300);

        public static void Run()
        {
            Straightening();
            TeeWithTap();
            SizeChangeAndElbow();
            GapIsWelded();
            OtherSystemNotJoined();
            Loop();
            TooShortBranch();
            Bullhead();
            UntaggedAndRoot();
            Options();
        }

        private static DuctLineCurve Curve(int id, string system, DuctSize size, params double[] xyb)
        {
            DuctLineCurve curve = new DuctLineCurve { Id = id, System = system, Size = size, Layer = "M-HVAC-DUCT-" + system };
            for (int i = 0; i + 2 < xyb.Length + 1; i += 3)
                curve.Vertices.Add(new OutlineVertex(xyb[i], xyb[i + 1], i + 2 < xyb.Length ? xyb[i + 2] : 0));
            return curve;
        }

        private static DuctLinePlan Plan(Vec2 root, params DuctLineCurve[] curves)
        {
            return DuctLinePlanner.Plan(curves, root, new DuctLineSettings());
        }

        private static string Pts(IEnumerable<Vec2> points)
        {
            return string.Join(" ", points.Select(p => p.X.ToString("0") + "," + p.Y.ToString("0")));
        }

        private static void Straightening()
        {
            Check.Case("dl straighten", () =>
            {
                int bends, hops;
                // A 300 mm radius bend between an X leg and a Y leg: the corner replaces the arc.
                List<Vec2> bend = DuctLinePlanner.Straighten(new[]
                {
                    new OutlineVertex(0, 0, 0), new OutlineVertex(1000, 0, 0.41421356), new OutlineVertex(1300, 300, 0),
                    new OutlineVertex(1300, 2000, 0)
                }, false, out bends, out hops);
                Check.Equal("dl bend corner", "0,0 1300,0 1300,2000", Pts(bend));
                Check.Equal("dl one bend", 1, bends);

                // A hop over a crossing duct: collinear legs, the chord continues the line.
                List<Vec2> hop = DuctLinePlanner.Straighten(new[]
                {
                    new OutlineVertex(0, 0, 0), new OutlineVertex(1000, 0, 1), new OutlineVertex(1400, 0, 0),
                    new OutlineVertex(3000, 0, 0)
                }, false, out bends, out hops);
                Check.Equal("dl hop chord", "0,0 1000,0 1400,0 3000,0", Pts(hop));
                Check.Equal("dl one hop", 1, hops);

                List<Vec2> endArc = DuctLinePlanner.Straighten(new[]
                {
                    new OutlineVertex(0, 0, 0), new OutlineVertex(1000, 0, 0.5), new OutlineVertex(1500, 500, 0)
                }, false, out bends, out hops);
                Check.Equal("dl end arc becomes its chord", "0,0 1000,0 1500,500", Pts(endArc));

                List<Vec2> spline = DuctLinePlanner.Straighten(new[]
                {
                    new OutlineVertex(0, 0, 0), new OutlineVertex(500, 300, 0), new OutlineVertex(1000, 0, 0)
                }, true, out bends, out hops);
                Check.Equal("dl spline ends", "0,0 1000,0", Pts(spline));
            });
        }

        private static void TeeWithTap()
        {
            Check.Case("dl tee", () =>
            {
                DuctLinePlan plan = Plan(new Vec2(10, 10),
                    Curve(0, "SA", S600, 0, 0, 0, 10000, 0, 0),
                    Curve(1, "SA", S300, 5000, 0, 0, 5000, 4000, 0));
                Check.True("dl ok", plan.Ok, plan.Error);
                Check.Equal("dl two runs", 2, plan.Runs.Count);
                Check.Equal("dl one tee", 1, plan.Tees);
                DuctLineRun main = plan.Runs.Single(r => !r.StartsWithTap);
                Check.Equal("dl main straight through the tee", "0,0 5000,0 10000,0", Pts(main.Points));
                Check.True("dl main solved", main.Ok, main.Error);
                Check.Equal("dl main has no fitting", 0, main.Result.Fittings.Count);
                DuctLineRun branch = plan.Runs.Single(r => r.StartsWithTap);
                Check.Equal("dl branch starts on the wall", "5000,300 5000,4000", Pts(branch.Points));
                Check.Near("dl tap axis along the main", 1, branch.TapMainAxis.X);
                Check.Near("dl tap outward", 1, branch.TapOutward.Y);
                Check.True("dl branch solved", branch.Ok, branch.Error);
                Check.Near("dl branch duct starts after the tap lead", 300 + DuctFittings.TapLead(S300, new FittingSettings()),
                    branch.Result.Straights[0].Start.Y, 1e-6);
                Check.Equal("dl both curves done", 2, plan.DoneCurves.Count);
                Check.Equal("dl group", "SA", main.Group);
                Check.Equal("dl SA layer", "EIA_1 SUPPLY AIR", DuctLinePlanner.LayerForSystem(main.Group));
            });
        }

        private static void SizeChangeAndElbow()
        {
            Check.Case("dl size change and elbow", () =>
            {
                DuctLinePlan plan = Plan(new Vec2(0, 0),
                    Curve(0, "SA", S600, 0, 0, 0, 5000, 0, 0),
                    Curve(1, "SA", S400, 5000, 0, 0, 9000, 0, 0, 9000, 5000, 0),
                    Curve(2, "SA", S300, 5000, 0, 0, 5000, -3000, 0));
                DuctLineRun main = plan.Runs.Single(r => !r.StartsWithTap);
                Check.Equal("dl main points", "0,0 5000,0 9000,0 9000,5000", Pts(main.Points));
                Check.True("dl solved", main.Ok, main.Error);
                Check.True("dl transition at the tee", main.Result.Fittings.Any(f => f.Kind == RouteFitting.TransitionKind && f.Vertex == 1));
                Check.True("dl elbow at the corner", main.Result.Fittings.Any(f => f.Kind == RouteFitting.ElbowKind && f.Vertex == 2));
                DuctLineRun branch = plan.Runs.Single(r => r.StartsWithTap);
                Check.Near("dl tap on the wall of the incoming 600 duct", -300, branch.TapWallPoint.Y);
                Check.Near("dl branch goes down", -1, branch.TapOutward.Y);
            });
        }

        private static void GapIsWelded()
        {
            Check.Case("dl gap", () =>
            {
                DuctLinePlan plan = Plan(new Vec2(0, 0),
                    Curve(0, "SA", S600, 0, 0, 0, 10000, 0, 0),
                    Curve(1, "SA", S300, 5000, 60, 0, 5000, 4000, 0));
                Check.Equal("dl branch end 60 mm off still a tee", 1, plan.Tees);
                Check.True("dl branch tapped", plan.Runs.Any(r => r.StartsWithTap));
                DuctLinePlan far = DuctLinePlanner.Plan(new[]
                {
                    Curve(0, "SA", S600, 0, 0, 0, 10000, 0, 0), Curve(1, "SA", S300, 5000, 300, 0, 5000, 4000, 0)
                }, new Vec2(0, 0), new DuctLineSettings { WeldTolerance = 100 });
                Check.Equal("dl 300 mm gap is not joined", 0, far.Tees);
                Check.True("dl gap reported", far.Reports.Any(r => r.Contains("not connected")));
                Check.False("dl unreached curve not done", far.DoneCurves.Contains(1));
            });
        }

        private static void OtherSystemNotJoined()
        {
            Check.Case("dl other system", () =>
            {
                DuctLinePlan plan = Plan(new Vec2(0, 0),
                    Curve(0, "SA", S600, 0, 0, 0, 10000, 0, 0),
                    Curve(1, "RA", S300, 5000, 0, 0, 5000, 4000, 0));
                Check.Equal("dl RA end on SA is not a tee", 0, plan.Tees);
                Check.Equal("dl one run", 1, plan.Runs.Count);
                Check.False("dl RA left alone", plan.DoneCurves.Contains(1));
            });
        }

        private static void Loop()
        {
            Check.Case("dl loop", () =>
            {
                DuctLinePlan plan = Plan(new Vec2(-3000, 0),
                    Curve(0, "SA", S600, -3000, 0, 0, 0, 0, 0),
                    Curve(1, "SA", S400, 0, 0, 0, 5000, 0, 0, 5000, 5000, 0, 0, 5000, 0, 0, 0, 0));
                Check.True("dl loop reported", plan.Reports.Any(r => r.Contains("loop")), string.Join("|", plan.Reports));
                Check.True("dl rest still drawn", plan.Ok, plan.Error);
            });
        }

        private static void TooShortBranch()
        {
            Check.Case("dl too short", () =>
            {
                DuctLinePlan plan = Plan(new Vec2(0, 0),
                    Curve(0, "SA", S600, 0, 0, 0, 10000, 0, 0),
                    Curve(1, "SA", S300, 5000, 0, 0, 5000, 200, 0));
                DuctLineRun branch = plan.Runs.Single(r => r.StartsWithTap);
                Check.False("dl short branch not solved", branch.Ok);
                Check.True("dl reported", plan.Reports.Any(r => r.StartsWith("A run could not be drawn")));
                Check.False("dl its line is kept", plan.DoneCurves.Contains(1));
                Check.True("dl main still drawn", plan.DoneCurves.Contains(0) && plan.Ok);
            });
        }

        private static void Bullhead()
        {
            Check.Case("dl bullhead", () =>
            {
                DuctLinePlan plan = Plan(new Vec2(0, 0),
                    Curve(0, "SA", S600, 0, 0, 0, 5000, 0, 0),
                    Curve(1, "SA", S300, 5000, 0, 0, 5000, -4000, 0),
                    Curve(2, "SA", S400, 5000, 0, 0, 5000, 4000, 0));
                DuctLineRun main = plan.Runs.First(r => r.Points[0].X == 0);
                Check.Equal("dl follows the larger branch", "0,0 5000,0 5000,4000", Pts(main.Points));
                Check.True("dl bullhead reported", plan.Reports.Any(r => r.Contains("bullhead")));
                Check.False("dl other side without tap", plan.Runs.Any(r => r.StartsWithTap));
            });
        }

        private static void UntaggedAndRoot()
        {
            Check.Case("dl untagged and root", () =>
            {
                DuctLinePlan plan = DuctLinePlanner.Plan(new[] { Curve(0, "", null, 0, 0, 0, 6000, 0, 0) }, new Vec2(0, 0),
                    new DuctLineSettings { DefaultSize = S400 });
                Check.Equal("dl untagged counted", 1, plan.UntaggedCurves);
                Check.Equal("dl default size used", "400x300", plan.Runs[0].Sizes[0].Label);
                Check.True("dl untagged group has no system layer", DuctLinePlanner.LayerForSystem(plan.Runs[0].Group) == null);
                DuctLinePlan far = Plan(new Vec2(50000, 50000), Curve(0, "SA", S600, 0, 0, 0, 6000, 0, 0));
                Check.False("dl root too far", far.Ok);
                Check.Contains("dl root message", far.Error, "Pick the start point");
            });
        }

        private static void Options()
        {
            Check.Case("dl options", () =>
            {
                DuctLineOptions o = new DuctLineOptions { SizeText = "D250", Layer = "EIA_1 RETURN AIR", WeldTolerance = 50, EraseLines = true };
                DuctLineOptions back = DuctLineOptions.Deserialize(o.Serialize());
                Check.Equal("dl size", "D250", back.SizeText);
                Check.Equal("dl layer", "EIA_1 RETURN AIR", back.Layer);
                Check.Near("dl weld", 50, back.WeldTolerance);
                Check.True("dl erase", back.EraseLines);
                DuctSize size;
                Check.True("dl round parsed", back.TryGetSize(out size) && size.IsRound);
                DuctLineOptions bad = DuctLineOptions.Deserialize("size=abc\nweld=0\n");
                Check.Equal("dl bad size ignored", "300x300", bad.SizeText);
                Check.Near("dl bad weld ignored", 100, bad.WeldTolerance);
                string error;
                Check.False("dl invalid size", new DuctLineOptions { SizeText = "x" }.IsValid(out error));
            });
        }
    }
}
