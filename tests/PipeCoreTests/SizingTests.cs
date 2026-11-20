using System;
using System.Collections.Generic;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Pipes;
using static PipeCoreTests.Support;

namespace PipeCoreTests
{
    /// <summary>
    /// HVACREFRIG pipe sizing: the Daikin VRV IV (RXYQ-T/U) tables as the user gave them (AGENTS.md,
    /// "HVACREFRIG", "Pipe sizing"), tested at every band edge, plus the capping, the default outdoor
    /// unit, the connection ratio, manual overrides, main pipe upsizing, capacity text and label
    /// placement. Expected values are written out by hand from the tables, not taken from the code.
    /// </summary>
    internal static class SizingTests
    {
        private const string Rp = "EIA_1 RP 22.2-9.5 (28)";

        public static void Run()
        {
            IndexAndKw();
            CapacityTextParsing();
            PairsAndCatalogue();
            UnitConnections();
            BetweenRefnetEdges();
            MainByHpTable();
            DefaultHpAndRatio();
            RefnetKits();
            SingleUnit();
            TeeOfTwoUnits();
            SeriesSizing();
            CapAtTheMain();
            ManualOverridesAreKept();
            ToUnitOverrideWarns();
            MissingCapacityBlocksApply();
            UpsizeLadderWithEveryLayer();
            UpsizeWithTheTemplatesLayers();
            UpsizeThroughCompute();
            UnconfirmedNotes();
            LongestPipe();
            LabelPlacement();
        }

        // ------------------------------------------------------------------ helpers

        private static RefrigPath P(string layer, params double[] xy)
        {
            RefrigPath path = new RefrigPath { Layer = layer };
            for (int i = 0; i + 1 < xy.Length; i += 2)
                path.Points.Add(new Vec2(xy[i], xy[i + 1]));
            return path;
        }

        private static NetworkTopology Single()
        {
            return NetworkTopologyBuilder.Build(new TopologyInput
            {
                Paths = new[] { P(Rp, 0, 0, 6000, 0) },
                Root = V(0, 0)
            });
        }

        // Root -> refnet at 5000 -> through end (9000, 0) = unit 0 and branch end (5000, 3000) = unit 1.
        private static NetworkTopology Tee()
        {
            RefrigResult r = CleanWithRoot(new List<PipeCurve>
            {
                Line(0, Rp, 0, 0, 5000, 0),
                Line(1, Rp, 5000, 0, 9000, 0),
                Line(2, Rp, 5000, 0, 5000, 3000)
            }, V(0, 0));
            Check.True("tee: cleaner ok", r.Ok);
            return NetworkTopologyBuilder.Build(new TopologyInput
            {
                Paths = r.Paths, PlannedRefnets = r.Refnets, Root = r.Root, Tolerance = 50
            });
        }

        // Two refnets in series: units 0 (20000, 0), 1 (16000, 4000), 2 (8000, 4000);
        // segments 0 main, 1 between, 2 and 3 to units 0 and 1, 4 to unit 2.
        private static NetworkTopology Series()
        {
            RefrigResult r = CleanWithRoot(new List<PipeCurve>
            {
                Line(0, Rp, 0, 0, 8000, 0),
                Line(1, Rp, 8000, 0, 16000, 0),
                Line(2, Rp, 8000, 0, 8000, 4000),
                Line(3, Rp, 16000, 0, 16000, 4000),
                Line(4, Rp, 16000, 0, 20000, 0)
            }, V(0, 0));
            Check.True("series: cleaner ok", r.Ok);
            return NetworkTopologyBuilder.Build(new TopologyInput
            {
                Paths = r.Paths, PlannedRefnets = r.Refnets, Root = r.Root, Tolerance = 50
            });
        }

        private static SizingResult Size(
            NetworkTopology topology, int[] units, int hp = 0, bool upsize = false,
            Dictionary<int, string> overrides = null, Func<PipePair, bool> available = null)
        {
            return RefrigSizer.Compute(new SizingRequest
            {
                Topology = topology,
                UnitIndexes = units,
                Hp = hp,
                UpsizeMain = upsize,
                Overrides = overrides ?? new Dictionary<int, string>(),
                LayerAvailable = available
            });
        }

        private static void Pair(string label, double gas, double liquid, PipePair actual)
        {
            Check.True(label + " (" + actual + ")", actual == new PipePair(gas, liquid),
                actual.ToString());
        }

        private static bool AnyContains(List<string> lines, string fragment)
        {
            return lines.Exists(l => l.IndexOf(fragment, StringComparison.Ordinal) >= 0);
        }

        // ------------------------------------------------------------------ tables

        private static void IndexAndKw()
        {
            Check.Case("kW to index", () =>
            {
                int[] indexes = { 15, 20, 25, 32, 40, 50, 63, 71, 80, 100, 125, 140, 200, 250 };
                double[] kw = { 1.7, 2.2, 2.8, 3.6, 4.5, 5.6, 7.1, 8.0, 9.0, 11.2, 14.0, 16.0, 22.4, 28.0 };
                Check.Equal("tables line up", indexes.Length, kw.Length);
                for (int i = 0; i < indexes.Length; i++)
                {
                    Check.Equal("index list " + indexes[i], indexes[i], VrvTables.UnitIndexes[i]);
                    Check.Near("nominal kW of " + indexes[i], kw[i], VrvTables.NominalKw(indexes[i]), 1e-9);
                    Check.Equal("kW " + kw[i] + " maps to " + indexes[i], indexes[i], VrvTables.NearestIndex(kw[i]));
                }

                Check.Equal("2.6 kW is nearer 2.8 than 2.2", 25, VrvTables.NearestIndex(2.6));
                Check.Equal("2.3 kW is nearer 2.2", 20, VrvTables.NearestIndex(2.3));
                Check.Equal("7.6 kW is nearer 8.0 than 7.1", 71, VrvTables.NearestIndex(7.6));
                Check.Equal("12 kW is nearer 11.2", 100, VrvTables.NearestIndex(12.0));
                Check.Equal("above the list: the largest", 250, VrvTables.NearestIndex(40));
                Check.Equal("below the list: the smallest", 15, VrvTables.NearestIndex(0.4));
                Check.True("2.85 kW is a nominal value", VrvTables.IsNominalKw(2.85));
                Check.False("3.0 kW is not", VrvTables.IsNominalKw(3.0));
                Check.Equal("dialog text", "25 · 2.8 kW", VrvTables.IndexText(25));
                Check.True("15, 71 and 140 are unconfirmed",
                    VrvTables.IsUnitUnconfirmed(15) && VrvTables.IsUnitUnconfirmed(71) && VrvTables.IsUnitUnconfirmed(140));
                Check.False("25 is confirmed", VrvTables.IsUnitUnconfirmed(25));
                Check.True("an index in the list", VrvTables.IsUnitIndex(63));
                Check.False("an index not in the list", VrvTables.IsUnitIndex(64));
            });
        }

        private static void CapacityTextParsing()
        {
            Check.Case("capacity text", () =>
            {
                double kw;
                Check.True("block name", CapacityText.TryParseKw("FCU-CAS-2.8 kW", out kw));
                Check.Near("2.8", 2.8, kw);
                Check.True("IU", CapacityText.TryParseKw("IU-5.6 kW", out kw));
                Check.Near("5.6", 5.6, kw);
                Check.True("no space", CapacityText.TryParseKw("FCU 2.8kW", out kw));
                Check.Near("2.8 again", 2.8, kw);
                Check.True("decimal comma", CapacityText.TryParseKw("IU 5,6 kW", out kw));
                Check.Near("5.6 again", 5.6, kw);
                Check.True("an unit tag in front", CapacityText.TryParseKw("FCU-1.1.1 2.8 kW", out kw));
                Check.Near("2.8 after the tag", 2.8, kw);
                Check.True("two digits before the point", CapacityText.TryParseKw("FCU 11.2 kW", out kw));
                Check.Near("11.2", 11.2, kw);
                Check.True("upper case", CapacityText.TryParseKw("AHU 14 KW", out kw));
                Check.Near("14", 14, kw);
                Check.True("the first one wins", CapacityText.TryParseKw("Cool 3 kW / Heat 3.2 kW", out kw));
                Check.Near("3", 3, kw);
                Check.False("no capacity", CapacityText.TryParseKw("FCU-CAS", out kw));
                Check.False("kWh is not kW", CapacityText.TryParseKw("12 kWh", out kw));
                Check.False("empty", CapacityText.TryParseKw("", out kw));
                Check.False("null", CapacityText.TryParseKw(null, out kw));
                Check.False("zero", CapacityText.TryParseKw("0 kW", out kw));
            });
        }

        private static void PairsAndCatalogue()
        {
            Check.Case("pairs and the layer catalogue", () =>
            {
                PipePair pair;
                Check.True("parse with bracket", PipePair.TryParse("15.9-9.5 (14)", out pair));
                Check.True("is 15.9/9.5", pair == new PipePair(15.9, 9.5));
                Check.True("parse without bracket", PipePair.TryParse("9.5-6.4", out pair));
                Check.True("is 9.5/6.4", pair == new PipePair(9.5, 6.4));
                Check.False("parse garbage", PipePair.TryParse("DN40", out pair));
                Check.Equal("key", "15.9-9.5", new PipePair(15.9, 9.5).Key);
                Check.Equal("label text", "%%C15.9/9.5", new PipePair(15.9, 9.5).LabelText);
                Check.Equal("label text of a short size", "%%C12.7/6.4", new PipePair(12.7, 6.4).LabelText);

                // Every pair the tables can give has a layer.
                double[][] pairs =
                {
                    new[] { 12.7, 6.4 }, new[] { 15.9, 9.5 }, new[] { 19.1, 9.5 }, new[] { 22.2, 9.5 },
                    new[] { 28.6, 12.7 }, new[] { 28.6, 15.9 }, new[] { 34.9, 15.9 }, new[] { 34.9, 19.1 },
                    new[] { 41.3, 19.1 }
                };
                foreach (double[] p in pairs)
                {
                    PipePair standard = new PipePair(p[0], p[1]);
                    Check.True(standard + " has a catalogue layer", RefrigCatalog.Has(standard));
                    Check.True(standard + " layer starts with EIA_1 RP",
                        RefrigCatalog.LayerFor(standard).StartsWith("EIA_1 RP "));
                }

                Check.Equal("15.9/9.5 keeps its bracket", "15.9-9.5 (14)", RefrigCatalog.TextFor(new PipePair(15.9, 9.5)));
                Check.Equal("28.6/12.7 layer", "EIA_1 RP 28.6-12.7 (33.6-44.8)",
                    RefrigCatalog.LayerFor(new PipePair(28.6, 12.7)));
                Check.Equal("12.7/6.4 has no bracket", "EIA_1 RP 12.7-6.4", RefrigCatalog.LayerFor(new PipePair(12.7, 6.4)));
                Check.False("19.1/12.7 has no layer", RefrigCatalog.Has(new PipePair(19.1, 12.7)));
                Check.True("no layer name for it", RefrigCatalog.LayerFor(new PipePair(19.1, 12.7)) == null);
            });
        }

        private static void UnitConnections()
        {
            Check.Case("refnet to unit by the unit's index", () =>
            {
                foreach (int index in new[] { 15, 20, 25, 32, 40, 50 })
                    Pair("index " + index, 12.7, 6.4, VrvTables.UnitConnection(index));
                foreach (int index in new[] { 63, 71, 80, 100, 125, 140 })
                    Pair("index " + index, 15.9, 9.5, VrvTables.UnitConnection(index));
                Pair("index 200", 19.1, 9.5, VrvTables.UnitConnection(200));
                Pair("index 250", 22.2, 9.5, VrvTables.UnitConnection(250));
            });
        }

        private static void BetweenRefnetEdges()
        {
            Check.Case("refnet to refnet at every band edge", () =>
            {
                Pair("1", 15.9, 9.5, VrvTables.BetweenRefnets(1));
                Pair("149", 15.9, 9.5, VrvTables.BetweenRefnets(149));
                Pair("150", 19.1, 9.5, VrvTables.BetweenRefnets(150));
                Pair("199", 19.1, 9.5, VrvTables.BetweenRefnets(199));
                Pair("200", 22.2, 9.5, VrvTables.BetweenRefnets(200));
                Pair("289", 22.2, 9.5, VrvTables.BetweenRefnets(289));
                Pair("290", 28.6, 12.7, VrvTables.BetweenRefnets(290));
                Pair("419", 28.6, 12.7, VrvTables.BetweenRefnets(419));
                Pair("420", 28.6, 15.9, VrvTables.BetweenRefnets(420));
                Pair("639", 28.6, 15.9, VrvTables.BetweenRefnets(639));
                Pair("640", 34.9, 19.1, VrvTables.BetweenRefnets(640));
                Pair("919", 34.9, 19.1, VrvTables.BetweenRefnets(919));
                Pair("920", 41.3, 19.1, VrvTables.BetweenRefnets(920));
                Pair("1350", 41.3, 19.1, VrvTables.BetweenRefnets(1350));
                Check.False("919 is confirmed", VrvTables.IsBetweenUnconfirmed(919));
                Check.True("920 is unconfirmed", VrvTables.IsBetweenUnconfirmed(920));
            });
        }

        private static void MainByHpTable()
        {
            Check.Case("main pipe by outdoor unit HP", () =>
            {
                Pair("5 HP", 15.9, 9.5, VrvTables.MainByHp(5));
                Pair("6 HP", 19.1, 9.5, VrvTables.MainByHp(6));
                Pair("8 HP", 19.1, 9.5, VrvTables.MainByHp(8));
                Pair("10 HP", 22.2, 9.5, VrvTables.MainByHp(10));
                foreach (int hp in new[] { 12, 14, 16 })
                    Pair(hp + " HP", 28.6, 12.7, VrvTables.MainByHp(hp));
                foreach (int hp in new[] { 18, 20, 22 })
                    Pair(hp + " HP", 28.6, 15.9, VrvTables.MainByHp(hp));
                Pair("24 HP", 34.9, 15.9, VrvTables.MainByHp(24));
                foreach (int hp in new[] { 26, 28, 30, 32, 34 })
                    Pair(hp + " HP", 34.9, 19.1, VrvTables.MainByHp(hp));
                foreach (int hp in new[] { 36, 38, 40, 42, 44, 46, 48, 50, 52, 54 })
                    Pair(hp + " HP", 41.3, 19.1, VrvTables.MainByHp(hp));
                Pair("4 HP is taken as 5 HP", 15.9, 9.5, VrvTables.MainByHp(4));
                Check.True("6 HP is unconfirmed", VrvTables.IsMainUnconfirmed(6));
                Check.False("8 HP is confirmed", VrvTables.IsMainUnconfirmed(8));
            });
        }

        private static void DefaultHpAndRatio()
        {
            Check.Case("default outdoor unit and the outdoor index", () =>
            {
                int[] choices = { 4, 5, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 32, 34, 36, 38, 40, 42, 44, 46, 48, 50, 52, 54 };
                Check.Equal("choices", choices.Length, VrvTables.HpChoices.Length);
                for (int i = 0; i < choices.Length; i++)
                    Check.Equal("choice " + i, choices[i], VrvTables.HpChoices[i]);

                Check.Equal("4 HP index", 100, VrvTables.OduIndex(4));
                Check.Equal("8 HP index", 200, VrvTables.OduIndex(8));
                Check.Equal("10 HP index", 250, VrvTables.OduIndex(10));
                Check.Equal("54 HP index", 1350, VrvTables.OduIndex(54));
                Check.True("above 8 HP is unconfirmed", VrvTables.IsOduIndexUnconfirmed(10));
                Check.False("8 HP is not", VrvTables.IsOduIndexUnconfirmed(8));

                Check.Equal("nothing entered", 4, VrvTables.DefaultHp(0));
                Check.Equal("100 fits 4 HP", 4, VrvTables.DefaultHp(100));
                Check.Equal("101 needs 5 HP", 5, VrvTables.DefaultHp(101));
                Check.Equal("125 fits 5 HP", 5, VrvTables.DefaultHp(125));
                Check.Equal("126 needs 6 HP", 6, VrvTables.DefaultHp(126));
                Check.Equal("150 fits 6 HP", 6, VrvTables.DefaultHp(150));
                Check.Equal("151 needs 8 HP", 8, VrvTables.DefaultHp(151));
                Check.Equal("200 fits 8 HP", 8, VrvTables.DefaultHp(200));
                Check.Equal("201 needs 10 HP", 10, VrvTables.DefaultHp(201));
                Check.Equal("250 fits 10 HP", 10, VrvTables.DefaultHp(250));
                Check.Equal("251 needs 12 HP", 12, VrvTables.DefaultHp(251));
                Check.Equal("1350 fits 54 HP", 54, VrvTables.DefaultHp(1350));
                Check.Equal("above the largest, the largest", 54, VrvTables.DefaultHp(2000));

                // Ratio: 188 of 8 HP (200) is 94 %; 100 of 8 HP is 50 %; 260 of 8 HP is 130 %.
                NetworkTopology t = Tee();
                SizingResult r = Size(t, new[] { 125, 63 });
                Check.Equal("sum", 188, r.SumIndex);
                Check.Equal("default HP", 8, r.Hp);
                Check.True("it is the default", r.HpIsDefault);
                Check.Equal("outdoor index", 200, r.OduIndex);
                Check.Near("ratio", 94, r.Ratio, 1e-9);
                Check.Equal("no ratio warning at 94 %", 0, r.Warnings.FindAll(w => w.Contains("Connection ratio")).Count);

                SizingResult edgeLow = Size(t, new[] { 50, 50 }, 8);
                Check.Near("100 of 200 is 50 %", 50, edgeLow.Ratio, 1e-9);
                Check.False("50 % is inside the range", AnyContains(edgeLow.Warnings, "Connection ratio"));
                SizingResult low = Size(t, new[] { 40, 50 }, 8);
                Check.Near("90 of 200", 45, low.Ratio, 1e-9);
                Check.True("45 % warns", AnyContains(low.Warnings, "Connection ratio 45 %"));
                SizingResult edgeHigh = Size(t, new[] { 125, 135 }, 8);
                Check.Near("260 of 200", 130, edgeHigh.Ratio, 1e-9);
                Check.False("130 % is inside the range", AnyContains(edgeHigh.Warnings, "Connection ratio"));
                SizingResult high = Size(t, new[] { 140, 125 }, 8);
                Check.Near("265 of 200", 132.5, high.Ratio, 1e-9);
                Check.True("132.5 % warns", AnyContains(high.Warnings, "Connection ratio 133 %"));
                SizingResult chosen = Size(t, new[] { 125, 63 }, 12);
                Check.False("a chosen HP is not the default", chosen.HpIsDefault);
                Check.Equal("chosen HP", 12, chosen.Hp);
                Check.Near("188 of 300", 188.0 / 300 * 100, chosen.Ratio, 1e-9);
                Check.True("63 % is fine", !AnyContains(chosen.Warnings, "Connection ratio"));
                SizingResult unknown = Size(t, new[] { 125, 63 }, 7);
                Check.True("an HP that is not offered falls back to the default", unknown.HpIsDefault && unknown.Hp == 8);
            });
        }

        private static void RefnetKits()
        {
            Check.Case("refnet kits", () =>
            {
                foreach (int hp in new[] { 4, 5, 6 })
                    Check.Equal("first joint " + hp, "KHRQ22M20T", VrvTables.FirstKit(hp));
                foreach (int hp in new[] { 8, 10 })
                    Check.Equal("first joint " + hp, "KHRQ22M29T9", VrvTables.FirstKit(hp));
                foreach (int hp in new[] { 12, 14, 16, 18, 20, 22 })
                    Check.Equal("first joint " + hp, "KHRQ22M64T", VrvTables.FirstKit(hp));
                foreach (int hp in new[] { 24, 26, 36, 54 })
                    Check.Equal("first joint " + hp, "KHRQ22M75T", VrvTables.FirstKit(hp));

                Check.Equal("later 199", "KHRQ22M20T", VrvTables.LaterKit(199));
                Check.Equal("later 200", "KHRQ22M29T9", VrvTables.LaterKit(200));
                Check.Equal("later 289", "KHRQ22M29T9", VrvTables.LaterKit(289));
                Check.Equal("later 290", "KHRQ22M64T", VrvTables.LaterKit(290));
                Check.Equal("later 639", "KHRQ22M64T", VrvTables.LaterKit(639));
                Check.Equal("later 640", "KHRQ22M75T", VrvTables.LaterKit(640));
            });
        }

        // ------------------------------------------------------------------ sizing a tree

        private static void SingleUnit()
        {
            Check.Case("a single unit with no refnet: the pipe is the main, sized by the outdoor unit", () =>
            {
                SizingResult r = Size(Single(), new[] { 25 }, 8);
                Check.Equal("one segment", 1, r.Segments.Count);
                Check.Equal("kind", SegmentKind.Main, r.Segments[0].Kind);
                Check.Equal("8 HP main", "19.1-9.5 (22.4)", r.Segments[0].ChosenText);
                Check.True("can apply", r.CanApply);
                Check.Equal("no kits", 0, r.Kits.Count);
                Check.True("25 of 200 is a low connection ratio", AnyContains(r.Warnings, "Connection ratio 13 %"));

                SizingResult byDefault = Size(Single(), new[] { 25 });
                Check.Equal("default 4 HP", 4, byDefault.Hp);
                Check.Equal("4 HP main", "15.9-9.5 (14)", byDefault.Segments[0].ChosenText);
            });
        }

        private static void TeeOfTwoUnits()
        {
            Check.Case("a refnet with two units", () =>
            {
                SizingResult r = Size(Tee(), new[] { 125, 63 });
                Check.Equal("segments", 3, r.Segments.Count);
                Check.Equal("main by 8 HP", "19.1-9.5 (22.4)", r.Segments[0].ChosenText);
                Check.Equal("main total", 188, r.Segments[0].TotalIndex);
                Check.Equal("main units", 2, r.Segments[0].UnitCount);
                Check.Equal("125 is 15.9/9.5", "15.9-9.5 (14)", r.Segments[1].ChosenText);
                Check.Equal("63 is 15.9/9.5", "15.9-9.5 (14)", r.Segments[2].ChosenText);
                Check.Equal("to-unit total", 125, r.Segments[1].TotalIndex);
                Check.True("can apply", r.CanApply);
                Check.Equal("no warnings", 0, r.Warnings.Count);
                Check.Equal("one kit", 1, r.Kits.Count);
                Check.True("it is the first joint", r.Kits[0].First);
                Check.Equal("first joint at 8 HP", "KHRQ22M29T9", r.Kits[0].Kit);
                Check.Equal("its total", 188, r.Kits[0].TotalIndex);

                SizingResult small = Size(Tee(), new[] { 25, 20 });
                Check.Equal("small units connect at 12.7/6.4", "12.7-6.4", small.Segments[1].ChosenText);
                Check.Equal("small units connect at 12.7/6.4", "12.7-6.4", small.Segments[2].ChosenText);
                Check.Equal("the main is 4 HP", "15.9-9.5 (14)", small.Segments[0].ChosenText);

                SizingResult big = Size(Tee(), new[] { 250, 200 });
                Check.Equal("250", "22.2-9.5 (28)", big.Segments[1].ChosenText);
                Check.Equal("200", "19.1-9.5 (22.4)", big.Segments[2].ChosenText);
                Check.Equal("450 needs 18 HP", 18, big.Hp);
                Check.Equal("and the 18 HP main", "28.6-15.9 (50.4-61.6)", big.Segments[0].ChosenText);
            });
        }

        private static void SeriesSizing()
        {
            Check.Case("two refnets in series", () =>
            {
                NetworkTopology t = Series();
                SizingResult r = Size(t, new[] { 100, 100, 100 });
                Check.Equal("sum", 300, r.SumIndex);
                Check.Equal("12 HP", 12, r.Hp);
                Check.Equal("main by 12 HP", "28.6-12.7 (33.6-44.8)", r.Segments[0].ChosenText);
                Check.Equal("the pipe between has units 0 and 1: 200 is 22.2/9.5", "22.2-9.5 (28)", r.Segments[1].ChosenText);
                Check.Equal("its total", 200, r.Segments[1].TotalIndex);
                Check.Equal("its units", 2, r.Segments[1].UnitCount);
                Check.Equal("100 is 15.9/9.5", "15.9-9.5 (14)", r.Segments[2].ChosenText);
                Check.Equal("100 is 15.9/9.5", "15.9-9.5 (14)", r.Segments[3].ChosenText);
                Check.Equal("100 is 15.9/9.5", "15.9-9.5 (14)", r.Segments[4].ChosenText);
                Check.Equal("two kits", 2, r.Kits.Count);
                Check.Equal("the first joint at 12 HP", "KHRQ22M64T", r.Kits[0].Kit);
                Check.True("it is first", r.Kits[0].First);
                Check.Equal("the second joint at 200", "KHRQ22M29T9", r.Kits[1].Kit);
                Check.False("it is not first", r.Kits[1].First);
                Check.Equal("its total", 200, r.Kits[1].TotalIndex);
                Check.Equal("no warnings", 0, r.Warnings.Count);

                // Band edges on the pipe between refnets: units 0 + 1 = 149, 150, 199, 200.
                Check.Equal("149", "15.9-9.5 (14)", Size(t, new[] { 74, 75, 1 }, 12).Segments[1].ChosenText);
                Check.Equal("150", "19.1-9.5 (22.4)", Size(t, new[] { 75, 75, 1 }, 12).Segments[1].ChosenText);
                Check.Equal("199", "19.1-9.5 (22.4)", Size(t, new[] { 100, 99, 1 }, 12).Segments[1].ChosenText);
                Check.Equal("200", "22.2-9.5 (28)", Size(t, new[] { 100, 100, 1 }, 12).Segments[1].ChosenText);
                Check.Equal("289", "22.2-9.5 (28)", Size(t, new[] { 150, 139, 1 }, 24).Segments[1].ChosenText);
                Check.Equal("290", "28.6-12.7 (33.6-44.8)", Size(t, new[] { 150, 140, 1 }, 24).Segments[1].ChosenText);
            });
        }

        private static void CapAtTheMain()
        {
            Check.Case("a pipe between refnets is never larger than the main, gas and liquid each", () =>
            {
                NetworkTopology t = Series();
                // Units 0 and 1 total 500: the table gives 28.6/15.9.
                SizingResult small = Size(t, new[] { 250, 250, 50 }, 8);
                Check.Equal("8 HP main is 19.1/9.5", "19.1-9.5 (22.4)", small.Segments[0].ChosenText);
                Check.Equal("capped to the main", "19.1-9.5 (22.4)", small.Segments[1].ChosenText);
                Check.False("the cap is not manual", small.Segments[1].Manual);

                SizingResult liquid = Size(t, new[] { 250, 250, 50 }, 12);
                Check.Equal("12 HP main is 28.6/12.7", "28.6-12.7 (33.6-44.8)", liquid.Segments[0].ChosenText);
                Check.Equal("gas is the main's already, liquid is capped to 12.7",
                    "28.6-12.7 (33.6-44.8)", liquid.Segments[1].ChosenText);

                SizingResult open = Size(t, new[] { 250, 250, 50 }, 18);
                Check.Equal("18 HP main is 28.6/15.9", "28.6-15.9 (50.4-61.6)", open.Segments[0].ChosenText);
                Check.Equal("500 fits 28.6/15.9 under it", "28.6-15.9 (50.4-61.6)", open.Segments[1].ChosenText);

                // The cap uses the standard main, not an upsized one.
                SizingResult upsized = Size(t, new[] { 250, 250, 50 }, 12, true);
                Check.Equal("the main is upsized (liquid only: the template has no 31.8 layer)",
                    "28.6-15.9 (50.4-61.6)", upsized.Segments[0].ChosenText);
                Check.Equal("the pipe between stays under the standard 12 HP main",
                    "28.6-12.7 (33.6-44.8)", upsized.Segments[1].ChosenText);
            });
        }

        private static void ManualOverridesAreKept()
        {
            Check.Case("manual sizes are kept when the capacities change", () =>
            {
                NetworkTopology t = Series();
                Dictionary<int, string> overrides = new Dictionary<int, string>
                {
                    { 1, "28.6-15.9 (50.4-61.6)" }
                };

                SizingResult first = Size(t, new[] { 100, 100, 100 }, 0, false, overrides);
                Check.Equal("the manual size is used", "28.6-15.9 (50.4-61.6)", first.Segments[1].ChosenText);
                Check.True("it is manual", first.Segments[1].Manual);
                Check.Equal("the automatic size is still worked out", "22.2-9.5 (28)", first.Segments[1].AutoText);
                Check.False("others are not manual", first.Segments[2].Manual);

                SizingResult second = Size(t, new[] { 80, 80, 80 }, 0, false, overrides);
                Check.Equal("after a change it is still there", "28.6-15.9 (50.4-61.6)", second.Segments[1].ChosenText);
                Check.True("still manual", second.Segments[1].Manual);
                Check.Equal("the automatic size follows the change (160)", "19.1-9.5 (22.4)", second.Segments[1].AutoText);

                SizingResult same = Size(t, new[] { 100, 100, 100 }, 0, false,
                    new Dictionary<int, string> { { 1, "22.2-9.5 (28)" } });
                Check.False("a manual value equal to the automatic one is not marked", same.Segments[1].Manual);

                // Above the main: a warning.
                SizingResult above = Size(t, new[] { 100, 100, 100 }, 5, false,
                    new Dictionary<int, string> { { 1, "34.9-19.1 (72.8-95.2)" } });
                Check.Equal("the manual size is used", "34.9-19.1 (72.8-95.2)", above.Segments[1].ChosenText);
                Check.Contains("the segment says why", above.Segments[1].Warning, "larger than the main");
                Check.True("and the list of warnings", AnyContains(above.Warnings, "S2: larger than the main"));
                Check.True("it can still be applied", above.CanApply);

                SizingResult bad = Size(t, new[] { 100, 100, 100 }, 0, false,
                    new Dictionary<int, string> { { 1, "DN40" } });
                Check.Equal("an unknown manual size is ignored", "22.2-9.5 (28)", bad.Segments[1].ChosenText);
            });
        }

        private static void ToUnitOverrideWarns()
        {
            Check.Case("a pipe to a unit that is not the unit's connection size warns", () =>
            {
                NetworkTopology t = Tee();
                SizingResult r = Size(t, new[] { 125, 63 }, 0, false,
                    new Dictionary<int, string> { { 1, "12.7-6.4" } });
                Check.True("manual", r.Segments[1].Manual);
                Check.Contains("warning", r.Segments[1].Warning, "connection size");
                Check.True("in the list", AnyContains(r.Warnings, "S2:"));
                Check.True("applies anyway", r.CanApply);
            });
        }

        private static void MissingCapacityBlocksApply()
        {
            Check.Case("a unit without capacity blocks Apply", () =>
            {
                NetworkTopology t = Tee();
                SizingResult r = Size(t, new[] { 125, 0 });
                Check.False("cannot apply", r.CanApply);
                Check.Equal("one missing", 1, r.UnitsWithoutCapacity);
                Check.True("the warning says so", AnyContains(r.Warnings, "1 indoor unit(s) have no capacity"));
                Check.False("its pipe has no size", r.Segments[2].HasChosen);
                Check.Contains("and says why", r.Segments[2].Warning, "without capacity");
                Check.True("the main is sized from the others", r.Segments[0].HasChosen);
                Check.Equal("the sum counts only what is entered", 125, r.SumIndex);

                SizingResult shortList = Size(t, new int[0]);
                Check.Equal("an empty list: both missing", 2, shortList.UnitsWithoutCapacity);
                Check.False("cannot apply", shortList.CanApply);

                SizingResult withManual = Size(t, new[] { 125, 0 }, 0, false,
                    new Dictionary<int, string> { { 2, "12.7-6.4" } });
                Check.False("a manual size does not fix a missing capacity", withManual.CanApply);
            });
        }

        // ------------------------------------------------------------------ upsizing

        private static PipePair Up(int hp, Func<PipePair, bool> available, out string note)
        {
            return VrvTables.UpsizeMain(VrvTables.MainByHp(hp), hp, available, out note);
        }

        private static void UpsizeLadderWithEveryLayer()
        {
            Check.Case("main upsizing, one step on gas and liquid, when every size has a layer", () =>
            {
                string note;
                Func<PipePair, bool> everything = p => true;
                Pair("5 HP 15.9/9.5", 19.1, 12.7, Up(5, everything, out note));
                Check.True("no note", note == null);
                Pair("6 HP 19.1/9.5", 22.2, 12.7, Up(6, everything, out note));
                Pair("8 HP 19.1/9.5", 22.2, 12.7, Up(8, everything, out note));
                Pair("10 HP 22.2/9.5 (gas 25.4)", 25.4, 12.7, Up(10, everything, out note));
                Pair("12 HP 28.6/12.7 (gas 31.8)", 31.8, 15.9, Up(12, everything, out note));
                Pair("16 HP", 31.8, 15.9, Up(16, everything, out note));
                Pair("18 HP: no gas upsize", 28.6, 19.1, Up(18, everything, out note));
                Check.True("and no note for that", note == null);
                Pair("22 HP: no gas upsize", 28.6, 19.1, Up(22, everything, out note));
                Pair("24 HP 34.9/15.9 (gas 38.1)", 38.1, 19.1, Up(24, everything, out note));
                Pair("26 HP 34.9/19.1", 38.1, 22.2, Up(26, everything, out note));
                Pair("36 HP 41.3/19.1: gas has no step", 41.3, 22.2, Up(36, everything, out note));
                Check.True("none of these needed a note", note == null);
            });
        }

        private static void UpsizeWithTheTemplatesLayers()
        {
            Check.Case("main upsizing keeps to the layers the template has", () =>
            {
                string note;
                Pair("5 HP: only the gas can step up", 19.1, 9.5, Up(5, null, out note));
                Check.Contains("note names the gas", note, "gas");
                Pair("6 HP: only the gas", 22.2, 9.5, Up(6, null, out note));
                Pair("8 HP: only the gas", 22.2, 9.5, Up(8, null, out note));
                Pair("10 HP: 25.4 has no layer, kept", 22.2, 9.5, Up(10, null, out note));
                Check.Contains("note says kept", note, "kept the standard size");
                Pair("12 HP: only the liquid", 28.6, 15.9, Up(12, null, out note));
                Check.Contains("note names the liquid", note, "liquid");
                Pair("16 HP: only the liquid", 28.6, 15.9, Up(16, null, out note));
                Pair("18 HP: 28.6/19.1 has no layer, kept", 28.6, 15.9, Up(18, null, out note));
                Check.Contains("note says kept", note, "kept the standard size");
                Pair("24 HP: only the liquid", 34.9, 19.1, Up(24, null, out note));
                Pair("26 HP: kept", 34.9, 19.1, Up(26, null, out note));
                Pair("36 HP: kept", 41.3, 19.1, Up(36, null, out note));
                Check.Contains("note says kept", note, "kept the standard size");
            });
        }

        private static void UpsizeThroughCompute()
        {
            Check.Case("the upsize option changes only the main pipe", () =>
            {
                NetworkTopology t = Tee();
                SizingResult plain = Size(t, new[] { 125, 63 }, 12);
                SizingResult up = Size(t, new[] { 125, 63 }, 12, true);
                Check.Equal("plain main", "28.6-12.7 (33.6-44.8)", plain.Segments[0].ChosenText);
                Check.Equal("upsized main (liquid only)", "28.6-15.9 (50.4-61.6)", up.Segments[0].ChosenText);
                Check.Equal("the pipes to units are unchanged", plain.Segments[1].ChosenText, up.Segments[1].ChosenText);
                Check.True("a warning says what was done", AnyContains(up.Warnings, "only the liquid"));

                SizingResult kept = Size(t, new[] { 125, 63 }, 10, true);
                Check.Equal("10 HP stays at the standard size", "22.2-9.5 (28)", kept.Segments[0].ChosenText);
                Check.True("with a warning", AnyContains(kept.Warnings, "kept the standard size"));

                SizingResult all = Size(t, new[] { 125, 63 }, 10, true, null, p => true);
                Check.True("the main would be 25.4/12.7 where the layer existed", all.Segments[0].Auto == new PipePair(25.4, 12.7));
                Check.True("but with no catalogue text it has no usable size", !all.Segments[0].HasAuto);
            });
        }

        private static void UnconfirmedNotes()
        {
            Check.Case("unconfirmed cells are noted", () =>
            {
                NetworkTopology t = Tee();
                SizingResult r = Size(t, new[] { 15, 71 }, 6);
                Check.True("index 15", AnyContains(r.Notes, "Indoor index 15"));
                Check.True("index 71", AnyContains(r.Notes, "Indoor index 71"));
                Check.True("6 HP main", AnyContains(r.Notes, "main pipe size of 6 HP"));
                Check.False("6 HP index is not above 8", AnyContains(r.Notes, "outdoor index"));
                SizingResult confirmed = Size(t, new[] { 25, 32 }, 8);
                Check.Equal("nothing unconfirmed at 8 HP with 25 and 32", 0, confirmed.Notes.Count);
                SizingResult ten = Size(t, new[] { 25, 32 }, 10);
                Check.True("10 HP index", AnyContains(ten.Notes, "outdoor index of 10 HP"));
            });
        }

        private static void LongestPipe()
        {
            Check.Case("the longest pipe from the root, with 0.5 m per refnet", () =>
            {
                NetworkTopology t = Series();
                SizingResult r = Size(t, new[] { 100, 100, 100 });
                // The far unit (20000, 0): 20000 less two refnet bodies of 930, passing two refnets.
                Check.Near("pipe", 20000 - 2 * 930, r.LongestPipe, 1e-6);
                Check.Near("equivalent", 20000 - 2 * 930 + 2 * 500, r.LongestEquivalent, 1e-6);
                Check.Equal("the far unit", 0, r.LongestUnit);
            });
        }

        // ------------------------------------------------------------------ labels

        private static void LabelPlacement()
        {
            Check.Case("size label placement", () =>
            {
                SizeLabelPlacement p;
                Check.True("horizontal", SizeLabels.TryPlace(
                    new List<Vec2> { V(0, 0), V(4000, 0) }, 150, "%%C15.9/9.5", out p));
                Check.True("at the middle, 150 above", Near(V(2000, 150), p.Position));
                Check.Near("rotation 0", 0, p.Rotation, 1e-12);
                Check.Equal("text", "%%C15.9/9.5", p.Text);

                Check.True("horizontal, drawn the other way", SizeLabels.TryPlace(
                    new List<Vec2> { V(4000, 0), V(0, 0) }, 150, "t", out p));
                Check.Near("rotation stays readable", 0, p.Rotation, 1e-12);
                Check.True("still above the pipe", Near(V(2000, 150), p.Position));

                Check.True("vertical up", SizeLabels.TryPlace(
                    new List<Vec2> { V(0, 0), V(0, 4000) }, 150, "t", out p));
                Check.Near("rotation 90", Math.PI / 2, p.Rotation, 1e-12);
                Check.True("150 to the left of the pipe", Near(V(-150, 2000), p.Position));

                Check.True("vertical down", SizeLabels.TryPlace(
                    new List<Vec2> { V(0, 4000), V(0, 0) }, 150, "t", out p));
                Check.Near("rotation 90, not -90", Math.PI / 2, p.Rotation, 1e-12);
                Check.True("same place", Near(V(-150, 2000), p.Position));

                Check.True("45 degrees", SizeLabels.TryPlace(
                    new List<Vec2> { V(0, 0), V(1000, 1000) }, 100, "t", out p));
                Check.Near("rotation 45", Math.PI / 4, p.Rotation, 1e-12);
                Check.True("offset to the left of the reading direction",
                    Near(V(500 - 100 * Math.Sqrt(0.5), 500 + 100 * Math.Sqrt(0.5)), p.Position, 1e-9));

                Check.True("135 degrees reads the other way", SizeLabels.TryPlace(
                    new List<Vec2> { V(1000, 0), V(0, 1000) }, 100, "t", out p));
                Check.Near("rotation -45", -Math.PI / 4, p.Rotation, 1e-12);

                Check.True("the longest straight piece", SizeLabels.TryPlace(
                    new List<Vec2> { V(0, 0), V(1000, 0), V(1000, 5000), V(3000, 5000) }, 150, "t", out p));
                Check.True("is the vertical one", Near(V(850, 2500), p.Position));
                Check.Near("rotated 90", Math.PI / 2, p.Rotation, 1e-12);

                Check.True("the height sets the offset", SizeLabels.TryPlace(
                    new List<Vec2> { V(0, 0), V(4000, 0) }, 200, "t", out p));
                Check.True("200 above", Near(V(2000, 200), p.Position));

                Check.False("one point", SizeLabels.TryPlace(new List<Vec2> { V(0, 0) }, 150, "t", out p));
                Check.False("no length", SizeLabels.TryPlace(
                    new List<Vec2> { V(5, 5), V(5, 5) }, 150, "t", out p));
            });
        }
    }
}
