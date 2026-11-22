using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
    // Walls and obstacles are ignored by those two; the user edits the draft.
    //
    // The corridor mode (the default when HVAC-ROOM polygons are given) routes over a cost grid instead:
    // corridor and lobby rooms are cheap, other rooms and the open area cost more, and every room wall
    // crossed costs extra. The tree grows from the target: the cheapest remaining unit port is joined to
    // the tree by a multi-source Dijkstra (orthogonal moves, a penalty per bend), the cell path is
    // simplified to axis-aligned polylines, and each unit port is reached by a short exact stub.
    // All coordinates are drawing units (mm).

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

        public const string ModeCorridor = "corridor";
        public const string ModeBest = "best";
        public const string ModeTrunk = "trunk";
        public const string ModeMst = "mst";
        public const string PortsInsertion = "insertion";
        public const string PortsBlock = "port";
        public const string AxisAuto = "auto";
        public const string AxisHorizontal = "horizontal";
        public const string AxisVertical = "vertical";

        public bool DrawRefrig = true;
        public bool DrawDrain = true;
        public bool DrawPower;

        /// <summary>
        /// "corridor" follows corridors using the HVAC-ROOM polygons (and falls back to "best" when there are
        /// none), "best" tries both tree modes and keeps the shorter, "trunk" or "mst" forces one.
        /// </summary>
        public string Mode = ModeCorridor;

        /// <summary>"auto", "horizontal" or "vertical": the trunk direction of the trunk mode.</summary>
        public string TrunkAxis = AxisAuto;

        /// <summary>The shortest stub the target gets when it must not lie on the route (mm).</summary>
        public double MinStub = 1500;

        /// <summary>True: ask for a separate drain target (for example another shaft).</summary>
        public bool SeparateDrainTarget;

        /// <summary>True: ask for a separate power supply point.</summary>
        public bool SeparatePowerTarget;

        /// <summary>"port": end the pipes at the unit's connection port; "insertion": at the block insertion point.</summary>
        public string PortMode = PortsBlock;

        /// <summary>Connection ports in block coordinates per block-name pattern (the longest matching pattern wins).</summary>
        public List<PortRule> PortRules = RoutePorts.DefaultRules();

        /// <summary>Corridor mode: grid cell size (mm); coarsened automatically for large areas.</summary>
        public double CellSize = 200;

        /// <summary>Corridor mode: cost of a cell inside a room that is not a corridor (corridor = 1).</summary>
        public double RoomPenalty = 5;

        /// <summary>Corridor mode: cost of a cell outside every room.</summary>
        public double OutsidePenalty = 3;

        /// <summary>Corridor mode: extra cost of every room boundary crossed.</summary>
        public double BoundaryPenalty = 2;

        /// <summary>Corridor mode: extra cost of every bend (in cells), to favour long straight runs.</summary>
        public double BendPenalty = 2;

        public bool AnyDraft
        {
            get { return DrawRefrig || DrawDrain || DrawPower; }
        }

        public bool IsValid(out string error)
        {
            error = null;
            if (!AnyDraft)
                error = "Tick at least one draft to draw.";
            else if (Mode != ModeCorridor && Mode != ModeBest && Mode != ModeTrunk && Mode != ModeMst)
                error = "Unknown tree mode.";
            else if (PortMode != PortsBlock && PortMode != PortsInsertion)
                error = "Unknown port mode.";
            else if (!InRange(CellSize, 20, 5000))
                error = "The cell size must be between 20 and 5000 mm.";
            else if (!InRange(RoomPenalty, 1, 1000) || !InRange(OutsidePenalty, 1, 1000))
                error = "The room and outside costs must be between 1 and 1000.";
            else if (!InRange(BoundaryPenalty, 0, 1000) || !InRange(BendPenalty, 0, 1000))
                error = "The boundary and bend costs must be between 0 and 1000.";
            else if (TrunkAxis != AxisAuto && TrunkAxis != AxisHorizontal && TrunkAxis != AxisVertical)
                error = "Unknown trunk axis.";
            else if (double.IsNaN(MinStub) || double.IsInfinity(MinStub) || MinStub < 0 || MinStub > 100000)
                error = "The shortest stub must be between 0 and 100000 mm.";
            if (error == null)
            {
                foreach (PortRule rule in PortRules)
                {
                    if (!rule.IsValid(out error))
                        break;
                }
            }

            return error == null;
        }

        private static bool InRange(double value, double lo, double hi)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= lo && value <= hi;
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
            Add(text, "portmode", PortMode);
            Add(text, "cell", CellSize.ToString("R", CultureInfo.InvariantCulture));
            Add(text, "roompenalty", RoomPenalty.ToString("R", CultureInfo.InvariantCulture));
            Add(text, "outsidepenalty", OutsidePenalty.ToString("R", CultureInfo.InvariantCulture));
            Add(text, "boundarypenalty", BoundaryPenalty.ToString("R", CultureInfo.InvariantCulture));
            Add(text, "bendpenalty", BendPenalty.ToString("R", CultureInfo.InvariantCulture));
            Add(text, "portrules", PortRules.Count.ToString(CultureInfo.InvariantCulture));
            foreach (PortRule rule in PortRules)
                Add(text, "port", rule.Format());
            return text.ToString();
        }

        /// <summary>Never throws: unknown keys and bad values keep the defaults.</summary>
        public static RouteDraftSettings Deserialize(string text)
        {
            RouteDraftSettings settings = new RouteDraftSettings();
            if (string.IsNullOrEmpty(text))
                return settings;

            List<PortRule> rules = new List<PortRule>();
            bool rulesSaved = false;

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
                        if (value == ModeCorridor || value == ModeBest || value == ModeTrunk || value == ModeMst)
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
                    case "portmode":
                        if (value == PortsBlock || value == PortsInsertion)
                            settings.PortMode = value;
                        break;
                    case "cell":
                        if (ParseIn(value, 20, 5000, out number))
                            settings.CellSize = number;
                        break;
                    case "roompenalty":
                        if (ParseIn(value, 1, 1000, out number))
                            settings.RoomPenalty = number;
                        break;
                    case "outsidepenalty":
                        if (ParseIn(value, 1, 1000, out number))
                            settings.OutsidePenalty = number;
                        break;
                    case "boundarypenalty":
                        if (ParseIn(value, 0, 1000, out number))
                            settings.BoundaryPenalty = number;
                        break;
                    case "bendpenalty":
                        if (ParseIn(value, 0, 1000, out number))
                            settings.BendPenalty = number;
                        break;
                    case "portrules":
                        rulesSaved = true; // an explicit (possibly empty) list replaces the defaults
                        break;
                    case "port":
                        PortRule rule = PortRule.Parse(value);
                        if (rule != null)
                            rules.Add(rule);
                        break;
                    case "draintarget":
                        settings.SeparateDrainTarget = value == "1";
                        break;
                    case "powertarget":
                        settings.SeparatePowerTarget = value == "1";
                        break;
                }
            }

            if (rulesSaved)
                settings.PortRules = rules;
            return settings;
        }

        private static bool ParseIn(string value, double lo, double hi, out double number)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                && InRange(number, lo, hi);
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

        /// <summary>Rooms for the corridor mode (null or empty: that mode falls back to "best").</summary>
        public IList<RouteRoom> Rooms;

        /// <summary>
        /// Direction (unit vector, zero = none) in which each input unit's pipe leaves its port, parallel to
        /// the unit list; only the corridor mode uses it. Null: no directions.
        /// </summary>
        public IList<Vec2> UnitOut;

        public double CellSize = 200;
        public double RoomPenalty = 5;
        public double OutsidePenalty = 3;
        public double BoundaryPenalty = 2;
        public double BendPenalty = 2;

        /// <summary>Cap on the grid cell count; the cell size grows until the grid fits.</summary>
        public int MaxCells = 120000;

        /// <summary>The grid extends this far beyond the units and the target (mm).</summary>
        public double Margin = 3000;

        /// <summary>Corridor mode: extra cost of a cell next to an existing pipe (pipes closer than the cleaner's tolerance merge).</summary>
        public double NearPenalty = 3;

        /// <summary>Corridor mode: extra cost of starting a branch closer to a junction than <see cref="JunctionSpacing"/>.</summary>
        public double SpacingPenalty = 60;

        /// <summary>Length of the straight lead from a unit port along its direction (mm).</summary>
        public double PortLead = 400;

        /// <summary>
        /// The least distance between two junctions of the corridor tree (mm): the HVACREFRIG cleaner needs the
        /// refnet spacing R1 (1000) plus a refnet body (750 x 1.24 = 930) between two T nodes. Branches are not
        /// started closer to a junction than this when another place exists; a closer pair is only warned about.
        /// </summary>
        public double JunctionSpacing = 2000;

        public static RouteDraftOptions From(RouteDraftSettings settings)
        {
            return new RouteDraftOptions
            {
                Mode = settings.Mode,
                TrunkAxis = settings.TrunkAxis,
                MinStub = settings.MinStub,
                CellSize = settings.CellSize,
                RoomPenalty = settings.RoomPenalty,
                OutsidePenalty = settings.OutsidePenalty,
                BoundaryPenalty = settings.BoundaryPenalty,
                BendPenalty = settings.BendPenalty
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

        /// <summary>
        /// Cost of the tree in the corridor cost model (corridor 1 per cell, rooms and outside dearer, boundary
        /// crossings, bends); 0 when no rooms were given. Comparable between the modes.
        /// </summary>
        public double RouteCost;

        /// <summary>Corridor mode: the grid cell size used (mm; larger than asked for when the area is large).</summary>
        public double CellSizeUsed;

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
            List<Vec2> uniqueOut = new List<Vec2>();
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
                    uniqueOut.Add(options.UnitOut != null && i < options.UnitOut.Count ? options.UnitOut[i] : new Vec2(0, 0));
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
            bool haveRooms = options.Rooms != null && options.Rooms.Count > 0;
            if (options.Mode == RouteDraftSettings.ModeCorridor && haveRooms)
            {
                Candidate corridor = CorridorRouter.Run(target, unique, uniqueOut, options);
                if (corridor != null)
                    candidates.Add(corridor);
            }

            bool wantTrunk = options.Mode != RouteDraftSettings.ModeMst;
            bool wantMst = options.Mode != RouteDraftSettings.ModeTrunk && unique.Count <= MstLimit;
            if (candidates.Count > 0)
            {
                wantTrunk = false;
                wantMst = false;
            }

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
            result.CellSizeUsed = best.CellSize;
            result.Warnings.AddRange(best.Notes);
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
            if (haveRooms)
            {
                RouteCostModel model = new RouteCostModel(options.Rooms, options.CellSize, options.RoomPenalty,
                    options.OutsidePenalty, options.BoundaryPenalty, options.BendPenalty);
                result.RouteCost = model.CostOf(result.Polylines);
            }

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
            public double CellSize;
            public readonly List<string> Notes = new List<string>();

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
            Candidate candidate = MakeCandidate(name, segments, target, units);
            if (candidate != null)
                candidates.Add(candidate);
        }

        private static Candidate MakeCandidate(string name, List<Seg> segments, Vec2 target, IList<Vec2> units)
        {
            Graph graph = Graph.Build(segments);
            if (graph == null || !graph.IsTree)
                return null;
            Candidate candidate = new Candidate { Name = name, Graph = graph };
            candidate.RootDegree = graph.DegreeOf(target);
            candidate.CrossJunctions = graph.CountJunctionsOver(3);
            foreach (Vec2 u in units)
            {
                if (graph.DegreeOf(u) != 1)
                    candidate.NonLeafUnits++;
            }

            return candidate;
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

        // ------------------------------------------------------------------ corridor mode

        private static class CorridorRouter
        {
            /// <summary>Builds the corridor tree, or returns null when it could not (the caller falls back).</summary>
            public static Candidate Run(Vec2 target, List<Vec2> units, List<Vec2> outs, RouteDraftOptions options)
            {
                try
                {
                    return new Router(target, units, outs, options).Run();
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }
        }

        private sealed class Router
        {
            private static readonly int[] Dx = { 1, 0, -1, 0 };
            private static readonly int[] Dy = { 0, 1, 0, -1 };
            private const double Eps = 1e-6;

            private readonly Vec2 _target;
            private readonly List<Vec2> _units;
            private readonly RouteDraftOptions _options;
            private readonly Vec2[] _out;
            private readonly Vec2[] _anchor;

            // grid
            private double _ox, _oy, _cs;
            private int _nx, _ny;
            private int _ti, _tj; // the cell whose centre is exactly the target
            private double[] _cost;
            private int[] _room;
            private double _boundary, _bend;

            // tree
            private readonly List<Seg> _tree = new List<Seg>();
            private bool[] _isTree;
            private byte[] _deg;
            private bool[] _locked;
            private bool[] _spaced; // closer to a junction than JunctionSpacing: starting a branch here costs SpacingPenalty
            private bool[] _near; // next to a tree cell: running here costs NearPenalty
            private readonly List<int> _treeCells = new List<int>();
            private readonly Dictionary<int, List<int>> _anchorUnits = new Dictionary<int, List<int>>();

            // search
            private double[] _dist;
            private int[] _parent;
            private double[] _hk;
            private int[] _hv;
            private int _hn;

            public Router(Vec2 target, List<Vec2> units, List<Vec2> outs, RouteDraftOptions options)
            {
                _target = target;
                _units = units;
                _options = options;
                _out = new Vec2[units.Count];
                _anchor = new Vec2[units.Count];
                for (int i = 0; i < units.Count; i++)
                {
                    _out[i] = RoutePorts.SnapAxis(outs != null && i < outs.Count ? outs[i] : new Vec2(0, 0));
                    bool has = _out[i].X != 0 || _out[i].Y != 0;
                    _anchor[i] = has ? units[i] + _out[i] * options.PortLead : units[i];
                }
            }

            // Centres are anchored on the target so that the target's own cell centre is exactly the target.
            private Vec2 Centre(int i, int j)
            {
                return new Vec2(_target.X + (i - _ti) * _cs, _target.Y + (j - _tj) * _cs);
            }

            private int CellAt(Vec2 p)
            {
                int i = (int)Math.Floor((p.X - _ox) / _cs);
                int j = (int)Math.Floor((p.Y - _oy) / _cs);
                i = Math.Max(0, Math.Min(_nx - 1, i));
                j = Math.Max(0, Math.Min(_ny - 1, j));
                return j * _nx + i;
            }

            private Vec2 CentreOf(int cell)
            {
                return Centre(cell % _nx, cell / _nx);
            }

            // ---- grid

            private void BuildGrid()
            {
                double stub = Math.Max(0, _options.MinStub);
                double loX = _target.X - stub, hiX = _target.X + stub, loY = _target.Y - stub, hiY = _target.Y + stub;
                foreach (Vec2 p in _units)
                {
                    loX = Math.Min(loX, p.X);
                    hiX = Math.Max(hiX, p.X);
                    loY = Math.Min(loY, p.Y);
                    hiY = Math.Max(hiY, p.Y);
                }

                foreach (Vec2 p in _anchor)
                {
                    loX = Math.Min(loX, p.X);
                    hiX = Math.Max(hiX, p.X);
                    loY = Math.Min(loY, p.Y);
                    hiY = Math.Max(hiY, p.Y);
                }

                double margin = Math.Max(0, _options.Margin);
                double cs = Math.Max(20, _options.CellSize);
                double w = hiX - loX + 2 * margin, h = hiY - loY + 2 * margin;
                int maxCells = Math.Max(1000, _options.MaxCells);
                while (Math.Ceiling(w / cs) * Math.Ceiling(h / cs) > maxCells)
                    cs *= 1.25;

                _cs = cs;
                double rawX = loX - margin, rawY = loY - margin;
                _ti = (int)Math.Ceiling((_target.X - rawX) / cs);
                _tj = (int)Math.Ceiling((_target.Y - rawY) / cs);
                _ox = _target.X - (_ti + 0.5) * cs;
                _oy = _target.Y - (_tj + 0.5) * cs;
                _nx = (int)Math.Ceiling((hiX + margin - _ox) / cs) + 1;
                _ny = (int)Math.Ceiling((hiY + margin - _oy) / cs) + 1;
                int cells = _nx * _ny;
                _boundary = _options.BoundaryPenalty;
                _bend = _options.BendPenalty;

                _cost = new double[cells];
                _room = new int[cells];
                for (int c = 0; c < cells; c++)
                {
                    _cost[c] = _options.OutsidePenalty;
                    _room[c] = -1;
                }

                // Normal rooms first, then shafts, then corridors: later ones win where rooms overlap.
                IList<RouteRoom> rooms = _options.Rooms;
                for (int pass = 0; pass < 3; pass++)
                {
                    for (int k = 0; k < rooms.Count; k++)
                    {
                        RouteRoom room = rooms[k];
                        int kind = room.Corridor ? 2 : (room.Shaft ? 1 : 0);
                        if (kind != pass)
                            continue;
                        Rasterize(room, k, kind == 0 ? _options.RoomPenalty : 1.0);
                    }
                }

                _isTree = new bool[cells];
                _deg = new byte[cells];
                _locked = new bool[cells];
                _spaced = new bool[cells];
                _near = new bool[cells];
                _dist = new double[cells * 4];
                _parent = new int[cells * 4];
                _hk = new double[1024];
                _hv = new int[1024];
            }

            private void Rasterize(RouteRoom room, int index, double cost)
            {
                List<Vec2> ring = room.Ring;
                int n = ring.Count;
                if (n < 3)
                    return;
                double minY = double.MaxValue, maxY = double.MinValue, minX = double.MaxValue, maxX = double.MinValue;
                foreach (Vec2 p in ring)
                {
                    minY = Math.Min(minY, p.Y);
                    maxY = Math.Max(maxY, p.Y);
                    minX = Math.Min(minX, p.X);
                    maxX = Math.Max(maxX, p.X);
                }

                int jlo = Math.Max(0, (int)Math.Ceiling((minY - _oy) / _cs - 0.5));
                int jhi = Math.Min(_ny - 1, (int)Math.Floor((maxY - _oy) / _cs - 0.5));
                if (maxX < _ox || minX > _ox + _nx * _cs)
                    return;
                List<double> xs = new List<double>();
                for (int j = jlo; j <= jhi; j++)
                {
                    double y = _oy + (j + 0.5) * _cs;
                    xs.Clear();
                    for (int e = 0; e < n; e++)
                    {
                        Vec2 a = ring[e], b = ring[(e + 1) % n];
                        if ((a.Y <= y) != (b.Y <= y))
                            xs.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                    }

                    xs.Sort();
                    for (int k = 0; k + 1 < xs.Count; k += 2)
                    {
                        int ilo = Math.Max(0, (int)Math.Ceiling((xs[k] - _ox) / _cs - 0.5));
                        int ihi = Math.Min(_nx - 1, (int)Math.Floor((xs[k + 1] - _ox) / _cs - 0.5));
                        for (int i = ilo; i <= ihi; i++)
                        {
                            _cost[j * _nx + i] = cost;
                            _room[j * _nx + i] = index;
                        }
                    }
                }
            }

            // ---- main

            public Candidate Run()
            {
                BuildGrid();
                double stub = Math.Max(0, _options.MinStub);

                // The root: a straight stub from the target, then the tree's first cell.
                // Its length is rounded up to whole cells so that its end is a cell centre (stub >= MinStub).
                List<Vec2> root = new List<Vec2> { _target };
                double rootLength = 0;
                if (stub > 0)
                {
                    int cellsLong = (int)Math.Ceiling(stub / _cs - 1e-9);
                    Vec2 rootDir = ChooseRootDirection(cellsLong * _cs);
                    root.Add(Centre(_ti + (int)rootDir.X * cellsLong, _tj + (int)rootDir.Y * cellsLong));
                    rootLength = cellsLong * _cs;
                }

                AddToTree(root, rootLength);

                List<int> remaining = new List<int>();
                for (int i = 0; i < _units.Count; i++)
                {
                    remaining.Add(i);
                    int cell = CellAt(_anchor[i]);
                    List<int> list;
                    if (!_anchorUnits.TryGetValue(cell, out list))
                    {
                        list = new List<int>();
                        _anchorUnits[cell] = list;
                    }

                    list.Add(i);
                }

                while (remaining.Count > 0)
                {
                    // Units whose anchor cell already belongs to the tree join it directly.
                    int direct = -1;
                    foreach (int u in remaining)
                    {
                        int anchorCell = CellAt(_anchor[u]);
                        if (_isTree[anchorCell] && _deg[anchorCell] < 250)
                        {
                            direct = u;
                            break;
                        }
                    }

                    int unit;
                    List<Vec2> cells;
                    if (direct >= 0)
                    {
                        unit = direct;
                        cells = new List<Vec2> { CentreOf(CellAt(_anchor[unit])) };
                    }
                    else if (!Search(out unit, out cells))
                    {
                        _fallbacks++;
                        FallbackAttach(remaining);
                        continue;
                    }

                    Attach(unit, cells);
                    remaining.Remove(unit);
                    RemoveAnchor(unit);
                }

                List<Seg> segments = new List<Seg>(_tree);
                segments.Add(new Seg(_target, _target));
                foreach (Vec2 u in _units)
                    segments.Add(new Seg(u, u));

                Candidate candidate = MakeCandidate("corridor", segments, _target, _units);
                if (candidate == null)
                    return null;
                candidate.CellSize = _cs;
                if (_fallbacks > 0)
                    candidate.Notes.Add(_fallbacks + " unit(s) could not be reached over the grid and were joined by a plain L-shaped pipe.");
                AddNotes(candidate, stub);
                return candidate;
            }

            private void RemoveAnchor(int unit)
            {
                int cell = CellAt(_anchor[unit]);
                List<int> list;
                if (_anchorUnits.TryGetValue(cell, out list))
                {
                    list.Remove(unit);
                    if (list.Count == 0)
                        _anchorUnits.Remove(cell);
                }
            }

            private Vec2 ChooseRootDirection(double stub)
            {
                Vec2[] dirs = { new Vec2(1, 0), new Vec2(0, 1), new Vec2(-1, 0), new Vec2(0, -1) };
                Vec2 centroid = _units.Count > 0 ? RouteDraftCore.Centroid(_units) : _target;
                int home = CellAt(_target);
                Vec2 best = dirs[0];
                double bestScore = double.MaxValue;
                foreach (Vec2 d in dirs)
                {
                    Vec2 r = _target + d * stub;
                    int cell = CellAt(r);
                    if (r.X < _ox || r.Y < _oy || r.X > _ox + _nx * _cs || r.Y > _oy + _ny * _cs)
                        continue;
                    double score = _cost[cell] + (_room[cell] != _room[home] ? _boundary : 0)
                        + (Math.Abs(r.X - centroid.X) + Math.Abs(r.Y - centroid.Y)) / _cs * 0.01;
                    if (score < bestScore - 1e-9)
                    {
                        bestScore = score;
                        best = d;
                    }
                }

                return best;
            }

            // ---- polylines

            // P, the lead point Q = P + dir * lead, then an L to the cell centre C.
            private static List<Vec2> BuildLead(Vec2 p, Vec2 dir, double lead, Vec2 c, bool horizontalFirst)
            {
                List<Vec2> pts = new List<Vec2> { p };
                Vec2 q = p;
                if ((dir.X != 0 || dir.Y != 0) && lead > 0)
                {
                    // The lead ends on the cell centre's row (or column) when that keeps at least half of it,
                    // so that no small jog is needed to reach the cell.
                    double along = (c - p).Dot(dir);
                    q = p + dir * (along >= 0.5 * lead ? along : lead);
                    if (along >= 0.5 * lead)
                        q = dir.X != 0 ? new Vec2(c.X, p.Y) : new Vec2(p.X, c.Y);
                    pts.Add(q);
                }

                Vec2 k = horizontalFirst ? new Vec2(c.X, q.Y) : new Vec2(q.X, c.Y);
                pts.Add(k);
                pts.Add(c);
                return Normalize(pts);
            }

            // Removes the short jogs (a short piece between two parallel runs) that the snap to cell centres
            // leaves, by sliding the run next to it onto the other's line. The first and the last point stay.
            private List<Vec2> RemoveJogs(List<Vec2> pts)
            {
                double limit = 0.55 * _cs;
                List<Vec2> r = new List<Vec2>(pts);
                for (int round = 0; round < 40; round++)
                {
                    bool changed = false;
                    for (int i = 0; i + 3 < r.Count; i++)
                    {
                        Vec2 a = r[i], b = r[i + 1], c = r[i + 2], d = r[i + 3];
                        double bc = b.DistanceTo(c);
                        if (bc > limit)
                            continue;
                        Vec2 shift = c - b;
                        if (i > 0)
                            r[i] = a + shift;
                        else if (i + 3 < r.Count - 1)
                            r[i + 3] = d - shift;
                        else
                            continue;
                        r = Normalize(r);
                        changed = true;
                        break;
                    }

                    if (!changed)
                        break;
                }

                return r;
            }

            private static bool SelfTouch(List<Vec2> pts)
            {
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    for (int j = i + 2; j + 1 < pts.Count; j++)
                    {
                        if (Math.Max(Math.Min(pts[i].X, pts[i + 1].X), Math.Min(pts[j].X, pts[j + 1].X))
                            <= Math.Min(Math.Max(pts[i].X, pts[i + 1].X), Math.Max(pts[j].X, pts[j + 1].X))
                            && Math.Max(Math.Min(pts[i].Y, pts[i + 1].Y), Math.Min(pts[j].Y, pts[j + 1].Y))
                            <= Math.Min(Math.Max(pts[i].Y, pts[i + 1].Y), Math.Max(pts[j].Y, pts[j + 1].Y)))
                            return true;
                    }
                }

                return false;
            }

            // Drops repeated points and collinear vertices, and folds back-and-forth runs.
            private static List<Vec2> Normalize(List<Vec2> pts)
            {
                List<Vec2> r = new List<Vec2>(pts);
                bool changed = true;
                while (changed)
                {
                    changed = false;
                    for (int i = 1; i < r.Count; i++)
                    {
                        if (r[i].X == r[i - 1].X && r[i].Y == r[i - 1].Y)
                        {
                            r.RemoveAt(i);
                            changed = true;
                            break;
                        }
                    }

                    if (changed)
                        continue;
                    for (int i = 1; i + 1 < r.Count; i++)
                    {
                        Vec2 a = r[i - 1], b = r[i], c = r[i + 1];
                        if ((a.X == b.X && b.X == c.X) || (a.Y == b.Y && b.Y == c.Y))
                        {
                            r.RemoveAt(i);
                            changed = true;
                            break;
                        }
                    }
                }

                return r;
            }

            // ---- attaching a unit

            private void Attach(int unit, List<Vec2> cells)
            {
                Vec2 p = _units[unit];
                Vec2 dir = _out[unit];
                double lead = _options.PortLead;
                Vec2 c = cells[0];

                List<Vec2> pts = null;
                for (int option = 0; option < 2 && pts == null; option++)
                {
                    List<Vec2> candidate = BuildLead(p, dir, lead, c, option == 0);
                    for (int k = 1; k < cells.Count; k++)
                        candidate.Add(cells[k]);
                    candidate = Normalize(candidate);
                    List<Vec2> smooth = RemoveJogs(candidate);
                    if (!SelfTouch(smooth))
                        candidate = smooth;
                    if (!SelfTouch(candidate) || option == 1)
                        pts = candidate;
                }

                AddToTree(Truncate(pts), 0);
            }

            private void AddToTree(List<Vec2> pts, double skipRadius)
            {
                if (pts.Count == 1 && _treeCells.Count == 0)
                {
                    AddTreeCell(CellAt(pts[0]), 0); // a root without a stub: the target cell itself
                    return;
                }

                if (pts.Count < 2)
                    return;
                for (int i = 0; i + 1 < pts.Count; i++)
                    _tree.Add(new Seg(pts[i], pts[i + 1]));
                Mark(pts, skipRadius);
                for (int i = 0; i + 1 < pts.Count; i++)
                    BlockSegment(pts[i], pts[i + 1]);

                // A new T junction: keep further junctions the refnet spacing away along the pipes.
                int end = ExactCell(pts[pts.Count - 1]);
                if (end >= 0 && _isTree[end] && _deg[end] == 3)
                    SpaceOut(end);
            }

            // Cuts the polyline at its first contact with the tree, walking from its first point.
            private List<Vec2> Truncate(List<Vec2> pts)
            {
                List<Vec2> result = new List<Vec2> { pts[0] };
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    Vec2 a = pts[i], b = pts[i + 1];
                    bool horizontal = a.Y == b.Y;
                    double a0 = horizontal ? a.X : a.Y;
                    double best = double.MaxValue;
                    bool hit = false;
                    foreach (Seg s in _tree)
                    {
                        double coord;
                        if (!ContactCoord(a, b, s, out coord))
                            continue;
                        double d = Math.Abs(coord - a0);
                        if (d < best)
                        {
                            best = d;
                            hit = true;
                            _hitCoord = coord;
                        }
                    }

                    if (hit)
                    {
                        Vec2 h = horizontal ? new Vec2(_hitCoord, a.Y) : new Vec2(a.X, _hitCoord);
                        if (h.X != result[result.Count - 1].X || h.Y != result[result.Count - 1].Y)
                            result.Add(h);
                        return result;
                    }

                    result.Add(b);
                }

                return result;
            }

            private double _hitCoord;
            private int _fallbacks;

            // The coordinate (along a -> b) of the contact of segment a-b with s nearest to a, excluding a itself.
            private static bool ContactCoord(Vec2 a, Vec2 b, Seg s, out double coord)
            {
                coord = 0;
                bool horizontal = a.Y == b.Y;
                double a0 = horizontal ? a.X : a.Y;
                double b0 = horizontal ? b.X : b.Y;
                double lo = Math.Min(a0, b0), hi = Math.Max(a0, b0);
                bool forward = b0 >= a0;
                double perp = horizontal ? a.Y : a.X;
                bool sHorizontal = s.A.Y == s.B.Y;
                if (sHorizontal == horizontal)
                {
                    double sp = horizontal ? s.A.Y : s.A.X;
                    if (sp != perp)
                        return false;
                    double t1 = horizontal ? Math.Min(s.A.X, s.B.X) : Math.Min(s.A.Y, s.B.Y);
                    double t2 = horizontal ? Math.Max(s.A.X, s.B.X) : Math.Max(s.A.Y, s.B.Y);
                    double ol = Math.Max(lo, t1), oh = Math.Min(hi, t2);
                    if (ol > oh)
                        return false;
                    coord = forward ? ol : oh;
                }
                else
                {
                    double along = horizontal ? s.A.X : s.A.Y;
                    double sLo = horizontal ? Math.Min(s.A.Y, s.B.Y) : Math.Min(s.A.X, s.B.X);
                    double sHi = horizontal ? Math.Max(s.A.Y, s.B.Y) : Math.Max(s.A.X, s.B.X);
                    if (along < lo || along > hi || perp < sLo || perp > sHi)
                        return false;
                    coord = along;
                }

                return coord != a0;
            }

            // ---- tree cells

            private int ExactCell(Vec2 p)
            {
                int i = (int)Math.Round((p.X - _ox) / _cs - 0.5);
                int j = (int)Math.Round((p.Y - _oy) / _cs - 0.5);
                if (i < 0 || j < 0 || i >= _nx || j >= _ny)
                    return -1;
                Vec2 c = Centre(i, j);
                return Math.Abs(c.X - p.X) <= Eps && Math.Abs(c.Y - p.Y) <= Eps ? j * _nx + i : -1;
            }

            private void SpaceOut(int junction)
            {
                double spacing = _options.JunctionSpacing;
                if (spacing <= _cs)
                    return;
                int ci = junction % _nx, cj = junction / _nx;
                int r = (int)Math.Ceiling(spacing / _cs);
                for (int dj = -r; dj <= r; dj++)
                {
                    for (int di = -r; di <= r; di++)
                    {
                        int i = ci + di, j = cj + dj;
                        if (i < 0 || j < 0 || i >= _nx || j >= _ny)
                            continue;
                        if ((Math.Abs(di) + Math.Abs(dj)) * _cs >= spacing - Eps)
                            continue;
                        int n = j * _nx + i;
                        if (_isTree[n] && _deg[n] < 250 && n != junction)
                            _spaced[n] = true;
                    }
                }
            }

            // A cell the tree passes through without a pipe node on its centre line: nothing may cross or join it.
            private void BlockCell(int cell)
            {
                if (_isTree[cell])
                    return;
                _isTree[cell] = true;
                _treeCells.Add(cell);
                _deg[cell] = 250;
                MarkNear(cell);
            }

            private void MarkNear(int cell)
            {
                int i = cell % _nx, j = cell / _nx;
                for (int d = 0; d < 4; d++)
                {
                    int ni = i + Dx[d], nj = j + Dy[d];
                    if (ni >= 0 && nj >= 0 && ni < _nx && nj < _ny)
                        _near[nj * _nx + ni] = true;
                }
            }

            private void BlockSegment(Vec2 a, Vec2 b)
            {
                bool horizontal = a.Y == b.Y;
                if (!horizontal && a.X != b.X)
                    return;
                double fixedCoord = horizontal ? a.Y : a.X;
                double fixedOrigin = horizontal ? _oy : _ox;
                int fixedLimit = horizontal ? _ny : _nx;
                double lo = horizontal ? Math.Min(a.X, b.X) : Math.Min(a.Y, b.Y);
                double hi = horizontal ? Math.Max(a.X, b.X) : Math.Max(a.Y, b.Y);
                double origin = horizontal ? _ox : _oy;
                int limit = horizontal ? _nx : _ny;
                int f0 = (int)Math.Floor((fixedCoord - fixedOrigin) / _cs - 1e-9);
                int f1 = (int)Math.Floor((fixedCoord - fixedOrigin) / _cs + 1e-9);
                int t0 = (int)Math.Floor((lo - origin) / _cs + 1e-9);
                int t1 = (int)Math.Floor((hi - origin) / _cs - 1e-9);
                if (t1 < t0)
                    t1 = t0;
                for (int f = f0; f <= f1; f++)
                {
                    if (f < 0 || f >= fixedLimit)
                        continue;
                    for (int t = Math.Max(0, t0); t <= Math.Min(limit - 1, t1); t++)
                        BlockCell(horizontal ? f * _nx + t : t * _nx + f);
                }
            }

            private void AddTreeCell(int cell, int amount)
            {
                if (!_isTree[cell])
                {
                    _isTree[cell] = true;
                    _treeCells.Add(cell);
                    MarkNear(cell);
                }

                _deg[cell] = (byte)Math.Min(250, _deg[cell] + amount);
            }

            // Registers the grid cells whose centres lie on the polyline, with their number of pipes.
            private void Mark(List<Vec2> pts, double skipRadius)
            {
                Vec2 start = pts[0];
                int lockCell = ExactCell(start);
                if (lockCell >= 0)
                    _locked[lockCell] = true;

                for (int k = 0; k + 1 < pts.Count; k++)
                {
                    Vec2 a = pts[k], b = pts[k + 1];
                    bool horizontal = a.Y == b.Y;
                    if (!horizontal && a.X != b.X)
                        continue;
                    double fixedCoord = horizontal ? a.Y : a.X;
                    double fixedOrigin = horizontal ? _oy : _ox;
                    double f = (fixedCoord - fixedOrigin) / _cs - 0.5;
                    int fixedIndex = (int)Math.Round(f);
                    if (Math.Abs(f - fixedIndex) * _cs > Eps)
                        continue;
                    int fixedLimit = horizontal ? _ny : _nx;
                    if (fixedIndex < 0 || fixedIndex >= fixedLimit)
                        continue;

                    double lo = horizontal ? Math.Min(a.X, b.X) : Math.Min(a.Y, b.Y);
                    double hi = horizontal ? Math.Max(a.X, b.X) : Math.Max(a.Y, b.Y);
                    double origin = horizontal ? _ox : _oy;
                    int limit = horizontal ? _nx : _ny;
                    int from = Math.Max(0, (int)Math.Ceiling((lo - origin) / _cs - 0.5 - Eps / _cs));
                    int to = Math.Min(limit - 1, (int)Math.Floor((hi - origin) / _cs - 0.5 + Eps / _cs));
                    for (int t = from; t <= to; t++)
                    {
                        int i = horizontal ? t : fixedIndex;
                        int j = horizontal ? fixedIndex : t;
                        Vec2 centre = Centre(i, j);
                        if (skipRadius > 0 && k == 0 && centre.DistanceTo(start) < skipRadius - Eps
                            && centre.DistanceTo(pts[pts.Count - 1]) > Eps)
                        {
                            BlockCell(j * _nx + i); // inside the straight stub: no branch may start here
                            continue;
                        }

                        double along = horizontal ? centre.X : centre.Y;
                        double a0 = horizontal ? a.X : a.Y;
                        double b0 = horizontal ? b.X : b.Y;
                        bool atEnd = Math.Abs(along - a0) <= Eps || Math.Abs(along - b0) <= Eps;
                        AddTreeCell(j * _nx + i, atEnd ? 1 : 2);
                    }
                }
            }

            // ---- search

            private void HeapPush(double key, int value)
            {
                if (_hn == _hk.Length)
                {
                    Array.Resize(ref _hk, _hn * 2);
                    Array.Resize(ref _hv, _hn * 2);
                }

                int i = _hn++;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (_hk[parent] <= key)
                        break;
                    _hk[i] = _hk[parent];
                    _hv[i] = _hv[parent];
                    i = parent;
                }

                _hk[i] = key;
                _hv[i] = value;
            }

            private bool HeapPop(out double key, out int value)
            {
                key = 0;
                value = 0;
                if (_hn == 0)
                    return false;
                key = _hk[0];
                value = _hv[0];
                _hn--;
                if (_hn > 0)
                {
                    double lastKey = _hk[_hn];
                    int lastValue = _hv[_hn];
                    int i = 0;
                    while (true)
                    {
                        int child = 2 * i + 1;
                        if (child >= _hn)
                            break;
                        if (child + 1 < _hn && _hk[child + 1] < _hk[child])
                            child++;
                        if (_hk[child] >= lastKey)
                            break;
                        _hk[i] = _hk[child];
                        _hv[i] = _hv[child];
                        i = child;
                    }

                    _hk[i] = lastKey;
                    _hv[i] = lastValue;
                }

                return true;
            }

            // Multi-source Dijkstra from the tree to the cheapest remaining unit anchor cell.
            private bool Search(out int unit, out List<Vec2> cells)
            {
                unit = -1;
                cells = null;
                for (int s = 0; s < _dist.Length; s++)
                    _dist[s] = double.MaxValue;
                _hn = 0;

                int sources = 0;
                foreach (int cell in _treeCells)
                {
                    if (_locked[cell] || _deg[cell] > 2)
                        continue;
                    sources++;
                    double start = _spaced[cell] ? _options.SpacingPenalty : 0;
                    for (int d = 0; d < 4; d++)
                    {
                        int state = cell * 4 + d;
                        _dist[state] = start;
                        _parent[state] = -1;
                        HeapPush(start, state);
                    }
                }

                if (sources == 0)
                    return false;

                double key;
                int popped;
                while (HeapPop(out key, out popped))
                {
                    if (key > _dist[popped])
                        continue;
                    int cell = popped >> 2;
                    int dir = popped & 3;
                    List<int> here;
                    if (!_isTree[cell] && _anchorUnits.TryGetValue(cell, out here) && here.Count > 0)
                    {
                        unit = here[0];
                        cells = new List<Vec2>();
                        int s = popped;
                        int previous = -1;
                        while (s >= 0)
                        {
                            int c = s >> 2;
                            if (c != previous)
                                cells.Add(CentreOf(c));
                            previous = c;
                            s = _parent[s];
                        }

                        return true;
                    }

                    int ci = cell % _nx, cj = cell / _nx;
                    for (int nd = 0; nd < 4; nd++)
                    {
                        if (nd == (dir ^ 2))
                            continue;
                        int ni = ci + Dx[nd], nj = cj + Dy[nd];
                        if (ni < 0 || nj < 0 || ni >= _nx || nj >= _ny)
                            continue;
                        int next = nj * _nx + ni;
                        if (_isTree[next])
                            continue;
                        double step = _cost[next] + (_room[next] != _room[cell] ? _boundary : 0) + (nd != dir ? _bend : 0)
                            + (_near[next] ? _options.NearPenalty : 0);
                        double total = key + step;
                        int nstate = next * 4 + nd;
                        if (total < _dist[nstate])
                        {
                            _dist[nstate] = total;
                            _parent[nstate] = popped;
                            HeapPush(total, nstate);
                        }
                    }
                }

                return false;
            }

            // Last resort when the grid search finds nothing: the rectilinear Prim step to the nearest unit.
            private void FallbackAttach(List<int> remaining)
            {
                int pick = remaining[0];
                double pickDistance = double.MaxValue;
                Vec2 pickPoint = _target;
                foreach (int u in remaining)
                {
                    Vec2 at;
                    double d = NearestOnTree(_units[u], _target, _tree, out at);
                    if (d < pickDistance)
                    {
                        pickDistance = d;
                        pick = u;
                        pickPoint = at;
                    }
                }

                Vec2 unit = _units[pick];
                remaining.Remove(pick);
                RemoveAnchor(pick);
                if (pickDistance <= 0)
                    return;
                List<Vec2> first = TruncatedPath(unit, new Vec2(pickPoint.X, unit.Y), pickPoint, _tree);
                List<Vec2> second = TruncatedPath(unit, new Vec2(unit.X, pickPoint.Y), pickPoint, _tree);
                List<Vec2> chosen = PathLength(second) < PathLength(first) - 1e-9 ? second : first;
                AddToTree(chosen, 0);
            }

            // ---- notes

            private void AddNotes(Candidate candidate, double stub)
            {
                List<List<Vec2>> lines = candidate.Graph.ToPolylines(_target, _units);
                Dictionary<Tuple<double, double>, int> count = new Dictionary<Tuple<double, double>, int>();
                foreach (List<Vec2> line in lines)
                {
                    Bump(count, line[0]);
                    Bump(count, line[line.Count - 1]);
                }

                int tight = 0;
                foreach (List<Vec2> line in lines)
                {
                    Vec2 a = line[0], b = line[line.Count - 1];
                    double length = PathLength(line);
                    bool aJunction = count[Key(a)] >= 3, bJunction = count[Key(b)] >= 3;
                    if (aJunction && bJunction && length < _options.JunctionSpacing - 1e-6)
                        tight++;
                    bool atTarget = Same(a, _target) || Same(b, _target);
                    if (atTarget && stub > 0 && (aJunction || bJunction) && length < stub - 1e-6)
                        candidate.Notes.Add(string.Format(CultureInfo.InvariantCulture,
                            "Only {0:0} mm of straight pipe at the target before the first branch (wanted {1:0} mm).", length, stub));
                }

                if (tight > 0)
                    candidate.Notes.Add(tight + " pipe piece(s) between two junctions are shorter than "
                        + _options.JunctionSpacing.ToString("0", CultureInfo.InvariantCulture)
                        + " mm; the cleaner's refnet spacing may need them moved.");
            }

            private static void Bump(Dictionary<Tuple<double, double>, int> count, Vec2 p)
            {
                int n;
                count.TryGetValue(Key(p), out n);
                count[Key(p)] = n + 1;
            }
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

    /// <summary>A room outline for the corridor mode: a closed ring of vertices and what kind of room it is.</summary>
    public sealed class RouteRoom
    {
        private static readonly Regex CorridorName = new Regex(
            @"\b(hanh lang|sanh|corridor|lobby|hall|hallway|passage|loi di)\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public string Name = "";

        /// <summary>The outline (closed implicitly: the last vertex joins the first).</summary>
        public List<Vec2> Ring = new List<Vec2>();

        /// <summary>A corridor or lobby: cheap to route through.</summary>
        public bool Corridor;

        /// <summary>A shaft: cheap inside, so that a route can reach a target at its centre.</summary>
        public bool Shaft;

        /// <summary>
        /// True when the HVACAI_LOAD room type is "lobby" (lobby and corridor in heat_load_table.json) or the
        /// name holds a corridor keyword (hành lang, sảnh, corridor, lobby, hall; accents are ignored).
        /// </summary>
        public static bool IsCorridor(string name, string roomType)
        {
            if (string.Equals((roomType ?? "").Trim(), "lobby", StringComparison.OrdinalIgnoreCase))
                return true;
            return CorridorName.IsMatch(RemoveAccents(name ?? ""));
        }

        public static string RemoveAccents(string text)
        {
            string decomposed = (text ?? "").Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
            StringBuilder plain = new StringBuilder(decomposed.Length);
            foreach (char ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    plain.Append(ch);
            }

            return plain.ToString().Normalize(NormalizationForm.FormC);
        }

        /// <summary>Even-odd point in polygon.</summary>
        public bool Contains(Vec2 p)
        {
            bool inside = false;
            int n = Ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vec2 a = Ring[i], b = Ring[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }

            return inside;
        }
    }

    /// <summary>
    /// The corridor cost model: corridor (and shaft) cells 1, other rooms RoomPenalty, outside OutsidePenalty,
    /// BoundaryPenalty for every room boundary crossed and BendPenalty for every bend, all per cell length.
    /// It prices any polyline set, so the corridor tree and the straight modes can be compared.
    /// </summary>
    public sealed class RouteCostModel
    {
        private readonly IList<RouteRoom> _rooms;
        private readonly double _cell;
        private readonly double _roomPenalty, _outsidePenalty, _boundary, _bend;

        public RouteCostModel(IList<RouteRoom> rooms, double cellSize, double roomPenalty, double outsidePenalty,
            double boundaryPenalty, double bendPenalty)
        {
            _rooms = rooms ?? new List<RouteRoom>();
            _cell = Math.Max(1, cellSize);
            _roomPenalty = roomPenalty;
            _outsidePenalty = outsidePenalty;
            _boundary = boundaryPenalty;
            _bend = bendPenalty;
        }

        /// <summary>Room index at the point (corridor over shaft over room), or -1 outside every room.</summary>
        public int RoomAt(Vec2 p)
        {
            int best = -1, bestKind = -1;
            for (int k = 0; k < _rooms.Count; k++)
            {
                RouteRoom room = _rooms[k];
                int kind = room.Corridor ? 2 : (room.Shaft ? 1 : 0);
                if (kind > bestKind && room.Contains(p))
                {
                    best = k;
                    bestKind = kind;
                }
            }

            return best;
        }

        public double CellCost(int room)
        {
            if (room < 0)
                return _outsidePenalty;
            RouteRoom r = _rooms[room];
            return r.Corridor || r.Shaft ? 1.0 : _roomPenalty;
        }

        public double CostOf(IEnumerable<List<Vec2>> polylines)
        {
            double total = 0;
            foreach (List<Vec2> line in polylines)
            {
                int previousRoom = int.MinValue;
                for (int i = 0; i + 1 < line.Count; i++)
                {
                    Vec2 a = line[i], b = line[i + 1];
                    double length = a.DistanceTo(b);
                    if (length <= 0)
                        continue;
                    int steps = Math.Max(1, (int)Math.Ceiling(length / (_cell / 2)));
                    double stepLength = length / steps;
                    for (int s = 0; s < steps; s++)
                    {
                        Vec2 mid = a + (b - a) * ((s + 0.5) / steps);
                        int room = RoomAt(mid);
                        total += stepLength / _cell * CellCost(room);
                        if (previousRoom != int.MinValue && room != previousRoom)
                            total += _boundary;
                        previousRoom = room;
                    }

                    if (i + 2 < line.Count)
                    {
                        Vec2 c = line[i + 2];
                        bool straight = (a.X == b.X && b.X == c.X) || (a.Y == b.Y && b.Y == c.Y);
                        if (!straight)
                            total += _bend;
                    }
                }
            }

            return total;
        }
    }

    /// <summary>
    /// The connection ports of one indoor-unit block family, in block coordinates (the block's own units,
    /// before scale and rotation). The direction is the way a pipe leaves the port: "+X", "-X", "+Y", "-Y" or
    /// empty. The pattern is matched as a case-insensitive part of the block name.
    /// </summary>
    public sealed class PortRule
    {
        public string Pattern = "";
        public double RefrigX, RefrigY;
        public string RefrigDir = "+X";
        public double DrainX, DrainY;
        public string DrainDir = "+X";

        public bool Matches(string blockName)
        {
            return Pattern.Length > 0 && blockName != null
                && blockName.IndexOf(Pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public bool IsValid(out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(Pattern))
                error = "A port rule needs a block name pattern.";
            else if (Pattern.Contains("|") || Pattern.Contains("\n"))
                error = "A block name pattern cannot contain '|'.";
            else if (!RoutePorts.IsDirection(RefrigDir) || !RoutePorts.IsDirection(DrainDir))
                error = "A port direction must be +X, -X, +Y, -Y or empty.";
            else if (!Finite(RefrigX) || !Finite(RefrigY) || !Finite(DrainX) || !Finite(DrainY))
                error = "A port offset must be a number.";
            return error == null;
        }

        private static bool Finite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v) && Math.Abs(v) < 1e7;
        }

        /// <summary>pattern|refrigX|refrigY|refrigDir|drainX|drainY|drainDir</summary>
        public string Format()
        {
            return Pattern + "|" + Num(RefrigX) + "|" + Num(RefrigY) + "|" + RefrigDir + "|"
                + Num(DrainX) + "|" + Num(DrainY) + "|" + DrainDir;
        }

        private static string Num(double v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>Null when the text is not a rule.</summary>
        public static PortRule Parse(string text)
        {
            string[] parts = (text ?? "").Split('|');
            if (parts.Length != 7)
                return null;
            PortRule rule = new PortRule { Pattern = parts[0].Trim() };
            if (!Number(parts[1], out rule.RefrigX) || !Number(parts[2], out rule.RefrigY)
                || !Number(parts[4], out rule.DrainX) || !Number(parts[5], out rule.DrainY))
                return null;
            rule.RefrigDir = parts[3].Trim();
            rule.DrainDir = parts[6].Trim();
            string error;
            return rule.IsValid(out error) ? rule : null;
        }

        private static bool Number(string text, out double value)
        {
            return double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public PortRule Clone()
        {
            return (PortRule)MemberwiseClone();
        }
    }

    /// <summary>Connection port positions of indoor-unit blocks.</summary>
    public static class RoutePorts
    {
        /// <summary>
        /// Default ports of the user's template cassettes. FCU-CAS-n kW (and IU-CAS) have a 950 x 950 body,
        /// x = -475..475 and y = -950..0 in block coordinates, base point at the middle of the top edge. The
        /// template blocks hold no pipe-connection marks (only the text "%%C12.7/6.4" of the gas/liquid size),
        /// so these offsets are ASSUMED: both connections on the +X side of the body, refrigerant 300 mm and
        /// drain 650 mm below the top edge, pipes leaving along +X. Change them in the dialog.
        /// </summary>
        public static List<PortRule> DefaultRules()
        {
            return new List<PortRule>
            {
                new PortRule { Pattern = "FCU-CAS", RefrigX = 475, RefrigY = -300, RefrigDir = "+X", DrainX = 475, DrainY = -650, DrainDir = "+X" },
                new PortRule { Pattern = "IU-CAS", RefrigX = 475, RefrigY = -300, RefrigDir = "+X", DrainX = 475, DrainY = -650, DrainDir = "+X" }
            };
        }

        /// <summary>The longest matching pattern, or null.</summary>
        public static PortRule Find(IList<PortRule> rules, string blockName)
        {
            PortRule best = null;
            if (rules == null)
                return null;
            foreach (PortRule rule in rules)
            {
                if (rule.Matches(blockName) && (best == null || rule.Pattern.Length > best.Pattern.Length))
                    best = rule;
            }

            return best;
        }

        public static bool IsDirection(string dir)
        {
            return dir == "" || dir == "+X" || dir == "-X" || dir == "+Y" || dir == "-Y";
        }

        public static Vec2 DirectionVector(string dir)
        {
            switch (dir)
            {
                case "+X":
                    return new Vec2(1, 0);
                case "-X":
                    return new Vec2(-1, 0);
                case "+Y":
                    return new Vec2(0, 1);
                case "-Y":
                    return new Vec2(0, -1);
                default:
                    return new Vec2(0, 0);
            }
        }

        /// <summary>The nearest axis direction (unit vector); zero stays zero.</summary>
        public static Vec2 SnapAxis(Vec2 d)
        {
            if (Math.Abs(d.X) < 1e-9 && Math.Abs(d.Y) < 1e-9)
                return new Vec2(0, 0);
            if (Math.Abs(d.X) >= Math.Abs(d.Y))
                return new Vec2(d.X > 0 ? 1 : -1, 0);
            return new Vec2(0, d.Y > 0 ? 1 : -1);
        }

        /// <summary>
        /// World position of a block-coordinate offset: insertion + R(rotation) * S(scale, negative = mirrored)
        /// * offset. The outward direction gets the same mirroring and rotation, snapped to an axis.
        /// </summary>
        public static void Resolve(Vec2 insertion, double rotation, double scaleX, double scaleY,
            double offsetX, double offsetY, string dir, out Vec2 point, out Vec2 outward)
        {
            double c = Math.Cos(rotation), s = Math.Sin(rotation);
            double lx = offsetX * scaleX, ly = offsetY * scaleY;
            point = new Vec2(insertion.X + c * lx - s * ly, insertion.Y + s * lx + c * ly);

            Vec2 d = DirectionVector(dir ?? "");
            double dx = d.X * (scaleX < 0 ? -1 : 1), dy = d.Y * (scaleY < 0 ? -1 : 1);
            outward = SnapAxis(new Vec2(c * dx - s * dy, s * dx + c * dy));
        }
    }
}
