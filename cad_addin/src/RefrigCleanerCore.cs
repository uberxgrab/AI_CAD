using System;
using System.Collections.Generic;
using System.Globalization;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Pipes
{
    // AutoCAD-free core of HVACREFRIG: cleans a rough refrigerant pipe network and finds the refnet
    // (branch joint) positions. The order of the cleaning steps is fixed (AGENTS.md, "HVACREFRIG"):
    // weld, T-splits, classify, chain, cluster, collapse jogs, delete zero-length edges, dissolve
    // collinear nodes. The result is checked before anything is written. Straight segments only.

    public sealed class RefrigInput
    {
        public IList<PipeCurve> Curves = new List<PipeCurve>();
        public RefrigSettings Settings = new RefrigSettings();

        /// <summary>The base direction (radians from world X): horizontal is along it, vertical across it.</summary>
        public double BaseAngle;

        /// <summary>A point near the outdoor-unit end of the network; null skips the refnets.</summary>
        public Vec2? RootPoint;

        /// <summary>The root must be a pipe end this close to <see cref="RootPoint"/> (mm).</summary>
        public double RootTolerance = 500;

        /// <summary>Port points of refnet blocks already in the drawing.</summary>
        public IList<Vec2> ExistingRefnetPorts = new List<Vec2>();
    }

    /// <summary>One cleaned pipe run, to be written as one polyline.</summary>
    public sealed class RefrigPath
    {
        public string Layer;
        public double Elevation;
        /// <summary>The <see cref="PipeCurve.Id"/> that contributed most length; its properties are copied.</summary>
        public int SourceCurve;
        public readonly List<Vec2> Points = new List<Vec2>();
    }

    public sealed class RefnetPlacement
    {
        /// <summary>The degree-3 node the refnet replaces.</summary>
        public Vec2 Node;
        /// <summary>Insertion point: the block's inlet, <see cref="SetBack"/> + 750 s upstream of <see cref="Node"/>.</summary>
        public Vec2 Inlet;
        /// <summary>Unit direction from the inlet toward the main outlet.</summary>
        public Vec2 Direction;
        /// <summary>True when the branch is on the left of <see cref="Direction"/>.</summary>
        public bool Mirrored;
        public double Scale;
        public string Layer;
        public double Elevation;
        /// <summary>The main outlet: <see cref="SetBack"/> upstream of <see cref="Node"/>, on the main.</summary>
        public Vec2 MainPort;
        /// <summary>The branch outlet, 200 s to the side of <see cref="MainPort"/>.</summary>
        public Vec2 BranchPort;
        /// <summary>
        /// Where the branch pipe turns toward the branch: 200 s to the side of <see cref="Node"/>. A pipe
        /// of length <see cref="SetBack"/> runs from <see cref="BranchPort"/> to here.
        /// </summary>
        public Vec2 BranchTurn;
        /// <summary>How far the main outlet is back from the T node (mm); at least the R2 distance.</summary>
        public double SetBack;

        /// <summary>Block rotation: along the direction, plus 180 degrees when mirrored.</summary>
        public double Rotation
        {
            get { return Direction.Angle + (Mirrored ? Math.PI : 0); }
        }

        /// <summary>Block X scale: negative when mirrored.</summary>
        public double XScale
        {
            get { return Mirrored ? -Scale : Scale; }
        }
    }

    /// <summary>One measured check of a piping rule (R1, R2 or R3), kept whether it passed or not.</summary>
    public sealed class RuleCheck
    {
        /// <summary>"R1", "R2" or "R3".</summary>
        public string Rule;
        /// <summary>The refnet's T node (R1: the downstream refnet's) or, for R3, the farthest indoor unit.</summary>
        public Vec2 At;
        public double Measured;
        /// <summary>R1, R2: the least allowed (mm). R3: the length above which a warning is given (mm).</summary>
        public double Required;
        public bool Passed;
        /// <summary>The warning text of a failed check, otherwise null.</summary>
        public string Message;
    }

    public sealed class RefrigResult
    {
        public bool Ok;
        public string Error;
        /// <summary>Where a failed check found its problem, for a highlight.</summary>
        public readonly List<Vec2> ErrorPoints = new List<Vec2>();
        public readonly List<RefrigPath> Paths = new List<RefrigPath>();
        public readonly List<RefnetPlacement> Refnets = new List<RefnetPlacement>();
        /// <summary>Cases the command reports and leaves alone.</summary>
        public readonly List<string> Reports = new List<string>();
        public readonly List<Vec2> ReportPoints = new List<Vec2>();

        /// <summary>Every R1, R2 and R3 measurement, passed or not.</summary>
        public readonly List<RuleCheck> RuleChecks = new List<RuleCheck>();
        /// <summary>The failed checks: the refnets are still placed, the user is told.</summary>
        public readonly List<string> Warnings = new List<string>();
        public readonly List<Vec2> WarningPoints = new List<Vec2>();
        /// <summary>R3: pipe length from the first refnet's inlet to the farthest indoor unit (mm); -1 when there is no refnet or unit.</summary>
        public double FarthestUnitDistance = -1;
        public Vec2 FarthestUnit;
        public Vec2 FirstRefnetInlet;

        /// <summary>True when a root was given and found; <see cref="Root"/> is then the pipe end it resolved to.</summary>
        public bool HasRoot;
        public Vec2 Root;

        /// <summary>False when the network is already clean and no refnet is due: nothing to write.</summary>
        public bool Changed;
        public int Components;
        public int Anchors;
        public int NodesMoved;
        public int DuplicatesRemoved;
        public int ZeroLengthRemoved;
        public int NodesDissolved;
        public int JogsMerged;
    }

    public static class RefrigCleaner
    {
        public static RefrigResult Run(RefrigInput input)
        {
            RefrigResult result = new RefrigResult();
            new Engine(input, result).Execute();
            return result;
        }

        /// <summary>Distance between two segments, and the point on the first that is nearest.</summary>
        internal static double SegmentDistance(Vec2 p1, Vec2 p2, Vec2 q1, Vec2 q2, out Vec2 at)
        {
            Vec2 d1 = p2 - p1;
            Vec2 d2 = q2 - q1;
            double denominator = d1.Cross(d2);
            double scale = d1.Length * d2.Length;
            if (scale > 1e-18 && Math.Abs(denominator) > 1e-12 * scale)
            {
                double t = (q1 - p1).Cross(d2) / denominator;
                double u = (q1 - p1).Cross(d1) / denominator;
                if (t >= 0 && t <= 1 && u >= 0 && u <= 1)
                {
                    at = p1 + d1 * t;
                    return 0;
                }
            }

            double best = double.MaxValue;
            at = p1;
            Consider(p1, q1, q2, ref best, ref at, true);
            Consider(p2, q1, q2, ref best, ref at, true);
            Consider(q1, p1, p2, ref best, ref at, false);
            Consider(q2, p1, p2, ref best, ref at, false);
            return best;
        }

        private static void Consider(Vec2 p, Vec2 a, Vec2 b, ref double best, ref Vec2 at, bool pointOnFirst)
        {
            double distance;
            double f = PipeSeg.Project(a, b, 0, p, out distance);
            if (distance < best)
            {
                best = distance;
                at = pointOnFirst ? p : a + (b - a) * f;
            }
        }

        /// <summary>
        /// Groups the chains of one direction class into clusters that will share one line. Chains that
        /// are pinned to an anchor go first, by pin value; the others join the nearest cluster that can
        /// take them. A chain can join a cluster only if its projection along the line does not overlap
        /// any member's (by more than <paramref name="overlapTolerance"/>), its pin matches the
        /// cluster's, and the cluster's span stays within <paramref name="alignTolerance"/>.
        /// The span cap stops merges from chaining on: with a cap of 200, chains at 0, 120 and 240 end
        /// up as two clusters, not one.
        /// </summary>
        internal static List<Cluster> ClusterChains(
            IList<Chain> chains, int cls, double alignTolerance, double overlapTolerance)
        {
            List<Chain> pinned = new List<Chain>();
            List<Chain> free = new List<Chain>();
            foreach (Chain chain in chains)
            {
                if (chain.HasPin)
                    pinned.Add(chain);
                else
                    free.Add(chain);
            }

            pinned.Sort((p, q) =>
            {
                int byPin = p.Pin.CompareTo(q.Pin);
                return byPin != 0 ? byPin : p.Lo.CompareTo(q.Lo);
            });
            free.Sort((p, q) =>
            {
                int byMean = p.Mean.CompareTo(q.Mean);
                return byMean != 0 ? byMean : p.Lo.CompareTo(q.Lo);
            });

            List<Cluster> clusters = new List<Cluster>();
            foreach (Chain chain in pinned)
                AssignChain(clusters, chain, cls, alignTolerance, overlapTolerance);
            foreach (Chain chain in free)
                AssignChain(clusters, chain, cls, alignTolerance, overlapTolerance);
            return clusters;
        }

        private static bool CanJoin(Cluster cluster, Chain chain, double alignTolerance, double overlapTolerance)
        {
            if (chain.HasPin && cluster.HasPin && Math.Abs(chain.Pin - cluster.Pin) > PinEquality)
                return false;

            double min = chain.Effective;
            double max = chain.Effective;
            foreach (Chain member in cluster.Members)
            {
                double overlap = Math.Min(member.Hi, chain.Hi) - Math.Max(member.Lo, chain.Lo);
                if (overlap > overlapTolerance)
                    return false;
                min = Math.Min(min, member.Effective);
                max = Math.Max(max, member.Effective);
            }

            return max - min <= alignTolerance + 1e-9;
        }

        private static void AssignChain(
            List<Cluster> clusters, Chain chain, int cls, double alignTolerance, double overlapTolerance)
        {
            Cluster best = null;
            double bestDifference = double.MaxValue;
            foreach (Cluster cluster in clusters)
            {
                if (!CanJoin(cluster, chain, alignTolerance, overlapTolerance))
                    continue;
                double difference = Math.Abs(cluster.Coord - chain.Effective);
                if (difference < bestDifference)
                {
                    bestDifference = difference;
                    best = cluster;
                }
            }

            if (best == null)
            {
                best = new Cluster { Class = cls };
                clusters.Add(best);
            }

            best.Members.Add(chain);
            chain.Cluster = best;
            if (chain.HasPin && !best.HasPin)
            {
                best.HasPin = true;
                best.Pin = chain.Pin;
            }

            best.Recompute();
        }

        // ------------------------------------------------------------------------------------------

        private sealed class CNode
        {
            public int Id;
            public Vec2 P;
            public Vec2 Orig;
            public bool Anchor;
            public bool Dead;
            public int MergedInto = -1;
            public readonly List<int> Edges = new List<int>();
        }

        private sealed class CEdge
        {
            public int Id;
            public int A;
            public int B;
            public string Layer;
            public int Curve;
            public readonly HashSet<int> Src = new HashSet<int>();
            public bool Dead;
            public int Class = Other;
            public int Chain = -1;

            public int OtherNode(int node)
            {
                return node == A ? B : A;
            }
        }

        internal sealed class Chain
        {
            public int Id;
            public int Class;
            public readonly List<int> Edges = new List<int>();
            public readonly HashSet<int> Nodes = new HashSet<int>();
            public double Mean;
            public double Weight;
            public double Lo;
            public double Hi;
            public bool HasPin;
            public double Pin;
            public Cluster Cluster;

            public double Effective
            {
                get { return HasPin ? Pin : Mean; }
            }
        }

        internal sealed class Cluster
        {
            public int Id;
            public int Class;
            public readonly List<Chain> Members = new List<Chain>();
            public bool HasPin;
            public double Pin;
            public double Coord;

            public void Recompute()
            {
                if (HasPin)
                {
                    Coord = Pin;
                    return;
                }

                double sum = 0;
                double weight = 0;
                foreach (Chain chain in Members)
                {
                    sum += chain.Mean * chain.Weight;
                    weight += chain.Weight;
                }

                Coord = weight > 0 ? sum / weight : Members[0].Mean;
            }
        }

        private sealed class InitialEdge
        {
            public int A;
            public int B;
            public Vec2 P;
            public Vec2 Q;
        }

        private struct Conflict
        {
            public int X;
            public int Y;
            public Vec2 At;
        }

        // Edge classes against the base angle.
        private const int Horizontal = 0;
        private const int Vertical = 1;
        private const int Diagonal45 = 2;
        private const int Diagonal135 = 3;
        private const int Other = 4;

        private const double ZeroLength = 1e-3;
        private const double CollinearDeviation = 0.01;
        private const double PinEquality = 1e-6;
        private const double AnchorMoveTolerance = 1e-5;
        private const double ConflictDistance = 1e-6;

        private sealed class Engine
        {
            private readonly RefrigInput _input;
            private readonly RefrigResult _result;
            private readonly RefrigSettings _settings;
            private readonly double _angleTolerance;
            private readonly List<CNode> _nodes = new List<CNode>();
            private readonly List<CEdge> _edges = new List<CEdge>();
            private readonly List<InitialEdge> _initial = new List<InitialEdge>();
            private readonly List<Chain> _chains = new List<Chain>();
            private readonly List<Cluster> _clusters = new List<Cluster>();
            private int _root = -1;
            private int _initialComponents;

            public Engine(RefrigInput input, RefrigResult result)
            {
                _input = input;
                _result = result;
                _settings = input.Settings;
                _angleTolerance = _settings.AngleToleranceDegrees * Math.PI / 180.0;
            }

            private bool Fail(string message, params Vec2[] points)
            {
                _result.Ok = false;
                _result.Error = message;
                _result.ErrorPoints.AddRange(points);
                return false;
            }

            private static string Num(double value)
            {
                return value.ToString("0.#", CultureInfo.InvariantCulture);
            }

            private static string At(Vec2 point)
            {
                return "(" + Num(point.X) + ", " + Num(point.Y) + ")";
            }

            public void Execute()
            {
                string settingsError;
                if (!_settings.IsValid(out settingsError))
                {
                    Fail(settingsError);
                    return;
                }

                if (_input.Curves.Count == 0)
                {
                    Fail("There are no lines or polylines to clean.");
                    return;
                }

                foreach (PipeCurve curve in _input.Curves)
                {
                    if (curve.HasArcs)
                    {
                        Fail("A polyline has arc segments. HVACREFRIG handles straight segments only; "
                            + "explode or redraw the arcs first.", curve.SegmentStart(0));
                        return;
                    }
                }

                if (!BuildGraph())
                    return;

                ClassifyEdges();
                BuildChains();
                BuildClusters();
                Solve();
                MergeJogs();
                RemoveZeroLength();
                DissolveCollinear();

                if (!Validate())
                    return;

                _result.Ok = true;
                _result.Components = _initialComponents;
                CountMoved();

                if (_input.RootPoint.HasValue)
                {
                    // Anchors never move, so the root is still the pipe end that was found.
                    _result.HasRoot = true;
                    _result.Root = _nodes[Resolve(_root)].P;
                    PlaceRefnets();
                }

                BuildPaths();
                _result.Changed = _weldMoved
                    || _result.NodesMoved > 0
                    || _result.DuplicatesRemoved > 0
                    || _result.ZeroLengthRemoved > 0
                    || _result.NodesDissolved > 0
                    || _result.JogsMerged > 0
                    || _result.Refnets.Count > 0;
            }

            // ------------------------------------------------------------------ 1-2. weld and T nodes

            private bool _weldMoved;

            private bool BuildGraph()
            {
                PipeNetwork net = PipeNetwork.Build(_input.Curves, _settings.WeldTolerance);
                foreach (NetworkNode source in net.Nodes)
                {
                    Vec2 p = source.IsTNode ? source.HostPoint : source.Point;
                    _nodes.Add(new CNode { Id = source.Index, P = p, Orig = p });
                }

                Dictionary<long, int> byPair = new Dictionary<long, int>();
                foreach (NetworkEdge source in net.Edges)
                {
                    int a = Math.Min(source.NodeA, source.NodeB);
                    int b = Math.Max(source.NodeA, source.NodeB);
                    long key = ((long)a << 32) | (uint)b;
                    int existing;
                    if (byPair.TryGetValue(key, out existing))
                    {
                        // A pipe drawn twice over the same two nodes.
                        _result.DuplicatesRemoved++;
                        continue;
                    }

                    CEdge edge = new CEdge
                    {
                        Id = _edges.Count,
                        A = source.NodeA,
                        B = source.NodeB,
                        Layer = source.Layer,
                        Curve = source.Curve
                    };
                    edge.Src.Add(edge.Id);
                    byPair[key] = edge.Id;
                    _edges.Add(edge);
                    _nodes[edge.A].Edges.Add(edge.Id);
                    _nodes[edge.B].Edges.Add(edge.Id);
                    _initial.Add(new InitialEdge
                    {
                        A = edge.A,
                        B = edge.B,
                        P = _nodes[edge.A].P,
                        Q = _nodes[edge.B].P
                    });

                    if (source.PointA.DistanceTo(_nodes[edge.A].P) > 1e-6
                        || source.PointB.DistanceTo(_nodes[edge.B].P) > 1e-6)
                        _weldMoved = true;
                }

                if (_edges.Count == 0)
                    return Fail("There are no pipe segments to clean.");

                int[] parent = NewParents(_nodes.Count);
                foreach (CEdge edge in _edges)
                {
                    int ra = FindRoot(parent, edge.A);
                    int rb = FindRoot(parent, edge.B);
                    if (ra == rb)
                    {
                        return Fail(
                            "The network has a loop near " + At(_nodes[edge.A].P)
                            + ". HVACREFRIG expects a tree: break the loop first.",
                            _nodes[edge.A].P);
                    }

                    parent[rb] = ra;
                }

                _initialComponents = CountComponents();

                foreach (CNode node in _nodes)
                {
                    if (node.Edges.Count == 1)
                    {
                        node.Anchor = true;
                        _result.Anchors++;
                        _anchorIds.Add(node.Id);
                        _anchorOrig.Add(node.P);
                    }
                    else if (node.Edges.Count == 0)
                    {
                        node.Dead = true;
                    }
                }

                if (_input.RootPoint.HasValue)
                {
                    Vec2 pick = _input.RootPoint.Value;
                    double best = double.MaxValue;
                    foreach (CNode node in _nodes)
                    {
                        if (!node.Anchor)
                            continue;
                        double distance = node.P.DistanceTo(pick);
                        if (distance < best)
                        {
                            best = distance;
                            _root = node.Id;
                        }
                    }

                    if (_root < 0 || best > _input.RootTolerance)
                    {
                        _root = -1;
                        return Fail(
                            "No pipe end lies within " + Num(_input.RootTolerance)
                            + " mm of the picked root point. Pick the outdoor-unit end of the network.");
                    }
                }

                return true;
            }

            private static int[] NewParents(int count)
            {
                int[] parent = new int[count];
                for (int i = 0; i < count; i++)
                    parent[i] = i;
                return parent;
            }

            private static int FindRoot(int[] parent, int i)
            {
                while (parent[i] != i)
                {
                    parent[i] = parent[parent[i]];
                    i = parent[i];
                }

                return i;
            }

            private int CountComponents()
            {
                int[] parent = NewParents(_nodes.Count);
                foreach (CEdge edge in _edges)
                {
                    if (edge.Dead)
                        continue;
                    int ra = FindRoot(parent, edge.A);
                    int rb = FindRoot(parent, edge.B);
                    if (ra != rb)
                        parent[rb] = ra;
                }

                HashSet<int> roots = new HashSet<int>();
                foreach (CNode node in _nodes)
                {
                    if (!node.Dead && node.Edges.Count > 0)
                        roots.Add(FindRoot(parent, node.Id));
                }

                return roots.Count;
            }

            // ------------------------------------------------------------------ 3. classify

            private Vec2 Direction(int cls)
            {
                double[] offsets = { 0, Math.PI / 2, Math.PI / 4, 3 * Math.PI / 4 };
                return Vec2.FromAngle(_input.BaseAngle + offsets[cls]);
            }

            private Vec2 Normal(int cls)
            {
                return Direction(cls).Left;
            }

            private static int Perpendicular(int cls)
            {
                return cls < 2 ? 1 - cls : 5 - cls;
            }

            private int Classify(Vec2 from, Vec2 to)
            {
                Vec2 v = to - from;
                if (v.Length < 1e-9)
                    return Other;

                double phi = Math.Atan2(v.Y, v.X) - _input.BaseAngle;
                phi = phi % Math.PI;
                if (phi < 0)
                    phi += Math.PI;

                double tolerance = _angleTolerance;
                if (phi <= tolerance || phi >= Math.PI - tolerance)
                    return Horizontal;
                if (Math.Abs(phi - Math.PI / 2) <= tolerance)
                    return Vertical;
                if (Math.Abs(phi - Math.PI / 4) <= tolerance)
                    return Diagonal45;
                if (Math.Abs(phi - 3 * Math.PI / 4) <= tolerance)
                    return Diagonal135;
                return Other;
            }

            private void ClassifyEdges()
            {
                foreach (CEdge edge in _edges)
                    edge.Class = Classify(_nodes[edge.A].P, _nodes[edge.B].P);
            }

            // ------------------------------------------------------------------ 4. chains

            private double EdgeLength(CEdge edge)
            {
                return _nodes[edge.A].P.DistanceTo(_nodes[edge.B].P);
            }

            private void BuildChains()
            {
                int[] parent = NewParents(_edges.Count);
                foreach (CNode node in _nodes)
                {
                    if (node.Dead || node.Edges.Count != 2)
                        continue;
                    CEdge first = _edges[node.Edges[0]];
                    CEdge second = _edges[node.Edges[1]];
                    if (first.Id == second.Id || first.Class == Other || first.Class != second.Class)
                        continue;
                    int ra = FindRoot(parent, first.Id);
                    int rb = FindRoot(parent, second.Id);
                    if (ra != rb)
                        parent[rb] = ra;
                }

                Dictionary<int, Chain> byRoot = new Dictionary<int, Chain>();
                foreach (CEdge edge in _edges)
                {
                    if (edge.Class == Other)
                        continue;
                    int root = FindRoot(parent, edge.Id);
                    Chain chain;
                    if (!byRoot.TryGetValue(root, out chain))
                    {
                        chain = new Chain { Id = _chains.Count, Class = edge.Class };
                        byRoot[root] = chain;
                        _chains.Add(chain);
                    }

                    chain.Edges.Add(edge.Id);
                    chain.Nodes.Add(edge.A);
                    chain.Nodes.Add(edge.B);
                }

                foreach (Chain chain in _chains)
                {
                    Vec2 normal = Normal(chain.Class);
                    Vec2 direction = Direction(chain.Class);
                    double sum = 0;
                    foreach (int id in chain.Edges)
                    {
                        CEdge edge = _edges[id];
                        double length = EdgeLength(edge);
                        Vec2 mid = (_nodes[edge.A].P + _nodes[edge.B].P) / 2;
                        sum += normal.Dot(mid) * length;
                        chain.Weight += length;
                    }

                    if (chain.Weight > 0)
                    {
                        chain.Mean = sum / chain.Weight;
                    }
                    else
                    {
                        CEdge only = _edges[chain.Edges[0]];
                        chain.Mean = normal.Dot(_nodes[only.A].P);
                    }

                    chain.Lo = double.MaxValue;
                    chain.Hi = double.MinValue;
                    double pinMin = double.MaxValue;
                    double pinMax = double.MinValue;
                    foreach (int nodeId in chain.Nodes)
                    {
                        CNode node = _nodes[nodeId];
                        double along = direction.Dot(node.P);
                        chain.Lo = Math.Min(chain.Lo, along);
                        chain.Hi = Math.Max(chain.Hi, along);
                        if (node.Anchor)
                        {
                            double value = normal.Dot(node.P);
                            pinMin = Math.Min(pinMin, value);
                            pinMax = Math.Max(pinMax, value);
                        }
                    }

                    if (pinMax >= pinMin)
                    {
                        if (pinMax - pinMin > PinEquality)
                        {
                            // Anchored at both ends off the line: it cannot be made straight without
                            // moving an anchor, so it stays as drawn.
                            foreach (int id in chain.Edges)
                                _edges[id].Class = Other;
                            chain.Edges.Clear();
                            continue;
                        }

                        chain.HasPin = true;
                        chain.Pin = pinMin;
                    }
                }

                _chains.RemoveAll(c => c.Edges.Count == 0);
                for (int i = 0; i < _chains.Count; i++)
                {
                    _chains[i].Id = i;
                    foreach (int id in _chains[i].Edges)
                        _edges[id].Chain = i;
                }
            }

            // ------------------------------------------------------------------ 5. clusters

            private void BuildClusters()
            {
                for (int cls = 0; cls < 4; cls++)
                {
                    List<Chain> mine = _chains.FindAll(chain => chain.Class == cls);
                    foreach (Cluster cluster in ClusterChains(
                        mine, cls, _settings.AlignTolerance, _settings.WeldTolerance))
                    {
                        cluster.Id = _clusters.Count;
                        _clusters.Add(cluster);
                    }
                }
            }

            // ------------------------------------------------------------------ node positions

            /// <summary>
            /// Moves each node onto the lines of the clusters its edges belong to: the intersection for
            /// two directions, or a projection onto the line for one. Nodes with no clustered edge stay.
            /// </summary>
            private void Solve()
            {
                foreach (CNode node in _nodes)
                {
                    if (node.Dead)
                        continue;

                    List<Cluster> constraints = new List<Cluster>();
                    foreach (int id in node.Edges)
                    {
                        CEdge edge = _edges[id];
                        if (edge.Chain < 0)
                            continue;
                        Cluster cluster = _chains[edge.Chain].Cluster;
                        if (!constraints.Contains(cluster))
                            constraints.Add(cluster);
                    }

                    if (constraints.Count == 0)
                        continue;

                    double a11 = 0;
                    double a12 = 0;
                    double a22 = 0;
                    double b1 = 0;
                    double b2 = 0;
                    foreach (Cluster cluster in constraints)
                    {
                        Vec2 n = Normal(cluster.Class);
                        a11 += n.X * n.X;
                        a12 += n.X * n.Y;
                        a22 += n.Y * n.Y;
                        b1 += cluster.Coord * n.X;
                        b2 += cluster.Coord * n.Y;
                    }

                    double determinant = a11 * a22 - a12 * a12;
                    if (determinant > 1e-6)
                    {
                        node.P = new Vec2(
                            (a22 * b1 - a12 * b2) / determinant,
                            (a11 * b2 - a12 * b1) / determinant);
                    }
                    else
                    {
                        // Parallel lines only: slide the node across them to their mean.
                        Vec2 n = Normal(constraints[0].Class);
                        double mean = 0;
                        foreach (Cluster cluster in constraints)
                            mean += cluster.Coord;
                        mean /= constraints.Count;
                        node.P = node.P + n * (mean - n.Dot(node.P));
                    }
                }
            }

            // ------------------------------------------------------------------ 6. jogs

            private CEdge OtherEdgeAt(CNode node, CEdge edge)
            {
                return _edges[node.Edges[0] == edge.Id ? node.Edges[1] : node.Edges[0]];
            }

            private void MergeJogs()
            {
                for (int pass = 0; pass < 10; pass++)
                {
                    bool merged = false;
                    foreach (CEdge jog in _edges)
                    {
                        if (jog.Dead)
                            continue;
                        double length = EdgeLength(jog);
                        if (length <= ZeroLength || length >= _settings.JogTolerance)
                            continue;

                        CNode na = _nodes[jog.A];
                        CNode nb = _nodes[jog.B];
                        if (na.Edges.Count != 2 || nb.Edges.Count != 2)
                            continue;

                        CEdge before = OtherEdgeAt(na, jog);
                        CEdge after = OtherEdgeAt(nb, jog);
                        if (before.Id == after.Id || before.Chain < 0 || after.Chain < 0
                            || before.Class != after.Class)
                            continue;
                        if (Classify(na.P, nb.P) != Perpendicular(before.Class))
                            continue;

                        Cluster c1 = _chains[before.Chain].Cluster;
                        Cluster c2 = _chains[after.Chain].Cluster;
                        if (c1 == c2)
                            continue;
                        if (c1.HasPin && c2.HasPin && Math.Abs(c1.Pin - c2.Pin) > PinEquality)
                            continue;

                        foreach (Chain chain in c2.Members)
                        {
                            chain.Cluster = c1;
                            c1.Members.Add(chain);
                        }

                        if (c2.HasPin && !c1.HasPin)
                        {
                            c1.HasPin = true;
                            c1.Pin = c2.Pin;
                        }

                        c2.Members.Clear();
                        _clusters.Remove(c2);
                        c1.Recompute();
                        _result.JogsMerged++;
                        merged = true;
                    }

                    if (!merged)
                        break;
                    Solve();
                }
            }

            // ------------------------------------------------------------------ 7. zero-length edges

            private void KillEdge(CEdge edge)
            {
                edge.Dead = true;
                _nodes[edge.A].Edges.Remove(edge.Id);
                _nodes[edge.B].Edges.Remove(edge.Id);
            }

            private void RemoveZeroLength()
            {
                bool again = true;
                while (again)
                {
                    again = false;
                    foreach (CEdge edge in _edges)
                    {
                        if (edge.Dead || EdgeLength(edge) > ZeroLength)
                            continue;
                        Contract(edge);
                        _result.ZeroLengthRemoved++;
                        again = true;
                        break;
                    }
                }
            }

            private void Contract(CEdge edge)
            {
                CNode a = _nodes[edge.A];
                CNode b = _nodes[edge.B];
                if (b.Anchor && !a.Anchor)
                {
                    CNode swap = a;
                    a = b;
                    b = swap;
                }

                KillEdge(edge);
                b.Dead = true;
                b.MergedInto = a.Id;
                if (b.Anchor)
                    a.Anchor = true;
                if (_root == b.Id)
                    _root = a.Id;

                foreach (int id in new List<int>(b.Edges))
                {
                    CEdge other = _edges[id];
                    if (other.A == b.Id)
                        other.A = a.Id;
                    if (other.B == b.Id)
                        other.B = a.Id;
                    a.Edges.Add(id);
                }

                b.Edges.Clear();

                // The merge can leave two pipes between the same nodes, or a pipe from a node to itself.
                List<int> list = new List<int>(a.Edges);
                for (int i = 0; i < list.Count; i++)
                {
                    CEdge first = _edges[list[i]];
                    if (first.Dead)
                        continue;
                    if (first.A == first.B)
                    {
                        KillEdge(first);
                        continue;
                    }

                    for (int j = i + 1; j < list.Count; j++)
                    {
                        CEdge second = _edges[list[j]];
                        if (second.Dead)
                            continue;
                        bool same = (first.A == second.A && first.B == second.B)
                            || (first.A == second.B && first.B == second.A);
                        if (!same)
                            continue;
                        first.Src.UnionWith(second.Src);
                        KillEdge(second);
                    }
                }
            }

            // ------------------------------------------------------------------ 8. collinear nodes

            private void DissolveCollinear()
            {
                bool again = true;
                while (again)
                {
                    again = false;
                    foreach (CNode node in _nodes)
                    {
                        if (node.Dead || node.Anchor || node.Edges.Count != 2 || node.Id == _root)
                            continue;

                        CEdge first = _edges[node.Edges[0]];
                        CEdge second = _edges[node.Edges[1]];
                        if (first.Id == second.Id
                            || !string.Equals(first.Layer, second.Layer, StringComparison.OrdinalIgnoreCase))
                            continue;

                        int u = first.OtherNode(node.Id);
                        int v = second.OtherNode(node.Id);
                        if (u == v)
                            continue;

                        Vec2 pu = _nodes[u].P;
                        Vec2 pv = _nodes[v].P;
                        Vec2 span = pv - pu;
                        double spanLength = span.Length;
                        if (spanLength < 1e-9)
                            continue;
                        double deviation = Math.Abs(span.Cross(node.P - pu)) / spanLength;
                        if (deviation > CollinearDeviation
                            || (pu - node.P).Dot(pv - node.P) >= 0)
                            continue;

                        // Merge the two edges into the first one, from u to v.
                        double firstLength = EdgeLength(first);
                        double secondLength = EdgeLength(second);
                        if (secondLength > firstLength)
                            first.Curve = second.Curve;
                        first.Src.UnionWith(second.Src);
                        second.Dead = true;
                        _nodes[v].Edges.Remove(second.Id);
                        _nodes[v].Edges.Add(first.Id);
                        node.Edges.Clear();
                        node.Dead = true;
                        first.A = u;
                        first.B = v;
                        _result.NodesDissolved++;
                        again = true;
                    }
                }
            }

            // ------------------------------------------------------------------ validation

            private bool Validate()
            {
                // Same number of connected components.
                int components = CountComponents();
                if (components != _initialComponents)
                {
                    return Fail(
                        "Cleaning would change the number of separate pipe networks from "
                        + _initialComponents + " to " + components + ". Nothing was changed.");
                }

                // Still a tree.
                int[] parent = NewParents(_nodes.Count);
                foreach (CEdge edge in _edges)
                {
                    if (edge.Dead)
                        continue;
                    int ra = FindRoot(parent, edge.A);
                    int rb = FindRoot(parent, edge.B);
                    if (ra == rb)
                    {
                        return Fail(
                            "Cleaning would create a loop near " + At(_nodes[edge.A].P) + ". Nothing was changed.",
                            _nodes[edge.A].P);
                    }

                    parent[rb] = ra;
                }

                // Anchors unmoved.
                for (int i = 0; i < _anchorIds.Count; i++)
                {
                    CNode survivor = _nodes[Resolve(_anchorIds[i])];
                    Vec2 orig = _anchorOrig[i];
                    if (survivor.P.DistanceTo(orig) > AnchorMoveTolerance)
                    {
                        return Fail(
                            "Cleaning would move the pipe end at " + At(orig) + ". Nothing was changed.",
                            orig, survivor.P);
                    }
                }

                // No new crossings or overlaps.
                HashSet<long> old = new HashSet<long>();
                List<Conflict> before = FindConflicts(InitialGeometry());
                foreach (Conflict conflict in before)
                    old.Add(PairKey(conflict.X, conflict.Y));

                List<int> live = new List<int>();
                foreach (CEdge edge in _edges)
                {
                    if (!edge.Dead)
                        live.Add(edge.Id);
                }

                List<Conflict> after = FindConflicts(LiveGeometry(live));
                foreach (Conflict conflict in after)
                {
                    CEdge x = _edges[conflict.X];
                    CEdge y = _edges[conflict.Y];
                    bool existed = false;
                    foreach (int sx in x.Src)
                    {
                        foreach (int sy in y.Src)
                        {
                            if (old.Contains(PairKey(sx, sy)))
                            {
                                existed = true;
                                break;
                            }
                        }

                        if (existed)
                            break;
                    }

                    if (!existed)
                    {
                        return Fail(
                            "Cleaning would create a new crossing or overlap between pipes near "
                            + At(conflict.At) + ". Nothing was changed.",
                            conflict.At);
                    }
                }

                return true;
            }

            private readonly List<int> _anchorIds = new List<int>();
            private readonly List<Vec2> _anchorOrig = new List<Vec2>();

            private int Resolve(int node)
            {
                while (_nodes[node].MergedInto >= 0)
                    node = _nodes[node].MergedInto;
                return node;
            }

            private static long PairKey(int a, int b)
            {
                int lo = Math.Min(a, b);
                int hi = Math.Max(a, b);
                return ((long)lo << 32) | (uint)hi;
            }

            private sealed class Geometry
            {
                public int Id;
                public int A;
                public int B;
                public Vec2 P;
                public Vec2 Q;
            }

            private List<Geometry> InitialGeometry()
            {
                List<Geometry> list = new List<Geometry>();
                for (int i = 0; i < _initial.Count; i++)
                {
                    list.Add(new Geometry
                    {
                        Id = i,
                        A = _initial[i].A,
                        B = _initial[i].B,
                        P = _initial[i].P,
                        Q = _initial[i].Q
                    });
                }

                return list;
            }

            private List<Geometry> LiveGeometry(List<int> ids)
            {
                List<Geometry> list = new List<Geometry>();
                foreach (int id in ids)
                {
                    CEdge edge = _edges[id];
                    list.Add(new Geometry
                    {
                        Id = id,
                        A = edge.A,
                        B = edge.B,
                        P = _nodes[edge.A].P,
                        Q = _nodes[edge.B].P
                    });
                }

                return list;
            }

            // Pairs of pipes that cross, touch or overlap without sharing a node, plus pairs that share a
            // node but fold back over each other. A sweep over the x extents keeps this fast.
            private static List<Conflict> FindConflicts(List<Geometry> geometry)
            {
                List<Conflict> found = new List<Conflict>();
                geometry.Sort((p, q) =>
                    Math.Min(p.P.X, p.Q.X).CompareTo(Math.Min(q.P.X, q.Q.X)));

                for (int i = 0; i < geometry.Count; i++)
                {
                    Geometry g = geometry[i];
                    double maxX = Math.Max(g.P.X, g.Q.X) + ConflictDistance;
                    double minY = Math.Min(g.P.Y, g.Q.Y) - ConflictDistance;
                    double maxY = Math.Max(g.P.Y, g.Q.Y) + ConflictDistance;
                    for (int j = i + 1; j < geometry.Count; j++)
                    {
                        Geometry h = geometry[j];
                        if (Math.Min(h.P.X, h.Q.X) > maxX)
                            break;
                        if (Math.Max(h.P.Y, h.Q.Y) < minY || Math.Min(h.P.Y, h.Q.Y) > maxY)
                            continue;

                        Vec2 at;
                        if (Conflicts(g, h, out at))
                            found.Add(new Conflict { X = g.Id, Y = h.Id, At = at });
                    }
                }

                return found;
            }

            private static bool Conflicts(Geometry g, Geometry h, out Vec2 at)
            {
                at = g.P;
                int shared = -1;
                Vec2 farG = g.Q;
                Vec2 farH = h.Q;
                Vec2 common = g.P;
                if (g.A == h.A) { shared = g.A; common = g.P; farG = g.Q; farH = h.Q; }
                else if (g.A == h.B) { shared = g.A; common = g.P; farG = g.Q; farH = h.P; }
                else if (g.B == h.A) { shared = g.B; common = g.Q; farG = g.P; farH = h.Q; }
                else if (g.B == h.B) { shared = g.B; common = g.Q; farG = g.P; farH = h.P; }

                if (shared >= 0)
                {
                    Vec2 d1 = (farG - common).Normalized();
                    Vec2 d2 = (farH - common).Normalized();
                    at = common;
                    return d1.Dot(d2) > 0 && Math.Abs(d1.Cross(d2)) < 1e-6;
                }

                return SegmentDistance(g.P, g.Q, h.P, h.Q, out at) <= ConflictDistance;
            }

            // ------------------------------------------------------------------ moved nodes

            private void CountMoved()
            {
                foreach (CNode node in _nodes)
                {
                    if (node.Dead || node.Edges.Count == 0)
                        continue;
                    if (node.P.DistanceTo(node.Orig) > 1e-6)
                        _result.NodesMoved++;
                }
            }

            // ------------------------------------------------------------------ refnets
            //
            // A refnet is set back from its T node N by D, the set-back (at least OutletStraight, R2).
            // With d the flow direction into N, n the unit normal toward the branch and s the scale:
            //   main outlet   M  = N - D d              inlet        I  = N - (D + 750 s) d
            //   branch outlet B1 = M + 200 s n          branch turn  B2 = N + 200 s n
            // The upstream pipe is cut at I, the through pipe runs from M straight on through N (N stops
            // being a node), a new D-long branch pipe runs from B1 to B2 on the branch's layer, and the
            // original branch pipe starts at B2.
            //
            // The set-backs are chosen on a plan first (Analyse and Solve change nothing), then applied.

            private const double StripClearance = 1.0;
            private const double BendSine = 0.01745240643728351; // sin(1 degree): a flatter turn is not a bend

            /// <summary>One refnet to place.</summary>
            private sealed class Plan
            {
                public int Node;
                public Vec2 N;
                public CEdge Up;
                public CEdge Through;
                public CEdge Branch;
                public Vec2 Direction;
                public Vec2 Normal;
                public bool Mirrored;
                public double Body;
                public double Offset;
                /// <summary>The nearest refnet upstream, or null; <see cref="PredThrough"/> is true when the pipe leaves its main outlet.</summary>
                public Plan Pred;
                public bool PredThrough;
                /// <summary>Pipe length from the predecessor's node N (less 200 s on its branch side) to this node.</summary>
                public double PredPath;
                /// <summary>True when that pipe is straight, with no bend or junction, from the predecessor's main outlet.</summary>
                public bool PredStraight;
                /// <summary>The predecessor when this refnet's last upstream pipe starts at the predecessor's node.</summary>
                public Plan Adjacent;
                public bool AdjacentThrough;
                /// <summary>The greatest set-back that keeps the inlet on the upstream pipe and the branch pipe clear.</summary>
                public double Hi;
                public double D;
                public readonly List<Plan> Next = new List<Plan>();
                public int InletNode;
                public int MainNode;
                public int BranchNode;
            }

            /// <summary>How the pipe reaches a node from the refnet upstream of it.</summary>
            private sealed class Trail
            {
                public Plan Pred;
                public bool Through;
                public double Path;
                public bool Straight;
            }

            // Limits found by the clash checks, kept between passes: the greatest set-back of a refnet's
            // node, and the nodes whose refnet cannot be placed at all.
            private readonly Dictionary<int, double> _stripLimit = new Dictionary<int, double>();
            private readonly HashSet<int> _stripForced = new HashSet<int>();

            private int AddNode(Vec2 point)
            {
                CNode node = new CNode { Id = _nodes.Count, P = point, Orig = point };
                _nodes.Add(node);
                return node.Id;
            }

            private CEdge AddEdge(int a, int b, string layer, int curve)
            {
                CEdge edge = new CEdge { Id = _edges.Count, A = a, B = b, Layer = layer, Curve = curve };
                _edges.Add(edge);
                _nodes[a].Edges.Add(edge.Id);
                _nodes[b].Edges.Add(edge.Id);
                return edge;
            }

            private void Report(string message, Vec2 point)
            {
                _result.Reports.Add(message);
                _result.ReportPoints.Add(point);
            }

            private void PlaceRefnets()
            {
                int root = Resolve(_root);

                // Breadth-first from the root: each node's parent edge is the upstream pipe.
                Dictionary<int, int> parentEdge = new Dictionary<int, int>();
                List<int> order = new List<int>();
                Queue<int> queue = new Queue<int>();
                parentEdge[root] = -1;
                queue.Enqueue(root);
                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    order.Add(current);
                    foreach (int id in _nodes[current].Edges)
                    {
                        int next = _edges[id].OtherNode(current);
                        if (!parentEdge.ContainsKey(next))
                        {
                            parentEdge[next] = id;
                            queue.Enqueue(next);
                        }
                    }
                }

                int networks = CountComponents();
                if (networks > 1)
                {
                    Report(
                        (networks - 1) + " other pipe network(s) are not connected to the root; no refnets "
                        + "were placed on them.",
                        _nodes[root].P);
                }

                // Choose the set-backs. A branch pipe that clashes with another new branch pipe lowers that
                // refnet's limit (or drops it) and the plan is made again; each pass lowers a limit by at
                // least 1 mm or drops a refnet, so this ends. After many passes the clashing refnet is dropped.
                List<Plan> plans;
                List<string> messages = new List<string>();
                List<Vec2> points = new List<Vec2>();
                for (int pass = 0; ; pass++)
                {
                    messages.Clear();
                    points.Clear();
                    plans = Analyse(order, parentEdge, root, messages, points);
                    Solve(plans);
                    if (!SeparateStrips(plans, pass >= 500))
                        break;
                }

                for (int i = 0; i < messages.Count; i++)
                    Report(messages[i], points[i]);

                ApplyPlans(plans);
                EvaluateRules(plans);
            }

            // Walks the tree from the root and makes a plan for every junction that gets a refnet. The graph
            // is not changed. Cases that get no refnet go into messages.
            private List<Plan> Analyse(
                List<int> order, Dictionary<int, int> parentEdge, int root, List<string> messages, List<Vec2> points)
            {
                List<Plan> plans = new List<Plan>();
                Dictionary<int, Plan> byNode = new Dictionary<int, Plan>();
                Dictionary<int, Trail> trails = new Dictionary<int, Trail>();
                trails[root] = new Trail();

                foreach (int id in order)
                {
                    CNode node = _nodes[id];
                    Trail here = trails[id];
                    int upId = parentEdge[id];
                    Plan plan = null;
                    if (node.Edges.Count >= 3)
                    {
                        if (upId < 0)
                        {
                            messages.Add("The root at " + At(node.P) + " joins " + node.Edges.Count
                                + " pipes: no refnet placed there.");
                            points.Add(node.P);
                        }
                        else
                        {
                            plan = TryPlan(id, _edges[upId], here, byNode, messages, points);
                        }

                        if (plan != null)
                        {
                            plans.Add(plan);
                            byNode[id] = plan;
                            if (plan.Pred != null)
                                plan.Pred.Next.Add(plan);
                        }
                    }

                    foreach (int edgeId in node.Edges)
                    {
                        if (edgeId == upId)
                            continue;

                        CEdge edge = _edges[edgeId];
                        double length = EdgeLength(edge);
                        Trail trail = new Trail();
                        if (plan != null)
                        {
                            trail.Pred = plan;
                            trail.Through = edge.Id == plan.Through.Id;
                            trail.Path = trail.Through ? length : length - plan.Offset;
                            trail.Straight = trail.Through;
                        }
                        else
                        {
                            trail.Pred = here.Pred;
                            trail.Through = here.Through;
                            trail.Path = here.Path + length;
                            trail.Straight = here.Straight && upId >= 0 && node.Edges.Count == 2
                                && !IsBend(
                                    _nodes[_edges[upId].OtherNode(id)].P,
                                    node.P,
                                    _nodes[edge.OtherNode(id)].P);
                        }

                        trails[edge.OtherNode(id)] = trail;
                    }
                }

                return plans;
            }

            private static void Note(List<string> messages, List<Vec2> points, string text, Vec2 point)
            {
                messages.Add(text);
                points.Add(point);
            }

            private Plan TryPlan(
                int id, CEdge up, Trail here, Dictionary<int, Plan> byNode, List<string> messages, List<Vec2> points)
            {
                CNode node = _nodes[id];
                Vec2 n = node.P;
                int degree = node.Edges.Count;
                double body = RefrigSettings.RefnetLength * _settings.RefnetScale;
                double offset = RefrigSettings.RefnetBranchOffset * _settings.RefnetScale;
                double minSetBack = _settings.OutletStraight;

                if (degree >= 4)
                {
                    Note(messages, points, "Degree-" + degree + " junction at " + At(n) + ": no refnet placed.", n);
                    return null;
                }

                if (ExistingRefnetAt(n))
                {
                    Note(messages, points, "A " + RefrigSettings.RefnetBlockName + " already exists at " + At(n)
                        + ": left unchanged.", n);
                    return null;
                }

                List<CEdge> children = new List<CEdge>();
                foreach (int edgeId in node.Edges)
                {
                    if (edgeId != up.Id)
                        children.Add(_edges[edgeId]);
                }

                if (children.Count != 2)
                    return null;

                int upNode = up.OtherNode(id);
                Vec2 d = n - _nodes[upNode].P;
                double upstreamLength = d.Length;
                Vec2 direction = d.Normalized();

                Vec2 toFirst = (_nodes[children[0].OtherNode(id)].P - n).Normalized();
                Vec2 toSecond = (_nodes[children[1].OtherNode(id)].P - n).Normalized();
                double dotFirst = direction.Dot(toFirst);
                double dotSecond = direction.Dot(toSecond);
                CEdge through = dotFirst >= dotSecond ? children[0] : children[1];
                CEdge branch = dotFirst >= dotSecond ? children[1] : children[0];
                double throughDot = Math.Max(dotFirst, dotSecond);
                if (throughDot < Math.Cos(_angleTolerance))
                {
                    Note(messages, points, "Bull-head tee at " + At(n)
                        + ": neither branch continues the upstream pipe, so no refnet was placed.", n);
                    return null;
                }

                // The room for the set-back: the upstream pipe, plus the set-back of the refnet just upstream
                // when this refnet's last upstream pipe is that refnet's through pipe (it runs on from that
                // refnet's main outlet, so pushing that refnet back lengthens it). On a branch the pipe
                // starts 200 s along it, after the upstream refnet's branch pipe.
                Plan adjacent;
                byNode.TryGetValue(upNode, out adjacent);
                bool adjacentThrough = adjacent != null && up.Id == adjacent.Through.Id;
                double lastRun = upstreamLength - (adjacent != null && !adjacentThrough ? adjacent.Offset : 0);
                double runHi = lastRun - body + (adjacentThrough ? adjacent.Hi : 0);
                if (runHi < minSetBack - 1e-6)
                {
                    Note(messages, points, "The upstream straight run into " + At(n) + " is at most "
                        + Num(runHi + body) + " mm, shorter than the refnet's " + Num(body)
                        + " mm plus its " + Num(minSetBack) + " mm set-back: no refnet placed.", n);
                    return null;
                }

                Vec2 branchDirection = (_nodes[branch.OtherNode(id)].P - n).Normalized();
                double branchLength = EdgeLength(branch);
                if (branchLength < offset - 1e-6)
                {
                    Note(messages, points, "The branch leg at " + At(n) + " is " + Num(branchLength)
                        + " mm, shorter than " + Num(offset) + " mm: no refnet placed.", n);
                    return null;
                }

                double side = direction.Cross(branchDirection);
                if (Math.Abs(direction.Dot(branchDirection)) > Math.Sin(_angleTolerance))
                {
                    Note(messages, points, "The branch at " + At(n)
                        + " does not leave the main at right angles, so no refnet was placed.", n);
                    return null;
                }

                // The branch is on the left of the flow when side > 0; the block's own branch is on
                // the right, so a left branch mirrors it.
                bool mirrored = side > 0;
                Vec2 normal = direction.Left * (mirrored ? 1 : -1);

                // The new branch pipe must not cross or overlap another pipe, so it limits the set-back.
                double hi = runHi;
                double hit = StripHit(n + normal * offset, -direction, runHi, up, children[0], children[1]);
                if (hit >= 0)
                    hi = Math.Min(hi, hit - StripClearance);
                double limit;
                if (_stripLimit.TryGetValue(id, out limit))
                    hi = Math.Min(hi, limit);
                if (_stripForced.Contains(id))
                    hi = -1;
                if (hi < minSetBack - 1e-6)
                {
                    Note(messages, points, "The branch pipe at " + At(n) + ", set back at least "
                        + Num(minSetBack) + " mm from the junction, would cross or overlap another pipe: "
                        + "no refnet placed.", n);
                    return null;
                }

                return new Plan
                {
                    Node = id,
                    N = n,
                    Up = up,
                    Through = through,
                    Branch = branch,
                    Direction = direction,
                    Normal = normal,
                    Mirrored = mirrored,
                    Body = body,
                    Offset = offset,
                    Pred = here.Pred,
                    PredThrough = here.Through,
                    PredPath = here.Path,
                    PredStraight = here.Straight,
                    Adjacent = adjacent,
                    AdjacentThrough = adjacentThrough,
                    Hi = hi,
                    D = minSetBack
                };
            }

            // Every refnet starts at the smallest set-back. From the leaves back to the root, a refnet is
            // set back far enough that each refnet downstream of it has R1 (and R2, when no bend comes
            // first) of pipe between this refnet's outlet and its inlet:
            //   D_up >= D_down + need + body_down - (pipe length between the two T nodes)
            // capped at the refnet's limit. A refnet that hits its cap leaves the rule broken, and
            // EvaluateRules reports it. The cap keeps each inlet on its own upstream pipe.
            private void Solve(List<Plan> plans)
            {
                for (int i = plans.Count - 1; i >= 0; i--)
                {
                    Plan plan = plans[i];
                    double want = _settings.OutletStraight;
                    foreach (Plan next in plan.Next)
                    {
                        double need = _settings.RefnetSpacing;
                        if (next.PredStraight)
                            need = Math.Max(need, _settings.OutletStraight);
                        want = Math.Max(want, next.D + need + next.Body - next.PredPath);
                    }

                    plan.D = Math.Min(want, plan.Hi);
                }
            }

            // The smallest distance back along the line a + t u (0 <= t <= maxT) at which it touches the
            // segment q1-q2 (within ConflictDistance), or -1.
            private static double FirstHit(Vec2 a, Vec2 u, double maxT, Vec2 q1, Vec2 q2)
            {
                Vec2 e = q2 - q1;
                double length = e.Length;
                Vec2 w = q1 - a;
                if (length < 1e-9)
                {
                    double along = w.Dot(u);
                    if (Math.Abs(u.Cross(w)) <= ConflictDistance
                        && along >= -ConflictDistance && along <= maxT + ConflictDistance)
                        return Math.Max(along, 0);
                    return -1;
                }

                double cross = u.Cross(e);
                if (Math.Abs(cross) > 1e-9 * length)
                {
                    double t = w.Cross(e) / cross;
                    double s = w.Cross(u) / cross;
                    double sTolerance = ConflictDistance / length;
                    if (s >= -sTolerance && s <= 1 + sTolerance
                        && t >= -ConflictDistance && t <= maxT + ConflictDistance)
                        return Math.Max(t, 0);
                    return -1;
                }

                // Parallel: they touch only when they share a line.
                if (Math.Abs(u.Cross(w)) > ConflictDistance)
                    return -1;
                double t1 = w.Dot(u);
                double t2 = (q2 - a).Dot(u);
                double lo = Math.Min(t1, t2);
                double hi = Math.Max(t1, t2);
                if (hi < -ConflictDistance || lo > maxT + ConflictDistance)
                    return -1;
                return Math.Max(lo, 0);
            }

            // How far back from the branch turn a branch pipe can run before it meets a pipe of the
            // drawing, or -1. The refnet's own three pipes are left out.
            private double StripHit(Vec2 turn, Vec2 back, double maxT, CEdge a, CEdge b, CEdge c)
            {
                double best = -1;
                foreach (CEdge edge in _edges)
                {
                    if (edge.Dead || edge.Id == a.Id || edge.Id == b.Id || edge.Id == c.Id)
                        continue;
                    double t = FirstHit(turn, back, maxT, _nodes[edge.A].P, _nodes[edge.B].P);
                    if (t >= 0 && (best < 0 || t < best))
                        best = t;
                }

                return best;
            }

            // New branch pipes must not cross or overlap each other either. On the first clash the later
            // refnet gets a lower limit (or is dropped when the limit is below the least set-back) and the
            // caller plans again. Returns true when something changed.
            private bool SeparateStrips(List<Plan> plans, bool dropClashing)
            {
                for (int j = 1; j < plans.Count; j++)
                {
                    Plan later = plans[j];
                    if (later.D <= ZeroLength)
                        continue;
                    Vec2 turnLater = later.N + later.Normal * later.Offset;
                    for (int i = 0; i < j; i++)
                    {
                        Plan earlier = plans[i];
                        if (earlier.D <= ZeroLength)
                            continue;
                        Vec2 turnEarlier = earlier.N + earlier.Normal * earlier.Offset;
                        Vec2 farEarlier = turnEarlier - earlier.Direction * earlier.D;
                        double hit = FirstHit(turnLater, -later.Direction, later.D, farEarlier, turnEarlier);
                        if (hit < 0)
                            continue;

                        double limit = hit - StripClearance;
                        if (dropClashing || limit < _settings.OutletStraight - 1e-6)
                        {
                            _stripForced.Add(later.Node);
                        }
                        else
                        {
                            double old;
                            if (!_stripLimit.TryGetValue(later.Node, out old) || limit < old)
                                _stripLimit[later.Node] = limit;
                        }

                        return true;
                    }
                }

                return false;
            }

            private void ApplyPlans(List<Plan> plans)
            {
                double scale = _settings.RefnetScale;
                foreach (Plan plan in plans)
                {
                    Vec2 inlet = plan.N - plan.Direction * (plan.D + plan.Body);
                    Vec2 mainPort = plan.N - plan.Direction * plan.D;
                    Vec2 branchPort = mainPort + plan.Normal * plan.Offset;
                    Vec2 turn = plan.N + plan.Normal * plan.Offset;

                    plan.InletNode = AddNode(inlet);
                    plan.MainNode = AddNode(mainPort);
                    int turnNode = AddNode(turn);
                    Reattach(plan.Up, plan.Node, plan.InletNode);
                    Reattach(plan.Through, plan.Node, plan.MainNode);
                    Reattach(plan.Branch, plan.Node, turnNode);
                    plan.BranchNode = turnNode;
                    if (plan.D > ZeroLength)
                    {
                        plan.BranchNode = AddNode(branchPort);
                        AddEdge(plan.BranchNode, turnNode, plan.Branch.Layer, plan.Branch.Curve);
                    }

                    _nodes[plan.Node].Dead = true;

                    _result.Refnets.Add(new RefnetPlacement
                    {
                        Node = plan.N,
                        Inlet = inlet,
                        Direction = plan.Direction,
                        Mirrored = plan.Mirrored,
                        Scale = scale,
                        Layer = plan.Up.Layer,
                        Elevation = _input.Curves[plan.Up.Curve].Elevation,
                        MainPort = mainPort,
                        BranchPort = branchPort,
                        BranchTurn = turn,
                        SetBack = plan.D
                    });
                }
            }

            private void Reattach(CEdge edge, int from, int to)
            {
                if (edge.A == from)
                    edge.A = to;
                else
                    edge.B = to;
                _nodes[from].Edges.Remove(edge.Id);
                _nodes[to].Edges.Add(edge.Id);
            }

            private bool ExistingRefnetAt(Vec2 point)
            {
                foreach (Vec2 port in _input.ExistingRefnetPorts)
                {
                    if (port.DistanceTo(point) <= _settings.WeldTolerance)
                        return true;
                }

                return false;
            }

            // ------------------------------------------------------------------ piping rules
            //
            // R1, R2 and R3 are measured on the finished geometry, so what is reported is what is drawn.
            // The rules graph is the pipes plus the refnet bodies: the inlet joins the main outlet (the
            // body length, 750 s) and the branch outlet (the straight distance between the two ports,
            // 776 s). Refnet blocks already in the drawing join the graph the same way when their three
            // ports meet pipe ends.

            private sealed class RuleRefnet
            {
                public Vec2 At;
                public int Inlet;
                public int Main;
                public int Branch;
                public double MainBody;
                public double BranchBody;
            }

            private sealed class Link
            {
                public int To;
                public double Length;
                public bool Pipe;
            }

            private sealed class Frame
            {
                public int Node;
                public int From;
                public double Dist;
                public bool Straight;
                public Vec2 Prev;
            }

            private static bool IsBend(Vec2 before, Vec2 at, Vec2 after)
            {
                Vec2 a = at - before;
                Vec2 b = after - at;
                double la = a.Length;
                double lb = b.Length;
                if (la < ZeroLength || lb < ZeroLength)
                    return false;
                return a.Dot(b) < 0 || Math.Abs(a.Cross(b)) / (la * lb) > BendSine;
            }

            private static string Metres(double millimetres)
            {
                return (millimetres / 1000.0).ToString("0.00", CultureInfo.InvariantCulture);
            }

            private static void AddLink(
                Dictionary<int, List<Link>> links, int a, int b, double length, bool pipe)
            {
                List<Link> list;
                if (!links.TryGetValue(a, out list))
                    links[a] = list = new List<Link>();
                list.Add(new Link { To = b, Length = length, Pipe = pipe });
                if (!links.TryGetValue(b, out list))
                    links[b] = list = new List<Link>();
                list.Add(new Link { To = a, Length = length, Pipe = pipe });
            }

            private static List<Link> LinksOf(Dictionary<int, List<Link>> links, int node)
            {
                List<Link> list;
                return links.TryGetValue(node, out list) ? list : new List<Link>();
            }

            private int NodeNear(Vec2 point)
            {
                int best = -1;
                double bestDistance = _settings.WeldTolerance;
                foreach (CNode node in _nodes)
                {
                    if (node.Dead || node.Edges.Count == 0)
                        continue;
                    double distance = node.P.DistanceTo(point);
                    if (distance <= bestDistance)
                    {
                        bestDistance = distance;
                        best = node.Id;
                    }
                }

                return best;
            }

            private void AddExistingRefnets(List<RuleRefnet> refnets)
            {
                // Three ports per block: inlet, main outlet, branch outlet.
                IList<Vec2> ports = _input.ExistingRefnetPorts;
                for (int i = 0; i + 2 < ports.Count; i += 3)
                {
                    int inlet = NodeNear(ports[i]);
                    int main = NodeNear(ports[i + 1]);
                    int branch = NodeNear(ports[i + 2]);
                    if (inlet < 0 || main < 0 || branch < 0 || inlet == main || inlet == branch || main == branch)
                        continue;

                    refnets.Add(new RuleRefnet
                    {
                        At = ports[i],
                        Inlet = inlet,
                        Main = main,
                        Branch = branch,
                        MainBody = ports[i].DistanceTo(ports[i + 1]),
                        BranchBody = ports[i].DistanceTo(ports[i + 2])
                    });
                }
            }

            private void EvaluateRules(List<Plan> plans)
            {
                List<RuleRefnet> refnets = new List<RuleRefnet>();
                foreach (Plan plan in plans)
                {
                    refnets.Add(new RuleRefnet
                    {
                        At = plan.N,
                        Inlet = plan.InletNode,
                        Main = plan.MainNode,
                        Branch = plan.BranchNode,
                        MainBody = plan.Body,
                        BranchBody = Math.Sqrt(plan.Body * plan.Body + plan.Offset * plan.Offset)
                    });
                }

                AddExistingRefnets(refnets);
                if (refnets.Count == 0)
                    return;

                Dictionary<int, List<Link>> links = new Dictionary<int, List<Link>>();
                foreach (CEdge edge in _edges)
                {
                    if (!edge.Dead)
                        AddLink(links, edge.A, edge.B, EdgeLength(edge), true);
                }

                Dictionary<int, RuleRefnet> inletOf = new Dictionary<int, RuleRefnet>();
                foreach (RuleRefnet refnet in refnets)
                {
                    AddLink(links, refnet.Inlet, refnet.Main, refnet.MainBody, false);
                    AddLink(links, refnet.Inlet, refnet.Branch, refnet.BranchBody, false);
                    inletOf[refnet.Inlet] = refnet;
                }

                foreach (RuleRefnet refnet in refnets)
                {
                    CheckOutlet(links, inletOf, refnet, refnet.Main, "main");
                    CheckOutlet(links, inletOf, refnet, refnet.Branch, "branch");
                }

                CheckUnitRun(links, refnets);
            }

            private void AddCheck(string rule, Vec2 at, double measured, double required, string failure)
            {
                bool passed = measured >= required - 1e-6;
                _result.RuleChecks.Add(new RuleCheck
                {
                    Rule = rule,
                    At = at,
                    Measured = measured,
                    Required = required,
                    Passed = passed,
                    Message = passed ? null : failure
                });
                if (!passed)
                {
                    _result.Warnings.Add(failure);
                    _result.WarningPoints.Add(at);
                }
            }

            // R1 and R2 for one outlet. Walks the pipe downstream from the outlet. R1: the pipe length to
            // each refnet inlet reached. R2: the straight length to the first change of flow, a bend, a
            // junction or a refnet inlet. A pipe end is not a change of flow: nothing is checked there.
            private void CheckOutlet(
                Dictionary<int, List<Link>> links,
                Dictionary<int, RuleRefnet> inletOf,
                RuleRefnet from,
                int outlet,
                string which)
            {
                string source = "the " + which + " outlet of the refnet at " + At(from.At);
                Stack<Frame> stack = new Stack<Frame>();
                stack.Push(new Frame { Node = outlet, From = from.Inlet, Dist = 0, Straight = true, Prev = _nodes[outlet].P });
                int guard = 0;
                while (stack.Count > 0 && guard++ < 1000000)
                {
                    Frame frame = stack.Pop();
                    Vec2 here = _nodes[frame.Node].P;

                    RuleRefnet next;
                    if (frame.Node != outlet && inletOf.TryGetValue(frame.Node, out next))
                    {
                        string target = "the inlet of the refnet at " + At(next.At);
                        AddCheck("R1", next.At, frame.Dist, _settings.RefnetSpacing,
                            "R1: " + Num(frame.Dist) + " mm of pipe from " + source + " to " + target
                            + "; at least " + Num(_settings.RefnetSpacing) + " mm is required.");
                        if (frame.Straight)
                        {
                            AddCheck("R2", from.At, frame.Dist, _settings.OutletStraight,
                                "R2: only " + Num(frame.Dist) + " mm of straight pipe from " + source + " to "
                                + target + "; at least " + Num(_settings.OutletStraight) + " mm is required.");
                        }

                        continue;
                    }

                    List<Link> forward = new List<Link>();
                    foreach (Link link in LinksOf(links, frame.Node))
                    {
                        if (link.Pipe && link.To != frame.From)
                            forward.Add(link);
                    }

                    if (forward.Count == 0)
                        continue;

                    bool straight = frame.Straight;
                    if (straight && frame.Node != outlet)
                    {
                        string change = null;
                        if (forward.Count >= 2)
                            change = "the junction at " + At(here);
                        else if (IsBend(frame.Prev, here, _nodes[forward[0].To].P))
                            change = "the bend at " + At(here);

                        if (change != null)
                        {
                            AddCheck("R2", from.At, frame.Dist, _settings.OutletStraight,
                                "R2: only " + Num(frame.Dist) + " mm of straight pipe from " + source + " to "
                                + change + "; at least " + Num(_settings.OutletStraight) + " mm is required.");
                            straight = false;
                        }
                    }

                    foreach (Link link in forward)
                    {
                        stack.Push(new Frame
                        {
                            Node = link.To,
                            From = frame.Node,
                            Dist = frame.Dist + link.Length,
                            Straight = straight,
                            Prev = here
                        });
                    }
                }
            }

            // R3: the pipe length from the inlet of the first refnet (the nearest the root) to the farthest
            // pipe end that is not the root. Every such end counts as an indoor unit. Warning only.
            private void CheckUnitRun(Dictionary<int, List<Link>> links, List<RuleRefnet> refnets)
            {
                int root = Resolve(_root);
                Dictionary<int, double> fromRoot = new Dictionary<int, double>();
                Dictionary<int, int> parent = new Dictionary<int, int>();
                Stack<int> stack = new Stack<int>();
                fromRoot[root] = 0;
                parent[root] = -1;
                stack.Push(root);
                while (stack.Count > 0)
                {
                    int node = stack.Pop();
                    foreach (Link link in LinksOf(links, node))
                    {
                        if (fromRoot.ContainsKey(link.To))
                            continue;
                        fromRoot[link.To] = fromRoot[node] + link.Length;
                        parent[link.To] = node;
                        stack.Push(link.To);
                    }
                }

                RuleRefnet first = null;
                double firstDistance = double.MaxValue;
                foreach (RuleRefnet refnet in refnets)
                {
                    double distance;
                    if (fromRoot.TryGetValue(refnet.Inlet, out distance) && distance < firstDistance)
                    {
                        first = refnet;
                        firstDistance = distance;
                    }
                }

                if (first == null)
                    return;

                _result.FirstRefnetInlet = _nodes[first.Inlet].P;

                Dictionary<int, double> down = new Dictionary<int, double>();
                HashSet<int> seen = new HashSet<int> { parent[first.Inlet], first.Inlet };
                down[first.Inlet] = 0;
                stack.Push(first.Inlet);
                int farthest = -1;
                double farthestDistance = -1;
                while (stack.Count > 0)
                {
                    int node = stack.Pop();
                    int pipes = 0;
                    int bodies = 0;
                    foreach (Link link in LinksOf(links, node))
                    {
                        if (link.Pipe)
                            pipes++;
                        else
                            bodies++;
                        if (seen.Add(link.To))
                        {
                            down[link.To] = down[node] + link.Length;
                            stack.Push(link.To);
                        }
                    }

                    if (node != first.Inlet && node != root && pipes == 1 && bodies == 0
                        && down[node] > farthestDistance)
                    {
                        farthest = node;
                        farthestDistance = down[node];
                    }
                }

                if (farthest < 0)
                    return;

                Vec2 unit = _nodes[farthest].P;
                _result.FarthestUnit = unit;
                _result.FarthestUnitDistance = farthestDistance;

                double warnAbove = _settings.MaxUnitRun - _settings.RunAllowance;
                string message = null;
                if (farthestDistance > _settings.MaxUnitRun + 1e-6)
                {
                    message = "R3: the farthest indoor unit, the pipe end at " + At(unit) + ", is "
                        + Metres(farthestDistance) + " m of pipe from the inlet of the first refnet: over the "
                        + Metres(_settings.MaxUnitRun) + " m limit.";
                }
                else if (farthestDistance > warnAbove + 1e-6)
                {
                    message = "R3: the farthest indoor unit, the pipe end at " + At(unit) + ", is "
                        + Metres(farthestDistance) + " m of pipe from the inlet of the first refnet: over "
                        + Metres(warnAbove) + " m (the " + Metres(_settings.MaxUnitRun) + " m limit less a "
                        + Metres(_settings.RunAllowance) + " m construction allowance).";
                }

                _result.RuleChecks.Add(new RuleCheck
                {
                    Rule = "R3",
                    At = unit,
                    Measured = farthestDistance,
                    Required = warnAbove,
                    Passed = message == null,
                    Message = message
                });
                if (message != null)
                {
                    _result.Warnings.Add(message);
                    _result.WarningPoints.Add(unit);
                }
            }

            // ------------------------------------------------------------------ output paths

            private void BuildPaths()
            {
                // A pipe the refnet cut down to nothing is dropped.
                foreach (CEdge edge in _edges)
                {
                    if (!edge.Dead && EdgeLength(edge) <= ZeroLength)
                        KillEdge(edge);
                }

                HashSet<int> done = new HashSet<int>();
                foreach (CEdge start in _edges)
                {
                    if (start.Dead || done.Contains(start.Id))
                        continue;

                    List<int> forward = new List<int>();
                    List<int> backward = new List<int>();
                    done.Add(start.Id);

                    int endNode = start.B;
                    CEdge current = start;
                    while (true)
                    {
                        CEdge next;
                        if (!NextInPath(current, endNode, done, out next))
                            break;
                        forward.Add(next.Id);
                        done.Add(next.Id);
                        endNode = next.OtherNode(endNode);
                        current = next;
                    }

                    int startNode = start.A;
                    current = start;
                    while (true)
                    {
                        CEdge next;
                        if (!NextInPath(current, startNode, done, out next))
                            break;
                        backward.Add(next.Id);
                        done.Add(next.Id);
                        startNode = next.OtherNode(startNode);
                        current = next;
                    }

                    RefrigPath path = new RefrigPath { Layer = start.Layer };
                    path.Points.Add(_nodes[startNode].P);
                    int walker = startNode;
                    List<int> ordered = new List<int>();
                    for (int i = backward.Count - 1; i >= 0; i--)
                        ordered.Add(backward[i]);
                    ordered.Add(start.Id);
                    ordered.AddRange(forward);

                    Dictionary<int, double> byCurve = new Dictionary<int, double>();
                    foreach (int id in ordered)
                    {
                        CEdge edge = _edges[id];
                        walker = edge.OtherNode(walker);
                        path.Points.Add(_nodes[walker].P);
                        double sum;
                        byCurve.TryGetValue(edge.Curve, out sum);
                        byCurve[edge.Curve] = sum + EdgeLength(edge);
                    }

                    int bestCurve = start.Curve;
                    double bestLength = -1;
                    foreach (KeyValuePair<int, double> pair in byCurve)
                    {
                        if (pair.Value > bestLength)
                        {
                            bestLength = pair.Value;
                            bestCurve = pair.Key;
                        }
                    }

                    path.SourceCurve = _input.Curves[bestCurve].Id;
                    path.Elevation = _input.Curves[bestCurve].Elevation;
                    _result.Paths.Add(path);
                }
            }

            // The pipe that continues a path through a corner: the node has exactly two pipes, the other
            // one is on the same layer and not yet in a path.
            private bool NextInPath(CEdge current, int node, HashSet<int> done, out CEdge next)
            {
                next = null;
                CNode n = _nodes[node];
                if (n.Edges.Count != 2)
                    return false;
                CEdge other = _edges[n.Edges[0] == current.Id ? n.Edges[1] : n.Edges[0]];
                if (other.Id == current.Id || done.Contains(other.Id)
                    || !string.Equals(other.Layer, current.Layer, StringComparison.OrdinalIgnoreCase))
                    return false;
                next = other;
                return true;
            }
        }
    }
}
