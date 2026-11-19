using System;
using System.Collections.Generic;
using HvacAiCadAddin.Ducts;

namespace DuctCoreTests
{
    /// <summary>
    /// Route solving: straight lengths after takeups, the R = W/2 retry and the "too short"
    /// refusal. All numbers are worked out by hand from the fitting table.
    /// </summary>
    internal static class RouteTests
    {
        private static readonly DuctSize W600 = DuctSize.Rect(600, 300);

        public static void Run()
        {
            SingleSegment();
            LShape();
            UShape();
            RetryWithHalfWidth();
            RefuseTooShort();
            FrozenCorners();
            ZeroLengthStraight();
            SizeChanges();
            ContinuingAnExistingDuct();
            TapLeads();
            RoundElbow();
            ObliqueTurn();
            Refusals();
        }

        private static RouteInput Input(DuctSize size, params double[] xy)
        {
            RouteInput input = new RouteInput();
            for (int i = 0; i < xy.Length; i += 2)
                input.Points.Add(new Vec2(xy[i], xy[i + 1]));
            for (int i = 1; i < input.Points.Count; i++)
                input.Sizes.Add(size);
            return input;
        }

        private static void Straight(string name, RouteResult r, int index, double sx, double sy, double length)
        {
            if (index >= r.Straights.Count)
            {
                Check.Fail(name, "straight " + index, "only " + r.Straights.Count);
                return;
            }

            RouteStraight s = r.Straights[index];
            Check.Near(name + " start x", sx, s.Start.X, 1e-6);
            Check.Near(name + " start y", sy, s.Start.Y, 1e-6);
            Check.Near(name + " length", length, s.Length, 1e-6);
        }

        private static int Count(RouteResult r, string kind)
        {
            int count = 0;
            foreach (RouteFitting f in r.Fittings)
            {
                if (f.Kind == kind)
                    count++;
            }
            return count;
        }

        private static void SingleSegment()
        {
            Check.Case("single free segment", () =>
            {
                RouteResult r = RouteSolver.Solve(Input(W600, 0, 0, 3000, 0));
                Check.True("ok", r.Ok, r.Error);
                Check.Equal("one straight", 1, r.Straights.Count);
                Straight("seg0", r, 0, 0, 0, 3000);
                Check.Equal("no fittings", 0, r.Fittings.Count);
                Check.Near("previous shorten", 0, r.PreviousShorten);
            });
        }

        private static void LShape()
        {
            Check.Case("L route W=600: 90 degree elbow takes 450 from each leg", () =>
            {
                RouteResult r = RouteSolver.Solve(Input(W600, 0, 0, 3000, 0, 3000, 2000));
                Check.True("ok", r.Ok, r.Error);
                Straight("seg0", r, 0, 0, 0, 3000 - 450);
                Straight("seg1", r, 1, 3000, 450, 2000 - 450);
                Check.Equal("one elbow", 1, Count(r, RouteFitting.ElbowKind));
                Check.Equal("elbow at vertex 1", 1, r.Fittings[0].Vertex);
                Check.Equal("elbow size text", "600x300", r.Fittings[0].SizeText);
                Check.True("rectangular elbow has no centreline", r.Fittings[0].Centreline == null);
                Check.Near("elbow area W * R * theta", 600 * 450 * Math.PI / 2, r.Fittings[0].Shape.Area(), 1e-6);
            });
        }

        private static void UShape()
        {
            Check.Case("U route W=600", () =>
            {
                RouteResult r = RouteSolver.Solve(Input(W600, 0, 0, 3000, 0, 3000, 2000, 0, 2000));
                Check.True("ok", r.Ok, r.Error);
                Straight("seg0", r, 0, 0, 0, 2550);
                Straight("seg1 loses 450 at both ends", r, 1, 3000, 450, 1100);
                Straight("seg2 runs -X", r, 2, 2550, 2000, 2550);
                Check.Equal("two elbows", 2, Count(r, RouteFitting.ElbowKind));
            });
        }

        private static void RetryWithHalfWidth()
        {
            Check.Case("a 800 mm middle leg retries both corners with R = W/2", () =>
            {
                // R = 450 would need 900 > 800. R = 300 needs 600 and leaves 200.
                RouteResult r = RouteSolver.Solve(Input(W600, 0, 0, 3000, 0, 3000, 800, 0, 800));
                Check.True("ok", r.Ok, r.Error);
                Straight("seg0", r, 0, 0, 0, 2700);
                Straight("seg1", r, 1, 3000, 300, 200);
                Straight("seg2", r, 2, 2700, 800, 2700);
                Check.True("vertex 1 reduced", r.CornerReduced[1]);
                Check.True("vertex 2 reduced", r.CornerReduced[2]);
                Check.Equal("two notes", 2, r.Notes.Count);
                Check.Near("reduced elbow area W * (W/2) * theta", 600 * 300 * Math.PI / 2, r.Fittings[0].Shape.Area(), 1e-6);
            });

            Check.Case("a 1000 mm middle leg needs no retry", () =>
            {
                RouteResult r = RouteSolver.Solve(Input(W600, 0, 0, 3000, 0, 3000, 1000, 0, 1000));
                Check.True("ok", r.Ok, r.Error);
                Straight("seg1", r, 1, 3000, 450, 100);
                Check.False("vertex 1 not reduced", r.CornerReduced[1]);
                Check.Equal("no notes", 0, r.Notes.Count);
            });
        }

        private static void RefuseTooShort()
        {
            Check.Case("a 500 mm middle leg is refused even with R = W/2", () =>
            {
                RouteResult r = RouteSolver.Solve(Input(W600, 0, 0, 3000, 0, 3000, 500, 0, 500));
                Check.False("not ok", r.Ok);
                Check.Contains("names the segment", r.Error, "Segment 2");
                Check.Contains("says it is too short", r.Error, "too short");
            });
        }

        private static void FrozenCorners()
        {
            Check.Case("a frozen corner keeps its radius, so only the new corner retries", () =>
            {
                // Vertex 1 is frozen at R = 450 (takeup 450); vertex 2 falls back to R = 300:
                // 800 - 450 - 300 = 50.
                RouteInput input = Input(W600, 0, 0, 3000, 0, 3000, 800, 0, 800);
                input.FrozenCorners = 2;
                input.CornerReduced = new bool[4];
                RouteResult r = RouteSolver.Solve(input);
                Check.True("ok", r.Ok, r.Error);
                Straight("seg1", r, 1, 3000, 450, 50);
                Check.False("vertex 1 kept", r.CornerReduced[1]);
                Check.True("vertex 2 reduced", r.CornerReduced[2]);
            });

            Check.Case("all corners frozen: the same route is refused", () =>
            {
                RouteInput input = Input(W600, 0, 0, 3000, 0, 3000, 800, 0, 800);
                input.FrozenCorners = 3;
                RouteResult r = RouteSolver.Solve(input);
                Check.False("not ok", r.Ok);
            });

            Check.Case("a frozen reduced corner stays reduced", () =>
            {
                RouteInput input = Input(W600, 0, 0, 3000, 0, 3000, 2000);
                input.FrozenCorners = 2;
                input.CornerReduced = new[] { false, true, false };
                RouteResult r = RouteSolver.Solve(input);
                Check.True("ok", r.Ok, r.Error);
                Straight("seg0 loses only 300", r, 0, 0, 0, 2700);
            });
        }

        private static void ZeroLengthStraight()
        {
            Check.Case("a straight of exactly zero is allowed", () =>
            {
                RouteResult r = RouteSolver.Solve(Input(W600, 0, 0, 450, 0, 450, 2000));
                Check.True("ok", r.Ok, r.Error);
                Straight("seg0", r, 0, 0, 0, 0);
                Straight("seg1", r, 1, 450, 450, 1550);
            });
        }

        private static void SizeChanges()
        {
            Check.Case("size change on a straight run: 1000x300 -> 500x300", () =>
            {
                RouteInput input = Input(DuctSize.Rect(1000, 300), 0, 0, 2000, 0, 5000, 0);
                input.Sizes[1] = DuctSize.Rect(500, 300);
                RouteResult r = RouteSolver.Solve(input);
                Check.True("ok", r.Ok, r.Error);
                // L = max(500, 603.5) = 603.5
                Straight("seg0", r, 0, 0, 0, 2000);
                Straight("seg1", r, 1, 2603.5, 0, 5000 - 2000 - 603.5);
                Check.Equal("one transition", 1, Count(r, RouteFitting.TransitionKind));
                RouteFitting t = r.Fittings[0];
                Check.Equal("transition size text", "1000x300>500x300", t.SizeText);
                ShapeTests.Vertex("trapezoid", t.Shape, 0, 2000, 500, 0);
                ShapeTests.Vertex("trapezoid", t.Shape, 1, 2603.5, 250, 0);
                ShapeTests.Vertex("trapezoid", t.Shape, 2, 2603.5, -250, 0);
                ShapeTests.Vertex("trapezoid", t.Shape, 3, 2000, -500, 0);
            });

            Check.Case("size change at a corner: elbow in the old size, then the transition", () =>
            {
                RouteInput input = Input(W600, 0, 0, 2000, 0, 2000, 3000);
                input.Sizes[1] = DuctSize.Rect(400, 300);
                RouteResult r = RouteSolver.Solve(input);
                Check.True("ok", r.Ok, r.Error);
                // Elbow takeup 450, transition L = max(300, 241.4) = 300.
                Straight("seg0", r, 0, 0, 0, 1550);
                Straight("seg1", r, 1, 2000, 750, 2250);
                Check.Equal("one elbow", 1, Count(r, RouteFitting.ElbowKind));
                Check.Equal("one transition", 1, Count(r, RouteFitting.TransitionKind));
                RouteFitting t = r.Fittings[1];
                ShapeTests.Vertex("transition starts after the elbow", t.Shape, 0, 1700, 450, 0);
                ShapeTests.Vertex("transition ends 300 later", t.Shape, 1, 1800, 750, 0);
            });

            Check.Case("rectangular to round transition", () =>
            {
                RouteInput input = Input(DuctSize.Rect(500, 300), 0, 0, 2000, 0, 4000, 0);
                input.Sizes[1] = DuctSize.Round(250);
                RouteResult r = RouteSolver.Solve(input);
                Check.True("ok", r.Ok, r.Error);
                // L = max(250, 1.207 * 250, 1.207 * 50) = 301.75
                Straight("round straight", r, 1, 2301.75, 0, 2000 - 301.75);
            });
        }

        private static void ContinuingAnExistingDuct()
        {
            Check.Case("continuing round a corner shortens the existing duct", () =>
            {
                RouteInput input = Input(W600, 2000, 0, 2000, 1500);
                input.PreviousSize = W600;
                input.PreviousDirection = new Vec2(1, 0);
                input.PreviousLength = 2000;
                RouteResult r = RouteSolver.Solve(input);
                Check.True("ok", r.Ok, r.Error);
                Check.Near("previous shortens by 450", 450, r.PreviousShorten);
                Straight("new leg", r, 0, 2000, 450, 1050);
                Check.Equal("elbow at vertex 0", 0, r.Fittings[0].Vertex);
            });

            Check.Case("continuing straight on needs no fitting", () =>
            {
                RouteInput input = Input(W600, 2000, 0, 5000, 0);
                input.PreviousSize = W600;
                input.PreviousDirection = new Vec2(1, 0);
                RouteResult r = RouteSolver.Solve(input);
                Check.True("ok", r.Ok, r.Error);
                Check.Near("previous untouched", 0, r.PreviousShorten);
                Straight("new leg", r, 0, 2000, 0, 3000);
                Check.Equal("no fittings", 0, r.Fittings.Count);
            });

            Check.Case("a short existing duct falls back to R = W/2", () =>
            {
                RouteInput input = Input(W600, 2000, 0, 2000, 1500);
                input.PreviousSize = W600;
                input.PreviousDirection = new Vec2(1, 0);
                input.PreviousLength = 400;
                RouteResult r = RouteSolver.Solve(input);
                Check.True("ok", r.Ok, r.Error);
                Check.Near("previous shortens by 300", 300, r.PreviousShorten);
                Straight("new leg", r, 0, 2000, 300, 1200);
            });

            Check.Case("an existing duct shorter than R = W/2 refuses the point", () =>
            {
                RouteInput input = Input(W600, 2000, 0, 2000, 1500);
                input.PreviousSize = W600;
                input.PreviousDirection = new Vec2(1, 0);
                input.PreviousLength = 250;
                RouteResult r = RouteSolver.Solve(input);
                Check.False("not ok", r.Ok);
                Check.Contains("names the existing duct", r.Error, "existing duct");
            });

            Check.Case("continuing out of a start face runs against the duct axis", () =>
            {
                // The open end is the duct's start: the run arrives travelling -X.
                RouteInput input = Input(W600, 0, 0, 0, -1500);
                input.PreviousSize = W600;
                input.PreviousDirection = new Vec2(-1, 0);
                input.PreviousLength = 2000;
                RouteResult r = RouteSolver.Solve(input);
                Check.True("ok", r.Ok, r.Error);
                Check.Near("previous shortens by 450", 450, r.PreviousShorten);
                Straight("new leg", r, 0, 0, -450, 1050);
            });

            Check.Case("continuing with a different size adds a transition", () =>
            {
                RouteInput input = Input(DuctSize.Rect(500, 300), 2000, 0, 5000, 0);
                input.PreviousSize = DuctSize.Rect(1000, 300);
                input.PreviousDirection = new Vec2(1, 0);
                RouteResult r = RouteSolver.Solve(input);
                Check.True("ok", r.Ok, r.Error);
                Straight("new leg after a 603.5 transition", r, 0, 2603.5, 0, 3000 - 603.5);
                Check.Equal("one transition", 1, Count(r, RouteFitting.TransitionKind));
            });
        }

        private static void TapLeads()
        {
            Check.Case("a branch starting on a main wall loses the tap lead", () =>
            {
                RouteInput input = Input(DuctSize.Rect(350, 250), 0, 0, 0, 2000);
                input.StartLead = 175;
                RouteResult r = RouteSolver.Solve(input);
                Check.True("ok", r.Ok, r.Error);
                Straight("branch", r, 0, 0, 175, 1825);
            });

            Check.Case("tap lead plus an elbow", () =>
            {
                // W=350: R = 262.5, takeup 262.5. 2000 - 175 - 262.5 = 1562.5
                RouteInput input = Input(DuctSize.Rect(350, 250), 0, 0, 0, 2000, 1500, 2000);
                input.StartLead = 175;
                RouteResult r = RouteSolver.Solve(input);
                Check.True("ok", r.Ok, r.Error);
                Straight("seg0", r, 0, 0, 175, 1562.5);
                Straight("seg1", r, 1, 262.5, 2000, 1500 - 262.5);
            });

            Check.Case("a run ending on a main wall loses the end lead (round tap 203)", () =>
            {
                RouteInput input = Input(DuctSize.Round(250), 0, 0, 0, 2000);
                input.EndLead = 203;
                RouteResult r = RouteSolver.Solve(input);
                Check.True("ok", r.Ok, r.Error);
                Straight("run", r, 0, 0, 0, 1797);
            });

            Check.Case("a lead longer than the segment is refused", () =>
            {
                RouteInput input = Input(DuctSize.Round(250), 0, 0, 0, 150);
                input.EndLead = 203;
                RouteResult r = RouteSolver.Solve(input);
                Check.False("not ok", r.Ok);
            });
        }

        private static void RoundElbow()
        {
            Check.Case("round D250 route: R = 375 and a centreline arc", () =>
            {
                RouteResult r = RouteSolver.Solve(Input(DuctSize.Round(250), 0, 0, 2000, 0, 2000, 2000));
                Check.True("ok", r.Ok, r.Error);
                Straight("seg0", r, 0, 0, 0, 1625);
                Straight("seg1", r, 1, 2000, 375, 1625);
                Check.True("centreline arc present", r.Fittings[0].Centreline != null);
                Check.True("centreline flagged", r.Fittings[0].Centreline.IsCentreline);
                Check.Equal("size text", "D250", r.Fittings[0].SizeText);
            });
        }

        private static void ObliqueTurn()
        {
            Check.Case("45 degree turn takes 450 * tan(22.5) = 186.396", () =>
            {
                double d = 1414.2135623731;
                RouteResult r = RouteSolver.Solve(Input(W600, 0, 0, 2000, 0, 2000 + d, d));
                Check.True("ok", r.Ok, r.Error);
                Straight("seg0", r, 0, 0, 0, 2000 - 186.39610306789);
                Check.Near("seg1 length", 2000 - 186.39610306789, r.Straights[1].Length, 1e-6);
            });

            Check.Case("a turn under 0.5 degrees is a straight run", () =>
            {
                RouteResult r = RouteSolver.Solve(Input(W600, 0, 0, 1000, 0, 2000, 5));
                Check.True("ok", r.Ok, r.Error);
                Check.Equal("no elbow", 0, r.Fittings.Count);
                Check.Near("seg0 full", 1000, r.Straights[0].Length);
            });
        }

        private static void Refusals()
        {
            Check.Case("a reversal is too sharp", () =>
            {
                RouteResult r = RouteSolver.Solve(Input(W600, 0, 0, 1000, 0, 0, 10));
                Check.False("not ok", r.Ok);
                Check.Contains("says too sharp", r.Error, "too sharp");
            });

            Check.Case("repeated point is a zero-length segment", () =>
            {
                RouteResult r = RouteSolver.Solve(Input(W600, 0, 0, 0, 0));
                Check.False("not ok", r.Ok);
                Check.Contains("says zero length", r.Error, "zero length");
            });

            Check.Case("bad input is reported, not thrown", () =>
            {
                RouteInput input = new RouteInput();
                input.Points.Add(new Vec2(0, 0));
                RouteResult r = RouteSolver.Solve(input);
                Check.False("not ok", r.Ok);
            });
        }
    }
}
