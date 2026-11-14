using System.Collections.Generic;
using System.Linq;
using HvacAiCadAddin.Takeoff;

namespace HvacQtoCoreTests
{
    /// <summary>Specification section 3: gauge bands, totals and validation.</summary>
    internal static class GaugeTests
    {
        public static void Run()
        {
            Defaults();
            BandBoundaries();
            BelowFirstBand();
            TotalsMatch();
            CustomSettings();
            Validation();
            Parsing();
        }

        private static void Defaults()
        {
            Check.Case("s3 default bands", () =>
            {
                Check.Equal("s3 BandCount", 4, DuctGaugeSettings.BandCount);
                DuctGaugeSettings settings = DuctGaugeSettings.CreateDefault();
                Check.Sequence("s3 default From", new[] { 100.0, 600, 1250, 2000 }, settings.Bands.Select(band => band.From));
                Check.Sequence("s3 default To", new double?[] { 550, 1200, 1950, null }, settings.Bands.Select(band => band.To));
                Check.Sequence("s3 default thickness", new[] { 0.58, 0.75, 0.95, 1.15 }, settings.Bands.Select(band => band.Thickness));
                string error;
                Check.True("s3 defaults are valid", settings.TryValidate(out error), error);
            });
        }

        private static void BandBoundaries()
        {
            var cases = new[]
            {
                new { Size = 99.0, Band = 0, Below = true },
                new { Size = 100.0, Band = 0, Below = false },
                new { Size = 550.0, Band = 0, Below = false },
                new { Size = 551.0, Band = 1, Below = false },
                new { Size = 575.0, Band = 1, Below = false },
                new { Size = 599.0, Band = 1, Below = false },
                new { Size = 600.0, Band = 1, Below = false },
                new { Size = 1200.0, Band = 1, Below = false },
                new { Size = 1225.0, Band = 2, Below = false },
                new { Size = 1250.0, Band = 2, Below = false },
                new { Size = 1950.0, Band = 2, Below = false },
                new { Size = 1975.0, Band = 3, Below = false },
                new { Size = 2000.0, Band = 3, Below = false },
                new { Size = 5000.0, Band = 3, Below = false },
            };

            foreach (var item in cases)
            {
                string name = "s3 band for size " + item.Size.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Check.Case(name, () =>
                {
                    DuctGaugeSettings settings = DuctGaugeSettings.CreateDefault();
                    Check.Equal(name, item.Band, settings.FindBandIndex(item.Size));
                    Check.Equal(name + " below first band", item.Below, settings.IsBelowFirstBand(item.Size));
                });
            }

            // The same rule seen through the area totals, using a 1 m round duct per size.
            Check.Case("s3 gap sizes reach the next thicker row", () =>
            {
                TakeoffResult result = Ducts(DuctGaugeSettings.CreateDefault(),
                    new TakeoffBlockInput("A 575x100") { Distance3 = 1000 },
                    new TakeoffBlockInput("B 1225x100") { Distance3 = 1000 },
                    new TakeoffBlockInput("C 1975x100") { Distance3 = 1000 });
                Check.Near("s3 gap band 0.58 empty", 0, result.DuctBandRows[0].AreaSquareMeters);
                Check.Near("s3 575 in 0.75", 2 * 675 * 1000 / 1000000.0, result.DuctBandRows[1].AreaSquareMeters);
                Check.Near("s3 1225 in 0.95", 2 * 1325 * 1000 / 1000000.0, result.DuctBandRows[2].AreaSquareMeters);
                Check.Near("s3 1975 in 1.15", 2 * 2075 * 1000 / 1000000.0, result.DuctBandRows[3].AreaSquareMeters);
            });

            Check.Case("s3 deciding size is the largest side", () =>
            {
                TakeoffResult result = Ducts(DuctGaugeSettings.CreateDefault(),
                    new TakeoffBlockInput("Duct 300x1300") { Distance3 = 1000 });
                Check.Near("s3 300x1300 goes to 0.95", 3.2, result.DuctBandRows[2].AreaSquareMeters);
            });
        }

        private static void BelowFirstBand()
        {
            Check.Case("s3 below first band", () =>
            {
                TakeoffResult result = Ducts(DuctGaugeSettings.CreateDefault(),
                    new TakeoffBlockInput("Duct 80x50") { Distance3 = 1000 },
                    new TakeoffBlockInput("Other 80x50") { Distance3 = 1000 },
                    new TakeoffBlockInput("Duct D99") { Distance3 = 1000 },
                    new TakeoffBlockInput("Duct D100") { Distance3 = 1000 });

                Check.Equal("s3 warning counts ducts", 3, result.DuctsBelowFirstBandCount);
                double expected = 2 * 0.26 + System.Math.PI * 99 * 1000 / 1000000.0 + System.Math.PI * 100 * 1000 / 1000000.0;
                Check.Near("s3 below-band area stays in first band", expected, result.DuctBandRows[0].AreaSquareMeters);
            });
        }

        private static void TotalsMatch()
        {
            Check.Case("s3 band total equals appendix total", () =>
            {
                TakeoffResult result = Ducts(DuctGaugeSettings.CreateDefault(),
                    new TakeoffBlockInput("Duct 80x50") { Distance3 = 1000 },
                    new TakeoffBlockInput("Duct 550x200") { Distance3 = 1234 },
                    new TakeoffBlockInput("Duct 575x200") { Distance3 = 1000 },
                    new TakeoffBlockInput("Duct 1225x200") { Distance3 = 777 },
                    new TakeoffBlockInput("Duct 1975x200") { Distance3 = 999 },
                    new TakeoffBlockInput("Duct 2600x900") { Distance3 = 3000 },
                    new TakeoffBlockInput("Duct D250") { Distance3 = 1500 },
                    new TakeoffBlockInput("Duct D1600") { Distance3 = 2500 });

                Check.Near("s3 totals match",
                    result.DuctSizeRows.Sum(row => row.AreaSquareMeters),
                    result.DuctBandRows.Sum(row => row.AreaSquareMeters), 1e-9);
            });
        }

        private static void CustomSettings()
        {
            Check.Case("s3 band rows use the settings", () =>
            {
                TakeoffResult defaults = Ducts(DuctGaugeSettings.CreateDefault());
                Check.Sequence("s3 default row text",
                    new[]
                    {
                        "Ống gió tôn mạ kẽm 0.58mm",
                        "Ống gió tôn mạ kẽm 0.75mm",
                        "Ống gió tôn mạ kẽm 0.95mm",
                        "Ống gió tôn mạ kẽm 1.15mm",
                    },
                    defaults.DuctBandRows.Select(row => row.Description));
                Check.Equal("s3 four rows without ducts", 4, defaults.DuctBandRows.Count);

                DuctGaugeSettings custom = Settings(B(100, 400, 0.5), B(500, 900, 0.6), B(1000, 1500, 0.8), B(1600, null, 1.2));
                TakeoffResult result = Ducts(custom, new TakeoffBlockInput("Duct 450x100") { Distance3 = 1000 });
                Check.Sequence("s3 custom row text",
                    new[]
                    {
                        "Ống gió tôn mạ kẽm 0.5mm",
                        "Ống gió tôn mạ kẽm 0.6mm",
                        "Ống gió tôn mạ kẽm 0.8mm",
                        "Ống gió tôn mạ kẽm 1.2mm",
                    },
                    result.DuctBandRows.Select(row => row.Description));
                Check.Sequence("s3 custom thickness", new[] { 0.5, 0.6, 0.8, 1.2 }, result.DuctBandRows.Select(row => row.Thickness));
                Check.Near("s3 custom gap goes to next band", 1.1, result.DuctBandRows[1].AreaSquareMeters);
            });

            Check.Case("s3 custom bands keep the band rule", () =>
            {
                DuctGaugeSettings custom = Settings(B(200, 400, 0.5), B(500, 900, 0.6), B(1000, 1500, 0.8), B(1600, null, 1));
                Check.True("s3 custom below first band", custom.IsBelowFirstBand(150));
                Check.False("s3 custom at first From", custom.IsBelowFirstBand(200));
                Check.Equal("s3 custom band index 400", 0, custom.FindBandIndex(400));
                Check.Equal("s3 custom band index 450", 1, custom.FindBandIndex(450));
                Check.Equal("s3 custom band index 1550", 3, custom.FindBandIndex(1550));
            });
        }

        private static void Validation()
        {
            Valid("s3 valid custom", B(100, 400, 0.5), B(500, 900, 0.6), B(1000, 1500, 0.8), B(1600, null, 1));
            Valid("s3 thickness order is not validated", B(100, 400, 2), B(500, 900, 1), B(1000, 1500, 0.8), B(1600, null, 0.5));
            Valid("s3 decimal limits", B(100.5, 400.5, 0.5), B(400.6, 900, 0.6), B(1000, 1500, 0.8), B(1600, null, 1));

            Invalid("s3 From equals To", B(550, 550, 0.58), B(600, 1200, 0.75), B(1250, 1950, 0.95), B(2000, null, 1.15));
            Invalid("s3 From above To", B(560, 550, 0.58), B(600, 1200, 0.75), B(1250, 1950, 0.95), B(2000, null, 1.15));
            Invalid("s3 From above To in row 3", B(100, 550, 0.58), B(600, 1200, 0.75), B(1950, 1950, 0.95), B(2000, null, 1.15));
            Invalid("s3 next From equals previous To", B(100, 550, 0.58), B(550, 1200, 0.75), B(1250, 1950, 0.95), B(2000, null, 1.15));
            Invalid("s3 next From below previous To", B(100, 550, 0.58), B(500, 1200, 0.75), B(1250, 1950, 0.95), B(2000, null, 1.15));
            Invalid("s3 last From equals previous To", B(100, 550, 0.58), B(600, 1200, 0.75), B(1250, 1950, 0.95), B(1950, null, 1.15));
            Invalid("s3 first From zero", B(0, 550, 0.58), B(600, 1200, 0.75), B(1250, 1950, 0.95), B(2000, null, 1.15));
            Invalid("s3 first From negative", B(-100, 550, 0.58), B(600, 1200, 0.75), B(1250, 1950, 0.95), B(2000, null, 1.15));
            Invalid("s3 thickness zero", B(100, 550, 0), B(600, 1200, 0.75), B(1250, 1950, 0.95), B(2000, null, 1.15));
            Invalid("s3 thickness negative", B(100, 550, 0.58), B(600, 1200, -0.75), B(1250, 1950, 0.95), B(2000, null, 1.15));
            Invalid("s3 last thickness zero", B(100, 550, 0.58), B(600, 1200, 0.75), B(1250, 1950, 0.95), B(2000, null, 0));
            Invalid("s3 last row has a To", B(100, 550, 0.58), B(600, 1200, 0.75), B(1250, 1950, 0.95), B(2000, 3000, 1.15));
            Invalid("s3 middle row missing To", B(100, 550, 0.58), B(600, null, 0.75), B(1250, 1950, 0.95), B(2000, null, 1.15));
            Invalid("s3 first row missing To", B(100, null, 0.58), B(600, 1200, 0.75), B(1250, 1950, 0.95), B(2000, null, 1.15));
            Invalid("s3 three rows", B(100, 550, 0.58), B(600, 1200, 0.75), B(1250, null, 0.95));
            Invalid("s3 five rows", B(100, 550, 0.58), B(600, 1200, 0.75), B(1250, 1950, 0.95), B(2000, 2500, 1.15), B(2600, null, 1.3));
        }

        private static void Parsing()
        {
            Check.Case("s3 parse dot decimals", () =>
            {
                DuctGaugeSettings settings;
                string error;
                bool parsed = DuctGaugeSettings.TryParse(Rows("100", "550", "0.58", "600", "1200", "0.75", "1250", "1950", "0.95", "2000", "", "1.15"),
                    out settings, out error);
                Check.True("s3 parse dot accepted", parsed, error);
                Check.Sequence("s3 parse dot thickness", new[] { 0.58, 0.75, 0.95, 1.15 }, settings.Bands.Select(band => band.Thickness));
                Check.Equal("s3 parse last To empty", null, settings.Bands[3].To);
            });

            Check.Case("s3 parse comma decimals", () =>
            {
                DuctGaugeSettings settings;
                string error;
                bool parsed = DuctGaugeSettings.TryParse(Rows("100,5", "550,5", "0,58", "600", "1200", "0,75", "1250", "1950", "0,95", "2000", "", "1,15"),
                    out settings, out error);
                Check.True("s3 parse comma accepted", parsed, error);
                Check.Near("s3 parse comma From", 100.5, settings.Bands[0].From);
                Check.Near("s3 parse comma To", 550.5, settings.Bands[0].To.Value);
                Check.Sequence("s3 parse comma thickness", new[] { 0.58, 0.75, 0.95, 1.15 }, settings.Bands.Select(band => band.Thickness));
            });

            Check.Case("s3 parse null last To", () =>
            {
                DuctGaugeSettings settings;
                string error;
                Check.True("s3 parse null To accepted",
                    DuctGaugeSettings.TryParse(Rows("100", "550", "0.58", "600", "1200", "0.75", "1250", "1950", "0.95", "2000", null, "1.15"),
                        out settings, out error), error);
            });

            Unparsable("s3 parse letters", Rows("abc", "550", "0.58", "600", "1200", "0.75", "1250", "1950", "0.95", "2000", "", "1.15"));
            Unparsable("s3 parse empty From", Rows("", "550", "0.58", "600", "1200", "0.75", "1250", "1950", "0.95", "2000", "", "1.15"));
            Unparsable("s3 parse empty middle To", Rows("100", "", "0.58", "600", "1200", "0.75", "1250", "1950", "0.95", "2000", "", "1.15"));
            Unparsable("s3 parse empty thickness", Rows("100", "550", "", "600", "1200", "0.75", "1250", "1950", "0.95", "2000", "", "1.15"));
            Unparsable("s3 parse last row To", Rows("100", "550", "0.58", "600", "1200", "0.75", "1250", "1950", "0.95", "2000", "3000", "1.15"));
            Unparsable("s3 parse both separators", Rows("100", "1,200.5", "0.58", "1300", "1400", "0.75", "1500", "1950", "0.95", "2000", "", "1.15"));
            Unparsable("s3 parse infinite thickness", Rows("100", "550", "0.58", "600", "1200", "0.75", "1250", "1950", "0.95", "2000", "", "Infinity"));
            Unparsable("s3 parse NaN", Rows("100", "550", "NaN", "600", "1200", "0.75", "1250", "1950", "0.95", "2000", "", "1.15"));
            Unparsable("s3 parse invalid order", Rows("600", "550", "0.58", "600", "1200", "0.75", "1250", "1950", "0.95", "2000", "", "1.15"));
            Unparsable("s3 parse three rows", new List<string[]>
            {
                new[] { "100", "550", "0.58" },
                new[] { "600", "1200", "0.75" },
                new[] { "1250", "", "0.95" },
            });
            Unparsable("s3 parse short row", new List<string[]>
            {
                new[] { "100", "550" },
                new[] { "600", "1200", "0.75" },
                new[] { "1250", "1950", "0.95" },
                new[] { "2000", "", "1.15" },
            });
        }

        private static void Unparsable(string name, IList<string[]> rows)
        {
            Check.Case(name, () =>
            {
                DuctGaugeSettings settings;
                string error;
                Check.False(name, DuctGaugeSettings.TryParse(rows, out settings, out error));
                Check.True(name + " has a reason", !string.IsNullOrWhiteSpace(error), Check.Show(error));
            });
        }

        private static void Valid(string name, params DuctGaugeBand[] bands)
        {
            Check.Case(name, () =>
            {
                string error;
                Check.True(name, new DuctGaugeSettings(bands).TryValidate(out error), error);
            });
        }

        private static void Invalid(string name, params DuctGaugeBand[] bands)
        {
            Check.Case(name, () =>
            {
                string error;
                Check.False(name, new DuctGaugeSettings(bands).TryValidate(out error));
                Check.True(name + " has a reason", !string.IsNullOrWhiteSpace(error), Check.Show(error));
            });
        }

        private static IList<string[]> Rows(params string[] cells)
        {
            var rows = new List<string[]>();
            for (int index = 0; index < cells.Length; index += 3)
                rows.Add(new[] { cells[index], cells[index + 1], cells[index + 2] });
            return rows;
        }

        private static DuctGaugeBand B(double from, double? to, double thickness)
        {
            return new DuctGaugeBand(from, to, thickness);
        }

        private static DuctGaugeSettings Settings(params DuctGaugeBand[] bands)
        {
            return new DuctGaugeSettings(bands);
        }

        private static TakeoffResult Ducts(DuctGaugeSettings settings, params TakeoffBlockInput[] blocks)
        {
            return TakeoffAggregator.Aggregate(blocks, new List<TakeoffCurveInput>(), settings);
        }
    }
}
