using HvacAiCadAddin.Pipes;

namespace PipeCoreTests
{
    /// <summary>The HVACPIPESIZE layer catalogue: names, sizes, parsing, colours and the layer filter.</summary>
    internal static class CatalogTests
    {
        public static void Run()
        {
            LayerNames();
            UnknownSizes();
            Counts();
            RoundTrip();
            ParseCasing();
            NotCatalogueLayers();
            Colours();
            CreatedKinds();
            LayerFilter();
        }

        private static void Name(PipeKind kind, string size, string expected)
        {
            string label = "layer of " + kind + " " + size;
            Check.Case(label, () => Check.Equal(label, expected, PipeCatalog.LayerName(kind, size)));
        }

        private static void LayerNames()
        {
            Name(PipeKind.Chws, "DN40", "EIA_1 CHWS DN40");
            Name(PipeKind.Chwr, "DN100", "EIA_1 CHWR DN100");
            Name(PipeKind.Chws, "DN15", "EIA_1 CHWS DN15");
            Name(PipeKind.Chwr, "DN600", "EIA_1 CHWR DN600");
            Name(PipeKind.Condensate, "DN27", "EIA_1 Pipe condensated DN27");
            Name(PipeKind.Condensate, "DN110", "EIA_1 Pipe condensated DN110");
            Name(PipeKind.Refrigerant, "9.5-6.4", "EIA_1 RP 9.5-6.4");
            Name(PipeKind.Refrigerant, "15.9-9.5 (14)", "EIA_1 RP 15.9-9.5 (14)");
            Name(PipeKind.Refrigerant, "28.6-12.7 (33.6-44.8)", "EIA_1 RP 28.6-12.7 (33.6-44.8)");
            Name(PipeKind.Refrigerant, "41.3-19.1 (100.8-151.2)", "EIA_1 RP 41.3-19.1 (100.8-151.2)");
            Name(PipeKind.Cws, "DN150", "EIA_1 CWS DN150");
            Name(PipeKind.Cwr, "DN150", "EIA_1 CWR DN150");
            Name(PipeKind.Hws, "DN32", "EIA_1 HWS DN32");
            Name(PipeKind.Hwr, "DN600", "EIA_1 HWR DN600");
            Name(PipeKind.Chws, "dn40", "EIA_1 CHWS DN40");
        }

        private static void UnknownSizes()
        {
            Name(PipeKind.Chws, "DN35", null);
            Name(PipeKind.Condensate, "DN15", null);
            Name(PipeKind.Refrigerant, "15.9", null);
            Name(PipeKind.Refrigerant, "15.9-9.5", null);
            Name(PipeKind.Cws, "", null);
            Name(PipeKind.Hws, null, null);
        }

        private static void Counts()
        {
            Check.Case("size counts", () =>
            {
                Check.Equal("water sizes", 19, PipeCatalog.SizeTexts(PipeKind.Chws).Count);
                Check.Equal("hot water sizes", 19, PipeCatalog.SizeTexts(PipeKind.Hwr).Count);
                Check.Equal("condensate sizes", 7, PipeCatalog.SizeTexts(PipeKind.Condensate).Count);
                Check.Equal("refrigerant sizes", 11, PipeCatalog.SizeTexts(PipeKind.Refrigerant).Count);
                Check.Equal("kinds", 8, PipeCatalog.Kinds.Count);
                Check.Equal("first water size", "DN15", PipeCatalog.SizeTexts(PipeKind.Cwr)[0]);
                Check.Equal("last water size", "DN600", PipeCatalog.SizeTexts(PipeKind.Cwr)[18]);
            });
        }

        private static void RoundTrip()
        {
            Check.Case("every catalogue layer parses back", () =>
            {
                foreach (PipeKind kind in PipeCatalog.Kinds)
                {
                    foreach (string size in PipeCatalog.SizeTexts(kind))
                    {
                        string layer = PipeCatalog.LayerName(kind, size);
                        PipeKind parsedKind;
                        string parsedSize;
                        bool ok = PipeCatalog.TryParseLayer(layer, out parsedKind, out parsedSize);
                        Check.True(layer + " parsed", ok);
                        if (!ok)
                            continue;
                        Check.Equal(layer + " kind", kind, parsedKind);
                        Check.Equal(layer + " size", size, parsedSize);
                    }
                }
            });
        }

        private static void ParseCasing()
        {
            Check.Case("parsing ignores case (tr-TR safe)", () =>
            {
                PipeKind kind;
                string size;
                Check.True("lower case water", PipeCatalog.TryParseLayer("eia_1 chws dn40", out kind, out size));
                Check.Equal("kind", PipeKind.Chws, kind);
                Check.Equal("size", "DN40", size);
                Check.True("upper case condensate",
                    PipeCatalog.TryParseLayer("EIA_1 PIPE CONDENSATED DN27", out kind, out size));
                Check.Equal("condensate kind", PipeKind.Condensate, kind);
                Check.True("lower case refrigerant",
                    PipeCatalog.TryParseLayer("eia_1 rp 15.9-9.5 (14)", out kind, out size));
                Check.Equal("refrigerant size", "15.9-9.5 (14)", size);
                Check.True("padded", PipeCatalog.TryParseLayer("  EIA_1 CWS DN80 ", out kind, out size));
                Check.Equal("padded kind", PipeKind.Cws, kind);
            });
        }

        private static void Not(string layer)
        {
            string label = "not a catalogue layer '" + layer + "'";
            Check.Case(label, () =>
            {
                PipeKind kind;
                string size;
                Check.False(label, PipeCatalog.TryParseLayer(layer, out kind, out size));
            });
        }

        private static void NotCatalogueLayers()
        {
            Not("EIA_1 CHILLED WATER SUPPLY");
            Not("EIA_1 CHWS DN35");
            Not("EIA_1 CHWS");
            Not("EIA_1 RP 15.9");
            Not("EIA_1 RP 15.9-9.5 (15)");
            Not("EIA_1 SUPPLY AIR");
            Not("EIA_1 Pipe condensated");
            Not("EIA_1 Pipe condensated DN15");
            Not("EIA_2 CHWS DN40");
            Not("");
            Not(null);
        }

        private static void Colours()
        {
            Check.Case("water layer colours", () =>
            {
                // The CHW table of AGENTS.md, in order.
                int[] sizes = { 15, 20, 25, 32, 40, 50, 65, 80, 100, 125, 150, 200, 250, 300, 350, 400, 450, 500, 600 };
                short[] colours = { 1, 2, 3, 4, 5, 6, 21, 45, 86, 140, 162, 190, 20, 61, 127, 187, 154, 221, 16 };
                for (int i = 0; i < sizes.Length; i++)
                    Check.Equal("DN" + sizes[i], colours[i], PipeCatalog.WaterColour("DN" + sizes[i]));
                Check.Equal("unknown size", (short)7, PipeCatalog.WaterColour("DN35"));
                Check.Equal("no size", (short)7, PipeCatalog.WaterColour(null));
            });
        }

        private static void CreatedKinds()
        {
            Check.Case("kinds the command creates", () =>
            {
                Check.True("CWS", PipeCatalog.IsCreatedByCommand(PipeKind.Cws));
                Check.True("CWR", PipeCatalog.IsCreatedByCommand(PipeKind.Cwr));
                Check.True("HWS", PipeCatalog.IsCreatedByCommand(PipeKind.Hws));
                Check.True("HWR", PipeCatalog.IsCreatedByCommand(PipeKind.Hwr));
                Check.False("CHWS", PipeCatalog.IsCreatedByCommand(PipeKind.Chws));
                Check.False("condensate", PipeCatalog.IsCreatedByCommand(PipeKind.Condensate));
                Check.False("refrigerant", PipeCatalog.IsCreatedByCommand(PipeKind.Refrigerant));

                Check.Equal("CWS linetype layer", "EIA_1 COOLING WATER SUPPLY",
                    PipeCatalog.LinetypeSourceLayer(PipeKind.Cws));
                Check.Equal("CWR linetype layer", "EIA_1 COOLING WATER RETURN",
                    PipeCatalog.LinetypeSourceLayer(PipeKind.Cwr));
                Check.Equal("HWS linetype layer", "EIA_1 HOT WATER SUPPLY",
                    PipeCatalog.LinetypeSourceLayer(PipeKind.Hws));
                Check.Equal("HWR linetype layer", "EIA_1 HOT WATER RETURN",
                    PipeCatalog.LinetypeSourceLayer(PipeKind.Hwr));
                Check.Equal("CHWS has none", (string)null, PipeCatalog.LinetypeSourceLayer(PipeKind.Chws));
            });
        }

        private static void Pipe(string layer, bool expected)
        {
            string label = "pipe layer '" + layer + "'";
            Check.Case(label, () =>
            {
                if (expected)
                    Check.True(label, PipeCatalog.IsPipeLayer(layer));
                else
                    Check.False(label, PipeCatalog.IsPipeLayer(layer));
            });
        }

        private static void LayerFilter()
        {
            Pipe("EIA_1 CHWS DN40", true);
            Pipe("EIA_1 Pipe condensated DN21", true);
            Pipe("EIA_1 RP 28.6-12.7 (33.6-44.8)", true);
            Pipe("EIA_1 CHILLED WATER SUPPLY", true);
            Pipe("EIA_1 COOLING WATER RETURN", true);
            Pipe("EIA_1 HOT WATER SUPPLY", true);
            Pipe("EIA_1 DRAINAGE", true);
            Pipe("EIA_1 GAS PIPE", true);
            Pipe("EIA_5 GI-STEEL PIPE", true);
            Pipe("EIA_1 MAKE UP WATER", true);
            Pipe("EIA_1 Pipe condensated", true);
            Pipe("eia_1 chilled water supply", true);

            Pipe("EIA_1 SUPPLY AIR", false);
            Pipe("EIA_1 MECHANICAL EQUIPMENT", false);
            Pipe("EIA_1 DUCT ANNOTATIONS", false);
            Pipe("EIA_1 Airflow+velocity", false);
            Pipe("EIA_1 WATER VALVE", false);
            Pipe("EIA_1 PIPE TAG", false);
            Pipe("0", false);
            Pipe("", false);
            Pipe(null, false);
        }
    }
}
