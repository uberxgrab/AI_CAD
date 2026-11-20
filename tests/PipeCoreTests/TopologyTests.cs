using System;
using System.Collections.Generic;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Pipes;
using static PipeCoreTests.Support;

namespace PipeCoreTests
{
    /// <summary>
    /// The pipe tree HVACREFRIG sizing works on. A refnet is one node that joins its inlet, main outlet
    /// and branch outlet; the pipes between nodes are segments (Main: root to the first refnet or to the
    /// only unit; BetweenRefnets; ToUnit: last refnet to an indoor unit), and every pipe end other than
    /// the root is an indoor unit. Segments and units are numbered in the order they are met from the
    /// root, main outlet before branch outlet. Expected values are worked out by hand.
    /// </summary>
    internal static class TopologyTests
    {
        private const string Rp = "EIA_1 RP 22.2-9.5 (28)";
        private const string Rp2 = "EIA_1 RP 15.9-9.5 (14)";

        public static void Run()
        {
            SingleUnitIsMain();
            FlatGroupsOfThree();
            PlannedRefnetTee();
            TwoPlannedRefnetsInSeries();
            ExistingRefnetBlock();
            ExistingRefnetOnAContinuousPath();
            ACornerInsideASegmentIsKept();
            ReachedThroughTheMainOutlet();
            JunctionWithoutARefnet();
            CornersAndLayerChangesStayOneSegment();
            PathsRunningTheOtherWay();
            OtherNetworkIsUnreached();
            RootNotOnAPipe();
            DistancesAndRefnetCounts();
            RootWithTwoPipes();
        }

        private static RefrigPath P(string layer, params double[] xy)
        {
            RefrigPath path = new RefrigPath { Layer = layer };
            for (int i = 0; i + 1 < xy.Length; i += 2)
                path.Points.Add(new Vec2(xy[i], xy[i + 1]));
            return path;
        }

        private static NetworkTopology Topo(
            IList<RefrigPath> paths,
            Vec2 root,
            IList<RefnetPlacement> planned = null,
            IList<RefnetPortGroup> existing = null,
            double tolerance = 1.0)
        {
            return NetworkTopologyBuilder.Build(new TopologyInput
            {
                Paths = paths,
                PlannedRefnets = planned ?? new List<RefnetPlacement>(),
                ExistingRefnets = existing ?? new List<RefnetPortGroup>(),
                Root = root,
                Tolerance = tolerance
            });
        }

        private static NetworkTopology FromCleaner(IList<PipeCurve> curves, Vec2 root)
        {
            RefrigResult r = CleanWithRoot(curves, root);
            Check.True("cleaner ok", r.Ok);
            Check.True("cleaner found the root", r.HasRoot);
            return Topo(r.Paths, r.Root, r.Refnets, null, 50);
        }

        private static string Ends(TopologySegment segment)
        {
            return string.Join(",", segment.DownstreamEnds);
        }

        private static void SingleUnitIsMain()
        {
            Check.Case("a single unit with no refnet: the pipe is the main", () =>
            {
                NetworkTopology t = Topo(new[] { P(Rp, 0, 0, 6000, 0) }, V(0, 0));
                Check.True("ok", t.Ok);
                Check.Equal("one segment", 1, t.Segments.Count);
                Check.Equal("kind", SegmentKind.Main, t.Segments[0].Kind);
                Check.Equal("one end", 1, t.Ends.Count);
                Check.True("the end is the far point", Near(V(6000, 0), t.Ends[0].Point));
                Check.Equal("the main serves unit 0", "0", Ends(t.Segments[0]));
                Check.Equal("no refnets", 0, t.Refnets.Count);
                Check.Equal("no warnings", 0, t.Warnings.Count);
                Check.Near("length", 6000, t.Segments[0].Length);
                Check.Equal("the root node", TopologyNodeKind.Root, t.Nodes[t.RootNode].Kind);
            });
        }

        private static void FlatGroupsOfThree()
        {
            Check.Case("flat port lists are grouped three to a block, inlet then main then branch", () =>
            {
                List<RefnetPortGroup> groups = RefnetPortGroup.FromFlat(new List<Vec2>
                {
                    V(0, 0), V(1, 0), V(1, -2), V(10, 0), V(11, 0), V(11, -2), V(99, 99)
                });
                Check.Equal("two whole blocks", 2, groups.Count);
                Check.Equal("ids", 1, groups[1].Id);
                Check.True("inlet", Near(V(10, 0), groups[1].Inlet));
                Check.True("main", Near(V(11, 0), groups[1].Main));
                Check.True("branch", Near(V(11, -2), groups[1].Branch));
            });
        }

        private static List<PipeCurve> Tee(double branchEndY)
        {
            return new List<PipeCurve>
            {
                Line(0, "EIA_1 RP 28.6-12.7 (33.6-44.8)", 0, 0, 5000, 0),
                Line(1, Rp, 5000, 0, 9000, 0),
                Line(2, Rp2, 5000, 0, 5000, branchEndY)
            };
        }

        private static void PlannedRefnetTee()
        {
            Check.Case("one planned refnet: a main to the refnet and a pipe to each unit", () =>
            {
                NetworkTopology t = FromCleaner(Tee(3000), V(0, 0));
                Check.True("ok", t.Ok);
                Check.Equal("no warnings", 0, t.Warnings.Count);
                Check.Equal("refnets", 1, t.Refnets.Count);
                Check.True("planned", t.Refnets[0].Planned);
                Check.Equal("planned index", 0, t.Refnets[0].SourceIndex);
                Check.Equal("reached at the inlet", 0, t.Refnets[0].ArrivedAt);
                Check.Equal("segments", 3, t.Segments.Count);
                Check.Equal("units", 2, t.Ends.Count);

                TopologySegment main = t.Segments[0];
                Check.Equal("S0 is the main", SegmentKind.Main, main.Kind);
                Check.True("the main ends at the refnet inlet",
                    Near(V(3570, 0), main.Points[main.Points.Count - 1]));
                Check.Equal("the main serves both units", "0,1", Ends(main));
                Check.Equal("the refnet is reached by the main", 0, t.Refnets[0].UpSegment);

                // Main outlet is met before the branch outlet.
                TopologySegment through = t.Segments[1];
                Check.Equal("S1 is a pipe to a unit", SegmentKind.ToUnit, through.Kind);
                Check.True("S1 is the through pipe", Near(V(9000, 0), t.Ends[0].Point));
                Check.Equal("S1 serves unit 0", "0", Ends(through));
                Check.Equal("the refnet's main segment", 1, t.Refnets[0].MainSegment);

                TopologySegment branch = t.Segments[2];
                Check.Equal("S2 is a pipe to a unit", SegmentKind.ToUnit, branch.Kind);
                Check.True("unit 1 is the branch end", Near(V(5000, 3000), t.Ends[1].Point));
                Check.Equal("S2 serves unit 1", "1", Ends(branch));
                Check.Equal("the refnet's branch segment", 2, t.Refnets[0].BranchSegment);
                Check.Equal("the D-long pipe and the original leg are one segment", 3, branch.Points.Count);
                Check.True("it starts at the branch outlet", Near(V(4500, 248), branch.Points[0]));
                Check.Equal("both pieces were on the branch layer", 1, branch.OriginalLayers.Count);
                Check.Equal("units are indices into Ends", 0, t.Ends[0].Index);
                Check.Equal("the end belongs to its last segment", 1, t.Ends[0].Segment);
            });
        }

        private static void TwoPlannedRefnetsInSeries()
        {
            Check.Case("two refnets in series: main, between-refnets pipe, three unit pipes", () =>
            {
                NetworkTopology t = FromCleaner(new List<PipeCurve>
                {
                    Line(0, Rp, 0, 0, 8000, 0),
                    Line(1, Rp, 8000, 0, 16000, 0),
                    Line(2, Rp2, 8000, 0, 8000, 4000),
                    Line(3, Rp2, 16000, 0, 16000, 4000),
                    Line(4, Rp2, 16000, 0, 20000, 0)
                }, V(0, 0));
                Check.True("ok", t.Ok);
                Check.Equal("refnets", 2, t.Refnets.Count);
                Check.Equal("units", 3, t.Ends.Count);
                Check.Equal("segments", 5, t.Segments.Count);
                Check.Equal("one main", 1, t.OfKind(SegmentKind.Main).Count);
                Check.Equal("one between", 1, t.OfKind(SegmentKind.BetweenRefnets).Count);
                Check.Equal("three to units", 3, t.OfKind(SegmentKind.ToUnit).Count);

                TopologySegment main = t.OfKind(SegmentKind.Main)[0];
                TopologySegment between = t.OfKind(SegmentKind.BetweenRefnets)[0];
                Check.Equal("the main serves every unit", "0,1,2", Ends(main));
                Check.Equal("the pipe between refnets serves the two units past the second refnet",
                    2, between.DownstreamEnds.Count);
                Check.True("and not the branch of the first refnet",
                    !between.DownstreamEnds.Contains(t.Ends.FindIndex(e => Near(e.Point, V(8000, 4000)))));

                int firstEnd = t.Ends.FindIndex(e => Near(e.Point, V(8000, 4000)));
                Check.True("the first refnet's branch unit exists", firstEnd >= 0);
                TopologySegment toFirst = t.Segments[t.Ends[firstEnd].Segment];
                Check.Equal("its pipe is a ToUnit pipe", SegmentKind.ToUnit, toFirst.Kind);
                Check.Equal("serving just it", 1, toFirst.DownstreamEnds.Count);

                // Up of the between pipe is the first refnet, down is the second.
                Check.Equal("between starts at a refnet", TopologyNodeKind.Refnet, t.Nodes[between.UpNode].Kind);
                Check.Equal("between ends at a refnet", TopologyNodeKind.Refnet, t.Nodes[between.DownNode].Kind);
                Check.Equal("the second refnet is reached by it", between.Index, t.Refnets[1].UpSegment);
                Check.Equal("the first refnet is reached by the main", main.Index, t.Refnets[0].UpSegment);

                // Each refnet's two outlets lead to segments.
                foreach (TopologyRefnet refnet in t.Refnets)
                {
                    Check.True("main outlet has a segment", refnet.MainSegment >= 0);
                    Check.True("branch outlet has a segment", refnet.BranchSegment >= 0);
                }
            });
        }

        private static IList<RefnetPortGroup> OneBlock()
        {
            return new List<RefnetPortGroup>
            {
                new RefnetPortGroup { Id = 7, Inlet = V(1000, 0), Main = V(1930, 0), Branch = V(1930, -248) }
            };
        }

        private static void ExistingRefnetBlock()
        {
            Check.Case("a refnet block already in the drawing joins its three ports", () =>
            {
                NetworkTopology t = Topo(new[]
                {
                    P(Rp, 0, 0, 1000, 0),
                    P(Rp, 1930, 0, 5000, 0),
                    P(Rp2, 1930, -248, 1930, -3000)
                }, V(0, 0), null, OneBlock());
                Check.True("ok", t.Ok);
                Check.Equal("no warnings", 0, t.Warnings.Count);
                Check.Equal("one refnet", 1, t.Refnets.Count);
                Check.False("not planned", t.Refnets[0].Planned);
                Check.Equal("the block's own id", 7, t.Refnets[0].SourceIndex);
                Check.Equal("three segments", 3, t.Segments.Count);
                Check.Equal("main", SegmentKind.Main, t.Segments[0].Kind);
                Check.Equal("to unit", SegmentKind.ToUnit, t.Segments[1].Kind);
                Check.Equal("to unit", SegmentKind.ToUnit, t.Segments[2].Kind);
                Check.Equal("the main serves both units", "0,1", Ends(t.Segments[0]));
                Check.True("unit 0 is on the main outlet", Near(V(5000, 0), t.Ends[0].Point));
                Check.True("unit 1 is on the branch outlet", Near(V(1930, -3000), t.Ends[1].Point));
            });
        }

        private static void ExistingRefnetOnAContinuousPath()
        {
            Check.Case("a path that has vertices at the ports is cut there", () =>
            {
                // One polyline goes root -> inlet -> (the block's own length) -> main outlet -> unit. The
                // piece between the two ports is the block's body, not a pipe.
                NetworkTopology t = Topo(new[]
                {
                    P(Rp, 0, 0, 1000, 0, 1930, 0, 5000, 0),
                    P(Rp2, 1930, -248, 1930, -3000)
                }, V(0, 0), null, OneBlock());
                Check.True("ok", t.Ok);
                Check.Equal("one refnet", 1, t.Refnets.Count);
                Check.Equal("three segments", 3, t.Segments.Count);
                Check.Equal("main ends at the inlet", 2, t.Segments[0].Points.Count);
                Check.True("main ends at the inlet point", Near(V(1000, 0), t.Segments[0].Points[1]));
                Check.True("the main outlet pipe starts at 1930", Near(V(1930, 0), t.Segments[1].Points[0]));
                Check.Equal("two units", 2, t.Ends.Count);
                Check.True("the body piece was reported", t.Warnings.Count == 1,
                    t.Warnings.Count + " warnings");
                Check.Equal("the body piece stays as drawn", 1, t.Unreached.Count);
                Check.True("it runs between the ports",
                    Near(V(1000, 0), t.Unreached[0].Points[0]) && Near(V(1930, 0), t.Unreached[0].Points[1]));
            });
        }

        private static void ACornerInsideASegmentIsKept()
        {
            Check.Case("a path with a vertex only at one port is split once", () =>
            {
                NetworkTopology t = Topo(new[]
                {
                    P(Rp, 0, 0, 1000, 0),
                    P(Rp, 1930, 0, 3000, 0, 5000, 0),
                    P(Rp2, 1930, -248, 1930, -3000)
                }, V(0, 0), null, OneBlock());
                Check.True("ok", t.Ok);
                Check.Equal("three segments", 3, t.Segments.Count);
                Check.Equal("the corner at 3000 is inside the through segment", 3, t.Segments[1].Points.Count);
                Check.Equal("nothing left over", 0, t.Unreached.Count);
            });
        }

        private static void ReachedThroughTheMainOutlet()
        {
            Check.Case("a root on the main outlet side: the refnet is entered through a port that is not the inlet", () =>
            {
                NetworkTopology t = Topo(new[]
                {
                    P(Rp, 0, 0, 1930, 0),
                    P(Rp, 1000, 0, -3000, 0),
                    P(Rp2, 1930, -248, 1930, -3000)
                }, V(0, 0), null, OneBlock());
                // The path from the root meets the main outlet (1930, 0), the other pipes leave from the
                // inlet and the branch outlet.
                Check.True("ok", t.Ok);
                Check.Equal("reached at the main outlet", 1, t.Refnets[0].ArrivedAt);
                Check.Equal("a warning says so", 1, t.Warnings.Count);
                Check.Contains("the warning", t.Warnings[0], "main outlet");
                Check.Equal("both other ports lead on", 3, t.Segments.Count);
            });
        }

        private static void JunctionWithoutARefnet()
        {
            Check.Case("a junction with no refnet is a node of its own, with a warning", () =>
            {
                NetworkTopology t = Topo(new[]
                {
                    P(Rp, 0, 0, 5000, 0),
                    P(Rp, 5000, 0, 9000, 0),
                    P(Rp2, 5000, 0, 5000, 3000)
                }, V(0, 0));
                Check.True("ok", t.Ok);
                Check.Equal("no refnets", 0, t.Refnets.Count);
                Check.Equal("three segments", 3, t.Segments.Count);
                Check.Equal("main to the junction", SegmentKind.Main, t.Segments[0].Kind);
                Check.Equal("the junction node", TopologyNodeKind.Junction, t.Nodes[t.Segments[0].DownNode].Kind);
                Check.Equal("to unit", SegmentKind.ToUnit, t.Segments[1].Kind);
                Check.Equal("to unit", SegmentKind.ToUnit, t.Segments[2].Kind);
                Check.Equal("the main serves both units", "0,1", Ends(t.Segments[0]));
                Check.Equal("one warning", 1, t.Warnings.Count);
                Check.Contains("names the junction", t.Warnings[0], "no refnet");
            });
        }

        private static void CornersAndLayerChangesStayOneSegment()
        {
            Check.Case("corners, and a layer change at a corner, stay inside one segment", () =>
            {
                NetworkTopology t = Topo(new[]
                {
                    P(Rp, 0, 0, 3000, 0, 3000, 2000),
                    P(Rp2, 3000, 2000, 6000, 2000)
                }, V(0, 0));
                Check.True("ok", t.Ok);
                Check.Equal("one segment", 1, t.Segments.Count);
                TopologySegment s = t.Segments[0];
                Check.Equal("four points in order", 4, s.Points.Count);
                Check.True("start", Near(V(0, 0), s.Points[0]));
                Check.True("first corner", Near(V(3000, 0), s.Points[1]));
                Check.True("second corner", Near(V(3000, 2000), s.Points[2]));
                Check.True("end", Near(V(6000, 2000), s.Points[3]));
                Check.Near("length", 3000 + 2000 + 3000, s.Length);
                Check.Equal("two original layers", 2, s.OriginalLayers.Count);
                // The piece that gave most of the length supplies the properties.
                Check.Equal("source curve of the longest piece", 0, s.SourceCurve);
            });
        }

        private static void PathsRunningTheOtherWay()
        {
            Check.Case("a path drawn from the unit to the root is turned round", () =>
            {
                RefrigPath backwards = P(Rp, 7000, 0, 3000, 0);
                backwards.SourceCurve = 5;
                RefrigPath other = P(Rp, 0, 0, 3000, 0);
                other.SourceCurve = 6;
                NetworkTopology t = Topo(new[] { backwards, other }, V(0, 0));
                Check.True("ok", t.Ok);
                Check.Equal("one segment", 1, t.Segments.Count);
                Check.True("it starts at the root", Near(V(0, 0), t.Segments[0].Points[0]));
                Check.True("and ends at the unit", Near(V(7000, 0), t.Segments[0].Points[2]));
                Check.Equal("three points", 3, t.Segments[0].Points.Count);
                Check.Equal("the longer piece's source curve", 5, t.Segments[0].SourceCurve);
            });
        }

        private static void OtherNetworkIsUnreached()
        {
            Check.Case("a pipe not connected to the root is left alone", () =>
            {
                NetworkTopology t = Topo(new[]
                {
                    P(Rp, 0, 0, 5000, 0),
                    P(Rp, 0, 20000, 5000, 20000)
                }, V(0, 0));
                Check.True("ok", t.Ok);
                Check.Equal("one segment", 1, t.Segments.Count);
                Check.Equal("one unreached path", 1, t.Unreached.Count);
                Check.Equal("with its layer", Rp, t.Unreached[0].Layer);
                Check.Equal("a warning", 1, t.Warnings.Count);
            });
        }

        private static void RootNotOnAPipe()
        {
            Check.Case("a root that is not on a pipe end is an error", () =>
            {
                NetworkTopology t = Topo(new[] { P(Rp, 0, 0, 5000, 0) }, V(100, 100));
                Check.False("not ok", t.Ok);
                Check.Contains("message", t.Error, "root");
            });
        }

        private static void DistancesAndRefnetCounts()
        {
            Check.Case("pipe length from the root and refnets passed are kept per unit", () =>
            {
                NetworkTopology t = FromCleaner(new List<PipeCurve>
                {
                    Line(0, Rp, 0, 0, 8000, 0),
                    Line(1, Rp, 8000, 0, 16000, 0),
                    Line(2, Rp2, 8000, 0, 8000, 4000),
                    Line(3, Rp2, 16000, 0, 16000, 4000),
                    Line(4, Rp2, 16000, 0, 20000, 0)
                }, V(0, 0));
                int branchOfFirst = t.Ends.FindIndex(e => Near(e.Point, V(8000, 4000)));
                int far = t.Ends.FindIndex(e => Near(e.Point, V(20000, 0)));
                Check.True("both units found", branchOfFirst >= 0 && far >= 0);
                Check.Equal("the first refnet's unit passes one refnet", 1, t.Ends[branchOfFirst].Refnets);
                Check.Equal("the far unit passes two", 2, t.Ends[far].Refnets);
                // The pipes keep their lengths; the set-backs and refnet bodies only move the cuts, so the
                // pipe length to the far unit is the straight run less the two refnet bodies (2 x 930).
                Check.Near("the far unit's pipe length", 20000 - 2 * 930, t.Ends[far].PipeLength, 1e-6);
                Check.Near("the near unit's pipe length: 8000 less the body, plus the 4000 leg less the 248 jog",
                    8000 - 930 + 4000 - 248, t.Ends[branchOfFirst].PipeLength, 1e-6);
            });
        }

        private static void RootWithTwoPipes()
        {
            Check.Case("a root with two pipes warns and sizes each as a main", () =>
            {
                NetworkTopology t = Topo(new[]
                {
                    P(Rp, 0, 0, 5000, 0),
                    P(Rp, 0, 0, 0, 5000)
                }, V(0, 0));
                Check.True("ok", t.Ok);
                Check.Equal("two segments", 2, t.Segments.Count);
                Check.Equal("both main", 2, t.OfKind(SegmentKind.Main).Count);
                Check.Equal("a warning", 1, t.Warnings.Count);
            });
        }
    }
}
