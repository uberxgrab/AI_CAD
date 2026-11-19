using System;
using System.Collections.Generic;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Pipes
{
    // AutoCAD-free pipe network maths shared by HVACPIPESIZE and HVACREFRIG: curves as plain
    // vertex lists, a welded graph with T nodes, run finding and the cut plan for splitting curves.
    // Vec2, OutlineVertex and Outline come from DuctGeometryCore.cs. All lengths are drawing units
    // (mm). Connectivity uses endpoint distances with an explicit tolerance, never IntersectWith
    // (CLAUDE.md, "AutoCAD API facts verified on the template").

    /// <summary>A LINE or LWPOLYLINE in plan: vertices with bulges (positive is counter-clockwise).</summary>
    public sealed class PipeCurve
    {
        /// <summary>Caller's identifier, echoed in results (an index into the caller's own list).</summary>
        public int Id;
        public string Layer = "";
        public bool Closed;
        public double Elevation;
        public readonly List<OutlineVertex> Vertices = new List<OutlineVertex>();

        public int SegmentCount
        {
            get
            {
                int count = Vertices.Count;
                if (count < 2)
                    return 0;
                return Closed ? count : count - 1;
            }
        }

        public Vec2 SegmentStart(int segment)
        {
            OutlineVertex v = Vertices[segment];
            return new Vec2(v.X, v.Y);
        }

        public Vec2 SegmentEnd(int segment)
        {
            OutlineVertex v = Vertices[(segment + 1) % Vertices.Count];
            return new Vec2(v.X, v.Y);
        }

        public double SegmentBulge(int segment)
        {
            return Vertices[segment].Bulge;
        }

        public bool HasArcs
        {
            get
            {
                for (int i = 0; i < SegmentCount; i++)
                {
                    if (Math.Abs(Vertices[i].Bulge) > 1e-9)
                        return true;
                }

                return false;
            }
        }

        public static PipeCurve FromPoints(int id, string layer, bool closed, params Vec2[] points)
        {
            PipeCurve curve = new PipeCurve { Id = id, Layer = layer, Closed = closed };
            foreach (Vec2 point in points)
                curve.Vertices.Add(new OutlineVertex(point.X, point.Y, 0));
            return curve;
        }
    }

    /// <summary>Line and arc segment maths. An arc is parameterised by its sweep angle (0 to 1).</summary>
    public static class PipeSeg
    {
        private const double StraightBulge = 1e-9;

        public static bool IsStraight(double bulge)
        {
            return Math.Abs(bulge) < StraightBulge;
        }

        public static double Length(Vec2 a, Vec2 b, double bulge)
        {
            double chord = a.DistanceTo(b);
            if (IsStraight(bulge))
                return chord;
            double sweep = Math.Abs(4 * Math.Atan(bulge));
            return chord / (2 * Math.Sin(sweep / 2)) * sweep;
        }

        private static void Arc(
            Vec2 a, Vec2 b, double bulge,
            out Vec2 centre, out double radius, out double startAngle, out double sweep)
        {
            double chord = a.DistanceTo(b);
            sweep = 4 * Math.Atan(bulge);
            double h = chord / 2 / Math.Tan(sweep / 2);
            Vec2 mid = (a + b) / 2;
            Vec2 direction = (b - a) / chord;
            centre = mid + direction.Left * h;
            radius = Math.Sqrt(chord * chord / 4 + h * h);
            startAngle = (a - centre).Angle;
        }

        /// <summary>The point at fraction f of the segment (0 is a, 1 is b).</summary>
        public static Vec2 PointAt(Vec2 a, Vec2 b, double bulge, double f)
        {
            if (f <= 0)
                return a;
            if (f >= 1)
                return b;
            if (IsStraight(bulge) || a.DistanceTo(b) < 1e-12)
                return a + (b - a) * f;

            Vec2 centre;
            double radius;
            double startAngle;
            double sweep;
            Arc(a, b, bulge, out centre, out radius, out startAngle, out sweep);
            return centre + Vec2.FromAngle(startAngle + f * sweep) * radius;
        }

        /// <summary>
        /// The fraction of the segment point p is closest to, and its distance to the segment. An
        /// arc's fraction is the share of its sweep; a point outside the sweep maps to the nearer end.
        /// </summary>
        public static double Project(Vec2 a, Vec2 b, double bulge, Vec2 p, out double distance)
        {
            double chord = a.DistanceTo(b);
            if (IsStraight(bulge) || chord < 1e-12)
            {
                Vec2 ab = b - a;
                double length2 = ab.Dot(ab);
                if (length2 < 1e-24)
                {
                    distance = a.DistanceTo(p);
                    return 0;
                }

                double t = (p - a).Dot(ab) / length2;
                t = Math.Max(0, Math.Min(1, t));
                distance = (a + ab * t).DistanceTo(p);
                return t;
            }

            Vec2 centre;
            double radius;
            double startAngle;
            double sweep;
            Arc(a, b, bulge, out centre, out radius, out startAngle, out sweep);

            Vec2 toPoint = p - centre;
            double toPointLength = toPoint.Length;
            double total = Math.Abs(sweep);
            double sign = sweep < 0 ? -1 : 1;
            double relative = (toPoint.Angle - startAngle) * sign;
            double twoPi = 2 * Math.PI;
            relative = relative % twoPi;
            if (relative < 0)
                relative += twoPi;

            if (toPointLength > 1e-12 && relative <= total)
            {
                distance = Math.Abs(toPointLength - radius);
                return relative / total;
            }

            double toA = a.DistanceTo(p);
            double toB = b.DistanceTo(p);
            if (toA <= toB)
            {
                distance = toA;
                return 0;
            }

            distance = toB;
            return 1;
        }

        /// <summary>The bulge of the part of an arc between two fractions (0 for a line).</summary>
        public static double SubBulge(double bulge, double f0, double f1)
        {
            if (IsStraight(bulge))
                return 0;
            double sweep = 4 * Math.Atan(bulge);
            return Math.Tan((f1 - f0) * sweep / 4);
        }
    }

    public sealed class NetworkNode
    {
        public int Index;
        /// <summary>Mean of the welded endpoints.</summary>
        public Vec2 Point;
        /// <summary>Each incident edge once per end.</summary>
        public readonly List<int> Edges = new List<int>();
        /// <summary>True when the node splits another segment (a T node).</summary>
        public bool IsTNode;
        /// <summary>For a T node: the node's projection onto the first segment it splits.</summary>
        public Vec2 HostPoint;

        public int Degree
        {
            get { return Edges.Count; }
        }
    }

    public sealed class NetworkEdge
    {
        public int Index;
        /// <summary>Index into <see cref="PipeNetwork.Curves"/>.</summary>
        public int Curve;
        public int Segment;
        /// <summary>The part of the segment the edge covers, as fractions from 0 to 1.</summary>
        public double F0;
        public double F1;
        public int NodeA;
        public int NodeB;
        public string Layer;
        public double Length;
        public double SubBulge;
        /// <summary>The exact points on the source segment at F0 and F1.</summary>
        public Vec2 PointA;
        public Vec2 PointB;

        public int OtherNode(int node)
        {
            return node == NodeA ? NodeB : NodeA;
        }
    }

    // A grid cell. A struct of two longs with its own Equals and hash, because KeyValuePair hashes
    // only its first field, which makes a tall grid collide.
    internal struct CellKey : IEquatable<CellKey>
    {
        public readonly long X;
        public readonly long Y;

        public CellKey(long x, long y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(CellKey other)
        {
            return X == other.X && Y == other.Y;
        }

        public override bool Equals(object obj)
        {
            return obj is CellKey && Equals((CellKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (int)(X * 73856093L ^ Y * 19349663L);
            }
        }
    }

    /// <summary>
    /// Every segment of every curve as a graph. Endpoints within the weld tolerance are one node.
    /// A node that touches the interior of another segment splits it there (a T node). Interior
    /// crossings are not nodes, because pipes cross at different levels.
    /// </summary>
    public sealed class PipeNetwork
    {
        public IList<PipeCurve> Curves;
        public readonly List<NetworkNode> Nodes = new List<NetworkNode>();
        public readonly List<NetworkEdge> Edges = new List<NetworkEdge>();
        public double WeldTolerance;

        // Segments whose two ends welded into one node (shorter than the tolerance), as curve * 2^32 + segment.
        private readonly HashSet<long> _degenerate = new HashSet<long>();

        public bool IsDegenerate(int curve, int segment)
        {
            return _degenerate.Contains(Key(curve, segment));
        }

        private static long Key(int curve, int segment)
        {
            return ((long)curve << 32) | (uint)segment;
        }

        private sealed class Seg
        {
            public int Curve;
            public int Index;
            public Vec2 A;
            public Vec2 B;
            public double Bulge;
            public int NodeA;
            public int NodeB;
            public readonly List<KeyValuePair<double, int>> Splits = new List<KeyValuePair<double, int>>();
        }

        public static PipeNetwork Build(IList<PipeCurve> curves, double weldTolerance)
        {
            PipeNetwork net = new PipeNetwork { Curves = curves, WeldTolerance = weldTolerance };
            double weld = Math.Max(weldTolerance, 1e-6);

            List<Seg> segments = new List<Seg>();
            for (int c = 0; c < curves.Count; c++)
            {
                PipeCurve curve = curves[c];
                for (int s = 0; s < curve.SegmentCount; s++)
                {
                    segments.Add(new Seg
                    {
                        Curve = c,
                        Index = s,
                        A = curve.SegmentStart(s),
                        B = curve.SegmentEnd(s),
                        Bulge = curve.SegmentBulge(s)
                    });
                }
            }

            // 1. Weld endpoints (union-find over a grid).
            int count = segments.Count * 2;
            Vec2[] points = new Vec2[count];
            for (int i = 0; i < segments.Count; i++)
            {
                points[2 * i] = segments[i].A;
                points[2 * i + 1] = segments[i].B;
            }

            int[] parent = new int[count];
            for (int i = 0; i < count; i++)
                parent[i] = i;

            Dictionary<CellKey, List<int>> grid =
                new Dictionary<CellKey, List<int>>();
            for (int i = 0; i < count; i++)
            {
                long ix = (long)Math.Floor(points[i].X / weld);
                long iy = (long)Math.Floor(points[i].Y / weld);
                for (long dx = -1; dx <= 1; dx++)
                {
                    for (long dy = -1; dy <= 1; dy++)
                    {
                        List<int> bucket;
                        if (!grid.TryGetValue(new CellKey(ix + dx, iy + dy), out bucket))
                            continue;
                        foreach (int j in bucket)
                        {
                            if (points[i].DistanceTo(points[j]) <= weldTolerance)
                                Union(parent, i, j);
                        }
                    }
                }

                CellKey own = new CellKey(ix, iy);
                List<int> list;
                if (!grid.TryGetValue(own, out list))
                {
                    list = new List<int>();
                    grid[own] = list;
                }

                list.Add(i);
            }

            Dictionary<int, int> nodeOfRoot = new Dictionary<int, int>();
            List<Vec2> sums = new List<Vec2>();
            List<int> counts = new List<int>();
            int[] nodeOfPoint = new int[count];
            for (int i = 0; i < count; i++)
            {
                int root = Find(parent, i);
                int node;
                if (!nodeOfRoot.TryGetValue(root, out node))
                {
                    node = sums.Count;
                    nodeOfRoot[root] = node;
                    sums.Add(new Vec2(0, 0));
                    counts.Add(0);
                }

                sums[node] = sums[node] + points[i];
                counts[node]++;
                nodeOfPoint[i] = node;
            }

            for (int n = 0; n < sums.Count; n++)
            {
                net.Nodes.Add(new NetworkNode { Index = n, Point = sums[n] / counts[n] });
            }

            for (int i = 0; i < segments.Count; i++)
            {
                segments[i].NodeA = nodeOfPoint[2 * i];
                segments[i].NodeB = nodeOfPoint[2 * i + 1];
            }

            // 2. T nodes: a node within the tolerance of another segment's interior splits it.
            net.FindTouches(segments, weldTolerance);

            // 3. Edges.
            foreach (Seg seg in segments)
                net.AddEdges(seg);

            return net;
        }

        private void FindTouches(List<Seg> segments, double weld)
        {
            if (Nodes.Count == 0)
                return;

            double cell = Math.Max(weld * 4, 100.0);
            Dictionary<CellKey, List<int>> grid =
                new Dictionary<CellKey, List<int>>();
            foreach (NetworkNode node in Nodes)
            {
                CellKey key = new CellKey(
                    (long)Math.Floor(node.Point.X / cell), (long)Math.Floor(node.Point.Y / cell));
                List<int> list;
                if (!grid.TryGetValue(key, out list))
                {
                    list = new List<int>();
                    grid[key] = list;
                }

                list.Add(node.Index);
            }

            List<int> candidates = new List<int>();
            foreach (Seg seg in segments)
            {
                candidates.Clear();
                if (!BoundingBox(seg, weld, out double minX, out double minY, out double maxX, out double maxY))
                    continue;

                long x0 = (long)Math.Floor(minX / cell);
                long x1 = (long)Math.Floor(maxX / cell);
                long y0 = (long)Math.Floor(minY / cell);
                long y1 = (long)Math.Floor(maxY / cell);
                double cells = ((double)(x1 - x0) + 1) * ((double)(y1 - y0) + 1);
                if (cells > 4000)
                {
                    // A very long diagonal: scan every node instead of every cell.
                    for (int n = 0; n < Nodes.Count; n++)
                        candidates.Add(n);
                }
                else
                {
                    for (long x = x0; x <= x1; x++)
                    {
                        for (long y = y0; y <= y1; y++)
                        {
                            List<int> bucket;
                            if (grid.TryGetValue(new CellKey(x, y), out bucket))
                                candidates.AddRange(bucket);
                        }
                    }
                }

                foreach (int n in candidates)
                {
                    if (n == seg.NodeA || n == seg.NodeB)
                        continue;

                    NetworkNode node = Nodes[n];
                    if (node.Point.DistanceTo(Nodes[seg.NodeA].Point) <= weld
                        || node.Point.DistanceTo(Nodes[seg.NodeB].Point) <= weld)
                        continue;

                    double distance;
                    double f = PipeSeg.Project(seg.A, seg.B, seg.Bulge, node.Point, out distance);
                    if (distance > weld || f <= 1e-9 || f >= 1 - 1e-9)
                        continue;

                    seg.Splits.Add(new KeyValuePair<double, int>(f, n));
                    if (!node.IsTNode)
                    {
                        node.IsTNode = true;
                        node.HostPoint = PipeSeg.PointAt(seg.A, seg.B, seg.Bulge, f);
                    }
                }
            }
        }

        private static bool BoundingBox(
            Seg seg, double inflate, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = Math.Min(seg.A.X, seg.B.X) - inflate;
            maxX = Math.Max(seg.A.X, seg.B.X) + inflate;
            minY = Math.Min(seg.A.Y, seg.B.Y) - inflate;
            maxY = Math.Max(seg.A.Y, seg.B.Y) + inflate;
            if (!PipeSeg.IsStraight(seg.Bulge))
            {
                // An arc can bulge outside its chord box by up to its sagitta.
                double chord = seg.A.DistanceTo(seg.B);
                double sagitta = Math.Abs(seg.Bulge) * chord / 2;
                minX -= sagitta;
                maxX += sagitta;
                minY -= sagitta;
                maxY += sagitta;
            }

            return !(double.IsNaN(minX) || double.IsNaN(minY) || double.IsNaN(maxX) || double.IsNaN(maxY));
        }

        private void AddEdges(Seg seg)
        {
            seg.Splits.Sort((p, q) =>
            {
                int byF = p.Key.CompareTo(q.Key);
                return byF != 0 ? byF : p.Value.CompareTo(q.Value);
            });

            List<KeyValuePair<double, int>> chain = new List<KeyValuePair<double, int>>();
            chain.Add(new KeyValuePair<double, int>(0, seg.NodeA));
            chain.AddRange(seg.Splits);
            chain.Add(new KeyValuePair<double, int>(1, seg.NodeB));

            string layer = Curves[seg.Curve].Layer;
            double full = PipeSeg.Length(seg.A, seg.B, seg.Bulge);
            for (int k = 0; k + 1 < chain.Count; k++)
            {
                int a = chain[k].Value;
                int b = chain[k + 1].Value;
                if (a == b)
                {
                    _degenerate.Add(Key(seg.Curve, seg.Index));
                    continue;
                }

                double f0 = chain[k].Key;
                double f1 = chain[k + 1].Key;
                NetworkEdge edge = new NetworkEdge
                {
                    Index = Edges.Count,
                    Curve = seg.Curve,
                    Segment = seg.Index,
                    F0 = f0,
                    F1 = f1,
                    NodeA = a,
                    NodeB = b,
                    Layer = layer,
                    Length = full * (f1 - f0),
                    SubBulge = PipeSeg.SubBulge(seg.Bulge, f0, f1),
                    PointA = PipeSeg.PointAt(seg.A, seg.B, seg.Bulge, f0),
                    PointB = PipeSeg.PointAt(seg.A, seg.B, seg.Bulge, f1)
                };
                Edges.Add(edge);
                Nodes[a].Edges.Add(edge.Index);
                Nodes[b].Edges.Add(edge.Index);
            }
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a);
            int rb = Find(parent, b);
            if (ra != rb)
                parent[rb] = ra;
        }
    }

    /// <summary>The edges of one run, and the nodes it stopped at.</summary>
    public sealed class PipeRun
    {
        public readonly List<int> Edges = new List<int>();
        public readonly List<int> EndNodes = new List<int>();
        public string Layer;
        public double Length;
    }

    /// <summary>
    /// Where a curve is cut to separate the run from the rest, and which pieces are the run.
    /// Positions on a curve are keys: segment index plus the fraction along that segment.
    /// </summary>
    public sealed class CurveSplitPlan
    {
        public int CurveIndex;
        public PipeCurve Curve;
        /// <summary>Every segment is in the run: no cut, just move the whole curve.</summary>
        public bool WholeCurve;
        public readonly List<double> CutKeys = new List<double>();
        public readonly List<Vec2> CutPoints = new List<Vec2>();
        internal readonly List<double[]> Intervals = new List<double[]>();

        /// <summary>True when the position is inside the run (a position inside a cut piece, not on a cut).</summary>
        public bool KeyInRun(double key)
        {
            if (WholeCurve)
                return true;

            int segments = Curve.SegmentCount;
            foreach (double[] interval in Intervals)
            {
                if (key >= interval[0] - 1e-9 && key <= interval[1] + 1e-9)
                    return true;
                if (Curve.Closed && key + segments >= interval[0] - 1e-9 && key + segments <= interval[1] + 1e-9)
                    return true;
            }

            return false;
        }

        /// <summary>True when the point, on this curve, lies in the run.</summary>
        public bool IsRunAt(Vec2 point)
        {
            if (WholeCurve)
                return true;

            double bestDistance = double.MaxValue;
            double bestKey = 0;
            for (int s = 0; s < Curve.SegmentCount; s++)
            {
                double distance;
                double f = PipeSeg.Project(
                    Curve.SegmentStart(s), Curve.SegmentEnd(s), Curve.SegmentBulge(s), point, out distance);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestKey = s + f;
                }
            }

            return KeyInRun(bestKey);
        }
    }

    public static class PipeRunFinder
    {
        /// <summary>The edge of the given curve nearest to a point; -1 when the curve has none.</summary>
        public static int FindEdge(PipeNetwork net, int curveIndex, Vec2 point)
        {
            int best = -1;
            double bestDistance = double.MaxValue;
            PipeCurve curve = net.Curves[curveIndex];
            foreach (NetworkEdge edge in net.Edges)
            {
                if (edge.Curve != curveIndex)
                    continue;

                double distance;
                double f = PipeSeg.Project(
                    curve.SegmentStart(edge.Segment),
                    curve.SegmentEnd(edge.Segment),
                    curve.SegmentBulge(edge.Segment),
                    point,
                    out distance);
                if (f < edge.F0 - 1e-12 || f > edge.F1 + 1e-12)
                    distance = Math.Min(point.DistanceTo(edge.PointA), point.DistanceTo(edge.PointB));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = edge.Index;
                }
            }

            return best;
        }

        /// <summary>
        /// Walks both ways from an edge through degree-2 nodes (corners). It stops at a degree-1 node, at
        /// a node of degree 3 or more, and where the next edge is on a different layer. Valves and other
        /// blocks are not nodes, so a run passes through them.
        /// </summary>
        public static PipeRun Find(PipeNetwork net, int startEdge)
        {
            NetworkEdge first = net.Edges[startEdge];
            PipeRun run = new PipeRun { Layer = first.Layer };
            HashSet<int> seen = new HashSet<int> { startEdge };
            run.Edges.Add(startEdge);

            Extend(net, run, seen, first.NodeA, startEdge);
            Extend(net, run, seen, first.NodeB, startEdge);

            foreach (int index in run.Edges)
                run.Length += net.Edges[index].Length;
            return run;
        }

        private static void Extend(PipeNetwork net, PipeRun run, HashSet<int> seen, int node, int fromEdge)
        {
            while (true)
            {
                NetworkNode current = net.Nodes[node];
                if (current.Degree != 2)
                {
                    run.EndNodes.Add(node);
                    return;
                }

                int other = current.Edges[0] == fromEdge ? current.Edges[1] : current.Edges[0];
                if (seen.Contains(other))
                    return;

                NetworkEdge next = net.Edges[other];
                if (!string.Equals(next.Layer, run.Layer, StringComparison.OrdinalIgnoreCase))
                {
                    run.EndNodes.Add(node);
                    return;
                }

                seen.Add(other);
                run.Edges.Add(other);
                fromEdge = other;
                node = next.OtherNode(node);
            }
        }

        /// <summary>The run as open outlines, one per edge, for a transient highlight.</summary>
        public static List<Outline> Outlines(PipeNetwork net, PipeRun run)
        {
            List<Outline> outlines = new List<Outline>();
            foreach (int index in run.Edges)
            {
                NetworkEdge edge = net.Edges[index];
                Outline outline = new Outline { Closed = false };
                outline.Add(edge.PointA, edge.SubBulge);
                outline.Add(edge.PointB, 0);
                outlines.Add(outline);
            }

            return outlines;
        }

        /// <summary>
        /// For each curve the run touches: where to cut it, so that the run's pieces and the rest come
        /// apart. A curve that lies entirely in the run needs no cut.
        /// </summary>
        public static List<CurveSplitPlan> PlanSplits(PipeNetwork net, PipeRun run)
        {
            // key interval start, end, and whether it is a real run edge (not a bridged tiny segment)
            SortedDictionary<int, List<double[]>> byCurve = new SortedDictionary<int, List<double[]>>();
            foreach (int index in run.Edges)
            {
                NetworkEdge edge = net.Edges[index];
                List<double[]> list;
                if (!byCurve.TryGetValue(edge.Curve, out list))
                {
                    list = new List<double[]>();
                    byCurve[edge.Curve] = list;
                }

                list.Add(new[] { edge.Segment + edge.F0, edge.Segment + edge.F1, 1 });
            }

            List<CurveSplitPlan> plans = new List<CurveSplitPlan>();
            foreach (KeyValuePair<int, List<double[]>> pair in byCurve)
            {
                PipeCurve curve = net.Curves[pair.Key];
                for (int s = 0; s < curve.SegmentCount; s++)
                {
                    // A segment whose ends welded together is not an edge; let it join a neighbouring run piece.
                    if (net.IsDegenerate(pair.Key, s))
                        pair.Value.Add(new[] { (double)s, s + 1.0, 0 });
                }

                plans.Add(BuildPlan(pair.Key, curve, pair.Value));
            }

            return plans;
        }

        private static CurveSplitPlan BuildPlan(int curveIndex, PipeCurve curve, List<double[]> raw)
        {
            const double eps = 1e-9;
            int segments = curve.SegmentCount;
            raw.Sort((p, q) => p[0].CompareTo(q[0]));

            List<double[]> merged = new List<double[]>();
            double[] current = null;
            foreach (double[] interval in raw)
            {
                if (current == null)
                {
                    current = (double[])interval.Clone();
                }
                else if (interval[0] <= current[1] + eps)
                {
                    current[1] = Math.Max(current[1], interval[1]);
                    current[2] = Math.Max(current[2], interval[2]);
                }
                else
                {
                    if (current[2] > 0)
                        merged.Add(current);
                    current = (double[])interval.Clone();
                }
            }

            if (current != null && current[2] > 0)
                merged.Add(current);

            if (curve.Closed && merged.Count >= 2
                && merged[0][0] <= eps && merged[merged.Count - 1][1] >= segments - eps)
            {
                double[] last = merged[merged.Count - 1];
                last[1] = merged[0][1] + segments;
                merged.RemoveAt(0);
            }

            CurveSplitPlan plan = new CurveSplitPlan { CurveIndex = curveIndex, Curve = curve };
            plan.Intervals.AddRange(merged);

            bool whole = merged.Count == 1
                && (curve.Closed
                    ? merged[0][1] - merged[0][0] >= segments - eps
                    : merged[0][0] <= eps && merged[0][1] >= segments - eps);
            plan.WholeCurve = whole;
            if (whole)
                return plan;

            List<double> keys = new List<double>();
            foreach (double[] interval in merged)
            {
                if (curve.Closed)
                {
                    keys.Add(Wrap(interval[0], segments));
                    keys.Add(Wrap(interval[1], segments));
                }
                else
                {
                    if (interval[0] > eps)
                        keys.Add(interval[0]);
                    if (interval[1] < segments - eps)
                        keys.Add(interval[1]);
                }
            }

            keys.Sort();
            foreach (double key in keys)
            {
                if (plan.CutKeys.Count > 0 && key - plan.CutKeys[plan.CutKeys.Count - 1] <= eps)
                    continue;
                plan.CutKeys.Add(key);
                plan.CutPoints.Add(PointAtKey(curve, key));
            }

            return plan;
        }

        private static double Wrap(double key, int segments)
        {
            while (key >= segments - 1e-9)
                key -= segments;
            return key < 0 ? 0 : key;
        }

        /// <summary>The point at a key: the segment index plus the fraction along that segment.</summary>
        public static Vec2 PointAtKey(PipeCurve curve, double key)
        {
            int segments = curve.SegmentCount;
            int segment = (int)Math.Floor(key + 1e-12);
            if (segment >= segments)
                segment = segments - 1;
            if (segment < 0)
                segment = 0;
            double f = key - segment;
            return PipeSeg.PointAt(
                curve.SegmentStart(segment), curve.SegmentEnd(segment), curve.SegmentBulge(segment), f);
        }
    }
}
