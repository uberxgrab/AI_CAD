using System;
using System.Collections.Generic;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Pipes;

namespace PipeCoreTests
{
    /// <summary>Builders and lookups shared by the pipe tests.</summary>
    internal static class Support
    {
        public const string Layer = "EIA_1 CHWS DN40";
        public const string Rp = "EIA_1 RP 22.2-9.5 (28)";

        public static Vec2 V(double x, double y)
        {
            return new Vec2(x, y);
        }

        public static PipeCurve Line(int id, string layer, double x1, double y1, double x2, double y2)
        {
            return PipeCurve.FromPoints(id, layer, false, new Vec2(x1, y1), new Vec2(x2, y2));
        }

        /// <summary>A polyline from x, y pairs.</summary>
        public static PipeCurve Poly(int id, string layer, bool closed, params double[] xy)
        {
            PipeCurve curve = new PipeCurve { Id = id, Layer = layer, Closed = closed };
            for (int i = 0; i + 1 < xy.Length; i += 2)
                curve.Vertices.Add(new OutlineVertex(xy[i], xy[i + 1], 0));
            return curve;
        }

        /// <summary>A polyline from x, y, bulge triples.</summary>
        public static PipeCurve PolyBulge(int id, string layer, bool closed, params double[] xyb)
        {
            PipeCurve curve = new PipeCurve { Id = id, Layer = layer, Closed = closed };
            for (int i = 0; i + 2 < xyb.Length; i += 3)
                curve.Vertices.Add(new OutlineVertex(xyb[i], xyb[i + 1], xyb[i + 2]));
            return curve;
        }

        public static bool Near(Vec2 a, Vec2 b, double tolerance = 1e-6)
        {
            return a.DistanceTo(b) <= tolerance;
        }

        /// <summary>True when some path runs between exactly these two points, in either direction.</summary>
        public static bool HasPath(RefrigResult result, Vec2 a, Vec2 b, double tolerance = 1e-6)
        {
            foreach (RefrigPath path in result.Paths)
            {
                if (path.Points.Count != 2)
                    continue;
                Vec2 first = path.Points[0];
                Vec2 last = path.Points[1];
                if ((Near(first, a, tolerance) && Near(last, b, tolerance))
                    || (Near(first, b, tolerance) && Near(last, a, tolerance)))
                    return true;
            }

            return false;
        }

        /// <summary>True when some path runs through exactly these points, in either direction.</summary>
        public static bool HasRoute(RefrigResult result, params Vec2[] route)
        {
            foreach (RefrigPath path in result.Paths)
            {
                if (path.Points.Count != route.Length)
                    continue;
                bool forward = true;
                bool backward = true;
                for (int i = 0; i < route.Length; i++)
                {
                    if (!Near(path.Points[i], route[i]))
                        forward = false;
                    if (!Near(path.Points[route.Length - 1 - i], route[i]))
                        backward = false;
                }

                if (forward || backward)
                    return true;
            }

            return false;
        }

        /// <summary>The layer of the path that runs through exactly these points, or null.</summary>
        public static string RouteLayer(RefrigResult result, params Vec2[] route)
        {
            foreach (RefrigPath path in result.Paths)
            {
                if (path.Points.Count != route.Length)
                    continue;
                bool forward = true;
                bool backward = true;
                for (int i = 0; i < route.Length; i++)
                {
                    if (!Near(path.Points[i], route[i]))
                        forward = false;
                    if (!Near(path.Points[route.Length - 1 - i], route[i]))
                        backward = false;
                }

                if (forward || backward)
                    return path.Layer;
            }

            return null;
        }

        /// <summary>The measured checks of one rule ("R1", "R2" or "R3"), in the order they were made.</summary>
        public static List<RuleCheck> ChecksOf(RefrigResult result, string rule)
        {
            List<RuleCheck> found = new List<RuleCheck>();
            foreach (RuleCheck check in result.RuleChecks)
            {
                if (check.Rule == rule)
                    found.Add(check);
            }

            return found;
        }

        /// <summary>True when a check of the rule measured this value (mm).</summary>
        public static bool HasMeasure(RefrigResult result, string rule, double measured, double tolerance = 1e-6)
        {
            foreach (RuleCheck check in ChecksOf(result, rule))
            {
                if (Math.Abs(check.Measured - measured) <= tolerance)
                    return true;
            }

            return false;
        }

        /// <summary>How many warnings contain the text.</summary>
        public static int WarningsWith(RefrigResult result, string fragment)
        {
            int count = 0;
            foreach (string warning in result.Warnings)
            {
                if (warning.IndexOf(fragment, StringComparison.Ordinal) >= 0)
                    count++;
            }

            return count;
        }

        /// <summary>True when some path passes through the point (any vertex).</summary>
        public static bool AnyPathHasPoint(RefrigResult result, Vec2 point, double tolerance = 1e-6)
        {
            foreach (RefrigPath path in result.Paths)
            {
                foreach (Vec2 p in path.Points)
                {
                    if (Near(p, point, tolerance))
                        return true;
                }
            }

            return false;
        }

        public static RefrigResult Clean(IList<PipeCurve> curves, Action<RefrigInput> tweak = null)
        {
            RefrigInput input = new RefrigInput { Curves = curves };
            if (tweak != null)
                tweak(input);
            return RefrigCleaner.Run(input);
        }

        public static RefrigResult CleanWithRoot(IList<PipeCurve> curves, Vec2 root, Action<RefrigInput> tweak = null)
        {
            return Clean(curves, input =>
            {
                input.RootPoint = root;
                if (tweak != null)
                    tweak(input);
            });
        }

        public static Vec2 Rotate(Vec2 point, double radians)
        {
            double c = Math.Cos(radians);
            double s = Math.Sin(radians);
            return new Vec2(point.X * c - point.Y * s, point.X * s + point.Y * c);
        }

        /// <summary>Where a point of the refnet block's local geometry lands in the drawing.</summary>
        public static Vec2 MapLocal(RefnetPlacement refnet, double x, double y)
        {
            Vec2 scaled = new Vec2(x * refnet.XScale, y * refnet.Scale);
            return refnet.Inlet + Rotate(scaled, refnet.Rotation);
        }
    }
}
