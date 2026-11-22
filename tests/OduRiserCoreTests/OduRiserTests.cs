using System;
using System.Collections.Generic;
using System.Globalization;
using HvacAiCadAddin.Pipes;
using HvacAiCadAddin.Vrv;
using PipeCoreTests;

namespace OduRiserCoreTests
{
    // Hand-computed expectations come from the Daikin tables in RefrigSizingCore.cs (VrvTables):
    // ODU index = HP x 25, ratio 50-130 %, between-refnet size by downstream index
    // (<150 15.9/9.5, <200 19.1/9.5, <290 22.2/9.5, <420 28.6/12.7), main pipe by HP
    // (<=16 HP 28.6/12.7), kits (first: <=6 KHRQ22M20T, <=10 M29T9, <=22 M64T; later: <200 M20T, <290 M29T9, <640 M64T).
    internal static class OduRiserTests
    {
        public static void Run()
        {
            Capacity();
            Planning();
            Blocks();
            Footprint();
            Split();
            Settings();
            Riser();
            RiserSettingsRoundTrip();
        }

        private static OduIndoorUnit U(int index)
        {
            return new OduIndoorUnit { Index = index, Kw = index * 0.112, Nominal = true, BlockName = "U" + index };
        }

        private static void Capacity()
        {
            Check.Case("capacity", () =>
            {
                OduIndoorUnit unit;
                Check.True("name 2.8", IndoorCapacity.TryFromName("FCU-CAS-2.8 kW", out unit));
                Check.Equal("index 25", 25, unit.Index);
                Check.Near("kw 2.8", 2.8, unit.Kw);
                Check.True("name 14", IndoorCapacity.TryFromName("FCU-CAS-14 kW", out unit));
                Check.Equal("index 125", 125, unit.Index);
                Check.True("name 16", IndoorCapacity.TryFromName("FCU-CAS-16 kW", out unit));
                Check.Equal("index 140", 140, unit.Index);
                Check.True("comma", IndoorCapacity.TryFromName("IU-5,6 kW", out unit));
                Check.Equal("index 50", 50, unit.Index);
                Check.False("odu has no kW", IndoorCapacity.TryFromName("ODU-12 HP", out unit));
                Check.False("btuh not kw", IndoorCapacity.TryFromName("TRANE_ODU_240000 BTUH", out unit));
                Check.False("empty", IndoorCapacity.TryFromName("", out unit));
                Check.False("null", IndoorCapacity.TryFromName(null, out unit));

                Check.True("label", IndoorCapacity.TryFromLabel("FCU-1", "Cooling 4.5kW 220V", out unit));
                Check.Equal("label index", 40, unit.Index);
                Check.Equal("label name", "FCU-1", unit.BlockName);

                object[] tag = { "HVACAI_PLACED", "R12", 63, 7.1, (short)1, (short)2 };
                Check.True("placed", IndoorCapacity.TryFromPlacedTag(tag, "FCU-CAS-7.1 kW", out unit));
                Check.Equal("placed index", 63, unit.Index);
                Check.Near("placed kw", 7.1, unit.Kw);
                Check.Equal("placed source", "placed", unit.Source);
                Check.False("placed short", IndoorCapacity.TryFromPlacedTag(new object[] { "A", "R" }, "x", out unit));
                Check.False("placed zero", IndoorCapacity.TryFromPlacedTag(new object[] { "A", "R", 0, 1.0 }, "x", out unit));
            });
        }

        private static void Planning()
        {
            Check.Case("plan default HP", () =>
            {
                OduPlan plan = OduPlanner.Plan(4, 250, 0);
                Check.Equal("hp 10", 10, plan.Hp);
                Check.True("default flag", plan.HpIsDefault);
                Check.Equal("index 250", 250, plan.OduIndex);
                Check.Near("ratio 100", 100, plan.Ratio);
                Check.False("no ratio warning", plan.RatioOutside);
                Check.Equal("block", "ODU-10 HP", plan.BlockName);
                Check.Equal("no warnings", 0, plan.Warnings.Count);
                Check.Equal("note for 10 HP", 1, plan.Notes.Count);

                plan = OduPlanner.Plan(5, 251, 0);
                Check.Equal("251 -> 12 HP", 12, plan.Hp);
                Check.Near("ratio 83.67", 100.0 * 251 / 300, plan.Ratio, 1e-9);

                plan = OduPlanner.Plan(2, 100, 0);
                Check.Equal("100 -> 4 HP", 4, plan.Hp);
                Check.Equal("4 HP block", "ODU-4 HP", plan.BlockName);

                plan = OduPlanner.Plan(1, 40, 0);
                Check.Equal("40 -> 4 HP", 4, plan.Hp);
                Check.Near("ratio 40", 40, plan.Ratio);
                Check.True("under 50 warns", plan.RatioOutside);
                Check.Equal("one warning", 1, plan.Warnings.Count);
                Check.Contains("warning text", plan.Warnings[0], "outside 50-130 %");
            });

            Check.Case("plan chosen HP", () =>
            {
                OduPlan plan = OduPlanner.Plan(4, 250, 8);
                Check.Equal("hp 8", 8, plan.Hp);
                Check.False("not default", plan.HpIsDefault);
                Check.Near("125 %", 125, plan.Ratio);
                Check.False("125 is inside", plan.RatioOutside);

                plan = OduPlanner.Plan(4, 250, 6);
                Check.Near("166.7 %", 100.0 * 250 / 150, plan.Ratio, 1e-9);
                Check.True("over 130", plan.RatioOutside);
                Check.Equal("two warnings", 2, plan.Warnings.Count);
                Check.Contains("smaller", plan.Warnings[1], "smaller than the smallest HP");

                // 130 % exactly is allowed; 7 HP is not in the table so the default is used.
                plan = OduPlanner.Plan(1, 260, 8);
                Check.Near("130 %", 130, plan.Ratio);
                Check.False("130 inside", plan.RatioOutside);
                plan = OduPlanner.Plan(1, 100, 7);
                Check.True("7 HP falls back to default", plan.HpIsDefault);
                Check.Equal("default 4", 4, plan.Hp);
            });

            Check.Case("plan too large", () =>
            {
                OduPlan plan = OduPlanner.Plan(30, 1350, 0);
                Check.Equal("54 HP", 54, plan.Hp);
                Check.False("1350 fits", plan.OverLargest);
                plan = OduPlanner.Plan(30, 1351, 0);
                Check.Equal("still 54 HP", 54, plan.Hp);
                Check.True("over", plan.OverLargest);
                Check.Contains("split", plan.Warnings[0], "split the units into several systems");
                plan = OduPlanner.Plan(0, 0, 0);
                Check.Contains("no units", plan.Warnings[0], "No indoor unit");
            });

            Check.Case("unit notes", () =>
            {
                OduPlan plan = OduPlanner.Plan(2, 86, 0);
                OduPlanner.AddUnitNotes(plan, new[] { U(15), U(71), U(71) });
                int unconfirmed = 0;
                foreach (string note in plan.Notes)
                {
                    if (note.Contains("unconfirmed in the tables"))
                        unconfirmed++;
                }

                Check.Equal("15 and 71 once each", 2, unconfirmed);
                Check.Equal("sum", 86, OduPlanner.Sum(new[] { U(15), U(71) }));
            });

            Check.Case("label", () =>
            {
                OduPlan plan = OduPlanner.Plan(5, 251, 0);
                Check.Equal("label", "ODU-12 HP\\PΣ index 251 / 300\\P84 %", OduPlanner.LabelText(plan, ""));
                Check.Equal("label with model", "ODU-12 HP\\PRXYQ12T\\PΣ index 251 / 300\\P84 %",
                    OduPlanner.LabelText(plan, " RXYQ12T "));
            });
        }

        private static Func<string, bool> Has(params int[] hps)
        {
            HashSet<string> names = new HashSet<string>();
            foreach (int hp in hps)
                names.Add(OduPlanner.BlockNameFor(hp));
            return names.Contains;
        }

        private static void Blocks()
        {
            Check.Case("block resolution", () =>
            {
                Func<string, bool> template = Has(6, 8, 10, 12, 14, 16, 18, 20, 22, 24);
                OduBlockChoice choice = OduPlanner.ResolveBlock(12, template);
                Check.Equal("exact", "ODU-12 HP", choice.BlockName);
                Check.False("exact is not a substitute", choice.Substitute);
                Check.Equal("exact warning", null, choice.Warning);

                choice = OduPlanner.ResolveBlock(4, template);
                Check.Equal("4 -> 6", "ODU-6 HP", choice.BlockName);
                Check.True("substitute", choice.Substitute);
                Check.False("not drawn", choice.Fallback);
                Check.Contains("warns", choice.Warning, "ODU-4 HP is not in the template");

                choice = OduPlanner.ResolveBlock(5, template);
                Check.Equal("5 -> 6", "ODU-6 HP", choice.BlockName);
                choice = OduPlanner.ResolveBlock(26, template);
                Check.Equal("26 -> 24", "ODU-24 HP", choice.BlockName);
                choice = OduPlanner.ResolveBlock(54, template);
                Check.Equal("54 -> 24", "ODU-24 HP", choice.BlockName);

                // Tie: the larger neighbour (7 is 1 from 6 and 1 from 8 in a table without 7).
                choice = OduPlanner.ResolveBlock(7, Has(6, 8));
                Check.Equal("tie takes larger", "ODU-8 HP", choice.BlockName);

                int parsed;
                Check.True("parse hp", OduPlanner.TryParseHp("ODU-12 HP", out parsed));
                Check.Equal("parsed 12", 12, parsed);
                Check.True("parse hp lowercase tr", OduPlanner.TryParseHp("odu-8 hp", out parsed));
                Check.False("parse pana", OduPlanner.TryParseHp("ODU_Pana_8HP_Cooling Only", out parsed));
                Check.False("parse odd hp", OduPlanner.TryParseHp("ODU-7 HP", out parsed));

                choice = OduPlanner.ResolveBlock(12, Has());
                Check.True("fallback", choice.Fallback);
                Check.Equal("fallback name", "ODU-12 HP", choice.BlockName);
                Check.Contains("fallback text", choice.Warning, "drawn as a simple block");
            });
        }

        private static void Footprint()
        {
            Check.Case("footprint", () =>
            {
                double w, h;
                OduPlanner.Footprint(6, out w, out h);
                Check.Near("6 w", 930, w);
                Check.Near("6 h", 766, h);
                OduPlanner.Footprint(12, out w, out h);
                Check.Near("12 w", 930, w);
                OduPlanner.Footprint(16, out w, out h);
                Check.Near("16 w", 1240, w);
                Check.Near("16 h", 765, h);
                OduPlanner.Footprint(24, out w, out h);
                Check.Near("24 w", 1880, w);
                OduPlanner.Footprint(30, out w, out h);
                Check.Near("30 w", 3760, w);

                double[][] c = OduPlanner.Corners(100, 200, 0, 930, 766);
                Check.Near("c0x", 100 - 465, c[0][0]);
                Check.Near("c0y", 200 - 766, c[0][1]);
                Check.Near("c2x", 100 + 465, c[2][0]);
                Check.Near("c2y", 200, c[2][1]);

                c = OduPlanner.Corners(100, 200, Math.PI / 2, 930, 766);
                // Local (-465,-766) rotated 90 degrees: (766, -465).
                Check.Near("rot c0x", 100 + 766, c[0][0], 1e-9);
                Check.Near("rot c0y", 200 - 465, c[0][1], 1e-9);
            });
        }

        private static void Split()
        {
            Check.Case("split", () =>
            {
                List<OduIndoorUnit> units = new List<OduIndoorUnit>();
                for (int i = 0; i < 30; i++)
                    units.Add(U(63));
                List<List<int>> groups = OduPlanner.SplitProposal(units, 1350);
                Check.Equal("two systems", 2, groups.Count);
                int total = 0;
                foreach (List<int> group in groups)
                {
                    int sum = 0;
                    foreach (int position in group)
                        sum += units[position].Index;
                    Check.True("group within limit", sum <= 1350, sum.ToString(CultureInfo.InvariantCulture));
                    total += group.Count;
                }

                Check.Equal("every unit once", 30, total);
                Check.Equal("empty", 0, OduPlanner.SplitProposal(new List<OduIndoorUnit>(), 1350).Count);

                // 1000 + 1000 does not fit one system of 1350 and 3 units of 200,250,250.. stay balanced.
                List<OduIndoorUnit> big = new List<OduIndoorUnit> { U(250), U(250), U(250), U(250), U(250), U(250), U(250) };
                groups = OduPlanner.SplitProposal(big, 1350);
                Check.Equal("1750 -> 2 systems", 2, groups.Count);
                Check.Equal("4+3", 7, groups[0].Count + groups[1].Count);
            });
        }

        private static void Settings()
        {
            Check.Case("odu settings", () =>
            {
                OduSettings settings = new OduSettings { TextHeight = 212.5, ModelText = "RXYQ12T", WriteLabel = false };
                OduSettings back = OduSettings.Deserialize(settings.Serialize());
                Check.Near("height", 212.5, back.TextHeight);
                Check.Equal("model", "RXYQ12T", back.ModelText);
                Check.False("label", back.WriteLabel);

                OduSettings defaults = OduSettings.Deserialize("textheight=abc\nmodel=\nlabel=1\nnonsense\nx=y");
                Check.Near("bad value keeps default", 150, defaults.TextHeight);
                Check.True("label on", defaults.WriteLabel);
                Check.Near("null text", 150, OduSettings.Deserialize(null).TextHeight);
                Check.Near("out of range", 150, OduSettings.Deserialize("textheight=5").TextHeight);

                string error;
                Check.True("valid", new OduSettings().IsValid(out error));
                Check.False("invalid", new OduSettings { TextHeight = 1 }.IsValid(out error));
                Check.Contains("error text", error, "text height");
            });
        }

        private static RiserFloorInput F(string name, int units, int index)
        {
            return new RiserFloorInput { Name = name, UnitCount = units, FloorIndex = index, X = 1000, Y = 2000 };
        }

        private static void Riser()
        {
            Check.Case("riser three floors", () =>
            {
                // Floor 3 is the farthest, floor 1 the nearest. Cumulative 100, 225, 365 -> 16 HP (index 400).
                List<RiserFloorInput> input = new List<RiserFloorInput>
                {
                    F("Floor 3", 2, 100), F("Floor 2", 3, 125), F("Floor 1", 4, 140)
                };
                RiserPlan plan = RiserPlanner.Plan("R1", input, 0, 3600, false);
                Check.Equal("total", 365, plan.TotalIndex);
                Check.Equal("hp", 16, plan.Hp);
                Check.Near("ratio", 91.25, plan.Ratio, 1e-9);
                Check.Equal("floors", 3, plan.Floors.Count);
                Check.Equal("cum 0", 100, plan.Floors[0].Cumulative);
                Check.Equal("cum 1", 225, plan.Floors[1].Cumulative);
                Check.Equal("cum 2", 365, plan.Floors[2].Cumulative);
                Check.Equal("order", 0, plan.Floors[0].Order);
                Check.Equal("order 2", 2, plan.Floors[2].Order);
                Check.True("pair 0", plan.Floors[0].Pair == new PipePair(15.9, 9.5));
                Check.True("pair 1", plan.Floors[1].Pair == new PipePair(22.2, 9.5));
                Check.True("main", plan.Floors[2].Pair == new PipePair(28.6, 12.7));
                Check.True("only the last is main", plan.Floors[2].IsMain && !plan.Floors[0].IsMain && !plan.Floors[1].IsMain);
                Check.Equal("no kit on the farthest floor", null, plan.Floors[0].Kit);
                Check.Equal("kit 1", "KHRQ22M29T9", plan.Floors[1].Kit);
                Check.Equal("first kit 16 HP", "KHRQ22M64T", plan.Floors[2].Kit);
                Check.Equal("label text", "%%C15.9/9.5", plan.Floors[0].PairLabel);
                Check.Equal("marker text",
                    "Riser R1 · Floor 2 · %%C22.2/9.5 · Σ225", plan.MarkerText(plan.Floors[1]));
                Check.Equal("no warnings", 0, plan.Warnings.Count);
                Check.Equal("title", "Riser R1 schedule - ODU 16 HP, Σ index 365 / 400 (91 %)", plan.ScheduleTitle);

                List<string[]> rows = plan.ScheduleRows();
                Check.Equal("rows", 3, rows.Count);
                Check.Equal("row 1 cells", 6, rows[1].Length);
                Check.Equal("row 1 floor", "Floor 2", rows[1][0]);
                Check.Equal("row 1 units", "3", rows[1][1]);
                Check.Equal("row 1 floor idx", "125", rows[1][2]);
                Check.Equal("row 1 cum", "225", rows[1][3]);
                Check.Equal("row 1 size", "22.2 / 9.5", rows[1][4]);
                Check.Equal("row 0 kit", "-", rows[0][5]);
            });

            Check.Case("riser sizes by index bands", () =>
            {
                // A big outdoor unit: segments are capped by the main pipe, never larger.
                List<RiserFloorInput> input = new List<RiserFloorInput>
                {
                    F("A", 1, 150), F("B", 1, 50), F("C", 1, 100), F("D", 1, 400)
                };
                RiserPlan plan = RiserPlanner.Plan("R2", input, 0, 3600, false);
                Check.Equal("cum A", 150, plan.Floors[0].Cumulative);
                Check.True("150 -> 19.1/9.5", plan.Floors[0].Pair == new PipePair(19.1, 9.5));
                Check.Equal("cum B", 200, plan.Floors[1].Cumulative);
                Check.True("200 -> 22.2/9.5", plan.Floors[1].Pair == new PipePair(22.2, 9.5));
                Check.Equal("cum C", 300, plan.Floors[2].Cumulative);
                Check.True("300 -> 28.6/12.7", plan.Floors[2].Pair == new PipePair(28.6, 12.7));
                Check.Equal("total 700", 700, plan.TotalIndex);
                Check.Equal("hp 28", 28, plan.Hp);
                Check.True("main 28 HP", plan.Floors[3].Pair == VrvTables.MainByHp(28));
                Check.Equal("kit later 300", "KHRQ22M64T", plan.Floors[2].Kit);
                Check.Equal("first kit 28 HP", "KHRQ22M75T", plan.Floors[3].Kit);

                // Capped by a small main pipe: HP 6 main is 19.1/9.5, a 300 index segment would be 28.6/12.7.
                plan = RiserPlanner.Plan("R3", new List<RiserFloorInput> { F("A", 3, 300), F("B", 1, 25) }, 6, 3600, false);
                Check.True("capped gas", plan.Floors[0].Pair == new PipePair(19.1, 9.5));
                Check.True("ratio warning", plan.Warnings.Exists(w => w.Contains("outside 50-130 %")));
            });

            Check.Case("riser one floor and edge cases", () =>
            {
                RiserPlan plan = RiserPlanner.Plan("", new List<RiserFloorInput> { F("", 2, 100) }, 0, 3600, false);
                Check.Equal("default riser name", "R1", plan.Name);
                Check.Equal("default floor name", "Floor 1", plan.Floors[0].Name);
                Check.True("single floor is main", plan.Floors[0].IsMain);
                Check.Equal("no refnet", null, plan.Floors[0].Kit);
                Check.True("main by 4 HP", plan.Floors[0].Pair == VrvTables.MainByHp(4));

                plan = RiserPlanner.Plan("R1", new List<RiserFloorInput> { F("X", 1, 0), F("X", 1, 50) }, 0, 3600, false);
                Check.True("duplicate name warns", plan.Warnings.Exists(w => w.Contains("used twice")));
                Check.True("no capacity warns", plan.Warnings.Exists(w => w.Contains("no indoor unit capacity")));
                Check.False("no pair without capacity", plan.Floors[0].HasPair);
                Check.Equal("dash", "-", plan.Floors[0].PairLabel);
                Check.Equal("DefaultFloorName", "Floor 3", RiserPlanner.DefaultFloorName(2));
            });

            Check.Case("riser main upsizing", () =>
            {
                List<RiserFloorInput> input = new List<RiserFloorInput> { F("A", 3, 200), F("B", 3, 200) };
                RiserPlan plan = RiserPlanner.Plan("R1", input, 0, 3600, false);
                Check.False("short riser no suggestion", plan.UpsizeSuggested);
                Check.Near("equivalent 3600*2+500", 7700, plan.EquivalentLength);

                RiserPlan longPlan = RiserPlanner.Plan("R1", input, 0, 45000, false);
                Check.True("long riser suggests", longPlan.UpsizeSuggested);
                Check.True("not applied", longPlan.MainPair == longPlan.StandardMain);
                Check.True("warns", longPlan.Warnings.Exists(w => w.Contains("up-sized")));

                RiserPlan applied = RiserPlanner.Plan("R1", input, 0, 45000, true);
                Check.True("main changed", applied.MainPair != applied.StandardMain);
                Check.True("last floor takes it", applied.Floors[1].Pair == applied.MainPair);
                Check.True("between segment keeps the standard cap", applied.Floors[0].Pair != applied.MainPair
                    || applied.Floors[0].Pair == applied.StandardMain);
                Check.True("upsize result", applied.Warnings.Count >= 1);
            });
        }

        private static void RiserSettingsRoundTrip()
        {
            Check.Case("riser settings", () =>
            {
                RiserSettings settings = new RiserSettings { TextHeight = 180.5, MarkerRadius = 250, FloorHeight = 4200, LastName = "R7" };
                RiserSettings back = RiserSettings.Deserialize(settings.Serialize());
                Check.Near("height", 180.5, back.TextHeight);
                Check.Near("radius", 250, back.MarkerRadius);
                Check.Near("floor height", 4200, back.FloorHeight);
                Check.Equal("name", "R7", back.LastName);
                RiserSettings defaults = RiserSettings.Deserialize("radius=-4\nfloorheight=x\nname=");
                Check.Near("radius default", 300, defaults.MarkerRadius);
                Check.Near("floor height default", 3600, defaults.FloorHeight);
                Check.Equal("name default", "R1", defaults.LastName);
                string error;
                Check.False("invalid", new RiserSettings { MarkerRadius = 0 }.IsValid(out error));
                Check.True("valid", new RiserSettings().IsValid(out error));
            });
        }
    }
}
