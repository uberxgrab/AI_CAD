using System;
using System.Collections.Generic;
using System.Linq;
using HvacAiCadAddin.Takeoff;

namespace HvacQtoCoreTests
{
    /// <summary>Specification section 3: duct detection, sizes, area and length.</summary>
    internal static class DuctTests
    {
        public static void Run()
        {
            RectangularNames();
            RoundNames();
            RejectedNames();
            Distance3Rule();
            AreaAndLength();
            AppendixMerging();
        }

        private static void Size(string blockName, string text, bool isRound, double decidingSize, double perimeter)
        {
            string name = "s3 size `" + blockName + "`";
            Check.Case(name, () =>
            {
                DuctSize size;
                bool parsed = TakeoffClassifier.TryParseDuctSize(blockName, out size);
                Check.True(name + " parsed", parsed);
                if (!parsed)
                    return;
                Check.Equal(name + " text", text, size.Text);
                Check.Equal(name + " round", isRound, size.IsRound);
                Check.Near(name + " deciding size", decidingSize, size.DecidingSize);
                Check.Near(name + " perimeter", perimeter, size.PerimeterMm, 1e-6);
            });
        }

        private static void NoSize(string blockName)
        {
            string name = "s3 not a size `" + blockName + "`";
            Check.Case(name, () =>
            {
                DuctSize size;
                Check.False(name, TakeoffClassifier.TryParseDuctSize(blockName, out size));
            });
        }

        private static void RectangularNames()
        {
            Size("Duct 600x300", "600 x 300 mm", false, 600, 1800);
            Size("Duct 600X300", "600 x 300 mm", false, 600, 1800);
            Size("Duct 600×300", "600 x 300 mm", false, 600, 1800);
            Size("Duct 600*300", "600 x 300 mm", false, 600, 1800);
            Size("600x300", "600 x 300 mm", false, 600, 1800);
            Size("Duct 300x600", "300 x 600 mm", false, 600, 1800);
            Size("Duct 1000x1000", "1000 x 1000 mm", false, 1000, 4000);
        }

        private static void RoundNames()
        {
            double pi = Math.PI;
            Size("D250", "Ø250 mm", true, 250, pi * 250);
            Size("DN250", "Ø250 mm", true, 250, pi * 250);
            Size("Ø250", "Ø250 mm", true, 250, pi * 250);
            Size("Duct D250", "Ø250 mm", true, 250, pi * 250);
            Size("Duct DN250", "Ø250 mm", true, 250, pi * 250);
            Size("Duct Ø250", "Ø250 mm", true, 250, pi * 250);
            Size("Round duct-D315", "Ø315 mm", true, 315, pi * 315);
        }

        private static void RejectedNames()
        {
            NoSize("Sa box-500x400x500");
            NoSize("500x400x500");
            NoSize("Duct 600x");
            NoSize("Duct x300");

            // A bare number is not a size.
            NoSize("FCU-2.8 kW");
            NoSize("ODU-6 HP");
            NoSize("250");
            NoSize("Duct 250");
            NoSize("AHU-01");
            NoSize("IDU-160000 BTUH");
            NoSize("TRANE_Celing ducted FCU_120000 BTUH");
            NoSize("FCU-CAS-16 kW");
            NoSize("Water Cooled Chiller - 420 RT");
        }

        private static void Distance3Rule()
        {
            NotDuct("s3 no Distance3", new TakeoffBlockInput("Duct 600x300"));
            NotDuct("s3 zero Distance3", new TakeoffBlockInput("Duct 600x300") { Distance3 = 0 });
            NotDuct("s3 negative Distance3", new TakeoffBlockInput("Duct 600x300") { Distance3 = -1 });
            NotDuct("s3 negative Distance3 round", new TakeoffBlockInput("Duct D250") { Distance3 = -250 });
            NotDuct("s3 FCU with Distance3", new TakeoffBlockInput("FCU-2.8 kW") { Distance3 = 2000 });
            NotDuct("s3 ODU with Distance3", new TakeoffBlockInput("ODU-6 HP") { Distance3 = 2000 });
            NotDuct("s3 three dimensions with Distance3", new TakeoffBlockInput("Sa box-500x400x500") { Distance3 = 2000 });

            Check.Case("s3 positive Distance3 is a duct", () =>
            {
                DuctSize size;
                Check.True("s3 rectangular duct", TakeoffClassifier.TryReadDuct(
                    new TakeoffBlockInput("Duct 600x300") { Distance3 = 0.5 }, out size));
                Check.True("s3 round duct", TakeoffClassifier.TryReadDuct(
                    new TakeoffBlockInput("Duct D250") { Distance3 = 2000 }, out size));
            });

            Check.Case("s3 Distance3 is not read from dynamic properties", () =>
            {
                TakeoffBlockInput block = new TakeoffBlockInput("Duct 600x300").AddDynamicProperty("Distance3", "2000");
                DuctSize size;
                Check.False("s3 Distance3 field required", TakeoffClassifier.TryReadDuct(block, out size));
            });

            Check.Case("s3 rejected ducts fall through to equipment", () =>
            {
                var blocks = new List<TakeoffBlockInput>
                {
                    new TakeoffBlockInput("FCU-2.8 kW") { Distance3 = 2000 },
                    new TakeoffBlockInput("ODU-6 HP") { Distance3 = 2000 },
                    new TakeoffBlockInput("Duct 600x300") { Distance3 = 0 },
                };
                TakeoffResult result = TakeoffAggregator.Aggregate(blocks, new List<TakeoffCurveInput>(),
                    DuctGaugeSettings.CreateDefault());
                Check.Equal("s3 no duct rows", 0, result.DuctSizeRows.Count);
                Check.Sequence("s3 equipment rows",
                    new[] { "Dàn nóng ODU 6 HP", "Duct 600x300", "FCU-2.8 kW" },
                    result.EquipmentRows.Select(row => row.Description));
            });
        }

        private static void NotDuct(string name, TakeoffBlockInput block)
        {
            Check.Case(name, () =>
            {
                DuctSize size;
                Check.False(name, TakeoffClassifier.TryReadDuct(block, out size));
            });
        }

        private static TakeoffResult Ducts(params TakeoffBlockInput[] blocks)
        {
            return TakeoffAggregator.Aggregate(blocks, new List<TakeoffCurveInput>(), DuctGaugeSettings.CreateDefault());
        }

        private static void AreaAndLength()
        {
            Check.Case("s3 rectangular arithmetic", () =>
            {
                TakeoffResult result = Ducts(new TakeoffBlockInput("Duct 600x300") { Distance3 = 2000 });
                Check.Equal("s3 rectangular rows", 1, result.DuctSizeRows.Count);
                DuctSizeRow row = result.DuctSizeRows[0];
                Check.Equal("s3 rectangular size text", "600 x 300 mm", row.SizeText);
                Check.Equal("s3 rectangular count", 1, row.Count);
                Check.Near("s3 rectangular deciding size", 600, row.DecidingSize);
                Check.Near("s3 rectangular length m", 2, row.LengthMeters);
                Check.Near("s3 rectangular 2 x (W + H) x L", 3.6, row.AreaSquareMeters);
                Check.Equal("s3 duct is not equipment", 0, result.EquipmentRows.Count);
            });

            Check.Case("s3 round arithmetic", () =>
            {
                TakeoffResult result = Ducts(new TakeoffBlockInput("Duct D250") { Distance3 = 2000 });
                DuctSizeRow row = result.DuctSizeRows[0];
                Check.Equal("s3 round size text", "Ø250 mm", row.SizeText);
                Check.Near("s3 round length m", 2, row.LengthMeters);
                Check.Near("s3 round pi x D x L", Math.PI * 250 * 2000 / 1000000.0, row.AreaSquareMeters, 1e-9);
            });
        }

        private static void AppendixMerging()
        {
            Check.Case("s3 one row per size text", () =>
            {
                TakeoffResult result = Ducts(
                    new TakeoffBlockInput("Duct 600x300") { Distance3 = 2000 },
                    new TakeoffBlockInput("SA 600X300") { Distance3 = 1000 },
                    new TakeoffBlockInput("EA-600×300") { Distance3 = 500 },
                    new TakeoffBlockInput("Duct D250") { Distance3 = 1000 },
                    new TakeoffBlockInput("Round DN250") { Distance3 = 1000 });

                Check.Equal("s3 merged across block names", 2, result.DuctSizeRows.Count);
                DuctSizeRow rectangular = result.DuctSizeRows.First(row => row.SizeText == "600 x 300 mm");
                Check.Equal("s3 merged count", 3, rectangular.Count);
                Check.Near("s3 merged length", 3.5, rectangular.LengthMeters);
                Check.Near("s3 merged area", 6.3, rectangular.AreaSquareMeters);
                DuctSizeRow round = result.DuctSizeRows.First(row => row.SizeText == "Ø250 mm");
                Check.Equal("s3 merged round count", 2, round.Count);
            });
        }
    }
}
