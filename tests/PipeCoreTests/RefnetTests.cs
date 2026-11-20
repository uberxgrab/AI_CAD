using System;
using System.Collections.Generic;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Pipes;
using static PipeCoreTests.Support;

namespace PipeCoreTests
{
    /// <summary>
    /// HVACREFRIG refnets (AGENTS.md, "HVACREFRIG", step 5). A refnet is set back by D from its T node N
    /// (D at least 500, the R2 distance): the main outlet is at N - D along the flow, the inlet 750 s
    /// further upstream, the branch outlet 200 s to the side of the main outlet, and a D-long branch pipe
    /// runs from there to N + 200 s, where the original branch pipe starts. The default scale is 1.24, so
    /// 750 s = 930 and 200 s = 248.
    ///
    /// R1: at least 1000 mm of pipe from an outlet to the next refnet's inlet. R2: at least 500 mm of
    /// straight pipe from an outlet to the next bend or refnet inlet. R3: at most 40 m from the first
    /// refnet's inlet to the farthest indoor unit, with a warning above 37 m.
    /// </summary>
    internal static class RefnetTests
    {
        private const string Up = "EIA_1 RP 28.6-12.7 (33.6-44.8)";
        private const string Through = "EIA_1 RP 22.2-9.5 (28)";
        private const string Branch = "EIA_1 RP 15.9-9.5 (14)";

        public static void Run()
        {
            BranchOnTheLeftMirrors();
            BranchOnTheRight();
            MainAlongY();
            DefaultSetBackAndBranchPipe();
            SetBackFollowsTheSetting();
            UpstreamTooShort();
            UpstreamTooShortForTheSetBack();
            BranchLegTooShort();
            BullHeadTee();
            DegreeFour();
            ExistingRefnet();
            ObliqueBranch();
            TwoRefnetsInSeries();
            BranchThenBranch();
            UpstreamRemainderTooShort();
            NoRootNoRefnets();
            OtherNetworkReported();
            ScaleSetting();
            UpstreamInSeveralLines();
            R1MovesTheUpstreamRefnetBack();
            R1CascadesThroughThreeRefnets();
            R1CannotBeMetStillInserts();
            R2ToABendOnTheThroughSide();
            R2OnTheBranchSide();
            R3WarnsAboveTheAllowance();
            R3OverTheLimit();
            R3FollowsABranch();
            R3SettingsAndNoMovement();
            RotatedMains();
            BranchPipeFallsBackToAValidSetBack();
            BranchPipeThatCannotBePlaced();
            ExistingRefnetsAreMeasured();
        }

        private static IList<PipeCurve> Tee(double branchEndY)
        {
            return new List<PipeCurve>
            {
                Line(0, Up, 0, 0, 5000, 0),
                Line(1, Through, 5000, 0, 9000, 0),
                Line(2, Branch, 5000, 0, 5000, branchEndY)
            };
        }

        private static void Ports(string label, RefnetPlacement r, Vec2 inlet, Vec2 main, Vec2 branch)
        {
            Check.True(label + ": inlet at the block origin", Near(inlet, MapLocal(r, 0, 0), 1e-6));
            Check.True(label + ": main outlet at local (750, 0)", Near(main, MapLocal(r, 750, 0), 1e-6));
            Check.True(label + ": branch outlet at local (750, -200)", Near(branch, MapLocal(r, 750, -200), 1e-6));
        }

        private static void BranchOnTheLeftMirrors()
        {
            Check.Case("a branch on the left mirrors the block", () =>
            {
                RefrigResult r = CleanWithRoot(Tee(3000), V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("refnets", 1, r.Refnets.Count);
                Check.Equal("no reports", 0, r.Reports.Count);
                Check.Equal("no warnings", 0, r.Warnings.Count);
                RefnetPlacement n = r.Refnets[0];
                Check.True("direction", Near(V(1, 0), n.Direction));
                Check.True("mirrored", n.Mirrored);
                Check.Near("scale", 1.24, n.Scale);
                Check.Near("X scale is negative", -1.24, n.XScale);
                Check.Near("rotation is the direction plus 180 degrees", Math.PI, n.Rotation, 1e-9);
                Check.True("inlet 750 s upstream of the main outlet, which is 500 back from the node",
                    Near(V(5000 - 500 - 930, 0), n.Inlet));
                Check.True("main port", Near(V(4500, 0), n.MainPort));
                Check.True("branch port 200 s to the side of the main port", Near(V(4500, 248), n.BranchPort));
                Check.True("the branch turns at 200 s beside the node", Near(V(5000, 248), n.BranchTurn));
                Check.Equal("on the upstream layer", Up, n.Layer);
                Ports("left", n, V(3570, 0), V(4500, 0), V(4500, 248));

                Check.Equal("paths", 3, r.Paths.Count);
                Check.True("upstream pipe ends at the inlet", HasPath(r, V(0, 0), V(3570, 0)));
                Check.True("through pipe starts at the main outlet and runs on through the node",
                    HasPath(r, V(4500, 0), V(9000, 0)));
                Check.False("the node is no longer a vertex of the main", AnyPathHasPoint(r, V(5000, 0)));
                Check.True("branch pipe: D-long pipe to the turn, then the original leg",
                    HasRoute(r, V(4500, 248), V(5000, 248), V(5000, 3000)));
                Check.Equal("the new branch pipe is on the branch layer", Branch,
                    RouteLayer(r, V(4500, 248), V(5000, 248), V(5000, 3000)));
                Check.True("changed", r.Changed);
            });
        }

        private static void BranchOnTheRight()
        {
            Check.Case("a branch on the right does not mirror", () =>
            {
                RefrigResult r = CleanWithRoot(Tee(-3000), V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("refnets", 1, r.Refnets.Count);
                RefnetPlacement n = r.Refnets[0];
                Check.False("not mirrored", n.Mirrored);
                Check.Near("X scale", 1.24, n.XScale);
                Check.Near("rotation", 0, n.Rotation, 1e-9);
                Check.True("branch port", Near(V(4500, -248), n.BranchPort));
                Check.True("branch turn", Near(V(5000, -248), n.BranchTurn));
                Ports("right", n, V(3570, 0), V(4500, 0), V(4500, -248));
                Check.True("branch pipe",
                    HasRoute(r, V(4500, -248), V(5000, -248), V(5000, -3000)));
            });
        }

        private static void MainAlongY()
        {
            Check.Case("a main running along Y", () =>
            {
                RefrigResult r = CleanWithRoot(new List<PipeCurve>
                {
                    Line(0, Up, 0, 0, 0, 5000),
                    Line(1, Through, 0, 5000, 0, 9000),
                    Line(2, Branch, 0, 5000, -3000, 5000)
                }, V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("refnets", 1, r.Refnets.Count);
                RefnetPlacement n = r.Refnets[0];
                Check.True("direction", Near(V(0, 1), n.Direction));
                Check.True("-X is on the left of +Y, so mirrored", n.Mirrored);
                Check.Near("rotation", Math.PI / 2 + Math.PI, n.Rotation, 1e-9);
                Check.True("inlet", Near(V(0, 5000 - 500 - 930), n.Inlet));
                Check.True("branch port", Near(V(-248, 4500), n.BranchPort));
                Ports("along Y", n, V(0, 3570), V(0, 4500), V(-248, 4500));
                Check.True("branch pipe", HasRoute(r, V(-248, 4500), V(-248, 5000), V(-3000, 5000)));
            });
        }

        private static void DefaultSetBackAndBranchPipe()
        {
            Check.Case("the set-back is 500 by default and the branch pipe is 500 long", () =>
            {
                RefrigResult r = CleanWithRoot(Tee(3000), V(0, 0));
                RefnetPlacement n = r.Refnets[0];
                Check.Near("set-back", 500, n.SetBack);
                Check.Near("main outlet is set back from the node", 500, n.Node.DistanceTo(n.MainPort));
                Check.Near("inlet to main outlet is 750 s", 930, n.Inlet.DistanceTo(n.MainPort));
                Check.Near("the D-long branch pipe", 500, n.BranchPort.DistanceTo(n.BranchTurn));
                Check.Near("the branch outlet is 200 s beside the main outlet", 248, n.MainPort.DistanceTo(n.BranchPort));
                Check.True("the branch pipe is parallel to the main",
                    Near(V(0, 0), V((n.BranchTurn - n.BranchPort).Cross(n.Direction), 0)));

                // The branch outlet leaves the block along the flow, so the first straight run on the branch
                // is the D-long pipe up to the turn (R2). The main side ends at a pipe end: nothing to check.
                Check.True("R2 on the branch is the D-long pipe", HasMeasure(r, "R2", 500));
                Check.Equal("one R2 check", 1, ChecksOf(r, "R2").Count);
                Check.True("R2 passes", ChecksOf(r, "R2")[0].Passed);
                Check.Equal("no R1 check with one refnet", 0, ChecksOf(r, "R1").Count);
            });
        }

        private static void SetBackFollowsTheSetting()
        {
            Check.Case("the least set-back is the R2 setting", () =>
            {
                RefrigResult r = CleanWithRoot(Tee(3000), V(0, 0), input => input.Settings.OutletStraight = 800);
                Check.True("ok", r.Ok);
                Check.Equal("refnets", 1, r.Refnets.Count);
                Check.Near("set-back", 800, r.Refnets[0].SetBack);
                Check.True("inlet", Near(V(5000 - 800 - 930, 0), r.Refnets[0].Inlet));
                Check.True("branch pipe", HasRoute(r, V(4200, 248), V(5000, 248), V(5000, 3000)));
                Check.True("R2 on the branch", HasMeasure(r, "R2", 800));
            });
        }

        private static void UpstreamTooShort()
        {
            Check.Case("an upstream run shorter than the body is reported and left alone", () =>
            {
                RefrigResult r = CleanWithRoot(new List<PipeCurve>
                {
                    Line(0, Up, 0, 0, 800, 0),
                    Line(1, Through, 800, 0, 4800, 0),
                    Line(2, Branch, 800, 0, 800, 3000)
                }, V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("no refnet", 0, r.Refnets.Count);
                Check.Equal("one report", 1, r.Reports.Count);
                Check.Contains("report", r.Reports[0], "upstream");
                Check.Equal("report point", 1, r.ReportPoints.Count);
                Check.True("pipes untouched", HasPath(r, V(0, 0), V(800, 0)));
                Check.True("branch untouched", HasPath(r, V(800, 0), V(800, 3000)));
            });
        }

        private static void UpstreamTooShortForTheSetBack()
        {
            Check.Case("a run that holds the body but not the body plus 500 is reported", () =>
            {
                // 1400 is more than 750 s = 930 but less than 930 + 500.
                RefrigResult r = CleanWithRoot(new List<PipeCurve>
                {
                    Line(0, Up, 0, 0, 1400, 0),
                    Line(1, Through, 1400, 0, 5400, 0),
                    Line(2, Branch, 1400, 0, 1400, 3000)
                }, V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("no refnet", 0, r.Refnets.Count);
                Check.Equal("one report", 1, r.Reports.Count);
                Check.Contains("report", r.Reports[0], "upstream");

                RefrigResult fits = CleanWithRoot(new List<PipeCurve>
                {
                    Line(0, Up, 0, 0, 1430, 0),
                    Line(1, Through, 1430, 0, 5430, 0),
                    Line(2, Branch, 1430, 0, 1430, 3000)
                }, V(0, 0));
                Check.Equal("exactly the body plus 500 fits", 1, fits.Refnets.Count);
                Check.True("the inlet is at the root", Near(V(0, 0), fits.Refnets[0].Inlet));
            });
        }

        private static void BranchLegTooShort()
        {
            Check.Case("a branch leg shorter than 200 s is reported and left alone", () =>
            {
                RefrigResult r = CleanWithRoot(Tee(200), V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("no refnet", 0, r.Refnets.Count);
                Check.Equal("one report", 1, r.Reports.Count);
                Check.Contains("report", r.Reports[0], "branch");
                Check.True("pipes untouched", HasPath(r, V(5000, 0), V(5000, 200)));
            });
        }

        private static void BullHeadTee()
        {
            Check.Case("a bull-head tee is reported", () =>
            {
                RefrigResult r = CleanWithRoot(new List<PipeCurve>
                {
                    Line(0, Up, 0, 0, 5000, 0),
                    Line(1, Through, 5000, 0, 5000, 3000),
                    Line(2, Branch, 5000, 0, 5000, -3000)
                }, V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("no refnet", 0, r.Refnets.Count);
                Check.Equal("one report", 1, r.Reports.Count);
                Check.Contains("report", r.Reports[0], "Bull-head");
            });
        }

        private static void DegreeFour()
        {
            Check.Case("a junction of four pipes is reported", () =>
            {
                RefrigResult r = CleanWithRoot(new List<PipeCurve>
                {
                    Line(0, Up, 0, 0, 5000, 0),
                    Line(1, Through, 5000, 0, 9000, 0),
                    Line(2, Branch, 5000, 0, 5000, 3000),
                    Line(3, Branch, 5000, 0, 5000, -3000)
                }, V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("no refnet", 0, r.Refnets.Count);
                Check.Equal("one report", 1, r.Reports.Count);
                Check.Contains("report", r.Reports[0], "Degree-4");
            });
        }

        private static void ExistingRefnet()
        {
            Check.Case("an existing refnet at the node is reported and left alone", () =>
            {
                RefrigResult r = CleanWithRoot(Tee(3000), V(0, 0), input =>
                    input.ExistingRefnetPorts = new List<Vec2> { V(5000, 0.2) });
                Check.True("ok", r.Ok);
                Check.Equal("no refnet", 0, r.Refnets.Count);
                Check.Equal("one report", 1, r.Reports.Count);
                Check.Contains("report", r.Reports[0], "already exists");
                Check.Contains("report names the block", r.Reports[0], "Branch Connection (1)");
                Check.True("pipes untouched", HasPath(r, V(0, 0), V(5000, 0)));

                RefrigResult elsewhere = CleanWithRoot(Tee(3000), V(0, 0), input =>
                    input.ExistingRefnetPorts = new List<Vec2> { V(1000, 0) });
                Check.Equal("a refnet elsewhere does not block this one", 1, elsewhere.Refnets.Count);
            });
        }

        private static void ObliqueBranch()
        {
            Check.Case("a branch that does not leave at right angles is reported", () =>
            {
                RefrigResult r = CleanWithRoot(new List<PipeCurve>
                {
                    Line(0, Up, 0, 0, 5000, 0),
                    Line(1, Through, 5000, 0, 9000, 0),
                    Line(2, Branch, 5000, 0, 7000, 2000)
                }, V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("no refnet", 0, r.Refnets.Count);
                Check.Equal("one report", 1, r.Reports.Count);
                Check.Contains("report", r.Reports[0], "right angles");
            });
        }

        private static void TwoRefnetsInSeries()
        {
            Check.Case("two refnets along one main", () =>
            {
                RefrigResult r = CleanWithRoot(new List<PipeCurve>
                {
                    Line(0, Up, 0, 0, 4000, 0),
                    Line(1, Through, 4000, 0, 8000, 0),
                    Line(2, Through, 8000, 0, 12000, 0),
                    Line(3, Branch, 4000, 0, 4000, 2000),
                    Line(4, Branch, 8000, 0, 8000, -2000)
                }, V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("refnets", 2, r.Refnets.Count);
                Check.Equal("no reports", 0, r.Reports.Count);
                Check.Equal("no warnings", 0, r.Warnings.Count);
                if (r.Refnets.Count < 2)
                    return;

                RefnetPlacement first = r.Refnets[0];
                Check.Near("first set-back", 500, first.SetBack);
                Check.True("first inlet", Near(V(2570, 0), first.Inlet));
                Check.True("first is mirrored (left)", first.Mirrored);
                RefnetPlacement second = r.Refnets[1];
                Check.Near("second set-back", 500, second.SetBack);
                Check.True("second inlet", Near(V(6570, 0), second.Inlet));
                Check.False("second is not mirrored (right)", second.Mirrored);
                Check.True("second branch port", Near(V(7500, -248), second.BranchPort));
                Ports("second", second, V(6570, 0), V(7500, 0), V(7500, -248));

                Check.Equal("paths", 5, r.Paths.Count);
                Check.True("trunk to the first inlet", HasPath(r, V(0, 0), V(2570, 0)));
                Check.True("between the refnets", HasPath(r, V(3500, 0), V(6570, 0)));
                Check.True("after the second", HasPath(r, V(7500, 0), V(12000, 0)));
                Check.True("first branch", HasRoute(r, V(3500, 248), V(4000, 248), V(4000, 2000)));
                Check.True("second branch", HasRoute(r, V(7500, -248), V(8000, -248), V(8000, -2000)));

                Check.True("R1 from the first main outlet to the second inlet is 3070", HasMeasure(r, "R1", 3070));
            });
        }

        private static void BranchThenBranch()
        {
            Check.Case("a refnet on a branch", () =>
            {
                RefrigResult r = CleanWithRoot(new List<PipeCurve>
                {
                    Line(0, Up, 0, 0, 4000, 0),
                    Line(1, Through, 4000, 0, 8000, 0),
                    Line(2, Up, 4000, 0, 4000, 3000),
                    Line(3, Through, 4000, 3000, 4000, 6000),
                    Line(4, Branch, 4000, 3000, 6000, 3000)
                }, V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("refnets", 2, r.Refnets.Count);
                Check.Equal("no reports", 0, r.Reports.Count);
                Check.Equal("no warnings", 0, r.Warnings.Count);
                if (r.Refnets.Count < 2)
                    return;

                RefnetPlacement second = r.Refnets[1];
                Check.True("direction up the branch", Near(V(0, 1), second.Direction));
                Check.False("+X is on the right of +Y", second.Mirrored);
                Check.Near("rotation", Math.PI / 2, second.Rotation, 1e-9);
                // The first branch pipe turns 248 above the first node, leaving 2752 to the second node; the
                // second main outlet is 500 below that node and the inlet 930 below the outlet.
                Check.True("inlet", Near(V(4000, 3000 - 500 - 930), second.Inlet));
                Check.True("branch port", Near(V(4000 + 248, 2500), second.BranchPort));
                Ports("branch", second, V(4000, 1570), V(4000, 2500), V(4248, 2500));
                Check.True("the first branch pipe: the D-long pipe, then the leg to the second inlet",
                    HasRoute(r, V(3500, 248), V(4000, 248), V(4000, 1570)));
                // From the first branch outlet: 500 to the turn, then 1570 - 248 = 1322 to the inlet.
                Check.True("R1 along the branch", HasMeasure(r, "R1", 1822));
            });
        }

        private static void UpstreamRemainderTooShort()
        {
            Check.Case("the run left after the previous refnet's branch pipe counts", () =>
            {
                // The branch is 1000 long. The first refnet's turn takes 248 off its start, leaving 752 < 930 + 500.
                RefrigResult r = CleanWithRoot(new List<PipeCurve>
                {
                    Line(0, Up, 0, 0, 4000, 0),
                    Line(1, Through, 4000, 0, 8000, 0),
                    Line(2, Up, 4000, 0, 4000, 1000),
                    Line(3, Through, 4000, 1000, 4000, 4000),
                    Line(4, Branch, 4000, 1000, 6000, 1000)
                }, V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("only the first refnet", 1, r.Refnets.Count);
                Check.Equal("one report", 1, r.Reports.Count);
                if (r.Reports.Count > 0)
                    Check.Contains("report", r.Reports[0], "upstream");
            });
        }

        private static void NoRootNoRefnets()
        {
            Check.Case("without a root no refnets are placed", () =>
            {
                RefrigResult r = Clean(Tee(3000));
                Check.True("ok", r.Ok);
                Check.Equal("refnets", 0, r.Refnets.Count);
                Check.Equal("reports", 0, r.Reports.Count);
                Check.Equal("no rule checks", 0, r.RuleChecks.Count);
                Check.Equal("no warnings", 0, r.Warnings.Count);
            });
        }

        private static void OtherNetworkReported()
        {
            Check.Case("a network with no root is reported", () =>
            {
                List<PipeCurve> curves = new List<PipeCurve>(Tee(3000));
                curves.Add(Line(3, Up, 0, 10000, 3000, 10000));
                RefrigResult r = CleanWithRoot(curves, V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("components", 2, r.Components);
                Check.Equal("refnets", 1, r.Refnets.Count);
                bool found = false;
                foreach (string report in r.Reports)
                {
                    if (report.IndexOf("not connected to the root", StringComparison.Ordinal) >= 0)
                        found = true;
                }

                Check.True("reported", found);
            });
        }

        private static void ScaleSetting()
        {
            Check.Case("the refnet scale sets the distances", () =>
            {
                RefrigResult r = CleanWithRoot(Tee(3000), V(0, 0), input => input.Settings.RefnetScale = 1.0);
                Check.True("ok", r.Ok);
                Check.Equal("refnets", 1, r.Refnets.Count);
                RefnetPlacement n = r.Refnets[0];
                Check.True("inlet 750 before the main outlet", Near(V(5000 - 500 - 750, 0), n.Inlet));
                Check.True("branch port 200 off the main", Near(V(4500, 200), n.BranchPort));
                Check.Near("X scale", -1.0, n.XScale);

                RefrigResult big = CleanWithRoot(Tee(3000), V(0, 0), input => input.Settings.RefnetScale = 2.0);
                Check.True("inlet at 2.0", Near(V(5000 - 500 - 1500, 0), big.Refnets[0].Inlet));
                Check.True("branch port at 2.0", Near(V(4500, 400), big.Refnets[0].BranchPort));
            });
        }

        private static void UpstreamInSeveralLines()
        {
            Check.Case("an upstream run drawn as several collinear lines is one straight run", () =>
            {
                RefrigResult r = CleanWithRoot(new List<PipeCurve>
                {
                    Line(0, Up, 0, 0, 800, 0),
                    Line(1, Up, 800, 0, 2000, 0),
                    Line(2, Up, 2000, 0, 5000, 0),
                    Line(3, Through, 5000, 0, 9000, 0),
                    Line(4, Branch, 5000, 0, 5000, 3000)
                }, V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("the collinear nodes were dissolved", 2, r.NodesDissolved);
                Check.Equal("refnets", 1, r.Refnets.Count);
                Check.True("upstream pipe is one run to the inlet", HasPath(r, V(0, 0), V(3570, 0)));
            });
        }

        // ------------------------------------------------------------------ R1

        private static IList<PipeCurve> Series(double secondX)
        {
            return new List<PipeCurve>
            {
                Line(0, Up, 0, 0, 4000, 0),
                Line(1, Through, 4000, 0, secondX, 0),
                Line(2, Through, secondX, 0, 9000, 0),
                Line(3, Branch, 4000, 0, 4000, 2000),
                Line(4, Branch, secondX, 0, secondX, -2000)
            };
        }

        private static void R1MovesTheUpstreamRefnetBack()
        {
            Check.Case("R1: an upstream refnet is set back further to leave 1000 before the next inlet", () =>
            {
                // The T nodes are 1500 apart. With both set back 500 the pipe between the first main outlet
                // and the second inlet is 1500 - 930 = 570. The first refnet moves back by 430.
                RefrigResult r = CleanWithRoot(Series(5500), V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("refnets", 2, r.Refnets.Count);
                Check.Equal("no warnings", 0, r.Warnings.Count);
                if (r.Refnets.Count < 2)
                    return;

                Check.Near("the upstream refnet is set back 930", 930, r.Refnets[0].SetBack);
                Check.Near("the downstream refnet stays at 500", 500, r.Refnets[1].SetBack);
                Check.True("first inlet", Near(V(4000 - 930 - 930, 0), r.Refnets[0].Inlet));
                Check.True("first main outlet", Near(V(3070, 0), r.Refnets[0].MainPort));
                Check.True("second inlet", Near(V(5500 - 500 - 930, 0), r.Refnets[1].Inlet));
                Check.True("the pipe between them is exactly 1000", HasPath(r, V(3070, 0), V(4070, 0)));
                Check.True("R1 measured 1000", HasMeasure(r, "R1", 1000));
                foreach (RuleCheck check in ChecksOf(r, "R1"))
                    Check.True("R1 passes", check.Passed);
                Check.True("the branch pipe of the first refnet is 930 long",
                    HasRoute(r, V(3070, 248), V(4000, 248), V(4000, 2000)));
            });

            Check.Case("R1: a spacing of 0 leaves every refnet at the least set-back", () =>
            {
                RefrigResult r = CleanWithRoot(Series(5500), V(0, 0), input =>
                {
                    input.Settings.RefnetSpacing = 0;
                    input.Settings.OutletStraight = 0;
                });
                Check.Equal("refnets", 2, r.Refnets.Count);
                Check.Near("no set-back needed", 0, r.Refnets[0].SetBack);
                Check.Equal("no warnings", 0, r.Warnings.Count);
            });
        }

        private static void R1CascadesThroughThreeRefnets()
        {
            Check.Case("R1: each refnet is set back for the one after it", () =>
            {
                RefrigResult r = CleanWithRoot(new List<PipeCurve>
                {
                    Line(0, Up, 0, 0, 4000, 0),
                    Line(1, Through, 4000, 0, 5500, 0),
                    Line(2, Through, 5500, 0, 7000, 0),
                    Line(3, Through, 7000, 0, 10000, 0),
                    Line(4, Branch, 4000, 0, 4000, 2000),
                    Line(5, Branch, 5500, 0, 5500, -2000),
                    Line(6, Branch, 7000, 0, 7000, 2000)
                }, V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("refnets", 3, r.Refnets.Count);
                Check.Equal("no warnings", 0, r.Warnings.Count);
                if (r.Refnets.Count < 3)
                    return;

                Check.Near("last stays at 500", 500, r.Refnets[2].SetBack);
                Check.Near("middle 500 + 430", 930, r.Refnets[1].SetBack);
                Check.Near("first 930 + 430", 1360, r.Refnets[0].SetBack);
                Check.Equal("R1 checks", 2, ChecksOf(r, "R1").Count);
                foreach (RuleCheck check in ChecksOf(r, "R1"))
                {
                    Check.Near("each gap is 1000", 1000, check.Measured);
                    Check.True("passes", check.Passed);
                }
            });
        }

        private static void R1CannotBeMetStillInserts()
        {
            Check.Case("R1: when no set-back can meet it the refnet is still inserted, with a warning", () =>
            {
                // The first node is 1600 from the root, so its set-back cannot exceed 1600 - 930 = 670 and the
                // gap to the second inlet stays at 670 + 1000 - 500 - 930 = 240.
                RefrigResult r = CleanWithRoot(new List<PipeCurve>
                {
                    Line(0, Up, 0, 0, 1600, 0),
                    Line(1, Through, 1600, 0, 2600, 0),
                    Line(2, Through, 2600, 0, 6000, 0),
                    Line(3, Branch, 1600, 0, 1600, 2000),
                    Line(4, Branch, 2600, 0, 2600, -2000)
                }, V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("both refnets are inserted", 2, r.Refnets.Count);
                Check.Equal("no refusal", 0, r.Reports.Count);
                if (r.Refnets.Count < 2)
                    return;

                Check.Near("the upstream refnet is set back as far as the run allows", 670, r.Refnets[0].SetBack);
                Check.True("its inlet is at the root", Near(V(0, 0), r.Refnets[0].Inlet));
                Check.Near("the second stays at 500", 500, r.Refnets[1].SetBack);
                Check.True("R1 measured 240", HasMeasure(r, "R1", 240));
                Check.Equal("an R1 warning", 1, WarningsWith(r, "R1"));
                Check.Equal("an R2 warning for the same short pipe", 1, WarningsWith(r, "R2"));
                Check.Contains("R1 warning gives the measured value", r.Warnings[0], "240 mm");
                Check.Contains("R1 warning gives the required value", r.Warnings[0], "1000 mm");
                Check.Contains("R1 warning names both refnets", r.Warnings[0], "(1600, 0)");
                Check.Contains("R1 warning names both refnets", r.Warnings[0], "(2600, 0)");
                Check.Equal("a circle for each warning", r.Warnings.Count, r.WarningPoints.Count);
                bool circled = false;
                foreach (Vec2 point in r.WarningPoints)
                {
                    if (Near(point, V(2600, 0)))
                        circled = true;
                }

                Check.True("the downstream refnet is circled", circled);
                Check.True("the pipe between the bodies", HasPath(r, V(930, 0), V(1170, 0)));
            });
        }

        // ------------------------------------------------------------------ R2

        private static void R2ToABendOnTheThroughSide()
        {
            Check.Case("R2: straight pipe on the through side is measured to the next bend", () =>
            {
                RefrigResult r = CleanWithRoot(new List<PipeCurve>
                {
                    Line(0, Up, 0, 0, 5000, 0),
                    Line(1, Through, 5000, 0, 5250, 0),
                    Line(2, Through, 5250, 0, 5250, 4000),
                    Line(3, Branch, 5000, 0, 5000, -3000)
                }, V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("refnet", 1, r.Refnets.Count);
                Check.Equal("no warnings", 0, r.Warnings.Count);
                // From the main outlet, 500 back from the node, through the node and 250 on to the bend.
                Check.True("main side: 500 + 250 to the bend", HasMeasure(r, "R2", 750));
                Check.True("branch side: the D-long pipe to the turn", HasMeasure(r, "R2", 500));
                Check.Equal("two R2 checks", 2, ChecksOf(r, "R2").Count);
                foreach (RuleCheck check in ChecksOf(r, "R2"))
                    Check.True("passes", check.Passed);

                RefrigResult more = CleanWithRoot(new List<PipeCurve>
                {
                    Line(0, Up, 0, 0, 5000, 0),
                    Line(1, Through, 5000, 0, 5250, 0),
                    Line(2, Through, 5250, 0, 5250, 4000),
                    Line(3, Branch, 5000, 0, 5000, -3000)
                }, V(0, 0), input => input.Settings.OutletStraight = 700);
                Check.True("with R2 = 700 the main side is 950", HasMeasure(more, "R2", 950));
                Check.True("and the branch side 700", HasMeasure(more, "R2", 700));
            });
        }

        private static void R2OnTheBranchSide()
        {
            Check.Case("R2: the branch side is the D-long pipe, then the next leg", () =>
            {
                // The branch turns at the D-long pipe's end and runs 2752 more to a bend; R2 counts only the
                // straight pipe from the outlet, which ends at the turn.
                RefrigResult r = CleanWithRoot(new List<PipeCurve>
                {
                    Line(0, Up, 0, 0, 5000, 0),
                    Line(1, Through, 5000, 0, 9000, 0),
                    Line(2, Branch, 5000, 0, 5000, 3000),
                    Line(3, Branch, 5000, 3000, 7000, 3000)
                }, V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("refnet", 1, r.Refnets.Count);
                Check.Equal("no warnings", 0, r.Warnings.Count);
                Check.True("R2 branch side 500", HasMeasure(r, "R2", 500));
                Check.True("the branch route has two bends", HasRoute(r,
                    V(4500, 248), V(5000, 248), V(5000, 3000), V(7000, 3000)));
            });
        }

        // ------------------------------------------------------------------ R3

        // The first refnet's inlet is at x = 3000 - 500 - 930 = 1570, so a main leaf at x = 1570 + L is
        // L mm of pipe (body included) from the inlet.
        private static IList<PipeCurve> LongMain(double leafX, double branchEndY = 3000)
        {
            return new List<PipeCurve>
            {
                Line(0, Up, 0, 0, 3000, 0),
                Line(1, Through, 3000, 0, leafX, 0),
                Line(2, Branch, 3000, 0, 3000, branchEndY)
            };
        }

        private static void R3WarnsAboveTheAllowance()
        {
            Check.Case("R3: no warning at 36.9 m, a warning above 37 m", () =>
            {
                RefrigResult ok = CleanWithRoot(LongMain(1570 + 36900), V(0, 0));
                Check.True("ok", ok.Ok);
                Check.Equal("refnet", 1, ok.Refnets.Count);
                Check.Near("measured 36900", 36900, ok.FarthestUnitDistance);
                Check.True("the farthest unit is the main's end", Near(V(1570 + 36900, 0), ok.FarthestUnit));
                Check.True("the first inlet", Near(V(1570, 0), ok.FirstRefnetInlet));
                Check.Equal("no R3 warning at 36.9 m", 0, WarningsWith(ok, "R3"));
                Check.Equal("no warnings at all", 0, ok.Warnings.Count);
                Check.Equal("one R3 check", 1, ChecksOf(ok, "R3").Count);
                Check.True("it passed", ChecksOf(ok, "R3")[0].Passed);
                Check.Near("the warning level is 37000", 37000, ChecksOf(ok, "R3")[0].Required);

                RefrigResult exactly = CleanWithRoot(LongMain(1570 + 37000), V(0, 0));
                Check.Equal("no warning at exactly 37 m", 0, WarningsWith(exactly, "R3"));

                RefrigResult warn = CleanWithRoot(LongMain(1570 + 37100), V(0, 0));
                Check.True("ok", warn.Ok);
                Check.Equal("refnet is still placed", 1, warn.Refnets.Count);
                Check.Near("measured 37100", 37100, warn.FarthestUnitDistance);
                Check.Equal("one R3 warning at 37.1 m", 1, WarningsWith(warn, "R3"));
                Check.Contains("names the unit", warn.Warnings[0], "(38670, 0)");
                Check.Contains("gives the length", warn.Warnings[0], "37.10 m");
                Check.Contains("and the 37 m level", warn.Warnings[0], "37.00 m");
                Check.False("37.1 m is not over the 40 m limit", warn.Warnings[0].Contains("over the 40.00 m limit"));
                Check.True("the unit is circled", Near(warn.WarningPoints[0], V(1570 + 37100, 0)));
                Check.False("the check failed", ChecksOf(warn, "R3")[0].Passed);
            });
        }

        private static void R3OverTheLimit()
        {
            Check.Case("R3: over 40 m is a warning that names the limit", () =>
            {
                RefrigResult r = CleanWithRoot(LongMain(1570 + 40100), V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("refnet is placed", 1, r.Refnets.Count);
                Check.Equal("one R3 warning", 1, WarningsWith(r, "R3"));
                Check.Contains("the length", r.Warnings[0], "40.10 m");
                Check.Contains("the limit", r.Warnings[0], "over the 40.00 m limit");
                Check.Contains("the unit", r.Warnings[0], "(41670, 0)");

                RefrigResult exactly = CleanWithRoot(LongMain(1570 + 40000), V(0, 0));
                Check.Equal("exactly 40 m is only past the allowance", 1, WarningsWith(exactly, "R3"));
                Check.False("and not over the limit", exactly.Warnings[0].Contains("over the 40.00 m limit"));
            });
        }

        private static void R3FollowsABranch()
        {
            Check.Case("R3: a branch is measured through the real outlet geometry", () =>
            {
                RefrigResult r = CleanWithRoot(LongMain(4000, 5000), V(0, 0));
                Check.True("ok", r.Ok);
                // Inlet to branch outlet: the chord of the block, sqrt(930^2 + 248^2); then the D-long pipe
                // (500) to the turn; then the leg from y = 248 to y = 5000.
                double expected = Math.Sqrt(930.0 * 930.0 + 248.0 * 248.0) + 500 + (5000 - 248);
                Check.Near("branch distance", expected, r.FarthestUnitDistance, 1e-6);
                Check.True("the farthest unit is the branch end", Near(V(3000, 5000), r.FarthestUnit));

                RefrigResult far = CleanWithRoot(LongMain(4000, 40000), V(0, 0));
                Check.Equal("a long branch warns", 1, WarningsWith(far, "R3"));
                Check.Contains("branch end named", far.Warnings[0], "(3000, 40000)");
            });
        }

        private static void R3SettingsAndNoMovement()
        {
            Check.Case("R3 only warns: nothing moves, and the settings change the levels", () =>
            {
                RefrigResult near = CleanWithRoot(LongMain(1570 + 10000), V(0, 0));
                RefrigResult far = CleanWithRoot(LongMain(1570 + 45000), V(0, 0));
                Check.Equal("same refnets", near.Refnets.Count, far.Refnets.Count);
                Check.Near("same set-back", near.Refnets[0].SetBack, far.Refnets[0].SetBack);
                Check.True("same inlet", Near(near.Refnets[0].Inlet, far.Refnets[0].Inlet));
                Check.Equal("same number of paths", near.Paths.Count, far.Paths.Count);
                Check.Equal("no reports from R3", 0, far.Reports.Count);

                RefrigResult noAllowance = CleanWithRoot(LongMain(1570 + 39000), V(0, 0), input =>
                    input.Settings.RunAllowance = 0);
                Check.Equal("with no allowance 39 m is fine", 0, noAllowance.Warnings.Count);

                RefrigResult shorter = CleanWithRoot(LongMain(1570 + 29000), V(0, 0), input =>
                {
                    input.Settings.MaxUnitRun = 30000;
                    input.Settings.RunAllowance = 2000;
                });
                Check.Equal("a 30 m limit with a 2 m allowance warns at 29 m", 1, WarningsWith(shorter, "R3"));
                Check.Contains("and says 28.00 m", shorter.Warnings[0], "28.00 m");

                RefrigResult none = CleanWithRoot(new List<PipeCurve>
                {
                    Line(0, Up, 0, 0, 50000, 0)
                }, V(0, 0));
                Check.True("ok", none.Ok);
                Check.Equal("no refnet, no R3", 0, ChecksOf(none, "R3").Count);
                Check.True("no distance", none.FarthestUnitDistance < 0);
            });
        }

        // ------------------------------------------------------------------ geometry

        private static PipeCurve Rotated(PipeCurve source, int id, string layer, double radians)
        {
            Vec2 a = Rotate(source.SegmentStart(0), radians);
            Vec2 b = Rotate(source.SegmentEnd(0), radians);
            return Line(id, layer, a.X, a.Y, b.X, b.Y);
        }

        private static void RotatedMains()
        {
            Check.Case("a main rotated 30 degrees with a left branch", () =>
            {
                double angle = 30 * Math.PI / 180.0;
                IList<PipeCurve> source = Tee(3000);
                RefrigResult r = CleanWithRoot(new List<PipeCurve>
                {
                    Rotated(source[0], 0, Up, angle),
                    Rotated(source[1], 1, Through, angle),
                    Rotated(source[2], 2, Branch, angle)
                }, V(0, 0), input => input.BaseAngle = angle);
                Check.True("ok", r.Ok);
                Check.Equal("refnets", 1, r.Refnets.Count);
                Check.Equal("no warnings", 0, r.Warnings.Count);
                RefnetPlacement n = r.Refnets[0];
                Check.True("direction", Near(Rotate(V(1, 0), angle), n.Direction, 1e-6));
                Check.True("mirrored: the branch is on the left", n.Mirrored);
                Check.Near("set-back", 500, n.SetBack, 1e-6);
                Check.True("inlet", Near(Rotate(V(3570, 0), angle), n.Inlet, 1e-6));
                Check.True("main port", Near(Rotate(V(4500, 0), angle), n.MainPort, 1e-6));
                Check.True("branch port", Near(Rotate(V(4500, 248), angle), n.BranchPort, 1e-6));
                Check.True("branch turn", Near(Rotate(V(5000, 248), angle), n.BranchTurn, 1e-6));
                Ports("rotated", n, Rotate(V(3570, 0), angle), Rotate(V(4500, 0), angle), Rotate(V(4500, 248), angle));
                Check.True("branch pipe", HasRoute(r,
                    Rotate(V(4500, 248), angle), Rotate(V(5000, 248), angle), Rotate(V(5000, 3000), angle)));
                Check.True("R2 on the branch is 500", HasMeasure(r, "R2", 500, 1e-6));
            });

            Check.Case("a main rotated to point back along -X with a right branch", () =>
            {
                double angle = 200 * Math.PI / 180.0;
                IList<PipeCurve> source = Tee(-3000);
                RefrigResult r = CleanWithRoot(new List<PipeCurve>
                {
                    Rotated(source[0], 0, Up, angle),
                    Rotated(source[1], 1, Through, angle),
                    Rotated(source[2], 2, Branch, angle)
                }, V(0, 0), input => input.BaseAngle = angle);
                Check.True("ok", r.Ok);
                Check.Equal("refnets", 1, r.Refnets.Count);
                RefnetPlacement n = r.Refnets[0];
                Check.False("not mirrored: the branch is on the right", n.Mirrored);
                Check.True("inlet", Near(Rotate(V(3570, 0), angle), n.Inlet, 1e-6));
                Check.True("branch port", Near(Rotate(V(4500, -248), angle), n.BranchPort, 1e-6));
                Check.True("branch turn", Near(Rotate(V(5000, -248), angle), n.BranchTurn, 1e-6));
                Ports("rotated 200", n, Rotate(V(3570, 0), angle), Rotate(V(4500, 0), angle), Rotate(V(4500, -248), angle));
            });
        }

        // ------------------------------------------------------------------ branch pipe clashes

        private static void BranchPipeFallsBackToAValidSetBack()
        {
            Check.Case("a branch pipe that would cross a pipe uses the largest set-back that does not", () =>
            {
                // R1 wants the first refnet set back 930, so its branch pipe would run 930 back from x = 4000
                // along y = 248. A separate pipe crosses that line at x = 3300, 700 back, so 699 is the most.
                List<PipeCurve> curves = new List<PipeCurve>(Series(5500));
                curves.Add(Line(5, Branch, 3300, 100, 3300, 400));
                RefrigResult r = CleanWithRoot(curves, V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("both refnets are inserted", 2, r.Refnets.Count);
                if (r.Refnets.Count < 2)
                    return;

                Check.Near("set-back limited by the crossing pipe", 699, r.Refnets[0].SetBack);
                Check.True("the branch outlet stops short of the pipe", r.Refnets[0].BranchPort.X > 3300);
                Check.True("R1 is short: 699 + 1500 - 500 - 930", HasMeasure(r, "R1", 769));
                Check.Equal("an R1 warning", 1, WarningsWith(r, "R1"));
                Check.True("the crossing pipe is untouched", HasPath(r, V(3300, 100), V(3300, 400)));
            });
        }

        private static void BranchPipeThatCannotBePlaced()
        {
            Check.Case("a branch pipe that cannot clear a pipe even at the least set-back gets no refnet", () =>
            {
                // The line y = 248 is crossed 300 back from the turn, less than the 500 set-back.
                List<PipeCurve> curves = new List<PipeCurve>(Tee(3000));
                curves.Add(Line(3, Branch, 4700, 100, 4700, 400));
                RefrigResult r = CleanWithRoot(curves, V(0, 0));
                Check.True("ok", r.Ok);
                Check.Equal("no refnet", 0, r.Refnets.Count);
                bool reported = false;
                foreach (string report in r.Reports)
                {
                    if (report.IndexOf("cross or overlap", StringComparison.Ordinal) >= 0)
                        reported = true;
                }

                Check.True("reported", reported);
                Check.True("the pipes are untouched", HasPath(r, V(0, 0), V(5000, 0)));
                Check.True("the branch is untouched", HasPath(r, V(5000, 0), V(5000, 3000)));

                // The same pipe lying along the new branch pipe is an overlap.
                List<PipeCurve> along = new List<PipeCurve>(Tee(3000));
                along.Add(Line(3, Branch, 4000, 248, 4800, 248));
                RefrigResult overlap = CleanWithRoot(along, V(0, 0));
                Check.Equal("an overlapping pipe also blocks the refnet", 0, overlap.Refnets.Count);
            });
        }

        // ------------------------------------------------------------------ existing refnets

        private static void ExistingRefnetsAreMeasured()
        {
            Check.Case("refnets already in the drawing are measured when their ports meet pipe ends", () =>
            {
                // A network written by an earlier run: the pipes stop at the block's three ports.
                RefrigResult r = CleanWithRoot(new List<PipeCurve>
                {
                    Line(0, Up, 0, 0, 1570, 0),
                    Line(1, Through, 2500, 0, 39000, 0),
                    Line(2, Branch, 2500, 248, 2500, 3000)
                }, V(0, 0), input =>
                    input.ExistingRefnetPorts = new List<Vec2> { V(1570, 0), V(2500, 0), V(2500, 248) });
                Check.True("ok", r.Ok);
                Check.Equal("nothing to place", 0, r.Refnets.Count);
                Check.False("nothing to write", r.Changed);
                Check.True("the first refnet's inlet", Near(V(1570, 0), r.FirstRefnetInlet));
                // 930 through the body, then 36500 to the end of the main.
                Check.Near("R3 across the block", 930 + 36500, r.FarthestUnitDistance);
                Check.Equal("R3 warns", 1, WarningsWith(r, "R3"));
                Check.True("the unit is circled", Near(V(39000, 0), r.WarningPoints[0]));

                RefrigResult fine = CleanWithRoot(new List<PipeCurve>
                {
                    Line(0, Up, 0, 0, 1570, 0),
                    Line(1, Through, 2500, 0, 20000, 0),
                    Line(2, Branch, 2500, 248, 2500, 3000)
                }, V(0, 0), input =>
                    input.ExistingRefnetPorts = new List<Vec2> { V(1570, 0), V(2500, 0), V(2500, 248) });
                Check.Equal("no warning for a shorter main", 0, fine.Warnings.Count);
                Check.False("nothing to write", fine.Changed);
                Check.Near("measured", 930 + 17500, fine.FarthestUnitDistance);
            });
        }
    }
}
