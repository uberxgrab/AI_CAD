using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HvacAiCadAddin.Ducts
{
    // AutoCAD-free core of HVACDUCTLINE: turns a single-line duct network (centrelines, as other offices
    // draw them) into duct runs that RouteSolver turns into straights, elbows, transitions and taps.
    //
    // 1. Each curve becomes straight segments: an arc between two straight legs is a bend (the corner,
    //    where the legs meet, replaces it) unless the legs are collinear, when it is a hop over another
    //    duct (its chord replaces it); arcs at a polyline end, lone arcs and splines become their chord.
    // 2. Ends closer than the weld tolerance are one node; an end on another segment's interior splits it
    //    (a tee). Only segments of the same group (system, or layer when untagged) are joined.
    // 3. The tree is grown from the node nearest the picked root (the fan / AHU end). A loop edge is left
    //    out and reported; segments not reached are left alone and reported.
    // 4. Runs: from the root, a run goes straight through tees (the collinear child), turns when the
    //    node has one child, and at a node with two children and no straight one (a bullhead) follows
    //    the larger. Every other child starts a branch run: on a tee at right angles (within 15 degrees)
    //    it starts with a tap on the side wall of the incoming duct, otherwise it starts free (reported).
    // 5. Each run is solved with RouteSolver; a run that does not fit (segments too short for its
    //    fittings) is reported and not drawn.

    public sealed class DuctLineCurve
    {
        public int Id;
        /// <summary>Vertices with the bulge of the segment that follows each (AutoCAD convention).</summary>
        public List<OutlineVertex> Vertices = new List<OutlineVertex>();
        /// <summary>True for a spline (only its ends are used).</summary>
        public bool IsSpline;
        /// <summary>The size from the HVACNORM tag; null when untagged.</summary>
        public DuctSize Size;
        public string System = "";
        public string Layer = "";
        public double Elevation;

        /// <summary>The joining group: the tagged system, else the layer.</summary>
        public string Group
        {
            get { return string.IsNullOrEmpty(System) ? "layer:" + Layer : System.ToUpperInvariant(); }
        }
    }

    public sealed class DuctLineSettings
    {
        public double WeldTolerance = 100;
        /// <summary>Size of untagged curves.</summary>
        public DuctSize DefaultSize = DuctSize.Rect(300, 300);
        public FittingSettings Fittings = new FittingSettings();
        /// <summary>A branch within this many degrees of a right angle gets a tap.</summary>
        public double TapAngleDegrees = 15;
        public double StraightAngleDegrees = 10;
        /// <summary>The root must be a node this close to the picked point (mm).</summary>
        public double RootTolerance = 2000;
    }

    public sealed class DuctLineRun
    {
        public readonly List<Vec2> Points = new List<Vec2>();
        public readonly List<DuctSize> Sizes = new List<DuctSize>();
        public readonly HashSet<int> Curves = new HashSet<int>();
        public string Group;
        public double Elevation;
        /// <summary>True when the run starts with a tap on the main's side wall.</summary>
        public bool StartsWithTap;
        public Vec2 TapWallPoint;
        /// <summary>The main's direction (away from the root) at the tap.</summary>
        public Vec2 TapMainAxis;
        public Vec2 TapOutward;
        public RouteResult Result;
        public string Error;

        public bool Ok
        {
            get { return Result != null && Result.Ok; }
        }
    }

    public sealed class DuctLinePlan
    {
        public bool Ok;
        public string Error;
        public Vec2 Root;
        public readonly List<DuctLineRun> Runs = new List<DuctLineRun>();
        public readonly List<string> Reports = new List<string>();
        public readonly List<Vec2> ReportPoints = new List<Vec2>();
        /// <summary>Curves that are part of the drawn network (their runs all solved).</summary>
        public readonly HashSet<int> DoneCurves = new HashSet<int>();
        public int Segments;
        public int Tees;
        public int UntaggedCurves;
    }

    public static class DuctLinePlanner
    {
        private sealed class Edge
        {
            public int A;
            public int B;
            public DuctSize Size;
            public int Curve;
            public string Group;
            public double Elevation;
            public bool Removed;

            public int Other(int node)
            {
                return node == A ? B : A;
            }
        }

        private sealed class Seg
        {
            public Vec2 P;
            public Vec2 Q;
            public DuctLineCurve Curve;
        }

        // ------------------------------------------------------------------------------ step 1

        /// <summary>The straight centreline of a curve: bends become corners, hops and other arcs chords.</summary>
        public static List<Vec2> Straighten(IList<OutlineVertex> vertices, bool isSpline, out int bends, out int hops)
        {
            bends = 0;
            hops = 0;
            List<Vec2> points = vertices.Select(v => new Vec2(v.X, v.Y)).ToList();
            if (isSpline && points.Count >= 2)
                return new List<Vec2> { points[0], points[points.Count - 1] };
            int n = points.Count;
            if (n < 2)
                return points;

            bool[] arc = new bool[n - 1];
            for (int i = 0; i < n - 1; i++)
                arc[i] = Math.Abs(vertices[i].Bulge) > 1e-9;

            // Point i may be replaced (by a corner); points are kept unless an arc between two straight
            // legs that turn is folded into their intersection.
            Vec2?[] replace = new Vec2?[n];
            bool[] drop = new bool[n];
            for (int i = 0; i < n - 1; i++)
            {
                if (!arc[i])
                    continue;
                bool hasIn = i >= 1 && !arc[i - 1];
                bool hasOut = i + 2 <= n - 1 && !arc[i + 1];
                if (!hasIn || !hasOut)
                    continue; // chord
                Vec2 inDir = (points[i] - points[i - 1]).Normalized();
                Vec2 outDir = (points[i + 2] - points[i + 1]).Normalized();
                double cross = inDir.Cross(outDir);
                if (Math.Abs(cross) < Math.Sin(5 * Math.PI / 180))
                {
                    hops++;
                    continue; // collinear legs: a hop, the chord continues the line
                }

                // Corner: intersection of the incoming and outgoing legs.
                double t = (points[i + 1] - points[i]).Cross(outDir) / cross;
                Vec2 corner = points[i] + inDir * t;
                if (t < -1e-6 || corner.DistanceTo(points[i]) > 20 * Math.Max(1.0, points[i].DistanceTo(points[i + 1])))
                    continue;
                replace[i] = corner;
                drop[i + 1] = true;
                bends++;
            }

            List<Vec2> result = new List<Vec2>();
            for (int i = 0; i < n; i++)
            {
                if (drop[i])
                    continue;
                Vec2 p = replace[i] ?? points[i];
                if (result.Count == 0 || result[result.Count - 1].DistanceTo(p) > 1e-6)
                    result.Add(p);
            }

            return result;
        }

        // ------------------------------------------------------------------------------ steps 2-5

        public static DuctLinePlan Plan(IList<DuctLineCurve> curves, Vec2 rootPick, DuctLineSettings settings)
        {
            DuctLinePlan plan = new DuctLinePlan();
            List<Seg> segs = new List<Seg>();
            int bends = 0, hops = 0;
            foreach (DuctLineCurve curve in curves)
            {
                if (curve.Size == null)
                    plan.UntaggedCurves++;
                int b, h;
                List<Vec2> points = Straighten(curve.Vertices, curve.IsSpline, out b, out h);
                bends += b;
                hops += h;
                for (int i = 0; i + 1 < points.Count; i++)
                {
                    if (points[i].DistanceTo(points[i + 1]) > 1e-6)
                        segs.Add(new Seg { P = points[i], Q = points[i + 1], Curve = curve });
                }
            }

            if (segs.Count == 0)
            {
                plan.Error = "No line segments were selected.";
                return plan;
            }

            if (hops > 0)
                plan.Reports.Add(hops.ToString(CultureInfo.InvariantCulture) + " hop arcs (crossings) were straightened.");

            // Nodes: weld ends, then split segments at tees.
            double tol = settings.WeldTolerance;
            List<Vec2> nodes = new List<Vec2>();
            List<string> nodeGroup = new List<string>();
            Func<Vec2, string, int> nodeAt = (p, group) =>
            {
                for (int k = 0; k < nodes.Count; k++)
                {
                    if (nodeGroup[k] == group && nodes[k].DistanceTo(p) <= tol)
                        return k;
                }

                nodes.Add(p);
                nodeGroup.Add(group);
                return nodes.Count - 1;
            };

            List<int[]> ends = new List<int[]>();
            foreach (Seg s in segs)
                ends.Add(new[] { nodeAt(s.P, s.Curve.Group), nodeAt(s.Q, s.Curve.Group) });

            List<Edge> edges = new List<Edge>();
            for (int i = 0; i < segs.Count; i++)
            {
                Seg s = segs[i];
                Vec2 a = nodes[ends[i][0]];
                Vec2 b = nodes[ends[i][1]];
                Vec2 axis = b - a;
                double length = axis.Length;
                if (length < 1e-6)
                    continue;
                Vec2 dir = axis / length;
                List<KeyValuePair<double, int>> cuts = new List<KeyValuePair<double, int>>();
                for (int k = 0; k < nodes.Count; k++)
                {
                    if (k == ends[i][0] || k == ends[i][1] || nodeGroup[k] != s.Curve.Group)
                        continue;
                    double along = (nodes[k] - a).Dot(dir);
                    if (along <= tol || along >= length - tol)
                        continue;
                    double across = Math.Abs((nodes[k] - a).Cross(dir));
                    if (across <= tol)
                        cuts.Add(new KeyValuePair<double, int>(along, k));
                }

                cuts.Sort((x, y) => x.Key.CompareTo(y.Key));
                int from = ends[i][0];
                foreach (KeyValuePair<double, int> cut in cuts)
                {
                    nodes[cut.Value] = a + dir * cut.Key; // the tee node sits on the main's centreline
                    edges.Add(NewEdge(from, cut.Value, s));
                    from = cut.Value;
                    plan.Tees++;
                }

                edges.Add(NewEdge(from, ends[i][1], s));
            }

            plan.Segments = edges.Count;
            Dictionary<int, List<Edge>> adjacency = new Dictionary<int, List<Edge>>();
            foreach (Edge e in edges)
            {
                if (e.A == e.B)
                    continue;
                Adj(adjacency, e.A).Add(e);
                Adj(adjacency, e.B).Add(e);
            }

            // Root: the node nearest the pick.
            int root = -1;
            double best = double.MaxValue;
            foreach (int k in adjacency.Keys)
            {
                double d = nodes[k].DistanceTo(rootPick);
                if (d < best)
                {
                    best = d;
                    root = k;
                }
            }

            if (root < 0 || best > settings.RootTolerance)
            {
                plan.Error = "Pick the start point near the fan / AHU end of a selected line.";
                return plan;
            }

            plan.Root = nodes[root];
            if (Adj(adjacency, root).Count > 1)
            {
                plan.Reports.Add("The start point is not a line end: every line leaving it starts its own run.");
                plan.ReportPoints.Add(nodes[root]);
            }

            // Tree by breadth-first search from the root.
            Dictionary<int, Edge> parentEdge = new Dictionary<int, Edge>();
            Dictionary<int, List<Edge>> children = new Dictionary<int, List<Edge>>();
            HashSet<int> seen = new HashSet<int> { root };
            Queue<int> queue = new Queue<int>();
            queue.Enqueue(root);
            HashSet<Edge> used = new HashSet<Edge>();
            while (queue.Count > 0)
            {
                int n = queue.Dequeue();
                children[n] = new List<Edge>();
                foreach (Edge e in Adj(adjacency, n))
                {
                    if (used.Contains(e))
                        continue;
                    used.Add(e);
                    int m = e.Other(n);
                    if (seen.Contains(m))
                    {
                        e.Removed = true;
                        plan.Reports.Add("A loop: one segment is left out.");
                        plan.ReportPoints.Add((nodes[n] + nodes[m]) * 0.5);
                        continue;
                    }

                    seen.Add(m);
                    parentEdge[m] = e;
                    children[n].Add(e);
                    queue.Enqueue(m);
                }
            }

            int unreached = edges.Count(e => !used.Contains(e) && e.A != e.B);
            if (unreached > 0)
            {
                plan.Reports.Add(unreached.ToString(CultureInfo.InvariantCulture)
                    + " segments are not connected to the start point (another system, or a gap wider than the weld tolerance) and were left alone.");
                foreach (Edge e in edges.Where(x => !used.Contains(x) && x.A != x.B).Take(5))
                    plan.ReportPoints.Add((nodes[e.A] + nodes[e.B]) * 0.5);
            }

            // Runs.
            double straightCos = Math.Cos(settings.StraightAngleDegrees * Math.PI / 180);
            double tapSin = Math.Sin(settings.TapAngleDegrees * Math.PI / 180);
            Stack<DuctLineRun> pending = new Stack<DuctLineRun>();
            Stack<Edge> pendingEdge = new Stack<Edge>();
            foreach (Edge first in children[root])
            {
                DuctLineRun run = new DuctLineRun { Group = first.Group, Elevation = first.Elevation };
                run.Points.Add(nodes[root]);
                pending.Push(run);
                pendingEdge.Push(first);
            }

            while (pending.Count > 0)
            {
                DuctLineRun run = pending.Pop();
                Edge edge = pendingEdge.Pop();
                int from = parentEdge.ContainsKey(edge.A) && parentEdge[edge.A] == edge ? edge.B : edge.A;
                while (edge != null)
                {
                    int to = edge.Other(from);
                    DuctSize size = edge.Size ?? settings.DefaultSize;
                    run.Points.Add(nodes[to]);
                    run.Sizes.Add(size);
                    run.Curves.Add(edge.Curve);
                    Vec2 dir = (nodes[to] - nodes[from]).Normalized();

                    List<Edge> next = children.ContainsKey(to) ? children[to] : new List<Edge>();
                    Edge straight = null;
                    foreach (Edge c in next)
                    {
                        if ((nodes[c.Other(to)] - nodes[to]).Normalized().Dot(dir) >= straightCos)
                            straight = c;
                    }

                    Edge go = straight;
                    if (go == null && next.Count == 1)
                        go = next[0];
                    if (go == null && next.Count > 1)
                    {
                        go = next.OrderByDescending(c => Area(c.Size ?? settings.DefaultSize)).First();
                        plan.Reports.Add("A bullhead tee: the run follows the larger branch; the other starts without a tap.");
                        plan.ReportPoints.Add(nodes[to]);
                    }

                    foreach (Edge side in next)
                    {
                        if (side == go)
                            continue;
                        DuctLineRun branch = new DuctLineRun { Group = side.Group, Elevation = side.Elevation };
                        Vec2 outward = (nodes[side.Other(to)] - nodes[to]).Normalized();
                        bool square = Math.Abs(outward.Dot(dir)) <= tapSin && straight != null;
                        if (square)
                        {
                            branch.StartsWithTap = true;
                            branch.TapMainAxis = dir;
                            branch.TapOutward = outward;
                            branch.TapWallPoint = nodes[to] + outward * (size.Width / 2);
                            branch.Points.Add(branch.TapWallPoint);
                        }
                        else
                        {
                            branch.Points.Add(nodes[to]);
                            if (straight != null)
                            {
                                plan.Reports.Add("A branch leaves at an angle: it starts without a tap.");
                                plan.ReportPoints.Add(nodes[to]);
                            }
                        }

                        pending.Push(branch);
                        pendingEdge.Push(side);
                    }

                    from = to;
                    edge = go;
                }

                Solve(run, settings);
                plan.Runs.Add(run);
                if (!run.Ok)
                {
                    plan.Reports.Add("A run could not be drawn: " + run.Error);
                    plan.ReportPoints.Add(run.Points[0]);
                }
            }

            foreach (DuctLineRun run in plan.Runs.Where(r => r.Ok))
                plan.DoneCurves.UnionWith(run.Curves);
            foreach (DuctLineRun run in plan.Runs.Where(r => !r.Ok))
                plan.DoneCurves.ExceptWith(run.Curves);
            plan.Ok = plan.Runs.Any(r => r.Ok);
            if (!plan.Ok && plan.Error == null)
                plan.Error = "No run could be drawn.";
            return plan;
        }

        private static void Solve(DuctLineRun run, DuctLineSettings settings)
        {
            RouteInput input = new RouteInput { Settings = settings.Fittings };
            input.Points.AddRange(run.Points);
            input.Sizes.AddRange(run.Sizes);
            if (run.StartsWithTap)
                input.StartLead = DuctFittings.TapLead(run.Sizes[0], settings.Fittings);
            if (input.Points.Count < 2 || input.Sizes.Count != input.Points.Count - 1)
            {
                run.Error = "the run has no segment.";
                return;
            }

            run.Result = RouteSolver.Solve(input);
            if (!run.Result.Ok)
                run.Error = run.Result.Error;
        }

        private static double Area(DuctSize size)
        {
            return size.Width * size.Height;
        }

        private static Edge NewEdge(int a, int b, Seg s)
        {
            return new Edge { A = a, B = b, Size = s.Curve.Size, Curve = s.Curve.Id, Group = s.Curve.Group, Elevation = s.Curve.Elevation };
        }

        private static List<Edge> Adj(Dictionary<int, List<Edge>> adjacency, int node)
        {
            List<Edge> list;
            if (!adjacency.TryGetValue(node, out list))
            {
                list = new List<Edge>();
                adjacency[node] = list;
            }

            return list;
        }

        /// <summary>The template layer of a duct system code (mirrors mep_normalize.SYSTEMS), or null.</summary>
        public static string LayerForSystem(string system)
        {
            switch ((system ?? "").Trim().ToUpperInvariant())
            {
                case "SA": return "EIA_1 SUPPLY AIR";
                case "RA": return "EIA_1 RETURN AIR";
                case "OA": return "EIA_1 OUTSIDE AIR";
                case "EA": return "EIA_1 EXHAUST AIR";
                case "TA": return "EIA_1 TOILET AIR";
                case "KE": return "EIA_3 KITCHEN EXHAUST AIR";
                case "SE": return "EIA_2 EXHAUST SMOKE";
                case "PA": return "EIA_2 STAIR PRESSURISATION";
                case "MA": return "EIA_2 MAKUP AIR";
                default: return null;
            }
        }
    }

    /// <summary>HVACDUCTLINE values remembered between runs (%APPDATA%\HvacAiCadAddin\HVACDUCTLINE.settings).</summary>
    public sealed class DuctLineOptions
    {
        /// <summary>Size of lines without an HVACNORM tag: 600x300, D250, Ø250.</summary>
        public string SizeText = "300x300";
        /// <summary>Layer of runs whose system is unknown; empty = the first duct layer offered.</summary>
        public string Layer = "";
        public double WeldTolerance = 100;
        public bool EraseLines;

        public bool TryGetSize(out DuctSize size)
        {
            return DuctSize.TryParseAny(SizeText, false, out size);
        }

        public bool IsValid(out string error)
        {
            DuctSize size;
            error = null;
            if (!TryGetSize(out size))
                error = "The default size must look like 600x300 or D250.";
            else if (!(WeldTolerance >= 1 && WeldTolerance <= 1000))
                error = "The weld tolerance must be between 1 and 1000 mm.";
            return error == null;
        }

        public string Serialize()
        {
            return "size=" + OneLine(SizeText) + "\nlayer=" + OneLine(Layer)
                + "\nweld=" + WeldTolerance.ToString("R", CultureInfo.InvariantCulture)
                + "\nerase=" + (EraseLines ? "1" : "0") + "\n";
        }

        private static string OneLine(string value)
        {
            return (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        /// <summary>Never throws: unknown keys and bad values keep the defaults.</summary>
        public static DuctLineOptions Deserialize(string text)
        {
            DuctLineOptions options = new DuctLineOptions();
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;
                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Trim();
                double number;
                switch (key)
                {
                    case "size":
                        DuctSize size;
                        if (DuctSize.TryParseAny(value, false, out size))
                            options.SizeText = value;
                        break;
                    case "layer":
                        options.Layer = value;
                        break;
                    case "weld":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                            && number >= 1 && number <= 1000)
                            options.WeldTolerance = number;
                        break;
                    case "erase":
                        options.EraseLines = value == "1";
                        break;
                }
            }

            return options;
        }
    }
}
