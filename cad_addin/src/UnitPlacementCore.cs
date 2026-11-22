using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HvacAiCadAddin.Units
{
    // AutoCAD-free core of HVACPLACEUNITS: which indoor unit (Daikin VRV IV cassette) a room needs, and
    // where in the room polygon the units go. Plain doubles only, so a console test project links this
    // file (tests/UnitCoreTests). Coordinates are drawing units (mm).

    /// <summary>One block of a unit family: Daikin capacity index, nominal kW and the template block name.</summary>
    public sealed class UnitModel
    {
        public int Index;
        public double Kw;
        public string BlockName;
    }

    /// <summary>
    /// A family of indoor unit blocks. The body is a square of <see cref="Width"/> x <see cref="Height"/>
    /// whose centre is at (0, <see cref="CentreY"/>) in block coordinates (the template cassettes have
    /// their base point at the middle of one edge).
    /// </summary>
    public sealed class UnitFamily
    {
        public string Name;
        public double Width;
        public double Height;
        public double CentreY;
        public UnitModel[] Models;
    }

    public static class UnitCatalog
    {
        /// <summary>
        /// FCU-CAS-n kW of CAD\MEP_DWT_3.dwg: 950 x 950 plan view, body spans y = -950..0 and x = -475..475
        /// around the base point. The template has no 71 (8.0 kW) or 15/20 cassette, so those are not offered.
        /// </summary>
        public static readonly UnitFamily Cassette = new UnitFamily
        {
            Name = "FCU-CAS",
            Width = 950,
            Height = 950,
            CentreY = -475,
            Models = new[]
            {
                new UnitModel { Index = 25, Kw = 2.8, BlockName = "FCU-CAS-2.8 kW" },
                new UnitModel { Index = 32, Kw = 3.6, BlockName = "FCU-CAS-3.6 kW" },
                new UnitModel { Index = 40, Kw = 4.5, BlockName = "FCU-CAS-4.5 kW" },
                new UnitModel { Index = 50, Kw = 5.6, BlockName = "FCU-CAS-5.6 kW" },
                new UnitModel { Index = 63, Kw = 7.1, BlockName = "FCU-CAS-7.1 kW" },
                new UnitModel { Index = 80, Kw = 9.0, BlockName = "FCU-CAS-9.0 kW" },
                new UnitModel { Index = 100, Kw = 11.2, BlockName = "FCU-CAS-11.2 kW" },
                new UnitModel { Index = 125, Kw = 14.0, BlockName = "FCU-CAS-14 kW" },
                new UnitModel { Index = 140, Kw = 16.0, BlockName = "FCU-CAS-16 kW" }
            }
        };

        public static readonly UnitFamily[] Families = { Cassette };

        public static UnitFamily Find(string name)
        {
            foreach (UnitFamily family in Families)
            {
                if (string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase))
                    return family;
            }

            return Cassette;
        }
    }

    /// <summary>HVACPLACEUNITS values remembered between runs, and the names of the data it uses.</summary>
    public sealed class UnitSettings
    {
        /// <summary>The layer the template uses for its indoor units.</summary>
        public const string UnitLayer = "EIA_1 MECHANICAL EQUIPMENT";

        /// <summary>Colour of the layer when the template has none.</summary>
        public const short UnitLayerColor = 4;

        /// <summary>Extended-data application the placed units are tagged with.</summary>
        public const string PlacedApp = "HVACAI_PLACED";

        /// <summary>Extended-data application of the room polylines (HVACROOMS).</summary>
        public const string RoomApp = "HVACAI_ROOM";

        /// <summary>Extended-data application of the room load (heat-load command).</summary>
        public const string LoadApp = "HVACAI_LOAD";

        public const string RoomLayer = "HVAC-ROOM";

        /// <summary>Free space kept between a unit and the walls (mm).</summary>
        public double Clearance = 600;

        /// <summary>The largest Daikin index a room may use for one unit.</summary>
        public int MaxIndex = 140;

        /// <summary>A unit may be this fraction smaller than the load it serves (0.03 = 3 percent).</summary>
        public double Tolerance = 0.03;

        public int MaxUnitsPerRoom = 6;

        public string Family = UnitCatalog.Cassette.Name;

        public bool IsValid(out string error)
        {
            error = null;
            if (double.IsNaN(Clearance) || Clearance < 0 || Clearance > 5000)
                error = "The wall clearance must be between 0 and 5000 mm.";
            else if (double.IsNaN(Tolerance) || Tolerance < 0 || Tolerance > 0.5)
                error = "The tolerance must be between 0 and 50 percent.";
            else if (MaxUnitsPerRoom < 1 || MaxUnitsPerRoom > 50)
                error = "The most units per room must be between 1 and 50.";
            else if (MaxIndex < UnitCatalog.Find(Family).Models[0].Index)
                error = "The largest index is smaller than the smallest unit.";
            return error == null;
        }

        public string Serialize()
        {
            StringBuilder text = new StringBuilder();
            text.Append("clearance=").Append(Clearance.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            text.Append("maxindex=").Append(MaxIndex.ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append("tolerance=").Append(Tolerance.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            text.Append("maxunits=").Append(MaxUnitsPerRoom.ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append("family=").Append((Family ?? "").Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
            return text.ToString();
        }

        /// <summary>Never throws: unknown keys and bad values keep the defaults.</summary>
        public static UnitSettings Deserialize(string text)
        {
            UnitSettings settings = new UnitSettings();
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
                int whole;
                switch (key)
                {
                    case "clearance":
                        if (TryNumber(value, out number) && number >= 0 && number <= 5000)
                            settings.Clearance = number;
                        break;
                    case "maxindex":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out whole) && whole > 0)
                            settings.MaxIndex = whole;
                        break;
                    case "tolerance":
                        if (TryNumber(value, out number) && number >= 0 && number <= 0.5)
                            settings.Tolerance = number;
                        break;
                    case "maxunits":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out whole) && whole >= 1 && whole <= 50)
                            settings.MaxUnitsPerRoom = whole;
                        break;
                    case "family":
                        if (value.Length > 0)
                            settings.Family = value;
                        break;
                }
            }

            return settings;
        }

        private static bool TryNumber(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    /// <summary>Reads the HVACAI_ROOM and HVACAI_LOAD extended data (values after the application name).</summary>
    public static class RoomTags
    {
        public sealed class RoomTag
        {
            public string Id;
            public string Kind;
            public string Name;
            public double AreaM2;
            public bool IsShaft { get { return string.Equals(Kind, "shaft", StringComparison.OrdinalIgnoreCase); } }
        }

        public sealed class LoadTag
        {
            public string RoomType;
            public double WattsPerM2;
            public double LoadKw;
            public double SafetyFactor;
        }

        /// <summary>values: the xdata values in order, the 1001 application name first (it is skipped).</summary>
        public static RoomTag ParseRoom(IList<object> values)
        {
            List<object> rest = Rest(values);
            if (rest.Count < 4)
                return null;
            string id = rest[0] as string;
            if (string.IsNullOrEmpty(id))
                return null;
            double area;
            if (!AsDouble(rest[3], out area))
                return null;
            return new RoomTag { Id = id, Kind = rest[1] as string ?? "room", Name = rest[2] as string ?? "", AreaM2 = area };
        }

        public static LoadTag ParseLoad(IList<object> values)
        {
            List<object> rest = Rest(values);
            if (rest.Count < 4)
                return null;
            LoadTag tag = new LoadTag { RoomType = rest[0] as string ?? "" };
            if (!AsDouble(rest[1], out tag.WattsPerM2) || !AsDouble(rest[2], out tag.LoadKw)
                || !AsDouble(rest[3], out tag.SafetyFactor))
                return null;
            return tag;
        }

        private static List<object> Rest(IList<object> values)
        {
            List<object> rest = new List<object>();
            if (values == null)
                return rest;
            for (int i = 1; i < values.Count; i++)
                rest.Add(values[i]);
            return rest;
        }

        private static bool AsDouble(object value, out double result)
        {
            result = 0;
            if (value is double)
                result = (double)value;
            else if (value is int)
                result = (int)value;
            else if (value is short)
                result = (short)value;
            else
                return false;
            return !double.IsNaN(result) && !double.IsInfinity(result);
        }
    }

    /// <summary>How many units a load needs and which model each one is.</summary>
    public sealed class UnitChoice
    {
        public int Units;
        public UnitModel Model;
        public double PerUnitKw;

        /// <summary>True when even the largest allowed model, at the most units, is too small.</summary>
        public bool Undersized;
    }

    public static class UnitSelector
    {
        /// <summary>
        /// The smallest model whose capacity covers the load (within the tolerance). When the load exceeds
        /// the largest allowed model, the load is split into the smallest number of equal units that each
        /// fit. <paramref name="unitsOverride"/> above zero fixes the number of units instead. Returns
        /// null for a load of zero or less.
        /// </summary>
        public static UnitChoice Choose(double loadKw, UnitFamily family, UnitSettings settings, int unitsOverride)
        {
            if (family == null || settings == null || double.IsNaN(loadKw) || loadKw <= 0)
                return null;

            List<UnitModel> allowed = new List<UnitModel>();
            foreach (UnitModel model in family.Models)
            {
                if (model.Index <= settings.MaxIndex)
                    allowed.Add(model);
            }

            if (allowed.Count == 0)
                allowed.Add(family.Models[0]);

            double largest = allowed[allowed.Count - 1].Kw;
            double slack = 1 + settings.Tolerance;
            int units = unitsOverride > 0 ? unitsOverride : 1;
            if (unitsOverride <= 0)
            {
                while (loadKw / units > largest * slack && units < settings.MaxUnitsPerRoom)
                    units++;
            }

            double per = loadKw / units;
            UnitModel pick = null;
            foreach (UnitModel model in allowed)
            {
                if (model.Kw * slack >= per)
                {
                    pick = model;
                    break;
                }
            }

            UnitChoice choice = new UnitChoice { Units = units, PerUnitKw = per };
            if (pick == null)
            {
                pick = allowed[allowed.Count - 1];
                choice.Undersized = true;
            }

            choice.Model = pick;
            return choice;
        }
    }

    public sealed class PlacedUnitPose
    {
        /// <summary>Centre of the unit body.</summary>
        public double X;
        public double Y;

        /// <summary>Rotation in radians (the block rotation).</summary>
        public double Rotation;

        /// <summary>False when the room is too small for the unit with its clearance (it is still placed).</summary>
        public bool Fits = true;
    }

    public static class UnitPlacer
    {
        /// <summary>Gap kept between two units (mm).</summary>
        public const double UnitGap = 300;

        /// <summary>
        /// The direction the walls mostly run in, in radians within (-pi/4, pi/4]: the length-weighted mean
        /// of the edge directions taken modulo 90 degrees (so a room with a short slanted corner still
        /// follows its long walls).
        /// </summary>
        public static double DominantAngle(double[][] polygon)
        {
            double sx = 0, sy = 0;
            int n = polygon.Length;
            for (int i = 0; i < n; i++)
            {
                double[] a = polygon[i];
                double[] b = polygon[(i + 1) % n];
                double dx = b[0] - a[0], dy = b[1] - a[1];
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 1e-9)
                    continue;
                double angle = Math.Atan2(dy, dx);
                sx += length * Math.Cos(4 * angle);
                sy += length * Math.Sin(4 * angle);
            }

            if (Math.Abs(sx) < 1e-9 && Math.Abs(sy) < 1e-9)
                return 0;
            double result = Math.Atan2(sy, sx) / 4;
            if (Math.Abs(result) < 1e-9)
                result = 0;
            return result;
        }

        /// <summary>The point of the polygon farthest from its boundary and that distance: x, y, distance.</summary>
        public static double[] Pole(double[][] polygon)
        {
            double minX, minY, maxX, maxY;
            Bounds(polygon, out minX, out minY, out maxX, out maxY);
            double step = GridStep(minX, minY, maxX, maxY, 0);
            double bx = (minX + maxX) / 2, by = (minY + maxY) / 2, best = -1;
            Search(polygon, minX, minY, maxX, maxY, step, ref bx, ref by, ref best);
            double fine = step / 4;
            Search(polygon, bx - step, by - step, bx + step, by + step, fine, ref bx, ref by, ref best);
            return new[] { bx, by, best };
        }

        private static void Search(double[][] polygon, double x0, double y0, double x1, double y1, double step,
            ref double bx, ref double by, ref double best)
        {
            for (double y = y0; y <= y1 + 1e-9; y += step)
            {
                for (double x = x0; x <= x1 + 1e-9; x += step)
                {
                    if (!Inside(polygon, x, y))
                        continue;
                    double d = DistanceToBoundary(polygon, x, y);
                    if (d > best + 1e-9)
                    {
                        best = d;
                        bx = x;
                        by = y;
                    }
                }
            }
        }

        /// <summary>
        /// Places <paramref name="count"/> square-ish units (width x height, mm) in the polygon: one at the
        /// roomiest point, several spread at equal spacing along the room's long direction. Each keeps the
        /// clearance from the walls and a gap from the others. Rotation is the dominant wall direction.
        /// </summary>
        public static List<PlacedUnitPose> Place(double[][] polygon, int count, double width, double height, double clearance)
        {
            List<PlacedUnitPose> result = new List<PlacedUnitPose>();
            if (polygon == null || polygon.Length < 3 || count < 1)
                return result;

            double angle = DominantAngle(polygon);
            double cos = Math.Cos(angle), sin = Math.Sin(angle);

            // Work in a frame where the walls run along x and y.
            double[][] local = new double[polygon.Length][];
            for (int i = 0; i < polygon.Length; i++)
                local[i] = new[] { polygon[i][0] * cos + polygon[i][1] * sin, -polygon[i][0] * sin + polygon[i][1] * cos };

            double minX, minY, maxX, maxY;
            Bounds(local, out minX, out minY, out maxX, out maxY);
            bool alongX = (maxX - minX) >= (maxY - minY);
            double lo = alongX ? minX : minY;
            double hi = alongX ? maxX : maxY;

            List<double[]> placed = new List<double[]>();
            double[] clearances = clearance > 0 ? new[] { clearance, 0 } : new[] { 0.0 };
            for (int unit = 0; unit < count; unit++)
            {
                double slabLo = lo + (hi - lo) * unit / count;
                double slabHi = lo + (hi - lo) * (unit + 1) / count;
                double[] spot = null;
                bool fits = true;
                for (int c = 0; c < clearances.Length && spot == null; c++)
                {
                    double hw = width / 2 + clearances[c], hh = height / 2 + clearances[c];
                    spot = Find(local, minX, minY, maxX, maxY, hw, hh, width, height, placed, alongX,
                        count > 1 ? slabLo : lo, count > 1 ? slabHi : hi);
                    if (spot == null && count > 1)
                        spot = Find(local, minX, minY, maxX, maxY, hw, hh, width, height, placed, alongX, lo, hi);
                    if (spot != null && c > 0)
                        fits = false;
                }

                if (spot == null)
                {
                    // No clear spot: the roomiest point of the slab, flagged.
                    fits = false;
                    double[] pole = Pole(local);
                    spot = new[] { pole[0], pole[1] };
                    if (count > 1)
                    {
                        double middle = (slabLo + slabHi) / 2;
                        spot = alongX ? new[] { middle, pole[1] } : new[] { pole[0], middle };
                    }
                }

                placed.Add(spot);
                double wx = spot[0] * cos - spot[1] * sin;
                double wy = spot[0] * sin + spot[1] * cos;
                result.Add(new PlacedUnitPose { X = wx, Y = wy, Rotation = angle, Fits = fits });
            }

            return result;
        }

        /// <summary>The block insertion point for a unit body centre, given where the base point is.</summary>
        public static double[] InsertionPoint(PlacedUnitPose pose, UnitFamily family)
        {
            // The body centre is at (0, CentreY) in block coordinates: insertion = centre - R * (0, CentreY).
            double cos = Math.Cos(pose.Rotation), sin = Math.Sin(pose.Rotation);
            return new[] { pose.X + sin * family.CentreY, pose.Y - cos * family.CentreY };
        }

        /// <summary>The four corners of the unit body in drawing coordinates.</summary>
        public static double[][] Corners(PlacedUnitPose pose, double width, double height)
        {
            double cos = Math.Cos(pose.Rotation), sin = Math.Sin(pose.Rotation);
            double[][] corners = new double[4][];
            double[] sx = { -1, 1, 1, -1 };
            double[] sy = { -1, -1, 1, 1 };
            for (int i = 0; i < 4; i++)
            {
                double lx = sx[i] * width / 2, ly = sy[i] * height / 2;
                corners[i] = new[] { pose.X + lx * cos - ly * sin, pose.Y + lx * sin + ly * cos };
            }

            return corners;
        }

        // The best centre in the slab: the farthest from the walls whose clearance box fits the room and
        // does not touch an earlier unit. Ties go to the middle of the slab.
        private static double[] Find(double[][] local, double minX, double minY, double maxX, double maxY,
            double hw, double hh, double width, double height, List<double[]> placed, bool alongX, double slabLo, double slabHi)
        {
            double need = Math.Min(hw, hh);
            double step = GridStep(minX, minY, maxX, maxY, need);
            double middle = (slabLo + slabHi) / 2;
            double bestX = 0, bestY = 0, bestScore = double.NegativeInfinity;
            bool found = false;
            Scan(local, minX, minY, maxX, maxY, step, hw, hh, width, height, need, placed, alongX, slabLo, slabHi, middle,
                ref bestX, ref bestY, ref bestScore, ref found);
            if (!found)
                return null;
            double fine = step / 4;
            Scan(local, bestX - step, bestY - step, bestX + step, bestY + step, fine, hw, hh, width, height, need, placed,
                alongX, slabLo, slabHi, middle, ref bestX, ref bestY, ref bestScore, ref found);
            return new[] { bestX, bestY };
        }

        private static void Scan(double[][] local, double x0, double y0, double x1, double y1, double step,
            double hw, double hh, double width, double height, double need, List<double[]> placed, bool alongX,
            double slabLo, double slabHi, double middle, ref double bestX, ref double bestY, ref double bestScore, ref bool found)
        {
            for (double y = y0; y <= y1 + 1e-9; y += step)
            {
                for (double x = x0; x <= x1 + 1e-9; x += step)
                {
                    double along = alongX ? x : y;
                    if (along < slabLo - 1e-9 || along > slabHi + 1e-9)
                        continue;
                    if (!Inside(local, x, y))
                        continue;
                    double d = DistanceToBoundary(local, x, y);
                    if (d < need - 1e-9)
                        continue;
                    if (Overlaps(placed, x, y, width, height))
                        continue;
                    if (!BoxInside(local, x, y, hw, hh))
                        continue;
                    double score = d - 0.001 * Math.Abs(along - middle);
                    if (!found || score > bestScore)
                    {
                        found = true;
                        bestScore = score;
                        bestX = x;
                        bestY = y;
                    }
                }
            }
        }

        private static bool Overlaps(List<double[]> placed, double x, double y, double width, double height)
        {
            foreach (double[] other in placed)
            {
                if (Math.Abs(other[0] - x) < width + UnitGap - 1e-9 && Math.Abs(other[1] - y) < height + UnitGap - 1e-9)
                    return true;
            }

            return false;
        }

        private static double GridStep(double minX, double minY, double maxX, double maxY, double need)
        {
            double smaller = Math.Min(maxX - minX, maxY - minY);
            double step = Math.Max(25, Math.Min(150, smaller / 40));
            double cells = ((maxX - minX) / step + 1) * ((maxY - minY) / step + 1);
            while (cells > 90000)
            {
                step *= 1.5;
                cells = ((maxX - minX) / step + 1) * ((maxY - minY) / step + 1);
            }

            return step;
        }

        private static void Bounds(double[][] polygon, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = double.MaxValue;
            maxX = maxY = double.MinValue;
            foreach (double[] p in polygon)
            {
                minX = Math.Min(minX, p[0]);
                maxX = Math.Max(maxX, p[0]);
                minY = Math.Min(minY, p[1]);
                maxY = Math.Max(maxY, p[1]);
            }
        }

        public static bool Inside(double[][] polygon, double x, double y)
        {
            bool inside = false;
            int n = polygon.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = polygon[i][0], yi = polygon[i][1], xj = polygon[j][0], yj = polygon[j][1];
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                    inside = !inside;
            }

            return inside;
        }

        public static double DistanceToBoundary(double[][] polygon, double x, double y)
        {
            double best = double.MaxValue;
            int n = polygon.Length;
            for (int i = 0; i < n; i++)
            {
                double[] a = polygon[i];
                double[] b = polygon[(i + 1) % n];
                double dx = b[0] - a[0], dy = b[1] - a[1];
                double length2 = dx * dx + dy * dy;
                double t = length2 < 1e-18 ? 0 : ((x - a[0]) * dx + (y - a[1]) * dy) / length2;
                t = Math.Max(0, Math.Min(1, t));
                double px = a[0] + t * dx - x, py = a[1] + t * dy - y;
                best = Math.Min(best, Math.Sqrt(px * px + py * py));
            }

            return best;
        }

        /// <summary>True when the box (centre, half extents) lies inside the polygon (touching a wall is fine).</summary>
        public static bool BoxInside(double[][] polygon, double cx, double cy, double hw, double hh)
        {
            if (!Inside(polygon, cx, cy))
                return false;
            const double eps = 1e-6;
            double x0 = cx - hw + eps, x1 = cx + hw - eps, y0 = cy - hh + eps, y1 = cy + hh - eps;
            int n = polygon.Length;
            for (int i = 0; i < n; i++)
            {
                double[] a = polygon[i];
                double[] b = polygon[(i + 1) % n];
                if (SegmentHitsBox(a[0], a[1], b[0], b[1], x0, y0, x1, y1))
                    return false;
            }

            return true;
        }

        // Liang-Barsky: does the segment pass through the (shrunk) box?
        private static bool SegmentHitsBox(double ax, double ay, double bx, double by, double x0, double y0, double x1, double y1)
        {
            double dx = bx - ax, dy = by - ay;
            double t0 = 0, t1 = 1;
            double[] p = { -dx, dx, -dy, dy };
            double[] q = { ax - x0, x1 - ax, ay - y0, y1 - ay };
            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(p[i]) < 1e-12)
                {
                    if (q[i] < 0)
                        return false;
                }
                else
                {
                    double t = q[i] / p[i];
                    if (p[i] < 0)
                    {
                        if (t > t1)
                            return false;
                        if (t > t0)
                            t0 = t;
                    }
                    else
                    {
                        if (t < t0)
                            return false;
                        if (t < t1)
                            t1 = t;
                    }
                }
            }

            return t0 <= t1;
        }
    }
}
