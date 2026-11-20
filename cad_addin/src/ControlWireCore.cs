using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.ControlWires
{
    // AutoCAD-free core of HVACCTRLWIRE: the remembered dialog values, the 2D transform that carries
    // the template unit's terminal point onto another unit of the same block, and the planner that
    // lays the control wires out parallel to the template route. Plain doubles and Vec2 only, so a
    // console test project can link it. See AGENTS.md, "HVACCTRLWIRE: draw control wires from a
    // template route".

    /// <summary>HVACCTRLWIRE values: the offset spacing and the cable text. The layer is fixed.</summary>
    public sealed class ControlWireSettings
    {
        /// <summary>Layer of the wires and the cable text (ACI 11, linetype Broken Line in the template).</summary>
        public const string LayerName = "EIA_5 CONTROL WIRE";

        /// <summary>Linetype of <see cref="LayerName"/> in the template.</summary>
        public const string LinetypeName = "Broken Line";

        /// <summary>ACI colour of <see cref="LayerName"/> in the template.</summary>
        public const short LayerColorIndex = 11;

        /// <summary>Text style of the template's cable text; the current style is used when it is missing.</summary>
        public const string TextStyleName = "T_unicode";

        /// <summary>Extended-data application name of the wires and texts this command writes.</summary>
        public const string XDataApp = "HVACAI_CTRLWIRE";

        /// <summary>Distance between the template and the first wire, and between neighbouring wires (mm).</summary>
        public double Spacing = 150;

        public bool WriteText = true;
        public string TextString = "2x0.75 MM2";
        public double TextHeight = 275;

        public bool IsValid(out string error)
        {
            error = null;
            if (double.IsNaN(Spacing) || double.IsInfinity(Spacing) || Spacing <= 0 || Spacing > 10000)
                error = "The offset spacing must be above 0 and at most 10000 mm.";
            else if (WriteText && string.IsNullOrWhiteSpace(TextString))
                error = "The cable text is empty.";
            else if (WriteText && (double.IsNaN(TextHeight) || double.IsInfinity(TextHeight)
                || TextHeight <= 0 || TextHeight > 100000))
                error = "The text height must be above 0 and at most 100000.";
            return error == null;
        }

        public string Serialize()
        {
            StringBuilder text = new StringBuilder();
            Add(text, "spacing", Num(Spacing));
            Add(text, "writetext", WriteText ? "1" : "0");
            Add(text, "text", (TextString ?? "").Replace('\r', ' ').Replace('\n', ' '));
            Add(text, "height", Num(TextHeight));
            return text.ToString();
        }

        /// <summary>Never throws: unknown keys and bad values keep the defaults.</summary>
        public static ControlWireSettings Deserialize(string text)
        {
            ControlWireSettings settings = new ControlWireSettings();
            if (string.IsNullOrEmpty(text))
                return settings;

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                int equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;
                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1);

                double number;
                switch (key)
                {
                    case "spacing":
                        if (TryNumber(value, out number) && number > 0 && number <= 10000)
                            settings.Spacing = number;
                        break;
                    case "writetext":
                        settings.WriteText = value.Trim() != "0";
                        break;
                    case "text":
                        if (!string.IsNullOrWhiteSpace(value))
                            settings.TextString = value.Trim();
                        break;
                    case "height":
                        if (TryNumber(value, out number) && number > 0 && number <= 100000)
                            settings.TextHeight = number;
                        break;
                }
            }

            return settings;
        }

        private static bool TryNumber(string value, out double number)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                && !double.IsNaN(number) && !double.IsInfinity(number);
        }

        private static void Add(StringBuilder text, string key, string value)
        {
            text.Append(key).Append('=').Append(value).Append('\n');
        }

        private static string Num(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// A 2D affine map, x' = A x + B y + Tx and y' = C x + D y + Ty: the plan part of a block's
    /// BlockTransform (rotation, scale, mirroring and position).
    /// </summary>
    public struct ControlWireAffine
    {
        public readonly double A;
        public readonly double B;
        public readonly double C;
        public readonly double D;
        public readonly double Tx;
        public readonly double Ty;

        public ControlWireAffine(double a, double b, double c, double d, double tx, double ty)
        {
            A = a;
            B = b;
            C = c;
            D = d;
            Tx = tx;
            Ty = ty;
        }

        public static ControlWireAffine Identity
        {
            get { return new ControlWireAffine(1, 0, 0, 1, 0, 0); }
        }

        /// <summary>From the 16 values of Matrix3d.ToArray() (row-major 4x4, column vectors).</summary>
        public static ControlWireAffine FromMatrixArray(double[] m)
        {
            if (m == null || m.Length != 16)
                throw new ArgumentException("A 4x4 matrix has 16 values.");
            return new ControlWireAffine(m[0], m[1], m[4], m[5], m[3], m[7]);
        }

        public Vec2 Apply(Vec2 p)
        {
            return new Vec2(A * p.X + B * p.Y + Tx, C * p.X + D * p.Y + Ty);
        }

        /// <summary>False when the map is singular (a zero scale).</summary>
        public bool TryInverse(out ControlWireAffine inverse)
        {
            double det = A * D - B * C;
            if (Math.Abs(det) < 1e-12)
            {
                inverse = Identity;
                return false;
            }

            double ia = D / det;
            double ib = -B / det;
            double ic = -C / det;
            double id = A / det;
            inverse = new ControlWireAffine(ia, ib, ic, id, -(ia * Tx + ib * Ty), -(ic * Tx + id * Ty));
            return true;
        }

        /// <summary>
        /// The template unit's terminal point, in the template unit's block-local coordinates, carried
        /// into the world by another unit's transform. False when the template unit's map is singular.
        /// </summary>
        public static bool TryMapTerminal(
            Vec2 templateTerminal, ControlWireAffine templateUnit, ControlWireAffine otherUnit, out Vec2 terminal)
        {
            ControlWireAffine inverse;
            if (!templateUnit.TryInverse(out inverse))
            {
                terminal = templateTerminal;
                return false;
            }

            terminal = otherUnit.Apply(inverse.Apply(templateTerminal));
            return true;
        }
    }

    /// <summary>An indoor unit to wire, with the point its wire must end on.</summary>
    public sealed class ControlWireUnit
    {
        public string Name;
        public Vec2 Terminal;
    }

    public sealed class ControlWireInput
    {
        /// <summary>The template route from the thermostat point to the template unit's terminal.</summary>
        public IList<OutlineVertex> Route;

        public bool RouteClosed;

        /// <summary>The units that need a new wire (the template's own unit is served by the template).</summary>
        public IList<ControlWireUnit> Units;

        /// <summary>The offset step d (mm).</summary>
        public double Spacing = 150;

        /// <summary>Text height, for placing the cable text beside the exit leg (mm).</summary>
        public double TextHeight = 275;

        /// <summary>A terminal this far beyond an end of the route is reported (mm).</summary>
        public double FarDistance = 3000;
    }

    public sealed class ControlWireWarning
    {
        public Vec2 Point;
        public string Message;
    }

    /// <summary>One new wire: an open polyline parallel to the route, then a connection to its unit.</summary>
    public sealed class ControlWire
    {
        /// <summary>W1, W2... in the order the wires leave the route, nearest the thermostat first.</summary>
        public string Label;

        public string UnitName;

        /// <summary>Index into <see cref="ControlWireInput.Units"/>.</summary>
        public int UnitIndex;

        /// <summary>+1 for the left of the route (looking from the thermostat), -1 for the right.</summary>
        public int Side;

        /// <summary>The wire is k * d from the route (k = 1 is the nearest).</summary>
        public int K;

        /// <summary>Signed distance from the route: Side * K * d.</summary>
        public double Offset;

        /// <summary>Distance along the route from the thermostat to the point nearest the unit (mm).</summary>
        public double ExitDistance;

        /// <summary>Vertices from the thermostat end to the unit's terminal point.</summary>
        public readonly List<Vec2> Points = new List<Vec2>();

        /// <summary>Index in <see cref="Points"/> where the wire leaves the parallel run.</summary>
        public int ExitIndex;

        /// <summary>The cable text: its middle point, and a readable rotation (radians).</summary>
        public Vec2 TextPoint;

        public double TextRotation;

        /// <summary>Corners where this wire's offset fails (sharp corner or a short segment).</summary>
        internal readonly List<Vec2> DegenerateAt = new List<Vec2>();

        public double Length
        {
            get
            {
                double total = 0;
                for (int i = 1; i < Points.Count; i++)
                    total += Points[i - 1].DistanceTo(Points[i]);
                return total;
            }
        }
    }

    public sealed class ControlWirePlan
    {
        public bool Ok;
        public string Error;

        /// <summary>The route as used: duplicate points removed, thermostat end first.</summary>
        public readonly List<Vec2> Route = new List<Vec2>();

        public readonly List<ControlWire> Wires = new List<ControlWire>();
        public readonly List<ControlWireWarning> Warnings = new List<ControlWireWarning>();
    }

    public static class ControlWirePlanner
    {
        private const double Eps = 1e-6;

        /// <summary>A mitre longer than this many offsets means the corner is too sharp to offset.</summary>
        private const double MitreLimit = 5.0;

        /// <summary>Sides with at most this many wires are re-ordered by search when the default order crosses.</summary>
        private const int MaxPermuted = 6;

        /// <summary>The route must be an open chain of straight segments.</summary>
        public static bool CheckRoute(IList<OutlineVertex> route, bool closed, out string error)
        {
            error = null;
            if (route == null || route.Count < 2)
            {
                error = "The template route needs at least two points.";
                return false;
            }

            if (closed)
            {
                error = "A closed template route is not supported: pick an open line or polyline.";
                return false;
            }

            // An open polyline's last bulge belongs to no segment.
            for (int i = 0; i < route.Count - 1; i++)
            {
                if (Math.Abs(route[i].Bulge) > 1e-9)
                {
                    error = "The template route has an arc: arcs are not supported. Use straight segments.";
                    return false;
                }
            }

            return true;
        }

        public static ControlWirePlan Plan(ControlWireInput input)
        {
            ControlWirePlan plan = new ControlWirePlan();
            if (input == null || input.Route == null || input.Units == null)
            {
                plan.Error = "No route or units were given.";
                return plan;
            }

            if (double.IsNaN(input.Spacing) || double.IsInfinity(input.Spacing) || input.Spacing <= 0)
            {
                plan.Error = "The offset spacing must be above 0.";
                return plan;
            }

            string error;
            if (!CheckRoute(input.Route, input.RouteClosed, out error))
            {
                plan.Error = error;
                return plan;
            }

            RouteGeometry route = RouteGeometry.Build(input.Route);
            if (route == null)
            {
                plan.Error = "The template route has no length.";
                return plan;
            }

            plan.Route.AddRange(route.P);

            // Where each unit meets the route, and which side it is on.
            List<Placement> placements = new List<Placement>();
            for (int i = 0; i < input.Units.Count; i++)
                placements.Add(Place(route, input.Units[i].Terminal, i));

            List<Placement> left = new List<Placement>();
            List<Placement> right = new List<Placement>();
            foreach (Placement placement in placements)
            {
                if (placement.Side > 0)
                    left.Add(placement);
                else if (placement.Side < 0)
                    right.Add(placement);
            }

            // A terminal exactly on the route goes to the side with fewer wires.
            foreach (Placement placement in placements)
            {
                if (placement.Side != 0)
                    continue;
                if (left.Count <= right.Count)
                {
                    placement.Side = 1;
                    left.Add(placement);
                }
                else
                {
                    placement.Side = -1;
                    right.Add(placement);
                }
            }

            List<ControlWire> wires = new List<ControlWire>();
            wires.AddRange(WiresOfSide(route, input, left, 1));
            wires.AddRange(WiresOfSide(route, input, right, -1));

            wires.Sort(delegate (ControlWire a, ControlWire b)
            {
                if (Math.Abs(a.ExitDistance - b.ExitDistance) > Eps)
                    return a.ExitDistance.CompareTo(b.ExitDistance);
                if (a.Side != b.Side)
                    return b.Side.CompareTo(a.Side);
                return a.UnitIndex.CompareTo(b.UnitIndex);
            });

            for (int i = 0; i < wires.Count; i++)
            {
                wires[i].Label = "W" + (i + 1).ToString(CultureInfo.InvariantCulture);
                PlaceText(wires[i], input.TextHeight);
            }

            plan.Wires.AddRange(wires);
            AddWarnings(plan, route, input, placements);
            plan.Ok = true;
            return plan;
        }

        // ------------------------------------------------------------------ route geometry

        private sealed class RouteGeometry
        {
            public Vec2[] P;
            public Vec2[] T;
            public Vec2[] N;
            public double[] Len;
            public double[] Start;
            public double Total;

            public int Segments
            {
                get { return T.Length; }
            }

            /// <summary>Null when the route has no length. Consecutive duplicate points are dropped.</summary>
            public static RouteGeometry Build(IList<OutlineVertex> vertices)
            {
                List<Vec2> points = new List<Vec2>();
                foreach (OutlineVertex vertex in vertices)
                {
                    Vec2 point = new Vec2(vertex.X, vertex.Y);
                    if (points.Count == 0 || points[points.Count - 1].DistanceTo(point) > Eps)
                        points.Add(point);
                }

                if (points.Count < 2)
                    return null;

                int count = points.Count - 1;
                RouteGeometry geometry = new RouteGeometry
                {
                    P = points.ToArray(),
                    T = new Vec2[count],
                    N = new Vec2[count],
                    Len = new double[count],
                    Start = new double[count]
                };

                double total = 0;
                for (int i = 0; i < count; i++)
                {
                    Vec2 direction = geometry.P[i + 1] - geometry.P[i];
                    geometry.Len[i] = direction.Length;
                    geometry.T[i] = direction.Normalized();
                    geometry.N[i] = geometry.T[i].Left;
                    geometry.Start[i] = total;
                    total += geometry.Len[i];
                }

                geometry.Total = total;
                return geometry;
            }

            /// <summary>
            /// The offset vertex at route vertex j for a signed offset o (positive is left). Interior
            /// vertices are mitre joins. degenerate is set for a fold-back or a very long mitre.
            /// </summary>
            public Vec2 OffsetVertex(int j, double o, out bool degenerate)
            {
                degenerate = false;
                if (j == 0)
                    return P[0] + N[0] * o;
                if (j == Segments)
                    return P[j] + N[j - 1] * o;

                double cross = T[j - 1].Cross(T[j]);
                if (Math.Abs(cross) < 1e-9)
                {
                    if (T[j - 1].Dot(T[j]) < 0)
                        degenerate = true;
                    return P[j] + N[j - 1] * o;
                }

                Vec2 a = P[j] + N[j - 1] * o;
                Vec2 b = P[j] + N[j] * o;
                double s = (b - a).Cross(T[j]) / cross;
                Vec2 mitre = a + T[j - 1] * s;
                if ((mitre - P[j]).Length > MitreLimit * Math.Abs(o))
                    degenerate = true;
                return mitre;
            }
        }

        private sealed class Placement
        {
            public int Unit;
            public int Segment;
            public double Along;
            public double Lateral;
            public int Side;
            public double Distance;
            public double ExitArc;
            public double Overshoot;
        }

        /// <summary>The point of the route nearest a terminal (clamped to the route's ends) and its side.</summary>
        private static Placement Place(RouteGeometry route, Vec2 terminal, int unitIndex)
        {
            int best = 0;
            double bestDistance = double.MaxValue;
            for (int i = 0; i < route.Segments; i++)
            {
                double along = (terminal - route.P[i]).Dot(route.T[i]);
                double clamped = Math.Max(0, Math.Min(route.Len[i], along));
                double distance = (terminal - (route.P[i] + route.T[i] * clamped)).Length;
                if (distance < bestDistance - 1e-9)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            double a = (terminal - route.P[best]).Dot(route.T[best]);
            double c = Math.Max(0, Math.Min(route.Len[best], a));
            double lateral = (terminal - route.P[best]).Dot(route.N[best]);

            double overshoot = 0;
            if (best == 0 && a < 0)
                overshoot = -a;
            else if (best == route.Segments - 1 && a > route.Len[best])
                overshoot = a - route.Len[best];

            return new Placement
            {
                Unit = unitIndex,
                Segment = best,
                Along = a,
                Lateral = lateral,
                Side = lateral > Eps ? 1 : (lateral < -Eps ? -1 : 0),
                Distance = bestDistance,
                ExitArc = route.Start[best] + c,
                Overshoot = overshoot
            };
        }

        // ------------------------------------------------------------------ wires of one side

        /// <summary>
        /// Gives each unit on one side its k. The wire that leaves the route first goes on the outside
        /// (largest k), so its exit leg does not cut across the wires that run on, and the last to
        /// leave is the nearest the template (k = 1). When that order still crosses (a corner can do
        /// it), every order of up to six wires is tried and the one with fewest crossings is kept.
        /// </summary>
        private static List<ControlWire> WiresOfSide(
            RouteGeometry route, ControlWireInput input, List<Placement> side, int sign)
        {
            List<ControlWire> result = new List<ControlWire>();
            if (side.Count == 0)
                return result;

            List<Placement> order = new List<Placement>(side);
            order.Sort(delegate (Placement a, Placement b)
            {
                if (Math.Abs(a.ExitArc - b.ExitArc) > Eps)
                    return a.ExitArc.CompareTo(b.ExitArc);
                int byLateral = Math.Abs(b.Lateral).CompareTo(Math.Abs(a.Lateral));
                return byLateral != 0 ? byLateral : a.Unit.CompareTo(b.Unit);
            });

            List<ControlWire> best = BuildSide(route, input, order, sign);
            int bestCrossings = CountCrossings(best);
            if (bestCrossings > 0 && order.Count <= MaxPermuted)
            {
                List<Placement> trial = new List<Placement>(order);
                SearchOrders(route, input, trial, 0, sign, ref best, ref bestCrossings);
            }

            return best;
        }

        private static List<ControlWire> BuildSide(
            RouteGeometry route, ControlWireInput input, List<Placement> order, int sign)
        {
            List<ControlWire> wires = new List<ControlWire>();
            for (int rank = 0; rank < order.Count; rank++)
            {
                int k = order.Count - rank;
                wires.Add(BuildWire(route, input, order[rank], sign, k));
            }

            return wires;
        }

        private static void SearchOrders(
            RouteGeometry route,
            ControlWireInput input,
            List<Placement> order,
            int index,
            int sign,
            ref List<ControlWire> best,
            ref int bestCrossings)
        {
            if (bestCrossings == 0)
                return;

            if (index == order.Count)
            {
                List<ControlWire> candidate = BuildSide(route, input, order, sign);
                int crossings = CountCrossings(candidate);
                if (crossings < bestCrossings)
                {
                    best = candidate;
                    bestCrossings = crossings;
                }

                return;
            }

            for (int i = index; i < order.Count; i++)
            {
                Placement swap = order[index];
                order[index] = order[i];
                order[i] = swap;
                SearchOrders(route, input, order, index + 1, sign, ref best, ref bestCrossings);
                swap = order[index];
                order[index] = order[i];
                order[i] = swap;
            }
        }

        private static ControlWire BuildWire(
            RouteGeometry route, ControlWireInput input, Placement placement, int sign, int k)
        {
            double offset = sign * k * input.Spacing;
            int i = placement.Segment;
            ControlWireUnit unit = input.Units[placement.Unit];

            ControlWire wire = new ControlWire
            {
                UnitName = unit.Name,
                UnitIndex = placement.Unit,
                Side = sign,
                K = k,
                Offset = offset,
                ExitDistance = placement.ExitArc
            };

            // Offset vertices 0..i+1: the parallel run, and the end of the segment the wire leaves.
            Vec2[] offsetVertex = new Vec2[i + 2];
            for (int j = 0; j <= i + 1; j++)
            {
                bool degenerate;
                offsetVertex[j] = route.OffsetVertex(j, offset, out degenerate);
                if (degenerate && j >= 1 && j <= i)
                    wire.DegenerateAt.Add(route.P[j]);
            }

            for (int j = 0; j <= i; j++)
            {
                // A segment whose offset runs backwards turns into a swallow-tail.
                if ((offsetVertex[j + 1] - offsetVertex[j]).Dot(route.T[j]) < -Eps)
                    wire.DegenerateAt.Add(route.P[j + 1]);
            }

            for (int j = 0; j <= i; j++)
                AddPoint(wire.Points, offsetVertex[j]);

            // The exit point: the foot of the terminal on the route, moved out to the offset line.
            // A mitre can shorten the offset segment, so it is kept on that segment.
            double foot = Math.Max(0, Math.Min(route.Len[i], placement.Along));
            Vec2 wanted = route.P[i] + route.T[i] * foot + route.N[i] * offset;
            double q = (wanted - offsetVertex[i]).Dot(route.T[i]);
            double offsetLength = (offsetVertex[i + 1] - offsetVertex[i]).Dot(route.T[i]);
            q = Math.Max(0, Math.Min(Math.Max(0, offsetLength), q));
            Vec2 exit = offsetVertex[i] + route.T[i] * q;
            AddPoint(wire.Points, exit);
            wire.ExitIndex = wire.Points.Count - 1;

            // The connection: out along the segment's normal to the terminal's lateral position, then
            // along the segment to the terminal (no second leg when it is straight out).
            double delta = placement.Lateral - offset;
            Vec2 corner = exit;
            if (Math.Abs(delta) > Eps)
            {
                corner = exit + route.N[i] * delta;
                AddPoint(wire.Points, corner);
            }

            Vec2 end = unit.Terminal;
            if (wire.Points[wire.Points.Count - 1].DistanceTo(end) > Eps)
                wire.Points.Add(end);
            else
                wire.Points[wire.Points.Count - 1] = end;

            return wire;
        }

        private static void AddPoint(List<Vec2> points, Vec2 point)
        {
            if (points.Count == 0 || points[points.Count - 1].DistanceTo(point) > 1e-9)
                points.Add(point);
        }

        // ------------------------------------------------------------------ cable text

        /// <summary>Puts the text beside the longest leg of the connection, one text height off it.</summary>
        private static void PlaceText(ControlWire wire, double height)
        {
            if (wire.Points.Count < 2)
                return;

            int from = Math.Min(wire.ExitIndex, wire.Points.Count - 2);
            int bestStart = from;
            double bestLength = -1;
            for (int j = from; j < wire.Points.Count - 1; j++)
            {
                double length = wire.Points[j].DistanceTo(wire.Points[j + 1]);
                if (length > bestLength + 1e-9)
                {
                    bestLength = length;
                    bestStart = j;
                }
            }

            Vec2 a = wire.Points[bestStart];
            Vec2 b = wire.Points[bestStart + 1];
            Vec2 direction = (b - a).Normalized();
            double angle = Math.Atan2(direction.Y, direction.X);

            // Readable: the text runs to the right, or upward when the leg is vertical.
            if (angle > Math.PI / 2 + 1e-9)
                angle -= Math.PI;
            else if (angle <= -Math.PI / 2 + 1e-9)
                angle += Math.PI;

            Vec2 readable = Vec2.FromAngle(angle);
            wire.TextRotation = angle;
            wire.TextPoint = (a + b) / 2 + readable.Left * height;
        }

        // ------------------------------------------------------------------ crossings and warnings

        private static int CountCrossings(List<ControlWire> wires)
        {
            int count = 0;
            for (int i = 0; i < wires.Count; i++)
            {
                for (int j = i + 1; j < wires.Count; j++)
                {
                    Vec2 point;
                    if (WiresMeet(wires[i], wires[j], out point))
                        count++;
                }
            }

            return count;
        }

        private static bool WiresMeet(ControlWire a, ControlWire b, out Vec2 point)
        {
            for (int i = 0; i + 1 < a.Points.Count; i++)
            {
                for (int j = 0; j + 1 < b.Points.Count; j++)
                {
                    if (SegmentsMeet(a.Points[i], a.Points[i + 1], b.Points[j], b.Points[j + 1], out point))
                        return true;
                }
            }

            point = new Vec2(0, 0);
            return false;
        }

        /// <summary>
        /// True when two segments touch, cross or overlap. Zero-length segments never meet anything.
        /// </summary>
        internal static bool SegmentsMeet(Vec2 a0, Vec2 a1, Vec2 b0, Vec2 b1, out Vec2 point)
        {
            point = a0;
            Vec2 r = a1 - a0;
            Vec2 s = b1 - b0;
            double rl = r.Length;
            double sl = s.Length;
            if (rl < 1e-9 || sl < 1e-9)
                return false;

            double denominator = r.Cross(s);
            Vec2 qp = b0 - a0;
            if (Math.Abs(denominator) <= 1e-9 * rl * sl)
            {
                // Parallel: they meet only when collinear and the intervals overlap or touch.
                if (Math.Abs(qp.Cross(r)) > Eps * rl)
                    return false;
                double t0 = qp.Dot(r) / (rl * rl);
                double t1 = t0 + s.Dot(r) / (rl * rl);
                double low = Math.Max(0, Math.Min(t0, t1));
                double high = Math.Min(1, Math.Max(t0, t1));
                if (high < low - Eps / rl)
                    return false;
                point = a0 + r * ((low + high) / 2);
                return true;
            }

            double t = qp.Cross(s) / denominator;
            double u = qp.Cross(r) / denominator;
            double tolT = Eps / rl;
            double tolU = Eps / sl;
            if (t < -tolT || t > 1 + tolT || u < -tolU || u > 1 + tolU)
                return false;
            point = a0 + r * t;
            return true;
        }

        private static void AddWarnings(
            ControlWirePlan plan, RouteGeometry route, ControlWireInput input, List<Placement> placements)
        {
            foreach (ControlWire wire in plan.Wires)
            {
                foreach (Vec2 corner in wire.DegenerateAt)
                {
                    plan.Warnings.Add(new ControlWireWarning
                    {
                        Point = corner,
                        Message = wire.Label + " (" + wire.UnitName + "): its offset breaks down at this corner "
                            + "(sharp corner or a short segment); check the wire there."
                    });
                }

                Placement placement = placements[wire.UnitIndex];
                if (placement.Overshoot > input.FarDistance)
                {
                    plan.Warnings.Add(new ControlWireWarning
                    {
                        Point = input.Units[wire.UnitIndex].Terminal,
                        Message = wire.Label + " (" + wire.UnitName + "): the unit is "
                            + Metres(placement.Overshoot) + " m beyond the end of the template route; "
                            + "its wire runs the whole route and then leaves."
                    });
                }

                if (Math.Abs(placement.Lateral) < wire.K * input.Spacing - Eps)
                {
                    plan.Warnings.Add(new ControlWireWarning
                    {
                        Point = input.Units[wire.UnitIndex].Terminal,
                        Message = wire.Label + " (" + wire.UnitName + "): the unit is closer to the route than "
                            + "the wire's offset, so the connection turns back toward the route."
                    });
                }
            }

            for (int i = 0; i < plan.Wires.Count; i++)
            {
                for (int j = i + 1; j < plan.Wires.Count; j++)
                {
                    Vec2 point;
                    if (WiresMeet(plan.Wires[i], plan.Wires[j], out point))
                    {
                        plan.Warnings.Add(new ControlWireWarning
                        {
                            Point = point,
                            Message = plan.Wires[i].Label + " and " + plan.Wires[j].Label + " cross or overlap."
                        });
                    }
                }

                ControlWire wire = plan.Wires[i];
                bool crossesTemplate = false;
                for (int a = 0; a + 1 < wire.Points.Count && !crossesTemplate; a++)
                {
                    for (int b = 0; b < route.Segments; b++)
                    {
                        Vec2 point;
                        if (SegmentsMeet(wire.Points[a], wire.Points[a + 1], route.P[b], route.P[b + 1], out point))
                        {
                            plan.Warnings.Add(new ControlWireWarning
                            {
                                Point = point,
                                Message = wire.Label + " (" + wire.UnitName + ") crosses the template route."
                            });
                            crossesTemplate = true;
                            break;
                        }
                    }
                }
            }
        }

        private static string Metres(double millimetres)
        {
            return (millimetres / 1000.0).ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
