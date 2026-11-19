using HvacAiCadAddin.Ducts;

namespace DuctCoreTests
{
    /// <summary>Duct size names: REC_&lt;W&gt;x&lt;H&gt; and Round D&lt;d&gt;, strict and typed.</summary>
    internal static class SizeTests
    {
        public static void Run()
        {
            BlockNames();
            RejectedBlockNames();
            TypedRectangular();
            TypedRound();
            TypedAny();
            Equality();
            LayerFilter();
        }

        private static void Block(string name, bool round, double width, double height, string expectedBlockName)
        {
            string label = "block name `" + name + "`";
            Check.Case(label, () =>
            {
                DuctSize size;
                bool parsed = DuctSize.TryParseBlockName(name, out size);
                Check.True(label + " parsed", parsed);
                if (!parsed)
                    return;
                Check.Equal(label + " round", round, size.IsRound);
                Check.Near(label + " width", width, size.Width);
                Check.Near(label + " height", height, size.Height);
                Check.Equal(label + " canonical name", expectedBlockName, size.BlockName);
            });
        }

        private static void BlockNames()
        {
            Block("REC_600x300", false, 600, 300, "REC_600x300");
            Block("REC_1000x250", false, 1000, 250, "REC_1000x250");
            Block("rec_600x300", false, 600, 300, "REC_600x300");
            Block("  REC_600x300 ", false, 600, 300, "REC_600x300");
            Block("REC_650.5x300", false, 650.5, 300, "REC_650.5x300");
            Block("Round D350", true, 350, 350, "Round D350");
            Block("round d350", true, 350, 350, "Round D350");
            Block("Round D125", true, 125, 125, "Round D125");
        }

        private static void Rejected(string name)
        {
            string label = "not a block name `" + name + "`";
            Check.Case(label, () =>
            {
                DuctSize size;
                Check.False(label, DuctSize.TryParseBlockName(name, out size));
            });
        }

        private static void RejectedBlockNames()
        {
            Rejected("");
            Rejected(null);
            Rejected("600x300");
            Rejected("REC_600");
            Rejected("REC_600x");
            Rejected("REC_600x300x2");
            Rejected("REC_0x300");
            Rejected("Round350");
            Rejected("Round D");
            Rejected("Round D350 copy");
            Rejected("HVAC_PUMP");
            Rejected("Branch Connection (1)");
        }

        private static void Typed(string text, bool roundShape, bool expectedValid, string expectedBlockName)
        {
            string label = "typed `" + text + "` as " + (roundShape ? "round" : "rectangular");
            Check.Case(label, () =>
            {
                DuctSize size;
                bool parsed = DuctSize.TryParseText(text, roundShape, out size);
                Check.Equal(label + " valid", expectedValid, parsed);
                if (parsed)
                    Check.Equal(label + " name", expectedBlockName, size.BlockName);
            });
        }

        private static void TypedRectangular()
        {
            Typed("600x300", false, true, "REC_600x300");
            Typed("600X300", false, true, "REC_600x300");
            Typed("600 x 300", false, true, "REC_600x300");
            Typed("600×300", false, true, "REC_600x300");
            Typed("600*300", false, true, "REC_600x300");
            Typed("REC_600x300", false, true, "REC_600x300");
            Typed("600x300mm", false, true, "REC_600x300");
            Typed("650,5x300", false, true, "REC_650.5x300");
            Typed("10x10", false, true, "REC_10x10");
            Typed("9x300", false, false, null);
            Typed("20000x300", false, false, null);
            Typed("0x300", false, false, null);
            Typed("600", false, false, null);
            Typed("600x", false, false, null);
            Typed("a x b", false, false, null);
            Typed("", false, false, null);
            Typed(null, false, false, null);
            Typed("D350", false, false, null);
        }

        private static void TypedRound()
        {
            Typed("350", true, true, "Round D350");
            Typed("D350", true, true, "Round D350");
            Typed("d350", true, true, "Round D350");
            Typed("Ø350", true, true, "Round D350");
            Typed("DN350", true, true, "Round D350");
            Typed("Round D350", true, true, "Round D350");
            Typed("350 mm", true, true, "Round D350");
            Typed("350,5", true, true, "Round D350.5");
            Typed("5", true, false, null);
            Typed("600x300", true, false, null);
            Typed("", true, false, null);
        }

        private static void Any(string text, bool currentIsRound, bool expectedValid, string expectedBlockName)
        {
            string label = "Size keyword `" + text + "` while " + (currentIsRound ? "round" : "rectangular");
            Check.Case(label, () =>
            {
                DuctSize size;
                bool parsed = DuctSize.TryParseAny(text, currentIsRound, out size);
                Check.Equal(label + " valid", expectedValid, parsed);
                if (parsed)
                    Check.Equal(label + " name", expectedBlockName, size.BlockName);
            });
        }

        private static void TypedAny()
        {
            Any("500x300", false, true, "REC_500x300");
            Any("500x300", true, true, "REC_500x300");
            Any("D350", false, true, "Round D350");
            Any("Ø250", false, true, "Round D250");
            Any("350", true, true, "Round D350");
            Any("350", false, false, null);
            Any("nonsense", true, false, null);
        }

        private static void Equality()
        {
            Check.Case("size equality", () =>
            {
                Check.True("same rect", DuctSize.Rect(600, 300).Equals(DuctSize.Rect(600, 300)));
                Check.False("rect differs by height", DuctSize.Rect(600, 300).Equals(DuctSize.Rect(600, 250)));
                Check.False("rect vs round", DuctSize.Rect(350, 350).Equals(DuctSize.Round(350)));
                Check.Equal("rect label", "600x300", DuctSize.Rect(600, 300).Label);
                Check.Equal("round label", "D350", DuctSize.Round(350).Label);
            });
        }

        private static void LayerFilter()
        {
            string[] ducts =
            {
                "EIA_1 SUPPLY AIR", "EIA_1 RETURN AIR", "EIA_1 OUTSIDE AIR", "EIA_1 EXHAUST AIR",
                "EIA_1 PRIMARY SUPPLY AIR", "EIA_1 PRIMARY RETURN AIR", "EIA_1 TOILET AIR",
                "EIA_2 EXHAUST SMOKE", "EIA_3 KITCHEN EXHAUST AIR", "eia_1 supply air"
            };
            string[] others =
            {
                "EIA_1 SUPPLY AIR TERMINALS", "EIA_1 DUCT ANNOTATIONS", "EIA_1 Airflow+velocity",
                "EIA_1 CHILLED WATER SUPPLY", "EIA_1 MECHANICAL EQUIPMENT", "EIA_1 CHWS DN40",
                "EIA_1 MAKE UP WATER", "EIA_4 VENTILATION", "EIA_2 EXHAUST SMOKE AIR TERMINALS", "0", "", null
            };
            foreach (string name in ducts)
            {
                string label = "duct layer `" + name + "`";
                Check.Case(label, () => Check.True(label, DuctLayerFilter.IsDuctSystemLayer(name)));
            }

            foreach (string name in others)
            {
                string label = "not a duct layer `" + name + "`";
                Check.Case(label, () => Check.False(label, DuctLayerFilter.IsDuctSystemLayer(name)));
            }
        }
    }
}
