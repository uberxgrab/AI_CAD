using System;
using System.Collections.Generic;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Pipes;
using static PipeCoreTests.Support;

namespace PipeCoreTests
{
    /// <summary>
    /// HVACDRAIN cores (AGENTS.md, "HVACDRAIN: draw and size a condensate network"): the tree from the drain
    /// point, the 45 degree joints (chamfers and slants), the Daikin Si39 sizing and the settings. Expected
    /// values are worked out by hand: with the default 300 mm slant the cut is 300 cos 45 = 212.132 mm.
    /// </summary>
    internal static class DrainTests
    {
        private static readonly double Cut = 300 * Math.Cos(Math.PI / 4);
        private const string Condensate = "EIA_1 Pipe condensated DN27";

        public static void Run()
        {
            TreeOfAStraightPipe();
            TreeOfAComb();
            TreeDropsCollinearPoints();
            TreeKeepsBends();
            TreeTakesLayerFromLongerPath();
            TreeRootChecks();
            TreeOtherNetworkIsKept();
            TreeLoopIsRefused();
            TreeCross();

            TeeSlantGeometry();
            TeeSlantGeometryRotated();
            BendChamfer();
            BendChamferRotated();
            NonRightBendsAreLeft();
            ShortLegKeepsBend();
            ShortMiddleLegChamfersOneBend();
            ShortMainLegKeepsTee();
            ShortBranchKeepsTee();
            NeighbouringChamferCountsOnTheLeg();
            WyeGeometry();
            CrossStaysACross();
            NonRightAngleTee();
            ExistingDiagonalTeeIsQuiet();
            CustomSlantLength();

            TableEdges();
            TableFactorScale();
            SizeText();
            SizingOfAComb();
            SizingSlope();
            SizingNeverDecreases();
            SizingManualKept();
            SizingMissingCapacity();
            SizingExtrapolated();
            SizingLeafIsD27();
            PreviewColours();

            PlannerEndToEnd();
            PlannerRefusesBadInput();
            PlannerIsRepeatable();

            SettingsRoundTrip();
            SettingsValidation();
            LabelTexts();
        }

        // ------------------------------------------------------------------ builders

        private static RefrigPath P(params double[] xy)
        {
            return PL(Condensate, 0, xy);
        }

        private static RefrigPath PL(string layer, int curve, params double[] xy)
        {
            RefrigPath path = new RefrigPath { Layer = layer, Elevation = 0, SourceCurve = curve };
            for (int i = 0; i + 1 < xy.Length; i += 2)
                path.Points.Add(new Vec2(xy[i], xy[i + 1]));
            return path;
        }

        private static DrainTree Tree(Vec2 root, params RefrigPath[] paths)
        {
            DrainTreeResult built = DrainNetwork.Build(paths, root, 100);
            Check.True("the tree is built" + (built.Ok ? "" : " (" + built.Error + ")"), built.Ok);
            return built.Tree;
        }

        private static DrainGeometryResult Geometry(DrainTree tree, double slant = 300)
        {
            return DrainGeometry.Build(tree, slant);
        }

        // The index of the segment that starts at the pipe end at this point.
        private static int SegmentFrom(DrainTree tree, Vec2 end)
        {
            foreach (DrainSegment segment in tree.Segments)
            {
                if (Near(segment.Points[0], end, 1e-6))
                    return segment.Index;
            }

            return -1;
        }

        private static bool Route(IList<Vec2> actual, params double[] xy)
        {
            if (actual.Count * 2 != xy.Length)
                return false;
            for (int i = 0; i < actual.Count; i++)
            {
                if (!Near(actual[i], new Vec2(xy[2 * i], xy[2 * i + 1]), 1e-6))
                    return false;
            }

            return true;
        }

        private static string Show(IList<Vec2> points)
        {
            List<string> parts = new List<string>();
            foreach (Vec2 point in points)
                parts.Add(point.ToString());
            return string.Join(" ", parts);
        }

        private static void RouteIs(string name, DrainGeometryResult geometry, DrainTree tree, Vec2 end, params double[] xy)
        {
            int index = SegmentFrom(tree, end);
            if (index < 0)
            {
                Check.Fail(name, "a segment from " + end, "none");
                return;
            }

            IList<Vec2> actual = geometry.Segments[index];
            Check.True(name, Route(actual, xy), Show(actual));
        }

        private static double[] RotateAll(double radians, params double[] xy)
        {
            double[] result = new double[xy.Length];
            for (int i = 0; i + 1 < xy.Length; i += 2)
            {
                Vec2 r = Rotate(new Vec2(xy[i], xy[i + 1]), radians);
                result[i] = r.X;
                result[i + 1] = r.Y;
            }

            return result;
        }

        // A T: the pipe to the root runs along -X from the junction (4000, 0); the main goes on to (10000, 0)
        // and the branch rises to (4000, 3000).
        private static DrainTree TeeTree(double rotation = 0)
        {
            return Tree(
                Rotate(new Vec2(0, 0), rotation),
                PL(Condensate, 0, RotateAll(rotation, 0, 0, 4000, 0)),
                PL(Condensate, 1, RotateAll(rotation, 4000, 0, 10000, 0)),
                PL(Condensate, 2, RotateAll(rotation, 4000, 0, 4000, 3000)));
        }

        // ------------------------------------------------------------------ tree

        private static void TreeOfAStraightPipe()
        {
            Check.Case("a straight pipe is one segment from the unit to the root", () =>
            {
                DrainTree tree = Tree(V(0, 0), P(0, 0, 5000, 0));
                Check.Equal("one segment", 1, tree.Segments.Count);
                Check.Equal("one unit", 1, tree.Units.Count);
                Check.Equal("the root node", DrainNodeKind.Root, tree.Nodes[tree.RootNode].Kind);
                Check.True("the unit is at the far end", Near(tree.Units[0].Point, V(5000, 0)));
                DrainSegment segment = tree.Segments[0];
                Check.True("the path runs from the unit to the root", Route(segment.Points, 5000, 0, 0, 0), Show(segment.Points));
                Check.Equal("the unit drains through it", 1, segment.Units.Count);
                Check.Equal("no parent", -1, segment.Parent);
                Check.Equal("it is the root segment", 0, tree.RootSegment);
                Check.Equal("length", 5000.0, segment.Length);

                // The other end as the root: the flow reverses.
                DrainTree reversed = Tree(V(5000, 0), P(0, 0, 5000, 0));
                Check.True("the unit is now at the origin", Near(reversed.Units[0].Point, V(0, 0)));
                Check.True("and the path runs the other way", Route(reversed.Segments[0].Points, 0, 0, 5000, 0));
            });
        }

        // The root (0,0); a main along +X with a branch up at 4000 and another at 8000; the main ends at 12000.
        private static DrainTree Comb()
        {
            return Tree(
                V(0, 0),
                P(0, 0, 4000, 0),
                P(4000, 0, 8000, 0),
                P(8000, 0, 12000, 0),
                P(4000, 0, 4000, 3000),
                P(8000, 0, 8000, 3000));
        }

        private static void TreeOfAComb()
        {
            Check.Case("a comb: segments, units and downstream units", () =>
            {
                DrainTree tree = Comb();
                Check.Equal("segments", 5, tree.Segments.Count);
                Check.Equal("units", 3, tree.Units.Count);
                Check.Equal("junctions", 2, tree.Nodes.FindAll(n => n.Kind == DrainNodeKind.Junction).Count);

                DrainSegment root = tree.Segments[tree.RootSegment];
                Check.Equal("the root segment drains every unit", 3, root.Units.Count);
                Check.Equal("the root segment has two children", 2, root.Children.Count);
                Check.True("its path is the first pipe", Route(root.Points, 4000, 0, 0, 0), Show(root.Points));

                DrainSegment toSecond = null;
                DrainSegment leafAtFirst = null;
                foreach (int child in root.Children)
                {
                    DrainSegment c = tree.Segments[child];
                    if (c.Units.Count == 2)
                        toSecond = c;
                    else
                        leafAtFirst = c;
                }

                Check.True("the pipe between the junctions is found", toSecond != null);
                Check.True("the branch at the first junction is found", leafAtFirst != null);
                if (toSecond != null)
                {
                    Check.Equal("two units drain through the middle pipe", 2, toSecond.Units.Count);
                    Check.Equal("its parent is the root segment", tree.RootSegment, toSecond.Parent);
                    Check.Equal("it has two children", 2, toSecond.Children.Count);
                    foreach (int child in toSecond.Children)
                        Check.Equal("a leaf pipe drains one unit", 1, tree.Segments[child].Units.Count);
                }

                if (leafAtFirst != null)
                    Check.True("the branch starts at the unit", Near(leafAtFirst.Points[0], V(4000, 3000)));

                foreach (DrainSegment segment in tree.Segments)
                {
                    if (segment.Parent >= 0)
                        Check.True("a segment comes after the one it flows into", segment.Index > segment.Parent);
                }

                // Units are the upstream ends of leaf segments, and each leaf segment names its unit.
                foreach (DrainUnit unit in tree.Units)
                {
                    Check.True("the unit's segment starts at the unit", Near(tree.Segments[unit.Segment].Points[0], unit.Point));
                    Check.Equal("the unit's node kind", DrainNodeKind.Unit, tree.Nodes[unit.Node].Kind);
                }
            });
        }

        private static void TreeDropsCollinearPoints()
        {
            Check.Case("collinear points inside a pipe are dropped", () =>
            {
                DrainTree tree = Tree(V(0, 0), P(0, 0, 1000, 0, 2000, 0, 2000, 1000));
                Check.True(
                    "the straight run is one leg",
                    Route(tree.Segments[0].Points, 2000, 1000, 2000, 0, 0, 0),
                    Show(tree.Segments[0].Points));
            });
        }

        private static void TreeKeepsBends()
        {
            Check.Case("bends stay as points of the segment", () =>
            {
                DrainTree tree = Tree(V(0, 0), P(0, 0, 3000, 0, 3000, 2000, 5000, 2000));
                Check.Equal("one segment", 1, tree.Segments.Count);
                Check.True(
                    "both bends are kept",
                    Route(tree.Segments[0].Points, 5000, 2000, 3000, 2000, 3000, 0, 0, 0),
                    Show(tree.Segments[0].Points));
            });
        }

        private static void TreeTakesLayerFromLongerPath()
        {
            Check.Case("a segment takes its layer, curve and elevation from the longer path", () =>
            {
                RefrigPath shortPath = PL("EIA_1 Pipe condensated DN21", 7, 0, 0, 1000, 0);
                shortPath.Elevation = 5;
                RefrigPath longPath = PL("EIA_1 Pipe condensated DN34", 9, 1000, 0, 6000, 0);
                longPath.Elevation = 8;
                DrainTree tree = Tree(V(0, 0), shortPath, longPath);
                Check.Equal("one segment through the layer change", 1, tree.Segments.Count);
                Check.Equal("the longer path's layer", "EIA_1 Pipe condensated DN34", tree.Segments[0].Layer);
                Check.Equal("the longer path's curve", 9, tree.Segments[0].SourceCurve);
                Check.Near("the longer path's elevation", 8, tree.Segments[0].Elevation);
            });
        }

        private static void TreeRootChecks()
        {
            Check.Case("the root must be a pipe end", () =>
            {
                RefrigPath[] paths = { P(0, 0, 4000, 0), P(4000, 0, 8000, 0), P(4000, 0, 4000, 3000) };

                DrainTreeResult middle = DrainNetwork.Build(paths, V(4000, 0), 100);
                Check.False("the junction is not a pipe end", middle.Ok);
                Check.Contains("the message says to pick an end", middle.Error, "pipe end");

                DrainTreeResult far = DrainNetwork.Build(paths, V(-5000, -5000), 100);
                Check.False("a point far from every end", far.Ok);

                DrainTreeResult near = DrainNetwork.Build(paths, V(30, 20), 100);
                Check.True("a point near an end picks it", near.Ok);
                Check.True("the root is that end", near.Ok && Near(near.Tree.Nodes[near.Tree.RootNode].Point, V(0, 0)));

                DrainTreeResult none = DrainNetwork.Build(new List<RefrigPath>(), V(0, 0), 100);
                Check.False("nothing to build from", none.Ok);
            });
        }

        private static void TreeOtherNetworkIsKept()
        {
            Check.Case("a separate network is kept as cleaned and reported", () =>
            {
                DrainTree tree = Tree(V(0, 0), P(0, 0, 5000, 0), P(0, 9000, 3000, 9000));
                Check.Equal("one segment is sized", 1, tree.Segments.Count);
                Check.Equal("one path is left over", 1, tree.Unreached.Count);
                Check.True("it is the far pipe", Near(tree.Unreached[0].Points[0], V(0, 9000)));
                Check.True("a marker is given", tree.ReportPoints.Count == 1 && Near(tree.ReportPoints[0], V(0, 9000)));
                Check.True("the report says it is not connected", tree.Reports.Count == 1 && tree.Reports[0].Contains("not connected"));
            });
        }

        private static void TreeLoopIsRefused()
        {
            Check.Case("a loop is refused", () =>
            {
                DrainTreeResult built = DrainNetwork.Build(
                    new[]
                    {
                        P(-1000, 0, 0, 0),
                        P(0, 0, 1000, 0),
                        P(1000, 0, 500, 800),
                        P(500, 800, 0, 0)
                    },
                    V(-1000, 0),
                    100);
                Check.False("not a tree", built.Ok);
                Check.Contains("the message names the loop", built.Error, "loop");
            });
        }

        private static void TreeCross()
        {
            Check.Case("a four-pipe joint is a cross and is reported", () =>
            {
                DrainTree tree = Tree(
                    V(0, 0),
                    P(0, 0, 4000, 0),
                    P(4000, 0, 8000, 0),
                    P(4000, 0, 4000, 3000),
                    P(4000, 0, 4000, -3000));
                Check.Equal("one cross", 1, tree.Nodes.FindAll(n => n.Kind == DrainNodeKind.Cross).Count);
                Check.Equal("four segments (the pipe to the root and three arms)", 4, tree.Segments.Count);
                Check.Equal("three units", 3, tree.Units.Count);
                Check.True("it is reported once", tree.Reports.Count == 1 && tree.Reports[0].Contains("4 pipes"));
                Check.True("and marked", tree.ReportPoints.Count == 1 && Near(tree.ReportPoints[0], V(4000, 0)));
            });
        }

        // ------------------------------------------------------------------ geometry

        private static void TeeSlantGeometry()
        {
            Check.Case("a T gets a 45 degree slant that lands downstream of the T point", () =>
            {
                DrainTree tree = TeeTree();
                DrainGeometryResult g = Geometry(tree);
                Check.Near("the cut is 212.13 mm", 212.132034355964, g.Cut, 1e-9);
                Check.Equal("one slant", 1, g.Slants);
                Check.Equal("nothing kept", 0, g.Kept);
                Check.Equal("no warnings", 0, g.Warnings.Count);
                Check.Equal("no chamfers", 0, g.Chamfers);

                // The main stays straight and ends at the landing point; the pipe to the root starts there.
                RouteIs("the main through the T", g, tree, V(10000, 0), 10000, 0, 4000 - Cut, 0);
                RouteIs("the branch stops short and slants", g, tree, V(4000, 3000), 4000, 3000, 4000, Cut, 4000 - Cut, 0);
                RouteIs("the pipe to the root starts at the landing point", g, tree, V(4000, 0), 4000 - Cut, 0, 0, 0);

                // The slant is 300 long and at 45 degrees to the main.
                IList<Vec2> branch = g.Segments[SegmentFrom(tree, V(4000, 3000))];
                Vec2 slant = branch[2] - branch[1];
                Check.Near("the slant is 300 mm long", 300, slant.Length, 1e-9);
                Check.Near("at 45 degrees to the main", Math.Cos(Math.PI / 4), Math.Abs(slant.Normalized().Dot(V(1, 0))), 1e-12);
                Check.True("it lands toward the root", branch[2].X < 4000);
            });
        }

        private static void TeeSlantGeometryRotated()
        {
            Check.Case("the T slant follows any orientation of the network", () =>
            {
                foreach (double degrees in new[] { 90.0, 180.0, -90.0, 37.0, -141.0 })
                {
                    double a = degrees * Math.PI / 180.0;
                    DrainTree tree = TeeTree(a);
                    DrainGeometryResult g = Geometry(tree);
                    string name = " at " + degrees + " degrees";
                    Check.Equal("one slant" + name, 1, g.Slants);
                    RouteIs(
                        "main" + name, g, tree, Rotate(V(10000, 0), a),
                        RotateAll(a, 10000, 0, 4000 - Cut, 0));
                    RouteIs(
                        "branch" + name, g, tree, Rotate(V(4000, 3000), a),
                        RotateAll(a, 4000, 3000, 4000, Cut, 4000 - Cut, 0));
                    RouteIs(
                        "root pipe" + name, g, tree, Rotate(V(4000, 0), a),
                        RotateAll(a, 4000 - Cut, 0, 0, 0));
                }

                // The branch on the other side of the main.
                DrainTree mirrored = Tree(
                    V(0, 0), P(0, 0, 4000, 0), P(4000, 0, 10000, 0), P(4000, 0, 4000, -3000));
                DrainGeometryResult m = Geometry(mirrored);
                RouteIs("branch below the main", m, mirrored, V(4000, -3000), 4000, -3000, 4000, -Cut, 4000 - Cut, 0);
            });
        }

        private static void BendChamfer()
        {
            Check.Case("a 90 degree bend is chamfered with a 300 mm 45 degree piece", () =>
            {
                DrainTree tree = Tree(V(0, 0), P(0, 0, 5000, 0, 5000, 4000));
                DrainGeometryResult g = Geometry(tree);
                Check.Equal("one chamfer", 1, g.Chamfers);
                Check.Equal("no slants", 0, g.Slants);
                RouteIs("cut from both legs", g, tree, V(5000, 4000), 5000, 4000, 5000, Cut, 5000 - Cut, 0, 0, 0);
                IList<Vec2> route = g.Segments[0];
                Check.Near("the 45 degree piece is 300 mm", 300, route[1].DistanceTo(route[2]), 1e-9);
            });
        }

        private static void BendChamferRotated()
        {
            Check.Case("bends are chamfered in every direction and handedness", () =>
            {
                foreach (double degrees in new[] { 0.0, 90.0, 180.0, 53.0 })
                {
                    double a = degrees * Math.PI / 180.0;
                    DrainTree left = Tree(
                        Rotate(V(0, 0), a), PL(Condensate, 0, RotateAll(a, 0, 0, 5000, 0, 5000, 4000)));
                    DrainGeometryResult g = Geometry(left);
                    Check.Equal("left turn at " + degrees, 1, g.Chamfers);
                    RouteIs(
                        "left turn route at " + degrees, g, left, Rotate(V(5000, 4000), a),
                        RotateAll(a, 5000, 4000, 5000, Cut, 5000 - Cut, 0, 0, 0));

                    DrainTree right = Tree(
                        Rotate(V(0, 0), a), PL(Condensate, 0, RotateAll(a, 0, 0, 5000, 0, 5000, -4000)));
                    DrainGeometryResult r = Geometry(right);
                    RouteIs(
                        "right turn route at " + degrees, r, right, Rotate(V(5000, -4000), a),
                        RotateAll(a, 5000, -4000, 5000, -Cut, 5000 - Cut, 0, 0, 0));
                }

                // Two bends in a row, each leg long enough for both cuts.
                DrainTree twice = Tree(V(0, 0), P(0, 0, 3000, 0, 3000, 2000, 6000, 2000));
                DrainGeometryResult t = Geometry(twice);
                Check.Equal("two chamfers", 2, t.Chamfers);
                Check.Equal("nothing kept", 0, t.Kept);
                Check.Equal("each bend adds one point", 6, t.Segments[0].Count);
            });
        }

        private static void NonRightBendsAreLeft()
        {
            Check.Case("only 90 degree bends are chamfered (within 5 degrees)", () =>
            {
                // A 45 degree turn (interior angle 135).
                DrainTree diagonal = Tree(V(0, 0), P(0, 0, 5000, 0, 7000, 2000));
                DrainGeometryResult d = Geometry(diagonal);
                Check.Equal("no chamfer on a 45 degree turn", 0, d.Chamfers);
                RouteIs("unchanged", d, diagonal, V(7000, 2000), 7000, 2000, 5000, 0, 0, 0);
                Check.Equal("no warning for it", 0, d.Warnings.Count);

                // A bend of 93 degrees counts as a right angle; one of 100 degrees does not.
                double t = 87 * Math.PI / 180.0;
                DrainTree near = Tree(V(0, 0), P(0, 0, 5000, 0, 5000 + 3000 * Math.Cos(t), 3000 * Math.Sin(t)));
                Check.Equal("93 degrees is chamfered", 1, Geometry(near).Chamfers);
                double w = 80 * Math.PI / 180.0;
                DrainTree wide = Tree(V(0, 0), P(0, 0, 5000, 0, 5000 + 3000 * Math.Cos(w), 3000 * Math.Sin(w)));
                Check.Equal("100 degrees is not", 0, Geometry(wide).Chamfers);
            });
        }

        private static void ShortLegKeepsBend()
        {
            Check.Case("a leg shorter than the cut keeps the corner and says so", () =>
            {
                DrainTree tree = Tree(V(0, 0), P(0, 0, 150, 0, 150, 3000));
                DrainGeometryResult g = Geometry(tree);
                Check.Equal("no chamfer", 0, g.Chamfers);
                Check.Equal("one corner kept", 1, g.Kept);
                RouteIs("as drawn", g, tree, V(150, 3000), 150, 3000, 150, 0, 0, 0);
                Check.Equal("one warning", 1, g.Warnings.Count);
                Check.True("it names the corner and the leg", g.Warnings.Count == 1
                    && g.Warnings[0].Contains("(150, 0)") && g.Warnings[0].Contains("150 mm")
                    && g.Warnings[0].Contains("212.1 mm"), g.Warnings.Count > 0 ? g.Warnings[0] : "none");
                Check.True("the corner is circled", g.WarningPoints.Count == 1 && Near(g.WarningPoints[0], V(150, 0)));

                // Exactly the cut is enough.
                DrainTree exact = Tree(V(0, 0), P(0, 0, Cut, 0, Cut, 3000));
                Check.Equal("a leg of exactly the cut is chamfered", 1, Geometry(exact).Chamfers);
            });
        }

        private static void ShortMiddleLegChamfersOneBend()
        {
            Check.Case("two bends on a leg too short for both: the downstream one is chamfered", () =>
            {
                DrainTree tree = Tree(V(0, 0), P(0, 0, 3000, 0, 3000, 300, 6000, 300));
                DrainGeometryResult g = Geometry(tree);
                Check.Equal("one chamfer", 1, g.Chamfers);
                Check.Equal("one corner kept", 1, g.Kept);
                RouteIs(
                    "the upstream corner stays square", g, tree, V(6000, 300),
                    6000, 300, 3000, 300, 3000, Cut, 3000 - Cut, 0, 0, 0);
                Check.True("the kept corner is circled", g.WarningPoints.Count == 1 && Near(g.WarningPoints[0], V(3000, 300)));
            });
        }

        private static void ShortMainLegKeepsTee()
        {
            Check.Case("a T whose downstream leg is too short is kept", () =>
            {
                DrainTree tree = Tree(V(0, 0), P(0, 0, 150, 0), P(150, 0, 8000, 0), P(150, 0, 150, 3000));
                DrainGeometryResult g = Geometry(tree);
                Check.Equal("no slant", 0, g.Slants);
                Check.Equal("the T is kept", 1, g.Kept);
                RouteIs("main", g, tree, V(8000, 0), 8000, 0, 150, 0);
                RouteIs("branch", g, tree, V(150, 3000), 150, 3000, 150, 0);
                RouteIs("pipe to the root", g, tree, V(150, 0), 150, 0, 0, 0);
                Check.True("the T is circled", g.WarningPoints.Count == 1 && Near(g.WarningPoints[0], V(150, 0)));
                Check.True("and reported", g.Warnings.Count == 1 && g.Warnings[0].Contains("(150, 0)"));
            });
        }

        private static void ShortBranchKeepsTee()
        {
            Check.Case("a T whose branch is too short is kept", () =>
            {
                DrainTree tree = Tree(V(0, 0), P(0, 0, 4000, 0), P(4000, 0, 8000, 0), P(4000, 0, 4000, 150));
                DrainGeometryResult g = Geometry(tree);
                Check.Equal("no slant", 0, g.Slants);
                Check.Equal("kept", 1, g.Kept);
                RouteIs("main", g, tree, V(8000, 0), 8000, 0, 4000, 0);
                RouteIs("branch", g, tree, V(4000, 150), 4000, 150, 4000, 0);
            });
        }

        private static void NeighbouringChamferCountsOnTheLeg()
        {
            Check.Case("the landing and a neighbouring chamfer share the leg's length", () =>
            {
                // The pipe to the root is (250, 0) -> (0, 0) -> (0, 1000): 250 mm for a landing and a chamfer.
                DrainTree tree = Tree(
                    V(0, 1000), P(0, 1000, 0, 0, 250, 0), P(250, 0, 8000, 0), P(250, 0, 250, 3000));
                DrainGeometryResult g = Geometry(tree);
                Check.Equal("the T gives way", 0, g.Slants);
                Check.Equal("so the bend can be cut", 1, g.Chamfers);
                Check.Equal("one joint kept", 1, g.Kept);
                RouteIs("the pipe to the root", g, tree, V(250, 0), 250, 0, Cut, 0, 0, Cut, 0, 1000);
                Check.True("the T is circled", g.WarningPoints.Count == 1 && Near(g.WarningPoints[0], V(250, 0)));

                // Long enough for both: the landing and the chamfer fit (2 x 212.13 = 424.3).
                DrainTree roomy = Tree(
                    V(0, 1000), P(0, 1000, 0, 0, 430, 0), P(430, 0, 8000, 0), P(430, 0, 430, 3000));
                DrainGeometryResult r = Geometry(roomy);
                Check.Equal("a slant", 1, r.Slants);
                Check.Equal("and a chamfer", 1, r.Chamfers);
                Check.Equal("nothing kept", 0, r.Kept);
                RouteIs("the pipe to the root", r, roomy, V(430, 0), 430 - Cut, 0, Cut, 0, 0, Cut, 0, 1000);
            });
        }

        private static void WyeGeometry()
        {
            Check.Case("two in-line pipes meeting a stem at right angles make a Y", () =>
            {
                DrainTree tree = Tree(
                    V(0, 0), P(0, 0, 0, 4000), P(0, 4000, -3000, 4000), P(0, 4000, 3000, 4000));
                DrainGeometryResult g = Geometry(tree);
                Check.Equal("two slants", 2, g.Slants);
                Check.Equal("nothing kept", 0, g.Kept);
                RouteIs("left arm", g, tree, V(-3000, 4000), -3000, 4000, -Cut, 4000, 0, 4000 - Cut);
                RouteIs("right arm", g, tree, V(3000, 4000), 3000, 4000, Cut, 4000, 0, 4000 - Cut);
                RouteIs("stem starts at the meeting point", g, tree, V(0, 4000), 0, 4000 - Cut, 0, 0);
            });
        }

        private static void CrossStaysACross()
        {
            Check.Case("a cross is left as a plain cross", () =>
            {
                DrainTree tree = Tree(
                    V(0, 0), P(0, 0, 4000, 0), P(4000, 0, 8000, 0), P(4000, 0, 4000, 3000), P(4000, 0, 4000, -3000));
                DrainGeometryResult g = Geometry(tree);
                Check.Equal("no slants", 0, g.Slants);
                Check.Equal("no chamfers", 0, g.Chamfers);
                Check.Equal("nothing kept", 0, g.Kept);
                RouteIs("pipe to the root", g, tree, V(4000, 0), 4000, 0, 0, 0);
                RouteIs("arm along the main", g, tree, V(8000, 0), 8000, 0, 4000, 0);
                RouteIs("arm up", g, tree, V(4000, 3000), 4000, 3000, 4000, 0);
                RouteIs("arm down", g, tree, V(4000, -3000), 4000, -3000, 4000, 0);
                Check.Equal("the tree reported it", 1, tree.Reports.Count);
            });
        }

        private static void NonRightAngleTee()
        {
            Check.Case("a branch that is not at right angles is left and reported", () =>
            {
                DrainTree tree = Tree(
                    V(0, 0), P(0, 0, 4000, 0), P(4000, 0, 9000, 0), P(4000, 0, 4500, 1000 * Math.Sin(Math.PI / 3)));
                DrainGeometryResult g = Geometry(tree);
                Check.Equal("no slant", 0, g.Slants);
                Check.Equal("a warning", 1, g.Warnings.Count);
                Check.True("it says so", g.Warnings.Count == 1 && g.Warnings[0].Contains("not a right-angle T"));
                Check.True("and circles it", g.WarningPoints.Count == 1 && Near(g.WarningPoints[0], V(4000, 0)));
                RouteIs("main unchanged", g, tree, V(9000, 0), 9000, 0, 4000, 0);
            });
        }

        private static void ExistingDiagonalTeeIsQuiet()
        {
            Check.Case("a branch already at 45 degrees is left without a warning", () =>
            {
                double c = 1000 * Math.Cos(Math.PI / 4);
                DrainTree tree = Tree(V(0, 0), P(0, 0, 4000, 0), P(4000, 0, 9000, 0), P(4000, 0, 4000 + c, c));
                DrainGeometryResult g = Geometry(tree);
                Check.Equal("no slant", 0, g.Slants);
                Check.Equal("no warning", 0, g.Warnings.Count);
                Check.Equal("nothing kept", 0, g.Kept);

                // A branch at 45 degrees against the flow is not a proper Y: it is reported.
                DrainTree against = Tree(V(0, 0), P(0, 0, 4000, 0), P(4000, 0, 9000, 0), P(4000, 0, 4000 - c, c));
                Check.Equal("against the flow is reported", 1, Geometry(against).Warnings.Count);
            });
        }

        private static void CustomSlantLength()
        {
            Check.Case("the slant length is a setting; the cut is its cos 45 part", () =>
            {
                foreach (double slant in new[] { 100.0, 400.0 })
                {
                    double cut = slant * Math.Cos(Math.PI / 4);
                    DrainTree tree = TeeTree();
                    DrainGeometryResult g = Geometry(tree, slant);
                    Check.Near("cut for " + slant, cut, g.Cut, 1e-9);
                    RouteIs(
                        "branch for " + slant, g, tree, V(4000, 3000), 4000, 3000, 4000, cut, 4000 - cut, 0);
                    IList<Vec2> branch = g.Segments[SegmentFrom(tree, V(4000, 3000))];
                    Check.Near("slant piece for " + slant, slant, branch[1].DistanceTo(branch[2]), 1e-9);
                }
            });
        }

        // ------------------------------------------------------------------ sizing

        private static void TableEdges()
        {
            Check.Case("the size bands at their edges, 1/100 and 1/50", () =>
            {
                // 1/100: D34 to 120, D48 to 245, D60 to 465, D90 to 900, then D110.
                double[][] at100 =
                {
                    new[] { 0.0, 34 }, new[] { 120.0, 34 }, new[] { 120.01, 48 }, new[] { 245.0, 48 },
                    new[] { 245.01, 60 }, new[] { 465.0, 60 }, new[] { 465.01, 90 }, new[] { 900.0, 90 },
                    new[] { 900.01, 110 }, new[] { 5000.0, 110 }
                };
                foreach (double[] pair in at100)
                {
                    bool extrapolated;
                    Check.Equal(
                        "1/100, " + pair[0] + " kW",
                        (int)pair[1],
                        DrainSizing.TableSize(2, pair[0], DrainSlope.OneIn100, 0.71, out extrapolated));
                }

                // 1/50: D34 to 175, D48 to 346, D60 to 662.
                double[][] at50 =
                {
                    new[] { 175.0, 34 }, new[] { 175.01, 48 }, new[] { 346.0, 48 }, new[] { 346.01, 60 },
                    new[] { 662.0, 60 }, new[] { 662.01, 90 }, new[] { 1280.0, 90 }, new[] { 1280.01, 110 }
                };
                foreach (double[] pair in at50)
                {
                    bool extrapolated;
                    Check.Equal(
                        "1/50, " + pair[0] + " kW",
                        (int)pair[1],
                        DrainSizing.TableSize(2, pair[0], DrainSlope.OneIn50, 0.71, out extrapolated));
                }

                bool ext;
                DrainSizing.TableSize(2, 465, DrainSlope.OneIn100, 0.71, out ext);
                Check.False("D60 is confirmed", ext);
                DrainSizing.TableSize(2, 466, DrainSlope.OneIn100, 0.71, out ext);
                Check.True("D90 is extrapolated", ext);
                DrainSizing.TableSize(2, 901, DrainSlope.OneIn100, 0.71, out ext);
                Check.True("D110 is extrapolated", ext);

                // The allowable flows of the table (Daikin Si39).
                Check.Near("VP30 at 1/100", 88, DrainSizing.Bands[0].Flow100);
                Check.Near("VP40 at 1/100", 175, DrainSizing.Bands[1].Flow100);
                Check.Near("VP50 at 1/100", 334, DrainSizing.Bands[2].Flow100);
                Check.Near("VP30 at 1/50", 125, DrainSizing.Bands[0].Flow50);
                Check.Near("VP40 at 1/50", 247, DrainSizing.Bands[1].Flow50);
                Check.Near("VP50 at 1/50", 473, DrainSizing.Bands[2].Flow50);
            });
        }

        private static void TableFactorScale()
        {
            Check.Case("a different condensate factor scales the limits", () =>
            {
                bool e;
                // Twice the litres per kW: half the kW per size.
                Check.Equal("60 kW fits D34", 34, DrainSizing.TableSize(2, 60, DrainSlope.OneIn100, 1.42, out e));
                Check.Equal("60.01 kW does not", 48, DrainSizing.TableSize(2, 60.01, DrainSlope.OneIn100, 1.42, out e));
                // Half the litres per kW: twice the kW.
                Check.Equal("240 kW fits D34", 34, DrainSizing.TableSize(2, 240, DrainSlope.OneIn100, 0.355, out e));
                Check.Equal("a factor of zero uses the reference", 34, DrainSizing.TableSize(2, 120, DrainSlope.OneIn100, 0, out e));
            });
        }

        private static void SizeText()
        {
            Check.Case("size text and parsing", () =>
            {
                Check.Equal("D34", "D34", DrainSizing.SizeText(34));
                int size;
                Check.True("D60", DrainSizing.TryParseSize("D60", out size) && size == 60);
                Check.True("d60", DrainSizing.TryParseSize("d60", out size) && size == 60);
                Check.True("bare number", DrainSizing.TryParseSize("27", out size) && size == 27);
                Check.True("spaces", DrainSizing.TryParseSize(" D110 ", out size) && size == 110);
                Check.False("not a catalogue size", DrainSizing.TryParseSize("D35", out size));
                Check.False("DN is not D", DrainSizing.TryParseSize("DN34", out size));
                Check.False("empty", DrainSizing.TryParseSize("", out size));
                Check.False("null", DrainSizing.TryParseSize(null, out size));
                Check.Equal("the choices are the catalogue's", "21,27,34,48,60,90,110", string.Join(",", DrainSizing.Choices));
                Check.Equal(
                    "the layer of a size",
                    "EIA_1 Pipe condensated DN34",
                    PipeCatalog.LayerName(PipeKind.Condensate, "DN34"));
            });
        }

        private static double[] Uniform(DrainTree tree, double kw)
        {
            double[] values = new double[tree.Units.Count];
            for (int i = 0; i < values.Length; i++)
                values[i] = kw;
            return values;
        }

        private static DrainSegmentSizing RowFrom(DrainSizingResult sizing, DrainTree tree, Vec2 start)
        {
            int index = SegmentFrom(tree, start);
            return index < 0 ? null : sizing.Segments[index];
        }

        private static void SizingOfAComb()
        {
            Check.Case("sizes of a comb: leaves D27, then by the total kW", () =>
            {
                DrainTree tree = Comb();
                DrainSizingResult sizing = DrainSizing.Compute(tree, Uniform(tree, 40), DrainSlope.OneIn100, 0.71, null);
                DrainSegmentSizing root = sizing.Segments[tree.RootSegment];
                DrainSegmentSizing middle = RowFrom(sizing, tree, V(8000, 0));
                Check.Equal("three units drain through the root pipe", 3, root.UnitCount);
                Check.Near("their total", 120, root.TotalKw);
                Check.Near("the flow is 0.71 L/h per kW", 85.2, root.FlowLh, 1e-9);
                Check.Equal("120 kW is D34", 34, root.Size);
                Check.Equal("the middle pipe drains two units", 2, middle.UnitCount);
                Check.Near("80 kW", 80, middle.TotalKw);
                Check.Equal("D34", 34, middle.Size);
                foreach (DrainUnit unit in tree.Units)
                    Check.Equal("a pipe from a unit is D27", 27, sizing.Segments[unit.Segment].Size);
                Check.True("ready to apply", sizing.CanApply);
                Check.Equal("no warnings", 0, sizing.Warnings.Count);

                DrainSizingResult more = DrainSizing.Compute(tree, Uniform(tree, 40.1), DrainSlope.OneIn100, 0.71, null);
                Check.Equal("120.3 kW is D48", 48, more.Segments[tree.RootSegment].Size);
                Check.Equal("80.2 kW is still D34", 34, RowFrom(more, tree, V(8000, 0)).Size);

                DrainSizingResult big = DrainSizing.Compute(tree, Uniform(tree, 82), DrainSlope.OneIn100, 0.71, null);
                Check.Equal("246 kW is D60", 60, big.Segments[tree.RootSegment].Size);
                Check.Equal("164 kW is D48", 48, RowFrom(big, tree, V(8000, 0)).Size);
            });
        }

        private static void SizingSlope()
        {
            Check.Case("a slope of 1/50 takes larger totals per size", () =>
            {
                DrainTree tree = Comb();
                double[] kw = Uniform(tree, 100);
                DrainSizingResult slow = DrainSizing.Compute(tree, kw, DrainSlope.OneIn100, 0.71, null);
                DrainSizingResult steep = DrainSizing.Compute(tree, kw, DrainSlope.OneIn50, 0.71, null);
                Check.Equal("300 kW at 1/100", 60, slow.Segments[tree.RootSegment].Size);
                Check.Equal("300 kW at 1/50", 48, steep.Segments[tree.RootSegment].Size);
                Check.Equal("200 kW at 1/100", 48, RowFrom(slow, tree, V(8000, 0)).Size);
                Check.Equal("200 kW at 1/50", 48, RowFrom(steep, tree, V(8000, 0)).Size);
            });
        }

        private static void SizingNeverDecreases()
        {
            Check.Case("a pipe is never smaller than the pipes that flow into it", () =>
            {
                DrainTree tree = Comb();
                double[] kw = Uniform(tree, 40);
                int middle = SegmentFrom(tree, V(8000, 0));
                int root = tree.RootSegment;

                // Setting an upstream pipe larger pushes the automatic size of the pipe downstream up.
                Dictionary<int, int> up = new Dictionary<int, int> { { middle, 60 } };
                DrainSizingResult sizing = DrainSizing.Compute(tree, kw, DrainSlope.OneIn100, 0.71, up);
                Check.Equal("the middle pipe is set to D60", 60, sizing.Segments[middle].Size);
                Check.True("and marked as set by hand", sizing.Segments[middle].Manual);
                Check.Equal("the table still gives D34 for the root pipe", 34, sizing.Segments[root].TableSize);
                Check.Equal("the upstream size is D60", 60, sizing.Segments[root].UpstreamSize);
                Check.Equal("so the automatic size is D60", 60, sizing.Segments[root].AutoSize);
                Check.Equal("and the size in use", 60, sizing.Segments[root].Size);
                Check.True("with no warning", sizing.Segments[root].Warning == null);

                // Setting the downstream pipe smaller by hand is kept, with a warning.
                Dictionary<int, int> both = new Dictionary<int, int> { { middle, 60 }, { root, 34 } };
                DrainSizingResult warned = DrainSizing.Compute(tree, kw, DrainSlope.OneIn100, 0.71, both);
                Check.Equal("the hand size is kept", 34, warned.Segments[root].Size);
                Check.True("marked", warned.Segments[root].Manual);
                Check.Equal("the warning", "smaller than upstream D60", warned.Segments[root].Warning);
                Check.True(
                    "the warning is in the list with its segment",
                    warned.Warnings.Contains("S" + (root + 1) + ": smaller than upstream D60"));

                // Below the table for its own load, though not below an upstream pipe.
                Dictionary<int, int> low = new Dictionary<int, int> { { middle, 27 } };
                DrainSizingResult below = DrainSizing.Compute(tree, kw, DrainSlope.OneIn100, 0.71, low);
                Check.Equal("D27 for 80 kW is kept", 27, below.Segments[middle].Size);
                Check.True(
                    "with a warning about the table",
                    below.Segments[middle].Warning != null && below.Segments[middle].Warning.Contains("D34"),
                    below.Segments[middle].Warning ?? "none");
                Check.Equal("the root pipe stays D34", 34, below.Segments[root].Size);

                // Sizes only grow toward the root with no hand sizes.
                DrainSizingResult plain = DrainSizing.Compute(tree, kw, DrainSlope.OneIn100, 0.71, null);
                foreach (DrainSegment segment in tree.Segments)
                {
                    if (segment.Parent < 0)
                        continue;
                    Check.True(
                        "S" + (segment.Index + 1) + " is not larger than its parent",
                        plain.Segments[segment.Index].Size <= plain.Segments[segment.Parent].Size);
                }
            });
        }

        private static void SizingManualKept()
        {
            Check.Case("a size set by hand is kept when the capacities change", () =>
            {
                DrainTree tree = Comb();
                int root = tree.RootSegment;
                Dictionary<int, int> manual = new Dictionary<int, int> { { root, 60 } };
                DrainSizingResult first = DrainSizing.Compute(tree, Uniform(tree, 40), DrainSlope.OneIn100, 0.71, manual);
                Check.Equal("D60", 60, first.Segments[root].Size);
                Check.True("manual", first.Segments[root].Manual);

                DrainSizingResult second = DrainSizing.Compute(tree, Uniform(tree, 10), DrainSlope.OneIn100, 0.71, manual);
                Check.Equal("still D60", 60, second.Segments[root].Size);
                Check.True("still manual", second.Segments[root].Manual);
                Check.Equal("the automatic size follows the capacities", 34, second.Segments[root].AutoSize);

                // The same call without the entry goes back to automatic.
                DrainSizingResult cleared = DrainSizing.Compute(
                    tree, Uniform(tree, 10), DrainSlope.OneIn100, 0.71, new Dictionary<int, int>());
                Check.False("cleared", cleared.Segments[root].Manual);

                // A size that is not in the catalogue is ignored.
                DrainSizingResult odd = DrainSizing.Compute(
                    tree, Uniform(tree, 10), DrainSlope.OneIn100, 0.71, new Dictionary<int, int> { { root, 35 } });
                Check.False("D35 is ignored", odd.Segments[root].Manual);
            });
        }

        private static void SizingMissingCapacity()
        {
            Check.Case("a unit without capacity blocks Apply and counts as zero kW", () =>
            {
                DrainTree tree = Comb();
                double[] kw = Uniform(tree, 40);
                kw[0] = 0;
                DrainSizingResult sizing = DrainSizing.Compute(tree, kw, DrainSlope.OneIn100, 0.71, null);
                Check.Equal("one missing", 1, sizing.MissingCapacity);
                Check.False("cannot apply", sizing.CanApply);
                Check.True("a warning names the unit", sizing.Warnings.Exists(w => w.Contains("E1") && w.Contains("no capacity")));
                Check.Near("the total leaves it out", 80, sizing.Segments[tree.RootSegment].TotalKw);
                Check.Equal("the unit's pipe counts it as missing", 1, sizing.Segments[tree.Units[0].Segment].MissingCapacity);

                kw[0] = double.NaN;
                Check.Equal("not a number is missing too", 1, DrainSizing.Compute(tree, kw, DrainSlope.OneIn100, 0.71, null).MissingCapacity);
                kw[0] = -3;
                Check.Equal("so is a negative", 1, DrainSizing.Compute(tree, kw, DrainSlope.OneIn100, 0.71, null).MissingCapacity);

                DrainSizingResult shortList = DrainSizing.Compute(tree, new double[] { 40 }, DrainSlope.OneIn100, 0.71, null);
                Check.Equal("units past the end of the list are missing", 2, shortList.MissingCapacity);

                Check.True("complete data can apply", DrainSizing.Compute(tree, Uniform(tree, 5), DrainSlope.OneIn100, 0.71, null).CanApply);
            });
        }

        private static void SizingExtrapolated()
        {
            Check.Case("D90 and D110 are flagged as extrapolated", () =>
            {
                DrainTree tree = Comb();
                DrainSizingResult d90 = DrainSizing.Compute(tree, Uniform(tree, 200), DrainSlope.OneIn100, 0.71, null);
                Check.Equal("600 kW is D90", 90, d90.Segments[tree.RootSegment].Size);
                Check.True("flagged", d90.Segments[tree.RootSegment].Extrapolated);
                Check.True("the row warns", d90.Segments[tree.RootSegment].Warning.Contains("extrapolated"));
                Check.Equal("and a note", 1, d90.Notes.Count);

                DrainSizingResult d110 = DrainSizing.Compute(tree, Uniform(tree, 400), DrainSlope.OneIn100, 0.71, null);
                Check.Equal("1200 kW is D110 at 1/100", 110, d110.Segments[tree.RootSegment].Size);
                DrainSizingResult steep = DrainSizing.Compute(tree, Uniform(tree, 400), DrainSlope.OneIn50, 0.71, null);
                Check.Equal("1200 kW is D90 at 1/50", 90, steep.Segments[tree.RootSegment].Size);

                Check.Equal("no note for confirmed sizes", 0,
                    DrainSizing.Compute(tree, Uniform(tree, 40), DrainSlope.OneIn100, 0.71, null).Notes.Count);
            });
        }

        private static void SizingLeafIsD27()
        {
            Check.Case("a pipe that drains one unit is D27 whatever the capacity", () =>
            {
                DrainTree tree = Tree(V(0, 0), P(0, 0, 5000, 0));
                foreach (double kw in new[] { 2.8, 14, 500 })
                {
                    DrainSizingResult sizing = DrainSizing.Compute(tree, new[] { kw }, DrainSlope.OneIn100, 0.71, null);
                    Check.Equal("D27 for " + kw + " kW", 27, sizing.Segments[0].Size);
                    Check.Equal("the auto size too", 27, sizing.Segments[0].AutoSize);
                }

                bool e;
                Check.Equal("the table", 27, DrainSizing.TableSize(1, 1000, DrainSlope.OneIn50, 0.71, out e));
                Check.Equal("two units with no kW are D34", 34, DrainSizing.TableSize(2, 0, DrainSlope.OneIn100, 0.71, out e));
            });
        }

        private static void PreviewColours()
        {
            Check.Case("preview colours differ per size and never use the warning yellow", () =>
            {
                HashSet<short> seen = new HashSet<short>();
                foreach (int size in DrainSizing.Choices)
                {
                    short colour = DrainSizing.PreviewColour(size);
                    Check.True("a new colour for D" + size, seen.Add(colour));
                    Check.True("not yellow for D" + size, colour != 2);
                }

                Check.Equal("an unknown size is white", (short)7, DrainSizing.PreviewColour(35));
            });
        }

        // ------------------------------------------------------------------ planner

        private static void PlannerEndToEnd()
        {
            Check.Case("a rough T is cleaned, becomes a tree and gets its slant", () =>
            {
                List<PipeCurve> curves = new List<PipeCurve>
                {
                    Line(0, Condensate, 0, 0, 4000, 0),
                    Line(1, Condensate, 4005, 3, 10000, 0),
                    Line(2, Condensate, 4000, 0, 4000, 3000)
                };
                DrainPlan plan = DrainPlanner.Run(curves, new DrainSettings(), 0, V(10, 5), 200);
                Check.True("ok" + (plan.Ok ? "" : " (" + plan.Error + ")"), plan.Ok);
                if (!plan.Ok)
                    return;

                Check.Equal("no refnets", 0, plan.Clean.Refnets.Count);
                Check.Equal("no piping rules ran", 0, plan.Clean.RuleChecks.Count);
                Check.False("no root was given to the cleaner", plan.Clean.HasRoot);
                Check.Equal("two units", 2, plan.Tree.Units.Count);
                Check.Equal("three segments", 3, plan.Tree.Segments.Count);
                Check.Equal("one slant", 1, plan.Geometry.Slants);
                Check.Equal("nothing to report", 0, plan.Reports.Count);

                int main = SegmentFrom(plan.Tree, V(10000, 0));
                int branch = SegmentFrom(plan.Tree, V(4000, 3000));
                Check.True("the main ends at the landing point", main >= 0
                    && Near(plan.Geometry.Segments[main][1], V(4000 - Cut, 0), 1e-3), main >= 0 ? Show(plan.Geometry.Segments[main]) : "none");
                Check.True("the branch lands there too", branch >= 0
                    && Near(plan.Geometry.Segments[branch][plan.Geometry.Segments[branch].Count - 1], V(4000 - Cut, 0), 1e-3),
                    branch >= 0 ? Show(plan.Geometry.Segments[branch]) : "none");
            });
        }

        private static void PlannerRefusesBadInput()
        {
            Check.Case("bad input is refused with a reason", () =>
            {
                List<PipeCurve> good = new List<PipeCurve> { Line(0, Condensate, 0, 0, 5000, 0) };

                DrainSettings bad = new DrainSettings { SlantLength = 0 };
                DrainPlan badSettings = DrainPlanner.Run(good, bad, 0, V(0, 0), 200);
                Check.False("a zero slant", badSettings.Ok);
                Check.Contains("the message", badSettings.Error, "slant");

                DrainPlan far = DrainPlanner.Run(good, new DrainSettings(), 0, V(2500, 3000), 200);
                Check.False("a root far from every end", far.Ok);
                Check.Contains("asks for an end", far.Error, "pipe end");

                DrainPlan none = DrainPlanner.Run(new List<PipeCurve>(), new DrainSettings(), 0, V(0, 0), 200);
                Check.False("no curves", none.Ok);

                List<PipeCurve> arcs = new List<PipeCurve> { PolyBulge(0, Condensate, false, 0, 0, 0.5, 1000, 0, 0) };
                DrainPlan arc = DrainPlanner.Run(arcs, new DrainSettings(), 0, V(0, 0), 200);
                Check.False("arcs", arc.Ok);
                Check.Contains("the cleaner's message", arc.Error, "arc");
            });
        }

        private static List<PipeCurve> CurvesOf(DrainPlan plan)
        {
            List<PipeCurve> curves = new List<PipeCurve>();
            for (int i = 0; i < plan.Geometry.Segments.Count; i++)
                curves.Add(PipeCurve.FromPoints(i, Condensate, false, plan.Geometry.Segments[i].ToArray()));
            return curves;
        }

        private static void PlannerIsRepeatable()
        {
            Check.Case("running again on the result changes nothing", () =>
            {
                Vec2 root = V(0, 0);
                List<List<PipeCurve>> inputs = new List<List<PipeCurve>>
                {
                    new List<PipeCurve>
                    {
                        Line(0, Condensate, 0, 0, 4000, 0),
                        Line(1, Condensate, 4000, 0, 10000, 0),
                        Line(2, Condensate, 4000, 0, 4000, 3000)
                    },
                    new List<PipeCurve>
                    {
                        Line(0, Condensate, 0, 0, 5000, 0),
                        Line(1, Condensate, 5000, 0, 5000, 4000)
                    },
                    new List<PipeCurve>
                    {
                        Line(0, Condensate, 0, 0, 0, 4000),
                        Line(1, Condensate, 0, 4000, -3000, 4000),
                        Line(2, Condensate, 0, 4000, 3000, 4000)
                    }
                };

                for (int n = 0; n < inputs.Count; n++)
                {
                    DrainPlan first = DrainPlanner.Run(inputs[n], new DrainSettings(), 0, root, 200);
                    Check.True("first run " + n, first.Ok, first.Error);
                    if (!first.Ok)
                        continue;

                    DrainPlan second = DrainPlanner.Run(CurvesOf(first), new DrainSettings(), 0, root, 200);
                    Check.True("second run " + n, second.Ok, second.Error);
                    if (!second.Ok)
                        continue;

                    Check.Equal("same number of segments " + n, first.Geometry.Segments.Count, second.Geometry.Segments.Count);
                    Check.Equal("no new slants " + n, 0, second.Geometry.Slants);
                    Check.Equal("no new chamfers " + n, 0, second.Geometry.Chamfers);
                    Check.Equal("no warnings " + n, 0, second.Reports.Count);

                    foreach (List<Vec2> route in first.Geometry.Segments)
                    {
                        bool found = false;
                        foreach (List<Vec2> other in second.Geometry.Segments)
                        {
                            if (other.Count != route.Count)
                                continue;
                            bool same = true;
                            for (int i = 0; i < route.Count; i++)
                            {
                                if (!Near(route[i], other[i], 1e-6))
                                    same = false;
                            }

                            if (same)
                                found = true;
                        }

                        Check.True("a segment is unchanged " + n + ": " + Show(route), found);
                    }
                }
            });
        }

        // ------------------------------------------------------------------ settings

        private static void SettingsRoundTrip()
        {
            Check.Case("the settings default as approved and survive a round trip", () =>
            {
                DrainSettings d = new DrainSettings();
                Check.Near("weld", 50, d.WeldTolerance);
                Check.Near("slant", 300, d.SlantLength);
                Check.True("size pipes", d.SizePipes);
                Check.Near("factor", 0.71, d.CondensateFactor);
                Check.Equal("slope", DrainSlope.OneIn100, d.Slope);
                Check.True("labels", d.WriteLabels);
                Check.Near("label height", 150, d.LabelHeight);
                Check.Equal("label format", "D{size}-{slope}%", d.LabelFormat);
                Check.Equal("label layer", "EIA_1 GAS ANNOTATIONS", d.LabelLayer);
                Check.Near("radius", 1500, d.CapacityRadius);
                Check.True("UCS base angle", d.UseUcsBaseAngle);
                Check.Near("cut", 212.132034355964, d.CutDistance, 1e-9);

                DrainSettings s = new DrainSettings
                {
                    WeldTolerance = 12.5,
                    AngleToleranceDegrees = 7,
                    AlignTolerance = 150,
                    JogTolerance = 120,
                    UseUcsBaseAngle = false,
                    BaseAngleDegrees = 33.25,
                    SlantLength = 250,
                    SizePipes = false,
                    CondensateFactor = 0.8,
                    Slope = DrainSlope.OneIn50,
                    WriteLabels = false,
                    LabelHeight = 200,
                    LabelFormat = "{size}/{slope}%",
                    LabelLayer = "",
                    CapacityRadius = 2000
                };
                string text = s.Serialize();
                Check.Contains("a dot decimal in any culture", text, "factor=0.8");
                DrainSettings r = DrainSettings.Deserialize(text);
                Check.Near("weld", 12.5, r.WeldTolerance);
                Check.Near("angle", 7, r.AngleToleranceDegrees);
                Check.Near("align", 150, r.AlignTolerance);
                Check.Near("jog", 120, r.JogTolerance);
                Check.False("ucs", r.UseUcsBaseAngle);
                Check.Near("base angle", 33.25, r.BaseAngleDegrees);
                Check.Near("slant", 250, r.SlantLength);
                Check.False("size pipes", r.SizePipes);
                Check.Near("factor", 0.8, r.CondensateFactor);
                Check.Equal("slope", DrainSlope.OneIn50, r.Slope);
                Check.False("labels", r.WriteLabels);
                Check.Near("height", 200, r.LabelHeight);
                Check.Equal("format", "{size}/{slope}%", r.LabelFormat);
                Check.Equal("blank layer", "", r.LabelLayer);
                Check.Near("radius", 2000, r.CapacityRadius);

                DrainSettings fromEmpty = DrainSettings.Deserialize("");
                Check.Near("empty text gives the defaults", 300, fromEmpty.SlantLength);
                DrainSettings junk = DrainSettings.Deserialize("slant=abc\nunknown=1\nfactor=0.9\n=\nweld=");
                Check.Near("a bad number keeps the default", 300, junk.SlantLength);
                Check.Near("a good line is read", 0.9, junk.CondensateFactor);
                DrainSettings invalid = DrainSettings.Deserialize("slant=-5\nfactor=0.9\n");
                Check.Near("an invalid set falls back to the defaults", 0.71, invalid.CondensateFactor);
            });
        }

        private static void SettingsValidation()
        {
            Check.Case("invalid settings are refused with a reason", () =>
            {
                string error;
                Check.True("the defaults are valid", new DrainSettings().IsValid(out error));
                Check.False("weld 0", new DrainSettings { WeldTolerance = 0 }.IsValid(out error));
                Check.False("angle 30", new DrainSettings { AngleToleranceDegrees = 30 }.IsValid(out error));
                Check.False("slant 0", new DrainSettings { SlantLength = 0 }.IsValid(out error));
                Check.Contains("slant message", error, "slant");
                Check.False("slant NaN", new DrainSettings { SlantLength = double.NaN }.IsValid(out error));
                Check.False("factor 0", new DrainSettings { CondensateFactor = 0 }.IsValid(out error));
                Check.False("label height 0", new DrainSettings { LabelHeight = 0 }.IsValid(out error));
                Check.False("radius -1", new DrainSettings { CapacityRadius = -1 }.IsValid(out error));
                Check.False("empty format", new DrainSettings { LabelFormat = "" }.IsValid(out error));
                Check.False("a layer with a slash", new DrainSettings { LabelLayer = "A/B" }.IsValid(out error));
                Check.True("a blank layer is allowed", new DrainSettings { LabelLayer = "" }.IsValid(out error));

                DrainSettings s = new DrainSettings { WeldTolerance = 33, AngleToleranceDegrees = 8, AlignTolerance = 90, JogTolerance = 70 };
                RefrigSettings c = s.ToRefrigSettings();
                Check.Near("cleaner weld", 33, c.WeldTolerance);
                Check.Near("cleaner angle", 8, c.AngleToleranceDegrees);
                Check.Near("cleaner align", 90, c.AlignTolerance);
                Check.Near("cleaner jog", 70, c.JogTolerance);
                string cleanerError;
                Check.True("the cleaner's settings are valid", c.IsValid(out cleanerError), cleanerError);
            });
        }

        private static void LabelTexts()
        {
            Check.Case("the size label is the size and the slope in percent", () =>
            {
                DrainSettings s = new DrainSettings();
                Check.Equal("D34 at 1/100", "D34-1%", s.LabelText(34));
                Check.Equal("D27", "D27-1%", s.LabelText(27));
                Check.Equal("1/100 text", "1/100", s.SlopeText);
                s.Slope = DrainSlope.OneIn50;
                Check.Equal("D48 at 1/50", "D48-2%", s.LabelText(48));
                Check.Equal("1/50 text", "1/50", s.SlopeText);
                s.LabelFormat = "{size}-{slope}";
                Check.Equal("a custom format", "110-2", s.LabelText(110));
                s.LabelFormat = "DN{size} ({slope}%)";
                Check.Equal("another one", "DN60 (2%)", s.LabelText(60));
                s.LabelFormat = "";
                Check.Equal("an empty format falls back", "D34-2%", s.LabelText(34));

                // The text is placed by the shared label rule: readable, along the longest piece, to its left.
                SizeLabelPlacement placement;
                Check.True(
                    "a placement",
                    SizeLabels.TryPlace(new List<Vec2> { V(0, 0), V(1000, 0), V(1000, 300) }, 150, "D34-1%", out placement));
                Check.True("above the middle of the long leg", Near(placement.Position, V(500, 150)));
                Check.Near("along it", 0, placement.Rotation);
            });
        }
    }
}
