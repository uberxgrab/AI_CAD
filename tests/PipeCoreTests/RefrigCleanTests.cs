using System;
using System.Collections.Generic;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Pipes;
using static PipeCoreTests.Support;

namespace PipeCoreTests
{
    /// <summary>
    /// HVACREFRIG geometry cleaning: weld, T-splits, classify, chain, cluster, collapse jogs, delete
    /// zero-length edges, dissolve collinear nodes, and the checks before anything is written
    /// (AGENTS.md, "HVACREFRIG", steps 3 and 4). Every pipe end is an anchor and never moves.
    /// </summary>
    internal static class RefrigCleanTests
    {
        public static void Run()
        {
            RejectsBadInput();
            AlreadyCleanIsUnchanged();
            WeldsNearEndpoints();
            StraightensWobble();
            SquaresCorner();
            AnchorsStayExactly();
            CollapsesJog();
            MergesJogWhenAlignToleranceIsSmaller();
            KeepsJogWhenAnchorsDisagree();
            KeepsJogLongerThanTolerance();
            AngleToleranceControlsClassification();
            BaseAngleRotatesClasses();
            Diagonals();
            ClusterCap();
            ClusterOverlapAndPins();
            ClusterWeightedMean();
            NewCrossingIsRejected();
            ExistingCrossingsAreAllowed();
            CountsComponents();
            RemovesDuplicates();
            RootChecks();
            SegmentDistanceMaths();
        }

        private static IList<PipeCurve> Tee()
        {
            // Root (0,0) to a T at (5000,0); through to (9000,0); branch up to (5000,3000).
            return new List<PipeCurve>
            {
                Line(0, Rp, 0, 0, 5000, 0),
                Line(1, Rp, 5000, 0, 9000, 0),
                Line(2, Rp, 5000, 0, 5000, 3000)
            };
        }

        private static void RejectsBadInput()
        {
            Check.Case("bad input is refused with a reason", () =>
            {
                RefrigResult badAngle = Clean(Tee(), input => input.Settings.AngleToleranceDegrees = 30);
                Check.False("angle above 22.5", badAngle.Ok);
                Check.Contains("angle message", badAngle.Error, "angle tolerance");

                RefrigResult empty = Clean(new List<PipeCurve>());
                Check.False("no curves", empty.Ok);

                RefrigResult arcs = Clean(new List<PipeCurve> { PolyBulge(0, Rp, false, 0, 0, 0.5, 1000, 0, 0) });
                Check.False("arcs", arcs.Ok);
                Check.Contains("arc message", arcs.Error, "arc");

                RefrigResult loop = Clean(new List<PipeCurve> { Poly(0, Rp, true, 0, 0, 1000, 0, 1000, 1000, 0, 1000) });
                Check.False("a closed loop", loop.Ok);
                Check.Contains("loop message", loop.Error, "loop");

                // Three lines forming a triangle through welded corners.
                RefrigResult triangle = Clean(new List<PipeCurve>
                {
                    Line(0, Rp, 0, 0, 1000, 0),
                    Line(1, Rp, 1000, 0, 500, 800),
                    Line(2, Rp, 500, 800, 0, 0)
                });
                Check.False("a loop of separate lines", triangle.Ok);
                Check.Contains("loop message", triangle.Error, "loop");
                Check.True("the loop is pointed out", triangle.ErrorPoints.Count > 0);
            });
        }

        private static void AlreadyCleanIsUnchanged()
        {
            Check.Case("a clean tee is left alone", () =>
            {
                RefrigResult r = Clean(Tee());
                Check.True("ok", r.Ok);
                Check.False("nothing changed", r.Changed);
                Check.Equal("paths", 3, r.Paths.Count);
                Check.Equal("anchors", 3, r.Anchors);
                Check.Equal("components", 1, r.Components);
                Check.Equal("moved", 0, r.NodesMoved);
                Check.Equal("no refnets without a root", 0, r.Refnets.Count);
                Check.True("through", HasPath(r, V(5000, 0), V(9000, 0)));
                Check.True("branch", HasPath(r, V(5000, 0), V(5000, 3000)));
                Check.True("trunk", HasPath(r, V(0, 0), V(5000, 0)));
            });
        }

        private static void WeldsNearEndpoints()
        {
            Check.Case("endpoints 30 mm apart are welded, then the straight node is dissolved", () =>
            {
                RefrigResult r = Clean(new List<PipeCurve>
                {
                    Line(0, Rp, 0, 0, 2000, 0),
                    Line(1, Rp, 2030, 0, 4000, 0)
                });
                Check.True("ok", r.Ok);
                Check.True("changed", r.Changed);
                Check.Equal("paths", 1, r.Paths.Count);
                Check.True("one run end to end", HasPath(r, V(0, 0), V(4000, 0)));
                Check.Equal("dissolved", 1, r.NodesDissolved);
            });
        }

        private static void StraightensWobble()
        {
            Check.Case("a wobbling polyline between two level ends is straightened", () =>
            {
                RefrigResult r = Clean(new List<PipeCurve> { Poly(0, Rp, false, 0, 0, 2000, 40, 4000, 0) });
                Check.True("ok", r.Ok);
                Check.Equal("paths", 1, r.Paths.Count);
                Check.True("straight", HasPath(r, V(0, 0), V(4000, 0)));
                Check.Equal("dissolved", 1, r.NodesDissolved);
            });
        }

        private static void SquaresCorner()
        {
            Check.Case("a corner is squared onto the two anchored lines", () =>
            {
                RefrigResult r = Clean(new List<PipeCurve>
                {
                    Line(0, Rp, 0, 0, 3000, 60),
                    Line(1, Rp, 3000, 60, 3050, 4000)
                });
                Check.True("ok", r.Ok);
                Check.Equal("paths: the corner is a vertex of one polyline", 1, r.Paths.Count);
                RefrigPath path = r.Paths[0];
                Check.Equal("vertices", 3, path.Points.Count);
                bool forward = Near(path.Points[0], V(0, 0));
                Vec2 first = forward ? path.Points[0] : path.Points[2];
                Vec2 corner = path.Points[1];
                Vec2 last = forward ? path.Points[2] : path.Points[0];
                Check.True("start", Near(first, V(0, 0)));
                Check.True("corner on the level of the root and the plumb line of the leaf", Near(corner, V(3050, 0)));
                Check.True("end", Near(last, V(3050, 4000)));
                Check.Equal("one node moved", 1, r.NodesMoved);
            });
        }

        private static void AnchorsStayExactly()
        {
            Check.Case("pipe ends are never moved", () =>
            {
                RefrigResult r = Clean(new List<PipeCurve>
                {
                    Line(0, Rp, 0.123, -0.456, 3000, 60),
                    Line(1, Rp, 3000, 60, 3050, 4000.789)
                });
                Check.True("ok", r.Ok);
                Check.True("the first end", AnyPathHasPoint(r, V(0.123, -0.456), 1e-9));
                Check.True("the second end", AnyPathHasPoint(r, V(3050, 4000.789), 1e-9));
            });
        }

        // Root (0,0) -> (2000,0) -> jog up by `height` -> T at (6000, height) with a stub up to a leaf and a
        // through pipe to a leaf at (9000, 0). The through leaf is level with the root.
        private static IList<PipeCurve> JogNetwork(double height)
        {
            return new List<PipeCurve>
            {
                Poly(0, Rp, false, 0, 0, 2000, 0, 2000, height, 6000, height),
                Line(1, Rp, 6000, height, 6000, 3000),
                Line(2, Rp, 6000, height, 9000, 0)
            };
        }

        private static void CollapsesJog()
        {
            Check.Case("a 150 mm jog between level ends is collapsed", () =>
            {
                RefrigResult r = Clean(JogNetwork(150));
                Check.True("ok", r.Ok);
                Check.Equal("paths", 3, r.Paths.Count);
                Check.True("the trunk is one straight run", HasPath(r, V(0, 0), V(6000, 0)));
                Check.True("the through pipe", HasPath(r, V(6000, 0), V(9000, 0)));
                Check.True("the stub", HasPath(r, V(6000, 0), V(6000, 3000)));
                Check.Equal("the jog edge became zero-length and was removed", 1, r.ZeroLengthRemoved);
                Check.Equal("the straight node was dissolved", 1, r.NodesDissolved);
                Check.Equal("the cluster step already merged them", 0, r.JogsMerged);
            });
        }

        private static void MergesJogWhenAlignToleranceIsSmaller()
        {
            Check.Case("the jog step merges what the align step could not", () =>
            {
                RefrigResult r = Clean(JogNetwork(150), input =>
                {
                    input.Settings.AlignTolerance = 100;
                    input.Settings.JogTolerance = 200;
                });
                Check.True("ok", r.Ok);
                Check.Equal("one jog merged", 1, r.JogsMerged);
                Check.True("the trunk is one straight run", HasPath(r, V(0, 0), V(6000, 0)));
                Check.True("the through pipe", HasPath(r, V(6000, 0), V(9000, 0)));
            });
        }

        private static void KeepsJogWhenAnchorsDisagree()
        {
            Check.Case("a jog between ends at different levels stays", () =>
            {
                RefrigResult r = Clean(new List<PipeCurve>
                {
                    Poly(0, Rp, false, 0, 0, 2000, 0, 2000, 150, 4000, 150)
                });
                Check.True("ok", r.Ok);
                Check.False("unchanged", r.Changed);
                Check.Equal("jogs merged", 0, r.JogsMerged);
                Check.Equal("paths", 1, r.Paths.Count);
                Check.Equal("the jog's corners are kept", 4, r.Paths[0].Points.Count);
            });
        }

        private static void KeepsJogLongerThanTolerance()
        {
            Check.Case("a jog longer than the tolerance stays", () =>
            {
                RefrigResult r = Clean(JogNetwork(250));
                Check.True("ok", r.Ok);
                Check.Equal("jogs merged", 0, r.JogsMerged);
                Check.True("both corners of the jog remain", AnyPathHasPoint(r, V(2000, 0)) && AnyPathHasPoint(r, V(2000, 250)));
            });
        }

        private static void AngleToleranceControlsClassification()
        {
            Check.Case("the angle tolerance decides what counts as horizontal", () =>
            {
                double rise = 3000 * Math.Tan(12 * Math.PI / 180);
                IList<PipeCurve> curves = new List<PipeCurve>
                {
                    Line(0, Rp, 0, 0, 3000, rise),
                    Line(1, Rp, 3000, rise, 3000, 4000)
                };

                RefrigResult strict = Clean(curves);
                Check.True("ok at 10 degrees", strict.Ok);
                Check.False("a pipe 12 degrees off stays as drawn", strict.Changed);

                RefrigResult loose = Clean(curves, input => input.Settings.AngleToleranceDegrees = 15);
                Check.True("ok at 15 degrees", loose.Ok);
                Check.True("changed at 15 degrees", loose.Changed);
                Check.True("the corner is squared", AnyPathHasPoint(loose, V(3000, 0)));
            });
        }

        private static void BaseAngleRotatesClasses()
        {
            Check.Case("the base angle rotates the classes", () =>
            {
                double angle = 30 * Math.PI / 180;
                Vec2 a = Rotate(V(0, 0), angle);
                Vec2 b = Rotate(V(3000, 60), angle);
                Vec2 c = Rotate(V(3050, 4000), angle);
                IList<PipeCurve> rotated = new List<PipeCurve>
                {
                    Line(0, Rp, a.X, a.Y, b.X, b.Y),
                    Line(1, Rp, b.X, b.Y, c.X, c.Y)
                };

                RefrigResult world = Clean(rotated);
                Check.True("ok on the world axes", world.Ok);
                Check.False("30 degrees off the world axes: left alone", world.Changed);

                RefrigResult ucs = Clean(rotated, input => input.BaseAngle = angle);
                Check.True("ok on the rotated axes", ucs.Ok);
                Check.True("squared to the rotated axes", AnyPathHasPoint(ucs, Rotate(V(3050, 0), angle), 1e-6));
            });
        }

        private static void Diagonals()
        {
            Check.Case("45 degree pipes are classified and squared to the diagonal", () =>
            {
                // Root, a 45 degree run, then a corner onto the other diagonal.
                RefrigResult r = Clean(new List<PipeCurve>
                {
                    Line(0, Rp, 0, 0, 2000, 2030),
                    Line(1, Rp, 2000, 2030, 4000, 40)
                });
                Check.True("ok", r.Ok);
                // Anchored at (0,0) on x = y and (4000,40) on x + y = 4040. The corner is their meeting point.
                Check.True("corner on both diagonals", AnyPathHasPoint(r, V(2020, 2020)));
            });
        }

        private static RefrigCleaner.Chain C(double mean, double lo, double hi, double weight = 100)
        {
            return new RefrigCleaner.Chain { Mean = mean, Lo = lo, Hi = hi, Weight = weight };
        }

        private static RefrigCleaner.Chain Pinned(double pin, double lo, double hi)
        {
            return new RefrigCleaner.Chain { Mean = pin, Pin = pin, HasPin = true, Lo = lo, Hi = hi, Weight = 100 };
        }

        private static void ClusterCap()
        {
            Check.Case("the cluster span is capped so merges cannot chain on", () =>
            {
                List<RefrigCleaner.Chain> chains = new List<RefrigCleaner.Chain>
                {
                    C(0, 0, 100), C(120, 100, 200), C(240, 200, 300), C(360, 300, 400)
                };
                List<RefrigCleaner.Cluster> clusters = RefrigCleaner.ClusterChains(chains, 0, 200, 0);
                Check.Equal("clusters", 2, clusters.Count);
                Check.Equal("first cluster members", 2, clusters[0].Members.Count);
                Check.Near("first cluster coordinate (equal weights)", 60, clusters[0].Coord);
                Check.Near("second cluster coordinate", 300, clusters[1].Coord);

                List<RefrigCleaner.Chain> within = new List<RefrigCleaner.Chain> { C(0, 0, 100), C(200, 100, 200) };
                Check.Equal("exactly at the cap still merges", 1,
                    RefrigCleaner.ClusterChains(within, 0, 200, 0).Count);

                List<RefrigCleaner.Chain> beyond = new List<RefrigCleaner.Chain> { C(0, 0, 100), C(201, 100, 200) };
                Check.Equal("beyond the cap does not", 2, RefrigCleaner.ClusterChains(beyond, 0, 200, 0).Count);
            });
        }

        private static void ClusterOverlapAndPins()
        {
            Check.Case("overlapping chains and different pins stay apart", () =>
            {
                List<RefrigCleaner.Chain> overlapping = new List<RefrigCleaner.Chain>
                {
                    C(0, 0, 1000), C(50, 500, 1500)
                };
                Check.Equal("parallel pipes side by side", 2,
                    RefrigCleaner.ClusterChains(overlapping, 0, 200, 50).Count);

                List<RefrigCleaner.Chain> touching = new List<RefrigCleaner.Chain>
                {
                    C(0, 0, 1000), C(50, 1000, 2000)
                };
                Check.Equal("pipes end to end", 1, RefrigCleaner.ClusterChains(touching, 0, 200, 50).Count);

                List<RefrigCleaner.Chain> slightOverlap = new List<RefrigCleaner.Chain>
                {
                    C(0, 0, 1000), C(50, 960, 2000)
                };
                Check.Equal("an overlap within the weld tolerance counts as touching", 1,
                    RefrigCleaner.ClusterChains(slightOverlap, 0, 200, 50).Count);

                List<RefrigCleaner.Chain> pins = new List<RefrigCleaner.Chain>
                {
                    Pinned(0, 0, 100), Pinned(100, 200, 300), C(40, 400, 500)
                };
                List<RefrigCleaner.Cluster> clusters = RefrigCleaner.ClusterChains(pins, 0, 200, 0);
                Check.Equal("two pins, two clusters", 2, clusters.Count);
                Check.Near("the free chain joins the nearer pin", 0, clusters[0].Coord);
                Check.Equal("the nearer pin's cluster has the free chain", 2, clusters[0].Members.Count);

                List<RefrigCleaner.Chain> samePin = new List<RefrigCleaner.Chain>
                {
                    Pinned(75, 0, 100), Pinned(75, 200, 300)
                };
                Check.Equal("equal pins merge", 1, RefrigCleaner.ClusterChains(samePin, 0, 200, 0).Count);
            });
        }

        private static void ClusterWeightedMean()
        {
            Check.Case("the cluster coordinate is the length-weighted mean", () =>
            {
                List<RefrigCleaner.Chain> chains = new List<RefrigCleaner.Chain>
                {
                    C(0, 0, 100, 100), C(100, 100, 400, 300)
                };
                List<RefrigCleaner.Cluster> clusters = RefrigCleaner.ClusterChains(chains, 0, 200, 0);
                Check.Equal("one cluster", 1, clusters.Count);
                Check.Near("(0*100 + 100*300) / 400", 75, clusters[0].Coord);

                List<RefrigCleaner.Chain> pinnedFirst = new List<RefrigCleaner.Chain>
                {
                    Pinned(10, 0, 100), C(100, 100, 400, 300)
                };
                List<RefrigCleaner.Cluster> pinnedClusters = RefrigCleaner.ClusterChains(pinnedFirst, 0, 200, 0);
                Check.Equal("one pinned cluster", 1, pinnedClusters.Count);
                Check.Near("the pin wins over the mean", 10, pinnedClusters[0].Coord);
            });
        }

        private static void NewCrossingIsRejected()
        {
            Check.Case("cleaning that would create a crossing is refused", () =>
            {
                // Squaring the corner of the first pipe lengthens its vertical leg down to y = 0, across a
                // short separate pipe at y = 30 that it did not touch before (its leg started at y = 60).
                // The weld tolerance is 5 so that the corner, 30 mm from the short pipe, does not weld to it.
                RefrigResult r = Clean(new List<PipeCurve>
                {
                    Line(0, Rp, 0, 0, 3000, 60),
                    Line(1, Rp, 3000, 60, 3000, 4000),
                    Line(2, Rp, 2900, 30, 3100, 30)
                }, input => input.Settings.WeldTolerance = 5);
                Check.False("refused", r.Ok);
                Check.Contains("message", r.Error, "crossing");
                Check.True("the crossing is pointed out", r.ErrorPoints.Count > 0);
                if (r.ErrorPoints.Count > 0)
                    Check.True("at the right place", Near(V(3000, 30), r.ErrorPoints[0], 1e-6));
                Check.Equal("nothing is offered for writing", 0, r.Paths.Count);
            });
        }

        private static void ExistingCrossingsAreAllowed()
        {
            Check.Case("pipes that already cross at different levels may keep crossing", () =>
            {
                RefrigResult r = Clean(new List<PipeCurve>
                {
                    Line(0, Rp, 0, 0, 1000, 0),
                    Line(1, Rp, 500, -500, 500, 500)
                });
                Check.True("ok", r.Ok);
                Check.False("unchanged", r.Changed);
                Check.Equal("paths", 2, r.Paths.Count);
            });
        }

        private static void CountsComponents()
        {
            Check.Case("separate networks are counted and kept apart", () =>
            {
                RefrigResult r = Clean(new List<PipeCurve>
                {
                    Line(0, Rp, 0, 0, 1000, 0),
                    Line(1, Rp, 0, 5000, 1000, 5000),
                    Line(2, Rp, 0, 9000, 1000, 9000)
                });
                Check.True("ok", r.Ok);
                Check.Equal("components", 3, r.Components);
                Check.Equal("paths", 3, r.Paths.Count);
            });
        }

        private static void RemovesDuplicates()
        {
            Check.Case("a pipe drawn twice is kept once", () =>
            {
                RefrigResult r = Clean(new List<PipeCurve>
                {
                    Line(0, Rp, 0, 0, 1000, 0),
                    Line(1, Rp, 0, 0, 1000, 0)
                });
                Check.True("ok", r.Ok);
                Check.Equal("duplicates removed", 1, r.DuplicatesRemoved);
                Check.Equal("paths", 1, r.Paths.Count);
                Check.True("changed", r.Changed);
            });
        }

        private static void RootChecks()
        {
            Check.Case("the root must be a pipe end", () =>
            {
                RefrigResult far = CleanWithRoot(Tee(), V(20000, 20000));
                Check.False("no end near the pick", far.Ok);
                Check.Contains("message", far.Error, "root");

                // The T node is not an end, so a pick beside it finds the nearest end instead, which is
                // too far away at this tolerance.
                RefrigResult atTee = CleanWithRoot(Tee(), V(5000, 10), input => input.RootTolerance = 100);
                Check.False("a pick at the junction", atTee.Ok);

                RefrigResult near = CleanWithRoot(Tee(), V(30, -20));
                Check.True("a pick near an end", near.Ok);
            });
        }

        private static void SegmentDistanceMaths()
        {
            Check.Case("segment distance", () =>
            {
                Vec2 at;
                Check.Near("crossing", 0, RefrigCleaner.SegmentDistance(V(0, 0), V(10, 0), V(5, -5), V(5, 5), out at));
                Check.True("crossing point", Near(V(5, 0), at));
                Check.Near("parallel", 3, RefrigCleaner.SegmentDistance(V(0, 0), V(10, 0), V(0, 3), V(10, 3), out at));
                Check.Near("end touching the middle", 0,
                    RefrigCleaner.SegmentDistance(V(0, 0), V(10, 0), V(5, 0), V(5, 4), out at));
                Check.Near("collinear overlap", 0,
                    RefrigCleaner.SegmentDistance(V(0, 0), V(10, 0), V(5, 0), V(15, 0), out at));
                Check.Near("collinear apart", 5,
                    RefrigCleaner.SegmentDistance(V(0, 0), V(10, 0), V(15, 0), V(20, 0), out at));
                Check.Near("skew", Math.Sqrt(2),
                    RefrigCleaner.SegmentDistance(V(0, 0), V(10, 0), V(11, 1), V(11, 5), out at));
            });
        }
    }
}
