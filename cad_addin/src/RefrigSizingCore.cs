using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Pipes
{
    // AutoCAD-free refrigerant pipe sizing for HVACREFRIG: the Daikin VRV IV heat-pump tables (RXYQ-T/U),
    // the sizing of a pipe tree (NetworkTopologyCore.cs) from the indoor units' capacity indexes, the rule
    // warnings, and the size label placement. Plain doubles and Vec2 only, so tests/PipeCoreTests compiles
    // this file without AutoCAD. The tables and their sources are in AGENTS.md, "HVACREFRIG", "Pipe sizing".
    // Cells marked "unconfirmed" were not found in the sources the user gave; check them against the
    // Daikin data book before relying on them.

    /// <summary>A refrigerant pipe pair: gas and liquid outer diameters in millimetres.</summary>
    public struct PipePair : IEquatable<PipePair>
    {
        public readonly double Gas;
        public readonly double Liquid;

        public PipePair(double gas, double liquid)
        {
            Gas = gas;
            Liquid = liquid;
        }

        /// <summary>The key the layer names use: <c>15.9-9.5</c>.</summary>
        public string Key
        {
            get
            {
                return Gas.ToString("0.0", CultureInfo.InvariantCulture) + "-"
                    + Liquid.ToString("0.0", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>The label text: <c>%%C15.9/9.5</c> ("%%C" is the diameter sign).</summary>
        public string LabelText
        {
            get
            {
                return "%%C" + Gas.ToString("0.0", CultureInfo.InvariantCulture) + "/"
                    + Liquid.ToString("0.0", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>The readable form: <c>15.9 / 9.5</c>.</summary>
        public override string ToString()
        {
            return Gas.ToString("0.0", CultureInfo.InvariantCulture) + " / "
                + Liquid.ToString("0.0", CultureInfo.InvariantCulture);
        }

        public bool Equals(PipePair other)
        {
            return Math.Abs(Gas - other.Gas) < 0.05 && Math.Abs(Liquid - other.Liquid) < 0.05;
        }

        public override bool Equals(object obj)
        {
            return obj is PipePair && Equals((PipePair)obj);
        }

        public override int GetHashCode()
        {
            return (int)Math.Round(Gas * 10) * 1000 + (int)Math.Round(Liquid * 10);
        }

        public static bool operator ==(PipePair a, PipePair b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(PipePair a, PipePair b)
        {
            return !a.Equals(b);
        }

        /// <summary>Reads <c>15.9-9.5</c>, with or without a bracket after it (a layer size text).</summary>
        public static bool TryParse(string sizeText, out PipePair pair)
        {
            pair = new PipePair();
            if (string.IsNullOrWhiteSpace(sizeText))
                return false;

            string key = sizeText.Trim();
            int space = key.IndexOf(' ');
            if (space > 0)
                key = key.Substring(0, space);
            int dash = key.IndexOf('-');
            double gas;
            double liquid;
            if (dash <= 0
                || !double.TryParse(key.Substring(0, dash), NumberStyles.Float, CultureInfo.InvariantCulture, out gas)
                || !double.TryParse(key.Substring(dash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out liquid))
                return false;

            pair = new PipePair(gas, liquid);
            return true;
        }
    }

    /// <summary>Maps size pairs to the template's RP layer size texts (the bracket is part of the name).</summary>
    public static class RefrigCatalog
    {
        /// <summary>The catalogue size text of a pair, for example <c>15.9-9.5 (14)</c>; null when the template has no such layer.</summary>
        public static string TextFor(PipePair pair)
        {
            foreach (string text in PipeCatalog.RefrigerantSizes)
            {
                PipePair known;
                if (PipePair.TryParse(text, out known) && known == pair)
                    return text;
            }

            return null;
        }

        public static bool Has(PipePair pair)
        {
            return TextFor(pair) != null;
        }

        /// <summary>The layer name of a pair, or null when the template has no such layer.</summary>
        public static string LayerFor(PipePair pair)
        {
            string text = TextFor(pair);
            return text == null ? null : PipeCatalog.LayerName(PipeKind.Refrigerant, text);
        }
    }

    /// <summary>Daikin VRV IV heat pump (RXYQ-T/U) indoor indexes, pipe size tables and refnet kits.</summary>
    public static class VrvTables
    {
        /// <summary>Indoor unit capacity indexes. kW = index x 0.112, rounded to the nominal value.</summary>
        public static readonly int[] UnitIndexes = { 15, 20, 25, 32, 40, 50, 63, 71, 80, 100, 125, 140, 200, 250 };

        // Nominal kW per index, same order. 1.7, 8.0 and 16.0 are unconfirmed.
        private static readonly double[] UnitKw =
        {
            1.7, // 15: unconfirmed
            2.2,
            2.8,
            3.6,
            4.5,
            5.6,
            7.1,
            8.0, // 71: unconfirmed
            9.0,
            11.2,
            14.0,
            16.0, // 140: unconfirmed
            22.4,
            28.0
        };

        /// <summary>Outdoor unit sizes (HP) the dialog offers.</summary>
        public static readonly int[] HpChoices =
        {
            4, 5, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 32, 34, 36, 38, 40, 42, 44, 46, 48, 50, 52, 54
        };

        /// <summary>The longest equivalent pipe length (mm) above which the main pipe should be upsized.</summary>
        public const double UpsizeEquivalentLength = 90000;

        /// <summary>Equivalent length of one refnet (mm).</summary>
        public const double RefnetEquivalentLength = 500;

        /// <summary>The connection ratio (percent) allowed between indoor and outdoor capacity.</summary>
        public const double MinRatio = 50;
        public const double MaxRatio = 130;

        public static bool IsUnitIndex(int index)
        {
            return Array.IndexOf(UnitIndexes, index) >= 0;
        }

        /// <summary>The nominal kW of an indoor index; index x 0.112 for a value outside the list.</summary>
        public static double NominalKw(int index)
        {
            int at = Array.IndexOf(UnitIndexes, index);
            return at >= 0 ? UnitKw[at] : index * 0.112;
        }

        /// <summary>True for the indexes whose kW value is unconfirmed (15, 71, 140).</summary>
        public static bool IsUnitUnconfirmed(int index)
        {
            return index == 15 || index == 71 || index == 140;
        }

        /// <summary>The index whose nominal kW is nearest the value; the lower index on a tie.</summary>
        public static int NearestIndex(double kw)
        {
            int best = UnitIndexes[0];
            double bestDistance = double.MaxValue;
            for (int i = 0; i < UnitIndexes.Length; i++)
            {
                double distance = Math.Abs(UnitKw[i] - kw);
                if (distance < bestDistance - 1e-12)
                {
                    bestDistance = distance;
                    best = UnitIndexes[i];
                }
            }

            return best;
        }

        /// <summary>True when the value is within 6 percent of its nearest nominal kW.</summary>
        public static bool IsNominalKw(double kw)
        {
            double nominal = NominalKw(NearestIndex(kw));
            return Math.Abs(nominal - kw) <= 0.06 * nominal;
        }

        /// <summary>The dialog text of an index: <c>25 · 2.8 kW</c>.</summary>
        public static string IndexText(int index)
        {
            return index.ToString(CultureInfo.InvariantCulture) + " · "
                + NominalKw(index).ToString("0.0", CultureInfo.InvariantCulture) + " kW";
        }

        // ------------------------------------------------------------------ refnet to indoor unit

        /// <summary>
        /// Connection size of one indoor unit: 15-50 gives 12.7/6.4, 63-140 gives 15.9/9.5, 200 gives
        /// 19.1/9.5 and 250 gives 22.2/9.5.
        /// </summary>
        public static PipePair UnitConnection(int index)
        {
            if (index <= 50)
                return new PipePair(12.7, 6.4);
            if (index <= 140)
                return new PipePair(15.9, 9.5);
            if (index <= 200)
                return new PipePair(19.1, 9.5);
            return new PipePair(22.2, 9.5);
        }

        // ------------------------------------------------------------------ refnet to refnet

        /// <summary>Size of a pipe between refnets by the total index of the units downstream.</summary>
        public static PipePair BetweenRefnets(int totalIndex)
        {
            if (totalIndex < 150)
                return new PipePair(15.9, 9.5);
            if (totalIndex < 200)
                return new PipePair(19.1, 9.5);
            if (totalIndex < 290)
                return new PipePair(22.2, 9.5);
            if (totalIndex < 420)
                return new PipePair(28.6, 12.7);
            if (totalIndex < 640)
                return new PipePair(28.6, 15.9);
            if (totalIndex < 920)
                return new PipePair(34.9, 19.1);
            return new PipePair(41.3, 19.1); // 920 and over: the liquid size is unconfirmed
        }

        /// <summary>True when the table cell used for this total is unconfirmed (920 and over: liquid).</summary>
        public static bool IsBetweenUnconfirmed(int totalIndex)
        {
            return totalIndex >= 920;
        }

        // ------------------------------------------------------------------ outdoor unit

        /// <summary>The outdoor unit's capacity index: HP x 25 (unconfirmed above 8 HP, where 8 HP is 200).</summary>
        public static int OduIndex(int hp)
        {
            return hp * 25;
        }

        public static bool IsOduIndexUnconfirmed(int hp)
        {
            return hp > 8;
        }

        public static bool IsHp(int hp)
        {
            return Array.IndexOf(HpChoices, hp) >= 0;
        }

        /// <summary>The smallest HP whose index covers the indoor total; the largest HP when none does.</summary>
        public static int DefaultHp(int sumIndex)
        {
            foreach (int hp in HpChoices)
            {
                if (OduIndex(hp) >= sumIndex)
                    return hp;
            }

            return HpChoices[HpChoices.Length - 1];
        }

        /// <summary>Main pipe (outdoor unit to the first refnet or the only unit) by outdoor unit HP.</summary>
        public static PipePair MainByHp(int hp)
        {
            if (hp <= 5)
                return new PipePair(15.9, 9.5); // 4 HP is not in the table: taken as 5 HP (unconfirmed)
            if (hp == 6)
                return new PipePair(19.1, 9.5); // unconfirmed
            if (hp <= 8)
                return new PipePair(19.1, 9.5);
            if (hp <= 10)
                return new PipePair(22.2, 9.5);
            if (hp <= 16)
                return new PipePair(28.6, 12.7);
            if (hp <= 22)
                return new PipePair(28.6, 15.9);
            if (hp <= 24)
                return new PipePair(34.9, 15.9);
            if (hp <= 34)
                return new PipePair(34.9, 19.1);
            return new PipePair(41.3, 19.1);
        }

        /// <summary>True when the main pipe cell of this HP is unconfirmed (4 HP and 6 HP).</summary>
        public static bool IsMainUnconfirmed(int hp)
        {
            return hp == 4 || hp == 6;
        }

        // ------------------------------------------------------------------ main pipe upsizing

        // The next size up. The starred sizes (25.4, 31.8, 38.1) are not in the template's layer list.
        private static double NextGas(double gas)
        {
            if (Same(gas, 9.5)) return 12.7;
            if (Same(gas, 12.7)) return 15.9;
            if (Same(gas, 15.9)) return 19.1;
            if (Same(gas, 19.1)) return 22.2;
            if (Same(gas, 22.2)) return 25.4; // *
            if (Same(gas, 28.6)) return 31.8; // *
            if (Same(gas, 34.9)) return 38.1; // *
            return 0;
        }

        private static double NextLiquid(double liquid)
        {
            if (Same(liquid, 6.4)) return 9.5;
            if (Same(liquid, 9.5)) return 12.7;
            if (Same(liquid, 12.7)) return 15.9;
            if (Same(liquid, 15.9)) return 19.1;
            if (Same(liquid, 19.1)) return 22.2;
            return 0;
        }

        private static bool Same(double a, double b)
        {
            return Math.Abs(a - b) < 0.05;
        }

        /// <summary>
        /// Upsizes the main pipe one step on the gas and the liquid side (equivalent length of 90 m or
        /// more). The gas side is left alone for 18-22 HP, where the sources conflict. A size the
        /// template has no layer for is not used: the pair with only the gas or only the liquid upsized
        /// is tried, then the standard size, and <paramref name="note"/> says what was done.
        /// </summary>
        public static PipePair UpsizeMain(
            PipePair standard, int hp, Func<PipePair, bool> available, out string note)
        {
            note = null;
            if (available == null)
                available = RefrigCatalog.Has;

            double gasUp = hp >= 18 && hp <= 22 ? 0 : NextGas(standard.Gas);
            double liquidUp = NextLiquid(standard.Liquid);
            PipePair wanted = new PipePair(gasUp > 0 ? gasUp : standard.Gas, liquidUp > 0 ? liquidUp : standard.Liquid);
            if (wanted == standard)
            {
                note = "The main pipe " + standard + " has no larger size to step up to; kept.";
                return standard;
            }

            List<PipePair> candidates = new List<PipePair> { wanted };
            if (gasUp > 0 && liquidUp > 0)
            {
                candidates.Add(new PipePair(standard.Gas, liquidUp));
                candidates.Add(new PipePair(gasUp, standard.Liquid));
            }

            foreach (PipePair candidate in candidates)
            {
                if (!available(candidate))
                    continue;
                if (candidate != wanted)
                {
                    note = "Upsized main pipe " + wanted + " has no layer in the template; only "
                        + (candidate.Gas != standard.Gas ? "the gas" : "the liquid") + " pipe was upsized: "
                        + candidate + ".";
                }

                return candidate;
            }

            note = "Upsized main pipe " + wanted + " has no layer in the template; kept the standard size "
                + standard + ".";
            return standard;
        }

        // ------------------------------------------------------------------ refnet kits (information only)

        /// <summary>The first refnet joint after the outdoor unit, by the outdoor unit's HP.</summary>
        public static string FirstKit(int hp)
        {
            if (hp <= 6)
                return "KHRQ22M20T";
            if (hp <= 10)
                return "KHRQ22M29T9";
            if (hp <= 22)
                return "KHRQ22M64T";
            return "KHRQ22M75T";
        }

        /// <summary>A later refnet joint, by the total index of the units downstream.</summary>
        public static string LaterKit(int totalIndex)
        {
            if (totalIndex < 200)
                return "KHRQ22M20T";
            if (totalIndex < 290)
                return "KHRQ22M29T9";
            if (totalIndex < 640)
                return "KHRQ22M64T";
            return "KHRQ22M75T";
        }
    }

    /// <summary>Reads a capacity written as text: <c>FCU-CAS-2.8 kW</c>, <c>2.8kW</c>, <c>IU-5,6 kW</c>.</summary>
    public static class CapacityText
    {
        private static readonly Regex Kw = new Regex(
            @"(?<![\d.,])(\d+(?:[.,]\d+)?)\s*kW(?![A-Za-z])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The first "number kW" in the text.</summary>
        public static bool TryParseKw(string text, out double kw)
        {
            kw = 0;
            if (string.IsNullOrEmpty(text))
                return false;

            Match match = Kw.Match(text);
            if (!match.Success)
                return false;

            string number = match.Groups[1].Value.Replace(',', '.');
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out kw))
                return false;
            return kw > 0 && !double.IsInfinity(kw);
        }
    }

    // ------------------------------------------------------------------------------------------------

    public sealed class SizingRequest
    {
        public NetworkTopology Topology;
        /// <summary>Capacity index of each indoor unit (an index into <see cref="NetworkTopology.Ends"/>); 0 or less means none yet.</summary>
        public IList<int> UnitIndexes = new List<int>();
        /// <summary>The outdoor unit HP; 0 takes the smallest HP that covers the indoor total.</summary>
        public int Hp;
        public bool UpsizeMain;
        /// <summary>Sizes the user set by hand: segment index to catalogue size text. They are kept.</summary>
        public IDictionary<int, string> Overrides = new Dictionary<int, string>();
        /// <summary>True when the template has a layer for the pair; null means the catalogue.</summary>
        public Func<PipePair, bool> LayerAvailable;
    }

    public sealed class SegmentSizing
    {
        public int Index;
        public SegmentKind Kind;
        /// <summary>Sum of the capacity indexes of the units downstream.</summary>
        public int TotalIndex;
        public int UnitCount;
        public int UnitsWithoutCapacity;
        public bool HasAuto;
        public PipePair Auto;
        /// <summary>The catalogue size text of <see cref="Auto"/>, or null.</summary>
        public string AutoText;
        /// <summary>The size to use: the manual one when set, otherwise <see cref="AutoText"/>; null when there is none.</summary>
        public string ChosenText;
        public PipePair Chosen;
        public bool HasChosen;
        public bool Manual;
        public string Warning;
    }

    public sealed class RefnetKit
    {
        public int Refnet;
        public int TotalIndex;
        public bool First;
        public string Kit;
    }

    public sealed class SizingResult
    {
        public int Hp;
        public bool HpIsDefault;
        public int SumIndex;
        public int OduIndex;
        /// <summary>Indoor total over the outdoor index, in percent.</summary>
        public double Ratio;
        public PipePair StandardMain;
        public readonly List<SegmentSizing> Segments = new List<SegmentSizing>();
        public readonly List<RefnetKit> Kits = new List<RefnetKit>();
        /// <summary>Things to check: a rule or an unusable size.</summary>
        public readonly List<string> Warnings = new List<string>();
        /// <summary>Things to know: unconfirmed table cells and upsizing.</summary>
        public readonly List<string> Notes = new List<string>();
        public int UnitsWithoutCapacity;
        /// <summary>False while a unit has no capacity or a segment has no size.</summary>
        public bool CanApply;
        /// <summary>Pipe length (mm) from the root to the farthest unit by equivalent length, and that equivalent length (pipe plus 0.5 m per refnet).</summary>
        public double LongestPipe;
        public double LongestEquivalent;
        public int LongestUnit = -1;
    }

    public static class RefrigSizer
    {
        public static SizingResult Compute(SizingRequest request)
        {
            SizingResult result = new SizingResult();
            NetworkTopology topology = request.Topology;
            Func<PipePair, bool> available = request.LayerAvailable ?? RefrigCatalog.Has;

            // Units.
            int endCount = topology.Ends.Count;
            int[] index = new int[endCount];
            int sum = 0;
            int missing = 0;
            for (int i = 0; i < endCount; i++)
            {
                int value = i < request.UnitIndexes.Count ? request.UnitIndexes[i] : 0;
                index[i] = value > 0 ? value : 0;
                if (index[i] == 0)
                    missing++;
                sum += index[i];
            }

            result.SumIndex = sum;
            result.UnitsWithoutCapacity = missing;

            // Outdoor unit.
            result.HpIsDefault = !VrvTables.IsHp(request.Hp);
            result.Hp = result.HpIsDefault ? VrvTables.DefaultHp(sum) : request.Hp;
            result.OduIndex = VrvTables.OduIndex(result.Hp);
            result.Ratio = result.OduIndex > 0 ? 100.0 * sum / result.OduIndex : 0;
            result.StandardMain = VrvTables.MainByHp(result.Hp);

            PipePair mainPair = result.StandardMain;
            if (request.UpsizeMain)
            {
                string note;
                mainPair = VrvTables.UpsizeMain(result.StandardMain, result.Hp, available, out note);
                if (note != null)
                    result.Warnings.Add(note);
            }

            // Segments.
            foreach (TopologySegment segment in topology.Segments)
            {
                SegmentSizing row = new SegmentSizing
                {
                    Index = segment.Index,
                    Kind = segment.Kind,
                    UnitCount = segment.DownstreamEnds.Count
                };
                foreach (int end in segment.DownstreamEnds)
                {
                    row.TotalIndex += index[end];
                    if (index[end] == 0)
                        row.UnitsWithoutCapacity++;
                }

                switch (segment.Kind)
                {
                    case SegmentKind.Main:
                        row.HasAuto = true;
                        row.Auto = mainPair;
                        break;
                    case SegmentKind.BetweenRefnets:
                        if (row.TotalIndex > 0)
                        {
                            PipePair table = VrvTables.BetweenRefnets(row.TotalIndex);
                            // Never larger than the main pipe, gas and liquid each.
                            row.Auto = new PipePair(
                                Math.Min(table.Gas, result.StandardMain.Gas),
                                Math.Min(table.Liquid, result.StandardMain.Liquid));
                            row.HasAuto = true;
                        }

                        break;
                    default:
                        if (row.TotalIndex > 0)
                        {
                            row.Auto = VrvTables.UnitConnection(row.TotalIndex);
                            row.HasAuto = true;
                        }

                        break;
                }

                if (row.HasAuto)
                    row.AutoText = RefrigCatalog.TextFor(row.Auto);
                if (row.HasAuto && row.AutoText == null)
                    row.HasAuto = false;

                string manual;
                PipePair manualPair;
                if (request.Overrides != null && request.Overrides.TryGetValue(segment.Index, out manual)
                    && PipeCatalog.IsKnownSize(PipeKind.Refrigerant, manual)
                    && PipePair.TryParse(manual, out manualPair))
                {
                    row.ChosenText = RefrigCatalog.TextFor(manualPair);
                    row.Chosen = manualPair;
                    row.HasChosen = true;
                    row.Manual = row.ChosenText != row.AutoText;
                }
                else if (row.HasAuto)
                {
                    row.ChosenText = row.AutoText;
                    row.Chosen = row.Auto;
                    row.HasChosen = true;
                }

                result.Segments.Add(row);
            }

            // Rules and warnings.
            SegmentSizing mainRow = result.Segments.Find(s => s.Kind == SegmentKind.Main && s.HasChosen);
            foreach (SegmentSizing row in result.Segments)
            {
                List<string> parts = new List<string>();
                if (row.UnitsWithoutCapacity > 0)
                    parts.Add(row.UnitsWithoutCapacity + " unit(s) without capacity");

                if (!row.HasChosen && row.UnitsWithoutCapacity == 0)
                    parts.Add("no size");

                if (row.Kind == SegmentKind.BetweenRefnets && row.HasChosen && mainRow != null
                    && (row.Chosen.Gas > mainRow.Chosen.Gas + 0.05 || row.Chosen.Liquid > mainRow.Chosen.Liquid + 0.05))
                    parts.Add("larger than the main pipe (" + mainRow.Chosen + ")");

                if (row.Kind == SegmentKind.ToUnit && row.HasChosen && row.HasAuto && row.Chosen != row.Auto)
                    parts.Add("differs from the unit's connection size (" + row.Auto + ")");

                row.Warning = parts.Count == 0 ? null : string.Join("; ", parts);
            }

            if (missing > 0)
                result.Warnings.Add(missing + " indoor unit(s) have no capacity yet.");

            if (sum > 0 && (result.Ratio < VrvTables.MinRatio - 1e-9 || result.Ratio > VrvTables.MaxRatio + 1e-9))
            {
                result.Warnings.Add(
                    "Connection ratio " + result.Ratio.ToString("0", CultureInfo.InvariantCulture)
                    + " % is outside " + VrvTables.MinRatio.ToString("0", CultureInfo.InvariantCulture) + "-"
                    + VrvTables.MaxRatio.ToString("0", CultureInfo.InvariantCulture) + " % ("
                    + sum.ToString(CultureInfo.InvariantCulture) + " of "
                    + result.OduIndex.ToString(CultureInfo.InvariantCulture) + " at "
                    + result.Hp.ToString(CultureInfo.InvariantCulture) + " HP).");
            }

            if (result.HpIsDefault && sum > VrvTables.OduIndex(result.Hp))
                result.Warnings.Add("The indoor total is above the largest outdoor unit (" + result.Hp + " HP).");

            foreach (SegmentSizing row in result.Segments)
            {
                if (row.Warning != null && row.UnitsWithoutCapacity == 0)
                    result.Warnings.Add("S" + (row.Index + 1) + ": " + row.Warning + ".");
            }

            AddNotes(result, index, topology);
            AddKits(result, topology);
            AddLongest(result, topology);

            bool allSized = result.Segments.Count > 0;
            foreach (SegmentSizing row in result.Segments)
            {
                if (!row.HasChosen)
                    allSized = false;
            }

            result.CanApply = missing == 0 && allSized && endCount > 0;
            return result;
        }

        private static void AddNotes(SizingResult result, int[] index, NetworkTopology topology)
        {
            HashSet<int> seen = new HashSet<int>();
            foreach (int value in index)
            {
                if (value > 0 && VrvTables.IsUnitUnconfirmed(value) && seen.Add(value))
                {
                    result.Notes.Add(
                        "Indoor index " + value + " (" + VrvTables.NominalKw(value).ToString("0.0", CultureInfo.InvariantCulture)
                        + " kW) is unconfirmed in the tables.");
                }
            }

            if (VrvTables.IsMainUnconfirmed(result.Hp))
                result.Notes.Add("The main pipe size of " + result.Hp + " HP is unconfirmed in the tables.");
            if (VrvTables.IsOduIndexUnconfirmed(result.Hp))
                result.Notes.Add("The outdoor index of " + result.Hp + " HP (HP x 25) is unconfirmed above 8 HP.");

            foreach (SegmentSizing row in result.Segments)
            {
                if (row.Kind == SegmentKind.BetweenRefnets && VrvTables.IsBetweenUnconfirmed(row.TotalIndex))
                {
                    result.Notes.Add("S" + (row.Index + 1) + ": the liquid size above index 920 is unconfirmed in the tables.");
                    break;
                }
            }
        }

        private static void AddKits(SizingResult result, NetworkTopology topology)
        {
            foreach (TopologyRefnet refnet in topology.Refnets)
            {
                if (refnet.UpSegment < 0 || refnet.UpSegment >= result.Segments.Count)
                    continue;
                SegmentSizing up = result.Segments[refnet.UpSegment];
                bool first = up.Kind == SegmentKind.Main;
                result.Kits.Add(new RefnetKit
                {
                    Refnet = refnet.Index,
                    TotalIndex = up.TotalIndex,
                    First = first,
                    Kit = first ? VrvTables.FirstKit(result.Hp) : VrvTables.LaterKit(up.TotalIndex)
                });
            }
        }

        private static void AddLongest(SizingResult result, NetworkTopology topology)
        {
            foreach (TopologyEnd end in topology.Ends)
            {
                double equivalent = end.PipeLength + VrvTables.RefnetEquivalentLength * end.Refnets;
                if (equivalent > result.LongestEquivalent)
                {
                    result.LongestEquivalent = equivalent;
                    result.LongestPipe = end.PipeLength;
                    result.LongestUnit = end.Index;
                }
            }
        }
    }

    // ------------------------------------------------------------------------------------------------

    public sealed class SizeLabelPlacement
    {
        public Vec2 Position;
        /// <summary>Text rotation in radians, in (-pi/2, pi/2], so the text reads from the left or from below.</summary>
        public double Rotation;
        public string Text;
    }

    public static class SizeLabels
    {
        /// <summary>
        /// Places a size label: the midpoint of the longest straight piece of the polyline, offset to
        /// one side (the left of the reading direction), rotated along the piece and kept readable.
        /// </summary>
        public static bool TryPlace(IList<Vec2> points, double offset, string text, out SizeLabelPlacement placement)
        {
            placement = null;
            if (points == null || points.Count < 2)
                return false;

            int best = -1;
            double bestLength = 1e-9;
            for (int i = 0; i + 1 < points.Count; i++)
            {
                double length = points[i].DistanceTo(points[i + 1]);
                if (length > bestLength + 1e-9)
                {
                    bestLength = length;
                    best = i;
                }
            }

            if (best < 0)
                return false;

            Vec2 a = points[best];
            Vec2 b = points[best + 1];
            double angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
            const double edge = 1e-9;
            if (angle > Math.PI / 2 + edge)
                angle -= Math.PI;
            else if (angle <= -Math.PI / 2 + edge)
                angle += Math.PI;

            Vec2 middle = (a + b) / 2;
            Vec2 reading = Vec2.FromAngle(angle);
            placement = new SizeLabelPlacement
            {
                Position = middle + reading.Left * offset,
                Rotation = angle,
                Text = text
            };
            return true;
        }
    }
}
