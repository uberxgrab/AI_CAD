using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HvacAiCadAddin.Takeoff;

namespace HvacQtoCoreTests
{
    /// <summary>
    /// Formulas, shaft risers and the contingency factor, approved on 2026-11-14:
    /// quantities are written as l1+l2+..., shaft runs add height*count terms, the factor lives in F6
    /// of the main sheet and multiplies duct and pipe quantities, and a formula longer than Excel's
    /// limit moves its segments to a detail sheet.
    /// </summary>
    internal static class FormulaTests
    {
        private const string Steel100 = "Ống thép hàn mạ kẽm DN100, SCH40";
        private const string Steel150 = "Ống thép hàn mạ kẽm DN150, SCH40";
        private const string Upvc34 = "Ống thoát nước ngưng uPVC Class 1 D34, kèm bảo ôn 13mm";

        public static void Run(string directory)
        {
            Check.Case("f options defaults", OptionDefaults);
            Check.Case("f options parsing", OptionParsing);
            Check.Case("f options remembered", OptionSerialization);
            Check.Case("f shaft aggregation", ShaftAggregation);
            Check.Case("f workbook formulas", () => WorkbookFormulas(directory));
            Check.Case("f long formula moves to detail sheet", () => LongFormula(directory));
        }

        private static TakeoffOptions Options(double factor, params ShaftPipeInput[] shafts)
        {
            return new TakeoffOptions(DuctGaugeSettings.CreateDefault(), factor, shafts);
        }

        private static List<string[]> DefaultGaugeRows()
        {
            return new List<string[]>
            {
                new[] { "100", "550", "0.58" },
                new[] { "600", "1200", "0.75" },
                new[] { "1250", "1950", "0.95" },
                new[] { "2000", "", "1.15" }
            };
        }

        private static bool Parse(string factor, List<string[]> shafts, out TakeoffOptions options, out string error)
        {
            return TakeoffOptions.TryParse(factor, DefaultGaugeRows(), shafts, out options, out error);
        }

        private static void OptionDefaults()
        {
            TakeoffOptions options = TakeoffOptions.CreateDefault();
            Check.Near("f default factor is 1.0", 1.0, options.ContingencyFactor);
            Check.Equal("f default has no shaft rows", 0, options.ShaftPipes.Count);
            Check.Sequence("f shaft kinds offered", new[] { "Ống gió", "Ống thép", "Ống nước ngưng", "Ống PPR", "Ống HDPE", "Ống uPVC thoát nước" },
                TakeoffOptions.ShaftKindNames);
        }

        private static void OptionParsing()
        {
            TakeoffOptions options;
            string error;
            Check.True("f parse valid", Parse("1,1", new List<string[]>
            {
                new[] { "Ống thép", "DN100", "3.5", "2" },
                new[] { "Ống gió", "600x300", "3", "" },
                new[] { "", "", "", "" },
                new[] { "Ống nước ngưng", "D34", "4,5", "1" }
            }, out options, out error), error);
            if (options != null)
            {
                Check.Near("f comma factor", 1.1, options.ContingencyFactor);
                Check.Equal("f blank shaft row skipped", 3, options.ShaftPipes.Count);
                Check.Equal("f shaft kind steel", ShaftPipeKind.Steel, options.ShaftPipes[0].Kind);
                Check.Equal("f blank count means 1", 1, options.ShaftPipes[1].Count);
                Check.Near("f comma height", 4.5, options.ShaftPipes[2].HeightMeters);
            }

            foreach (string factor in new[] { "0", "-1", "abc", "", null })
                Check.False("f factor rejected `" + factor + "`", Parse(factor, null, out options, out error));

            var invalidRows = new Dictionary<string, string[]>
            {
                { "no kind", new[] { "", "DN100", "3", "1" } },
                { "unknown kind", new[] { "Ống đồng", "DN100", "3", "1" } },
                { "duct size not WxH or D", new[] { "Ống gió", "DN", "3", "1" } },
                { "pipe size missing", new[] { "Ống thép", "", "3", "1" } },
                { "pipe size text", new[] { "Ống thép", "abc", "3", "1" } },
                { "height zero", new[] { "Ống thép", "DN100", "0", "1" } },
                { "height text", new[] { "Ống thép", "DN100", "x", "1" } },
                { "count zero", new[] { "Ống thép", "DN100", "3", "0" } },
                { "count decimal", new[] { "Ống thép", "DN100", "3", "1.5" } },
                { "count negative", new[] { "Ống thép", "DN100", "3", "-1" } }
            };
            foreach (KeyValuePair<string, string[]> row in invalidRows)
            {
                Check.False("f shaft rejected: " + row.Key,
                    Parse("1", new List<string[]> { row.Value }, out options, out error));
                Check.True("f shaft error names the row: " + row.Key, error != null && error.Contains("dòng 1"), error);
            }

            foreach (string size in new[] { "100", "DN100", "D100", "Ø100", "dn 100", "100mm" })
            {
                Check.True("f steel size accepted `" + size + "`",
                    Parse("1", new List<string[]> { new[] { "Ống thép", size, "3", "1" } }, out options, out error), error);
            }
        }

        private static void OptionSerialization()
        {
            TakeoffOptions original = new TakeoffOptions(
                new DuctGaugeSettings(new[]
                {
                    new DuctGaugeBand(100, 500, 0.6),
                    new DuctGaugeBand(550, 1000, 0.8),
                    new DuctGaugeBand(1050, 1800, 1.0),
                    new DuctGaugeBand(1850, null, 1.2)
                }),
                1.15,
                new[]
                {
                    new ShaftPipeInput(ShaftPipeKind.Steel, "DN100", 3.5, 2),
                    new ShaftPipeInput(ShaftPipeKind.Duct, "600x300", 12.25, 1)
                });

            TakeoffOptions restored = TakeoffOptions.Deserialize(original.Serialize());
            Check.Near("f remembered factor", 1.15, restored.ContingencyFactor);
            Check.Sequence("f remembered thicknesses", new[] { 0.6, 0.8, 1.0, 1.2 },
                restored.Gauge.Bands.Select(band => band.Thickness));
            Check.Equal("f remembered last To empty", false, restored.Gauge.Bands[3].To.HasValue);
            Check.Equal("f remembered shaft count", 2, restored.ShaftPipes.Count);
            Check.Equal("f remembered shaft kind", ShaftPipeKind.Duct, restored.ShaftPipes[1].Kind);
            Check.Equal("f remembered shaft size", "600x300", restored.ShaftPipes[1].Size);
            Check.Near("f remembered shaft height", 12.25, restored.ShaftPipes[1].HeightMeters);
            Check.Equal("f remembered shaft runs", 2, restored.ShaftPipes[0].Count);

            foreach (string text in new[] { null, "", "garbage", "factor=0\n", "factor=1\nband=1|2|3\n" })
            {
                TakeoffOptions fallback = TakeoffOptions.Deserialize(text);
                Check.Near("f invalid settings give default factor `" + text + "`", 1.0, fallback.ContingencyFactor);
                Check.Near("f invalid settings give default gauge `" + text + "`", 0.58, fallback.Gauge.Bands[0].Thickness);
            }
        }

        private static void ShaftAggregation()
        {
            TakeoffResult result = TakeoffAggregator.Aggregate(
                new[] { new TakeoffBlockInput("Duct 600x300") { Distance3 = 2000 } },
                new[] { new TakeoffCurveInput("EIA_1 CHWS DN100", 10000) },
                Options(1.2,
                    new ShaftPipeInput(ShaftPipeKind.Steel, "DN100", 3.5, 2),
                    new ShaftPipeInput(ShaftPipeKind.Steel, "150", 4, 1),
                    new ShaftPipeInput(ShaftPipeKind.Upvc, "DN34", 3, 3),
                    new ShaftPipeInput(ShaftPipeKind.Duct, "600x300", 3, 2),
                    new ShaftPipeInput(ShaftPipeKind.Duct, "D2200", 1, 1)));

            Check.Near("f result keeps factor", 1.2, result.ContingencyFactor);

            PipeRow steel100 = result.SteelRows.Single(row => row.Description == Steel100);
            Check.Near("f shaft added to drawn steel row", 10 + 7, steel100.LengthMeters);
            Check.Equal("f steel row keeps both terms", 2, steel100.Terms.Count);
            Check.True("f shaft term marked", steel100.Terms[1].FromShaft);
            Check.Equal("f shaft term runs", 2, steel100.Terms[1].Multiplier);

            PipeRow steel150 = result.SteelRows.Single(row => row.Description == Steel150);
            Check.Near("f shaft creates new steel row", 4, steel150.LengthMeters);
            Check.Near("f shaft uPVC printed as D", 9, result.UpvcRows.Single(row => row.Description == Upvc34).LengthMeters);
            Check.Equal("f shaft never makes copper", 0, result.CopperRows.Count);

            DuctSizeRow duct = result.DuctSizeRows.Single(row => row.SizeText == "600 x 300 mm");
            Check.Equal("f shaft duct count adds runs", 3, duct.Count);
            Check.Near("f shaft duct length", 2 + 6, duct.LengthMeters);
            Check.Near("f shaft duct area", 1.8 * 8, duct.AreaSquareMeters);
            Check.Near("f band area includes shaft", 1.8 * 8, result.DuctBandRows[1].AreaSquareMeters);
            Check.Near("f round shaft duct in last band", Math.PI * 2.2, result.DuctBandRows[3].AreaSquareMeters);
        }

        private static void WorkbookFormulas(string directory)
        {
            TakeoffResult result = TakeoffAggregator.Aggregate(
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
                Options(1.1,
                    new ShaftPipeInput(ShaftPipeKind.Steel, "DN100", 3.5, 2),
                    new ShaftPipeInput(ShaftPipeKind.Duct, "600x300", 3, 1)));

            string path = Path.Combine(directory, "formulas.xml");
            TakeoffWorkbookWriter.Write(path, result);
            WorkbookModel workbook = WorkbookModel.Load(path);
            SheetInfo main = workbook.Main;
            SheetInfo appendix = workbook.Appendix;

            Check.Equal("f factor cell value", "1.1", main[6].Text('F'));
            Check.Equal("f factor cell is plain input", null, main[6]['F'].Formula);

            RowInfo steel = Row(main, Steel100);
            Check.Equal("f steel formula", "=(12.345+3.2+3.5*2)*R6C6", steel['F'].Formula);
            Check.Near("f steel cached value", (12.345 + 3.2 + 7) * 1.1, Number(steel['F']), 1e-9);

            RowInfo copper = Row(main, "Ống đồng D15.9 dày 1.0mm, kèm bảo ôn 19mm");
            Check.Equal("f copper formula", "=(5)*R6C6", copper['F'].Formula);

            RowInfo odu = Row(main, "Dàn nóng ODU 6 HP");
            Check.Equal("f equipment has no formula", null, odu['F'].Formula);
            Check.Equal("f equipment count", "1", odu.Text('F'));

            // The appendix is ordered by deciding size, so Ø250 comes before 600 x 300.
            RowInfo rect = appendix[3];
            Check.Equal("f appendix first size", "600 x 300 mm", rect.Text('A'));
            Check.Equal("f appendix count includes shaft", "3", rect.Text('B'));
            Check.Equal("f appendix length formula", "=2+1.5+3*1", rect['C'].Formula);
            Check.Near("f appendix length value", 6.5, Number(rect['C']), 1e-9);
            Check.Equal("f appendix rectangular area formula", "=2*(0.6+0.3)*RC[-1]", rect['D'].Formula);
            Check.Near("f appendix rectangular area value", 1.8 * 6.5, Number(rect['D']), 1e-9);

            RowInfo round = appendix[2];
            Check.Equal("f appendix round size", "Ø250 mm", round.Text('A'));
            Check.Equal("f appendix round area formula", "=PI()*0.25*RC[-1]", round['D'].Formula);
            Check.Near("f appendix round area value", Math.PI * 0.25 * 4, Number(round['D']), 1e-9);

            RowInfo thin = Row(main, "Ống gió tôn mạ kẽm 0.58mm");
            Check.Equal("f band formula sums appendix areas", "=('PHU LUC ONG GIO'!R2C4)*R6C6", thin['F'].Formula);
            Check.Near("f band cached value", Math.PI * 0.25 * 4 * 1.1, Number(thin['F']), 1e-9);

            RowInfo medium = Row(main, "Ống gió tôn mạ kẽm 0.75mm");
            Check.Equal("f band 0.75 formula", "=('PHU LUC ONG GIO'!R3C4)*R6C6", medium['F'].Formula);
            Check.Near("f band 0.75 cached value", 1.8 * 6.5 * 1.1, Number(medium['F']), 1e-9);

            RowInfo empty = Row(main, "Ống gió tôn mạ kẽm 1.15mm");
            Check.Equal("f empty band has no formula", null, empty['F'].Formula);
            Check.Equal("f empty band is zero", "0", empty.Text('F'));
        }

        private static void LongFormula(string directory)
        {
            const int segments = 1500;
            var curves = Enumerable.Range(0, segments)
                .Select(index => new TakeoffCurveInput("EIA_1 CHWS DN50", 1234.567))
                .ToList();
            curves.Add(new TakeoffCurveInput("EIA_1 CHWS DN65", 1000));
            TakeoffResult result = TakeoffAggregator.Aggregate(null, curves,
                Options(1.0, new ShaftPipeInput(ShaftPipeKind.Steel, "DN50", 3, 2)));

            string path = Path.Combine(directory, "long.xml");
            TakeoffWorkbookWriter.Write(path, result);
            WorkbookModel workbook = WorkbookModel.Load(path);
            Check.Sequence("f detail sheet added",
                new[] { "DIEU HOA THONG GIO", "PHU LUC ONG GIO", "PHU LUC DOAN ONG" },
                workbook.Sheets.Select(sheet => sheet.Name));
            if (workbook.Sheets.Count < 3)
                return;

            SheetInfo detail = workbook.Sheets[2];
            RowInfo longRow = Row(workbook.Main, "Ống thép hàn mạ kẽm DN50, SCH40");
            int lastRow = 3 + segments;
            Check.Equal("f long row uses SUM over detail",
                "=(SUM('PHU LUC DOAN ONG'!R3C2:R" + lastRow + "C2))*R6C6", longRow['F'].Formula);
            Check.True("f long formula within Excel limit", longRow['F'].Formula.Length <= 8192);
            Check.Near("f long cached value", segments * 1.235 + 6, Number(longRow['F']), 1e-6);

            Check.Equal("f short row stays inline", "=(1)*R6C6", Row(workbook.Main, "Ống thép hàn mạ kẽm DN65, SCH40")['F'].Formula);

            Check.Equal("f detail title", "Ống thép hàn mạ kẽm DN50, SCH40", detail[2].Text('A'));
            Check.Equal("f detail first segment label", "Đoạn 1", detail[3].Text('A'));
            Check.Equal("f detail segment value", "1.235", detail[3].Text('B'));
            Check.Equal("f detail last segment label", "Đoạn " + segments, detail[2 + segments].Text('A'));
            Check.Equal("f detail shaft label", "Trục kỹ thuật", detail[lastRow].Text('A'));
            Check.Equal("f detail shaft formula", "=3*2", detail[lastRow]['B'].Formula);
        }

        private static RowInfo Row(SheetInfo sheet, string description)
        {
            RowInfo row = sheet.Rows.Values.FirstOrDefault(candidate => candidate.Text('B') == description);
            if (row == null)
                throw new InvalidOperationException("row not found: " + description);
            return row;
        }

        private static double Number(CellInfo cell)
        {
            return WorkbookModel.ParseDouble(cell.Text);
        }
    }
}
