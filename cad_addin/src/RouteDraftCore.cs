using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Routes
{
    // AutoCAD-free core of HVACROUTEDRAFT: drafts a rough orthogonal pipe/wire tree from one target point
    // (the riser, outdoor unit or supply end) to every indoor-unit point, ready for the HVACREFRIG,
    // HVACDRAIN and HVACPOWERWIRE cleaners. Plain doubles; Vec2 comes from DuctGeometryCore.cs.
    //
    // Two heuristics are tried and the better one is kept (fewest units lying on the route instead of at
    // its end, then the shortest total length):
    //   trunk  A straight trunk on one axis (horizontal or vertical) at a chosen offset; the target is
    //          joined to it by a perpendicular stub and every unit by a perpendicular branch.
    //   mst    A rectilinear Prim tree: starting at the target, the unit nearest to the tree is joined
    //          to the nearest tree point by an L bend that stops at the first contact with the tree.
    // Walls and obstacles are ignored; the user edits the draft. All coordinates are drawing units (mm).

    /// <summary>HVACROUTEDRAFT values remembered between runs.</summary>
    public sealed class RouteDraftSettings
    {
        public const string RefrigLayer = "HVAC-DRAFT-REFRIG";
        public const string DrainLayer = "HVAC-DRAFT-DRAIN";
        public const string PowerLayer = "HVAC-DRAFT-POWER";
        public const short RefrigColor = 4;
        public const short DrainColor = 5;
        public const short PowerColor = 30;

        /// <summary>Extended-data application name of every draft polyline.</summary>
        public const string XDataApp = "HVACAI_DRAFT";

        public const string ModeBest = "best";
        public const string ModeTrunk = "trunk";
        public const string ModeMst = "mst";
        public const string AxisAuto = "auto";
        public const string AxisHorizontal = "horizontal";
        public const string AxisVertical = "vertical";

        public bool DrawRefrig = true;
        public bool DrawDrain = true;
        public bool DrawPower;

        /// <summary>"best" tries both tree modes and keeps the shorter, "trunk" or "mst" forces one.</summary>
        public string Mode = ModeBest;

        /// <summary>"auto", "horizontal" or "vertical": the trunk direction of the trunk mode.</summary>
        public string TrunkAxis = AxisAuto;

        /// <summary>The shortest stub the target gets when it must not lie on the route (mm).</summary>
        public double MinStub = 1500;

        /// <summary>True: ask for a separate drain target (for example another shaft).</summary>
        public bool SeparateDrainTarget;

        /// <summary>True: ask for a separate power supply point.</summary>
        public bool SeparatePowerTarget;

        public bool AnyDraft
        {
            get { return DrawRefrig || DrawDrain || DrawPower; }
        }

        public bool IsValid(out string error)
        {
            error = null;
            if (!AnyDraft)
                error = "Tick at least one draft to draw.";
            else if (Mode != ModeBest && Mode != ModeTrunk && Mode != ModeMst)
                error = "Unknown tree mode.";
            else if (TrunkAxis != AxisAuto && TrunkAxis != AxisHorizontal && TrunkAxis != AxisVertical)
                error = "Unknown trunk axis.";
            else if (double.IsNaN(MinStub) || double.IsInfinity(MinStub) || MinStub < 0 || MinStub > 100000)
                error = "The shortest stub must be between 0 and 100000 mm.";
            return error == null;
        }

        public string Serialize()
        {
            StringBuilder text = new StringBuilder();
            Add(text, "refrig", DrawRefrig ? "1" : "0");
            Add(text, "drain", DrawDrain ? "1" : "0");
            Add(text, "power", DrawPower ? "1" : "0");
            Add(text, "mode", Mode);
            Add(text, "axis", TrunkAxis);
            Add(text, "minstub", MinStub.ToString("R", CultureInfo.InvariantCulture));
            Add(text, "draintarget", SeparateDrainTarget ? "1" : "0");
            Add(text, "powertarget", SeparatePowerTarget ? "1" : "0");
            return text.ToString();
        }

        /// <summary>Never throws: unknown keys and bad values keep the defaults.</summary>
        public static RouteDraftSettings Deserialize(string text)
        {
            RouteDraftSettings settings = new RouteDraftSettings();
            if (string.IsNullOrEmpty(text))
                return settings;

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                int equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;
                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Trim();
                double number;
                switch (key)
                {
                    case "refrig":
                        settings.DrawRefrig = value == "1";
                        break;
                    case "drain":
                        settings.DrawDrain = value == "1";
                        break;
                    case "power":
                        settings.DrawPower = value == "1";
                        break;
                    case "mode":
                        if (value == ModeBest || value == ModeTrunk || value == ModeMst)
                            settings.Mode = value;
                        break;
                    case "axis":
                        if (value == AxisAuto || value == AxisHorizontal || value == AxisVertical)
                            settings.TrunkAxis = value;
                        break;
                    case "minstub":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                            && !double.IsNaN(number) && !double.IsInfinity(number) && number >= 0 && number <= 100000)
                            settings.MinStub = number;
                        break;
                    case "draintarget":
                        settings.SeparateDrainTarget = value == "1";
                        break;
                    case "powertarget":
                        settings.SeparatePowerTarget = value == "1";
                        break;
                }
            }

            return settings;
        }

        private static void Add(StringBuilder text, string key, string value)
        {
            text.Append(key).Append('=').Append(value).Append('\n');
        }
    }

    public sealed class RouteDraftOptions
    {
        public string Mode = RouteDraftSettings.ModeBest;
        public string TrunkAxis = RouteDraftSettings.AxisAuto;
        public double MinStub = 1500;

        public static RouteDraftOptions From(RouteDraftSettings settings)
        {
            return new RouteDraftOptions
            {
                Mode = settings.Mode,
                TrunkAxis = settings.TrunkAxis,
                MinStub = settings.MinStub
            };
        }
    }

    public sealed class RouteDraftResult
    {
        /// <summary>The tree as polylines: each runs between two nodes (target, unit or junction).</summary>
        public readonly List<List<Vec2>> Polylines = new List<List<Vec2>>();

        public Vec2 Root;

        /// <summary>Sum of all polyline lengths (mm).</summary>
        public double TotalLength;

        /// <summary>Pipe length from the target to each input unit, in input order (mm).</summary>
        public double[] UnitPathLengths = new double[0];

        /// <summary>The heuristic that won, for example "trunk horizontal" or "mst".</summary>
        public string ModeUsed = "";

        /// <summary>False when the target lies on the route instead of at its end.</summary>
        public bool RootIsLeaf = true;

        /// <summary>Units that lie on the route (degree 2 or more) instead of at a pipe end.</summary>
        public int NonLeafUnits;

        /// <summary>Junctions where four pipes meet (the cleaners accept T junctions only).</summary>
        public int CrossJunctions;

        public readonly List<string> Warnings = new List<string>();
    }

    public static class RouteDraftCore
    {
        private const double SamePoint = 1e-6;

        /// <summary>Largest unit count for which the slower search variants are tried.</summary>
        private const int RichSearchLimit = 60;
        private const int MstLimit = 150;

        // ------------------------------------------------------------------ public API

        public static RouteDraftResult Build(Vec2 target, IList<Vec2> units, RouteDraftOptions options)
        {
            if (units == null)
                throw new ArgumentNullException("units");
            if (options == null)
                options = new RouteDraftOptions();

            RouteDraftResult result = new RouteDraftResult { Root = target };
            result.UnitPathLengths = new double[units.Count];

            // Unique unit points; a unit on the target needs no pipe.
            List<Vec2> unique = new List<Vec2>();
            int[] map = new int[units.Count];
            int atTarget = 0;
            for (int i = 0; i < units.Count; i++)
            {
                Vec2 u = units[i];
                if (Same(u, target))
                {
                    map[i] = -1;
                    atTarget++;
                    continue;
                }

                int found = -1;
                for (int k = 0; k < unique.Count; k++)
                {
                    if (Same(unique[k], u))
                    {
                        found = k;
                        break;
                    }
                }

                if (found < 0)
                {
                    found = unique.Count;
                    unique.Add(u);
                }

                map[i] = found;
            }

            if (atTarget > 0)
                result.Warnings.Add(atTarget + " unit(s) lie on the target point and get no pipe.");
            if (unique.Count == 0)
            {
                result.ModeUsed = "none";
                return result;
            }

            double stub = Math.Max(0, options.MinStub);
            List<Candidate> candidates = new List<Candidate>();
            bool wantTrunk = options.Mode != RouteDraftSettings.ModeMst;
            bool wantMst = options.Mode != RouteDraftSettings.ModeTrunk && unique.Count <= MstLimit;

            if (wantTrunk)
            {
                if (options.TrunkAxis != RouteDraftSettings.AxisVertical)
                    AddTrunkCandidates(candidates, target, unique, true, stub);
                if (options.TrunkAxis != RouteDraftSettings.AxisHorizontal)
                    AddTrunkCandidates(candidates, target, unique, false, stub);
            }

            if (wantMst || candidates.Count == 0)
                AddMstCandidates(candidates, target, unique, stub);

            Candidate best = null;
            foreach (Candidate candidate in candidates)
            {
                if (candidate.IsBetterThan(best))
                    best = candidate;
            }

            if (best == null)
                throw new InvalidOperationException("No route could be built.");

            result.ModeUsed = best.Name;
            result.TotalLength = best.Graph.TotalLength;
            result.RootIsLeaf = best.RootDegree == 1;
            result.NonLeafUnits = best.NonLeafUnits;
            result.CrossJunctions = best.CrossJunctions;
            result.Polylines.AddRange(best.Graph.ToPolylines(target, unique));

            double[] distance = best.Graph.DistancesFrom(target);
            for (int i = 0; i < units.Count; i++)
                result.UnitPathLengths[i] = map[i] < 0 ? 0 : best.Graph.DistanceTo(distance, unique[map[i]]);

            if (!result.RootIsLeaf)
                result.Warnings.Add("The target lies on the route instead of at its end; pick a pipe end as the root of the cleaner.");
            if (result.CrossJunctions > 0)
                result.Warnings.Add(result.CrossJunctions + " crossing(s) of four pipes; the cleaners accept T junctions only, so move a unit or the trunk.");
            if (result.NonLeafUnits > 0)
                result.Warnings.Add(result.NonLeafUnits + " unit(s) lie on the route instead of at a pipe end; move them or the trunk.");
            return result;
        }

        /// <summary>Area-weighted centre of a polygon (the bounding-box centre when it is degenerate).</summary>
        public static Vec2 PolygonCentre(IList<Vec2> polygon)
        {
            if (polygon == null || polygon.Count == 0)
                throw new ArgumentException("The polygon has no vertices.");

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (Vec2 p in polygon)
            {
                minX = Math.Min(minX, p.X);
                maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y);
                maxY = Math.Max(maxY, p.Y);
            }

            Vec2 box = new Vec2((minX + maxX) / 2, (minY + maxY) / 2);

            // Work relative to the box centre: the coordinates may be around 4e6.
            double twiceArea = 0, cx = 0, cy = 0;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vec2 a = polygon[i] - box;
                Vec2 b = polygon[(i + 1) % polygon.Count] - box;
                double cross = a.X * b.Y - b.X * a.Y;
                twiceArea += cross;
                cx += (a.X + b.X) * cross;
                cy += (a.Y + b.Y) * cross;
            }

            if (Math.Abs(twiceArea) < 1e-9)
                return box;
            return new Vec2(box.X + cx / (3 * twiceArea), box.Y + cy / (3 * twiceArea));
        }

        /// <summary>Index of the centre nearest to the point, or -1 for an empty list.</summary>
        public static int NearestIndex(Vec2 point, IList<Vec2> centres)
        {
            int best = -1;
            double bestDistance = double.MaxValue;
            for (int i = 0; i < centres.Count; i++)
            {
                double d = point.DistanceTo(centres[i]);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }

            return best;
        }

        public static Vec2 Centroid(IList<Vec2> points)
        {
            double x = 0, y = 0;
            if (points.Count == 0)
                return new Vec2(0, 0);
            Vec2 origin = points[0];
            foreach (Vec2 p in points)
            {
                x += p.X - origin.X;
                y += p.Y - origin.Y;
            }

            return new Vec2(origin.X + x / points.Count, origin.Y + y / points.Count);
        }

        // ------------------------------------------------------------------ candidates

        private sealed class Candidate
        {
            public string Name;
            public Graph Graph;
            public int RootDegree;
            public int NonLeafUnits;
            public int CrossJunctions;

            private int RootPenalty
            {
                get { return RootDegree == 1 ? 0 : 1; }
            }

            public bool IsBetterThan(Candidate other)
            {
                if (other == null)
                    return true;
                if (RootPenalty != other.RootPenalty)
                    return RootPenalty < other.RootPenalty;
                if (CrossJunctions != other.CrossJunctions)
                    return CrossJunctions < other.CrossJunctions;
                if (NonLeafUnits != other.NonLeafUnits)
                    return NonLeafUnits < other.NonLeafUnits;
                return Graph.TotalLength < other.Graph.TotalLength - 1e-6;
            }
        }

        private static void Consider(List<Candidate> candidates, string name, List<Seg> segments, Vec2 target, IList<Vec2> units)
        {
            Graph graph = Graph.Build(segments);
            if (graph == null || !graph.IsTree)
                return;
            Candidate candidate = new Candidate { Name = name, Graph = graph };
            candidate.RootDegree = graph.DegreeOf(target);
            candidate.CrossJunctions = graph.CountJunctionsOver(3);
            foreach (Vec2 u in units)
            {
                if (graph.DegreeOf(u) != 1)
                    candidate.NonLeafUnits++;
            }

            candidates.Add(candidate);
        }

        // ---- trunk

        private static void AddTrunkCandidates(List<Candidate> candidates, Vec2 target, IList<Vec2> units, bool horizontal, double stub)
        {
            double tc = Across(target, horizontal);
            List<double> offsets = new List<double> { tc, tc + stub, tc - stub };

            List<double> across = new List<double>();
            foreach (Vec2 u in units)
                across.Add(Across(u, horizontal));
            across.Sort();
            offsets.Add(across[(across.Count - 1) / 2]);
            offsets.Add(across[across.Count / 2]);

            if (units.Count <= RichSearchLimit)
            {
                foreach (double c in across)
                {
                    offsets.Add(c);
                    offsets.Add(c + stub);
                    offsets.Add(c - stub);
                }
            }

            List<double> done = new List<double>();
            foreach (double c in offsets)
            {
                if (done.Contains(c))
                    continue;
                done.Add(c);
                Consider(candidates, horizontal ? "trunk horizontal" : "trunk vertical",
                    TrunkSegments(target, units, horizontal, c), target, units);
            }
        }

        private static double Along(Vec2 p, bool horizontal)
        {
            return horizontal ? p.X : p.Y;
        }

        private static double Across(Vec2 p, bool horizontal)
        {
            return horizontal ? p.Y : p.X;
        }

        private static Vec2 Make(double along, double across, bool horizontal)
        {
            return horizontal ? new Vec2(along, across) : new Vec2(across, along);
        }

        private static List<Seg> TrunkSegments(Vec2 target, IList<Vec2> units, bool horizontal, double c)
        {
            double ts = Along(target, horizontal);
            double lo = ts, hi = ts;
            foreach (Vec2 u in units)
            {
                lo = Math.Min(lo, Along(u, horizontal));
                hi = Math.Max(hi, Along(u, horizontal));
            }

            List<Seg> segments = new List<Seg>();
            segments.Add(new Seg(target, Make(ts, c, horizontal)));
            segments.Add(new Seg(Make(lo, c, horizontal), Make(hi, c, horizontal)));
            foreach (Vec2 u in units)
                segments.Add(new Seg(Make(Along(u, horizontal), c, horizontal), u));
            return segments;
        }

        // ---- rectilinear Prim

        private static void AddMstCandidates(List<Candidate> candidates, Vec2 target, IList<Vec2> units, double stub)
        {
            // The target must end up at a pipe end: either it is a leaf of the Prim tree itself, or it is
            // joined by a stub to a root one stub away.
            Consider(candidates, "mst", Prim(target, units), target, units);
            if (stub <= 0)
                return;

            Vec2[] directions = { new Vec2(1, 0), new Vec2(-1, 0), new Vec2(0, 1), new Vec2(0, -1) };
            foreach (Vec2 d in directions)
            {
                Vec2 root = target + d * stub;
                List<Seg> segments = Prim(root, units);
                segments.Add(new Seg(target, root));
                Consider(candidates, "mst", segments, target, units);
            }
        }

        private static List<Seg> Prim(Vec2 root, IList<Vec2> units)
        {
            List<Seg> tree = new List<Seg>();
            List<Vec2> remaining = new List<Vec2>(units);
            while (remaining.Count > 0)
            {
                int pick = 0;
                double pickDistance = double.MaxValue;
                Vec2 pickPoint = root;
                for (int i = 0; i < remaining.Count; i++)
                {
                    Vec2 p;
                    double d = NearestOnTree(remaining[i], root, tree, out p);
                    if (d < pickDistance)
                    {
                        pickDistance = d;
                        pick = i;
                        pickPoint = p;
                    }
                }

                Vec2 unit = remaining[pick];
                remaining.RemoveAt(pick);
                if (pickDistance <= 0)
                    continue;

                List<Vec2> first = TruncatedPath(unit, new Vec2(pickPoint.X, unit.Y), pickPoint, tree);
                List<Vec2> second = TruncatedPath(unit, new Vec2(unit.X, pickPoint.Y), pickPoint, tree);
                List<Vec2> chosen = PathLength(second) < PathLength(first) - 1e-9 ? second : first;
                for (int i = 0; i + 1 < chosen.Count; i++)
                    tree.Add(new Seg(chosen[i], chosen[i + 1]));
            }

            return tree;
        }

        private static double NearestOnTree(Vec2 u, Vec2 root, List<Seg> tree, out Vec2 at)
        {
            at = root;
            double best = Math.Abs(u.X - root.X) + Math.Abs(u.Y - root.Y);
            foreach (Seg s in tree)
            {
                double x = Math.Min(Math.Max(u.X, Math.Min(s.A.X, s.B.X)), Math.Max(s.A.X, s.B.X));
                double y = Math.Min(Math.Max(u.Y, Math.Min(s.A.Y, s.B.Y)), Math.Max(s.A.Y, s.B.Y));
                double d = Math.Abs(u.X - x) + Math.Abs(u.Y - y);
                if (d < best)
                {
                    best = d;
                    at = new Vec2(x, y);
                }
            }

            return best;
        }

        // The L path unit -> corner -> end, cut at its first contact with the tree so that no loop forms.
        private static List<Vec2> TruncatedPath(Vec2 start, Vec2 corner, Vec2 end, List<Seg> tree)
        {
            List<Vec2> points = new List<Vec2> { start };
            if (!Same(corner, start) && !Same(corner, end))
                points.Add(corner);
            if (!Same(end, points[points.Count - 1]))
                points.Add(end);

            List<Vec2> result = new List<Vec2> { points[0] };
            for (int i = 0; i + 1 < points.Count; i++)
            {
                Vec2 a = points[i];
                Vec2 b = points[i + 1];
                double contact = double.MaxValue;
                foreach (Seg s in tree)
                {
                    double d = FirstContact(a, b, s);
                    if (d < contact)
                        contact = d;
                }

                double length = a.DistanceTo(b);
                if (contact < length)
                {
                    Vec2 direction = (b - a) / length;
                    Vec2 hit = contact <= 0 ? a : a + direction * contact;
                    // Snap the hit onto the axis it travels along so no rounding creeps into coordinates.
                    hit = a.Y == b.Y ? new Vec2(hit.X, a.Y) : new Vec2(a.X, hit.Y);
                    if (!Same(hit, result[result.Count - 1]))
                        result.Add(hit);
                    return result;
                }

                result.Add(b);
            }

            return result;
        }

        private static double PathLength(List<Vec2> points)
        {
            double length = 0;
            for (int i = 0; i + 1 < points.Count; i++)
                length += points[i].DistanceTo(points[i + 1]);
            return length;
        }

        // Distance along a -> b (axis aligned) to the first point lying on the segment; MaxValue when none.
        // Contacts at distance 0 (the start itself) are ignored.
        private static double FirstContact(Vec2 a, Vec2 b, Seg s)
        {
            double eps = 1e-9;
            bool abHorizontal = a.Y == b.Y;
            double result = double.MaxValue;
            if (abHorizontal)
            {
                double lo = Math.Min(a.X, b.X), hi = Math.Max(a.X, b.X);
                bool forward = b.X >= a.X;
                if (s.A.X == s.B.X)
                {
                    double y1 = Math.Min(s.A.Y, s.B.Y), y2 = Math.Max(s.A.Y, s.B.Y);
                    if (s.A.X >= lo && s.A.X <= hi && a.Y >= y1 && a.Y <= y2)
                        result = Math.Abs(s.A.X - a.X);
                }
                else if (s.A.Y == a.Y)
                {
                    double t1 = Math.Min(s.A.X, s.B.X), t2 = Math.Max(s.A.X, s.B.X);
                    double ol = Math.Max(lo, t1), oh = Math.Min(hi, t2);
                    if (ol <= oh)
                        result = Math.Abs((forward ? ol : oh) - a.X);
                }
            }
            else
            {
                double lo = Math.Min(a.Y, b.Y), hi = Math.Max(a.Y, b.Y);
                bool forward = b.Y >= a.Y;
                if (s.A.Y == s.B.Y)
                {
                    double x1 = Math.Min(s.A.X, s.B.X), x2 = Math.Max(s.A.X, s.B.X);
                    if (s.A.Y >= lo && s.A.Y <= hi && a.X >= x1 && a.X <= x2)
                        result = Math.Abs(s.A.Y - a.Y);
                }
                else if (s.A.X == a.X)
                {
                    double t1 = Math.Min(s.A.Y, s.B.Y), t2 = Math.Max(s.A.Y, s.B.Y);
                    double ol = Math.Max(lo, t1), oh = Math.Min(hi, t2);
                    if (ol <= oh)
                        result = Math.Abs((forward ? ol : oh) - a.Y);
                }
            }

            return result <= eps ? double.MaxValue : result;
        }

        private static bool Same(Vec2 a, Vec2 b)
        {
            return Math.Abs(a.X - b.X) <= SamePoint && Math.Abs(a.Y - b.Y) <= SamePoint;
        }

        // ------------------------------------------------------------------ graph

        private struct Seg
        {
            public readonly Vec2 A;
            public readonly Vec2 B;

            public Seg(Vec2 a, Vec2 b)
            {
                A = a;
                B = b;
            }
        }

        private static Tuple<double, double> Key(Vec2 p)
        {
            return Tuple.Create(p.X + 0.0, p.Y + 0.0);
        }

        // A tree of axis-aligned edges: overlapping and duplicate pieces merged, split at every endpoint.
        private sealed class Graph
        {
            private readonly Dictionary<Tuple<double, double>, int> _index = new Dictionary<Tuple<double, double>, int>();
            private readonly List<Vec2> _nodes = new List<Vec2>();
            private readonly List<int[]> _edges = new List<int[]>();
            private readonly List<List<int>> _adjacent = new List<List<int>>();

            public double TotalLength;
            public bool IsTree;

            public static Graph Build(List<Seg> segments)
            {
                // Distinct endpoints.
                List<Vec2> points = new List<Vec2>();
                HashSet<Tuple<double, double>> seen = new HashSet<Tuple<double, double>>();
                foreach (Seg s in segments)
                {
                    if (seen.Add(Key(s.A)))
                        points.Add(s.A);
                    if (seen.Add(Key(s.B)))
                        points.Add(s.B);
                }

                Graph graph = new Graph();
                HashSet<Tuple<int, int>> edgeSet = new HashSet<Tuple<int, int>>();
                foreach (Vec2 p in points)
                    graph.NodeOf(p);

                foreach (Seg s in segments)
                {
                    bool horizontal = s.A.Y == s.B.Y;
                    bool vertical = s.A.X == s.B.X;
                    if (horizontal && vertical)
                        continue; // zero length
                    if (!horizontal && !vertical)
                        return null;

                    double lo = horizontal ? Math.Min(s.A.X, s.B.X) : Math.Min(s.A.Y, s.B.Y);
                    double hi = horizontal ? Math.Max(s.A.X, s.B.X) : Math.Max(s.A.Y, s.B.Y);
                    List<Vec2> on = new List<Vec2>();
                    foreach (Vec2 p in points)
                    {
                        if (horizontal)
                        {
                            if (p.Y == s.A.Y && p.X >= lo && p.X <= hi)
                                on.Add(p);
                        }
                        else if (p.X == s.A.X && p.Y >= lo && p.Y <= hi)
                            on.Add(p);
                    }

                    if (horizontal)
                        on.Sort((p, q) => p.X.CompareTo(q.X));
                    else
                        on.Sort((p, q) => p.Y.CompareTo(q.Y));

                    for (int i = 0; i + 1 < on.Count; i++)
                    {
                        int a = graph.NodeOf(on[i]);
                        int b = graph.NodeOf(on[i + 1]);
                        if (a == b)
                            continue;
                        Tuple<int, int> edge = a < b ? Tuple.Create(a, b) : Tuple.Create(b, a);
                        if (edgeSet.Add(edge))
                            graph.AddEdge(edge.Item1, edge.Item2);
                    }
                }

                graph.Finish();
                return graph;
            }

            private int NodeOf(Vec2 p)
            {
                int index;
                if (_index.TryGetValue(Key(p), out index))
                    return index;
                index = _nodes.Count;
                _index[Key(p)] = index;
                _nodes.Add(p);
                _adjacent.Add(new List<int>());
                return index;
            }

            private void AddEdge(int a, int b)
            {
                int edge = _edges.Count;
                _edges.Add(new[] { a, b });
                _adjacent[a].Add(edge);
                _adjacent[b].Add(edge);
                TotalLength += _nodes[a].DistanceTo(_nodes[b]);
            }

            private void Finish()
            {
                // Only nodes that carry an edge count (a lone endpoint of a zero-length piece does not).
                int used = 0;
                int start = -1;
                for (int i = 0; i < _nodes.Count; i++)
                {
                    if (_adjacent[i].Count > 0)
                    {
                        used++;
                        if (start < 0)
                            start = i;
                    }
                }

                if (used == 0 || _edges.Count != used - 1)
                {
                    IsTree = false;
                    return;
                }

                bool[] visited = new bool[_nodes.Count];
                Stack<int> stack = new Stack<int>();
                stack.Push(start);
                visited[start] = true;
                int reached = 1;
                while (stack.Count > 0)
                {
                    int n = stack.Pop();
                    foreach (int e in _adjacent[n])
                    {
                        int other = _edges[e][0] == n ? _edges[e][1] : _edges[e][0];
                        if (!visited[other])
                        {
                            visited[other] = true;
                            reached++;
                            stack.Push(other);
                        }
                    }
                }

                IsTree = reached == used;
            }

            public int CountJunctionsOver(int degree)
            {
                int count = 0;
                foreach (List<int> edges in _adjacent)
                {
                    if (edges.Count > degree)
                        count++;
                }

                return count;
            }

            public int DegreeOf(Vec2 p)
            {
                int index;
                return _index.TryGetValue(Key(p), out index) ? _adjacent[index].Count : 0;
            }

            /// <summary>Pipe distance from the start point to every node (NaN when unreachable).</summary>
            public double[] DistancesFrom(Vec2 start)
            {
                double[] distance = new double[_nodes.Count];
                for (int i = 0; i < distance.Length; i++)
                    distance[i] = double.NaN;
                int s;
                if (!_index.TryGetValue(Key(start), out s))
                    return distance;
                distance[s] = 0;
                Stack<int> stack = new Stack<int>();
                stack.Push(s);
                while (stack.Count > 0)
                {
                    int n = stack.Pop();
                    foreach (int e in _adjacent[n])
                    {
                        int other = _edges[e][0] == n ? _edges[e][1] : _edges[e][0];
                        if (double.IsNaN(distance[other]))
                        {
                            distance[other] = distance[n] + _nodes[n].DistanceTo(_nodes[other]);
                            stack.Push(other);
                        }
                    }
                }

                return distance;
            }

            public double DistanceTo(double[] distances, Vec2 p)
            {
                int index;
                if (!_index.TryGetValue(Key(p), out index) || double.IsNaN(distances[index]))
                    return double.NaN;
                return distances[index];
            }

            /// <summary>
            /// Polylines between nodes that are the target, a unit or a junction or end of the tree; collinear
            /// vertices in between are dropped.
            /// </summary>
            public List<List<Vec2>> ToPolylines(Vec2 target, IList<Vec2> units)
            {
                HashSet<int> special = new HashSet<int>();
                int t;
                if (_index.TryGetValue(Key(target), out t))
                    special.Add(t);
                foreach (Vec2 u in units)
                {
                    int ui;
                    if (_index.TryGetValue(Key(u), out ui))
                        special.Add(ui);
                }

                for (int i = 0; i < _nodes.Count; i++)
                {
                    if (_adjacent[i].Count != 2 && _adjacent[i].Count > 0)
                        special.Add(i);
                }

                List<List<Vec2>> polylines = new List<List<Vec2>>();
                bool[] edgeUsed = new bool[_edges.Count];
                List<int> order = new List<int>(special);
                order.Sort();
                foreach (int startNode in order)
                {
                    foreach (int firstEdge in _adjacent[startNode])
                    {
                        if (edgeUsed[firstEdge])
                            continue;
                        List<Vec2> line = new List<Vec2> { _nodes[startNode] };
                        int node = startNode;
                        int edge = firstEdge;
                        while (true)
                        {
                            edgeUsed[edge] = true;
                            int next = _edges[edge][0] == node ? _edges[edge][1] : _edges[edge][0];
                            line.Add(_nodes[next]);
                            node = next;
                            if (special.Contains(node))
                                break;
                            int following = -1;
                            foreach (int e in _adjacent[node])
                            {
                                if (!edgeUsed[e])
                                    following = e;
                            }

                            if (following < 0)
                                break;
                            edge = following;
                        }

                        polylines.Add(DropCollinear(line));
                    }
                }

                return polylines;
            }

            private static List<Vec2> DropCollinear(List<Vec2> line)
            {
                List<Vec2> kept = new List<Vec2>();
                for (int i = 0; i < line.Count; i++)
                {
                    if (i > 0 && i < line.Count - 1)
                    {
                        Vec2 a = line[i - 1], b = line[i], c = line[i + 1];
                        bool sameX = a.X == b.X && b.X == c.X;
                        bool sameY = a.Y == b.Y && b.Y == c.Y;
                        if (sameX || sameY)
                            continue;
                    }

                    kept.Add(line[i]);
                }

                return kept;
            }
        }
    }
}
