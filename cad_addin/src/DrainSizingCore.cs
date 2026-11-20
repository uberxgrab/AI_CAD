using System;
using System.Collections.Generic;
using System.Globalization;

namespace HvacAiCadAddin.Pipes
{
    // AutoCAD-free sizing of a condensate drain tree. Daikin Si39 basis: an indoor unit drains 2 L/h per HP
    // and 1 HP class is 2.8 kW, so 0.71 L/h per kW. The allowable flow of a horizontal drain at a slope of
    // 1/100 is 88 L/h for VP30, 175 for VP40 and 334 for VP50; at 1/50 it is 125, 247 and 473. A pipe that
    // drains one unit is the smallest size (D27); otherwise the size comes from the total kW below. A pipe
    // never gets smaller toward the root. See AGENTS.md, "HVACDRAIN: draw and size a condensate network".

    /// <summary>One row of the sizing table: the largest total kW a size takes, at each slope.</summary>
    public sealed class DrainSizeBand
    {
        public readonly int Size;

        /// <summary>The largest total kW at a slope of 1/100 (at the reference condensate factor).</summary>
        public readonly double MaxKw100;

        /// <summary>The largest total kW at a slope of 1/50.</summary>
        public readonly double MaxKw50;

        /// <summary>The allowable flow in L/h at each slope; NaN where the sources give none.</summary>
        public readonly double Flow100;
        public readonly double Flow50;

        /// <summary>True for a band that no source confirms: it is extrapolated.</summary>
        public readonly bool Extrapolated;

        public DrainSizeBand(int size, double maxKw100, double maxKw50, double flow100, double flow50, bool extrapolated)
        {
            Size = size;
            MaxKw100 = maxKw100;
            MaxKw50 = maxKw50;
            Flow100 = flow100;
            Flow50 = flow50;
            Extrapolated = extrapolated;
        }
    }

    public static class DrainSizing
    {
        /// <summary>The condensate factor (L/h per kW) the table limits are written for.</summary>
        public const double ReferenceFactor = 0.71;

        /// <summary>The size of a pipe that drains one indoor unit.</summary>
        public const int LeafSize = 27;

        // Daikin Si39 basis. The kW limits are the allowable flows over 0.71 L/h per kW, rounded as the user
        // approved: VP30 (D34) 120, VP40 (D48) 245, VP50 (D60) 465 kW at 1/100; 175, 346, 662 kW at 1/50.
        // D90 and D110 have no source: 900 kW for D90 at 1/100 is the user's figure, and the 1/50 limit scales
        // it by the D60 ratio (662 / 465). Both are extrapolated and unconfirmed.
        public static readonly DrainSizeBand[] Bands =
        {
            new DrainSizeBand(34, 120, 175, 88, 125, false),
            new DrainSizeBand(48, 245, 346, 175, 247, false),
            new DrainSizeBand(60, 465, 662, 334, 473, false),
            new DrainSizeBand(90, 900, 1280, double.NaN, double.NaN, true),
            new DrainSizeBand(110, double.PositiveInfinity, double.PositiveInfinity, double.NaN, double.NaN, true)
        };

        /// <summary>The sizes a segment can be set to: the catalogue's condensate sizes.</summary>
        public static IList<int> Choices
        {
            get { return PipeCatalog.CondensateSizes; }
        }

        public static string SizeText(int size)
        {
            return "D" + size.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Reads "D34" (or "34") back to 34; false for a size the catalogue does not have.</summary>
        public static bool TryParseSize(string text, out int size)
        {
            size = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            string t = text.Trim();
            if (t.StartsWith("D", StringComparison.OrdinalIgnoreCase))
                t = t.Substring(1);
            return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out size)
                && Array.IndexOf(PipeCatalog.CondensateSizes, size) >= 0;
        }

        public static bool IsExtrapolatedSize(int size)
        {
            foreach (DrainSizeBand band in Bands)
            {
                if (band.Size == size)
                    return band.Extrapolated;
            }

            return false;
        }

        /// <summary>
        /// The table size for a pipe that drains the given units and kW. One unit gives <see cref="LeafSize"/>.
        /// A condensate factor other than the reference scales the limits (more litres per kW, lower limits).
        /// </summary>
        public static int TableSize(int unitCount, double kw, DrainSlope slope, double factor, out bool extrapolated)
        {
            extrapolated = false;
            if (unitCount <= 1)
                return LeafSize;

            double scale = ReferenceFactor / (factor > 0 ? factor : ReferenceFactor);
            foreach (DrainSizeBand band in Bands)
            {
                double limit = (slope == DrainSlope.OneIn50 ? band.MaxKw50 : band.MaxKw100) * scale;
                if (kw <= limit + 1e-9)
                {
                    extrapolated = band.Extrapolated;
                    return band.Size;
                }
            }

            extrapolated = true;
            return Bands[Bands.Length - 1].Size;
        }

        /// <summary>The preview colour of a size (AutoCAD colour index); yellow (2) is kept for warnings.</summary>
        public static short PreviewColour(int size)
        {
            switch (size)
            {
                case 21: return 8;
                case 27: return 4;
                case 34: return 3;
                case 48: return 5;
                case 60: return 6;
                case 90: return 30;
                case 110: return 1;
                default: return 7;
            }
        }

        // ------------------------------------------------------------------ the tree

        public static DrainSizingResult Compute(
            DrainTree tree,
            IList<double> unitKw,
            DrainSlope slope,
            double factor,
            IDictionary<int, int> manual)
        {
            DrainSizingResult result = new DrainSizingResult();
            int count = tree.Segments.Count;
            DrainSegmentSizing[] rows = new DrainSegmentSizing[count];

            // Units with no capacity: zero, negative or not a number.
            bool[] missing = new bool[tree.Units.Count];
            for (int u = 0; u < tree.Units.Count; u++)
            {
                double kw = u < unitKw.Count ? unitKw[u] : 0;
                missing[u] = double.IsNaN(kw) || double.IsInfinity(kw) || kw <= 0;
                if (missing[u])
                {
                    result.MissingCapacity++;
                    result.Warnings.Add(
                        "Indoor unit E" + (u + 1).ToString(CultureInfo.InvariantCulture)
                        + " has no capacity: its pipes cannot be sized yet.");
                }
            }

            // A segment always has a smaller index than the segments that flow into it, so the leaves come last.
            for (int s = count - 1; s >= 0; s--)
            {
                DrainSegment segment = tree.Segments[s];
                DrainSegmentSizing row = new DrainSegmentSizing { Index = s, UnitCount = segment.Units.Count };
                foreach (int u in segment.Units)
                {
                    if (missing[u])
                        row.MissingCapacity++;
                    else
                        row.TotalKw += unitKw[u];
                }

                row.FlowLh = row.TotalKw * factor;
                bool ownExtrapolated;
                row.TableSize = TableSize(row.UnitCount, row.TotalKw, slope, factor, out ownExtrapolated);

                int upstream = 0;
                foreach (int child in segment.Children)
                    upstream = Math.Max(upstream, rows[child].Size);
                row.UpstreamSize = upstream;
                row.AutoSize = Math.Max(row.TableSize, upstream);

                int chosen;
                if (manual != null && manual.TryGetValue(s, out chosen) && Array.IndexOf(PipeCatalog.CondensateSizes, chosen) >= 0)
                {
                    row.Manual = true;
                    row.Size = chosen;
                }
                else
                {
                    row.Size = row.AutoSize;
                }

                row.Extrapolated = IsExtrapolatedSize(row.Size);
                row.Warning = WarningFor(row);
                rows[s] = row;
            }

            for (int s = 0; s < count; s++)
            {
                result.Segments.Add(rows[s]);
                if (rows[s].Warning != null)
                    result.Warnings.Add("S" + (s + 1).ToString(CultureInfo.InvariantCulture) + ": " + rows[s].Warning);
            }

            for (int s = 0; s < count; s++)
            {
                if (rows[s].Extrapolated)
                {
                    result.Notes.Add(
                        "Sizes D90 and D110 are extrapolated: Daikin Si39 gives no drain size above VP50 (D60).");
                    break;
                }
            }

            return result;
        }

        private static string WarningFor(DrainSegmentSizing row)
        {
            if (row.Manual && row.Size < row.UpstreamSize)
                return "smaller than upstream " + SizeText(row.UpstreamSize);
            if (row.Manual && row.Size < row.TableSize)
            {
                return "below the " + SizeText(row.TableSize) + " the table gives for "
                    + row.TotalKw.ToString("0.##", CultureInfo.InvariantCulture) + " kW";
            }

            if (row.Extrapolated)
                return SizeText(row.Size) + " is extrapolated, not confirmed by Daikin Si39";
            return null;
        }
    }

    public sealed class DrainSegmentSizing
    {
        public int Index;
        public int UnitCount;

        /// <summary>Units upstream with no capacity (they count as zero kW).</summary>
        public int MissingCapacity;

        public double TotalKw;
        public double FlowLh;

        /// <summary>The size the table gives for this pipe alone.</summary>
        public int TableSize;

        /// <summary>The largest size of the pipes that flow into this one (0 for a pipe from a unit).</summary>
        public int UpstreamSize;

        /// <summary>The smallest size that is neither below the table nor below an upstream pipe.</summary>
        public int AutoSize;

        /// <summary>The size in use: the manual size when there is one, otherwise <see cref="AutoSize"/>.</summary>
        public int Size;

        public bool Manual;
        public bool Extrapolated;
        public string Warning;

        public string AutoText
        {
            get { return DrainSizing.SizeText(AutoSize); }
        }

        public string SizeText
        {
            get { return DrainSizing.SizeText(Size); }
        }
    }

    public sealed class DrainSizingResult
    {
        public readonly List<DrainSegmentSizing> Segments = new List<DrainSegmentSizing>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Notes = new List<string>();

        /// <summary>Indoor units with no capacity.</summary>
        public int MissingCapacity;

        /// <summary>Apply is allowed only when every unit has a capacity.</summary>
        public bool CanApply
        {
            get { return MissingCapacity == 0; }
        }
    }
}
