using System;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Pipes;

namespace PipeCoreTests
{
    /// <summary>Line and arc segment maths.</summary>
    internal static class SegmentTests
    {
        // The bulge of a quarter circle: tan(sweep / 4) with a sweep of 90 degrees.
        private static readonly double QuarterBulge = Math.Tan(Math.PI / 8);

        public static void Run()
        {
            Lengths();
            ArcPoints();
            ClockwiseArc();
            Projection();
            SubBulges();
            StraightProjection();
        }

        private static void Lengths()
        {
            Check.Case("segment lengths", () =>
            {
                Check.Near("line", 5, PipeSeg.Length(new Vec2(0, 0), new Vec2(3, 4), 0));
                // A semicircle over a chord of 2 has radius 1 and length pi.
                Check.Near("semicircle", Math.PI, PipeSeg.Length(new Vec2(0, 0), new Vec2(2, 0), 1));
                // A quarter circle of radius 1 between (1,0) and (0,1).
                Check.Near("quarter circle", Math.PI / 2,
                    PipeSeg.Length(new Vec2(1, 0), new Vec2(0, 1), QuarterBulge));
                Check.Near("negative bulge has the same length", Math.PI / 2,
                    PipeSeg.Length(new Vec2(1, 0), new Vec2(0, 1), -QuarterBulge));
                Check.True("tiny bulge is straight", PipeSeg.IsStraight(1e-12));
                Check.False("a real bulge is not", PipeSeg.IsStraight(0.1));
            });
        }

        private static void ArcPoints()
        {
            Check.Case("counter-clockwise arc points", () =>
            {
                Vec2 a = new Vec2(1, 0);
                Vec2 b = new Vec2(0, 1);
                double half = Math.Sqrt(0.5);
                Check.True("start", Support.Near(a, PipeSeg.PointAt(a, b, QuarterBulge, 0)));
                Check.True("end", Support.Near(b, PipeSeg.PointAt(a, b, QuarterBulge, 1)));
                Check.True("middle", Support.Near(new Vec2(half, half), PipeSeg.PointAt(a, b, QuarterBulge, 0.5)));
                Check.True("a third", Support.Near(
                    new Vec2(Math.Cos(Math.PI / 6), Math.Sin(Math.PI / 6)),
                    PipeSeg.PointAt(a, b, QuarterBulge, 1.0 / 3)));
            });
        }

        private static void ClockwiseArc()
        {
            Check.Case("clockwise arc points", () =>
            {
                Vec2 a = new Vec2(0, 1);
                Vec2 b = new Vec2(1, 0);
                double half = Math.Sqrt(0.5);
                Check.True("middle", Support.Near(new Vec2(half, half), PipeSeg.PointAt(a, b, -QuarterBulge, 0.5)));

                // The same chord with a positive bulge bows the other way, through (-0.2929.., ...).
                Vec2 other = PipeSeg.PointAt(a, b, QuarterBulge, 0.5);
                Check.True("opposite bulge goes round the other side", other.X < 0.5 && other.Y < 0.5);
            });
        }

        private static void Projection()
        {
            Check.Case("projection onto an arc", () =>
            {
                Vec2 a = new Vec2(1, 0);
                Vec2 b = new Vec2(0, 1);
                double distance;

                // A point at 30 degrees, 1.5 from the centre: a third of the way, half a unit off.
                Vec2 p = new Vec2(1.5 * Math.Cos(Math.PI / 6), 1.5 * Math.Sin(Math.PI / 6));
                double f = PipeSeg.Project(a, b, QuarterBulge, p, out distance);
                Check.Near("fraction", 1.0 / 3, f, 1e-9);
                Check.Near("distance", 0.5, distance, 1e-9);

                // A point outside the sweep maps to the nearer end.
                f = PipeSeg.Project(a, b, QuarterBulge, new Vec2(0, -1), out distance);
                Check.Near("outside, near the start", 0, f);
                Check.Near("outside distance", Math.Sqrt(2), distance, 1e-9);

                f = PipeSeg.Project(a, b, QuarterBulge, new Vec2(-1, 0), out distance);
                Check.Near("outside, near the end", 1, f);
                Check.Near("outside distance to the end", Math.Sqrt(2), distance, 1e-9);

                // The point on the arc itself.
                Vec2 on = PipeSeg.PointAt(a, b, QuarterBulge, 0.75);
                f = PipeSeg.Project(a, b, QuarterBulge, on, out distance);
                Check.Near("on the arc, fraction", 0.75, f, 1e-9);
                Check.Near("on the arc, distance", 0, distance, 1e-9);

                // Clockwise arcs project the same way.
                Vec2 c = new Vec2(0, 1);
                Vec2 d = new Vec2(1, 0);
                Vec2 onClockwise = PipeSeg.PointAt(c, d, -QuarterBulge, 0.25);
                f = PipeSeg.Project(c, d, -QuarterBulge, onClockwise, out distance);
                Check.Near("clockwise fraction", 0.25, f, 1e-9);
                Check.Near("clockwise distance", 0, distance, 1e-9);
            });
        }

        private static void SubBulges()
        {
            Check.Case("sub-arc bulges", () =>
            {
                Check.Near("a straight segment stays straight", 0, PipeSeg.SubBulge(0, 0.2, 0.7));
                // Half of a quarter circle sweeps 45 degrees: bulge tan(45/4 degrees).
                Check.Near("half a quarter", Math.Tan(Math.PI / 16), PipeSeg.SubBulge(QuarterBulge, 0, 0.5), 1e-12);
                Check.Near("the whole arc", QuarterBulge, PipeSeg.SubBulge(QuarterBulge, 0, 1), 1e-12);
                Check.Near("clockwise keeps its sign", -Math.Tan(Math.PI / 16),
                    PipeSeg.SubBulge(-QuarterBulge, 0.5, 1), 1e-12);
            });
        }

        private static void StraightProjection()
        {
            Check.Case("projection onto a line", () =>
            {
                double distance;
                Vec2 a = new Vec2(0, 0);
                Vec2 b = new Vec2(10, 0);
                Check.Near("middle", 0.3, PipeSeg.Project(a, b, 0, new Vec2(3, 4), out distance));
                Check.Near("middle distance", 4, distance);
                Check.Near("before the start", 0, PipeSeg.Project(a, b, 0, new Vec2(-3, 4), out distance));
                Check.Near("before the start distance", 5, distance);
                Check.Near("past the end", 1, PipeSeg.Project(a, b, 0, new Vec2(13, -4), out distance));
                Check.Near("past the end distance", 5, distance);
                Check.Near("a zero-length segment", 0, PipeSeg.Project(a, a, 0, new Vec2(3, 4), out distance));
                Check.Near("a zero-length distance", 5, distance);
            });
        }
    }
}
