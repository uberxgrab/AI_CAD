using System;
using System.Collections.Generic;
using System.Globalization;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Pipes
{
    // AutoCAD-free pipe tree for HVACREFRIG sizing: the cleaned pipe paths, the refnets the cleaner
    // plans and the refnet blocks already in the drawing become one tree from the root (the outdoor-unit
    // end). A refnet is one node that joins its three ports (inlet, main outlet, branch outlet); the
    // pipes between nodes are the segments, and the pipe ends other than the root are the indoor units.
    // Plain doubles and Vec2 only, so tests/PipeCoreTests compiles this file without AutoCAD.
    // See AGENTS.md, "HVACREFRIG", "Pipe sizing".

    /// <summary>The three port points of a refnet block already in the drawing.</summary>
    public sealed class RefnetPortGroup
    {
        /// <summary>The caller's identifier (an index into its own list of blocks).</summary>
        public int Id;
        public Vec2 Inlet;
        public Vec2 Main;
        public Vec2 Branch;

        /// <summary>Groups a flat list of ports, three per block in the order inlet, main outlet, branch outlet.</summary>
        public static List<RefnetPortGroup> FromFlat(IList<Vec2> ports)
        {
            List<RefnetPortGroup> groups = new List<RefnetPortGroup>();
            for (int i = 0; i + 2 < ports.Count; i += 3)
            {
                groups.Add(new RefnetPortGroup
                {
                    Id = i / 3,
                    Inlet = ports[i],
                    Main = ports[i + 1],
                    Branch = ports[i + 2]
                });
            }

            return groups;
        }
    }

    public enum SegmentKind
    {
        /// <summary>From the root to the first refnet, or to the only unit.</summary>
        Main,
        /// <summary>From one refnet (or junction) to the next.</summary>
        BetweenRefnets,
        /// <summary>From the last refnet (or junction) to an indoor unit.</summary>
        ToUnit
    }

    public enum TopologyNodeKind
    {
        Root,
        Refnet,
        /// <summary>A junction of three or more pipes that has no refnet (a case HVACREFRIG left alone).</summary>
        Junction,
        End
    }

    public sealed class TopologyNode
    {
        public int Index;
        public TopologyNodeKind Kind;
        public Vec2 Point;
        /// <summary>For a refnet node: its index in <see cref="NetworkTopology.Refnets"/>; otherwise -1.</summary>
        public int RefnetIndex = -1;
        /// <summary>For an end node: its index in <see cref="NetworkTopology.Ends"/>; otherwise -1.</summary>
        public int EndIndex = -1;
    }

    public sealed class TopologyRefnet
    {
        public int Index;
        /// <summary>True for a refnet the cleaner plans, false for a block already in the drawing.</summary>
        public bool Planned;
        /// <summary>Planned: the index in <see cref="RefrigResult.Refnets"/>. Existing: <see cref="RefnetPortGroup.Id"/>.</summary>
        public int SourceIndex;
        public Vec2 Inlet;
        public Vec2 Main;
        public Vec2 Branch;
        public int Node;
        /// <summary>The segment that reaches the refnet; -1 only when it was never reached.</summary>
        public int UpSegment = -1;
        /// <summary>0 when the pipe from the root reaches the inlet, 1 the main outlet, 2 the branch outlet.</summary>
        public int ArrivedAt;
        public int MainSegment = -1;
        public int BranchSegment = -1;
    }

    /// <summary>An indoor unit: a pipe end that is not the root.</summary>
    public sealed class TopologyEnd
    {
        public int Index;
        public Vec2 Point;
        public int Node;
        /// <summary>The segment that ends here.</summary>
        public int Segment;
        /// <summary>Pipe length from the root (mm), without refnet bodies.</summary>
        public double PipeLength;
        /// <summary>How many refnets lie between the root and this end.</summary>
        public int Refnets;
    }

    /// <summary>One pipe from node to node, as one polyline from upstream to downstream.</summary>
    public sealed class TopologySegment
    {
        public int Index;
        public SegmentKind Kind;
        public int UpNode;
        public int DownNode;
        public readonly List<Vec2> Points = new List<Vec2>();
        public double Length;
        /// <summary>The source curve that gave most of its length; the polyline copies its properties.</summary>
        public int SourceCurve;
        public double Elevation;
        /// <summary>The layers the pieces of this segment had before.</summary>
        public readonly List<string> OriginalLayers = new List<string>();
        public readonly List<int> Children = new List<int>();
        /// <summary>The indoor units (indexes into <see cref="NetworkTopology.Ends"/>) downstream of this segment.</summary>
        public readonly List<int> DownstreamEnds = new List<int>();
    }

    public sealed class NetworkTopology
    {
        public bool Ok;
        public string Error;
        public readonly List<TopologyNode> Nodes = new List<TopologyNode>();
        public readonly List<TopologySegment> Segments = new List<TopologySegment>();
        public readonly List<TopologyRefnet> Refnets = new List<TopologyRefnet>();
        public readonly List<TopologyEnd> Ends = new List<TopologyEnd>();
        /// <summary>Paths that no segment reaches (another network): written unchanged.</summary>
        public readonly List<RefrigPath> Unreached = new List<RefrigPath>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<Vec2> WarningPoints = new List<Vec2>();
        public int RootNode = -1;

        /// <summary>The segments of one kind, in order.</summary>
        public List<TopologySegment> OfKind(SegmentKind kind)
        {
            return Segments.FindAll(s => s.Kind == kind);
        }
    }

    public sealed class TopologyInput
    {
        /// <summary>The cleaned paths (<see cref="RefrigResult.Paths"/>).</summary>
        public IList<RefrigPath> Paths = new List<RefrigPath>();
        /// <summary>The refnets the cleaner plans (<see cref="RefrigResult.Refnets"/>).</summary>
        public IList<RefnetPlacement> PlannedRefnets = new List<RefnetPlacement>();
        /// <summary>Refnet blocks already in the drawing.</summary>
        public IList<RefnetPortGroup> ExistingRefnets = new List<RefnetPortGroup>();
        /// <summary>The root: the outdoor-unit end of the network.</summary>
        public Vec2 Root;
        /// <summary>Points closer than this are one node (mm).</summary>
        public double Tolerance = 1.0;
    }

    public static class NetworkTopologyBuilder
    {
        public static NetworkTopology Build(TopologyInput input)
        {
            NetworkTopology topology = new NetworkTopology();
            new Builder(input, topology).Execute();
            return topology;
        }

        private sealed class Piece
        {
            public readonly List<Vec2> Points = new List<Vec2>();
            public string Layer;
            public double Elevation;
            public int SourceCurve;
            public int A;
            public int B;
            public bool Used;
            /// <summary>The piece runs between two ports of one refnet: the block's body, not a pipe.</summary>
            public bool Body;

            public double Length
            {
                get
                {
                    double sum = 0;
                    for (int i = 1; i < Points.Count; i++)
                        sum += Points[i - 1].DistanceTo(Points[i]);
                    return sum;
                }
            }
        }

        private struct PortOwner
        {
            public int Refnet;
            public int Role;
        }

        private sealed class Builder
        {
            private readonly TopologyInput _input;
            private readonly NetworkTopology _topology;
            private readonly double _tolerance;
            private readonly List<Vec2> _points = new List<Vec2>();
            private readonly List<Piece> _pieces = new List<Piece>();
            private readonly List<List<int>> _adjacent = new List<List<int>>();
            private readonly Dictionary<int, PortOwner> _portOwner = new Dictionary<int, PortOwner>();
            private readonly List<int[]> _refnetPorts = new List<int[]>();
            private readonly Dictionary<int, int> _refnetNode = new Dictionary<int, int>();

            public Builder(TopologyInput input, NetworkTopology topology)
            {
                _input = input;
                _topology = topology;
                _tolerance = Math.Max(input.Tolerance, 1e-6);
            }

            private static string At(Vec2 point)
            {
                return "(" + point.X.ToString("0.#", CultureInfo.InvariantCulture) + ", "
                    + point.Y.ToString("0.#", CultureInfo.InvariantCulture) + ")";
            }

            private void Warn(string message, Vec2 point)
            {
                _topology.Warnings.Add(message);
                _topology.WarningPoints.Add(point);
            }

            private int NodeAt(Vec2 point)
            {
                int best = -1;
                double bestDistance = _tolerance;
                for (int i = 0; i < _points.Count; i++)
                {
                    double distance = _points[i].DistanceTo(point);
                    if (distance <= bestDistance)
                    {
                        bestDistance = distance;
                        best = i;
                    }
                }

                if (best >= 0)
                    return best;

                _points.Add(point);
                _adjacent.Add(new List<int>());
                return _points.Count - 1;
            }

            // ------------------------------------------------------------------ set-up

            public void Execute()
            {
                List<Vec2[]> refnetPorts = new List<Vec2[]>();
                List<bool> planned = new List<bool>();
                List<int> sourceIndex = new List<int>();
                for (int i = 0; i < _input.PlannedRefnets.Count; i++)
                {
                    RefnetPlacement p = _input.PlannedRefnets[i];
                    refnetPorts.Add(new[] { p.Inlet, p.MainPort, p.BranchPort });
                    planned.Add(true);
                    sourceIndex.Add(i);
                }

                foreach (RefnetPortGroup group in _input.ExistingRefnets)
                {
                    refnetPorts.Add(new[] { group.Inlet, group.Main, group.Branch });
                    planned.Add(false);
                    sourceIndex.Add(group.Id);
                }

                List<Vec2> special = new List<Vec2>();
                foreach (Vec2[] ports in refnetPorts)
                    special.AddRange(ports);

                BuildPieces(special);
                int root = NodeAt(_input.Root);
                if (_adjacent[root].Count == 0)
                {
                    _topology.Error = "No pipe ends at the root " + At(_input.Root) + ".";
                    return;
                }

                RegisterRefnets(refnetPorts, planned, sourceIndex);
                DetachBodyPieces();

                TopologyNode rootNode = AddNode(TopologyNodeKind.Root, _points[root]);
                _topology.RootNode = rootNode.Index;
                if (_adjacent[root].Count > 1)
                {
                    Warn(
                        "The root " + At(_points[root]) + " has " + _adjacent[root].Count
                        + " pipes; each is sized as a main pipe.",
                        _points[root]);
                }

                foreach (int pieceIndex in _adjacent[root].ToArray())
                    Trace(rootNode.Index, root, pieceIndex, 0, 0);

                CollectEnds();

                foreach (Piece piece in _pieces)
                {
                    if (piece.Used)
                        continue;
                    RefrigPath path = new RefrigPath
                    {
                        Layer = piece.Layer,
                        Elevation = piece.Elevation,
                        SourceCurve = piece.SourceCurve
                    };
                    path.Points.AddRange(piece.Points);
                    _topology.Unreached.Add(path);
                }

                int disconnected = 0;
                Vec2 firstDisconnected = _input.Root;
                foreach (Piece piece in _pieces)
                {
                    if (!piece.Used && !piece.Body)
                    {
                        if (disconnected == 0)
                            firstDisconnected = piece.Points[0];
                        disconnected++;
                    }
                }

                if (disconnected > 0)
                {
                    Warn(
                        disconnected + " pipe(s) are not connected to the root and were not sized.",
                        firstDisconnected);
                }

                _topology.Ok = true;
            }

            // Cuts every path at the vertices that coincide with a refnet port, so a piece never runs
            // through a refnet, then turns the pieces into graph edges.
            private void BuildPieces(List<Vec2> special)
            {
                foreach (RefrigPath path in _input.Paths)
                {
                    if (path.Points.Count < 2)
                        continue;

                    Piece current = NewPiece(path);
                    current.Points.Add(path.Points[0]);
                    for (int i = 1; i < path.Points.Count; i++)
                    {
                        current.Points.Add(path.Points[i]);
                        bool cut = i < path.Points.Count - 1 && IsSpecial(path.Points[i], special);
                        if (!cut)
                            continue;

                        AddPiece(current);
                        current = NewPiece(path);
                        current.Points.Add(path.Points[i]);
                    }

                    AddPiece(current);
                }
            }

            private bool IsSpecial(Vec2 point, List<Vec2> special)
            {
                foreach (Vec2 s in special)
                {
                    if (s.DistanceTo(point) <= _tolerance)
                        return true;
                }

                return false;
            }

            private static Piece NewPiece(RefrigPath path)
            {
                return new Piece { Layer = path.Layer, Elevation = path.Elevation, SourceCurve = path.SourceCurve };
            }

            private void AddPiece(Piece piece)
            {
                if (piece.Points.Count < 2 || piece.Length < 1e-9)
                    return;

                piece.A = NodeAt(piece.Points[0]);
                piece.B = NodeAt(piece.Points[piece.Points.Count - 1]);
                if (piece.A == piece.B)
                {
                    Warn(
                        "A pipe starts and ends at " + At(piece.Points[0]) + " and was not sized.",
                        piece.Points[0]);
                    RefrigPath leftover = new RefrigPath
                    {
                        Layer = piece.Layer,
                        Elevation = piece.Elevation,
                        SourceCurve = piece.SourceCurve
                    };
                    leftover.Points.AddRange(piece.Points);
                    _topology.Unreached.Add(leftover);
                    return;
                }

                int index = _pieces.Count;
                _pieces.Add(piece);
                _adjacent[piece.A].Add(index);
                _adjacent[piece.B].Add(index);
            }

            private void RegisterRefnets(List<Vec2[]> refnetPorts, List<bool> planned, List<int> sourceIndex)
            {
                for (int r = 0; r < refnetPorts.Count; r++)
                {
                    int[] nodes =
                    {
                        NodeAt(refnetPorts[r][0]), NodeAt(refnetPorts[r][1]), NodeAt(refnetPorts[r][2])
                    };
                    bool distinct = nodes[0] != nodes[1] && nodes[0] != nodes[2] && nodes[1] != nodes[2];
                    bool free = distinct
                        && !_portOwner.ContainsKey(nodes[0])
                        && !_portOwner.ContainsKey(nodes[1])
                        && !_portOwner.ContainsKey(nodes[2]);
                    if (!free)
                    {
                        Warn(
                            "The refnet at " + At(refnetPorts[r][0])
                            + " shares a port with another refnet and is treated as a plain junction.",
                            refnetPorts[r][0]);
                        continue;
                    }

                    TopologyRefnet refnet = new TopologyRefnet
                    {
                        Index = _topology.Refnets.Count,
                        Planned = planned[r],
                        SourceIndex = sourceIndex[r],
                        Inlet = refnetPorts[r][0],
                        Main = refnetPorts[r][1],
                        Branch = refnetPorts[r][2]
                    };
                    _topology.Refnets.Add(refnet);
                    _refnetPorts.Add(nodes);
                    for (int role = 0; role < 3; role++)
                        _portOwner[nodes[role]] = new PortOwner { Refnet = refnet.Index, Role = role };
                }
            }

            // A pipe drawn straight through a refnet block, from one of its ports to another, is the block's
            // own body: it is not a pipe of the tree. It stays as drawn.
            private void DetachBodyPieces()
            {
                for (int i = 0; i < _pieces.Count; i++)
                {
                    Piece piece = _pieces[i];
                    PortOwner a;
                    PortOwner b;
                    if (!_portOwner.TryGetValue(piece.A, out a) || !_portOwner.TryGetValue(piece.B, out b)
                        || a.Refnet != b.Refnet)
                        continue;

                    piece.Body = true;
                    _adjacent[piece.A].Remove(i);
                    _adjacent[piece.B].Remove(i);
                    Warn(
                        "A pipe runs through the body of the refnet at "
                        + At(_topology.Refnets[a.Refnet].Inlet) + "; it was left as drawn.",
                        _topology.Refnets[a.Refnet].Inlet);
                }
            }

            private TopologyNode AddNode(TopologyNodeKind kind, Vec2 point)
            {
                TopologyNode node = new TopologyNode { Index = _topology.Nodes.Count, Kind = kind, Point = point };
                _topology.Nodes.Add(node);
                return node;
            }

            // ------------------------------------------------------------------ tracing

            // Walks from a node along one piece, through every corner, to the next node: a refnet port,
            // a pipe end or a junction. That pipe is one segment. Then it follows on from the node it
            // reached. Segments are numbered in the order they are met, main outlet before branch.
            private int Trace(int upNode, int startPoint, int firstPiece, double distance, int refnets)
            {
                if (_pieces[firstPiece].Used)
                {
                    Warn(
                        "The pipe network has a loop near " + At(_points[startPoint]) + "; it was not followed.",
                        _points[startPoint]);
                    return -1;
                }

                TopologySegment segment = new TopologySegment { Index = _topology.Segments.Count };
                _topology.Segments.Add(segment);
                segment.UpNode = upNode;

                int current = startPoint;
                int pieceIndex = firstPiece;
                double bestLength = -1;
                Piece best = null;
                TopologyNode down;
                while (true)
                {
                    Piece piece = _pieces[pieceIndex];
                    piece.Used = true;
                    bool forward = piece.A == current;
                    AppendPoints(segment, piece, forward);
                    double length = piece.Length;
                    segment.Length += length;
                    if (!segment.OriginalLayers.Contains(piece.Layer))
                        segment.OriginalLayers.Add(piece.Layer);
                    if (length > bestLength)
                    {
                        bestLength = length;
                        best = piece;
                    }

                    current = forward ? piece.B : piece.A;

                    PortOwner owner;
                    if (_portOwner.TryGetValue(current, out owner))
                    {
                        down = ArriveAtRefnet(segment, owner, current);
                        break;
                    }

                    List<int> onward = new List<int>();
                    foreach (int other in _adjacent[current])
                    {
                        if (other != pieceIndex)
                            onward.Add(other);
                    }

                    if (onward.Count == 0)
                    {
                        down = AddNode(TopologyNodeKind.End, _points[current]);
                        TopologyEnd end = new TopologyEnd
                        {
                            Index = _topology.Ends.Count,
                            Point = _points[current],
                            Node = down.Index,
                            Segment = segment.Index,
                            PipeLength = distance + segment.Length,
                            Refnets = refnets
                        };
                        _topology.Ends.Add(end);
                        down.EndIndex = end.Index;
                        break;
                    }

                    if (onward.Count == 1 && !_pieces[onward[0]].Used)
                    {
                        pieceIndex = onward[0];
                        continue;
                    }

                    if (onward.Count == 1)
                    {
                        Warn(
                            "The pipe network has a loop near " + At(_points[current]) + "; it was not followed.",
                            _points[current]);
                        down = AddNode(TopologyNodeKind.Junction, _points[current]);
                        break;
                    }

                    down = AddNode(TopologyNodeKind.Junction, _points[current]);
                    Warn(
                        "There is no refnet at the junction " + At(_points[current])
                        + "; it is sized like a refnet.",
                        _points[current]);
                    break;
                }

                segment.DownNode = down.Index;
                segment.SourceCurve = best.SourceCurve;
                segment.Elevation = best.Elevation;
                TopologyNode up = _topology.Nodes[upNode];
                if (up.Kind == TopologyNodeKind.Root)
                    segment.Kind = SegmentKind.Main;
                else if (down.Kind == TopologyNodeKind.End)
                    segment.Kind = SegmentKind.ToUnit;
                else
                    segment.Kind = SegmentKind.BetweenRefnets;

                if (up.Kind == TopologyNodeKind.Refnet)
                    SetRefnetSegment(up.RefnetIndex, segment);

                double nextDistance = distance + segment.Length;
                if (down.Kind == TopologyNodeKind.Refnet)
                {
                    TopologyRefnet refnet = _topology.Refnets[down.RefnetIndex];
                    if (refnet.UpSegment >= 0)
                    {
                        Warn(
                            "The refnet at " + At(refnet.Inlet) + " is reached twice (a loop); "
                            + "the second pipe was not followed.",
                            refnet.Inlet);
                        return segment.Index;
                    }

                    refnet.UpSegment = segment.Index;
                    int[] ports = _refnetPorts[refnet.Index];
                    for (int role = 0; role < 3; role++)
                    {
                        if (role == refnet.ArrivedAt)
                            continue;
                        foreach (int next in _adjacent[ports[role]].ToArray())
                        {
                            int child = Trace(down.Index, ports[role], next, nextDistance, refnets + 1);
                            if (child >= 0)
                                segment.Children.Add(child);
                        }
                    }
                }
                else if (down.Kind == TopologyNodeKind.Junction)
                {
                    foreach (int next in _adjacent[current].ToArray())
                    {
                        if (_pieces[next].Used)
                            continue;
                        int child = Trace(down.Index, current, next, nextDistance, refnets);
                        if (child >= 0)
                            segment.Children.Add(child);
                    }
                }

                return segment.Index;
            }

            private TopologyNode ArriveAtRefnet(TopologySegment segment, PortOwner owner, int point)
            {
                TopologyRefnet refnet = _topology.Refnets[owner.Refnet];
                int node;
                if (!_refnetNode.TryGetValue(refnet.Index, out node))
                {
                    TopologyNode created = AddNode(TopologyNodeKind.Refnet, refnet.Inlet);
                    created.RefnetIndex = refnet.Index;
                    refnet.Node = created.Index;
                    refnet.ArrivedAt = owner.Role;
                    _refnetNode[refnet.Index] = created.Index;
                    node = created.Index;
                    if (owner.Role != 0)
                    {
                        string port = owner.Role == 1 ? "main outlet" : "branch outlet";
                        Warn(
                            "The pipe from the root reaches the " + port + " of the refnet at "
                            + At(refnet.Inlet) + ", not its inlet. Check which way the refnet faces.",
                            refnet.Inlet);
                    }
                }

                return _topology.Nodes[node];
            }

            private void SetRefnetSegment(int refnetIndex, TopologySegment segment)
            {
                TopologyRefnet refnet = _topology.Refnets[refnetIndex];
                int[] ports = _refnetPorts[refnetIndex];
                Vec2 start = segment.Points[0];
                // The segment leaves the refnet from the port it starts at.
                for (int role = 0; role < 3; role++)
                {
                    if (_points[ports[role]].DistanceTo(start) > _tolerance)
                        continue;
                    if (role == 1 && refnet.MainSegment < 0)
                        refnet.MainSegment = segment.Index;
                    else if (role == 2 && refnet.BranchSegment < 0)
                        refnet.BranchSegment = segment.Index;
                    return;
                }
            }

            private static void AppendPoints(TopologySegment segment, Piece piece, bool forward)
            {
                int count = piece.Points.Count;
                for (int k = 0; k < count; k++)
                {
                    Vec2 point = forward ? piece.Points[k] : piece.Points[count - 1 - k];
                    if (segment.Points.Count > 0 && k == 0)
                        continue;
                    segment.Points.Add(point);
                }
            }

            // Fills each segment's downstream ends, from the leaves back.
            private void CollectEnds()
            {
                for (int i = _topology.Segments.Count - 1; i >= 0; i--)
                {
                    TopologySegment segment = _topology.Segments[i];
                    TopologyNode down = _topology.Nodes[segment.DownNode];
                    if (down.Kind == TopologyNodeKind.End)
                    {
                        segment.DownstreamEnds.Add(down.EndIndex);
                        continue;
                    }

                    foreach (int child in segment.Children)
                        segment.DownstreamEnds.AddRange(_topology.Segments[child].DownstreamEnds);
                    segment.DownstreamEnds.Sort();
                }
            }
        }
    }
}
