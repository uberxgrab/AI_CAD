using System;
using System.Collections.Generic;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Pipes;
using static PipeCoreTests.Support;

namespace PipeCoreTests
{
    /// <summary>
    /// The welded graph and run finding: corners, T nodes, interior crossings, layer changes, closed
    /// polylines and arcs (AGENTS.md, "HVACPIPESIZE", behaviour step 1).
    /// </summary>
    internal static class RunTests
    {
        public static void Run()
        {
            SingleLine();
            PolylineCorner();
            WeldedLines();
            GapBeyondTolerance();
            TNode();
            TNodeWithGap();
            TNodeOnPolylineInterior();
            CrossingIsNotANode();
            LayerChangeStopsRun();
            LayerCasing();
            ClosedPolyline();
            ArcLength();
            TinySegment();
            Staircase();
            FindNearestEdge();
            StarStopsEverywhere();
            RunOutlines();
            DuplicateLinesAreNotACorner();
        }

        private static PipeNetwork Net(double weld, params PipeCurve[] curves)
        {
            return PipeNetwork.Build(new List<PipeCurve>(curves), weld);
        }

        private static PipeRun RunFrom(PipeNetwork net, int curve, double x, double y)
        {
            int edge = PipeRunFinder.FindEdge(net, curve, new Vec2(x, y));
            return PipeRunFinder.Find(net, edge);
        }

        private static void SingleLine()
        {
            Check.Case("single line", () =>
            {
                PipeNetwork net = Net(1, Line(0, Layer, 0, 0, 1000, 0));
                Check.Equal("nodes", 2, net.Nodes.Count);
                Check.Equal("edges", 1, net.Edges.Count);
                PipeRun run = RunFrom(net, 0, 500, 0);
                Check.Equal("run edges", 1, run.Edges.Count);
                Check.Near("run length", 1000, run.Length);
                Check.Equal("both ends are degree 1", 2, run.EndNodes.Count);
                Check.Equal("layer", Layer, run.Layer);
            });
        }

        private static void PolylineCorner()
        {
            Check.Case("polyline corner", () =>
            {
                PipeNetwork net = Net(1, Poly(0, Layer, false, 0, 0, 1000, 0, 1000, 500));
                Check.Equal("edges", 2, net.Edges.Count);
                Check.Equal("nodes", 3, net.Nodes.Count);
                PipeRun run = RunFrom(net, 0, 500, 0);
                Check.Equal("run edges", 2, run.Edges.Count);
                Check.Near("run length", 1500, run.Length);
                Check.Equal("end nodes", 2, run.EndNodes.Count);
                PipeRun fromSecond = RunFrom(net, 0, 1000, 250);
                Check.Equal("same run from the other segment", 2, fromSecond.Edges.Count);
            });
        }

        private static void WeldedLines()
        {
            Check.Case("endpoints within the tolerance are welded", () =>
            {
                PipeNetwork net = Net(1, Line(0, Layer, 0, 0, 1000, 0), Line(1, Layer, 1000.5, 0.3, 1000, 500));
                Check.Equal("nodes", 3, net.Nodes.Count);
                PipeRun run = RunFrom(net, 0, 500, 0);
                Check.Equal("run edges", 2, run.Edges.Count);
                double second = Math.Sqrt(0.5 * 0.5 + 499.7 * 499.7);
                Check.Near("run length", 1000 + second, run.Length, 1e-6);
            });
        }

        private static void GapBeyondTolerance()
        {
            Check.Case("a gap beyond the tolerance is not welded", () =>
            {
                PipeNetwork net = Net(1, Line(0, Layer, 0, 0, 1000, 0), Line(1, Layer, 1002, 0, 1002, 500));
                Check.Equal("nodes", 4, net.Nodes.Count);
                Check.Equal("run edges", 1, RunFrom(net, 0, 500, 0).Edges.Count);

                PipeNetwork wide = Net(3, Line(0, Layer, 0, 0, 1000, 0), Line(1, Layer, 1002, 0, 1002, 500));
                Check.Equal("a wider tolerance welds them", 3, wide.Nodes.Count);
                Check.Equal("and the run continues", 2, RunFrom(wide, 0, 500, 0).Edges.Count);
            });
        }

        private static void TNode()
        {
            Check.Case("T node splits the host and stops runs", () =>
            {
                PipeNetwork net = Net(1, Line(0, Layer, 0, 0, 2000, 0), Line(1, Layer, 1000, 0, 1000, 800));
                Check.Equal("edges: host split in two plus the branch", 3, net.Edges.Count);
                Check.Equal("nodes", 4, net.Nodes.Count);

                int degreeThree = 0;
                foreach (NetworkNode node in net.Nodes)
                {
                    if (node.Degree == 3)
                    {
                        degreeThree++;
                        Check.True("T node flag", node.IsTNode);
                        Check.True("host point", Near(new Vec2(1000, 0), node.HostPoint));
                    }
                }

                Check.Equal("one node of degree 3", 1, degreeThree);

                PipeRun branch = RunFrom(net, 1, 1000, 400);
                Check.Equal("branch run", 1, branch.Edges.Count);
                Check.Near("branch length", 800, branch.Length);

                PipeRun left = RunFrom(net, 0, 500, 0);
                Check.Equal("left half run", 1, left.Edges.Count);
                Check.Near("left half length", 1000, left.Length);
                Check.Near("left half ends at the T", 0.5, net.Edges[left.Edges[0]].F1);

                PipeRun right = RunFrom(net, 0, 1500, 0);
                Check.Equal("right half run", 1, right.Edges.Count);
                Check.Near("right half starts at the T", 0.5, net.Edges[right.Edges[0]].F0);
            });
        }

        private static void TNodeWithGap()
        {
            Check.Case("a T node within the tolerance of the host", () =>
            {
                PipeNetwork net = Net(1, Line(0, Layer, 0, 0, 2000, 0), Line(1, Layer, 1000, 0.4, 1000, 800));
                Check.Equal("edges", 3, net.Edges.Count);
                bool found = false;
                foreach (NetworkNode node in net.Nodes)
                {
                    if (node.IsTNode)
                    {
                        found = true;
                        Check.True("host point is on the host", Near(new Vec2(1000, 0), node.HostPoint));
                    }
                }

                Check.True("T node found", found);

                PipeNetwork apart = Net(1, Line(0, Layer, 0, 0, 2000, 0), Line(1, Layer, 1000, 5, 1000, 800));
                Check.Equal("5 mm away is not a T", 2, apart.Edges.Count);
            });
        }

        private static void TNodeOnPolylineInterior()
        {
            Check.Case("T node on a polyline segment", () =>
            {
                PipeNetwork net = Net(1,
                    Poly(0, Layer, false, 0, 0, 2000, 0, 2000, 1000),
                    Line(1, Layer, 1000, 0, 1000, -800));
                Check.Equal("edges: two for the first segment, one for the second, one branch", 4, net.Edges.Count);

                PipeRun run = RunFrom(net, 0, 2000, 500);
                Check.Equal("run edges", 2, run.Edges.Count);
                Check.Near("run length", 2000, run.Length);
                Check.Equal("end nodes: the leaf and the T", 2, run.EndNodes.Count);

                bool hasSecondHalf = false;
                foreach (int index in run.Edges)
                {
                    NetworkEdge edge = net.Edges[index];
                    if (edge.Segment == 0)
                    {
                        hasSecondHalf = true;
                        Check.Near("F0", 0.5, edge.F0);
                        Check.Near("F1", 1, edge.F1);
                    }
                }

                Check.True("the second half of the first segment is in the run", hasSecondHalf);
            });
        }

        private static void CrossingIsNotANode()
        {
            Check.Case("interior crossings are not nodes", () =>
            {
                PipeNetwork net = Net(1, Line(0, Layer, 0, 0, 1000, 0), Line(1, Layer, 500, -500, 500, 500));
                Check.Equal("nodes", 4, net.Nodes.Count);
                Check.Equal("edges", 2, net.Edges.Count);
                Check.Equal("run along the first", 1, RunFrom(net, 0, 250, 0).Edges.Count);
                Check.Near("run length", 1000, RunFrom(net, 0, 250, 0).Length);
                Check.Near("run along the second", 1000, RunFrom(net, 1, 500, 250).Length);
            });
        }

        private static void LayerChangeStopsRun()
        {
            Check.Case("a layer change stops the run", () =>
            {
                PipeNetwork net = Net(1,
                    Line(0, "EIA_1 CHWS DN40", 0, 0, 1000, 0),
                    Line(1, "EIA_1 CHWS DN50", 1000, 0, 2000, 0));
                PipeRun run = RunFrom(net, 0, 500, 0);
                Check.Equal("run edges", 1, run.Edges.Count);
                Check.Near("run length", 1000, run.Length);
                bool hasMiddle = false;
                foreach (int node in run.EndNodes)
                {
                    if (net.Nodes[node].Degree == 2)
                        hasMiddle = true;
                }

                Check.True("the run ends at the degree-2 node on the layer change", hasMiddle);
            });
        }

        private static void LayerCasing()
        {
            Check.Case("layer names compare ignoring case", () =>
            {
                PipeNetwork net = Net(1,
                    Line(0, "EIA_1 CHWS DN40", 0, 0, 1000, 0),
                    Line(1, "eia_1 chws dn40", 1000, 0, 2000, 0));
                Check.Equal("run edges", 2, RunFrom(net, 0, 500, 0).Edges.Count);
            });
        }

        private static void ClosedPolyline()
        {
            Check.Case("a closed polyline is one run with no ends", () =>
            {
                PipeNetwork net = Net(1, Poly(0, Layer, true, 0, 0, 1000, 0, 1000, 1000, 0, 1000));
                Check.Equal("edges", 4, net.Edges.Count);
                PipeRun run = RunFrom(net, 0, 500, 0);
                Check.Equal("run edges", 4, run.Edges.Count);
                Check.Near("run length", 4000, run.Length);
                Check.Equal("no end nodes", 0, run.EndNodes.Count);
            });
        }

        private static void ArcLength()
        {
            Check.Case("an arc segment", () =>
            {
                // A semicircle over a chord of 1000: radius 500.
                PipeNetwork net = Net(1, PolyBulge(0, Layer, false, 0, 0, 1, 1000, 0, 0));
                Check.Equal("edges", 1, net.Edges.Count);
                PipeRun run = RunFrom(net, 0, 500, -500);
                Check.Near("arc length", Math.PI * 500, run.Length, 1e-6);
                Check.Near("sub bulge", 1, net.Edges[0].SubBulge, 1e-12);
                List<Outline> outlines = PipeRunFinder.Outlines(net, run);
                Check.Equal("outlines", 1, outlines.Count);
                Check.Near("outline bulge", 1, outlines[0].Vertices[0].Bulge, 1e-12);
            });
        }

        private static void TinySegment()
        {
            Check.Case("a segment shorter than the tolerance is not an edge", () =>
            {
                PipeNetwork net = Net(1, Poly(0, Layer, false, 0, 0, 1000, 0, 1000.3, 0, 2000, 0));
                Check.Equal("edges", 2, net.Edges.Count);
                Check.True("the tiny segment is recorded", net.IsDegenerate(0, 1));
                Check.False("others are not", net.IsDegenerate(0, 0));
                PipeRun run = RunFrom(net, 0, 500, 0);
                Check.Equal("the run passes the weld", 2, run.Edges.Count);
            });
        }

        private static void Staircase()
        {
            Check.Case("a run through many corners", () =>
            {
                PipeNetwork net = Net(1, Poly(0, Layer, false,
                    0, 0, 1000, 0, 1000, 1000, 2000, 1000, 2000, 2000, 3000, 2000));
                PipeRun run = RunFrom(net, 0, 2500, 2000);
                Check.Equal("run edges", 5, run.Edges.Count);
                Check.Near("run length", 5000, run.Length);
            });
        }

        private static void FindNearestEdge()
        {
            Check.Case("find the edge nearest a point", () =>
            {
                PipeNetwork net = Net(1, Poly(0, Layer, false,
                    0, 0, 1000, 0, 1000, 1000, 2000, 1000));
                int edge = PipeRunFinder.FindEdge(net, 0, new Vec2(1500, 1010));
                Check.Equal("segment", 2, net.Edges[edge].Segment);
                edge = PipeRunFinder.FindEdge(net, 0, new Vec2(1010, 500));
                Check.Equal("middle segment", 1, net.Edges[edge].Segment);

                PipeNetwork two = Net(1, Line(0, Layer, 0, 0, 1000, 0), Line(1, Layer, 0, 50, 1000, 50));
                Check.Equal("only edges of the given curve", 1,
                    two.Edges[PipeRunFinder.FindEdge(two, 1, new Vec2(500, 1))].Curve);

                PipeNetwork host = Net(1, Line(0, Layer, 0, 0, 2000, 0), Line(1, Layer, 1000, 0, 1000, 800));
                int rightHalf = PipeRunFinder.FindEdge(host, 0, new Vec2(1800, 10));
                Check.Near("a split segment picks the part under the point", 0.5, host.Edges[rightHalf].F0);
            });
        }

        private static void StarStopsEverywhere()
        {
            Check.Case("a star of three lines", () =>
            {
                PipeNetwork net = Net(1,
                    Line(0, Layer, 0, 0, 1000, 0),
                    Line(1, Layer, 0, 0, 0, 1000),
                    Line(2, Layer, 0, 0, -1000, -1000));
                Check.Equal("nodes", 4, net.Nodes.Count);
                for (int curve = 0; curve < 3; curve++)
                    Check.Equal("run " + curve, 1, PipeRunFinder.Find(net, net.Edges[curve].Index).Edges.Count);
            });
        }

        private static void RunOutlines()
        {
            Check.Case("outlines follow the run", () =>
            {
                PipeNetwork net = Net(1, Poly(0, Layer, false, 0, 0, 1000, 0, 1000, 500));
                PipeRun run = RunFrom(net, 0, 500, 0);
                List<Outline> outlines = PipeRunFinder.Outlines(net, run);
                Check.Equal("one outline per edge", 2, outlines.Count);
                double total = 0;
                foreach (Outline outline in outlines)
                {
                    Check.False("open", outline.Closed);
                    Check.Equal("two vertices", 2, outline.Vertices.Count);
                    total += outline.PointAt(0).DistanceTo(outline.PointAt(1));
                }

                Check.Near("total length", 1500, total);
            });
        }

        private static void DuplicateLinesAreNotACorner()
        {
            Check.Case("two lines drawn over each other", () =>
            {
                // The shared end nodes see degree 2, so the walk takes the duplicate, then stops: no endless loop.
                PipeNetwork net = Net(1, Line(0, Layer, 0, 0, 1000, 0), Line(1, Layer, 0, 0, 1000, 0));
                PipeRun run = RunFrom(net, 0, 500, 0);
                Check.Equal("both lines are in the run", 2, run.Edges.Count);
            });
        }
    }
}
