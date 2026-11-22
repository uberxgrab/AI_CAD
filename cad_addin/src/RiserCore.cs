using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HvacAiCadAddin.Pipes;

namespace HvacAiCadAddin.Vrv
{
    // AutoCAD-free core of HVACRISER: the cumulative indoor index along a refrigerant riser through a
    // shaft, the pipe size of each riser segment and the refnet at each floor junction. The sizes come
    // from VrvTables (RefrigSizingCore.cs, the HVACREFRIG tables): a segment between floors is sized
    // like a pipe between refnets (by the index downstream, never larger than the main pipe); the
    // segment that leaves the nearest floor toward the outdoor unit is the main pipe (by the outdoor
    // HP, optionally up-sized when the equivalent length reaches 90 m); the joint is the first kit
    // after the outdoor unit or a later kit by the index downstream. Plain doubles only.

    /// <summary>One floor as the user entered it, in the order of entry (farthest from the outdoor unit first).</summary>
    public sealed class RiserFloorInput
    {
        public string Name;
        public int UnitCount;
        /// <summary>Sum of the Daikin indexes of the floor's indoor units.</summary>
        public int FloorIndex;
        public double X;
        public double Y;
    }

    public sealed class RiserFloor
    {
        public string Name;
        /// <summary>0 for the floor farthest from the outdoor unit.</summary>
        public int Order;
        public int UnitCount;
        public int FloorIndex;
        /// <summary>Sum of the indexes of this floor and every floor before it (farther from the outdoor unit).</summary>
        public int Cumulative;
        /// <summary>The riser pipe leaving this floor toward the outdoor unit.</summary>
        public bool HasPair;
        public PipePair Pair;
        /// <summary>True for the segment that is the main pipe (leaves the nearest floor).</summary>
        public bool IsMain;
        /// <summary>The refnet kit where this floor's network joins the riser; null on the first floor.</summary>
        public string Kit;
        public double X;
        public double Y;

        /// <summary>The label size text <c>%%C28.6/12.7</c>, or <c>-</c> when there is none.</summary>
        public string PairLabel { get { return HasPair ? Pair.LabelText : "-"; } }

        /// <summary>The readable size <c>28.6 / 12.7</c>, or <c>-</c>.</summary>
        public string PairText { get { return HasPair ? Pair.ToString() : "-"; } }
    }

    public sealed class RiserPlan
    {
        public string Name;
        public int Hp;
        public bool HpIsDefault;
        public int OduIndex;
        public int TotalIndex;
        public double Ratio;
        public PipePair StandardMain;
        public PipePair MainPair;
        public double EquivalentLength;
        public bool UpsizeSuggested;
        public readonly List<RiserFloor> Floors = new List<RiserFloor>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Notes = new List<string>();

        /// <summary>The text beside a floor's riser marker: <c>Riser R1 · Floor 2 · %%C28.6/12.7 · Σ300</c>.</summary>
        public string MarkerText(RiserFloor floor)
        {
            return "Riser " + Name + " · " + floor.Name + " · " + floor.PairLabel + " · Σ"
                + floor.Cumulative.ToString(CultureInfo.InvariantCulture);
        }

        public static readonly string[] ScheduleHeader =
        {
            "Floor", "Units", "Floor index", "Cumulative index", "Riser gas/liquid", "Refnet"
        };

        /// <summary>The schedule rows, in the order of entry. The riser size cell is the plain text (no %%C).</summary>
        public List<string[]> ScheduleRows()
        {
            List<string[]> rows = new List<string[]>();
            foreach (RiserFloor floor in Floors)
            {
                rows.Add(new[]
                {
                    floor.Name,
                    floor.UnitCount.ToString(CultureInfo.InvariantCulture),
                    floor.FloorIndex.ToString(CultureInfo.InvariantCulture),
                    floor.Cumulative.ToString(CultureInfo.InvariantCulture),
                    floor.PairText,
                    floor.Kit ?? "-"
                });
            }

            return rows;
        }

        /// <summary>The title row of the schedule.</summary>
        public string ScheduleTitle
        {
            get
            {
                return "Riser " + Name + " schedule - ODU " + Hp.ToString(CultureInfo.InvariantCulture) + " HP, Σ index "
                    + TotalIndex.ToString(CultureInfo.InvariantCulture) + " / "
                    + OduIndex.ToString(CultureInfo.InvariantCulture) + " ("
                    + Ratio.ToString("0", CultureInfo.InvariantCulture) + " %)";
            }
        }
    }

    public static class RiserPlanner
    {
        public static string DefaultFloorName(int order)
        {
            return "Floor " + (order + 1).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// floors: in the order of entry, farthest from the outdoor unit first. hp: the outdoor unit HP,
        /// 0 for the smallest HP that covers the total. floorHeight: used only to estimate the equivalent
        /// length of the riser (floors x height, plus 0.5 m per refnet). upsizeMain: apply the 90 m rule.
        /// </summary>
        public static RiserPlan Plan(
            string name, IList<RiserFloorInput> floors, int hp, double floorHeight, bool upsizeMain)
        {
            RiserPlan plan = new RiserPlan { Name = string.IsNullOrWhiteSpace(name) ? "R1" : name.Trim() };

            int total = 0;
            foreach (RiserFloorInput input in floors)
                total += Math.Max(0, input.FloorIndex);
            plan.TotalIndex = total;

            plan.HpIsDefault = !VrvTables.IsHp(hp);
            plan.Hp = plan.HpIsDefault ? VrvTables.DefaultHp(total) : hp;
            plan.OduIndex = VrvTables.OduIndex(plan.Hp);
            plan.Ratio = plan.OduIndex > 0 ? 100.0 * total / plan.OduIndex : 0;
            plan.StandardMain = VrvTables.MainByHp(plan.Hp);
            plan.MainPair = plan.StandardMain;

            plan.EquivalentLength = floors.Count * Math.Max(0, floorHeight)
                + VrvTables.RefnetEquivalentLength * Math.Max(0, floors.Count - 1);
            plan.UpsizeSuggested = plan.EquivalentLength >= VrvTables.UpsizeEquivalentLength;
            if (plan.UpsizeSuggested)
            {
                plan.Warnings.Add("The estimated equivalent length of the riser is "
                    + (plan.EquivalentLength / 1000).ToString("0", CultureInfo.InvariantCulture)
                    + " m (90 m or more): the main pipe from the outdoor unit should be up-sized one step"
                    + (upsizeMain ? " (applied)." : " (not applied)."));
            }

            if (upsizeMain)
            {
                string note;
                plan.MainPair = VrvTables.UpsizeMain(plan.StandardMain, plan.Hp, null, out note);
                if (note != null)
                    plan.Warnings.Add(note);
            }

            if (total > 0 && (plan.Ratio < VrvTables.MinRatio - 1e-9 || plan.Ratio > VrvTables.MaxRatio + 1e-9))
            {
                plan.Warnings.Add("Connection ratio " + plan.Ratio.ToString("0", CultureInfo.InvariantCulture)
                    + " % is outside " + VrvTables.MinRatio.ToString("0", CultureInfo.InvariantCulture) + "-"
                    + VrvTables.MaxRatio.ToString("0", CultureInfo.InvariantCulture) + " % ("
                    + total.ToString(CultureInfo.InvariantCulture) + " of "
                    + plan.OduIndex.ToString(CultureInfo.InvariantCulture) + " at "
                    + plan.Hp.ToString(CultureInfo.InvariantCulture) + " HP).");
            }

            if (plan.HpIsDefault && total > plan.OduIndex)
                plan.Warnings.Add("The indoor total is above the largest outdoor unit (" + plan.Hp + " HP).");
            if (VrvTables.IsOduIndexUnconfirmed(plan.Hp))
                plan.Notes.Add("The outdoor index of " + plan.Hp.ToString(CultureInfo.InvariantCulture)
                    + " HP (HP x 25) is unconfirmed above 8 HP.");
            if (VrvTables.IsMainUnconfirmed(plan.Hp))
                plan.Notes.Add("The main pipe size of " + plan.Hp.ToString(CultureInfo.InvariantCulture)
                    + " HP is unconfirmed in the tables.");
            if (VrvTables.IsBetweenUnconfirmed(total))
                plan.Notes.Add("The liquid size above index 920 is unconfirmed in the tables.");

            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int cumulative = 0;
            for (int i = 0; i < floors.Count; i++)
            {
                RiserFloorInput input = floors[i];
                cumulative += Math.Max(0, input.FloorIndex);
                bool isMain = i == floors.Count - 1;
                RiserFloor floor = new RiserFloor
                {
                    Name = string.IsNullOrWhiteSpace(input.Name) ? DefaultFloorName(i) : input.Name.Trim(),
                    Order = i,
                    UnitCount = input.UnitCount,
                    FloorIndex = Math.Max(0, input.FloorIndex),
                    Cumulative = cumulative,
                    IsMain = isMain,
                    X = input.X,
                    Y = input.Y
                };

                if (!names.Add(floor.Name))
                    plan.Warnings.Add("The floor name \"" + floor.Name + "\" is used twice.");
                if (input.FloorIndex <= 0)
                    plan.Warnings.Add(floor.Name + ": no indoor unit capacity.");

                if (isMain)
                {
                    floor.HasPair = true;
                    floor.Pair = plan.MainPair;
                }
                else if (cumulative > 0)
                {
                    PipePair table = VrvTables.BetweenRefnets(cumulative);
                    // Never larger than the main pipe, gas and liquid each (as HVACREFRIG does).
                    floor.Pair = new PipePair(
                        Math.Min(table.Gas, plan.StandardMain.Gas),
                        Math.Min(table.Liquid, plan.StandardMain.Liquid));
                    floor.HasPair = true;
                }

                if (i > 0 && cumulative > 0)
                    floor.Kit = isMain ? VrvTables.FirstKit(plan.Hp) : VrvTables.LaterKit(cumulative);

                if (floor.HasPair && RefrigCatalog.TextFor(floor.Pair) == null)
                    plan.Notes.Add(floor.Name + ": " + floor.Pair + " has no layer in the template.");

                plan.Floors.Add(floor);
            }

            return plan;
        }
    }

    /// <summary>HVACRISER values remembered between runs.</summary>
    public sealed class RiserSettings
    {
        /// <summary>The layer of the riser markers, texts and schedule.</summary>
        public const string Layer = "HVAC-RISER";

        public const short LayerColor = 6;

        /// <summary>Extended-data application of the riser markers and schedule.</summary>
        public const string RiserApp = "HVACAI_RISER";

        public double TextHeight = 150;
        public double MarkerRadius = 300;
        /// <summary>Floor-to-floor height (mm), used to estimate the riser's equivalent length.</summary>
        public double FloorHeight = 3600;
        public string LastName = "R1";

        public bool IsValid(out string error)
        {
            error = null;
            if (double.IsNaN(TextHeight) || TextHeight < 20 || TextHeight > 2000)
                error = "The text height must be between 20 and 2000 mm.";
            else if (double.IsNaN(MarkerRadius) || MarkerRadius < 20 || MarkerRadius > 5000)
                error = "The marker radius must be between 20 and 5000 mm.";
            else if (double.IsNaN(FloorHeight) || FloorHeight < 0 || FloorHeight > 20000)
                error = "The floor height must be between 0 and 20000 mm.";
            return error == null;
        }

        public string Serialize()
        {
            StringBuilder text = new StringBuilder();
            text.Append("textheight=").Append(TextHeight.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            text.Append("radius=").Append(MarkerRadius.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            text.Append("floorheight=").Append(FloorHeight.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            text.Append("name=").Append((LastName ?? "").Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
            return text.ToString();
        }

        /// <summary>Never throws: unknown keys and bad values keep the defaults.</summary>
        public static RiserSettings Deserialize(string text)
        {
            RiserSettings settings = new RiserSettings();
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
                bool ok = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                    && !double.IsNaN(number) && !double.IsInfinity(number);
                switch (key)
                {
                    case "textheight":
                        if (ok && number >= 20 && number <= 2000)
                            settings.TextHeight = number;
                        break;
                    case "radius":
                        if (ok && number >= 20 && number <= 5000)
                            settings.MarkerRadius = number;
                        break;
                    case "floorheight":
                        if (ok && number >= 0 && number <= 20000)
                            settings.FloorHeight = number;
                        break;
                    case "name":
                        if (value.Length > 0 && value.Length <= 40)
                            settings.LastName = value;
                        break;
                }
            }

            return settings;
        }
    }
}
