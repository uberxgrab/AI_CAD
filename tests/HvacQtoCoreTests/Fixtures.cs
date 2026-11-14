using System.Collections.Generic;
using System.IO;
using HvacAiCadAddin.Takeoff;

namespace HvacQtoCoreTests
{
    internal static class Fixtures
    {
        public const string Label = "HVAC-NAME OF EQUIPMENTS";

        public static TakeoffResult Full()
        {
            var blocks = new List<TakeoffBlockInput>
            {
                new TakeoffBlockInput("ODU-10 HP"),
                new TakeoffBlockInput("ODU-6 HP"),
                new TakeoffBlockInput("ODU-6 HP"),
                new TakeoffBlockInput("FCU-CAS-16 kW"),
                new TakeoffBlockInput("FCU-CAS-16 kW"),
                new TakeoffBlockInput("FCU-CAS-16 kW"),
                new TakeoffBlockInput("Water Cooled Chiller - 420 RT"),
                new TakeoffBlockInput(Label).AddAttribute("TITLE", "CHWP-01").AddAttribute("PROPERTIES", "40mH20 - 10050 LPM"),
                new TakeoffBlockInput(Label).AddAttribute("TITLE", "CHWP-02").AddAttribute("PROPERTIES", "40mH20 - 10050 LPM"),
                new TakeoffBlockInput("HVAC_PUMP"),
                new TakeoffBlockInput("HVAC_PUMP"),
                new TakeoffBlockInput("AHU-01").AddAttribute("TAG", "A1"),
                new TakeoffBlockInput("FCU-2.2 kW"),
                new TakeoffBlockInput("Duct 600x300") { Distance3 = 2000 },
                new TakeoffBlockInput("Duct 1000x500") { Distance3 = 3000 },
                new TakeoffBlockInput("Duct D250") { Distance3 = 1500 },
                new TakeoffBlockInput("Duct 2000x800") { Distance3 = 1000 },
                new TakeoffBlockInput("Duct 80x50") { Distance3 = 1000 },
            };
            var curves = new List<TakeoffCurveInput>
            {
                new TakeoffCurveInput("EIA_1 RP 15.9/9.5", 12000),
                new TakeoffCurveInput("EIA_1 Pipe condensated DN27", 5000),
                new TakeoffCurveInput("EIA_1 Pipe condensated", 1000),
                new TakeoffCurveInput("EIA_1 CHWR DN100", 12000),
                new TakeoffCurveInput("EIA_1 CHWS DN15", 1000),
                new TakeoffCurveInput("HWS MAIN", 2500),
                new TakeoffCurveInput("S-DIM", 999),
            };
            return TakeoffAggregator.Aggregate(blocks, curves, DuctGaugeSettings.CreateDefault());
        }

        public static TakeoffResult Empty()
        {
            return TakeoffAggregator.Aggregate(new List<TakeoffBlockInput>(), new List<TakeoffCurveInput>(),
                DuctGaugeSettings.CreateDefault());
        }

        public static TakeoffResult SteelOnly()
        {
            return TakeoffAggregator.Aggregate(new List<TakeoffBlockInput>(),
                new List<TakeoffCurveInput> { new TakeoffCurveInput("EIA_1 CHWR DN100", 12000) },
                DuctGaugeSettings.CreateDefault());
        }

        public static TakeoffResult CustomGauge()
        {
            var settings = new DuctGaugeSettings(new[]
            {
                new DuctGaugeBand(100, 400, 0.5),
                new DuctGaugeBand(500, 900, 0.6),
                new DuctGaugeBand(1000, 1500, 0.8),
                new DuctGaugeBand(1600, null, 1.2),
            });
            return TakeoffAggregator.Aggregate(
                new List<TakeoffBlockInput> { new TakeoffBlockInput("Duct 450x100") { Distance3 = 1000 } },
                new List<TakeoffCurveInput>(), settings);
        }

        public static TakeoffResult RowHeights(IEnumerable<string> blockNames)
        {
            var blocks = new List<TakeoffBlockInput>();
            foreach (string name in blockNames)
                blocks.Add(new TakeoffBlockInput(name));
            return TakeoffAggregator.Aggregate(blocks, new List<TakeoffCurveInput>(), DuctGaugeSettings.CreateDefault());
        }

        public static void Emit(string directory)
        {
            TakeoffWorkbookWriter.Write(Path.Combine(directory, "full.xml"), Full());
            TakeoffWorkbookWriter.Write(Path.Combine(directory, "empty.xml"), Empty());
            TakeoffWorkbookWriter.Write(Path.Combine(directory, "custom.xml"), CustomGauge());
            TakeoffWorkbookWriter.Write(Path.Combine(directory, "formulas.xml"), Formulas());
            TakeoffWorkbookWriter.Write(Path.Combine(directory, "long.xml"), LongFormula());
        }

        /// <summary>Ducts, pipes and shaft risers with factor 1.1, for checking formulas in Excel.</summary>
        public static TakeoffResult Formulas()
        {
            return TakeoffAggregator.Aggregate(
                new[]
                {
                    new TakeoffBlockInput("ODU-6 HP"),
                    new TakeoffBlockInput("Duct 600x300") { Distance3 = 2000 },
                    new TakeoffBlockInput("Duct 600x300") { Distance3 = 1500 },
                    new TakeoffBlockInput("Duct D250") { Distance3 = 4000 }
                },
                new[]
                {
                    new TakeoffCurveInput("EIA_1 CHWS DN100", 12345),
                    new TakeoffCurveInput("EIA_1 CHWS DN100", 3200),
                    new TakeoffCurveInput("EIA_1 RP 15.9-9.5", 5000)
                },
                new TakeoffOptions(DuctGaugeSettings.CreateDefault(), 1.1, new[]
                {
                    new ShaftPipeInput(ShaftPipeKind.Steel, "DN100", 3.5, 2),
                    new ShaftPipeInput(ShaftPipeKind.Duct, "600x300", 3, 1)
                }));
        }

        /// <summary>1500 segments on one layer, so that row moves to the detail sheet.</summary>
        public static TakeoffResult LongFormula()
        {
            var curves = new List<TakeoffCurveInput>();
            for (int index = 0; index < 1500; index++)
                curves.Add(new TakeoffCurveInput("EIA_1 CHWS DN50", 1234.567));
            return TakeoffAggregator.Aggregate(null, curves, new TakeoffOptions(DuctGaugeSettings.CreateDefault(), 1.0,
                new[] { new ShaftPipeInput(ShaftPipeKind.Steel, "DN50", 3, 2) }));
        }
    }
}
