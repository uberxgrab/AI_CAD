using System;
using HvacAiCadAddin.Ducts;

namespace DuctCoreTests
{
    /// <summary>Fitting outlines: vertex lists with bulges, worked out by hand.</summary>
    internal static class ShapeTests
    {
        private static readonly double Tan22 = Math.Tan(Math.PI / 8);

        public static void Run()
        {
            LeftElbow();
            RightElbow();
            ObliqueElbow();
            SquareThroatElbow();
            RoundElbow();
            StraightOutlines();
            Transitions();
            RectTaps();
            RoundTaps();
        }

        internal static void Vertex(string name, Outline outline, int index, double x, double y, double bulge)
        {
            if (index >= outline.Vertices.Count)
            {
                Check.Fail(name, "vertex " + index, "only " + outline.Vertices.Count + " vertices");
                return;
            }

            OutlineVertex v = outline.Vertices[index];
            Check.Near(name + " v" + index + ".x", x, v.X, 1e-6);
            Check.Near(name + " v" + index + ".y", y, v.Y, 1e-6);
            Check.Near(name + " v" + index + ".bulge", bulge, v.Bulge, 1e-9);
        }

        private static void LeftElbow()
        {
            Check.Case("90 degree left elbow, W=600, R=450", () =>
            {
                // Corner (0,0), travelling +X then +Y: arc centre (-450, 450), outer radius 750,
                // inner radius 150, takeup 450 on both legs.
                ElbowGeometry e = FittingShapes.Elbow(
                    new Vec2(0, 0), new Vec2(1, 0), new Vec2(0, 1), 600, 450);
                Check.Near("takeup", 450, e.Takeup);
                Check.Near("turn angle", Math.PI / 2, e.TurnAngle);
                Check.Near("start x", -450, e.StartPoint.X);
                Check.Near("start y", 0, e.StartPoint.Y);
                Check.Near("end x", 0, e.EndPoint.X);
                Check.Near("end y", 450, e.EndPoint.Y);
                Check.Near("centre x", -450, e.Centre.X);
                Check.Near("centre y", 450, e.Centre.Y);
                Check.True("closed", e.Shape.Closed);
                Check.Equal("vertex count", 4, e.Shape.Vertices.Count);
                Vertex("outer start", e.Shape, 0, -450, -300, Tan22);
                Vertex("outer end", e.Shape, 1, 300, 450, 0);
                Vertex("inner end", e.Shape, 2, -300, 450, -Tan22);
                Vertex("inner start", e.Shape, 3, -450, 300, 0);
                // Area = W * R * theta (Pappus) = 600 * 450 * pi/2.
                Check.Near("area", 270000 * Math.PI / 2, e.Shape.Area(), 1e-6);
                Check.False("a rectangular elbow has no centreline arc to draw", e.Centreline.Closed);
            });
        }

        private static void RightElbow()
        {
            Check.Case("90 degree right elbow, W=600, R=450", () =>
            {
                ElbowGeometry e = FittingShapes.Elbow(
                    new Vec2(0, 0), new Vec2(1, 0), new Vec2(0, -1), 600, 450);
                Check.Near("takeup", 450, e.Takeup);
                Check.Near("centre y", -450, e.Centre.Y);
                Vertex("outer start", e.Shape, 0, -450, 300, -Tan22);
                Vertex("outer end", e.Shape, 1, 300, -450, 0);
                Vertex("inner end", e.Shape, 2, -300, -450, Tan22);
                Vertex("inner start", e.Shape, 3, -450, -300, 0);
                Check.Near("area", 270000 * Math.PI / 2, e.Shape.Area(), 1e-6);
            });
        }

        private static void ObliqueElbow()
        {
            Check.Case("60 degree left elbow, W=600, R=450", () =>
            {
                Vec2 corner = new Vec2(1000, 500);
                Vec2 outDir = new Vec2(Math.Cos(Math.PI / 3), Math.Sin(Math.PI / 3));
                ElbowGeometry e = FittingShapes.Elbow(corner, new Vec2(1, 0), outDir, 600, 450);
                double takeup = 450 * Math.Tan(Math.PI / 6);
                Check.Near("takeup", takeup, e.Takeup, 1e-9);
                Check.Near("turn angle", Math.PI / 3, e.TurnAngle, 1e-12);
                Check.Near("start x", 1000 - takeup, e.StartPoint.X, 1e-9);
                Check.Near("end x", 1000 + takeup * 0.5, e.EndPoint.X, 1e-9);
                Check.Near("end y", 500 + takeup * Math.Sqrt(3) / 2, e.EndPoint.Y, 1e-9);
                Check.Near("bulge is tan(theta/4)", Math.Tan(Math.PI / 12), e.Shape.Vertices[0].Bulge, 1e-12);
                Check.Near("area", 600 * 450 * Math.PI / 3, e.Shape.Area(), 1e-6);
            });
        }

        private static void SquareThroatElbow()
        {
            Check.Case("R = W/2 elbow has a square throat", () =>
            {
                ElbowGeometry e = FittingShapes.Elbow(
                    new Vec2(0, 0), new Vec2(1, 0), new Vec2(0, 1), 600, 300);
                Check.Near("takeup", 300, e.Takeup);
                Check.Equal("vertex count", 3, e.Shape.Vertices.Count);
                Vertex("outer start", e.Shape, 0, -300, -300, Tan22);
                Vertex("outer end", e.Shape, 1, 300, 300, 0);
                Vertex("throat", e.Shape, 2, -300, 300, 0);
                Check.Near("area", 600 * 300 * Math.PI / 2, e.Shape.Area(), 1e-6);
            });
        }

        private static void RoundElbow()
        {
            Check.Case("round D250 elbow, R = 1.5 d = 375", () =>
            {
                FittingSettings f = new FittingSettings();
                double radius = DuctFittings.ElbowRadius(DuctSize.Round(250), f, false);
                ElbowGeometry e = FittingShapes.Elbow(
                    new Vec2(0, 0), new Vec2(1, 0), new Vec2(0, 1), 250, radius);
                Check.Near("takeup", 375, e.Takeup);
                Check.Near("area d * R * theta", 250 * 375 * Math.PI / 2, e.Shape.Area(), 1e-6);
                Check.True("centreline is flagged", e.Centreline.IsCentreline);
                Check.Equal("centreline vertices", 2, e.Centreline.Vertices.Count);
                Vertex("centreline start", e.Centreline, 0, -375, 0, Tan22);
                Vertex("centreline end", e.Centreline, 1, 0, 375, 0);
            });
        }

        private static void StraightOutlines()
        {
            Check.Case("straight outlines", () =>
            {
                Outline a = FittingShapes.Straight(new Vec2(0, 0), new Vec2(1, 0), 2000, 600);
                Vertex("along +X", a, 0, 0, 300, 0);
                Vertex("along +X", a, 1, 2000, 300, 0);
                Vertex("along +X", a, 2, 2000, -300, 0);
                Vertex("along +X", a, 3, 0, -300, 0);
                Check.Near("area", 1200000, a.Area());

                Outline b = FittingShapes.Straight(new Vec2(100, 100), new Vec2(0, 1), 1000, 600);
                Vertex("along +Y", b, 0, -200, 100, 0);
                Vertex("along +Y", b, 1, -200, 1100, 0);
                Vertex("along +Y", b, 2, 400, 1100, 0);
                Vertex("along +Y", b, 3, 400, 100, 0);
            });
        }

        private static void Transitions()
        {
            Check.Case("concentric transition outline", () =>
            {
                Outline t = FittingShapes.Transition(new Vec2(0, 0), new Vec2(1, 0), 300, 600, 400);
                Vertex("trapezoid", t, 0, 0, 300, 0);
                Vertex("trapezoid", t, 1, 300, 200, 0);
                Vertex("trapezoid", t, 2, 300, -200, 0);
                Vertex("trapezoid", t, 3, 0, -300, 0);
                Check.Near("area (600 + 400) / 2 * 300", 150000, t.Area());
            });

            Check.Case("eccentric transition with the flat side on the left", () =>
            {
                Vec2 end;
                Outline t = FittingShapes.EccentricTransition(
                    new Vec2(0, 0), new Vec2(1, 0), new Vec2(0, 1), 1, 346.4, 600, 400, out end);
                Check.Near("end centre x", 346.4, end.X);
                Check.Near("end centre y", 100, end.Y);
                Vertex("flat start", t, 0, 0, 300, 0);
                Vertex("far start", t, 1, 0, -300, 0);
                Vertex("far end", t, 2, 346.4, -100, 0);
                Vertex("flat end", t, 3, 346.4, 300, 0);
                Check.Near("area (600 + 400) / 2 * 346.4", 173200, t.Area(), 1e-6);
            });

            Check.Case("eccentric transition with the flat side on the right", () =>
            {
                Vec2 end;
                Outline t = FittingShapes.EccentricTransition(
                    new Vec2(0, 0), new Vec2(1, 0), new Vec2(0, 1), -1, 346.4, 600, 400, out end);
                Check.Near("end centre y", -100, end.Y);
                Vertex("flat start", t, 0, 0, -300, 0);
                Vertex("far start", t, 1, 0, 300, 0);
                Vertex("far end", t, 2, 346.4, 100, 0);
                Vertex("flat end", t, 3, 346.4, -300, 0);
            });

            Check.Case("eccentric transition growing wider keeps the flat side flat", () =>
            {
                Vec2 end;
                Outline t = FittingShapes.EccentricTransition(
                    new Vec2(0, 0), new Vec2(1, 0), new Vec2(0, 1), 1, 346.4, 400, 600, out end);
                // Flat side at y = +200; the wider far end reaches y = -400.
                Check.Near("end centre y", -100, end.Y);
                Vertex("flat start", t, 0, 0, 200, 0);
                Vertex("far start", t, 1, 0, -200, 0);
                Vertex("far end", t, 2, 346.4, -400, 0);
                Vertex("flat end", t, 3, 346.4, 200, 0);
            });
        }

        private static void RectTaps()
        {
            Check.Case("rectangular shoe tap, Wb=350, branch up from a main running +X", () =>
            {
                // L = 175. The 45 degree side is upstream (-X), so the opening on the wall is
                // from x = -350 to x = +175: 525 = Wb + L. The branch starts 175 from the wall.
                Outline t = FittingShapes.RectTap(new Vec2(0, 0), new Vec2(1, 0), new Vec2(0, 1), 350, 175);
                Check.True("closed", t.Closed);
                Equal4(t, -350, 0, 175, 0, 175, 175, -175, 175);
                Check.Near("area", 0.5 * (525 + 350) * 175, t.Area());
            });

            Check.Case("rectangular shoe tap on the other wall", () =>
            {
                Outline t = FittingShapes.RectTap(new Vec2(0, 0), new Vec2(1, 0), new Vec2(0, -1), 350, 175);
                Equal4(t, -350, 0, 175, 0, 175, -175, -175, -175);
            });

            Check.Case("rectangular shoe tap on a main running +Y", () =>
            {
                Outline t = FittingShapes.RectTap(new Vec2(100, 50), new Vec2(0, 1), new Vec2(-1, 0), 350, 175);
                Equal4(t, 100, -300, 100, 225, -75, 225, -75, -125);
            });

            Check.Case("small branch uses the 102 lead", () =>
            {
                Outline t = FittingShapes.RectTap(new Vec2(0, 0), new Vec2(1, 0), new Vec2(0, 1), 150, 102);
                Equal4(t, -177, 0, 75, 0, 75, 102, -75, 102);
            });
        }

        private static void RoundTaps()
        {
            Check.Case("round conical tap D250", () =>
            {
                FittingSettings f = new FittingSettings();
                Outline t = FittingShapes.RoundTap(new Vec2(0, 0), new Vec2(1, 0), new Vec2(0, 1), 250, f);
                Check.Equal("vertex count", 6, t.Vertices.Count);
                Vertex("base start", t, 0, -150.5, 0, 0);
                Vertex("base end", t, 1, 150.5, 0, 0);
                Vertex("cone end", t, 2, 125, 152, 0);
                Vertex("collar end", t, 3, 125, 203, 0);
                Vertex("collar start", t, 4, -125, 203, 0);
                Vertex("cone start", t, 5, -125, 152, 0);
                Check.Near("area", (301 + 250) / 2.0 * 152 + 250 * 51, t.Area());
            });

            Check.Case("Tap() picks the shape from the branch", () =>
            {
                FittingSettings f = new FittingSettings();
                Outline round = FittingShapes.Tap(
                    new Vec2(0, 0), new Vec2(1, 0), new Vec2(0, 1), DuctSize.Round(250), f);
                Check.Equal("round has 6 vertices", 6, round.Vertices.Count);
                Outline rect = FittingShapes.Tap(
                    new Vec2(0, 0), new Vec2(1, 0), new Vec2(0, 1), DuctSize.Rect(350, 250), f);
                Equal4(rect, -350, 0, 175, 0, 175, 175, -175, 175);
            });
        }

        private static void Equal4(Outline outline, params double[] xy)
        {
            Check.Equal("four vertices", 4, outline.Vertices.Count);
            for (int i = 0; i < 4 && i < outline.Vertices.Count; i++)
                Vertex("vertex", outline, i, xy[2 * i], xy[2 * i + 1], 0);
        }
    }
}
