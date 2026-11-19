using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HvacAiCadAddin.Ducts
{
    // AutoCAD-free duct drafting maths for HVACDUCT and HVACDUCTALIGN: sizes,
    // fitting dimensions and outlines, route solving and duct alignment. Only
    // plain doubles are used, so tests/DuctCoreTests compiles this file alone.
    // All lengths are drawing units (mm). Formulas come from AGENTS.md,
    // "Drafting commands", "Fitting dimensions".

    /// <summary>A 2D point or vector in plan.</summary>
    public struct Vec2
    {
        public readonly double X;
        public readonly double Y;

        public Vec2(double x, double y)
        {
            X = x;
            Y = y;
        }

        public static Vec2 operator +(Vec2 a, Vec2 b) { return new Vec2(a.X + b.X, a.Y + b.Y); }
        public static Vec2 operator -(Vec2 a, Vec2 b) { return new Vec2(a.X - b.X, a.Y - b.Y); }
        public static Vec2 operator -(Vec2 a) { return new Vec2(-a.X, -a.Y); }
        public static Vec2 operator *(Vec2 a, double k) { return new Vec2(a.X * k, a.Y * k); }
        public static Vec2 operator *(double k, Vec2 a) { return new Vec2(a.X * k, a.Y * k); }
        public static Vec2 operator /(Vec2 a, double k) { return new Vec2(a.X / k, a.Y / k); }

        public double Length { get { return Math.Sqrt(X * X + Y * Y); } }

        /// <summary>Counter-clockwise 90 degree rotation.</summary>
        public Vec2 Left { get { return new Vec2(-Y, X); } }

        public double Dot(Vec2 other) { return X * other.X + Y * other.Y; }
        public double Cross(Vec2 other) { return X * other.Y - Y * other.X; }
        public double DistanceTo(Vec2 other) { return (this - other).Length; }

        /// <summary>Unit vector, or the zero vector when the length is below 1e-12.</summary>
        public Vec2 Normalized()
        {
            double length = Length;
            return length < 1e-12 ? new Vec2(0, 0) : new Vec2(X / length, Y / length);
        }

        public static Vec2 FromAngle(double radians)
        {
            return new Vec2(Math.Cos(radians), Math.Sin(radians));
        }

        public double Angle { get { return Math.Atan2(Y, X); } }

        public override string ToString()
        {
            return "(" + X.ToString("R", CultureInfo.InvariantCulture) + ","
                + Y.ToString("R", CultureInfo.InvariantCulture) + ")";
        }
    }

    /// <summary>
    /// A duct cross section as drawn in plan: <c>REC_&lt;W&gt;x&lt;H&gt;</c> or <c>Round D&lt;d&gt;</c>.
    /// Width is the size seen in plan (W or d); Height is H or d.
    /// </summary>
    public sealed class DuctSize : IEquatable<DuctSize>
    {
        public const double MinimumDimension = 10;
        public const double MaximumDimension = 10000;

        private static readonly Regex RectName = new Regex(
            @"^REC_([0-9]+(?:\.[0-9]+)?)x([0-9]+(?:\.[0-9]+)?)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RoundName = new Regex(
            @"^Round D([0-9]+(?:\.[0-9]+)?)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RectText = new Regex(
            @"^\s*(?:REC_)?([0-9]+(?:[.,][0-9]+)?)\s*[x×*]\s*([0-9]+(?:[.,][0-9]+)?)\s*(?:mm)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RoundText = new Regex(
            @"^\s*(?:Round\s*)?(?:Ø|D|DN)?\s*([0-9]+(?:[.,][0-9]+)?)\s*(?:mm)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RoundPrefix = new Regex(
            @"^\s*(?:Round|Ø|D)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private DuctSize(bool isRound, double width, double height)
        {
            IsRound = isRound;
            Width = width;
            Height = height;
        }

        public bool IsRound { get; private set; }
        public double Width { get; private set; }
        public double Height { get; private set; }

        public static DuctSize Rect(double width, double height)
        {
            return new DuctSize(false, width, height);
        }

        public static DuctSize Round(double diameter)
        {
            return new DuctSize(true, diameter, diameter);
        }

        /// <summary>The dynamic block name: <c>REC_600x300</c> or <c>Round D350</c>.</summary>
        public string BlockName
        {
            get
            {
                return IsRound
                    ? "Round D" + FormatNumber(Width)
                    : "REC_" + FormatNumber(Width) + "x" + FormatNumber(Height);
            }
        }

        /// <summary>Short text for messages and xdata: <c>600x300</c> or <c>D350</c>.</summary>
        public string Label
        {
            get
            {
                return IsRound
                    ? "D" + FormatNumber(Width)
                    : FormatNumber(Width) + "x" + FormatNumber(Height);
            }
        }

        public bool IsValid
        {
            get
            {
                return Width >= MinimumDimension && Width <= MaximumDimension
                    && Height >= MinimumDimension && Height <= MaximumDimension;
            }
        }

        public static string FormatNumber(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>Strict: only the exact block names the template uses.</summary>
        public static bool TryParseBlockName(string name, out DuctSize size)
        {
            size = null;
            string text = (name ?? "").Trim();

            Match rect = RectName.Match(text);
            if (rect.Success)
            {
                double w = ParseNumber(rect.Groups[1].Value);
                double h = ParseNumber(rect.Groups[2].Value);
                if (w > 0 && h > 0)
                {
                    size = Rect(w, h);
                    return true;
                }
                return false;
            }

            Match round = RoundName.Match(text);
            if (round.Success)
            {
                double d = ParseNumber(round.Groups[1].Value);
                if (d > 0)
                {
                    size = Round(d);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Lenient, for typed input. <paramref name="roundShape"/> says which shape the dialog
        /// is on: a rectangular size needs two numbers (600x300, 600*300, 600×300); a round
        /// size takes 350, D350, Ø350 or DN350. A size outside 10..10000 is rejected.
        /// </summary>
        public static bool TryParseText(string text, bool roundShape, out DuctSize size)
        {
            size = null;
            string input = text ?? "";
            DuctSize candidate = null;

            if (!roundShape)
            {
                Match rect = RectText.Match(input);
                if (rect.Success)
                {
                    candidate = Rect(
                        ParseNumber(rect.Groups[1].Value),
                        ParseNumber(rect.Groups[2].Value));
                }
            }
            else
            {
                Match round = RoundText.Match(input);
                if (round.Success)
                    candidate = Round(ParseNumber(round.Groups[1].Value));
            }

            if (candidate == null || !candidate.IsValid)
                return false;
            size = candidate;
            return true;
        }

        /// <summary>
        /// Typed input in either shape, as the Size keyword accepts it: two numbers mean
        /// rectangular, otherwise round. A bare number keeps <paramref name="currentIsRound"/>.
        /// </summary>
        public static bool TryParseAny(string text, bool currentIsRound, out DuctSize size)
        {
            if (TryParseText(text, false, out size))
                return true;

            // A round size needs a D, Ø or Round prefix unless the run is already round.
            bool prefixed = RoundPrefix.IsMatch(text ?? "");
            if (!prefixed && !currentIsRound)
            {
                size = null;
                return false;
            }

            return TryParseText(text, true, out size);
        }

        private static double ParseNumber(string text)
        {
            return double.Parse(
                text.Replace(',', '.'),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture);
        }

        public bool Equals(DuctSize other)
        {
            return other != null &&
                string.Equals(BlockName, other.BlockName, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) { return Equals(obj as DuctSize); }
        public override int GetHashCode() { return BlockName.GetHashCode(); }
        public override string ToString() { return BlockName; }
    }

    /// <summary>Fitting factors, with the approved defaults. The dialog edits these.</summary>
    public sealed class FittingSettings
    {
        /// <summary>Rectangular elbow centreline radius R = factor * W (the user's habit).</summary>
        public double RectElbowRadiusFactor = 0.75;
        /// <summary>Round elbow centreline radius R = factor * d.</summary>
        public double RoundElbowRadiusFactor = 1.5;
        /// <summary>Concentric transition L >= slope * delta (45 degrees included diverging).</summary>
        public double ConcentricSlope = 1.207;
        /// <summary>Transition L >= factor * W_large.</summary>
        public double TransitionMinLengthFactor = 0.5;
        /// <summary>Eccentric transition L >= slope * deltaW (30 degrees from the flat side).</summary>
        public double EccentricSlope = 1.732;
        /// <summary>Rectangular shoe tap lead-in L = max(factor * Wb, minimum).</summary>
        public double TapLeadFactor = 0.5;
        public double TapMinLead = 102;
        /// <summary>Round conical tap: base d + extra, narrowing over cone length, then a collar.</summary>
        public double RoundTapBaseExtra = 51;
        public double RoundTapConeLength = 152;
        public double RoundTapCollar = 51;

        public FittingSettings Clone()
        {
            return (FittingSettings)MemberwiseClone();
        }

        public bool IsValid(out string error)
        {
            error = null;
            if (!(RectElbowRadiusFactor > 0 && RectElbowRadiusFactor <= 10))
                error = "Rectangular elbow radius factor must be between 0 and 10.";
            else if (!(RoundElbowRadiusFactor > 0 && RoundElbowRadiusFactor <= 10))
                error = "Round elbow radius factor must be between 0 and 10.";
            else if (!(ConcentricSlope > 0 && ConcentricSlope <= 20))
                error = "Concentric transition slope must be between 0 and 20.";
            else if (!(EccentricSlope > 0 && EccentricSlope <= 20))
                error = "Eccentric transition slope must be between 0 and 20.";
            else if (!(TransitionMinLengthFactor >= 0 && TransitionMinLengthFactor <= 10))
                error = "Transition minimum length factor must be between 0 and 10.";
            else if (!(TapLeadFactor >= 0 && TapLeadFactor <= 10))
                error = "Tap lead factor must be between 0 and 10.";
            else if (!(TapMinLead >= 0 && TapMinLead <= 2000))
                error = "Tap minimum lead must be between 0 and 2000.";
            else if (!(RoundTapBaseExtra >= 0 && RoundTapBaseExtra <= 1000)
                || !(RoundTapConeLength >= 0 && RoundTapConeLength <= 2000)
                || !(RoundTapCollar >= 0 && RoundTapCollar <= 1000))
                error = "Round tap dimensions are out of range.";
            return error == null;
        }
    }

    /// <summary>One outline vertex: a point and the bulge of the segment that follows it.</summary>
    public struct OutlineVertex
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Bulge;

        public OutlineVertex(double x, double y, double bulge)
        {
            X = x;
            Y = y;
            Bulge = bulge;
        }
    }

    /// <summary>A polyline in plan, with bulges (AutoCAD convention: positive is counter-clockwise).</summary>
    public sealed class Outline
    {
        public readonly List<OutlineVertex> Vertices = new List<OutlineVertex>();
        public bool Closed;
        /// <summary>True for a round elbow's centreline arc (drawn grey, CENTER).</summary>
        public bool IsCentreline;

        public void Add(Vec2 point, double bulge = 0)
        {
            Vertices.Add(new OutlineVertex(point.X, point.Y, bulge));
        }

        public Vec2 PointAt(int index)
        {
            return new Vec2(Vertices[index].X, Vertices[index].Y);
        }

        /// <summary>Exact area of a closed outline, arcs included (always non-negative).</summary>
        public double Area()
        {
            double twiceArea = 0;
            double segments = 0;
            int count = Vertices.Count;
            for (int i = 0; i < count; i++)
            {
                OutlineVertex a = Vertices[i];
                OutlineVertex b = Vertices[(i + 1) % count];
                twiceArea += a.X * b.Y - b.X * a.Y;

                if (Math.Abs(a.Bulge) > 1e-12)
                {
                    double chord = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                    double sweep = 4 * Math.Atan(a.Bulge);
                    double radius = chord / (2 * Math.Sin(sweep / 2));
                    segments += radius * radius / 2 * (sweep - Math.Sin(sweep));
                }
            }

            // Signed area of the chord polygon plus the signed circular segments: a
            // counter-clockwise arc (positive bulge) adds, a clockwise arc subtracts.
            return Math.Abs(twiceArea / 2 + segments);
        }
    }

    /// <summary>An elbow: end-to-end outline plus the leg takeup and arc data.</summary>
    public sealed class ElbowGeometry
    {
        public Outline Shape;
        /// <summary>The centreline arc; used for round elbows.</summary>
        public Outline Centreline;
        public double Takeup;
        public double Radius;
        public double TurnAngle;
        public Vec2 StartPoint;
        public Vec2 EndPoint;
        public Vec2 Centre;
    }

    /// <summary>Dimension rules for every fitting the commands draw.</summary>
    public static class DuctFittings
    {
        /// <summary>Turns smaller than this are straight runs (no elbow).</summary>
        public const double CollinearAngle = 0.5 * Math.PI / 180.0;
        /// <summary>Turns sharper than this are refused: the takeup tends to infinity.</summary>
        public const double MaximumTurnAngle = 170.0 * Math.PI / 180.0;

        /// <summary>
        /// Centreline radius of an elbow in the given size. Never below W/2: a smaller radius
        /// would make the inner wall cross itself, so W/2 (square throat) is the floor.
        /// <paramref name="reduced"/> forces the W/2 fallback.
        /// </summary>
        public static double ElbowRadius(DuctSize size, FittingSettings settings, bool reduced)
        {
            double half = size.Width / 2;
            if (reduced)
                return half;
            double factor = size.IsRound
                ? settings.RoundElbowRadiusFactor
                : settings.RectElbowRadiusFactor;
            return Math.Max(factor * size.Width, half);
        }

        /// <summary>Per-leg takeup R * tan(theta / 2).</summary>
        public static double ElbowTakeup(double radius, double turnAngle)
        {
            return radius * Math.Tan(turnAngle / 2);
        }

        /// <summary>
        /// Concentric transition (also rectangular-to-round and round reducers):
        /// L = max(k0 * W_large, k * dW, k * dH). Plan width and the other dimension are compared.
        /// </summary>
        public static double ConcentricLength(DuctSize from, DuctSize to, FittingSettings settings)
        {
            double large = Math.Max(from.Width, to.Width);
            double deltaWidth = Math.Abs(from.Width - to.Width);
            double deltaHeight = Math.Abs(from.Height - to.Height);
            return Math.Max(
                settings.TransitionMinLengthFactor * large,
                settings.ConcentricSlope * Math.Max(deltaWidth, deltaHeight));
        }

        /// <summary>Eccentric transition (align): L = max(k0 * W_large, slope * dW).</summary>
        public static double EccentricLength(double widthA, double widthB, FittingSettings settings)
        {
            return Math.Max(
                settings.TransitionMinLengthFactor * Math.Max(widthA, widthB),
                settings.EccentricSlope * Math.Abs(widthA - widthB));
        }

        /// <summary>Rectangular shoe tap lead-in L = max(factor * Wb, minimum). The branch starts L from the wall.</summary>
        public static double RectTapLead(double branchWidth, FittingSettings settings)
        {
            return Math.Max(settings.TapLeadFactor * branchWidth, settings.TapMinLead);
        }

        /// <summary>Opening the shoe tap cuts in the main wall: Wb + L.</summary>
        public static double RectTapOpening(double branchWidth, FittingSettings settings)
        {
            return branchWidth + RectTapLead(branchWidth, settings);
        }

        /// <summary>Round conical tap: the branch duct starts cone + collar from the wall (152 + 51 = 203).</summary>
        public static double RoundTapLead(FittingSettings settings)
        {
            return settings.RoundTapConeLength + settings.RoundTapCollar;
        }

        /// <summary>The opening along the main for the branch of this size.</summary>
        public static double TapOpening(DuctSize branch, FittingSettings settings)
        {
            return branch.IsRound
                ? branch.Width + settings.RoundTapBaseExtra
                : RectTapOpening(branch.Width, settings);
        }

        /// <summary>Distance from the main wall to where the branch duct starts.</summary>
        public static double TapLead(DuctSize branch, FittingSettings settings)
        {
            return branch.IsRound ? RoundTapLead(settings) : RectTapLead(branch.Width, settings);
        }

        /// <summary>
        /// Whether a tap whose branch centreline meets the main <paramref name="along"/> from the
        /// main's start lies wholly on the main. A rectangular tap's 45 degree lead-in is upstream
        /// (towards the main's start); a round tap's base is symmetric.
        /// </summary>
        public static bool TapFits(
            double mainLength,
            double along,
            DuctSize branch,
            FittingSettings settings,
            out double upstream,
            out double downstream)
        {
            if (branch.IsRound)
            {
                upstream = TapOpening(branch, settings) / 2;
                downstream = upstream;
            }
            else
            {
                downstream = branch.Width / 2;
                upstream = downstream + RectTapLead(branch.Width, settings);
            }

            return along - upstream >= -1e-6 && along + downstream <= mainLength + 1e-6;
        }
    }

    /// <summary>Outline vertex lists for each fitting, in plan.</summary>
    public static class FittingShapes
    {
        /// <summary>
        /// Elbow at <paramref name="corner"/> (the intersection of the two centrelines).
        /// Outline: outer arc, end face, inner arc, end face. When the radius is W/2 the inner
        /// radius is 0 and the outline has three vertices (square throat).
        /// </summary>
        public static ElbowGeometry Elbow(
            Vec2 corner, Vec2 dirIn, Vec2 dirOut, double width, double radius)
        {
            double cross = dirIn.Cross(dirOut);
            double dot = dirIn.Dot(dirOut);
            double turn = Math.Atan2(Math.Abs(cross), dot);
            double turnSign = cross >= 0 ? 1.0 : -1.0;
            double takeup = DuctFittings.ElbowTakeup(radius, turn);

            Vec2 start = corner - dirIn * takeup;
            Vec2 end = corner + dirOut * takeup;
            Vec2 centre = start + dirIn.Left * (turnSign * radius);
            Vec2 e1 = (start - centre) / radius;
            Vec2 e2 = (end - centre) / radius;

            double outer = radius + width / 2;
            double inner = radius - width / 2;
            double bulge = turnSign * Math.Tan(turn / 4);

            Outline shape = new Outline { Closed = true };
            shape.Add(centre + e1 * outer, bulge);
            shape.Add(centre + e2 * outer);
            if (inner > 1e-9)
            {
                shape.Add(centre + e2 * inner, -bulge);
                shape.Add(centre + e1 * inner);
            }
            else
            {
                shape.Add(centre);
            }

            Outline centreline = new Outline { Closed = false, IsCentreline = true };
            centreline.Add(start, bulge);
            centreline.Add(end);

            return new ElbowGeometry
            {
                Shape = shape,
                Centreline = centreline,
                Takeup = takeup,
                Radius = radius,
                TurnAngle = turn,
                StartPoint = start,
                EndPoint = end,
                Centre = centre
            };
        }

        /// <summary>A plain straight duct outline: length along dir, width across.</summary>
        public static Outline Straight(Vec2 start, Vec2 dir, double length, double width)
        {
            Vec2 side = dir.Left * (width / 2);
            Vec2 end = start + dir * length;
            Outline outline = new Outline { Closed = true };
            outline.Add(start + side);
            outline.Add(end + side);
            outline.Add(end - side);
            outline.Add(start - side);
            return outline;
        }

        /// <summary>Concentric transition: a trapezoid centred on the axis.</summary>
        public static Outline Transition(
            Vec2 start, Vec2 dir, double length, double widthStart, double widthEnd)
        {
            Vec2 left = dir.Left;
            Vec2 end = start + dir * length;
            Outline outline = new Outline { Closed = true };
            outline.Add(start + left * (widthStart / 2));
            outline.Add(end + left * (widthEnd / 2));
            outline.Add(end - left * (widthEnd / 2));
            outline.Add(start - left * (widthStart / 2));
            return outline;
        }

        /// <summary>
        /// Eccentric transition. <paramref name="flatSign"/> is +1 when the flat side is on the
        /// +<paramref name="lateral"/> side of the start centre, -1 for the other side. The end
        /// centre comes back in <paramref name="endCentre"/>.
        /// </summary>
        public static Outline EccentricTransition(
            Vec2 startCentre,
            Vec2 dir,
            Vec2 lateral,
            int flatSign,
            double length,
            double widthStart,
            double widthEnd,
            out Vec2 endCentre)
        {
            double sign = flatSign >= 0 ? 1.0 : -1.0;
            Vec2 flatStart = startCentre + lateral * (sign * widthStart / 2);
            Vec2 farStart = startCentre - lateral * (sign * widthStart / 2);
            endCentre = startCentre + dir * length
                + lateral * (sign * (widthStart - widthEnd) / 2);
            Vec2 flatEnd = endCentre + lateral * (sign * widthEnd / 2);
            Vec2 farEnd = endCentre - lateral * (sign * widthEnd / 2);

            Outline outline = new Outline { Closed = true };
            outline.Add(flatStart);
            outline.Add(farStart);
            outline.Add(farEnd);
            outline.Add(flatEnd);
            return outline;
        }

        /// <summary>
        /// Rectangular shoe tap. <paramref name="wallPoint"/> is where the branch centreline meets
        /// the main wall, <paramref name="downstream"/> the main's flow direction and
        /// <paramref name="outward"/> the branch direction. The 45 degree side is upstream and
        /// the downstream side is square; the opening is Wb + L.
        /// </summary>
        public static Outline RectTap(
            Vec2 wallPoint, Vec2 downstream, Vec2 outward, double branchWidth, double lead)
        {
            double half = branchWidth / 2;
            Outline outline = new Outline { Closed = true };
            outline.Add(wallPoint - downstream * (half + lead));
            outline.Add(wallPoint + downstream * half);
            outline.Add(wallPoint + downstream * half + outward * lead);
            outline.Add(wallPoint - downstream * half + outward * lead);
            return outline;
        }

        /// <summary>
        /// Round conical tap: cone base d + extra on the main wall, narrowing to d over the
        /// cone length, then a collar. The branch duct starts cone + collar from the wall.
        /// </summary>
        public static Outline RoundTap(
            Vec2 wallPoint,
            Vec2 downstream,
            Vec2 outward,
            double diameter,
            FittingSettings settings)
        {
            double baseHalf = (diameter + settings.RoundTapBaseExtra) / 2;
            double half = diameter / 2;
            double cone = settings.RoundTapConeLength;
            double total = cone + settings.RoundTapCollar;

            Outline outline = new Outline { Closed = true };
            outline.Add(wallPoint - downstream * baseHalf);
            outline.Add(wallPoint + downstream * baseHalf);
            outline.Add(wallPoint + downstream * half + outward * cone);
            outline.Add(wallPoint + downstream * half + outward * total);
            outline.Add(wallPoint - downstream * half + outward * total);
            outline.Add(wallPoint - downstream * half + outward * cone);
            return outline;
        }

        public static Outline Tap(
            Vec2 wallPoint,
            Vec2 downstream,
            Vec2 outward,
            DuctSize branch,
            FittingSettings settings)
        {
            return branch.IsRound
                ? RoundTap(wallPoint, downstream, outward, branch.Width, settings)
                : RectTap(
                    wallPoint,
                    downstream,
                    outward,
                    branch.Width,
                    DuctFittings.RectTapLead(branch.Width, settings));
        }
    }

    /// <summary>
    /// A duct reference seen in plan: start (Position), unit axis (the BlockTransform X axis,
    /// which is also right for mirrored inserts), length in drawing units and plan width.
    /// </summary>
    public struct DuctGeometry
    {
        public Vec2 Start;
        public Vec2 Axis;
        public double Length;
        public double Width;

        public DuctGeometry(Vec2 start, Vec2 axis, double length, double width)
        {
            Start = start;
            Axis = axis.Normalized();
            Length = length;
            Width = width;
        }

        public Vec2 End { get { return Start + Axis * Length; } }
        public Vec2 Left { get { return Axis.Left; } }

        /// <summary>Position along the axis of a point, from the start.</summary>
        public double Along(Vec2 point) { return (point - Start).Dot(Axis); }

        /// <summary>Signed offset of a point from the centreline, positive to the left of the axis.</summary>
        public double Across(Vec2 point) { return (point - Start).Dot(Left); }

        /// <summary>The wall segment on one side: +1 left, -1 right.</summary>
        public void Wall(int side, out Vec2 from, out Vec2 to)
        {
            Vec2 offset = Left * (side >= 0 ? Width / 2 : -Width / 2);
            from = Start + offset;
            to = End + offset;
        }
    }

    public enum DuctHitKind
    {
        None,
        StartFace,
        EndFace,
        LeftWall,
        RightWall
    }

    /// <summary>Where a picked point lies on a duct.</summary>
    public sealed class DuctHit
    {
        public DuctHitKind Kind;
        /// <summary>Distance along the axis from the start.</summary>
        public double Along;
        /// <summary>Distance from the picked point to the hit edge.</summary>
        public double Distance;
        /// <summary>The picked point's projection on the edge: the face centre, or the wall foot point.</summary>
        public Vec2 Point;
        /// <summary>
        /// Faces: the direction a run leaves through the face (+axis at the end, -axis at the
        /// start). Walls: the unit normal pointing away from the duct.
        /// </summary>
        public Vec2 Outward;
    }

    public static class DuctHitTest
    {
        /// <summary>
        /// Classifies a point against one duct's end faces and side walls. The nearest edge within
        /// <paramref name="tolerance"/> wins, and a face wins a tie (a corner pick).
        /// </summary>
        public static DuctHit Classify(DuctGeometry duct, Vec2 point, double tolerance)
        {
            double along = duct.Along(point);
            double across = duct.Across(point);
            double half = duct.Width / 2;
            bool insideWidth = Math.Abs(across) <= half + tolerance;
            bool insideLength = along >= -tolerance && along <= duct.Length + tolerance;

            DuctHit best = null;

            if (insideWidth)
            {
                double dStart = Math.Abs(along);
                double dEnd = Math.Abs(along - duct.Length);
                if (dStart <= tolerance)
                {
                    best = new DuctHit
                    {
                        Kind = DuctHitKind.StartFace,
                        Along = 0,
                        Distance = dStart,
                        Point = duct.Start,
                        Outward = -duct.Axis
                    };
                }

                if (dEnd <= tolerance && (best == null || dEnd <= best.Distance))
                {
                    best = new DuctHit
                    {
                        Kind = DuctHitKind.EndFace,
                        Along = duct.Length,
                        Distance = dEnd,
                        Point = duct.End,
                        Outward = duct.Axis
                    };
                }
            }

            if (insideLength)
            {
                double clamped = Math.Max(0, Math.Min(duct.Length, along));
                double dLeft = Math.Abs(across - half);
                double dRight = Math.Abs(across + half);
                if (dLeft <= tolerance && (best == null || dLeft < best.Distance))
                {
                    best = new DuctHit
                    {
                        Kind = DuctHitKind.LeftWall,
                        Along = clamped,
                        Distance = dLeft,
                        Point = duct.Start + duct.Axis * clamped + duct.Left * half,
                        Outward = duct.Left
                    };
                }

                if (dRight <= tolerance && (best == null || dRight < best.Distance))
                {
                    best = new DuctHit
                    {
                        Kind = DuctHitKind.RightWall,
                        Along = clamped,
                        Distance = dRight,
                        Point = duct.Start + duct.Axis * clamped - duct.Left * half,
                        Outward = -duct.Left
                    };
                }
            }

            return best;
        }
    }

    /// <summary>Everything one HVACDUCT dialog remembers between runs.</summary>
    public sealed class DuctDrawSettings
    {
        public bool IsRound;
        public string SizeText = "600x300";
        public string Layer = "";
        public double SegmentLength = 1000;
        public FittingSettings Fittings = new FittingSettings();

        public string Serialize()
        {
            StringBuilder text = new StringBuilder();
            Add(text, "shape", IsRound ? "round" : "rect");
            Add(text, "size", SizeText ?? "");
            Add(text, "layer", Layer ?? "");
            Add(text, "length", Num(SegmentLength));
            Add(text, "rectElbow", Num(Fittings.RectElbowRadiusFactor));
            Add(text, "roundElbow", Num(Fittings.RoundElbowRadiusFactor));
            Add(text, "concentricSlope", Num(Fittings.ConcentricSlope));
            Add(text, "transitionMin", Num(Fittings.TransitionMinLengthFactor));
            Add(text, "eccentricSlope", Num(Fittings.EccentricSlope));
            Add(text, "tapFactor", Num(Fittings.TapLeadFactor));
            Add(text, "tapMin", Num(Fittings.TapMinLead));
            Add(text, "roundTapExtra", Num(Fittings.RoundTapBaseExtra));
            Add(text, "roundTapCone", Num(Fittings.RoundTapConeLength));
            Add(text, "roundTapCollar", Num(Fittings.RoundTapCollar));
            return text.ToString();
        }

        /// <summary>Unknown keys and bad numbers keep the defaults; this never throws.</summary>
        public static DuctDrawSettings Deserialize(string text)
        {
            DuctDrawSettings settings = new DuctDrawSettings();
            if (string.IsNullOrEmpty(text))
                return settings;

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim('\r', ' ');
                int equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;

                string key = line.Substring(0, equals);
                string value = line.Substring(equals + 1);
                double number;
                bool isNumber = double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out number) && !double.IsNaN(number) && !double.IsInfinity(number);

                switch (key)
                {
                    case "shape": settings.IsRound = value == "round"; break;
                    case "size": settings.SizeText = value; break;
                    case "layer": settings.Layer = value; break;
                    case "length": if (isNumber && number > 0) settings.SegmentLength = number; break;
                    case "rectElbow": if (isNumber) settings.Fittings.RectElbowRadiusFactor = number; break;
                    case "roundElbow": if (isNumber) settings.Fittings.RoundElbowRadiusFactor = number; break;
                    case "concentricSlope": if (isNumber) settings.Fittings.ConcentricSlope = number; break;
                    case "transitionMin": if (isNumber) settings.Fittings.TransitionMinLengthFactor = number; break;
                    case "eccentricSlope": if (isNumber) settings.Fittings.EccentricSlope = number; break;
                    case "tapFactor": if (isNumber) settings.Fittings.TapLeadFactor = number; break;
                    case "tapMin": if (isNumber) settings.Fittings.TapMinLead = number; break;
                    case "roundTapExtra": if (isNumber) settings.Fittings.RoundTapBaseExtra = number; break;
                    case "roundTapCone": if (isNumber) settings.Fittings.RoundTapConeLength = number; break;
                    case "roundTapCollar": if (isNumber) settings.Fittings.RoundTapCollar = number; break;
                }
            }

            string error;
            if (!settings.Fittings.IsValid(out error))
                settings.Fittings = new FittingSettings();
            return settings;
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

    /// <summary>Which layers the system-layer list offers.</summary>
    public static class DuctLayerFilter
    {
        private static readonly Regex AirWord = new Regex(
            @"(^|[^A-Za-z])(AIR|SMOKE|PRESSURISATION)([^A-Za-z]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Excluded = new Regex(
            @"TERMINAL|ANNOTATION|AIRFLOW|WATER|FLOW",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// True for names like <c>EIA_1 SUPPLY AIR</c> and <c>EIA_2 EXHAUST SMOKE</c>, false for
        /// terminals, annotations and water layers. Used only to filter names that really
        /// exist in the template or the drawing; nothing is invented.
        /// </summary>
        public static bool IsDuctSystemLayer(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            return AirWord.IsMatch(name) && !Excluded.IsMatch(name);
        }
    }

    // ---------------------------------------------------------------------------------
    // Route solving
    // ---------------------------------------------------------------------------------

    /// <summary>The vertices of a run, with the existing duct (or tap) it starts from.</summary>
    public sealed class RouteInput
    {
        /// <summary>P0..Pn on the centreline; n >= 1 segments.</summary>
        public List<Vec2> Points = new List<Vec2>();
        /// <summary>One size per segment: Sizes[i] is the segment from Points[i] to Points[i+1].</summary>
        public List<DuctSize> Sizes = new List<DuctSize>();
        public FittingSettings Settings = new FittingSettings();

        /// <summary>The existing duct whose open end is Points[0]; null for a free or tap start.</summary>
        public DuctSize PreviousSize;
        /// <summary>The direction of travel arriving at Points[0] through the open end.</summary>
        public Vec2 PreviousDirection;
        /// <summary>Length of the previous duct that can still be used; PositiveInfinity if unlimited.</summary>
        public double PreviousLength = double.PositiveInfinity;

        /// <summary>Tap lead at Points[0] when the run starts as a branch (0 otherwise).</summary>
        public double StartLead;
        /// <summary>Tap lead at the last point when the run ends on a main's wall (0 otherwise).</summary>
        public double EndLead;

        /// <summary>Radius choice per vertex (index 0..n) carried over from earlier solves; may be null.</summary>
        public bool[] CornerReduced;
        /// <summary>Corners at vertices below this index keep their CornerReduced value.</summary>
        public int FrozenCorners;
    }

    public sealed class RouteStraight
    {
        public int Segment;
        public Vec2 Start;
        public Vec2 Direction;
        /// <summary>Centreline length minus the takeups; the block's Distance3.</summary>
        public double Length;
        /// <summary>The full centreline length of the segment.</summary>
        public double CentreLength;
        public DuctSize Size;
    }

    public sealed class RouteFitting
    {
        public const string ElbowKind = "ELBOW";
        public const string TransitionKind = "TRANSITION";
        public const string TapKind = "TAP";

        public int Vertex;
        public string Kind;
        public string SizeText;
        public Outline Shape;
        /// <summary>The centreline arc of a round elbow, otherwise null.</summary>
        public Outline Centreline;
    }

    public sealed class RouteResult
    {
        public bool Ok;
        public string Error;
        public readonly List<RouteStraight> Straights = new List<RouteStraight>();
        public readonly List<RouteFitting> Fittings = new List<RouteFitting>();
        /// <summary>How much the previous duct shrinks at its open end (0 when none).</summary>
        public double PreviousShorten;
        /// <summary>The solved radius choice per vertex, to carry into the next solve.</summary>
        public bool[] CornerReduced;
        /// <summary>Notes such as a corner that fell back to R = W/2.</summary>
        public readonly List<string> Notes = new List<string>();
    }

    public static class RouteSolver
    {
        private const double LengthTolerance = 1e-6;

        public static RouteResult Solve(RouteInput input)
        {
            RouteResult result = new RouteResult();
            int segments = input.Points.Count - 1;
            if (segments < 1 || input.Sizes.Count != segments)
            {
                result.Error = "A run needs at least one segment and one size per segment.";
                return result;
            }

            FittingSettings settings = input.Settings;
            double[] length = new double[segments];
            Vec2[] direction = new Vec2[segments];
            for (int i = 0; i < segments; i++)
            {
                Vec2 delta = input.Points[i + 1] - input.Points[i];
                length[i] = delta.Length;
                if (length[i] < 1e-6)
                {
                    result.Error = "Segment " + (i + 1) + " has zero length.";
                    return result;
                }

                direction[i] = delta / length[i];
            }

            bool[] reduced = new bool[segments + 1];
            if (input.CornerReduced != null)
            {
                for (int i = 0; i < reduced.Length && i < input.CornerReduced.Length; i++)
                    reduced[i] = input.CornerReduced[i];
            }

            bool hasPrevious = input.PreviousSize != null;
            Vec2[] inDirection = new Vec2[segments];
            DuctSize[] sizeIn = new DuctSize[segments];
            bool[] hasElbow = new bool[segments];
            double[] turn = new double[segments];
            bool[] hasTransition = new bool[segments];

            for (int v = 0; v < segments; v++)
            {
                if (v == 0)
                {
                    if (!hasPrevious)
                        continue;
                    inDirection[0] = input.PreviousDirection.Normalized();
                    sizeIn[0] = input.PreviousSize;
                }
                else
                {
                    inDirection[v] = direction[v - 1];
                    sizeIn[v] = input.Sizes[v - 1];
                }

                double cross = inDirection[v].Cross(direction[v]);
                double dot = inDirection[v].Dot(direction[v]);
                turn[v] = Math.Atan2(Math.Abs(cross), dot);
                hasElbow[v] = turn[v] > DuctFittings.CollinearAngle;
                if (turn[v] > DuctFittings.MaximumTurnAngle)
                {
                    result.Error = "The turn at point " + (v + 1) + " is too sharp for an elbow.";
                    return result;
                }

                hasTransition[v] = !sizeIn[v].Equals(input.Sizes[v]);
            }

            double[] elbowTakeup = new double[segments];
            double[] transitionLength = new double[segments];
            double[] outTake = new double[segments];
            double[] inTake = new double[segments + 1];
            double previousAvailable = 0;

            int guard = 4 * segments + 8;
            while (true)
            {
                for (int v = 0; v < segments; v++)
                {
                    elbowTakeup[v] = 0;
                    transitionLength[v] = 0;
                    if (hasElbow[v])
                    {
                        double radius = DuctFittings.ElbowRadius(sizeIn[v], settings, reduced[v]);
                        elbowTakeup[v] = DuctFittings.ElbowTakeup(radius, turn[v]);
                    }

                    if (hasTransition[v])
                    {
                        transitionLength[v] =
                            DuctFittings.ConcentricLength(sizeIn[v], input.Sizes[v], settings);
                    }

                    outTake[v] = elbowTakeup[v] + transitionLength[v] + (v == 0 ? input.StartLead : 0);
                    inTake[v] = elbowTakeup[v];
                }

                inTake[segments] = input.EndLead;
                previousAvailable = input.PreviousLength - inTake[0];

                // Find the first piece that came out shorter than zero.
                int violation = -2;
                if (hasPrevious && previousAvailable < -LengthTolerance)
                {
                    violation = -1;
                }
                else
                {
                    for (int i = 0; i < segments; i++)
                    {
                        if (length[i] - outTake[i] - inTake[i + 1] < -LengthTolerance)
                        {
                            violation = i;
                            break;
                        }
                    }
                }

                if (violation == -2)
                    break;

                bool changed = false;
                int firstVertex = violation == -1 ? 0 : violation;
                int lastVertex = violation == -1 ? 0 : violation + 1;
                for (int v = firstVertex; v <= lastVertex && v < segments; v++)
                {
                    if (!hasElbow[v] || reduced[v] || v < input.FrozenCorners)
                        continue;
                    double normal = DuctFittings.ElbowRadius(sizeIn[v], settings, false);
                    double fallback = DuctFittings.ElbowRadius(sizeIn[v], settings, true);
                    if (normal - fallback < 1e-9)
                        continue;
                    reduced[v] = true;
                    changed = true;
                }

                if (!changed || --guard < 0)
                {
                    if (violation == -1)
                    {
                        result.Error = "The existing duct is too short for the elbow at its end: "
                            + Format(input.PreviousLength) + " mm available, "
                            + Format(inTake[0]) + " mm needed.";
                    }
                    else
                    {
                        double need = outTake[violation] + inTake[violation + 1];
                        result.Error = "Segment " + (violation + 1) + " is too short for the fittings at "
                            + "its ends: " + Format(length[violation]) + " mm between points, "
                            + Format(need) + " mm needed.";
                    }

                    return result;
                }
            }

            result.PreviousShorten = hasPrevious ? inTake[0] : 0;

            for (int i = 0; i < segments; i++)
            {
                double straight = length[i] - outTake[i] - inTake[i + 1];
                if (straight < 0)
                    straight = 0;

                result.Straights.Add(new RouteStraight
                {
                    Segment = i,
                    Start = input.Points[i] + direction[i] * outTake[i],
                    Direction = direction[i],
                    Length = straight,
                    CentreLength = length[i],
                    Size = input.Sizes[i]
                });
            }

            for (int v = 0; v < segments; v++)
            {
                if (hasElbow[v])
                {
                    double radius = DuctFittings.ElbowRadius(sizeIn[v], settings, reduced[v]);
                    ElbowGeometry elbow = FittingShapes.Elbow(
                        input.Points[v],
                        inDirection[v],
                        direction[v],
                        sizeIn[v].Width,
                        radius);
                    result.Fittings.Add(new RouteFitting
                    {
                        Vertex = v,
                        Kind = RouteFitting.ElbowKind,
                        SizeText = sizeIn[v].Label,
                        Shape = elbow.Shape,
                        Centreline = sizeIn[v].IsRound ? elbow.Centreline : null
                    });

                    if (reduced[v] && (input.CornerReduced == null
                        || v >= input.CornerReduced.Length || !input.CornerReduced[v]))
                    {
                        result.Notes.Add("Elbow at point " + (v + 1) + " uses R = W/2 so the runs fit.");
                    }
                }

                if (hasTransition[v])
                {
                    Vec2 start = input.Points[v] + direction[v] * elbowTakeup[v];
                    result.Fittings.Add(new RouteFitting
                    {
                        Vertex = v,
                        Kind = RouteFitting.TransitionKind,
                        SizeText = sizeIn[v].Label + ">" + input.Sizes[v].Label,
                        Shape = FittingShapes.Transition(
                            start,
                            direction[v],
                            transitionLength[v],
                            sizeIn[v].Width,
                            input.Sizes[v].Width)
                    });
                }
            }

            result.CornerReduced = reduced;
            result.Ok = true;
            return result;
        }

        private static string Format(double value)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }

    // ---------------------------------------------------------------------------------
    // HVACDUCTALIGN
    // ---------------------------------------------------------------------------------

    public enum JoinKind
    {
        None,
        Direct,
        Eccentric,
        /// <summary>B is a perpendicular branch joined to A's side wall with a tap.</summary>
        Tap
    }

    /// <summary>What joining duct B to duct A would do.</summary>
    public sealed class JoinPlan
    {
        public JoinKind Kind;
        /// <summary>Why no join is offered or possible (empty when Kind is not None).</summary>
        public string Reason = "";
        /// <summary>B's new Position and length (drawing units).</summary>
        public Vec2 NewStart;
        public double NewLength;
        public bool PositionMoves;
        /// <summary>The eccentric transition, when needed.</summary>
        public Outline Transition;
        public double TransitionLength;
        /// <summary>+1 / -1: which side of A's axis the flat side (or, for a tap, the wall) is on.</summary>
        public int FlatSide;
        /// <summary>The tap on A's wall, for <see cref="JoinKind.Tap"/>.</summary>
        public Outline Tap;
        public double TapLead;
    }

    public static class DuctAlignMath
    {
        public const double ParallelToleranceDegrees = 1.0;
        public const double MinimumJoinedLength = 100.0;

        /// <summary>True when the two directions are parallel or anti-parallel within the angle.</summary>
        public static bool IsParallel(Vec2 first, Vec2 second, double maxDegrees)
        {
            Vec2 a = first.Normalized();
            Vec2 b = second.Normalized();
            if (a.Length < 0.5 || b.Length < 0.5)
                return false;
            return Math.Abs(a.Cross(b)) <= Math.Sin(maxDegrees * Math.PI / 180.0);
        }

        /// <summary>Angle between two lines in degrees, 0..90.</summary>
        public static double AngleBetweenLines(Vec2 first, Vec2 second)
        {
            Vec2 a = first.Normalized();
            Vec2 b = second.Normalized();
            double sin = Math.Abs(a.Cross(b));
            double cos = Math.Abs(a.Dot(b));
            return Math.Atan2(sin, cos) * 180.0 / Math.PI;
        }

        /// <summary>
        /// The translation that puts a point of the edge to align onto the reference line: it is
        /// perpendicular to the reference edge, never along it.
        /// </summary>
        public static Vec2 AlignTranslation(Vec2 referenceFrom, Vec2 referenceTo, Vec2 edgePoint)
        {
            Vec2 direction = (referenceTo - referenceFrom).Normalized();
            Vec2 normal = direction.Left;
            double offset = (edgePoint - referenceFrom).Dot(normal);
            return normal * (-offset);
        }

        /// <summary>
        /// Plans the join of duct B (already translated so the aligned edges are collinear) to duct
        /// A. The reference edge must be a side wall of A. A and B need parallel axes, bodies on the
        /// same side of the aligned edge and facing ends. Equal widths extend or shorten B to
        /// meet A; different widths add an eccentric transition whose flat side is the aligned
        /// edge. B must stay at least <see cref="MinimumJoinedLength"/> long.
        /// </summary>
        public static JoinPlan PlanJoin(
            DuctGeometry a,
            DuctGeometry b,
            Vec2 referencePoint,
            FittingSettings settings)
        {
            const double positionTolerance = 0.5;
            JoinPlan plan = new JoinPlan();

            if (!IsParallel(a.Axis, b.Axis, ParallelToleranceDegrees))
            {
                plan.Reason = "the ducts are not parallel";
                return plan;
            }

            double halfA = a.Width / 2;
            double halfB = b.Width / 2;
            double referenceAcross = a.Across(referencePoint);
            int flat;
            if (Math.Abs(referenceAcross - halfA) <= positionTolerance)
                flat = 1;
            else if (Math.Abs(referenceAcross + halfA) <= positionTolerance)
                flat = -1;
            else
            {
                plan.Reason = "the reference edge is not a side wall of duct A";
                return plan;
            }

            double centreB = a.Across(b.Start);
            if (Math.Abs(centreB + flat * halfB - flat * halfA) > positionTolerance)
            {
                plan.Reason = "the two ducts lie on opposite sides of the aligned edge";
                return plan;
            }

            double sameDirection = a.Axis.Dot(b.Axis) > 0 ? 1.0 : -1.0;
            double startAlong = a.Along(b.Start);
            double endAlong = startAlong + sameDirection * b.Length;
            double minAlong = Math.Min(startAlong, endAlong);
            double maxAlong = Math.Max(startAlong, endAlong);

            int travel;
            double near;
            double far;
            double faceAlong;
            if (minAlong >= a.Length - positionTolerance)
            {
                travel = 1;
                near = minAlong;
                far = maxAlong;
                faceAlong = a.Length;
            }
            else if (maxAlong <= positionTolerance)
            {
                travel = -1;
                near = maxAlong;
                far = minAlong;
                faceAlong = 0;
            }
            else
            {
                plan.Reason = "the ducts do not have facing ends";
                return plan;
            }

            bool equalWidth = Math.Abs(a.Width - b.Width) <= 0.5;
            double transition = equalWidth
                ? 0
                : DuctFittings.EccentricLength(a.Width, b.Width, settings);
            double target = faceAlong + travel * transition;
            double newLength = travel * (far - target);
            if (newLength < MinimumJoinedLength)
            {
                plan.Reason = "duct B would end up shorter than "
                    + MinimumJoinedLength.ToString("0", CultureInfo.InvariantCulture) + " mm";
                return plan;
            }

            bool nearIsStart = (sameDirection > 0) == (travel > 0);
            plan.PositionMoves = nearIsStart;
            plan.NewStart = nearIsStart ? b.Start + a.Axis * (target - near) : b.Start;
            plan.NewLength = newLength;
            plan.FlatSide = flat;
            plan.Kind = equalWidth ? JoinKind.Direct : JoinKind.Eccentric;
            plan.TransitionLength = transition;

            if (!equalWidth)
            {
                Vec2 faceCentre = a.Start + a.Axis * faceAlong;
                Vec2 endCentre;
                plan.Transition = FittingShapes.EccentricTransition(
                    faceCentre,
                    a.Axis * travel,
                    a.Left,
                    flat,
                    transition,
                    a.Width,
                    b.Width,
                    out endCentre);
            }

            return plan;
        }

        /// <summary>
        /// Plans a branch: duct B runs at right angles to duct A (within 1 degree) and the
        /// reference edge is a side wall of A. B keeps its centreline and its far end; its end
        /// nearer the wall moves to the tap lead from the wall, and a tap is drawn where B's
        /// centreline meets the wall. A's flow is taken as its drawing direction (start to end),
        /// as in HVACDUCT, so a rectangular tap's 45 degree side is towards A's start.
        /// </summary>
        public static JoinPlan PlanTap(
            DuctGeometry a,
            DuctGeometry b,
            DuctSize branch,
            Vec2 referencePoint,
            FittingSettings settings)
        {
            const double positionTolerance = 0.5;
            JoinPlan plan = new JoinPlan();

            if (Math.Abs(a.Axis.Dot(b.Axis)) > Math.Sin(ParallelToleranceDegrees * Math.PI / 180.0))
            {
                plan.Reason = "the ducts are not at right angles";
                return plan;
            }

            double halfA = a.Width / 2;
            double referenceAcross = a.Across(referencePoint);
            int side;
            if (Math.Abs(referenceAcross - halfA) <= positionTolerance)
                side = 1;
            else if (Math.Abs(referenceAcross + halfA) <= positionTolerance)
                side = -1;
            else
            {
                plan.Reason = "the reference edge is not a side wall of duct A";
                return plan;
            }

            Vec2 outward = a.Left * side;
            double startOut = (b.Start - a.Start).Dot(outward);
            double endOut = (b.End - a.Start).Dot(outward);
            bool nearIsStart = startOut <= endOut;
            double farOut = Math.Max(startOut, endOut);
            // Outward distance per unit of B's length, running from its near end to its far end.
            double rate = Math.Abs(b.Axis.Dot(outward));
            if (farOut <= halfA + positionTolerance)
            {
                plan.Reason = "duct B does not run out from that wall of duct A";
                return plan;
            }

            Vec2 nearPoint = nearIsStart ? b.Start : b.End;
            Vec2 toFar = nearIsStart ? b.Axis : -b.Axis;
            double nearOut = Math.Min(startOut, endOut);
            Vec2 wallPoint = nearPoint + toFar * ((halfA - nearOut) / rate);
            double along = a.Along(wallPoint);

            double upstream;
            double downstream;
            if (!DuctFittings.TapFits(a.Length, along, branch, settings, out upstream, out downstream))
            {
                plan.Reason = "the tap needs "
                    + upstream.ToString("0.#", CultureInfo.InvariantCulture)
                    + " mm of duct A before the branch centreline and "
                    + downstream.ToString("0.#", CultureInfo.InvariantCulture)
                    + " mm after it, and duct A is too short there";
                return plan;
            }

            double lead = DuctFittings.TapLead(branch, settings);
            double newLength = (farOut - halfA - lead) / rate;
            if (newLength < MinimumJoinedLength)
            {
                plan.Reason = "duct B would end up shorter than "
                    + MinimumJoinedLength.ToString("0", CultureInfo.InvariantCulture) + " mm";
                return plan;
            }

            plan.Kind = JoinKind.Tap;
            plan.PositionMoves = nearIsStart;
            plan.NewStart = nearIsStart ? wallPoint + toFar * (lead / rate) : b.Start;
            plan.NewLength = newLength;
            plan.FlatSide = side;
            plan.TapLead = lead;
            plan.Tap = FittingShapes.Tap(wallPoint, a.Axis, outward, branch, settings);
            return plan;
        }
    }
}
