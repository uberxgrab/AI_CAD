using System.Collections.Generic;
using System.IO;
using System.Linq;
using HvacAiCadAddin.Takeoff;

namespace HvacQtoCoreTests
{
    /// <summary>HVACNORM size tags in the takeoff (user decisions of 2026-11-23).</summary>
    internal static class TagTests
    {
        public static void Run(string tempDirectory)
        {
            RoundTrip();
            OtherMaterialKeepsLayerWithPrefix();
            CatalogueLayerWins();
            TagGivesSizeToUnsizedSystemLayer();
            DuctTags();
            RefrigerantPairAndUnknownMaterial();
            PlumbingShaft();
            Workbook(tempDirectory);
        }

        private static SizeTag Pipe(string system, string material, double size, string prefix = null)
        {
            return new SizeTag
            {
                Kind = SizeTagKind.Pipe, System = system, Material = material, Prefix = prefix ?? system,
                SizeMm = size, SizeText = "D" + size, Confidence = "high", SourceLayer = "M-P-WS", Label = "WS-PPR-DN90"
            };
        }

        private static TakeoffResult Aggregate(IEnumerable<TakeoffCurveInput> curves, IEnumerable<ShaftPipeInput> shafts = null)
        {
            TakeoffOptions options = new TakeoffOptions(DuctGaugeSettings.CreateDefault(), 1.0, shafts);
            return TakeoffAggregator.Aggregate(new TakeoffBlockInput[0], curves, options);
        }

        private static void RoundTrip()
        {
            Check.Case("t tag round trip", () =>
            {
                SizeTag pipe = Pipe("WS", "PPR", 90);
                pipe.Slope = 1;
                SizeTag back;
                Check.True("t pipe parses", SizeTag.TryParse(SizeTag.PipeApp, pipe.ToValues(), out back));
                Check.Equal("t pipe system", "WS", back.System);
                Check.Equal("t pipe material", "PPR", back.Material);
                Check.Near("t pipe size", 90, back.SizeMm);
                Check.Near("t pipe slope", 1, back.Slope);
                Check.Equal("t pipe label", "WS-PPR-DN90", back.Label);

                SizeTag duct = new SizeTag { Kind = SizeTagKind.Duct, System = "SA", SizeMm = 600, Size2Mm = 300, SizeText = "600x300" };
                Check.True("t duct parses", SizeTag.TryParse(SizeTag.DuctApp, duct.ToValues(), out back));
                Check.Equal("t duct kind", SizeTagKind.Duct, back.Kind);
                Check.Near("t duct height", 300, back.Size2Mm);
                Check.False("t duct not round", back.Round);

                Check.False("t wrong app", SizeTag.TryParse("HVACAI_ROOM", pipe.ToValues(), out back));
                Check.False("t pipe values read as duct app still need a duct layout",
                    SizeTag.TryParse(SizeTag.DuctApp, new List<KeyValuePair<short, object>>(), out back));
                List<KeyValuePair<short, object>> other = pipe.ToValues();
                other[0] = new KeyValuePair<short, object>(1000, "9");
                Check.False("t unknown version", SizeTag.TryParse(SizeTag.PipeApp, other, out back));
                string longLabel = new string('x', 400);
                Check.True("t long text is cut", Pipe("WS", "PPR", 90).ToValuesWithLabel(longLabel).Last().Value.ToString().Length <= 250);
            });
        }

        private static void OtherMaterialKeepsLayerWithPrefix()
        {
            Check.Case("t other material", () =>
            {
                TakeoffResult result = Aggregate(new[]
                {
                    new TakeoffCurveInput("M-P-WS", 6000) { Tag = Pipe("WS", "PPR", 90) },
                    new TakeoffCurveInput("M-P-WS", 4000) { Tag = Pipe("WS", "PPR", 90) },
                    new TakeoffCurveInput("0-M-WD-DARK", 3000) { Tag = Pipe("WW", "uPVC", 110) },
                    new TakeoffCurveInput("M-P-WS", 1000) { Tag = Pipe("DHW", "PPR", 25) },
                    new TakeoffCurveInput("M-P-WS", 1000) { Tag = Pipe("WS", "HDPE", 110) },
                    new TakeoffCurveInput("M-P-WS", 1000) { Tag = Pipe("WS", "SUS316", 50) },
                });
                Check.Sequence("t other rows", new[]
                {
                    "DHW - Ống PPR PN20 D25",
                    "WS - Ống HDPE PE100 PN10 D110",
                    "WS - Ống inox SUS316 DN50",
                    "WS - Ống PPR PN10 D90",
                    "WW - Ống uPVC Class 2 D110",
                }, result.OtherRows.Select(r => r.Description));
                Check.Near("t PPR D90 length", 10, result.OtherRows.Single(r => r.Description.EndsWith("PPR PN10 D90")).LengthMeters);
                Check.Equal("t tagged counted", 6, result.TaggedCurveCount);
                Check.Equal("t nothing unexported", 0, result.NotExportedCurveCount);
                Check.Equal("t no steel", 0, result.SteelRows.Count);
            });
        }

        private static void CatalogueLayerWins()
        {
            Check.Case("t catalogue layer wins", () =>
            {
                TakeoffResult result = Aggregate(new[]
                {
                    new TakeoffCurveInput("EIA_1 CHWS DN50", 2000) { Tag = Pipe("CHWS", "STEEL", 80) },
                });
                Check.Equal("t steel row", "Ống thép hàn mạ kẽm DN50, SCH40", result.SteelRows.Single().Description);
                Check.Equal("t no other", 0, result.OtherRows.Count);
            });
        }

        private static void TagGivesSizeToUnsizedSystemLayer()
        {
            Check.Case("t unsized system layer", () =>
            {
                // "M-HVAC-CTWS" has no catalogue layer in the template, so HVACNORM kept it and tagged it.
                TakeoffResult result = Aggregate(new[]
                {
                    new TakeoffCurveInput("M-HVAC-CWS", 5000) { Tag = Pipe("CWS", "STEEL", 100) },
                });
                Check.Equal("t prefixed steel", "CWS - Ống thép hàn mạ kẽm DN100, SCH40", result.OtherRows.Single().Description);
                Check.Equal("t not a plain steel row", 0, result.SteelRows.Count);
            });
        }

        private static void DuctTags()
        {
            Check.Case("t duct tags", () =>
            {
                SizeTag rect = new SizeTag { Kind = SizeTagKind.Duct, System = "SA", SizeMm = 600, Size2Mm = 300 };
                SizeTag round = new SizeTag { Kind = SizeTagKind.Duct, System = "EA", SizeMm = 250, Round = true };
                TakeoffResult result = Aggregate(new[]
                {
                    new TakeoffCurveInput("M-HVAC-DUCT-SA", 6000) { Tag = rect },
                    new TakeoffCurveInput("M-HVAC-DUCT-EA", 2000) { Tag = round },
                });
                DuctSizeRow r = result.DuctSizeRows.Single(row => !row.Size.IsRound);
                Check.Equal("t rect size", "600 x 300 mm", r.SizeText);
                Check.Near("t rect length", 6, r.LengthMeters);
                Check.Near("t rect area", 2 * 0.9 * 6, r.AreaSquareMeters, 1e-9);
                DuctSizeRow d = result.DuctSizeRows.Single(row => row.Size.IsRound);
                Check.Equal("t round size", "Ø250 mm", d.SizeText);
                Check.Near("t round area", System.Math.PI * 0.25 * 2, d.AreaSquareMeters, 1e-9);
                Check.Equal("t ducts are not pipes", 0, result.OtherRows.Count);
            });
        }

        private static void RefrigerantPairAndUnknownMaterial()
        {
            Check.Case("t pair and unknown", () =>
            {
                SizeTag pair = Pipe("RP", "CU", 15.9);
                pair.Size2Mm = 9.5;
                TakeoffResult result = Aggregate(new[]
                {
                    new TakeoffCurveInput("M-HVAC-REF", 3000) { Tag = pair },
                    new TakeoffCurveInput("PIPE-X", 1000) { Tag = Pipe("WS", "", 40) },
                });
                List<string> rows = result.OtherRows.Select(r => r.Description).ToList();
                Check.Equal("t three rows", 3, rows.Count);
                Check.True("t gas line", rows.Any(r => r.StartsWith("RP - Ống đồng D15.9")), string.Join(" | ", rows));
                Check.True("t liquid line", rows.Any(r => r.StartsWith("RP - Ống đồng D9.5")), string.Join(" | ", rows));
                Check.True("t unknown material", rows.Contains("WS - Ống D40 (chưa rõ vật liệu, layer: PIPE-X)"),
                    string.Join(" | ", rows));
            });
        }

        private static void PlumbingShaft()
        {
            Check.Case("t plumbing shaft", () =>
            {
                TakeoffResult result = Aggregate(new TakeoffCurveInput[0], new[]
                {
                    new ShaftPipeInput(ShaftPipeKind.Ppr, "D90", 3.6, 4),
                    new ShaftPipeInput(ShaftPipeKind.UpvcWaste, "110", 10, 1),
                });
                Check.Near("t PPR riser", 14.4, result.OtherRows.Single(r => r.Description == "Ống PPR PN10 D90").LengthMeters, 1e-9);
                Check.Near("t uPVC riser", 10, result.OtherRows.Single(r => r.Description == "Ống uPVC Class 2 D110").LengthMeters);
                TakeoffOptions remembered = TakeoffOptions.Deserialize(
                    TakeoffOptions.CreateDefault().Serialize() + "shaft=Hdpe|D63|3|2\n");
                Check.Equal("t riser kind remembered", ShaftPipeKind.Hdpe, remembered.ShaftPipes.Single().Kind);
                ShaftPipeKind kind;
                Check.True("t riser kind by label", TakeoffOptions.TryParseShaftKind("Ống uPVC thoát nước", out kind));
                Check.Equal("t riser kind label", ShaftPipeKind.UpvcWaste, kind);
            });
        }

        private static void Workbook(string tempDirectory)
        {
            Check.Case("t workbook", () =>
            {
                TakeoffResult result = Aggregate(new[]
                {
                    new TakeoffCurveInput("M-P-WS", 6000) { Tag = Pipe("WS", "PPR", 90) },
                    new TakeoffCurveInput("EIA_1 Pipe condensated DN27", 1000),
                });
                string path = Path.Combine(tempDirectory, "tags.xml");
                TakeoffWorkbookWriter.Write(path, result);
                string text = File.ReadAllText(path);
                Check.True("t subgroup title", text.Contains("Ống cấp thoát nước và ống khác (theo tiền tố hệ)"));
                Check.True("t prefixed row", text.Contains("WS - Ống PPR PN10 D90"));
                WorkbookModel model = WorkbookModel.Load(path);
                Check.True("t workbook loads", model.Sheets.Count >= 2);
            });
        }
    }

    internal static class SizeTagTestExtensions
    {
        public static List<KeyValuePair<short, object>> ToValuesWithLabel(this SizeTag tag, string label)
        {
            tag.Label = label;
            return tag.ToValues();
        }
    }
}
