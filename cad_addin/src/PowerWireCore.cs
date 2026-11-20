using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Pipes
{
    // AutoCAD-free core of HVACPOWERWIRE: turns a rough tree of lines (a trunk plus a short branch to each
    // indoor unit) into one series (daisy-chain) power wire. The rough lines are straightened with the
    // HVACREFRIG cleaner (no root, so no refnets), the tree is built from the supply end with
    // DrainNetwork.Build, and every branch junction becomes the hook of the template's legend glyph:
    //
    //   the wire arrives along the trunk at T, runs down the branch to the unit's terminal P (drop leg),
    //   slants 45 degrees to Q = P + s along - s down (s along the trunk, s back toward it), rises from Q
    //   to the trunk at T + s along (rise leg, parallel to the drop leg) and goes on along the trunk.
    //
    // The last unit of a wire gets only the drop leg. Units are numbered in series order (depth first
    // along the trunk, branches in order of distance along it). Straight segments only, plain doubles and
    // Vec2, so tests/PipeCoreTests compiles this file without AutoCAD.
    // See AGENTS.md, "HVACPOWERWIRE: draw the indoor-unit power wire".

    public enum PowerWireLayerKind
    {
        Supply,
        Signal
    }

    /// <summary>The HVACPOWERWIRE dialog values, remembered between runs as key=value lines.</summary>
    public sealed class PowerWireSettings
    {
        public const string SupplyLayerName = "EIA_5 SUPPLY WIRE";
        public const string SignalLayerName = "EIA_5 SIGNAL WIRE";

        /// <summary>The template's cable text style; the command uses the current style when the drawing has none.</summary>
        public const string TextStyleName = "T_unicode";

        /// <summary>The rise leg of a hook must be at least this long, and the legs at least this far from the next bend (mm).</summary>
        public const double HookMargin = 50;

        /// <summary>The cable text starts this share of its height above the end of the leg (the template: 68 of 275).</summary>
        public const double TextLead = 0.25;

        /// <summary>Endpoints closer than this are one joint (mm).</summary>
        public double WeldTolerance = 50;

        public double AngleToleranceDegrees = 10;

        /// <summary>True: the base angle is the UCS X axis. False: <see cref="BaseAngleDegrees"/>, from world X.</summary>
        public bool UseUcsBaseAngle = true;
        public double BaseAngleDegrees = 0;

        /// <summary>The 45 degree slant of a hook: s along the trunk and s across (mm). Its length is s times 1.414.</summary>
        public double HookSize = 300;

        /// <summary>
        /// A trunk end beyond the last branch is an indoor unit only when it is at least this long (mm,
        /// and at least a hook); a shorter one is an overshoot and is trimmed at the last branch.
        /// </summary>
        public double EndUnitTail = 1000;

        public PowerWireLayerKind Layer = PowerWireLayerKind.Supply;

        public bool WriteText = true;
        public string CableText = "2x1.5 MM2";
        public double TextHeight = 275;

        public string LayerName
        {
            get { return Layer == PowerWireLayerKind.Signal ? SignalLayerName : SupplyLayerName; }
        }

        public bool IsValid(out string error)
        {
            error = null;
            if (!Finite(WeldTolerance) || WeldTolerance <= 0 || WeldTolerance > 1000)
                error = "The weld tolerance must be above 0 and at most 1000 mm.";
            else if (!Finite(AngleToleranceDegrees) || AngleToleranceDegrees <= 0 || AngleToleranceDegrees > 22.5)
                error = "The angle tolerance must be above 0 and at most 22.5 degrees.";
            else if (!Finite(BaseAngleDegrees))
                error = "The base angle must be a number.";
            else if (!Finite(HookSize) || HookSize <= 0 || HookSize > 5000)
                error = "The hook size must be above 0 and at most 5000 mm.";
            else if (!Finite(EndUnitTail) || EndUnitTail < 0 || EndUnitTail > 100000)
                error = "The least trunk end for a unit must be 0 or more.";
            else if (!Finite(TextHeight) || TextHeight <= 0 || TextHeight > 10000)
                error = "The text height must be above 0 and at most 10000 mm.";
            else if (WriteText
                && (CableText == null || CableText.Length == 0 || CableText.Length > 100
                    || CableText.IndexOf('\n') >= 0 || CableText.IndexOf('\r') >= 0))
                error = "The cable text must be a single line of 1 to 100 characters.";
            return error == null;
        }

        /// <summary>The cleaner's settings: the wire tolerances, with everything else at its default.</summary>
        public RefrigSettings ToRefrigSettings()
        {
            RefrigSettings settings = new RefrigSettings();
            settings.WeldTolerance = WeldTolerance;
            settings.AngleToleranceDegrees = AngleToleranceDegrees;
            return settings;
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string Num(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void Add(StringBuilder text, string key, string value)
        {
            text.Append(key).Append('=').Append(value).Append('\n');
        }

        public string Serialize()
        {
            StringBuilder text = new StringBuilder();
            Add(text, "weld", Num(WeldTolerance));
            Add(text, "angle", Num(AngleToleranceDegrees));
            Add(text, "ucs", UseUcsBaseAngle ? "1" : "0");
            Add(text, "baseAngle", Num(BaseAngleDegrees));
            Add(text, "hook", Num(HookSize));
            Add(text, "endTail", Num(EndUnitTail));
            Add(text, "layer", Layer == PowerWireLayerKind.Signal ? "signal" : "supply");
            Add(text, "text", WriteText ? "1" : "0");
            Add(text, "cableText", CableText ?? "");
            Add(text, "textHeight", Num(TextHeight));
            return text.ToString();
        }

        /// <summary>Never throws: unknown keys and bad numbers keep the defaults, and invalid values give all defaults.</summary>
        public static PowerWireSettings Deserialize(string text)
        {
            PowerWireSettings settings = new PowerWireSettings();
            if (string.IsNullOrEmpty(text))
                return settings;

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim('\r');
                int equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;

                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1);
                double number;
                bool isNumber = double.TryParse(
                    value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                    && Finite(number);
                switch (key)
                {
                    case "weld": if (isNumber) settings.WeldTolerance = number; break;
                    case "angle": if (isNumber) settings.AngleToleranceDegrees = number; break;
                    case "ucs": settings.UseUcsBaseAngle = value.Trim() != "0"; break;
                    case "baseAngle": if (isNumber) settings.BaseAngleDegrees = number; break;
                    case "hook": if (isNumber) settings.HookSize = number; break;
                    case "endTail": if (isNumber) settings.EndUnitTail = number; break;
                    case "layer":
                        settings.Layer = value.Trim() == "signal" ? PowerWireLayerKind.Signal : PowerWireLayerKind.Supply;
                        break;
                    case "text": settings.WriteText = value.Trim() != "0"; break;
                    case "cableText": settings.CableText = value; break;
                    case "textHeight": if (isNumber) settings.TextHeight = number; break;
                }
            }

            string error;
            if (!settings.IsValid(out error))
                return new PowerWireSettings();
            return settings;
        }
    }

    public enum PowerWireUnitKind
    {
        /// <summary>A unit before the end of the wire: the full hook (drop, slant, rise).</summary>
        Hook,

        /// <summary>The last unit of a wire reached by a branch: the wire runs down the branch and ends at the unit.</summary>
        Drop,

        /// <summary>A unit that could not be hooked: its branch stays a plain drop, a separate line off the wire.</summary>
        Spur,

        /// <summary>A unit at the end of the trunk itself: the wire just ends there.</summary>
        TrunkEnd
    }

    /// <summary>Where the cable text goes: its justification point, rotation and which end of the text sits at the point.</summary>
    public sealed class PowerWireText
    {
        public Vec2 Anchor;

        /// <summary>Radians from world X, in (-90, 90] degrees so the text reads upright (a vertical leg gives 90).</summary>
        public double Rotation;

        /// <summary>True: the text ends at <see cref="Anchor"/> (right justified). False: it starts there (left justified).</summary>
        public bool RightJustified;
    }

    public sealed class PowerWireUnit
    {
        /// <summary>1-based, in series order.</summary>
        public int Number;
        public PowerWireUnitKind Kind;

        /// <summary>The unit's terminal point: the end of its branch, or the end of the trunk.</summary>
        public Vec2 Terminal;

        /// <summary>Where the branch leaves the trunk (the junction T); the terminal itself for a trunk end.</summary>
        public Vec2 Junction;

        /// <summary>For a <see cref="PowerWireUnitKind.Spur"/>: why the hook could not be drawn.</summary>
        public string Problem;

        public PowerWireText Text;

        public string Label
        {
            get { return "U" + Number.ToString(CultureInfo.InvariantCulture); }
        }
    }

    public enum PowerWireRunKind
    {
        /// <summary>The series wire: one polyline per trunk.</summary>
        Wire,

        /// <summary>The plain drop of a unit that could not be hooked.</summary>
        Spur
    }

    /// <summary>One polyline to write.</summary>
    public sealed class PowerWireRun
    {
        public PowerWireRunKind Kind;
        public readonly List<Vec2> Points = new List<Vec2>();
    }

    public sealed class PowerWireResult
    {
        public bool Ok;
        public string Error;
        public readonly List<Vec2> ErrorPoints = new List<Vec2>();

        public readonly List<PowerWireRun> Runs = new List<PowerWireRun>();

        /// <summary>The indoor units in series order.</summary>
        public readonly List<PowerWireUnit> Units = new List<PowerWireUnit>();

        /// <summary>Information: trimmed trunk ends, sub-trunks, other networks left alone.</summary>
        public readonly List<string> Notes = new List<string>();

        /// <summary>Hooks that could not be drawn (a plain drop was kept); each is circled.</summary>
        public readonly List<string> Warnings = new List<string>();
        public readonly List<Vec2> WarningPoints = new List<Vec2>();

        /// <summary>Cleaned paths of other networks, written back unchanged.</summary>
        public readonly List<RefrigPath> Unreached = new List<RefrigPath>();

        /// <summary>The elevation of the new polylines (that of the supply-end pipe).</summary>
        public double Elevation;

        /// <summary>The root of the tree, as found: the wire end nearest the picked point.</summary>
        public Vec2 Root;

        public int Hooks;
        public int Wires;

        /// <summary>The cleaning result; null when only <see cref="PowerWirePlanner.Plan"/> ran.</summary>
        public RefrigResult Clean;
    }

    public static class PowerWirePlanner
    {
        /// <summary>
        /// Cleans the rough lines (weld, orthogonalise, merge; no refnets) and plans the wire.
        /// </summary>
        /// <param name="baseAngle">The base direction in radians from world X.</param>
        /// <param name="root">A point near the supply end of the wire.</param>
        /// <param name="rootTolerance">The supply end must be a wire end this close to the picked point.</param>
        public static PowerWireResult Run(
            IList<PipeCurve> curves, PowerWireSettings settings, double baseAngle, Vec2 root, double rootTolerance)
        {
            PowerWireResult result = new PowerWireResult();
            string settingsError;
            if (!settings.IsValid(out settingsError))
            {
                result.Error = settingsError;
                return result;
            }

            RefrigInput input = new RefrigInput
            {
                Curves = curves,
                Settings = settings.ToRefrigSettings(),
                BaseAngle = baseAngle,
                RootPoint = null
            };
            RefrigResult clean = RefrigCleaner.Run(input);
            if (!clean.Ok)
            {
                result.Error = clean.Error;
                result.ErrorPoints.AddRange(clean.ErrorPoints);
                result.Clean = clean;
                return result;
            }

            PowerWireResult planned = Plan(clean.Paths, settings, root, rootTolerance);
            planned.Clean = clean;
            return planned;
        }

        /// <summary>Plans the wire on paths that are already clean (one polyline per run between junctions).</summary>
        public static PowerWireResult Plan(
            IList<RefrigPath> paths, PowerWireSettings settings, Vec2 root, double rootTolerance)
        {
            PowerWireResult result = new PowerWireResult();
            string settingsError;
            if (!settings.IsValid(out settingsError))
            {
                result.Error = settingsError;
                return result;
            }

            DrainTreeResult built = DrainNetwork.Build(paths, root, rootTolerance);
            if (!built.Ok)
            {
                // The tree builder words its errors for drains.
                string error = built.Error ?? "The wire network could not be built.";
                if (error.StartsWith("No pipe end lies within", StringComparison.Ordinal))
                {
                    error = "No wire end lies within " + DrainNetwork.Num(rootTolerance)
                        + " mm of the picked supply point. Pick the end of the wire where the supply comes in.";
                }
                else if (error.IndexOf("loop", StringComparison.Ordinal) >= 0)
                {
                    error = "The wire network has a loop. HVACPOWERWIRE expects a tree: break the loop first.";
                }
                else if (error.IndexOf("There are no pipes", StringComparison.Ordinal) >= 0)
                {
                    error = "There are no lines to build a wire from.";
                }

                result.Error = error;
                result.ErrorPoints.AddRange(built.ErrorPoints);
                return result;
            }

            new Planner(built.Tree, settings, result).Execute();
            return result;
        }

        /// <summary>
        /// The corner points of a hook, from the junction T: T, the unit's terminal P, the slant's top Q and the
        /// rise leg's top R on the trunk. <paramref name="along"/> is the trunk direction and
        /// <paramref name="toUnit"/> the direction from the trunk down the branch; both are unit vectors.
        /// </summary>
        public static List<Vec2> HookPoints(Vec2 junction, Vec2 along, Vec2 toUnit, double branchLength, double size)
        {
            Vec2 terminal = junction + toUnit * branchLength;
            Vec2 slantTop = terminal + along * size - toUnit * size;
            Vec2 riseTop = junction + along * size;
            return new List<Vec2> { junction, terminal, slantTop, riseTop };
        }

        // ------------------------------------------------------------------------------------------

        private sealed class Station
        {
            /// <summary>The junction's index in the chain's trunk list.</summary>
            public int TrunkIndex;
            public Vec2 Junction;

            /// <summary>The direction the trunk goes on in (toward the next bend or junction).</summary>
            public Vec2 Along;

            /// <summary>The branch from the junction to the unit's terminal, bends included; null for a sub-trunk.</summary>
            public List<Vec2> Branch;

            /// <summary>The wire that starts at the junction when the branch has junctions of its own.</summary>
            public Chain Sub;

            public PowerWireUnit Unit;
        }

        private sealed class Chain
        {
            /// <summary>The wire's start, then every bend and junction along the through path, then the trunk end.</summary>
            public readonly List<Vec2> Trunk = new List<Vec2>();
            public readonly List<Station> Stations = new List<Station>();
            public PowerWireUnit EndUnit;
        }

        private sealed class Planner
        {
            private readonly DrainTree _tree;
            private readonly PowerWireSettings _settings;
            private readonly PowerWireResult _result;
            private readonly double _rightAngleLimit;
            private bool _failed;

            public Planner(DrainTree tree, PowerWireSettings settings, PowerWireResult result)
            {
                _tree = tree;
                _settings = settings;
                _result = result;
                _rightAngleLimit = Math.Sin(settings.AngleToleranceDegrees * Math.PI / 180.0);
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
                foreach (DrainNode node in _tree.Nodes)
                {
                    if (node.Kind == DrainNodeKind.Cross)
                    {
                        _result.Error = "A joint of " + node.Degree + " wires at " + At(node.Point)
                            + " is not supported: draw each branch as a T off the trunk. Nothing was changed.";
                        _result.ErrorPoints.Add(node.Point);
                        return;
                    }
                }

                DrainSegment first = _tree.Segments[_tree.RootSegment];
                _result.Elevation = first.Elevation;
                _result.Root = _tree.Nodes[_tree.RootNode].Point;
                _result.Unreached.AddRange(_tree.Unreached);
                foreach (string report in _tree.Reports)
                    _result.Notes.Add(report);

                Chain main = BuildChain(first);
                if (_failed)
                    return;

                Evaluate(main);

                List<PowerWireUnit> units = new List<PowerWireUnit>();
                Collect(main, units);
                for (int i = 0; i < units.Count; i++)
                {
                    units[i].Number = i + 1;
                    _result.Units.Add(units[i]);
                }

                foreach (PowerWireUnit unit in units)
                {
                    if (unit.Kind != PowerWireUnitKind.Spur)
                        continue;
                    _result.Warnings.Add(unit.Label + ": " + unit.Problem + "; its branch is kept as a plain drop.");
                    _result.WarningPoints.Add(unit.Junction);
                }

                foreach (PowerWireRun run in _result.Runs)
                {
                    if (run.Kind == PowerWireRunKind.Wire)
                        _result.Wires++;
                }

                _result.Ok = true;
            }

            // The points of a segment from the node nearer the root to the node farther from it.
            private static List<Vec2> FromRoot(DrainSegment segment)
            {
                List<Vec2> points = new List<Vec2>(segment.Points);
                points.Reverse();
                return points;
            }

            // ------------------------------------------------------------------ the tree as chains

            // A chain is the wire along one trunk: it starts at the first point of the segment and follows the
            // through branch of every junction to a unit at the end. Each junction is a station; its other branch
            // is either one unit (a hook) or a sub-trunk, which is a chain of its own starting at the junction.
            private Chain BuildChain(DrainSegment start)
            {
                Chain chain = new Chain();
                chain.Trunk.AddRange(FromRoot(start));
                DrainSegment current = start;
                while (true)
                {
                    DrainNode up = _tree.Nodes[current.UpNode];
                    if (up.Kind == DrainNodeKind.Unit)
                        break;

                    if (up.UpSegments.Count != 2)
                    {
                        Fail("A joint of " + up.Degree + " wires at " + At(up.Point)
                            + " is not supported. Nothing was changed.", up.Point);
                        return chain;
                    }

                    Vec2 junction = up.Point;
                    Vec2 arrival = (chain.Trunk[chain.Trunk.Count - 1] - chain.Trunk[chain.Trunk.Count - 2]).Normalized();
                    DrainSegment firstKid = _tree.Segments[up.UpSegments[0]];
                    DrainSegment secondKid = _tree.Segments[up.UpSegments[1]];
                    Vec2 firstDirection = (FromRoot(firstKid)[1] - junction).Normalized();
                    Vec2 secondDirection = (FromRoot(secondKid)[1] - junction).Normalized();

                    // The through branch is the one that goes on straighter; the other is the branch.
                    bool firstThrough = arrival.Dot(firstDirection) >= arrival.Dot(secondDirection);
                    DrainSegment through = firstThrough ? firstKid : secondKid;
                    DrainSegment branch = firstThrough ? secondKid : firstKid;

                    List<Vec2> throughPoints = FromRoot(through);
                    Station station = new Station
                    {
                        TrunkIndex = chain.Trunk.Count - 1,
                        Junction = junction,
                        Along = (throughPoints[1] - junction).Normalized()
                    };

                    if (_tree.Nodes[branch.UpNode].Kind == DrainNodeKind.Unit)
                    {
                        List<Vec2> path = FromRoot(branch);
                        station.Branch = path;
                        station.Unit = new PowerWireUnit
                        {
                            Terminal = path[path.Count - 1],
                            Junction = junction
                        };
                    }
                    else
                    {
                        station.Sub = BuildChain(branch);
                        if (_failed)
                            return chain;
                        _result.Notes.Add(
                            "The branch at " + At(junction) + " has junctions of its own: its units get a separate wire "
                            + "that starts at the trunk there.");
                    }

                    chain.Stations.Add(station);
                    for (int i = 1; i < throughPoints.Count; i++)
                        chain.Trunk.Add(throughPoints[i]);
                    current = through;
                }

                return chain;
            }

            private void Fail(string message, Vec2 point)
            {
                _failed = true;
                _result.Ok = false;
                _result.Error = message;
                _result.ErrorPoints.Add(point);
            }

            // ------------------------------------------------------------------ the wire of one chain

            private static double Length(IList<Vec2> points, int from, int to)
            {
                double total = 0;
                for (int i = from; i < to; i++)
                    total += points[i].DistanceTo(points[i + 1]);
                return total;
            }

            private void Evaluate(Chain chain)
            {
                List<Vec2> trunk = chain.Trunk;
                int count = trunk.Count;
                Station last = chain.Stations.Count > 0 ? chain.Stations[chain.Stations.Count - 1] : null;

                // The trunk end is a unit unless it is a short overshoot past the last junction.
                bool endUnit = true;
                if (last != null)
                {
                    double tail = Length(trunk, last.TrunkIndex, count - 1);
                    double least = Math.Max(_settings.EndUnitTail, _settings.HookSize + PowerWireSettings.HookMargin);
                    endUnit = tail >= least - 1e-9;
                    if (!endUnit)
                    {
                        _result.Notes.Add(
                            "The trunk end " + Num(tail) + " mm beyond the junction at " + At(last.Junction)
                            + " is shorter than " + Num(least) + " mm: it is taken as an overshoot and trimmed there.");
                    }
                }

                if (endUnit)
                {
                    Vec2 end = trunk[count - 1];
                    Vec2 back = (trunk[count - 2] - end).Normalized();
                    chain.EndUnit = new PowerWireUnit
                    {
                        Kind = PowerWireUnitKind.TrunkEnd,
                        Terminal = end,
                        Junction = end,
                        Text = PlaceText(end, back, back.Left)
                    };
                }

                int lastVertex = endUnit ? count - 1 : last.TrunkIndex;
                Dictionary<int, Station> byIndex = new Dictionary<int, Station>();
                foreach (Station station in chain.Stations)
                    byIndex[station.TrunkIndex] = station;

                List<Vec2> wire = new List<Vec2>();
                List<PowerWireRun> spurs = new List<PowerWireRun>();
                for (int k = 0; k <= lastVertex; k++)
                {
                    wire.Add(trunk[k]);
                    Station st;
                    if (!byIndex.TryGetValue(k, out st) || st.Sub != null)
                        continue;

                    if (k == lastVertex)
                    {
                        // The last unit of the wire: down the branch and stop.
                        for (int i = 1; i < st.Branch.Count; i++)
                            wire.Add(st.Branch[i]);
                        st.Unit.Kind = PowerWireUnitKind.Drop;
                        st.Unit.Text = DropText(st);
                        continue;
                    }

                    string problem = HookProblem(trunk, st, k);
                    if (problem != null)
                    {
                        st.Unit.Kind = PowerWireUnitKind.Spur;
                        st.Unit.Problem = problem;
                        st.Unit.Text = DropText(st);
                        PowerWireRun spur = new PowerWireRun { Kind = PowerWireRunKind.Spur };
                        spur.Points.AddRange(st.Branch);
                        spurs.Add(spur);
                        continue;
                    }

                    Vec2 toUnit = (st.Branch[1] - st.Junction).Normalized();
                    double length = st.Junction.DistanceTo(st.Branch[1]);
                    List<Vec2> hook = HookPoints(st.Junction, st.Along, toUnit, length, _settings.HookSize);
                    wire.Add(hook[1]);
                    wire.Add(hook[2]);
                    wire.Add(hook[3]);
                    st.Unit.Kind = PowerWireUnitKind.Hook;
                    st.Unit.Text = PlaceText(hook[2], -toUnit, st.Along);
                    _result.Hooks++;
                }

                PowerWireRun run = new PowerWireRun { Kind = PowerWireRunKind.Wire };
                run.Points.AddRange(Simplify(wire));
                _result.Runs.Add(run);
                _result.Runs.AddRange(spurs);

                foreach (Station station in chain.Stations)
                {
                    if (station.Sub != null)
                        Evaluate(station.Sub);
                }
            }

            // Why the hook cannot be drawn at a junction, or null when it can.
            private string HookProblem(List<Vec2> trunk, Station st, int index)
            {
                double least = _settings.HookSize + PowerWireSettings.HookMargin;
                if (st.Branch.Count != 2)
                    return "its branch has a bend";

                double length = st.Junction.DistanceTo(st.Branch[1]);
                if (length < least)
                {
                    return "its branch is " + Num(length) + " mm, shorter than the hook needs (" + Num(least) + " mm)";
                }

                Vec2 toUnit = (st.Branch[1] - st.Junction).Normalized();
                if (Math.Abs(st.Along.Dot(toUnit)) > _rightAngleLimit)
                    return "its branch does not leave the trunk at a right angle";

                double next = trunk[index].DistanceTo(trunk[index + 1]);
                if (next < least)
                {
                    return "the next bend or junction is " + Num(next) + " mm along the trunk, closer than the hook needs ("
                        + Num(least) + " mm)";
                }

                return null;
            }

            // The text beside a unit's last leg: <base> is the end of the leg, <toward> the unit vector along the leg
            // toward the trunk, <side> the side of the leg the text goes on.
            private PowerWireText PlaceText(Vec2 baseAt, Vec2 toward, Vec2 side)
            {
                double height = _settings.TextHeight;
                Vec2 reading = toward;
                double angle = reading.Angle;
                bool flipped = angle > Math.PI / 2 + 1e-9 || angle <= -Math.PI / 2 + 1e-9;
                if (flipped)
                    reading = -reading;

                // The text body spans one text height from its baseline toward the reading direction's left; it is
                // one height away from the leg, on the chosen side.
                double across = height + (reading.Left.Dot(side) > 0 ? 0 : height);
                Vec2 anchor = baseAt + toward * (height * PowerWireSettings.TextLead) + side * across;
                double rotation = reading.Angle;
                if (rotation < 0)
                    rotation += 2 * Math.PI;
                return new PowerWireText { Anchor = anchor, Rotation = rotation, RightJustified = flipped };
            }

            // The text of a unit that has no rise leg: beside the last leg of its branch, on the side the wire goes on.
            private PowerWireText DropText(Station st)
            {
                Vec2 terminal = st.Branch[st.Branch.Count - 1];
                Vec2 toward = (st.Branch[st.Branch.Count - 2] - terminal).Normalized();
                Vec2 side = Math.Abs(toward.Dot(st.Along)) < 0.5 ? st.Along : toward.Left;
                return PlaceText(terminal, toward, side);
            }

            // Drops the vertices that lie on a straight line between their neighbours.
            private static List<Vec2> Simplify(IList<Vec2> source)
            {
                List<Vec2> kept = new List<Vec2>();
                foreach (Vec2 point in source)
                {
                    if (kept.Count > 0 && kept[kept.Count - 1].DistanceTo(point) < 1e-6)
                        continue;
                    kept.Add(point);
                }

                bool changed = true;
                while (changed)
                {
                    changed = false;
                    for (int i = 1; i + 1 < kept.Count; i++)
                    {
                        Vec2 a = kept[i] - kept[i - 1];
                        Vec2 b = kept[i + 1] - kept[i];
                        double scale = a.Length * b.Length;
                        if (scale > 1e-12 && Math.Abs(a.Cross(b)) <= 1e-9 * scale && a.Dot(b) > 0)
                        {
                            kept.RemoveAt(i);
                            changed = true;
                            break;
                        }
                    }
                }

                return kept;
            }

            // The units of a chain in series order: down the trunk, a branch's unit at its junction, a sub-trunk's
            // units where it starts, and the trunk end last.
            private static void Collect(Chain chain, List<PowerWireUnit> units)
            {
                foreach (Station station in chain.Stations)
                {
                    if (station.Sub != null)
                        Collect(station.Sub, units);
                    else
                        units.Add(station.Unit);
                }

                if (chain.EndUnit != null)
                    units.Add(chain.EndUnit);
            }
        }
    }
}
