using System.Collections.Generic;
using System.Linq;
using HvacAiCadAddin.Takeoff;

namespace HvacQtoCoreTests
{
    /// <summary>Specification section 2: pipe classification by layer.</summary>
    internal static class PipeTests
    {
        private const PipeMaterial None = PipeMaterial.None;
        private const PipeMaterial Copper = PipeMaterial.Copper;
        private const PipeMaterial Upvc = PipeMaterial.Upvc;
        private const PipeMaterial Steel = PipeMaterial.Steel;

        public static void Run()
        {
            RealLayerNames();
            PriorityOrder();
            CaseInsensitive();
            DiameterSymbolBoundaries();
            CopperSlashOnly();
            CopperAggregation();
            CopperWallThickness();
            DisplayPrefix();
            UnknownSize();
            NumberFormat();
            RowGroupingAndOrder();
            NotExported();
        }

        private static void Classify(string layer, PipeMaterial material, params double[] sizes)
        {
            string name = "s2 classify `" + layer + "`";
            Check.Case(name, () =>
            {
                PipeLayerClassification result = TakeoffClassifier.ClassifyPipeLayer(layer);
                Check.Equal(name + " material", material, result.Material);
                Check.Sequence(name + " sizes", sizes.OrderBy(size => size),
                    result.SizesMm.OrderBy(size => size));
            });
        }

        private static void Material(string layer, PipeMaterial material)
        {
            string name = "s2 material `" + layer + "`";
            Check.Case(name, () =>
                Check.Equal(name, material, TakeoffClassifier.ClassifyPipeLayer(layer).Material));
        }

        private static TakeoffResult Curves(params TakeoffCurveInput[] curves)
        {
            return TakeoffAggregator.Aggregate(new List<TakeoffBlockInput>(), curves, DuctGaugeSettings.CreateDefault());
        }

        private static void RealLayerNames()
        {
            Classify("EIA_1 CHWR DN100", Steel, 100);
            Classify("EIA_1 CHWS DN15", Steel, 15);
            Classify("EIA_1 Pipe condensated DN27", Upvc, 27);
            Classify("EIA_1 RP 9.5-6.4", Copper, 9.5, 6.4);
            Classify("EIA_1 RP 12.7-6.4", Copper, 12.7, 6.4);
            Classify("EIA_1 RP 15.9-6.4", Copper, 15.9, 6.4);
            Classify("EIA_1 RP 15.9-9.5 (14)", Copper, 15.9, 9.5);
            Classify("EIA_1 RP 19.1-9.5 (22.4)", Copper, 19.1, 9.5);
            Classify("EIA_1 RP 22.2-9.5 (28)", Copper, 22.2, 9.5);
            Classify("EIA_1 RP 28.6-12.7 (33.6-44.8)", Copper, 28.6, 12.7);
            Classify("EIA_1 RP 28.6-15.9 (50.4-61.6)", Copper, 28.6, 15.9);
            Classify("EIA_1 RP 34.9-15.9 (67.2)", Copper, 34.9, 15.9);
            Classify("EIA_1 RP 34.9-19.1 (72.8-95.2)", Copper, 34.9, 19.1);
            Classify("EIA_1 RP 41.3-19.1 (100.8-151.2)", Copper, 41.3, 19.1);
            Classify("EIA_1 CHILLED WATER SUPPLY", None);
            Classify("EIA_1 DRAINAGE", None);
            Classify("EIA_1 GAS PIPE", None);
            Classify("EIA_5 GI-STEEL PIPE", None);
            Classify("S-DIM", None);
            Classify("EIA_1 MECHANICAL EQUIPMENT", None);
        }

        private static void PriorityOrder()
        {
            // All four outcomes, one keyword each.
            Classify("CHWS DN50", Steel, 50);
            Classify("CHWR DN65", Steel, 65);
            Classify("HWS DN80", Steel, 80);
            Classify("HWR DN90", Steel, 90);
            Classify("RP 15.9/9.5", Copper, 15.9, 9.5);
            Classify("Pipe CONDENSATED", Upvc);
            Classify("PIPE_D34", Upvc, 34);
            Classify("Anything else", None);

            // First match wins.
            Material("CHWS 15.9/9.5", Steel);
            Material("HWR D15.9/D9.5", Steel);
            Classify("CHWR condensated DN50", Steel, 50);
            Material("CHWS condensated 15.9/9.5", Steel);
            Classify("condensated 15.9/9.5", Copper, 15.9, 9.5);
            Classify("DN20 15.9/9.5", Copper, 15.9, 9.5);
        }

        private static void CaseInsensitive()
        {
            Classify("eia_1 chwr dn100", Steel, 100);
            Classify("Chws Dn15", Steel, 15);
            Classify("hws dn40", Steel, 40);
            Classify("hwr dn40", Steel, 40);
            Classify("pipe condensated", Upvc);
            Classify("Pipe Condensated dn27", Upvc, 27);
            Classify("pipe_d34", Upvc, 34);
            Classify("d15.9/d9.5", Copper, 15.9, 9.5);
            Classify("ø15.9/ø9.5", Copper, 15.9, 9.5);
        }

        private static void DiameterSymbolBoundaries()
        {
            Classify("D34", Upvc, 34);
            Classify("DN50", Upvc, 50);
            Classify("PIPE_D34", Upvc, 34);
            Classify("X-DN25.5", Upvc, 25.5);
            Classify("D34-X", Upvc, 34);
            Classify("X D34 Y", Upvc, 34);

            Classify("DRAIN", None);
            Classify("DIM", None);
            Classify("ADD34", None);
            Classify("D34A", None);
            Classify("S-DIM", None);
            Classify("1D34", None);
            Classify("DN50X", None);
            Classify("D34.5A", None);
            Classify("XDN50", None);
            Classify("D", None);
            Classify("DN", None);
            Classify("ÁD34", None);
            Classify("D34Ă", None);

            // A symbol blocked by its boundaries gives no size on a steel layer either.
            Classify("CHWS 100", Steel);
            Classify("CHWSDN100", Steel);
            Classify("HWR D34A", Steel);
        }

        private static void CopperSlashOnly()
        {
            Classify("15.9/9.5", Copper, 15.9, 9.5);
            Classify("D15.9/D9.5", Copper, 15.9, 9.5);
            Classify("Ø15.9/Ø9.5", Copper, 15.9, 9.5);
            Classify("15.9 / 9.5", Copper, 15.9, 9.5);
            Classify("D15.9 / D9.5", Copper, 15.9, 9.5);
            Classify("Ø15.9 /Ø9.5", Copper, 15.9, 9.5);
            Classify("EIA_1 RP 15.9/9.5", Copper, 15.9, 9.5);

            // Hyphen pairs need RP as its own word.
            Classify("RP 15.9 - 9.5", Copper, 15.9, 9.5);
            Classify("rp 15.9-9.5", Copper, 15.9, 9.5);
            Classify("RP D15.9-D9.5", Copper, 15.9, 9.5);
            Classify("15.9-9.5", None);
            Classify("GRP 15.9-9.5", None);
            Classify("EIA_1 RP (33.6-44.8)", None);
            Classify("EIA_1 RP 15.9", None);
            Material("CHWS RP 15.9-9.5 DN100", Steel);

            // A hyphen D pair without RP is not copper, but each side is a diameter symbol.
            Material("D15.9-D9.5", Upvc);

            // Parenthesized text is ignored for copper pairs.
            Classify("EIA_1 RP 15.9/9.5 (14)", Copper, 15.9, 9.5);
            Classify("EIA_1 RP 28.6/12.7 (33.6-44.8)", Copper, 28.6, 12.7);
            Classify("EIA_1 RP (33.6/44.8) 12.7/6.4", Copper, 12.7, 6.4);
            Classify("EIA_1 RP (33.6/44.8)", None);
            Classify("EIA_1 RP (1/2) (3/4)", None);

            // A slash without two numbers is not a pair.
            Classify("EIA_1 A/B", None);
            Classify("DRAIN/VENT", None);
            Classify("15.9/", None);
            Classify("/9.5", None);
            Material("CHWS/CHWR DN100", Steel);
        }

        private static void CopperAggregation()
        {
            Check.Case("s2 copper full length to both sizes", () =>
            {
                TakeoffResult result = Curves(new TakeoffCurveInput("EIA_1 RP 15.9/9.5", 12000));
                Check.Equal("s2 copper pair gives two rows", 2, result.CopperRows.Count);
                Check.Near("s2 copper D9.5 full length", 12, Row(result.CopperRows, CopperText(9.5, "0.8mm")).LengthMeters);
                Check.Near("s2 copper D15.9 full length", 12, Row(result.CopperRows, CopperText(15.9, "1.0mm")).LengthMeters);
            });

            Check.Case("s2 copper hyphen pair full length to both sizes", () =>
            {
                TakeoffResult result = Curves(new TakeoffCurveInput("EIA_1 RP 15.9-9.5 (14)", 12000));
                Check.Equal("s2 copper hyphen pair gives two rows", 2, result.CopperRows.Count);
                Check.Near("s2 copper hyphen D9.5 full length", 12, Row(result.CopperRows, CopperText(9.5, "0.8mm")).LengthMeters);
                Check.Near("s2 copper hyphen D15.9 full length", 12, Row(result.CopperRows, CopperText(15.9, "1.0mm")).LengthMeters);
            });

            Check.Case("s2 copper equal sizes get length twice", () =>
            {
                TakeoffResult result = Curves(new TakeoffCurveInput("D12.7/D12.7", 10000));
                Check.Equal("s2 copper equal pair gives one row", 1, result.CopperRows.Count);
                Check.Near("s2 copper equal pair length twice", 20, result.CopperRows[0].LengthMeters);
            });

            Check.Case("s2 copper sizes sum across layers", () =>
            {
                TakeoffResult result = Curves(
                    new TakeoffCurveInput("EIA_1 RP 15.9/9.5", 12000),
                    new TakeoffCurveInput("EIA_1 RP 22.2/9.5", 3000),
                    new TakeoffCurveInput("EIA_1 RP 22.2/9.5 (14)", 1000));
                Check.Sequence("s2 copper ascending sizes", new double?[] { 9.5, 15.9, 22.2 },
                    result.CopperRows.Select(row => row.SizeMm));
                Check.Near("s2 copper D9.5 sum", 16, result.CopperRows[0].LengthMeters);
                Check.Near("s2 copper D15.9 sum", 12, result.CopperRows[1].LengthMeters);
                Check.Near("s2 copper D22.2 sum", 4, result.CopperRows[2].LengthMeters);
                Check.Equal("s2 copper material on row", Copper, result.CopperRows[0].Material);
            });
        }

        private static string CopperText(double size, string thickness)
        {
            return "Ống đồng D" + TakeoffClassifier.FormatNumber(size) + " dày " + thickness + ", kèm bảo ôn 19mm";
        }

        private static void CopperWallThickness()
        {
            Text("s2 spec text copper", Copper, 15.9, "L", "Ống đồng D15.9 dày 1.0mm, kèm bảo ôn 19mm");
            Text("s2 copper 6.4", Copper, 6.4, "L", "Ống đồng D6.4 dày 0.8mm, kèm bảo ôn 19mm");
            Text("s2 copper 9.5", Copper, 9.5, "L", "Ống đồng D9.5 dày 0.8mm, kèm bảo ôn 19mm");
            Text("s2 copper 12.7", Copper, 12.7, "L", "Ống đồng D12.7 dày 1.0mm, kèm bảo ôn 19mm");
            Text("s2 copper 19.1", Copper, 19.1, "L", "Ống đồng D19.1 dày 1.0mm, kèm bảo ôn 19mm");
            Text("s2 copper 22.2", Copper, 22.2, "L", "Ống đồng D22.2 dày 1.0mm, kèm bảo ôn 19mm");
            Text("s2 copper 28.6", Copper, 28.6, "L", "Ống đồng D28.6 dày 1.0mm, kèm bảo ôn 19mm");
            Text("s2 copper 34.9", Copper, 34.9, "L", "Ống đồng D34.9 dày 1.0mm, kèm bảo ôn 19mm");
            Text("s2 copper 41.3", Copper, 41.3, "L", "Ống đồng D41.3 dày 1.0mm, kèm bảo ôn 19mm");

            Text("s2 copper size outside table, spec example", Copper, 10, "L", "Ống đồng D10, kèm bảo ôn 19mm");
            Text("s2 copper 6.35 outside table", Copper, 6.35, "L", "Ống đồng D6.35, kèm bảo ôn 19mm");
            Text("s2 copper 44.5 outside table", Copper, 44.5, "L", "Ống đồng D44.5, kèm bảo ôn 19mm");
        }

        private static void Text(string name, PipeMaterial material, double? size, string layer, string expected)
        {
            Check.Case(name, () =>
                Check.Equal(name, expected, TakeoffClassifier.DescribePipe(material, size, layer)));
        }

        private static void DisplayPrefix()
        {
            Text("s2 spec text steel", Steel, 200, "L", "Ống thép hàn mạ kẽm DN200, SCH40");
            Text("s2 spec text uPVC", Upvc, 27, "L", "Ống thoát nước ngưng uPVC Class 1 D27, kèm bảo ôn 13mm");

            Check.Case("s2 steel shown as DN when layer says D", () =>
            {
                TakeoffResult result = Curves(new TakeoffCurveInput("CHWS D100", 1000));
                Check.Equal("s2 steel shown as DN", "Ống thép hàn mạ kẽm DN100, SCH40", result.SteelRows[0].Description);
            });
            Check.Case("s2 uPVC shown as D when layer says DN", () =>
            {
                TakeoffResult result = Curves(new TakeoffCurveInput("EIA_1 Pipe condensated DN27", 1000));
                Check.Equal("s2 uPVC shown as D", "Ống thoát nước ngưng uPVC Class 1 D27, kèm bảo ôn 13mm",
                    result.UpvcRows[0].Description);
            });
        }

        private static void UnknownSize()
        {
            Text("s2 spec text steel unknown", Steel, null, "HWS MAIN", "Ống thép hàn mạ kẽm, SCH40 (layer: HWS MAIN)");
            Text("s2 spec text uPVC unknown", Upvc, null, "X", "Ống thoát nước ngưng uPVC Class 1, kèm bảo ôn 13mm (layer: X)");

            Check.Case("s2 unknown size rows are per layer", () =>
            {
                TakeoffResult result = Curves(
                    new TakeoffCurveInput("HWS MAIN", 1000),
                    new TakeoffCurveInput("HWS MAIN", 500),
                    new TakeoffCurveInput("CHWR RISER", 2000),
                    new TakeoffCurveInput("EIA_1 Pipe condensated", 3000),
                    new TakeoffCurveInput("Other condensated", 4000));

                Check.Equal("s2 steel unknown rows", 2, result.SteelRows.Count);
                Check.Near("s2 steel unknown sums per layer", 1.5,
                    Row(result.SteelRows, "Ống thép hàn mạ kẽm, SCH40 (layer: HWS MAIN)").LengthMeters);
                Check.Near("s2 steel unknown second layer", 2,
                    Row(result.SteelRows, "Ống thép hàn mạ kẽm, SCH40 (layer: CHWR RISER)").LengthMeters);
                Check.Equal("s2 uPVC unknown rows", 2, result.UpvcRows.Count);
                Check.Near("s2 uPVC unknown layer", 3,
                    Row(result.UpvcRows, "Ống thoát nước ngưng uPVC Class 1, kèm bảo ôn 13mm (layer: EIA_1 Pipe condensated)").LengthMeters);
                Check.True("s2 unknown rows invent no size",
                    result.SteelRows.Concat(result.UpvcRows).All(row => !row.SizeMm.HasValue));
                Check.Equal("s2 unknown rows are exported", 0, result.NotExportedCurveCount);
            });
        }

        private static void NumberFormat()
        {
            Check.Case("s2 number format", () =>
            {
                Check.Equal("s2 format whole number", "100", TakeoffClassifier.FormatNumber(100));
                Check.Equal("s2 format one decimal", "25.5", TakeoffClassifier.FormatNumber(25.5));
                Check.Equal("s2 format trims zeros", "25.5", TakeoffClassifier.FormatNumber(25.50));
                Check.Equal("s2 format two decimals max", "2.67", TakeoffClassifier.FormatNumber(2.666));
            });
            Text("s2 uPVC decimal size", Upvc, 25.5, "L", "Ống thoát nước ngưng uPVC Class 1 D25.5, kèm bảo ôn 13mm");
            Text("s2 steel decimal size", Steel, 25.5, "L", "Ống thép hàn mạ kẽm DN25.5, SCH40");
        }

        private static void RowGroupingAndOrder()
        {
            Check.Case("s2 sorted ascending, unknown last", () =>
            {
                TakeoffResult result = Curves(
                    new TakeoffCurveInput("HWS MAIN", 1000),
                    new TakeoffCurveInput("EIA_1 CHWR DN100", 12000),
                    new TakeoffCurveInput("EIA_1 CHWS DN15", 1000),
                    new TakeoffCurveInput("EIA_1 CHWR DN100", 3000),
                    new TakeoffCurveInput("EIA_1 CHWS DN100", 1000),
                    new TakeoffCurveInput("EIA_1 Pipe condensated", 1000),
                    new TakeoffCurveInput("X_D34", 2000),
                    new TakeoffCurveInput("EIA_1 Pipe condensated DN27", 1000));

                Check.Sequence("s2 steel order", new double?[] { 15, 100, null },
                    result.SteelRows.Select(row => row.SizeMm));
                Check.Near("s2 steel mm to m and sum across layers", 16, result.SteelRows[1].LengthMeters);
                Check.Sequence("s2 uPVC order", new double?[] { 27, 34, null },
                    result.UpvcRows.Select(row => row.SizeMm));
            });
        }

        private static void NotExported()
        {
            Check.Case("s2 not exported", () =>
            {
                TakeoffResult result = Curves(
                    new TakeoffCurveInput("S-DIM", 100),
                    new TakeoffCurveInput("S-DIM", 200),
                    new TakeoffCurveInput("EIA_1 DRAINAGE", 300),
                    new TakeoffCurveInput("EIA_1 CHWR DN100", 1000));

                Check.Equal("s2 not exported curve count", 3, result.NotExportedCurveCount);
                Check.Sequence("s2 not exported layers are distinct",
                    new[] { "EIA_1 DRAINAGE", "S-DIM" }, result.NotExportedLayers.OrderBy(layer => layer, System.StringComparer.Ordinal));
                Check.Equal("s2 exported curve kept", 1, result.SteelRows.Count);
            });
        }

        private static PipeRow Row(IEnumerable<PipeRow> rows, string description)
        {
            PipeRow row = rows.FirstOrDefault(candidate => candidate.Description == description);
            if (row == null)
            {
                throw new KeyNotFoundException("no row `" + description + "` in [" +
                    string.Join(" | ", rows.Select(candidate => candidate.Description)) + "]");
            }
            return row;
        }
    }
}
