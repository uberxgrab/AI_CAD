using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HvacAiCadAddin.Pipes
{
    // AutoCAD-free pipe layer catalogue for HVACPIPESIZE and HVACREFRIG: which pipe types and
    // sizes exist, their exact layer names, the colours of new water layers, and the name filter
    // that decides which layers count as pipes. Plain strings and numbers only, so
    // tests/PipeCoreTests compiles this file without AutoCAD. The tables come from AGENTS.md,
    // "HVACPIPESIZE: size a pipe run", "Layer catalogue".

    public enum PipeKind
    {
        Chws,
        Chwr,
        Condensate,
        Refrigerant,
        Cws,
        Cwr,
        Hws,
        Hwr
    }

    public static class PipeCatalog
    {
        /// <summary>The prefix every catalogue layer carries.</summary>
        public const string LayerPrefix = "EIA_1 ";

        /// <summary>Nominal sizes of the water layers, in millimetres (the DN list).</summary>
        public static readonly int[] WaterSizes =
            { 15, 20, 25, 32, 40, 50, 65, 80, 100, 125, 150, 200, 250, 300, 350, 400, 450, 500, 600 };

        // AutoCAD colour index per water size, in the same order as WaterSizes (the template's CHW table).
        private static readonly short[] WaterColours =
            { 1, 2, 3, 4, 5, 6, 21, 45, 86, 140, 162, 190, 20, 61, 127, 187, 154, 221, 16 };

        /// <summary>Outer diameters of the condensate (uPVC) layers.</summary>
        public static readonly int[] CondensateSizes = { 21, 27, 34, 48, 60, 90, 110 };

        /// <summary>
        /// The refrigerant layers' size text exactly as the template spells it after <c>EIA_1 RP </c>,
        /// bracket included.
        /// </summary>
        public static readonly string[] RefrigerantSizes =
        {
            "9.5-6.4",
            "12.7-6.4",
            "15.9-6.4",
            "15.9-9.5 (14)",
            "19.1-9.5 (22.4)",
            "22.2-9.5 (28)",
            "28.6-12.7 (33.6-44.8)",
            "28.6-15.9 (50.4-61.6)",
            "34.9-15.9 (67.2)",
            "34.9-19.1 (72.8-95.2)",
            "41.3-19.1 (100.8-151.2)"
        };

        private const string CondensatePrefix = "EIA_1 Pipe condensated DN";
        private const string RefrigerantPrefix = "EIA_1 RP ";

        private static readonly PipeKind[] AllKinds =
        {
            PipeKind.Chws, PipeKind.Chwr, PipeKind.Condensate, PipeKind.Refrigerant,
            PipeKind.Cws, PipeKind.Cwr, PipeKind.Hws, PipeKind.Hwr
        };

        public static IList<PipeKind> Kinds
        {
            get { return AllKinds; }
        }

        /// <summary>The short name shown in the dialog: CHWS, Condensate, Refrigerant, ...</summary>
        public static string Label(PipeKind kind)
        {
            switch (kind)
            {
                case PipeKind.Chws: return "CHWS";
                case PipeKind.Chwr: return "CHWR";
                case PipeKind.Cws: return "CWS";
                case PipeKind.Cwr: return "CWR";
                case PipeKind.Hws: return "HWS";
                case PipeKind.Hwr: return "HWR";
                case PipeKind.Condensate: return "Condensate";
                default: return "Refrigerant";
            }
        }

        public static bool IsWater(PipeKind kind)
        {
            return kind != PipeKind.Condensate && kind != PipeKind.Refrigerant;
        }

        /// <summary>
        /// True for CWS, CWR, HWS and HWR: their sized layers are not in the template, so the command
        /// creates them.
        /// </summary>
        public static bool IsCreatedByCommand(PipeKind kind)
        {
            return kind == PipeKind.Cws || kind == PipeKind.Cwr
                || kind == PipeKind.Hws || kind == PipeKind.Hwr;
        }

        /// <summary>The size texts of one type, in the order the dialog lists them: DN15, DN20, ...</summary>
        public static IList<string> SizeTexts(PipeKind kind)
        {
            List<string> texts = new List<string>();
            if (IsWater(kind))
            {
                foreach (int size in WaterSizes)
                    texts.Add("DN" + size.ToString(CultureInfo.InvariantCulture));
            }
            else if (kind == PipeKind.Condensate)
            {
                foreach (int size in CondensateSizes)
                    texts.Add("DN" + size.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                texts.AddRange(RefrigerantSizes);
            }

            return texts;
        }

        public static bool IsKnownSize(PipeKind kind, string sizeText)
        {
            if (string.IsNullOrEmpty(sizeText))
                return false;
            foreach (string text in SizeTexts(kind))
            {
                if (string.Equals(text, sizeText, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The exact layer name, for example <c>EIA_1 CHWS DN40</c> or
        /// <c>EIA_1 RP 28.6-12.7 (33.6-44.8)</c>. Returns null for a size the type does not offer.
        /// </summary>
        public static string LayerName(PipeKind kind, string sizeText)
        {
            if (!IsKnownSize(kind, sizeText))
                return null;

            // Return the catalogue spelling, whatever the caller's case.
            string canonical = null;
            foreach (string text in SizeTexts(kind))
            {
                if (string.Equals(text, sizeText, StringComparison.OrdinalIgnoreCase))
                {
                    canonical = text;
                    break;
                }
            }

            if (kind == PipeKind.Refrigerant)
                return RefrigerantPrefix + canonical;
            if (kind == PipeKind.Condensate)
                return CondensatePrefix + canonical.Substring(2);
            return LayerPrefix + Label(kind) + " " + canonical;
        }

        private static readonly Regex WaterLayer = new Regex(
            @"^EIA_1 (CHWS|CHWR|CWS|CWR|HWS|HWR) DN(\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex CondensateLayer = new Regex(
            @"^EIA_1 Pipe condensated DN(\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Reads a layer name back into a catalogue type and size; false for any other layer.</summary>
        public static bool TryParseLayer(string layer, out PipeKind kind, out string sizeText)
        {
            kind = PipeKind.Chws;
            sizeText = null;
            if (string.IsNullOrWhiteSpace(layer))
                return false;

            string name = layer.Trim();
            Match water = WaterLayer.Match(name);
            if (water.Success)
            {
                PipeKind parsed;
                if (!TryKind(water.Groups[1].Value, out parsed))
                    return false;
                string text = "DN" + water.Groups[2].Value;
                if (!IsKnownSize(parsed, text))
                    return false;
                kind = parsed;
                sizeText = text;
                return true;
            }

            Match condensate = CondensateLayer.Match(name);
            if (condensate.Success)
            {
                string text = "DN" + condensate.Groups[1].Value;
                if (!IsKnownSize(PipeKind.Condensate, text))
                    return false;
                kind = PipeKind.Condensate;
                sizeText = text;
                return true;
            }

            if (name.StartsWith(RefrigerantPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string text = name.Substring(RefrigerantPrefix.Length).Trim();
                foreach (string known in RefrigerantSizes)
                {
                    if (string.Equals(known, text, StringComparison.OrdinalIgnoreCase))
                    {
                        kind = PipeKind.Refrigerant;
                        sizeText = known;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryKind(string word, out PipeKind kind)
        {
            switch (word.ToUpperInvariant())
            {
                case "CHWS": kind = PipeKind.Chws; return true;
                case "CHWR": kind = PipeKind.Chwr; return true;
                case "CWS": kind = PipeKind.Cws; return true;
                case "CWR": kind = PipeKind.Cwr; return true;
                case "HWS": kind = PipeKind.Hws; return true;
                case "HWR": kind = PipeKind.Hwr; return true;
                default: kind = PipeKind.Chws; return false;
            }
        }

        /// <summary>The AutoCAD colour index of a new water layer, from the CHW table. 7 for an unknown size.</summary>
        public static short WaterColour(string sizeText)
        {
            int size;
            if (sizeText != null
                && sizeText.StartsWith("DN", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(
                    sizeText.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out size))
            {
                for (int i = 0; i < WaterSizes.Length; i++)
                {
                    if (WaterSizes[i] == size)
                        return WaterColours[i];
                }
            }

            return 7;
        }

        /// <summary>
        /// The template layer whose linetype a new CW/HW layer copies: <c>EIA_1 COOLING WATER SUPPLY</c> and
        /// so on. Null for the types whose layers the template already holds.
        /// </summary>
        public static string LinetypeSourceLayer(PipeKind kind)
        {
            switch (kind)
            {
                case PipeKind.Cws: return "EIA_1 COOLING WATER SUPPLY";
                case PipeKind.Cwr: return "EIA_1 COOLING WATER RETURN";
                case PipeKind.Hws: return "EIA_1 HOT WATER SUPPLY";
                case PipeKind.Hwr: return "EIA_1 HOT WATER RETURN";
                default: return null;
            }
        }

        // Words that mark a layer as carrying pipework when the size is not in the name: the template's
        // unsized water, drain, gas and refrigerant layers (EIA_1 CHILLED WATER SUPPLY, EIA_1 DRAINAGE,
        // EIA_1 GAS PIPE, EIA_5 GI-STEEL PIPE, EIA_1 Pipe condensated, ...).
        private static readonly Regex PipeWord = new Regex(
            @"(^|[^A-Za-z])(WATER|DRAIN|DRAINAGE|GAS|PIPE|PIPES|PIPING|CONDENSATE|CONDENSATED|REFRIGERANT|RP"
            + @"|CHWS|CHWR|CWS|CWR|HWS|HWR)([^A-Za-z]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Layers that hold annotation or equipment rather than pipe curves.
        private static readonly Regex NotPipe = new Regex(
            @"TERMINAL|ANNOTATION|TAG|TEXT|DIM|LABEL|NOTE|SYMBOL|HATCH|EQUIPMENT|VALVE|INSULATION|AIRFLOW|DUCT",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// True for a layer that may carry pipe curves: a catalogue layer, or an unsized layer whose name
        /// has a water, drain, gas, pipe or refrigerant word and no annotation or equipment word.
        /// The picked layer always counts as well; the command adds it separately.
        /// </summary>
        public static bool IsPipeLayer(string layer)
        {
            if (string.IsNullOrWhiteSpace(layer))
                return false;

            PipeKind kind;
            string size;
            if (TryParseLayer(layer, out kind, out size))
                return true;

            return PipeWord.IsMatch(layer) && !NotPipe.IsMatch(layer);
        }
    }
}
