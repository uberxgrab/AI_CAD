using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HvacAiCadAddin.Pipes;

namespace HvacAiCadAddin.Vrv
{
    // AutoCAD-free core of HVACPLACEODU: reading an indoor unit's capacity from its block name, label text
    // or the HVACAI_PLACED data, the outdoor unit choice (it calls the Daikin VRV IV tables of
    // RefrigSizingCore.cs, nothing is copied), which template block stands in for an HP the template does
    // not have, the footprint of that block, the label text, a proposal for splitting an oversized system,
    // and the remembered settings. Plain doubles only, so tests/OduRiserCoreTests links this file.

    /// <summary>One indoor unit that belongs to a VRV system.</summary>
    public sealed class OduIndoorUnit
    {
        public string Handle;
        public string BlockName;
        public double Kw;
        /// <summary>Daikin capacity index.</summary>
        public int Index;
        public bool Nominal;
        /// <summary>"placed" (HVACAI_PLACED data), "block" (kW in the block name) or "label" (equipment label).</summary>
        public string Source;
        public double X;
        public double Y;

        public string Display
        {
            get
            {
                return (string.IsNullOrEmpty(BlockName) ? "?" : BlockName) + "  ("
                    + Index.ToString(CultureInfo.InvariantCulture) + ", "
                    + Kw.ToString("0.0#", CultureInfo.InvariantCulture) + " kW)";
            }
        }
    }

    /// <summary>Where an indoor unit's capacity comes from. The same three sources HVACREFRIG reads.</summary>
    public static class IndoorCapacity
    {
        /// <summary>The HVACAI_PLACED application name (HVACPLACEUNITS).</summary>
        public const string PlacedApp = "HVACAI_PLACED";

        /// <summary>The block that holds an equipment label.</summary>
        public const string LabelBlockName = "HVAC-NAME OF EQUIPMENTS";

        /// <summary>A block whose name has "number kW": FCU-CAS-2.8 kW, IU-5,6 kW.</summary>
        public static bool TryFromName(string blockName, out OduIndoorUnit unit)
        {
            return Make(blockName, "block", out unit);
        }

        /// <summary>The PROPERTIES text of an equipment label block ("2.8 kW ...").</summary>
        public static bool TryFromLabel(string title, string properties, out OduIndoorUnit unit)
        {
            if (!Make(properties, "label", out unit))
                return false;
            unit.BlockName = string.IsNullOrWhiteSpace(title) ? LabelBlockName : title.Trim();
            return true;
        }

        private static bool Make(string text, string source, out OduIndoorUnit unit)
        {
            unit = null;
            double kw;
            if (!CapacityText.TryParseKw(text, out kw))
                return false;
            unit = new OduIndoorUnit
            {
                BlockName = text,
                Kw = kw,
                Index = VrvTables.NearestIndex(kw),
                Nominal = VrvTables.IsNominalKw(kw),
                Source = source
            };
            return true;
        }

        /// <summary>
        /// The HVACAI_PLACED values in order, the 1001 name first: [name][room id][index][kW][number][of].
        /// </summary>
        public static bool TryFromPlacedTag(IList<object> values, string blockName, out OduIndoorUnit unit)
        {
            unit = null;
            if (values == null || values.Count < 4)
                return false;
            double index;
            double kw;
            if (!AsNumber(values[2], out index) || !AsNumber(values[3], out kw) || index <= 0 || kw <= 0)
                return false;
            unit = new OduIndoorUnit
            {
                BlockName = blockName,
                Kw = kw,
                Index = (int)Math.Round(index),
                Nominal = true,
                Source = "placed"
            };
            return true;
        }

        private static bool AsNumber(object value, out double number)
        {
            number = 0;
            if (value is double)
                number = (double)value;
            else if (value is int)
                number = (int)value;
            else if (value is short)
                number = (short)value;
            else
                return false;
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }
    }

    /// <summary>HVACPLACEODU values remembered between runs.</summary>
    public sealed class OduSettings
    {
        /// <summary>The outdoor unit blocks and their layer in the template.</summary>
        public const string UnitLayer = "EIA_1 MECHANICAL EQUIPMENT";

        public const string LabelLayer = "EIA_1 MECHANICAL EQUIPMENT ANNOTATIONS";

        public const short LayerColor = 4;

        /// <summary>Extended-data application of the placed outdoor unit.</summary>
        public const string OduApp = "HVACAI_ODU";

        /// <summary>Extended-data application of the label: [1000 handle of its outdoor unit block].</summary>
        public const string LabelApp = "HVACAI_ODULABEL";

        /// <summary>Label text height (mm).</summary>
        public double TextHeight = 150;

        /// <summary>An extra line of the label, for example the model series.</summary>
        public string ModelText = "";

        public bool WriteLabel = true;

        public bool IsValid(out string error)
        {
            error = null;
            if (double.IsNaN(TextHeight) || TextHeight < 20 || TextHeight > 2000)
                error = "The text height must be between 20 and 2000 mm.";
            else if (ModelText != null && ModelText.Length > 80)
                error = "The model text is longer than 80 characters.";
            return error == null;
        }

        public string Serialize()
        {
            StringBuilder text = new StringBuilder();
            text.Append("textheight=").Append(TextHeight.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            text.Append("model=").Append(Clean(ModelText)).Append('\n');
            text.Append("label=").Append(WriteLabel ? "1" : "0").Append('\n');
            return text.ToString();
        }

        /// <summary>Never throws: unknown keys and bad values keep the defaults.</summary>
        public static OduSettings Deserialize(string text)
        {
            OduSettings settings = new OduSettings();
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
                    case "textheight":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                            && !double.IsNaN(number) && !double.IsInfinity(number) && number >= 20 && number <= 2000)
                            settings.TextHeight = number;
                        break;
                    case "model":
                        if (value.Length <= 80)
                            settings.ModelText = value;
                        break;
                    case "label":
                        settings.WriteLabel = value != "0";
                        break;
                }
            }

            return settings;
        }

        private static string Clean(string value)
        {
            return (value ?? "").Replace('\n', ' ').Replace('\r', ' ');
        }
    }

    /// <summary>The outdoor unit chosen for a set of indoor units.</summary>
    public sealed class OduPlan
    {
        public int UnitCount;
        public int IndoorTotal;
        public int Hp;
        public bool HpIsDefault;
        public int OduIndex;
        /// <summary>Indoor total over the outdoor index, in percent.</summary>
        public double Ratio;
        /// <summary>True when the indoor total is above the largest outdoor unit.</summary>
        public bool OverLargest;
        public bool RatioOutside;
        /// <summary>The template block name of this HP: <c>ODU-12 HP</c>.</summary>
        public string BlockName;
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>The block that is inserted for an HP, and what to tell the user about it.</summary>
    public sealed class OduBlockChoice
    {
        public string BlockName;
        /// <summary>True when the block is not the template's block of that HP (nearest or drawn).</summary>
        public bool Substitute;
        /// <summary>True when no template block can be used and a simple block is drawn under the HP's own name.</summary>
        public bool Fallback;
        public string Warning;
    }

    public static class OduPlanner
    {
        public static string BlockNameFor(int hp)
        {
            return "ODU-" + hp.ToString(CultureInfo.InvariantCulture) + " HP";
        }

        /// <summary>The HP of an outdoor unit block name: <c>ODU-12 HP</c> gives 12 (a table size only).</summary>
        public static bool TryParseHp(string blockName, out int hp)
        {
            hp = 0;
            if (string.IsNullOrEmpty(blockName))
                return false;
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
                blockName.Trim(), @"^ODU-(\d+)\s*HP$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            return match.Success
                && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out hp)
                && VrvTables.IsHp(hp);
        }

        /// <summary>Total of the indoor indexes.</summary>
        public static int Sum(IEnumerable<OduIndoorUnit> units)
        {
            int sum = 0;
            foreach (OduIndoorUnit unit in units)
                sum += unit.Index;
            return sum;
        }

        /// <summary>
        /// The outdoor unit for an indoor total. requestedHp 0 (or not an HP of the table) takes the
        /// smallest HP whose index covers the total, as HVACREFRIG does. The ratio rule is 50-130 percent.
        /// </summary>
        public static OduPlan Plan(int unitCount, int indoorTotal, int requestedHp)
        {
            OduPlan plan = new OduPlan { UnitCount = unitCount, IndoorTotal = indoorTotal };
            plan.HpIsDefault = !VrvTables.IsHp(requestedHp);
            plan.Hp = plan.HpIsDefault ? VrvTables.DefaultHp(indoorTotal) : requestedHp;
            plan.OduIndex = VrvTables.OduIndex(plan.Hp);
            plan.Ratio = plan.OduIndex > 0 ? 100.0 * indoorTotal / plan.OduIndex : 0;
            plan.BlockName = BlockNameFor(plan.Hp);

            if (unitCount == 0 || indoorTotal <= 0)
                plan.Warnings.Add("No indoor unit with a readable capacity.");

            int largest = VrvTables.OduIndex(VrvTables.HpChoices[VrvTables.HpChoices.Length - 1]);
            plan.OverLargest = indoorTotal > largest;
            if (plan.OverLargest)
            {
                plan.Warnings.Add("The indoor total " + indoorTotal.ToString(CultureInfo.InvariantCulture)
                    + " is above the largest outdoor unit ("
                    + VrvTables.HpChoices[VrvTables.HpChoices.Length - 1].ToString(CultureInfo.InvariantCulture)
                    + " HP, index " + largest.ToString(CultureInfo.InvariantCulture) + "): split the units into several systems.");
            }

            plan.RatioOutside = indoorTotal > 0
                && (plan.Ratio < VrvTables.MinRatio - 1e-9 || plan.Ratio > VrvTables.MaxRatio + 1e-9);
            if (plan.RatioOutside)
            {
                plan.Warnings.Add("Connection ratio " + plan.Ratio.ToString("0", CultureInfo.InvariantCulture)
                    + " % is outside " + VrvTables.MinRatio.ToString("0", CultureInfo.InvariantCulture) + "-"
                    + VrvTables.MaxRatio.ToString("0", CultureInfo.InvariantCulture) + " % ("
                    + indoorTotal.ToString(CultureInfo.InvariantCulture) + " of "
                    + plan.OduIndex.ToString(CultureInfo.InvariantCulture) + " at "
                    + plan.Hp.ToString(CultureInfo.InvariantCulture) + " HP).");
            }

            if (!plan.HpIsDefault)
            {
                int smallest = VrvTables.DefaultHp(indoorTotal);
                if (plan.Hp < smallest && !plan.OverLargest)
                    plan.Warnings.Add(plan.Hp.ToString(CultureInfo.InvariantCulture)
                        + " HP is smaller than the smallest HP that covers the total (" + smallest + " HP).");
            }

            if (VrvTables.IsOduIndexUnconfirmed(plan.Hp))
                plan.Notes.Add("The outdoor index of " + plan.Hp.ToString(CultureInfo.InvariantCulture)
                    + " HP (HP x 25) is unconfirmed above 8 HP.");
            return plan;
        }

        /// <summary>Adds the unconfirmed-index notes for the units to a plan.</summary>
        public static void AddUnitNotes(OduPlan plan, IEnumerable<OduIndoorUnit> units)
        {
            HashSet<int> seen = new HashSet<int>();
            foreach (OduIndoorUnit unit in units)
            {
                if (VrvTables.IsUnitUnconfirmed(unit.Index) && seen.Add(unit.Index))
                    plan.Notes.Add("Indoor index " + unit.Index.ToString(CultureInfo.InvariantCulture) + " ("
                        + VrvTables.NominalKw(unit.Index).ToString("0.0", CultureInfo.InvariantCulture)
                        + " kW) is unconfirmed in the tables.");
                if (!unit.Nominal)
                    plan.Notes.Add(unit.Display + ": the kW is not a nominal capacity, the nearest index was used.");
            }
        }

        /// <summary>
        /// The block for an HP: the template's own <c>ODU-n HP</c>; else the template block of the nearest
        /// HP (the larger one on a tie), with a warning; else, when the template has no ODU block at all, a
        /// drawn block under the HP's own name. <paramref name="has"/> tells whether a block name exists.
        /// </summary>
        public static OduBlockChoice ResolveBlock(int hp, Func<string, bool> has)
        {
            string wanted = BlockNameFor(hp);
            if (has(wanted))
                return new OduBlockChoice { BlockName = wanted };

            int best = 0;
            int bestDistance = int.MaxValue;
            foreach (int candidate in VrvTables.HpChoices)
            {
                if (!has(BlockNameFor(candidate)))
                    continue;
                int distance = Math.Abs(candidate - hp);
                if (distance < bestDistance || (distance == bestDistance && candidate > best))
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            if (best > 0)
                return new OduBlockChoice
                {
                    BlockName = BlockNameFor(best),
                    Substitute = true,
                    Warning = wanted + " is not in the template; the nearest block " + BlockNameFor(best) + " is used."
                };

            return new OduBlockChoice
            {
                BlockName = wanted,
                Substitute = true,
                Fallback = true,
                Warning = "No ODU block in the template; " + wanted + " is drawn as a simple block (rectangle and label)."
            };
        }

        /// <summary>
        /// Plan-view size of the block (mm): the template's ODU-n HP blocks are 930 x 766 up to 12 HP,
        /// 1240 x 765 for 14-20 HP and 1880 x 766 for 22-24 HP, base point at the middle of the top edge
        /// (body below it). Above 24 HP the drawn fallback is as wide as one 24 HP module per started 24 HP.
        /// </summary>
        public static void Footprint(int hp, out double width, out double height)
        {
            height = 766;
            if (hp <= 12)
                width = 930;
            else if (hp <= 20)
            {
                width = 1240;
                height = 765;
            }
            else
                width = 1880 * Math.Max(1, (int)Math.Ceiling(hp / 24.0));
        }

        /// <summary>Corners of the footprint (x, y), counter-clockwise from the lower left, in world coordinates.</summary>
        public static double[][] Corners(double x, double y, double rotation, double width, double height)
        {
            double[][] local =
            {
                new[] { -width / 2, -height },
                new[] { width / 2, -height },
                new[] { width / 2, 0 },
                new[] { -width / 2, 0 }
            };
            double cos = Math.Cos(rotation);
            double sin = Math.Sin(rotation);
            double[][] result = new double[4][];
            for (int i = 0; i < 4; i++)
                result[i] = new[] { x + local[i][0] * cos - local[i][1] * sin, y + local[i][0] * sin + local[i][1] * cos };
            return result;
        }

        /// <summary>The label MText contents: HP, optional model line, indoor/outdoor index, ratio.</summary>
        public static string LabelText(OduPlan plan, string modelText)
        {
            StringBuilder text = new StringBuilder();
            text.Append(plan.BlockName);
            if (!string.IsNullOrWhiteSpace(modelText))
                text.Append("\\P").Append(modelText.Trim());
            text.Append("\\P\u03A3 index ")
                .Append(plan.IndoorTotal.ToString(CultureInfo.InvariantCulture))
                .Append(" / ")
                .Append(plan.OduIndex.ToString(CultureInfo.InvariantCulture));
            text.Append("\\P").Append(plan.Ratio.ToString("0", CultureInfo.InvariantCulture)).Append(" %");
            return text.ToString();
        }

        /// <summary>
        /// Splits units over the fewest systems whose indoor total stays within <paramref name="maxIndex"/>,
        /// largest units first into the lightest system. Returns, per system, positions in <paramref name="units"/>.
        /// </summary>
        public static List<List<int>> SplitProposal(IList<OduIndoorUnit> units, int maxIndex)
        {
            List<List<int>> groups = new List<List<int>>();
            if (units.Count == 0 || maxIndex <= 0)
                return groups;

            int total = 0;
            foreach (OduIndoorUnit unit in units)
                total += unit.Index;
            int count = Math.Max(1, (int)Math.Ceiling(total / (double)maxIndex));

            List<int> order = new List<int>();
            for (int i = 0; i < units.Count; i++)
                order.Add(i);
            order.Sort((a, b) =>
            {
                int byIndex = units[b].Index.CompareTo(units[a].Index);
                return byIndex != 0 ? byIndex : a.CompareTo(b);
            });

            while (true)
            {
                groups.Clear();
                int[] sums = new int[count];
                for (int g = 0; g < count; g++)
                    groups.Add(new List<int>());

                bool fits = true;
                foreach (int position in order)
                {
                    int lightest = 0;
                    for (int g = 1; g < count; g++)
                    {
                        if (sums[g] < sums[lightest])
                            lightest = g;
                    }

                    sums[lightest] += units[position].Index;
                    groups[lightest].Add(position);
                    if (sums[lightest] > maxIndex)
                        fits = false;
                }

                if (fits || count >= units.Count)
                    break;
                count++;
            }

            foreach (List<int> group in groups)
                group.Sort();
            return groups;
        }
    }
}
