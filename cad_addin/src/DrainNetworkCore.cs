using System;
using System.Collections.Generic;
using System.Globalization;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Pipes
{
    // AutoCAD-free tree of a condensate drain network, built from the cleaned paths of RefrigCleaner.
    // The flow runs toward the root (the drain point). The key nodes are the root, the indoor units
    // (degree-1 ends), the T junctions (degree 3) and the crosses (degree 4 or more); a segment is the
    // pipe between two key nodes, with its bends as interior points. Segments are written from the
    // upstream node to the downstream node, and numbered so that a segment always has a smaller index
    // than the segments that feed it. See AGENTS.md, "HVACDRAIN: draw and size a condensate network".

    public enum DrainNodeKind
    {
        Root,
        Unit,
        Junction,
        Cross
    }

    public sealed class DrainNode
    {
        public int Index;
        public Vec2 Point;
        public DrainNodeKind Kind;

        /// <summary>The number of pipes that meet here.</summary>
        public int Degree;

        /// <summary>The segment that leaves this node toward the root; -1 for the root.</summary>
        public int DownSegment = -1;

        /// <summary>The segments that end at this node, flowing in.</summary>
        public readonly List<int> UpSegments = new List<int>();

        /// <summary>For an indoor unit: its index in <see cref="DrainTree.Units"/>.</summary>
        public int Unit = -1;
    }

    public sealed class DrainSegment
    {
        public int Index;

        /// <summary>The key node at the far end, away from the root.</summary>
        public int UpNode;

        /// <summary>The key node at the near end, toward the root.</summary>
        public int DownNode;

        /// <summary>The segment this one flows into; -1 for the one that ends at the root.</summary>
        public int Parent = -1;

        /// <summary>The segments that flow into this one.</summary>
        public readonly List<int> Children = new List<int>();

        /// <summary>The path from the upstream node to the downstream node, bends included.</summary>
        public readonly List<Vec2> Points = new List<Vec2>();

        /// <summary>Every indoor unit upstream of this segment (indices into <see cref="DrainTree.Units"/>).</summary>
        public readonly List<int> Units = new List<int>();

        /// <summary>The <see cref="PipeCurve.Id"/> that gave most of this segment's length.</summary>
        public int SourceCurve;
        public double Elevation;

        /// <summary>The layer of that curve's path in the cleaned network.</summary>
        public string Layer;

        public double Length
        {
            get
            {
                double total = 0;
                for (int i = 0; i + 1 < Points.Count; i++)
                    total += Points[i].DistanceTo(Points[i + 1]);
                return total;
            }
        }
    }

    public sealed class DrainUnit
    {
        public int Index;
        public int Node;
        public Vec2 Point;

        /// <summary>The segment that leaves this indoor unit.</summary>
        public int Segment;
    }

    public sealed class DrainTree
    {
        public readonly List<DrainNode> Nodes = new List<DrainNode>();
        public readonly List<DrainSegment> Segments = new List<DrainSegment>();
        public readonly List<DrainUnit> Units = new List<DrainUnit>();
        public int RootNode = -1;
        public int RootSegment = -1;

        /// <summary>Paths that no segment reaches (another network): they are written as cleaned, not sized.</summary>
        public readonly List<RefrigPath> Unreached = new List<RefrigPath>();

        /// <summary>Cases the tree builder reports and leaves alone (crosses, other networks).</summary>
        public readonly List<string> Reports = new List<string>();
        public readonly List<Vec2> ReportPoints = new List<Vec2>();
    }

    public sealed class DrainTreeResult
    {
        public bool Ok;
        public string Error;
        public readonly List<Vec2> ErrorPoints = new List<Vec2>();
        public DrainTree Tree;
    }

    public static class DrainNetwork
    {
        // Cleaned nodes that should coincide are equal to the last bit; this only absorbs rounding.
        private const double MergeDistance = 1e-3;

        // A bend smaller than this is not a bend: the point is dropped from the segment.
        private const double CollinearDegrees = 0.5;

        private sealed class GEdge
        {
            public int A;
            public int B;
            public int Path;
            public double Length;

            public int Other(int node)
            {
                return node == A ? B : A;
            }
        }

        private sealed class WalkTask
        {
            public int DownNode;
            public int FromGraphNode;
            public int Edge;
        }

        internal static string Num(double value)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        }

        internal static string At(Vec2 point)
        {
            return "(" + Num(point.X) + ", " + Num(point.Y) + ")";
        }

        /// <summary>
        /// Builds the tree of the cleaned paths, flowing toward the pipe end nearest the root point.
        /// The root must be a pipe end within the tolerance, and the pipes it reaches must form a tree.
        /// Pipes of any other network go to <see cref="DrainTree.Unreached"/>.
        /// </summary>
        public static DrainTreeResult Build(IList<RefrigPath> paths, Vec2 rootPoint, double rootTolerance)
        {
            DrainTreeResult result = new DrainTreeResult();
            if (paths == null || paths.Count == 0)
            {
                result.Error = "There are no pipes to build a drain network from.";
                return result;
            }

            // 1. The graph of the paths' vertices.
            List<Vec2> points = new List<Vec2>();
            List<GEdge> edges = new List<GEdge>();
            HashSet<long> pairs = new HashSet<long>();
            for (int p = 0; p < paths.Count; p++)
            {
                RefrigPath path = paths[p];
                int previous = -1;
                for (int i = 0; i < path.Points.Count; i++)
                {
                    int node = NodeFor(points, path.Points[i]);
                    if (previous >= 0 && previous != node)
                    {
                        long key = ((long)Math.Min(previous, node) << 32) | (uint)Math.Max(previous, node);
                        if (pairs.Add(key))
                        {
                            edges.Add(new GEdge
                            {
                                A = previous,
                                B = node,
                                Path = p,
                                Length = points[previous].DistanceTo(points[node])
                            });
                        }
                    }

                    previous = node;
                }
            }

            if (edges.Count == 0)
            {
                result.Error = "There are no pipes to build a drain network from.";
                return result;
            }

            List<int>[] adjacent = new List<int>[points.Count];
            for (int i = 0; i < adjacent.Length; i++)
                adjacent[i] = new List<int>();
            for (int e = 0; e < edges.Count; e++)
            {
                adjacent[edges[e].A].Add(e);
                adjacent[edges[e].B].Add(e);
            }

            // 2. The root: the pipe end nearest to the picked point.
            int root = -1;
            double best = double.MaxValue;
            for (int n = 0; n < points.Count; n++)
            {
                if (adjacent[n].Count != 1)
                    continue;
                double distance = points[n].DistanceTo(rootPoint);
                if (distance < best)
                {
                    best = distance;
                    root = n;
                }
            }

            if (root < 0 || best > rootTolerance)
            {
                result.Error = "No pipe end lies within " + Num(rootTolerance)
                    + " mm of the picked drain point. Pick the end of the pipe that reaches the riser or floor drain.";
                return result;
            }

            // 3. One connected tree.
            bool[] reached = new bool[points.Count];
            Queue<int> queue = new Queue<int>();
            queue.Enqueue(root);
            reached[root] = true;
            int reachedCount = 0;
            int reachedEdges = 0;
            HashSet<int> edgeSeen = new HashSet<int>();
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                reachedCount++;
                foreach (int e in adjacent[current])
                {
                    if (edgeSeen.Add(e))
                        reachedEdges++;
                    int next = edges[e].Other(current);
                    if (!reached[next])
                    {
                        reached[next] = true;
                        queue.Enqueue(next);
                    }
                }
            }

            int total = 0;
            for (int n = 0; n < points.Count; n++)
            {
                if (adjacent[n].Count > 0)
                    total++;
            }

            DrainTree tree = new DrainTree();
            if (reachedCount < total)
            {
                // Pipes of another network are kept as cleaned, with one marker per network.
                bool[] marked = new bool[points.Count];
                int others = 0;
                for (int n = 0; n < points.Count; n++)
                {
                    if (reached[n] || marked[n] || adjacent[n].Count == 0)
                        continue;
                    others++;
                    tree.ReportPoints.Add(points[n]);
                    Queue<int> flood = new Queue<int>();
                    flood.Enqueue(n);
                    marked[n] = true;
                    while (flood.Count > 0)
                    {
                        int current = flood.Dequeue();
                        foreach (int e in adjacent[current])
                        {
                            int next = edges[e].Other(current);
                            if (!marked[next])
                            {
                                marked[next] = true;
                                flood.Enqueue(next);
                            }
                        }
                    }
                }

                foreach (RefrigPath path in paths)
                {
                    if (path.Points.Count < 2)
                        continue;
                    int first = NodeFor(points, path.Points[0]);
                    if (first < adjacent.Length && !reached[first] && adjacent[first].Count > 0)
                        tree.Unreached.Add(path);
                }

                tree.Reports.Add(
                    others + " other pipe network(s) are not connected to the drain point; they are kept as cleaned "
                    + "and not sized.");
            }

            if (reachedEdges != reachedCount - 1)
            {
                result.Error = "The network has a loop. HVACDRAIN expects a tree: break the loop first.";
                result.ErrorPoints.Add(points[root]);
                return result;
            }

            // 4. Walk from the root, one segment per task. A segment index is given when the task is taken,
            // so a segment always comes after the one it flows into.
            DrainNode rootNode = new DrainNode
            {
                Index = 0,
                Point = points[root],
                Kind = DrainNodeKind.Root,
                Degree = 1
            };
            tree.Nodes.Add(rootNode);
            tree.RootNode = 0;

            Stack<WalkTask> stack = new Stack<WalkTask>();
            stack.Push(new WalkTask { DownNode = 0, FromGraphNode = root, Edge = adjacent[root][0] });
            while (stack.Count > 0)
            {
                WalkTask task = stack.Pop();
                List<Vec2> walked = new List<Vec2> { points[task.FromGraphNode] };
                Dictionary<int, double> byPath = new Dictionary<int, double>();
                int previousNode = task.FromGraphNode;
                int edge = task.Edge;
                int node;
                while (true)
                {
                    GEdge e = edges[edge];
                    node = e.Other(previousNode);
                    walked.Add(points[node]);
                    double sum;
                    byPath.TryGetValue(e.Path, out sum);
                    byPath[e.Path] = sum + e.Length;

                    if (adjacent[node].Count != 2)
                        break;

                    int next = adjacent[node][0] == edge ? adjacent[node][1] : adjacent[node][0];
                    previousNode = node;
                    edge = next;
                }

                int degree = adjacent[node].Count;
                DrainNode key = new DrainNode
                {
                    Index = tree.Nodes.Count,
                    Point = points[node],
                    Degree = degree,
                    Kind = degree == 1
                        ? DrainNodeKind.Unit
                        : (degree == 3 ? DrainNodeKind.Junction : DrainNodeKind.Cross)
                };
                tree.Nodes.Add(key);

                DrainSegment segment = new DrainSegment
                {
                    Index = tree.Segments.Count,
                    UpNode = key.Index,
                    DownNode = task.DownNode,
                    Parent = tree.Nodes[task.DownNode].DownSegment
                };
                walked.Reverse();
                segment.Points.AddRange(RemoveCollinear(walked));

                int bestPath = -1;
                double bestLength = -1;
                foreach (KeyValuePair<int, double> pair in byPath)
                {
                    if (pair.Value > bestLength || (pair.Value == bestLength && pair.Key < bestPath))
                    {
                        bestLength = pair.Value;
                        bestPath = pair.Key;
                    }
                }

                segment.SourceCurve = paths[bestPath].SourceCurve;
                segment.Elevation = paths[bestPath].Elevation;
                segment.Layer = paths[bestPath].Layer;

                tree.Segments.Add(segment);
                key.DownSegment = segment.Index;
                tree.Nodes[task.DownNode].UpSegments.Add(segment.Index);
                if (segment.Parent >= 0)
                    tree.Segments[segment.Parent].Children.Add(segment.Index);
                else
                    tree.RootSegment = segment.Index;

                if (key.Kind == DrainNodeKind.Unit)
                {
                    DrainUnit unit = new DrainUnit
                    {
                        Index = tree.Units.Count,
                        Node = key.Index,
                        Point = key.Point,
                        Segment = segment.Index
                    };
                    key.Unit = unit.Index;
                    tree.Units.Add(unit);
                    continue;
                }

                if (key.Kind == DrainNodeKind.Cross)
                {
                    tree.Reports.Add(
                        "A joint with " + degree + " pipes at " + At(key.Point)
                        + " is not handled: it is left as a plain cross.");
                    tree.ReportPoints.Add(key.Point);
                }

                // Push in reverse so the first pipe out of the node is taken first.
                List<int> outgoing = new List<int>();
                foreach (int e in adjacent[node])
                {
                    if (e != edge)
                        outgoing.Add(e);
                }

                for (int i = outgoing.Count - 1; i >= 0; i--)
                    stack.Push(new WalkTask { DownNode = key.Index, FromGraphNode = node, Edge = outgoing[i] });
            }

            // 5. The units upstream of each segment, from the leaves back to the root.
            for (int s = tree.Segments.Count - 1; s >= 0; s--)
            {
                DrainSegment segment = tree.Segments[s];
                DrainNode up = tree.Nodes[segment.UpNode];
                if (up.Kind == DrainNodeKind.Unit)
                    segment.Units.Add(up.Unit);
                foreach (int child in segment.Children)
                    segment.Units.AddRange(tree.Segments[child].Units);
                segment.Units.Sort();
            }

            result.Ok = true;
            result.Tree = tree;
            return result;
        }

        private static int NodeFor(List<Vec2> points, Vec2 point)
        {
            for (int i = 0; i < points.Count; i++)
            {
                if (Math.Abs(points[i].X - point.X) <= MergeDistance
                    && Math.Abs(points[i].Y - point.Y) <= MergeDistance)
                    return i;
            }

            points.Add(point);
            return points.Count - 1;
        }

        // Drops the interior points that lie on a straight line, so a long straight run is one leg.
        private static List<Vec2> RemoveCollinear(IList<Vec2> source)
        {
            List<Vec2> kept = new List<Vec2> { source[0] };
            double sinLimit = Math.Sin(CollinearDegrees * Math.PI / 180.0);
            for (int i = 1; i + 1 < source.Count; i++)
            {
                Vec2 a = source[i] - kept[kept.Count - 1];
                Vec2 b = source[i + 1] - source[i];
                double scale = a.Length * b.Length;
                bool straight = scale > 1e-12
                    && Math.Abs(a.Cross(b)) <= sinLimit * scale
                    && a.Dot(b) > 0;
                if (!straight)
                    kept.Add(source[i]);
            }

            kept.Add(source[source.Count - 1]);
            return kept;
        }
    }
}
