using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace HvacAiCadAddin.Takeoff
{
    public sealed class TakeoffBlockInput
    {
        public TakeoffBlockInput(string name)
        {
            Name = name ?? "";
            Attributes = new List<KeyValuePair<string, string>>();
            DynamicProperties = new List<KeyValuePair<string, string>>();
        }

        public string Name { get; }
        public IList<KeyValuePair<string, string>> Attributes { get; }
        public IList<KeyValuePair<string, string>> DynamicProperties { get; }
        public double? Distance3 { get; set; }

        public TakeoffBlockInput AddAttribute(string tag, string value)
        {
            Attributes.Add(new KeyValuePair<string, string>(tag, value));
            return this;
        }

        public TakeoffBlockInput AddDynamicProperty(string name, string value)
        {
            DynamicProperties.Add(new KeyValuePair<string, string>(name, value));
            return this;
        }
    }

    public sealed class TakeoffCurveInput
    {
        public TakeoffCurveInput(string layer, double lengthMm)
        {
            Layer = layer ?? "";
            LengthMm = lengthMm;
        }

        public string Layer { get; }
        public double LengthMm { get; }

        /// <summary>The HVACNORM size tag (xdata HVACAI_PIPE / HVACAI_DUCT), or null.</summary>
        public SizeTag Tag { get; set; }
    }

    public sealed class DuctGaugeBand
    {
        public DuctGaugeBand(double from, double? to, double thickness)
        {
            From = from;
            To = to;
            Thickness = thickness;
        }

        public double From { get; }
        public double? To { get; }
        public double Thickness { get; }
    }

    public sealed class DuctGaugeSettings
    {
        public const int BandCount = 4;

        public DuctGaugeSettings(IEnumerable<DuctGaugeBand> bands)
        {
            Bands = bands.ToList().AsReadOnly();
        }

        public IReadOnlyList<DuctGaugeBand> Bands { get; }

        public static DuctGaugeSettings CreateDefault()
        {
            return new DuctGaugeSettings(new[]
            {
                new DuctGaugeBand(100, 550, 0.58),
                new DuctGaugeBand(600, 1200, 0.75),
                new DuctGaugeBand(1250, 1950, 0.95),
                new DuctGaugeBand(2000, null, 1.15)
            });
        }

        /// <summary>
        /// Parses and validates dialog rows. Each row is { From, To, Thickness } as typed text.
        /// </summary>
        public static bool TryParse(IList<string[]> rows, out DuctGaugeSettings settings, out string error)
        {
            settings = null;
            if (rows == null || rows.Count != BandCount)
            {
                error = "Phải có đúng " + BandCount + " dòng.";
                return false;
            }

            var bands = new List<DuctGaugeBand>();
            for (int index = 0; index < rows.Count; index++)
            {
                string[] row = rows[index];
                int rowNumber = index + 1;
                if (row == null || row.Length != 3)
                {
                    error = "Dòng " + rowNumber + ": phải có đủ 3 cột.";
                    return false;
                }

                double? from;
                double? to;
                double? thickness;
                if (!TryParseOptional(row[0], out from))
                {
                    error = InvalidNumberMessage(rowNumber, "Từ");
                    return false;
                }
                if (!TryParseOptional(row[1], out to))
                {
                    error = InvalidNumberMessage(rowNumber, "Đến");
                    return false;
                }
                if (!TryParseOptional(row[2], out thickness))
                {
                    error = InvalidNumberMessage(rowNumber, "Chiều dày tôn");
                    return false;
                }
                bands.Add(new DuctGaugeBand(from ?? double.NaN, to, thickness ?? double.NaN));
            }

            DuctGaugeSettings parsed = new DuctGaugeSettings(bands);
            if (!parsed.TryValidate(out error))
                return false;

            settings = parsed;
            return true;
        }

        public bool TryValidate(out string error)
        {
            if (Bands.Count != BandCount)
            {
                error = "Phải có đúng " + BandCount + " dòng.";
                return false;
            }

            for (int index = 0; index < Bands.Count; index++)
            {
                DuctGaugeBand band = Bands[index];
                int rowNumber = index + 1;
                bool last = index == Bands.Count - 1;

                if (!IsPositive(band.From))
                {
                    error = "Dòng " + rowNumber + ": \"Từ\" phải là số dương.";
                    return false;
                }
                if (!IsPositive(band.Thickness))
                {
                    error = "Dòng " + rowNumber + ": \"Chiều dày tôn\" phải lớn hơn 0.";
                    return false;
                }
                if (last)
                {
                    if (band.To.HasValue)
                    {
                        error = "Dòng " + rowNumber + ": dòng cuối không được có giá trị \"Đến\".";
                        return false;
                    }
                }
                else
                {
                    if (!band.To.HasValue || !IsPositive(band.To.Value))
                    {
                        error = "Dòng " + rowNumber + ": \"Đến\" phải là số dương.";
                        return false;
                    }
                    if (band.From >= band.To.Value)
                    {
                        error = "Dòng " + rowNumber + ": \"Từ\" phải nhỏ hơn \"Đến\".";
                        return false;
                    }
                }
                if (index > 0 && band.From <= Bands[index - 1].To.Value)
                {
                    error = "Dòng " + rowNumber + ": \"Từ\" phải lớn hơn \"Đến\" của dòng trước.";
                    return false;
                }
            }

            error = null;
            return true;
        }

        public int FindBandIndex(double decidingSize)
        {
            for (int index = 0; index < Bands.Count - 1; index++)
            {
                if (decidingSize <= Bands[index].To.Value)
                    return index;
            }
            return Bands.Count - 1;
        }

        public bool IsBelowFirstBand(double decidingSize)
        {
            return decidingSize < Bands[0].From;
        }

        internal static bool TryParseOptional(string text, out double? value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(text))
                return true;

            const NumberStyles styles = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite |
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
            double parsed;
            if (!double.TryParse(text.Replace(',', '.'), styles, CultureInfo.InvariantCulture, out parsed) ||
                double.IsNaN(parsed) || double.IsInfinity(parsed))
                return false;

            value = parsed;
            return true;
        }

        internal static bool IsPositive(double value)
        {
            return value > 0 && !double.IsInfinity(value);
        }

        private static string InvalidNumberMessage(int rowNumber, string column)
        {
            return "Dòng " + rowNumber + ": \"" + column + "\" không phải là số hợp lệ.";
        }
    }

    public enum ShaftPipeKind
    {
        Duct,
        Steel,
        Upvc,
        Ppr,
        Hdpe,
        UpvcWaste
    }

    /// <summary>
    /// A riser run inside a services shaft, which the drawing does not show.
    /// </summary>
    public sealed class ShaftPipeInput
    {
        public ShaftPipeInput(ShaftPipeKind kind, string size, double heightMeters, int count)
        {
            Kind = kind;
            Size = (size ?? "").Trim();
            HeightMeters = heightMeters;
            Count = count;
        }

        public ShaftPipeKind Kind { get; }
        public string Size { get; }
        public double HeightMeters { get; }
        public int Count { get; }
    }

    public sealed class TakeoffOptions
    {
        public const double DefaultContingencyFactor = 1.0;

        private static readonly string[] ShaftKindLabels =
        {
            "Ống gió", "Ống thép", "Ống nước ngưng", "Ống PPR", "Ống HDPE", "Ống uPVC thoát nước"
        };

        public TakeoffOptions(DuctGaugeSettings gauge, double contingencyFactor, IEnumerable<ShaftPipeInput> shaftPipes)
        {
            Gauge = gauge;
            ContingencyFactor = contingencyFactor;
            ShaftPipes = (shaftPipes ?? Enumerable.Empty<ShaftPipeInput>()).ToList().AsReadOnly();
        }

        public DuctGaugeSettings Gauge { get; }
        public double ContingencyFactor { get; }
        public IReadOnlyList<ShaftPipeInput> ShaftPipes { get; }

        public static IReadOnlyList<string> ShaftKindNames
        {
            get { return Array.AsReadOnly(ShaftKindLabels); }
        }

        public static TakeoffOptions CreateDefault()
        {
            return new TakeoffOptions(DuctGaugeSettings.CreateDefault(), DefaultContingencyFactor, null);
        }

        public static string GetShaftKindName(ShaftPipeKind kind)
        {
            return ShaftKindLabels[(int)kind];
        }

        public static bool TryParseShaftKind(string text, out ShaftPipeKind kind)
        {
            string value = (text ?? "").Trim();
            for (int index = 0; index < ShaftKindLabels.Length; index++)
            {
                ShaftPipeKind candidate = (ShaftPipeKind)index;
                if (string.Equals(value, ShaftKindLabels[index], StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    kind = candidate;
                    return true;
                }
            }
            kind = ShaftPipeKind.Duct;
            return false;
        }

        /// <summary>
        /// Parses and validates dialog input. Gauge rows are { From, To, Thickness } and shaft rows are
        /// { Kind, Size, Height (m), Count }. Fully blank shaft rows are ignored; a blank count means 1.
        /// </summary>
        public static bool TryParse(string factorText, IList<string[]> gaugeRows, IList<string[]> shaftRows,
            out TakeoffOptions options, out string error)
        {
            options = null;
            double? factor;
            if (!DuctGaugeSettings.TryParseOptional(factorText, out factor) || !factor.HasValue)
            {
                error = "\"Hệ số dự phòng\" không phải là số hợp lệ.";
                return false;
            }

            DuctGaugeSettings gauge;
            if (!DuctGaugeSettings.TryParse(gaugeRows, out gauge, out error))
                return false;

            var shafts = new List<ShaftPipeInput>();
            IList<string[]> rows = shaftRows ?? new List<string[]>();
            for (int index = 0; index < rows.Count; index++)
            {
                string[] row = rows[index];
                int rowNumber = index + 1;
                if (row == null || row.All(string.IsNullOrWhiteSpace))
                    continue;
                if (row.Length != 4)
                {
                    error = "Ống trục kỹ thuật, dòng " + rowNumber + ": phải có đủ 4 cột.";
                    return false;
                }

                ShaftPipeKind kind;
                if (!TryParseShaftKind(row[0], out kind))
                {
                    error = "Ống trục kỹ thuật, dòng " + rowNumber + ": chọn loại ống.";
                    return false;
                }
                double? height;
                if (!DuctGaugeSettings.TryParseOptional(row[2], out height) || !height.HasValue)
                {
                    error = "Ống trục kỹ thuật, dòng " + rowNumber + ": \"Chiều cao\" không phải là số hợp lệ.";
                    return false;
                }
                int count = 1;
                if (!string.IsNullOrWhiteSpace(row[3]) &&
                    !int.TryParse(row[3].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out count))
                {
                    error = "Ống trục kỹ thuật, dòng " + rowNumber + ": \"Số đoạn\" phải là số nguyên dương.";
                    return false;
                }
                shafts.Add(new ShaftPipeInput(kind, row[1], height.Value, count));
            }

            TakeoffOptions parsed = new TakeoffOptions(gauge, factor.Value, shafts);
            if (!parsed.TryValidate(out error))
                return false;

            options = parsed;
            return true;
        }

        public bool TryValidate(out string error)
        {
            if (Gauge == null)
            {
                error = "Thiếu bảng chiều dày tôn.";
                return false;
            }
            if (!Gauge.TryValidate(out error))
                return false;
            if (!DuctGaugeSettings.IsPositive(ContingencyFactor))
            {
                error = "\"Hệ số dự phòng\" phải lớn hơn 0.";
                return false;
            }

            for (int index = 0; index < ShaftPipes.Count; index++)
            {
                ShaftPipeInput shaft = ShaftPipes[index];
                string prefix = "Ống trục kỹ thuật, dòng " + (index + 1) + ": ";
                if (!TakeoffClassifier.IsValidShaftSize(shaft.Kind, shaft.Size))
                {
                    error = prefix + "kích thước \"" + shaft.Size + "\" không hợp lệ cho " +
                        GetShaftKindName(shaft.Kind).ToLowerInvariant() + ".";
                    return false;
                }
                if (!DuctGaugeSettings.IsPositive(shaft.HeightMeters))
                {
                    error = prefix + "\"Chiều cao\" phải lớn hơn 0.";
                    return false;
                }
                if (shaft.Count < 1)
                {
                    error = prefix + "\"Số đoạn\" phải là số nguyên dương.";
                    return false;
                }
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Text form used to remember the last dialog values between runs.
        /// </summary>
        public string Serialize()
        {
            StringBuilder text = new StringBuilder();
            text.Append("factor=").Append(Format(ContingencyFactor)).Append('\n');
            foreach (DuctGaugeBand band in Gauge.Bands)
            {
                text.Append("band=").Append(Format(band.From)).Append('|')
                    .Append(band.To.HasValue ? Format(band.To.Value) : "").Append('|')
                    .Append(Format(band.Thickness)).Append('\n');
            }
            foreach (ShaftPipeInput shaft in ShaftPipes)
            {
                text.Append("shaft=").Append(shaft.Kind).Append('|').Append(shaft.Size.Replace("|", ""))
                    .Append('|').Append(Format(shaft.HeightMeters))
                    .Append('|').Append(shaft.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            return text.ToString();
        }

        /// <summary>
        /// Reads text written by <see cref="Serialize"/>. Anything missing or invalid gives the defaults.
        /// </summary>
        public static TakeoffOptions Deserialize(string text)
        {
            string factor = null;
            var bands = new List<string[]>();
            var shafts = new List<string[]>();
            foreach (string rawLine in (text ?? "").Split('\n'))
            {
                string line = rawLine.Trim();
                int separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;
                string key = line.Substring(0, separator);
                string value = line.Substring(separator + 1);
                if (key == "factor")
                    factor = value;
                else if (key == "band")
                    bands.Add(value.Split('|'));
                else if (key == "shaft")
                    shafts.Add(value.Split('|'));
            }

            TakeoffOptions options;
            string error;
            return TryParse(factor, bands, shafts, out options, out error) ? options : CreateDefault();
        }

        private static string Format(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    public sealed class DuctSize
    {
        internal DuctSize(string text, bool isRound, double widthMm, double heightMm)
        {
            Text = text;
            IsRound = isRound;
            WidthMm = widthMm;
            HeightMm = heightMm;
        }

        public string Text { get; }
        public bool IsRound { get; }
        /// <summary>Width, or the diameter for a round duct.</summary>
        public double WidthMm { get; }
        /// <summary>Height, or the diameter for a round duct.</summary>
        public double HeightMm { get; }

        public double DecidingSize
        {
            get { return Math.Max(WidthMm, HeightMm); }
        }

        public double PerimeterMm
        {
            get { return IsRound ? Math.PI * WidthMm : 2 * (WidthMm + HeightMm); }
        }
    }

    /// <summary>
    /// One addend of a quantity formula: a drawn segment, or a shaft run of height x count.
    /// </summary>
    public sealed class QuantityTerm
    {
        internal QuantityTerm(double meters, int multiplier, bool fromShaft)
        {
            Meters = meters;
            Multiplier = multiplier;
            FromShaft = fromShaft;
        }

        public double Meters { get; }
        public int Multiplier { get; }
        public bool FromShaft { get; }
    }

    public enum PipeMaterial
    {
        None,
        Copper,
        Upvc,
        Steel,
        /// <summary>Tagged pipes kept on their own layer (BOQ prefix) and plumbing shaft risers.</summary>
        Other
    }

    public sealed class PipeLayerClassification
    {
        internal PipeLayerClassification(PipeMaterial material, params double[] sizesMm)
        {
            Material = material;
            SizesMm = Array.AsReadOnly(sizesMm);
        }

        public PipeMaterial Material { get; }
        public IReadOnlyList<double> SizesMm { get; }
    }

    public sealed class EquipmentDescription
    {
        internal EquipmentDescription(int sortRank, string text, string unit)
        {
            SortRank = sortRank;
            Text = text;
            Unit = unit;
        }

        public int SortRank { get; }
        public string Text { get; }
        public string Unit { get; }
    }

    public static class TakeoffClassifier
    {
        private const RegexOptions PatternOptions =
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private const string Number = @"(?>[0-9]+(?:[.,][0-9]+)?)";
        private const string PumpNumber = @"(?>[0-9]+(?:,[0-9]{3}(?![0-9]))*(?:[.,][0-9]+)?)";
        private const string NoLetterOrDigitBefore = @"(?<![\p{L}\p{Nd}])";
        private const string NoLetterOrDigitAfter = @"(?![\p{L}\p{Nd}])";
        private const string NotInsideNumber = @"(?<![0-9])(?<![0-9][.,])";

        private const int OduRank = 0;
        private const int CassetteRank = 1;
        private const int ChillerRank = 2;
        private const int PumpRank = 3;
        private const int OtherRank = 4;
        private const string MappedUnit = "bộ";
        private const string FallbackUnit = "cái";
        private const string PumpLabelBlock = "HVAC-NAME OF EQUIPMENTS";
        private const string PumpSymbolBlock = "HVAC_PUMP";

        // CWS/CWR (cooling water) is steel too: user decision of 2026-11-18.
        private static readonly string[] SteelLayerMarkers = { "CHWS", "CHWR", "HWS", "HWR", "CWS", "CWR" };
        private static readonly string[] CondensateLayerMarkers = { "CONDENSATED" };
        private static readonly Dictionary<string, string> CopperWallThickness = new Dictionary<string, string>
        {
            { "6.4", "0.8mm" }, { "9.5", "0.8mm" },
            { "12.7", "1.0mm" }, { "15.9", "1.0mm" }, { "19.1", "1.0mm" }, { "22.2", "1.0mm" },
            { "28.6", "1.0mm" }, { "34.9", "1.0mm" }, { "41.3", "1.0mm" }
        };

        private static readonly Regex DiameterSymbol = new Regex(
            NoLetterOrDigitBefore + "DN?(" + Number + ")" + NoLetterOrDigitAfter, PatternOptions);
        private static readonly Regex CopperPair = new Regex(
            NoLetterOrDigitBefore + NotInsideNumber + "(?:Ø|D)?(" + Number + @")\s*/\s*(?:Ø|D)?(" + Number + ")" +
            NoLetterOrDigitAfter, PatternOptions);
        private static readonly Regex CopperRpPair = new Regex(
            NoLetterOrDigitBefore + @"RP\s*(?:Ø|D)?(" + Number + @")\s*-\s*(?:Ø|D)?(" + Number + ")" +
            NoLetterOrDigitAfter, PatternOptions);
        private static readonly Regex ParenthesizedPart = new Regex(@"\([^()]*\)", PatternOptions);
        private static readonly Regex NominalSize = new Regex(
            @"^\s*(?:DN|D|Ø)?\s*(" + Number + @")\s*(?:mm)?\s*$", PatternOptions);

        private static readonly Regex DimensionSequence = new Regex(
            NotInsideNumber + Number + @"(?:\s*[x×*]\s*" + Number + ")+", PatternOptions);
        private static readonly Regex DimensionSeparator = new Regex(@"\s*[x×*]\s*", PatternOptions);
        private static readonly Regex RoundSize = new Regex(
            "(?:Ø|" + NoLetterOrDigitBefore + @"DN?)\s*(" + Number + @")(?:\s*mm)?" + NoLetterOrDigitAfter,
            PatternOptions);

        private static readonly Regex OduBlock = new Regex(
            @"^\s*ODU\s*-\s*(" + Number + @")\s*HP\s*$", PatternOptions);
        private static readonly Regex CassetteBlock = new Regex(
            @"^\s*FCU\s*-\s*CAS\s*-\s*(" + Number + @")\s*kW\s*$", PatternOptions);
        private static readonly Regex ChillerBlock = new Regex(
            @"^\s*Water\s+Cooled\s+Chiller\s*-\s*(" + Number + @")\s*RT\s*$", PatternOptions);
        private static readonly Regex PumpFlow = new Regex(
            NotInsideNumber + "(" + PumpNumber + @")\s*LPM" + NoLetterOrDigitAfter, PatternOptions);
        private static readonly Regex PumpHead = new Regex(
            NotInsideNumber + "(" + PumpNumber + @")\s*mH2[O0]" + NoLetterOrDigitAfter, PatternOptions);

        private static readonly Regex PumpThousandsSeparator = new Regex(
            ",(?=[0-9]{3}(?![0-9]))", RegexOptions.Compiled);

        private static readonly Regex NaturalToken = new Regex(
            @"[0-9]+(?:\.[0-9]+)?|[^0-9]+", RegexOptions.Compiled);

        public static IComparer<string> NaturalComparer { get; } = Comparer<string>.Create(CompareNatural);

        public static string FormatNumber(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        public static PipeLayerClassification ClassifyPipeLayer(string layer)
        {
            string name = layer ?? "";
            Match symbol = DiameterSymbol.Match(name);
            double[] symbolSize = symbol.Success
                ? new[] { ParseNumber(symbol.Groups[1].Value) }
                : new double[0];

            if (ContainsAny(name, SteelLayerMarkers))
                return new PipeLayerClassification(PipeMaterial.Steel, symbolSize);

            string unparenthesized = StripParenthesized(name);
            Match pair = CopperPair.Match(unparenthesized);
            if (!pair.Success)
                pair = CopperRpPair.Match(unparenthesized);
            if (pair.Success)
            {
                return new PipeLayerClassification(PipeMaterial.Copper,
                    ParseNumber(pair.Groups[1].Value), ParseNumber(pair.Groups[2].Value));
            }

            if (symbol.Success || ContainsAny(name, CondensateLayerMarkers))
                return new PipeLayerClassification(PipeMaterial.Upvc, symbolSize);

            return new PipeLayerClassification(PipeMaterial.None);
        }

        public static string DescribePipe(PipeMaterial material, double? sizeMm, string layer)
        {
            switch (material)
            {
                case PipeMaterial.Copper:
                    return DescribeCopper(sizeMm.Value);
                case PipeMaterial.Upvc:
                    return sizeMm.HasValue
                        ? "Ống thoát nước ngưng uPVC Class 1 D" + FormatNumber(sizeMm.Value) + ", kèm bảo ôn 13mm"
                        : "Ống thoát nước ngưng uPVC Class 1, kèm bảo ôn 13mm (layer: " + layer + ")";
                case PipeMaterial.Steel:
                    return sizeMm.HasValue
                        ? "Ống thép hàn mạ kẽm DN" + FormatNumber(sizeMm.Value) + ", SCH40"
                        : "Ống thép hàn mạ kẽm, SCH40 (layer: " + layer + ")";
                default:
                    throw new ArgumentOutOfRangeException(nameof(material));
            }
        }

        /// <summary>A single-line duct tagged by HVACNORM, as a duct size.</summary>
        public static bool TryReadDuctTag(SizeTag tag, out DuctSize size)
        {
            size = null;
            if (tag == null || tag.Kind != SizeTagKind.Duct || !(tag.SizeMm > 0))
                return false;
            if (tag.Round || !(tag.Size2Mm > 0))
            {
                size = new DuctSize("Ø" + FormatNumber(tag.SizeMm) + " mm", true, tag.SizeMm, tag.SizeMm);
                return true;
            }

            size = new DuctSize(FormatNumber(tag.SizeMm) + " x " + FormatNumber(tag.Size2Mm) + " mm", false,
                tag.SizeMm, tag.Size2Mm);
            return true;
        }

        /// <summary>
        /// BOQ lines of a tagged pipe kept on its own layer: "&lt;prefix&gt; - &lt;description&gt;" (user decision of
        /// 2026-11-23). A refrigerant pair gives its gas and liquid line.
        /// </summary>
        public static IEnumerable<string> DescribeTaggedPipe(SizeTag tag, string layer)
        {
            string prefix = string.IsNullOrWhiteSpace(tag.Prefix) ? tag.System : tag.Prefix.Trim();
            prefix = string.IsNullOrWhiteSpace(prefix) ? "" : prefix.Trim() + " - ";
            if (tag.Size2Mm > 0)
            {
                yield return prefix + DescribeCopper(tag.SizeMm);
                yield return prefix + DescribeCopper(tag.Size2Mm);
                yield break;
            }

            yield return prefix + DescribePlumbing(tag.Material, tag.SizeMm, tag.System, layer);
        }

        /// <summary>
        /// Description of a pipe by material (proposed wording, 2026-11-23). Plastic sizes are outside
        /// diameters (D), steel and stainless are nominal (DN).
        /// </summary>
        public static string DescribePlumbing(string material, double sizeMm, string system, string layer = null)
        {
            string d = "D" + FormatNumber(sizeMm);
            string dn = "DN" + FormatNumber(sizeMm);
            string mat = (material ?? "").Trim().ToUpperInvariant();
            string sys = (system ?? "").Trim().ToUpperInvariant();
            switch (mat)
            {
                case "PPR":
                    return sys == "DHW" ? "Ống PPR PN20 " + d : "Ống PPR PN10 " + d;
                case "HDPE":
                    return "Ống HDPE PE100 PN10 " + d;
                case "UPVC":
                    return sys == "COND" ? DescribePipe(PipeMaterial.Upvc, sizeMm, layer) : "Ống uPVC Class 2 " + d;
                case "CPVC":
                    return "Ống CPVC " + d;
                case "PE":
                    return "Ống PE " + d;
                case "ABS":
                    return "Ống ABS " + d;
                case "SUS304":
                case "SUS316":
                    return "Ống inox " + mat + " " + dn;
                case "STEEL":
                case "GI":
                    return DescribePipe(PipeMaterial.Steel, sizeMm, layer);
                case "CU":
                    return DescribeCopper(sizeMm);
                default:
                    return "Ống " + d + " (chưa rõ vật liệu" + (string.IsNullOrEmpty(layer) ? "" : ", layer: " + layer) + ")";
            }
        }

        /// <summary>The material of a plumbing shaft riser kind.</summary>
        public static string ShaftMaterial(ShaftPipeKind kind)
        {
            switch (kind)
            {
                case ShaftPipeKind.Ppr: return "PPR";
                case ShaftPipeKind.Hdpe: return "HDPE";
                case ShaftPipeKind.UpvcWaste: return "UPVC";
                default: return "";
            }
        }

        public static bool TryParseDuctSize(string blockName, out DuctSize size)
        {
            size = null;
            string name = blockName ?? "";

            MatchCollection sequences = DimensionSequence.Matches(name);
            if (sequences.Count > 0)
            {
                string[] dimensions = DimensionSeparator.Split(sequences[0].Value);
                if (sequences.Count != 1 || dimensions.Length != 2)
                    return false;

                double width = ParseNumber(dimensions[0]);
                double height = ParseNumber(dimensions[1]);
                if (width <= 0 || height <= 0)
                    return false;

                size = new DuctSize(FormatNumber(width) + " x " + FormatNumber(height) + " mm", false, width, height);
                return true;
            }

            Match round = RoundSize.Match(name);
            if (!round.Success)
                return false;

            double diameter = ParseNumber(round.Groups[1].Value);
            if (diameter <= 0)
                return false;

            size = new DuctSize("Ø" + FormatNumber(diameter) + " mm", true, diameter, diameter);
            return true;
        }

        /// <summary>
        /// Reads a pipe size typed in the dialog: <c>100</c>, <c>DN100</c>, <c>D34</c> or <c>Ø34</c>.
        /// </summary>
        public static bool TryParseNominalSize(string text, out double sizeMm)
        {
            sizeMm = 0;
            Match match = NominalSize.Match(text ?? "");
            if (!match.Success)
                return false;
            sizeMm = ParseNumber(match.Groups[1].Value);
            return sizeMm > 0;
        }

        public static bool IsValidShaftSize(ShaftPipeKind kind, string size)
        {
            if (kind == ShaftPipeKind.Duct)
            {
                DuctSize duct;
                return TryParseDuctSize(size, out duct);
            }
            double sizeMm;
            return TryParseNominalSize(size, out sizeMm);
        }

        public static bool TryReadDuct(TakeoffBlockInput block, out DuctSize size)
        {
            size = null;
            return block.Distance3.HasValue && block.Distance3.Value > 0 &&
                TryParseDuctSize(block.Name, out size);
        }

        public static bool IsPumpLabel(TakeoffBlockInput block)
        {
            string prefix;
            return TryGetPumpTitle(block, out prefix);
        }

        public static bool IsPumpSymbol(TakeoffBlockInput block)
        {
            return string.Equals(block.Name.Trim(), PumpSymbolBlock, StringComparison.OrdinalIgnoreCase);
        }

        public static EquipmentDescription DescribeEquipment(TakeoffBlockInput block)
        {
            Match odu = OduBlock.Match(block.Name);
            if (odu.Success)
            {
                return new EquipmentDescription(OduRank,
                    "Dàn nóng ODU " + FormatMatchedNumber(odu) + " HP", MappedUnit);
            }

            Match cassette = CassetteBlock.Match(block.Name);
            if (cassette.Success)
            {
                return new EquipmentDescription(CassetteRank,
                    "Dàn lạnh VRV Cassette âm trần Inverter " + FormatMatchedNumber(cassette) + " kW", MappedUnit);
            }

            Match chiller = ChillerBlock.Match(block.Name);
            if (chiller.Success)
            {
                return new EquipmentDescription(ChillerRank,
                    "Chiller giải nhiệt nước:\n- Công suất lạnh: " + FormatMatchedNumber(chiller) + " RT", MappedUnit);
            }

            string pumpTitle;
            if (TryGetPumpTitle(block, out pumpTitle))
                return new EquipmentDescription(PumpRank, pumpTitle + DescribePumpLines(block), MappedUnit);

            string parameters = BuildCadParameters(block);
            string text = parameters.Length == 0 ? block.Name : block.Name + "\n" + parameters;
            return new EquipmentDescription(OtherRank, text, FallbackUnit);
        }

        public static int CompareNatural(string left, string right)
        {
            MatchCollection leftTokens = NaturalToken.Matches(left ?? "");
            MatchCollection rightTokens = NaturalToken.Matches(right ?? "");
            int shared = Math.Min(leftTokens.Count, rightTokens.Count);
            for (int index = 0; index < shared; index++)
            {
                string leftToken = leftTokens[index].Value;
                string rightToken = rightTokens[index].Value;
                int result = IsAsciiDigit(leftToken[0]) && IsAsciiDigit(rightToken[0])
                    ? ParseNumber(leftToken).CompareTo(ParseNumber(rightToken))
                    : string.Compare(leftToken, rightToken, StringComparison.OrdinalIgnoreCase);
                if (result != 0)
                    return result;
            }
            return leftTokens.Count.CompareTo(rightTokens.Count);
        }

        private static string DescribeCopper(double sizeMm)
        {
            string size = FormatNumber(sizeMm);
            string wall;
            string thickness = CopperWallThickness.TryGetValue(size, out wall) ? " dày " + wall : "";
            return "Ống đồng D" + size + thickness + ", kèm bảo ôn 19mm";
        }

        private static bool TryGetPumpTitle(TakeoffBlockInput block, out string title)
        {
            title = null;
            if (!string.Equals(block.Name.Trim(), PumpLabelBlock, StringComparison.OrdinalIgnoreCase))
                return false;

            string value = FindAttribute(block, "TITLE").Trim();
            if (value.StartsWith("CHWP", StringComparison.OrdinalIgnoreCase))
                title = "Bơm tuần hoàn nước lạnh:";
            else if (value.StartsWith("CWP", StringComparison.OrdinalIgnoreCase))
                title = "Bơm tuần hoàn nước giải nhiệt:";
            return title != null;
        }

        private static string DescribePumpLines(TakeoffBlockInput block)
        {
            string properties = FindAttribute(block, "PROPERTIES");
            StringBuilder lines = new StringBuilder();
            Match flow = PumpFlow.Match(properties);
            if (flow.Success)
                lines.Append("\n- Lưu lượng nước: ").Append(FormatPumpNumber(flow)).Append(" (LPM)");
            Match head = PumpHead.Match(properties);
            if (head.Success)
                lines.Append("\n- Cột áp: ").Append(FormatPumpNumber(head)).Append(" (mH2O)");
            return lines.ToString();
        }

        private static string BuildCadParameters(TakeoffBlockInput block)
        {
            var values = new List<string>();
            foreach (KeyValuePair<string, string> attribute in block.Attributes)
                values.Add(attribute.Key + "=" + attribute.Value);
            foreach (KeyValuePair<string, string> property in block.DynamicProperties)
            {
                if (!string.Equals(property.Key, "Origin", StringComparison.OrdinalIgnoreCase))
                    values.Add("Dynamic:" + property.Key + "=" + property.Value);
            }
            return string.Join("; ", values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        }

        private static string FindAttribute(TakeoffBlockInput block, string tag)
        {
            foreach (KeyValuePair<string, string> attribute in block.Attributes)
            {
                if (string.Equals(attribute.Key, tag, StringComparison.OrdinalIgnoreCase))
                    return attribute.Value ?? "";
            }
            return "";
        }

        private static string StripParenthesized(string text)
        {
            string current = text;
            string previous;
            do
            {
                previous = current;
                current = ParenthesizedPart.Replace(previous, "");
            }
            while (current != previous);
            return current;
        }

        private static bool ContainsAny(string text, string[] markers)
        {
            return markers.Any(marker => text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string FormatMatchedNumber(Match match)
        {
            return FormatNumber(ParseNumber(match.Groups[1].Value));
        }

        private static string FormatPumpNumber(Match match)
        {
            return FormatNumber(ParseNumber(PumpThousandsSeparator.Replace(match.Groups[1].Value, "")));
        }

        private static double ParseNumber(string value)
        {
            return double.Parse(value.Replace(',', '.'), CultureInfo.InvariantCulture);
        }

        private static bool IsAsciiDigit(char value)
        {
            return value >= '0' && value <= '9';
        }
    }

    public sealed class EquipmentRow
    {
        internal EquipmentRow(EquipmentDescription description)
        {
            SortRank = description.SortRank;
            Description = description.Text;
            Unit = description.Unit;
        }

        public int SortRank { get; }
        public string Description { get; }
        public string Unit { get; }
        public int Count { get; internal set; }
    }

    public sealed class DuctBandRow
    {
        internal DuctBandRow(double thickness, double areaSquareMeters)
        {
            Thickness = thickness;
            AreaSquareMeters = areaSquareMeters;
        }

        public double Thickness { get; }
        public double AreaSquareMeters { get; }
        public string Description
        {
            get { return "Ống gió tôn mạ kẽm " + TakeoffClassifier.FormatNumber(Thickness) + "mm"; }
        }
    }

    public sealed class DuctSizeRow
    {
        private readonly List<QuantityTerm> terms = new List<QuantityTerm>();

        internal DuctSizeRow(DuctSize size, int bandIndex)
        {
            Size = size;
            SizeText = size.Text;
            DecidingSize = size.DecidingSize;
            BandIndex = bandIndex;
            Terms = terms.AsReadOnly();
        }

        public DuctSize Size { get; }
        public string SizeText { get; }
        public double DecidingSize { get; }
        public int BandIndex { get; }
        public int Count { get; internal set; }
        public double LengthMeters { get; internal set; }
        public double AreaSquareMeters { get; internal set; }
        public IReadOnlyList<QuantityTerm> Terms { get; }

        internal void AddTerm(QuantityTerm term)
        {
            terms.Add(term);
        }
    }

    public sealed class PipeRow
    {
        private readonly List<QuantityTerm> terms = new List<QuantityTerm>();

        internal PipeRow(PipeMaterial material, string description, double? sizeMm)
        {
            Material = material;
            Description = description;
            SizeMm = sizeMm;
            Terms = terms.AsReadOnly();
        }

        public PipeMaterial Material { get; }
        public string Description { get; }
        public double? SizeMm { get; }
        public double LengthMeters { get; internal set; }
        public IReadOnlyList<QuantityTerm> Terms { get; }

        internal void AddTerm(QuantityTerm term)
        {
            terms.Add(term);
        }
    }

    public sealed class TakeoffResult
    {
        internal TakeoffResult()
        {
        }

        public IReadOnlyList<EquipmentRow> EquipmentRows { get; internal set; }
        public IReadOnlyList<DuctBandRow> DuctBandRows { get; internal set; }
        public IReadOnlyList<DuctSizeRow> DuctSizeRows { get; internal set; }
        public IReadOnlyList<PipeRow> CopperRows { get; internal set; }
        public IReadOnlyList<PipeRow> UpvcRows { get; internal set; }
        public IReadOnlyList<PipeRow> SteelRows { get; internal set; }
        public IReadOnlyList<PipeRow> OtherRows { get; internal set; }
        public int TaggedCurveCount { get; internal set; }
        public int NotExportedCurveCount { get; internal set; }
        public IReadOnlyList<string> NotExportedLayers { get; internal set; }
        public int ExcludedPumpBlockCount { get; internal set; }
        public int DuctsBelowFirstBandCount { get; internal set; }
        public double ContingencyFactor { get; internal set; }
    }

    public static class TakeoffAggregator
    {
        public static TakeoffResult Aggregate(IEnumerable<TakeoffBlockInput> blocks,
            IEnumerable<TakeoffCurveInput> curves, DuctGaugeSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            string error;
            if (!settings.TryValidate(out error))
                throw new ArgumentException(error, nameof(settings));
            return Aggregate(blocks, curves, new TakeoffOptions(settings, TakeoffOptions.DefaultContingencyFactor, null));
        }

        public static TakeoffResult Aggregate(IEnumerable<TakeoffBlockInput> blocks,
            IEnumerable<TakeoffCurveInput> curves, TakeoffOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            string error;
            if (!options.TryValidate(out error))
                throw new ArgumentException(error, nameof(options));
            DuctGaugeSettings settings = options.Gauge;

            List<TakeoffBlockInput> blockList = (blocks ?? Enumerable.Empty<TakeoffBlockInput>()).ToList();
            bool hasPumpLabel = blockList.Any(TakeoffClassifier.IsPumpLabel);

            var equipment = new Dictionary<string, EquipmentRow>(StringComparer.Ordinal);
            var ducts = new Dictionary<string, DuctSizeRow>(StringComparer.Ordinal);
            double[] bandAreas = new double[settings.Bands.Count];
            int excludedPumps = 0;
            int belowFirstBand = 0;

            foreach (TakeoffBlockInput block in blockList)
            {
                DuctSize size;
                if (TakeoffClassifier.TryReadDuct(block, out size))
                {
                    belowFirstBand += AddDuct(ducts, bandAreas, settings, size,
                        new QuantityTerm(block.Distance3.Value / 1000.0, 1, false));
                    continue;
                }

                if (hasPumpLabel && TakeoffClassifier.IsPumpSymbol(block))
                {
                    excludedPumps++;
                    continue;
                }

                EquipmentDescription description = TakeoffClassifier.DescribeEquipment(block);
                EquipmentRow row;
                if (!equipment.TryGetValue(description.Text, out row))
                {
                    row = new EquipmentRow(description);
                    equipment.Add(description.Text, row);
                }
                row.Count++;
            }

            var pipes = new Dictionary<string, PipeRow>(StringComparer.OrdinalIgnoreCase);
            var notExportedLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int notExported = 0;
            int tagged = 0;
            foreach (TakeoffCurveInput curve in curves ?? Enumerable.Empty<TakeoffCurveInput>())
            {
                PipeLayerClassification classification = TakeoffClassifier.ClassifyPipeLayer(curve.Layer);
                // A size tag counts unless the layer itself names the size (a catalogue layer wins).
                if (curve.Tag != null && classification.SizesMm.Count == 0)
                {
                    QuantityTerm tagTerm = new QuantityTerm(curve.LengthMm / 1000.0, 1, false);
                    DuctSize tagDuct;
                    if (curve.Tag.Kind == SizeTagKind.Duct && TakeoffClassifier.TryReadDuctTag(curve.Tag, out tagDuct))
                    {
                        belowFirstBand += AddDuct(ducts, bandAreas, settings, tagDuct, tagTerm);
                        tagged++;
                        continue;
                    }

                    if (curve.Tag.Kind == SizeTagKind.Pipe)
                    {
                        foreach (string description in TakeoffClassifier.DescribeTaggedPipe(curve.Tag, curve.Layer))
                            AddPipeRow(pipes, PipeMaterial.Other, description, curve.Tag.SizeMm, tagTerm);
                        tagged++;
                        continue;
                    }
                }

                if (classification.Material == PipeMaterial.None)
                {
                    notExported++;
                    notExportedLayers.Add(curve.Layer);
                    continue;
                }

                QuantityTerm term = new QuantityTerm(curve.LengthMm / 1000.0, 1, false);
                if (classification.SizesMm.Count == 0)
                    AddPipeLength(pipes, classification.Material, null, curve.Layer, term);
                foreach (double sizeMm in classification.SizesMm)
                    AddPipeLength(pipes, classification.Material, sizeMm, curve.Layer, term);
            }

            foreach (ShaftPipeInput shaft in options.ShaftPipes)
            {
                QuantityTerm term = new QuantityTerm(shaft.HeightMeters, shaft.Count, true);
                if (shaft.Kind == ShaftPipeKind.Duct)
                {
                    DuctSize size;
                    TakeoffClassifier.TryParseDuctSize(shaft.Size, out size);
                    belowFirstBand += AddDuct(ducts, bandAreas, settings, size, term);
                    continue;
                }

                double sizeMm;
                TakeoffClassifier.TryParseNominalSize(shaft.Size, out sizeMm);
                if (shaft.Kind == ShaftPipeKind.Steel || shaft.Kind == ShaftPipeKind.Upvc)
                {
                    PipeMaterial material = shaft.Kind == ShaftPipeKind.Steel ? PipeMaterial.Steel : PipeMaterial.Upvc;
                    AddPipeLength(pipes, material, sizeMm, null, term);
                    continue;
                }

                AddPipeRow(pipes, PipeMaterial.Other, TakeoffClassifier.DescribePlumbing(
                    TakeoffClassifier.ShaftMaterial(shaft.Kind), sizeMm, null), sizeMm, term);
            }

            return new TakeoffResult
            {
                EquipmentRows = equipment.Values
                    .OrderBy(row => row.SortRank)
                    .ThenBy(row => row.Description, TakeoffClassifier.NaturalComparer)
                    .ToList().AsReadOnly(),
                DuctBandRows = settings.Bands
                    .Select((band, index) => new DuctBandRow(band.Thickness, bandAreas[index]))
                    .ToList().AsReadOnly(),
                DuctSizeRows = ducts.Values
                    .OrderBy(row => row.DecidingSize)
                    .ThenBy(row => row.SizeText, TakeoffClassifier.NaturalComparer)
                    .ToList().AsReadOnly(),
                CopperRows = SortPipes(pipes.Values, PipeMaterial.Copper),
                UpvcRows = SortPipes(pipes.Values, PipeMaterial.Upvc),
                SteelRows = SortPipes(pipes.Values, PipeMaterial.Steel),
                OtherRows = pipes.Values
                    .Where(row => row.Material == PipeMaterial.Other)
                    .OrderBy(row => row.Description, TakeoffClassifier.NaturalComparer)
                    .ToList().AsReadOnly(),
                TaggedCurveCount = tagged,
                NotExportedCurveCount = notExported,
                NotExportedLayers = notExportedLayers
                    .OrderBy(layer => layer, TakeoffClassifier.NaturalComparer)
                    .ToList().AsReadOnly(),
                ExcludedPumpBlockCount = excludedPumps,
                DuctsBelowFirstBandCount = belowFirstBand,
                ContingencyFactor = options.ContingencyFactor
            };
        }

        /// <returns>The number of ducts added below the first band, for the warning count.</returns>
        private static int AddDuct(Dictionary<string, DuctSizeRow> ducts, double[] bandAreas,
            DuctGaugeSettings settings, DuctSize size, QuantityTerm term)
        {
            double meters = term.Meters * term.Multiplier;
            double area = size.PerimeterMm / 1000.0 * meters;
            DuctSizeRow duct;
            if (!ducts.TryGetValue(size.Text, out duct))
            {
                duct = new DuctSizeRow(size, settings.FindBandIndex(size.DecidingSize));
                ducts.Add(size.Text, duct);
            }
            duct.Count += term.Multiplier;
            duct.LengthMeters += meters;
            duct.AreaSquareMeters += area;
            duct.AddTerm(term);
            bandAreas[duct.BandIndex] += area;
            return settings.IsBelowFirstBand(size.DecidingSize) ? term.Multiplier : 0;
        }

        private static void AddPipeLength(Dictionary<string, PipeRow> pipes, PipeMaterial material,
            double? sizeMm, string layer, QuantityTerm term)
        {
            AddPipeRow(pipes, material, TakeoffClassifier.DescribePipe(material, sizeMm, layer), sizeMm, term);
        }

        private static void AddPipeRow(Dictionary<string, PipeRow> pipes, PipeMaterial material,
            string description, double? sizeMm, QuantityTerm term)
        {
            PipeRow row;
            if (!pipes.TryGetValue(description, out row))
            {
                row = new PipeRow(material, description, sizeMm);
                pipes.Add(description, row);
            }
            row.LengthMeters += term.Meters * term.Multiplier;
            row.AddTerm(term);
        }

        private static IReadOnlyList<PipeRow> SortPipes(IEnumerable<PipeRow> rows, PipeMaterial material)
        {
            return rows
                .Where(row => row.Material == material)
                .OrderBy(row => row.SizeMm.HasValue ? 0 : 1)
                .ThenBy(row => row.SizeMm ?? 0)
                .ThenBy(row => row.Description, TakeoffClassifier.NaturalComparer)
                .ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Writes the takeoff as an Excel-compatible SpreadsheetML 2003 workbook.
    /// </summary>
    public static class TakeoffWorkbookWriter
    {
        private const string Spreadsheet = "urn:schemas-microsoft-com:office:spreadsheet";
        private const string MainSheetName = "DIEU HOA THONG GIO";
        private const string AppendixSheetName = "PHU LUC ONG GIO";
        private const string DetailSheetName = "PHU LUC DOAN ONG";

        // Excel rejects formulas longer than 8192 characters; leave room for the wrapper.
        private const int MaxFormulaLength = 8000;
        // The contingency factor cell, F6 on the main sheet, in R1C1 notation.
        private const string FactorReference = "R6C6";
        private const int FirstAppendixDataRow = 2;
        private const string FactorFill = "#FFF2CC";
        private const string FactorFormat = "0.00";

        private const string FontName = "Times New Roman";
        private const string HeaderFill = "#DAF2D0";
        private const string UsdHeaderFill = "#E2ECF8";
        private const string SectionFill = "#F2DCDB";
        private const string GroupFill = "#DCE6F1";
        private const string ClosingFill = "#E6B8B7";
        private const string QuantityFormat = "_(* #,##0.00_);_(* \\(#,##0.00\\);_(* \"-\"??_);_(@_)";
        private const string MoneyFormat = "_(* #,##0_);_(* \\(#,##0\\);_(* \"-\"??_);_(@_)";
        private const string ClosingMoneyFormat = "_-* #,##0_-;\\-* #,##0_-;_-* \"-\"_-;_-@_-";
        private const string CountFormat = "0";

        private const double GroupRowHeight = 29.25;
        private const double LineHeight = 15.75;
        private const int CharactersPerLine = 40;
        private const double TemplateWidthScale = 5.25;

        private const int FirstMoneyColumn = 7;
        private const int LastMoneyColumn = 14;

        private static readonly double[] MainColumnWidths =
        {
            8.7, 41.3, 38.0, 29.7, 12.1, 15.1, 18.3, 17.0, 20.3, 20.3, 20.3, 18.0, 16.7, 19.7, 17.9
        };
        private static readonly double[] AppendixColumnWidths = { 120, 80, 130, 120 };
        private static readonly double[] DetailColumnWidths = { 320, 130 };

        private sealed class DetailBlock
        {
            public string Title;
            public IReadOnlyList<QuantityTerm> Terms;
            public int FirstRow;
        }

        /// <summary>
        /// The sum expression of every duct size and pipe row, inline or as a SUM over the detail sheet.
        /// </summary>
        private sealed class FormulaLayout
        {
            public readonly Dictionary<object, string> Sums = new Dictionary<object, string>();
            public readonly List<DetailBlock> DetailBlocks = new List<DetailBlock>();
        }

        private enum BorderLine
        {
            None,
            Hair,
            Thin,
            Double
        }

        private sealed class CellStyle
        {
            public string Id;
            public bool Bold;
            public int FontSize = 12;
            public string Fill;
            public string Horizontal;
            public bool Wrap;
            public string Format;
            public BorderLine Left;
            public BorderLine Right;
            public BorderLine Top;
            public BorderLine Bottom;
        }

        public static void Write(string path, TakeoffResult result)
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                Write(buffer, result);
                File.WriteAllBytes(path, buffer.ToArray());
            }
        }

        public static void Write(Stream stream, TakeoffResult result)
        {
            using (XmlWriter writer = XmlWriter.Create(stream, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(true),
                Indent = true,
                NewLineChars = "\n"
            }))
            {
                writer.WriteStartDocument();
                writer.WriteProcessingInstruction("mso-application", "progid=\"Excel.Sheet\"");
                writer.WriteStartElement("Workbook", Spreadsheet);
                writer.WriteAttributeString("xmlns", "ss", null, Spreadsheet);
                FormulaLayout layout = BuildLayout(result);
                WriteStyles(writer);
                WriteMainSheet(writer, result, layout);
                WriteAppendixSheet(writer, result, layout);
                if (layout.DetailBlocks.Count > 0)
                    WriteDetailSheet(writer, layout);
                writer.WriteEndElement();
                writer.WriteEndDocument();
            }
        }

        private static FormulaLayout BuildLayout(TakeoffResult result)
        {
            FormulaLayout layout = new FormulaLayout();
            int nextDetailRow = 2;
            foreach (DuctSizeRow row in result.DuctSizeRows)
                nextDetailRow = AddSum(layout, row, "Ống gió " + row.SizeText, row.Terms, nextDetailRow);
            foreach (PipeRow row in result.CopperRows.Concat(result.UpvcRows).Concat(result.SteelRows).Concat(result.OtherRows))
                nextDetailRow = AddSum(layout, row, row.Description, row.Terms, nextDetailRow);
            return layout;
        }

        private static int AddSum(FormulaLayout layout, object owner, string title,
            IReadOnlyList<QuantityTerm> terms, int nextDetailRow)
        {
            string inline = InlineSum(terms);
            if (inline.Length <= MaxFormulaLength)
            {
                layout.Sums.Add(owner, inline);
                return nextDetailRow;
            }

            int firstTermRow = nextDetailRow + 1;
            int lastTermRow = firstTermRow + terms.Count - 1;
            layout.DetailBlocks.Add(new DetailBlock { Title = title, Terms = terms, FirstRow = nextDetailRow });
            layout.Sums.Add(owner, "SUM(" + SheetPrefix(DetailSheetName) +
                "R" + firstTermRow + "C2:R" + lastTermRow + "C2)");
            return lastTermRow + 2;
        }

        private static string InlineSum(IReadOnlyList<QuantityTerm> terms)
        {
            List<string> parts = terms.Where(term => RoundMeters(term.Meters) > 0).Select(FormatTerm).ToList();
            return parts.Count == 0 ? "0" : string.Join("+", parts);
        }

        private static string FormatTerm(QuantityTerm term)
        {
            string meters = RoundMeters(term.Meters).ToString("0.###", CultureInfo.InvariantCulture);
            return term.FromShaft || term.Multiplier != 1
                ? meters + "*" + term.Multiplier.ToString(CultureInfo.InvariantCulture)
                : meters;
        }

        private static double RoundMeters(double meters)
        {
            return Math.Round(meters, 3, MidpointRounding.AwayFromZero);
        }

        private static double TermValue(QuantityTerm term)
        {
            return RoundMeters(term.Meters) * term.Multiplier;
        }

        private static double SumValue(IEnumerable<QuantityTerm> terms)
        {
            return terms.Sum(term => TermValue(term));
        }

        private static string SheetPrefix(string sheetName)
        {
            return "'" + sheetName + "'!";
        }

        private static string FormatDimension(double millimetres)
        {
            return (millimetres / 1000.0).ToString("0.######", CultureInfo.InvariantCulture);
        }

        private static string AreaFactorFormula(DuctSize size)
        {
            return size.IsRound
                ? "PI()*" + FormatDimension(size.WidthMm)
                : "2*(" + FormatDimension(size.WidthMm) + "+" + FormatDimension(size.HeightMm) + ")";
        }

        private static double AreaFactorValue(DuctSize size)
        {
            return size.IsRound
                ? Math.PI * size.WidthMm / 1000.0
                : 2 * (size.WidthMm + size.HeightMm) / 1000.0;
        }

        private static double AppendixAreaValue(DuctSizeRow row)
        {
            return AreaFactorValue(row.Size) * SumValue(row.Terms);
        }

        private static void WriteMainSheet(XmlWriter writer, TakeoffResult result, FormulaLayout layout)
        {
            StartWorksheet(writer, MainSheetName, MainColumnWidths.Select(width => width * TemplateWidthScale));

            StartRow(writer, 43.5);
            WriteCell(writer, "title", "BẢNG BÁO GIÁ CHI TIẾT\n报价明细表", mergeAcross: MainColumnWidths.Length - 1);
            writer.WriteEndElement();
            WriteLabelRow(writer, 25.5, "Dự án:");
            WriteLabelRow(writer, 25.5, "项目:");
            WriteLabelRow(writer, 24.95, "Hạng mục:");
            WriteLabelRow(writer, 24.95, "项目:");
            StartRow(writer, 21);
            WriteCell(writer, "factorLabel", "Hệ số dự phòng:", index: 4, mergeAcross: 1);
            WriteNumberCell(writer, "factorValue", result.ContingencyFactor);
            writer.WriteEndElement();
            WriteHeaderRows(writer);

            WriteTitledRow(writer, "section", 37.5, "I", "HỆ THỐNG ĐIỀU HÒA THÔNG GIÓ");

            int number = 0;
            WriteTitledRow(writer, "group", GroupRowHeight, "I.1", "Thiết bị");
            foreach (EquipmentRow row in result.EquipmentRows)
                WriteDataRow(writer, ++number, row.Description, row.Unit, row.Count);

            WriteTitledRow(writer, "group", GroupRowHeight, "I.2", "Ống gió");
            for (int band = 0; band < result.DuctBandRows.Count; band++)
                WriteDuctBandRow(writer, ++number, result, band);

            WriteTitledRow(writer, "group", GroupRowHeight, "I.3", "Ống nước");
            number = WritePipeSubgroup(writer, number, "Ống đồng", result.CopperRows, result, layout);
            number = WritePipeSubgroup(writer, number, "Ống thoát nước ngưng uPVC", result.UpvcRows, result, layout);
            number = WritePipeSubgroup(writer, number, "Ống thép hàn mạ kẽm", result.SteelRows, result, layout);
            WritePipeSubgroup(writer, number, "Ống cấp thoát nước và ống khác (theo tiền tố hệ)", result.OtherRows, result, layout);

            WriteTitledRow(writer, "closing", 34.5, null, "TỔNG CỘNG (CHƯA VAT)");
            EndWorksheet(writer);
        }

        private static void WriteHeaderRows(XmlWriter writer)
        {
            StartRow(writer, 33);
            WriteCell(writer, "headerFirst", "STT\n序号", mergeDown: 1);
            WriteCell(writer, "header", "NỘI DUNG CÔNG VIỆC", mergeDown: 1);
            WriteCell(writer, "header", "工作内容", mergeDown: 1);
            WriteCell(writer, "header", "MÃ HIỆU \nSẢN PHẨM\n产品型号", mergeDown: 1);
            WriteCell(writer, "header", "ĐƠN VỊ\n单位", mergeDown: 1);
            WriteCell(writer, "header", "KHỐI LƯỢNG\n数量", mergeDown: 1);
            WriteCell(writer, "header", "ĐƠN GIÁ\n单价 (VND)", index: 7, mergeAcross: 1);
            WriteCell(writer, "header", "TỔNG PHỤ\n小计 (VND)", index: 9, mergeAcross: 1);
            WriteCell(writer, "header", "TỔNG CỘNG\n合计 (VND)", index: 11, mergeAcross: 1);
            WriteCell(writer, "headerUsd", "ĐƠN GIÁ\n单价 (USD)", index: 13, mergeDown: 1);
            WriteCell(writer, "headerUsd", "THÀNH TIỀN\n金额 (USD)", mergeDown: 1);
            WriteCell(writer, "headerLast", "GHI CHÚ\n备注", mergeDown: 1);
            writer.WriteEndElement();

            StartRow(writer, 30);
            WriteCell(writer, "headerSub", "VẬT LIỆU\n材料", index: 7);
            WriteCell(writer, "headerSub", "NHÂN CÔNG\n人工");
            WriteCell(writer, "headerSub", "VẬT LIỆU\n材料");
            WriteCell(writer, "headerSub", "NHÂN CÔNG\n人工");
            WriteCell(writer, "headerSub", "ĐƠN GIÁ\n单价");
            WriteCell(writer, "headerSub", "THÀNH TIỀN\n金额");
            writer.WriteEndElement();
        }

        /// <summary>
        /// A band row sums the appendix area cells of the sizes in that band, times the factor.
        /// </summary>
        private static void WriteDuctBandRow(XmlWriter writer, int number, TakeoffResult result, int band)
        {
            DuctBandRow row = result.DuctBandRows[band];
            var references = new List<string>();
            double area = 0;
            for (int index = 0; index < result.DuctSizeRows.Count; index++)
            {
                DuctSizeRow size = result.DuctSizeRows[index];
                if (size.BandIndex != band)
                    continue;
                references.Add(SheetPrefix(AppendixSheetName) + "R" + (FirstAppendixDataRow + index) + "C4");
                area += AppendixAreaValue(size);
            }

            if (references.Count == 0)
            {
                WriteDataRow(writer, number, row.Description, "m2", 0);
                return;
            }
            string formula = "=(" + string.Join("+", references) + ")*" + FactorReference;
            WriteDataRow(writer, number, row.Description, "m2", area * result.ContingencyFactor, formula);
        }

        private static int WritePipeSubgroup(XmlWriter writer, int number, string title, IReadOnlyList<PipeRow> rows,
            TakeoffResult result, FormulaLayout layout)
        {
            if (rows.Count == 0)
                return number;

            WriteTitledRow(writer, "subgroup", GroupRowHeight, "*", title);
            foreach (PipeRow row in rows)
            {
                string formula = "=(" + layout.Sums[row] + ")*" + FactorReference;
                WriteDataRow(writer, ++number, row.Description, "m",
                    SumValue(row.Terms) * result.ContingencyFactor, formula);
            }
            return number;
        }

        private static void WriteLabelRow(XmlWriter writer, double height, string label)
        {
            StartRow(writer, height);
            WriteCell(writer, "label", label);
            writer.WriteEndElement();
        }

        private static void WriteTitledRow(XmlWriter writer, string family, double height, string index, string title)
        {
            StartRow(writer, height);
            WriteCell(writer, family + "Index", index);
            WriteCell(writer, family + "Text", title);
            WriteCell(writer, family + "Text", null);
            WriteCell(writer, family + "Middle", null);
            WriteCell(writer, family + "Middle", null);
            WriteCell(writer, family + "Quantity", null);
            WriteEmptyTail(writer, family);
            writer.WriteEndElement();
        }

        private static void WriteDataRow(XmlWriter writer, int number, string description, string unit, double quantity,
            string formula = null)
        {
            StartRow(writer, EstimateRowHeight(description));
            WriteNumberCell(writer, "dataIndex", number);
            WriteCell(writer, "dataText", description);
            WriteCell(writer, "dataText", null);
            WriteCell(writer, "dataMiddle", null);
            WriteCell(writer, "dataMiddle", unit);
            WriteNumberCell(writer, "dataQuantity", quantity, formula);
            WriteEmptyTail(writer, "data");
            writer.WriteEndElement();
        }

        private static void WriteEmptyTail(XmlWriter writer, string family)
        {
            for (int column = FirstMoneyColumn; column <= LastMoneyColumn; column++)
                WriteCell(writer, family + "Money", null);
            WriteCell(writer, family + "End", null);
        }

        private static double EstimateRowHeight(string text)
        {
            int lines = 0;
            foreach (string line in text.Split('\n'))
                lines += Math.Max(1, (line.Length + CharactersPerLine - 1) / CharactersPerLine);
            return Math.Max(GroupRowHeight, lines * LineHeight);
        }

        private static void WriteAppendixSheet(XmlWriter writer, TakeoffResult result, FormulaLayout layout)
        {
            StartWorksheet(writer, AppendixSheetName, AppendixColumnWidths);

            StartRow(writer, GroupRowHeight);
            WriteCell(writer, "appendixHeader", "Kích thước");
            WriteCell(writer, "appendixHeader", "Số lượng");
            WriteCell(writer, "appendixHeader", "Tổng chiều dài (m)");
            WriteCell(writer, "appendixHeader", "Diện tích (m2)");
            writer.WriteEndElement();

            foreach (DuctSizeRow row in result.DuctSizeRows)
            {
                StartRow(writer, GroupRowHeight);
                WriteCell(writer, "appendixText", row.SizeText);
                WriteNumberCell(writer, "appendixCount", row.Count);
                WriteNumberCell(writer, "appendixQuantity", SumValue(row.Terms), "=" + layout.Sums[row]);
                WriteNumberCell(writer, "appendixQuantity", AppendixAreaValue(row),
                    "=" + AreaFactorFormula(row.Size) + "*RC[-1]");
                writer.WriteEndElement();
            }
            EndWorksheet(writer);
        }

        /// <summary>
        /// Lists every segment of the rows whose inline formula would be too long for Excel.
        /// </summary>
        private static void WriteDetailSheet(XmlWriter writer, FormulaLayout layout)
        {
            StartWorksheet(writer, DetailSheetName, DetailColumnWidths);

            StartRow(writer, GroupRowHeight);
            WriteCell(writer, "appendixHeader", "Nội dung");
            WriteCell(writer, "appendixHeader", "Chiều dài (m)");
            writer.WriteEndElement();

            int rowNumber = 2;
            foreach (DetailBlock block in layout.DetailBlocks)
            {
                for (; rowNumber < block.FirstRow; rowNumber++)
                {
                    StartRow(writer, GroupRowHeight);
                    writer.WriteEndElement();
                }

                StartRow(writer, GroupRowHeight);
                WriteCell(writer, "detailTitle", block.Title, mergeAcross: 1);
                writer.WriteEndElement();
                rowNumber++;

                int segment = 0;
                foreach (QuantityTerm term in block.Terms)
                {
                    StartRow(writer, GroupRowHeight);
                    if (term.FromShaft)
                    {
                        WriteCell(writer, "appendixText", "Trục kỹ thuật");
                        WriteNumberCell(writer, "appendixQuantity", TermValue(term), "=" + FormatTerm(term));
                    }
                    else
                    {
                        WriteCell(writer, "appendixText", "Đoạn " + (++segment).ToString(CultureInfo.InvariantCulture));
                        WriteNumberCell(writer, "appendixQuantity", TermValue(term));
                    }
                    writer.WriteEndElement();
                    rowNumber++;
                }
            }
            EndWorksheet(writer);
        }

        private static void StartWorksheet(XmlWriter writer, string name, IEnumerable<double> columnWidths)
        {
            writer.WriteStartElement("Worksheet", Spreadsheet);
            writer.WriteAttributeString("ss", "Name", Spreadsheet, name);
            writer.WriteStartElement("Table", Spreadsheet);
            foreach (double width in columnWidths)
            {
                writer.WriteStartElement("Column", Spreadsheet);
                writer.WriteAttributeString("ss", "AutoFitWidth", Spreadsheet, "0");
                writer.WriteAttributeString("ss", "Width", Spreadsheet,
                    width.ToString("0.###", CultureInfo.InvariantCulture));
                writer.WriteEndElement();
            }
        }

        private static void EndWorksheet(XmlWriter writer)
        {
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        private static void StartRow(XmlWriter writer, double height)
        {
            writer.WriteStartElement("Row", Spreadsheet);
            writer.WriteAttributeString("ss", "AutoFitHeight", Spreadsheet, "0");
            writer.WriteAttributeString("ss", "Height", Spreadsheet,
                height.ToString("0.##", CultureInfo.InvariantCulture));
        }

        private static void WriteCell(XmlWriter writer, string styleId, string text,
            int index = 0, int mergeAcross = 0, int mergeDown = 0)
        {
            StartCell(writer, styleId, index, mergeAcross, mergeDown);
            if (text != null)
            {
                writer.WriteStartElement("Data", Spreadsheet);
                writer.WriteAttributeString("ss", "Type", Spreadsheet, "String");
                WriteTextPreservingLineBreaks(writer, text);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        /// <param name="formula">Optional R1C1 formula; <paramref name="value"/> is then its cached result.</param>
        private static void WriteNumberCell(XmlWriter writer, string styleId, double value, string formula = null)
        {
            StartCell(writer, styleId, 0, 0, 0);
            if (formula != null)
                writer.WriteAttributeString("ss", "Formula", Spreadsheet, formula);
            writer.WriteStartElement("Data", Spreadsheet);
            writer.WriteAttributeString("ss", "Type", Spreadsheet, "Number");
            writer.WriteString(value.ToString("R", CultureInfo.InvariantCulture));
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        private static void StartCell(XmlWriter writer, string styleId, int index, int mergeAcross, int mergeDown)
        {
            writer.WriteStartElement("Cell", Spreadsheet);
            if (index > 0)
                writer.WriteAttributeString("ss", "Index", Spreadsheet, index.ToString(CultureInfo.InvariantCulture));
            if (mergeAcross > 0)
                writer.WriteAttributeString("ss", "MergeAcross", Spreadsheet, mergeAcross.ToString(CultureInfo.InvariantCulture));
            if (mergeDown > 0)
                writer.WriteAttributeString("ss", "MergeDown", Spreadsheet, mergeDown.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("ss", "StyleID", Spreadsheet, styleId);
        }

        private static void WriteTextPreservingLineBreaks(XmlWriter writer, string text)
        {
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                if (index > 0)
                    writer.WriteCharEntity('\n');
                writer.WriteString(lines[index]);
            }
        }

        private static void WriteStyles(XmlWriter writer)
        {
            writer.WriteStartElement("Styles", Spreadsheet);

            writer.WriteStartElement("Style", Spreadsheet);
            writer.WriteAttributeString("ss", "ID", Spreadsheet, "Default");
            writer.WriteAttributeString("ss", "Name", Spreadsheet, "Normal");
            WriteAlignment(writer, new CellStyle());
            WriteFont(writer, new CellStyle());
            writer.WriteEndElement();

            foreach (CellStyle style in BuildStyles())
                WriteStyle(writer, style);
            writer.WriteEndElement();
        }

        private static IEnumerable<CellStyle> BuildStyles()
        {
            yield return new CellStyle { Id = "title", Bold = true, FontSize = 14, Horizontal = "Center", Wrap = true };
            yield return new CellStyle { Id = "label", Bold = true };
            yield return new CellStyle { Id = "factorLabel", Bold = true, Horizontal = "Right" };
            yield return new CellStyle
            {
                Id = "factorValue",
                Bold = true,
                Fill = FactorFill,
                Horizontal = "Center",
                Format = FactorFormat,
                Left = BorderLine.Thin,
                Right = BorderLine.Thin,
                Top = BorderLine.Thin,
                Bottom = BorderLine.Thin
            };

            yield return HeaderStyle("headerFirst", BorderLine.Double, BorderLine.Thin, BorderLine.Double);
            yield return HeaderStyle("header", BorderLine.Thin, BorderLine.Thin, BorderLine.Double);
            yield return HeaderStyle("headerUsd", BorderLine.Thin, BorderLine.Thin, BorderLine.Double, UsdHeaderFill);
            yield return HeaderStyle("headerLast", BorderLine.Thin, BorderLine.Double, BorderLine.Double);
            yield return HeaderStyle("headerSub", BorderLine.Thin, BorderLine.Thin, BorderLine.Thin);

            IEnumerable<CellStyle> rowStyles = RowStyles("section", true, true, true, SectionFill, BorderLine.Thin, BorderLine.Hair, true, MoneyFormat)
                .Concat(RowStyles("group", true, true, true, GroupFill, BorderLine.Hair, BorderLine.Hair, false, MoneyFormat))
                .Concat(RowStyles("subgroup", true, false, false, null, BorderLine.Hair, BorderLine.Hair, false, MoneyFormat))
                .Concat(RowStyles("data", false, false, false, null, BorderLine.Hair, BorderLine.Hair, true, MoneyFormat))
                .Concat(RowStyles("closing", true, false, true, ClosingFill, BorderLine.Thin, BorderLine.Double, true, ClosingMoneyFormat));
            foreach (CellStyle style in rowStyles)
                yield return style;

            yield return HeaderStyle("appendixHeader", BorderLine.Thin, BorderLine.Thin, BorderLine.Thin);
            yield return AppendixStyle("appendixText", null);
            yield return AppendixStyle("appendixCount", CountFormat);
            yield return AppendixStyle("appendixQuantity", QuantityFormat);

            CellStyle detailTitle = AppendixStyle("detailTitle", null);
            detailTitle.Bold = true;
            detailTitle.Horizontal = "Left";
            detailTitle.Fill = GroupFill;
            yield return detailTitle;
        }

        private static CellStyle HeaderStyle(string id, BorderLine left, BorderLine right, BorderLine top,
            string fill = HeaderFill)
        {
            return new CellStyle
            {
                Id = id,
                Bold = true,
                Fill = fill,
                Horizontal = "Center",
                Wrap = true,
                Left = left,
                Right = right,
                Top = top,
                Bottom = BorderLine.Thin
            };
        }

        private static CellStyle AppendixStyle(string id, string format)
        {
            return new CellStyle
            {
                Id = id,
                Horizontal = "Center",
                Format = format,
                Left = BorderLine.Thin,
                Right = BorderLine.Thin,
                Top = BorderLine.Thin,
                Bottom = BorderLine.Thin
            };
        }

        private static IEnumerable<CellStyle> RowStyles(string family, bool boldLead, bool boldMiddle, bool boldTail,
            string fill,
            BorderLine top, BorderLine bottom, bool wrapText, string moneyFormat)
        {
            yield return RowStyle(family + "Index", boldLead, fill, top, bottom, "Center", false, null,
                BorderLine.Double, BorderLine.Thin);
            yield return RowStyle(family + "Text", boldLead, fill, top, bottom, "Left", wrapText, null,
                BorderLine.Thin, BorderLine.Thin);
            yield return RowStyle(family + "Middle", boldMiddle, fill, top, bottom, "Center", true, null,
                BorderLine.Thin, BorderLine.Thin);
            yield return RowStyle(family + "Quantity", boldMiddle, fill, top, bottom, "Center", false, QuantityFormat,
                BorderLine.Thin, BorderLine.Thin);
            yield return RowStyle(family + "Money", boldTail, fill, top, bottom, "Center", false, moneyFormat,
                BorderLine.Thin, BorderLine.Thin);
            yield return RowStyle(family + "End", boldTail, fill, top, bottom, "Center", true, null,
                BorderLine.Thin, BorderLine.Double);
        }

        private static CellStyle RowStyle(string id, bool bold, string fill, BorderLine top, BorderLine bottom,
            string horizontal, bool wrap, string format, BorderLine left, BorderLine right)
        {
            return new CellStyle
            {
                Id = id,
                Bold = bold,
                Fill = fill,
                Horizontal = horizontal,
                Wrap = wrap,
                Format = format,
                Left = left,
                Right = right,
                Top = top,
                Bottom = bottom
            };
        }

        private static void WriteStyle(XmlWriter writer, CellStyle style)
        {
            writer.WriteStartElement("Style", Spreadsheet);
            writer.WriteAttributeString("ss", "ID", Spreadsheet, style.Id);
            WriteAlignment(writer, style);

            writer.WriteStartElement("Borders", Spreadsheet);
            WriteBorder(writer, "Bottom", style.Bottom);
            WriteBorder(writer, "Left", style.Left);
            WriteBorder(writer, "Right", style.Right);
            WriteBorder(writer, "Top", style.Top);
            writer.WriteEndElement();

            WriteFont(writer, style);
            if (style.Fill != null)
            {
                writer.WriteStartElement("Interior", Spreadsheet);
                writer.WriteAttributeString("ss", "Color", Spreadsheet, style.Fill);
                writer.WriteAttributeString("ss", "Pattern", Spreadsheet, "Solid");
                writer.WriteEndElement();
            }
            if (style.Format != null)
            {
                writer.WriteStartElement("NumberFormat", Spreadsheet);
                writer.WriteAttributeString("ss", "Format", Spreadsheet, style.Format);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        private static void WriteAlignment(XmlWriter writer, CellStyle style)
        {
            writer.WriteStartElement("Alignment", Spreadsheet);
            if (style.Horizontal != null)
                writer.WriteAttributeString("ss", "Horizontal", Spreadsheet, style.Horizontal);
            writer.WriteAttributeString("ss", "Vertical", Spreadsheet, "Center");
            if (style.Wrap)
                writer.WriteAttributeString("ss", "WrapText", Spreadsheet, "1");
            writer.WriteEndElement();
        }

        private static void WriteFont(XmlWriter writer, CellStyle style)
        {
            writer.WriteStartElement("Font", Spreadsheet);
            writer.WriteAttributeString("ss", "FontName", Spreadsheet, FontName);
            writer.WriteAttributeString("ss", "Size", Spreadsheet, style.FontSize.ToString(CultureInfo.InvariantCulture));
            if (style.Bold)
                writer.WriteAttributeString("ss", "Bold", Spreadsheet, "1");
            writer.WriteEndElement();
        }

        private static void WriteBorder(XmlWriter writer, string position, BorderLine line)
        {
            if (line == BorderLine.None)
                return;

            writer.WriteStartElement("Border", Spreadsheet);
            writer.WriteAttributeString("ss", "Position", Spreadsheet, position);
            writer.WriteAttributeString("ss", "LineStyle", Spreadsheet, line == BorderLine.Double ? "Double" : "Continuous");
            writer.WriteAttributeString("ss", "Weight", Spreadsheet,
                line == BorderLine.Double ? "3" : line == BorderLine.Thin ? "1" : "0");
            writer.WriteEndElement();
        }
    }
}
