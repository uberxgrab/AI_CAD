using System;
using System.Collections.Generic;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Pipes;
using static PipeCoreTests.Support;

namespace PipeCoreTests
{
    /// <summary>
    /// HVACREFRIG refnets (AGENTS.md, "HVACREFRIG", step 5). At scale s the block's inlet is at
    /// N - 750 s along the flow, its main outlet is at N and its branch outlet at N + 200 s toward the
    /// branch. The default scale is 1.24, so 750 s = 930 and 200 s = 248.
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
            UpstreamTooShort();
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
                RefnetPlacement n = r.Refnets[0];
                Check.True("direction", Near(V(1, 0), n.Direction));
                Check.True("mirrored", n.Mirrored);
                Check.Near("scale", 1.24, n.Scale);
                Check.Near("X scale is negative", -1.24, n.XScale);
                Check.Near("rotation is the direction plus 180 degrees", Math.PI, n.Rotation, 1e-9);
                Check.True("inlet 750 s upstream of the node", Near(V(5000 - 930, 0), n.Inlet));
                Check.True("main port", Near(V(5000, 0), n.MainPort));
                Check.True("branch port 200 s toward the branch", Near(V(5000, 248), n.BranchPort));
                Check.Equal("on the upstream layer", Up, n.Layer);
                Ports("left", n, V(4070, 0), V(5000, 0), V(5000, 248));

                Check.Equal("paths", 3, r.Paths.Count);
                Check.True("upstream pipe ends at the inlet", HasPath(r, V(0, 0), V(4070, 0)));
                Check.True("through pipe starts at the node", HasPath(r, V(5000, 0), V(9000, 0)));
                Check.True("branch pipe starts at the branch port", HasPath(r, V(5000, 248), V(5000, 3000)));
                Check.True("changed", r.Changed);

                foreach (RefrigPath path in r.Paths)
                {
                    if (HasPath(r, V(5000, 248), V(5000, 3000)) && Near(path.Points[0], V(5000, 248)))
                        Check.Equal("branch layer kept", Branch, path.Layer);
                }
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
                Check.True("branch port", Near(V(5000, -248), n.BranchPort));
                Ports("right", n, V(4070, 0), V(5000, 0), V(5000, -248));
                Check.True("branch pipe starts at the branch port", HasPath(r, V(5000, -248), V(5000, -3000)));
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
                Check.True("inlet", Near(V(0, 5000 - 930), n.Inlet));
                Check.True("branch port", Near(V(-248, 5000), n.BranchPort));
                Ports("along Y", n, V(0, 4070), V(0, 5000), V(-248, 5000));
            });
        }

        private static void UpstreamTooShort()
        {
            Check.Case("an upstream run shorter than 750 s is reported and left alone", () =>
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
                if (r.Refnets.Count < 2)
                    return;

                RefnetPlacement first = r.Refnets[0];
                Check.True("first inlet", Near(V(3070, 0), first.Inlet));
                Check.True("first is mirrored (left)", first.Mirrored);
                RefnetPlacement second = r.Refnets[1];
                Check.True("second inlet", Near(V(7070, 0), second.Inlet));
                Check.False("second is not mirrored (right)", second.Mirrored);
                Check.True("second branch port", Near(V(8000, -248), second.BranchPort));
                Ports("second", second, V(7070, 0), V(8000, 0), V(8000, -248));

                Check.Equal("paths", 5, r.Paths.Count);
                Check.True("trunk to the first inlet", HasPath(r, V(0, 0), V(3070, 0)));
                Check.True("between the refnets", HasPath(r, V(4000, 0), V(7070, 0)));
                Check.True("after the second", HasPath(r, V(8000, 0), V(12000, 0)));
                Check.True("first branch", HasPath(r, V(4000, 248), V(4000, 2000)));
                Check.True("second branch", HasPath(r, V(8000, -248), V(8000, -2000)));
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
                if (r.Refnets.Count < 2)
                    return;

                RefnetPlacement second = r.Refnets[1];
                Check.True("direction up the branch", Near(V(0, 1), second.Direction));
                Check.False("+X is on the right of +Y", second.Mirrored);
                Check.Near("rotation", Math.PI / 2, second.Rotation, 1e-9);
                // The branch pipe starts 248 above the first node, so it has 2752 left; the inlet is 930 below the node.
                Check.True("inlet", Near(V(4000, 3000 - 930), second.Inlet));
                Check.True("branch port", Near(V(4000 + 248, 3000), second.BranchPort));
                Ports("branch", second, V(4000, 2070), V(4000, 3000), V(4248, 3000));
                Check.True("the first branch pipe runs from its port to the second inlet",
                    HasPath(r, V(4000, 248), V(4000, 2070)));
            });
        }

        private static void UpstreamRemainderTooShort()
        {
            Check.Case("the run left after the previous refnet's port counts", () =>
            {
                // The branch is 1000 long. The first refnet cuts 248 off its start, leaving 752 < 930.
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
                Check.True("inlet 750 upstream", Near(V(4250, 0), n.Inlet));
                Check.True("branch port 200 off the main", Near(V(5000, 200), n.BranchPort));
                Check.Near("X scale", -1.0, n.XScale);

                RefrigResult big = CleanWithRoot(Tee(3000), V(0, 0), input => input.Settings.RefnetScale = 2.0);
                Check.True("inlet at 2.0", Near(V(5000 - 1500, 0), big.Refnets[0].Inlet));
                Check.True("branch port at 2.0", Near(V(5000, 400), big.Refnets[0].BranchPort));
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
                Check.True("upstream pipe is one run to the inlet", HasPath(r, V(0, 0), V(4070, 0)));
            });
        }
    }
}
