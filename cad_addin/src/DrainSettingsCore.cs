using System;
using System.Globalization;
using System.Text;

namespace HvacAiCadAddin.Pipes
{
    // AutoCAD-free settings of HVACDRAIN: the cleaning tolerances, the 45 degree joint size, the sizing
    // basis (condensate factor and slope) and the size label. They are remembered between runs as plain
    // text (key=value per line). Deserialisation never throws: unknown keys and bad numbers keep the
    // defaults. Numbers are always written and read with the invariant culture.
    // See AGENTS.md, "HVACDRAIN: draw and size a condensate network".

    /// <summary>The fall of the drain pipes. It picks the allowable-flow column of the sizing table.</summary>
    public enum DrainSlope
    {
        OneIn100,
        OneIn50
    }

    public sealed class DrainSettings
    {
        /// <summary>Endpoints closer than this are one joint (mm).</summary>
        public double WeldTolerance = 50;

        public double AngleToleranceDegrees = 10;
        public double AlignTolerance = 200;
        public double JogTolerance = 200;

        /// <summary>True: the base angle is the UCS X axis. False: <see cref="BaseAngleDegrees"/>, from world X.</summary>
        public bool UseUcsBaseAngle = true;
        public double BaseAngleDegrees = 0;

        /// <summary>The length of the 45 degree piece at a chamfered bend and at a branch (mm).</summary>
        public double SlantLength = 300;

        /// <summary>True: size the pipes from the indoor-unit capacities. False: only straighten and draw the joints.</summary>
        public bool SizePipes = true;

        /// <summary>Condensate per kW of cooling capacity (L/h per kW). Daikin: 2 L/h per HP, 1 HP = 2.8 kW.</summary>
        public double CondensateFactor = 0.71;

        public DrainSlope Slope = DrainSlope.OneIn100;

        public bool WriteLabels = true;

        /// <summary>Text height of the size labels (mm). The label also sits this far from the pipe.</summary>
        public double LabelHeight = 150;

        /// <summary>The label text: <c>{size}</c> is the size number, <c>{slope}</c> the slope in percent.</summary>
        public string LabelFormat = "D{size}-{slope}%";

        /// <summary>
        /// The layer of the size labels: the template's annotation layer (the one HVACREFRIG puts its labels on). Empty puts
        /// each label on its own pipe's size layer.
        /// </summary>
        public string LabelLayer = "EIA_1 GAS ANNOTATIONS";

        /// <summary>How far from a pipe end an equipment label may be to give that unit its capacity (mm).</summary>
        public double CapacityRadius = 1500;

        /// <summary>How far from a 90 degree corner (or a T) a 45 degree piece starts: the slant length times cos 45.</summary>
        public double CutDistance
        {
            get { return SlantLength * Math.Cos(Math.PI / 4); }
        }

        /// <summary>The slope in percent: 1 for 1/100, 2 for 1/50.</summary>
        public int SlopePercent
        {
            get { return Slope == DrainSlope.OneIn50 ? 2 : 1; }
        }

        /// <summary>The slope as written in the dialog: 1/100 or 1/50.</summary>
        public string SlopeText
        {
            get { return Slope == DrainSlope.OneIn50 ? "1/50" : "1/100"; }
        }

        /// <summary>The size label of a pipe, for example <c>D34-1%</c>.</summary>
        public string LabelText(int size)
        {
            string format = string.IsNullOrEmpty(LabelFormat) ? "D{size}-{slope}%" : LabelFormat;
            return format
                .Replace("{size}", size.ToString(CultureInfo.InvariantCulture))
                .Replace("{slope}", SlopePercent.ToString(CultureInfo.InvariantCulture));
        }

        public bool IsValid(out string error)
        {
            error = null;
            if (!Finite(WeldTolerance) || WeldTolerance <= 0 || WeldTolerance > 1000)
                error = "The weld tolerance must be above 0 and at most 1000 mm.";
            else if (!Finite(AngleToleranceDegrees) || AngleToleranceDegrees <= 0 || AngleToleranceDegrees > 22.5)
                error = "The angle tolerance must be above 0 and at most 22.5 degrees.";
            else if (!Finite(AlignTolerance) || AlignTolerance < 0 || AlignTolerance > 100000)
                error = "The align tolerance must be 0 or more.";
            else if (!Finite(JogTolerance) || JogTolerance < 0 || JogTolerance > 100000)
                error = "The jog tolerance must be 0 or more.";
            else if (!Finite(BaseAngleDegrees))
                error = "The base angle must be a number.";
            else if (!Finite(SlantLength) || SlantLength <= 0 || SlantLength > 5000)
                error = "The slant length must be above 0 and at most 5000 mm.";
            else if (!Finite(CondensateFactor) || CondensateFactor <= 0 || CondensateFactor > 10)
                error = "The condensate factor must be above 0 and at most 10 L/h per kW.";
            else if (!Finite(LabelHeight) || LabelHeight <= 0 || LabelHeight > 10000)
                error = "The label height must be above 0 and at most 10000 mm.";
            else if (!Finite(CapacityRadius) || CapacityRadius < 0 || CapacityRadius > 100000)
                error = "The capacity search radius must be 0 or more.";
            else if (LabelFormat == null || LabelFormat.Length == 0 || LabelFormat.Length > 60
                || LabelFormat.IndexOf('\n') >= 0 || LabelFormat.IndexOf('\r') >= 0)
                error = "The label format must be a single line of 1 to 60 characters.";
            else if (LabelLayer == null || LabelLayer.Length > 255 || LabelLayer.IndexOfAny(BadLayerCharacters) >= 0)
                error = "The label layer is empty (the pipe's own layer) or a layer name without < > / \\ \" : ; ? * | = `.";
            return error == null;
        }

        /// <summary>The cleaner's settings: the drain tolerances, with the refnet rules left at their defaults.</summary>
        public RefrigSettings ToRefrigSettings()
        {
            RefrigSettings settings = new RefrigSettings();
            settings.WeldTolerance = WeldTolerance;
            settings.AngleToleranceDegrees = AngleToleranceDegrees;
            settings.AlignTolerance = AlignTolerance;
            settings.JogTolerance = JogTolerance;
            return settings;
        }

        private static readonly char[] BadLayerCharacters =
            { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', '=', '`', '\n', '\r' };

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        public string Serialize()
        {
            StringBuilder text = new StringBuilder();
            Add(text, "weld", Num(WeldTolerance));
            Add(text, "angle", Num(AngleToleranceDegrees));
            Add(text, "align", Num(AlignTolerance));
            Add(text, "jog", Num(JogTolerance));
            Add(text, "ucs", UseUcsBaseAngle ? "1" : "0");
            Add(text, "baseAngle", Num(BaseAngleDegrees));
            Add(text, "slant", Num(SlantLength));
            Add(text, "size", SizePipes ? "1" : "0");
            Add(text, "factor", Num(CondensateFactor));
            Add(text, "slope", Slope == DrainSlope.OneIn50 ? "50" : "100");
            Add(text, "labels", WriteLabels ? "1" : "0");
            Add(text, "labelHeight", Num(LabelHeight));
            Add(text, "labelFormat", LabelFormat ?? "");
            Add(text, "labelLayer", LabelLayer ?? "");
            Add(text, "radius", Num(CapacityRadius));
            return text.ToString();
        }

        public static DrainSettings Deserialize(string text)
        {
            DrainSettings settings = new DrainSettings();
            if (string.IsNullOrEmpty(text))
                return settings;

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim('\r', ' ');
                int equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;

                string key = line.Substring(0, equals);
                string value = line.Substring(equals + 1);
                double number;
                bool isNumber = TryNumber(value, out number);
                switch (key)
                {
                    case "weld": if (isNumber) settings.WeldTolerance = number; break;
                    case "angle": if (isNumber) settings.AngleToleranceDegrees = number; break;
                    case "align": if (isNumber) settings.AlignTolerance = number; break;
                    case "jog": if (isNumber) settings.JogTolerance = number; break;
                    case "ucs": settings.UseUcsBaseAngle = value != "0"; break;
                    case "baseAngle": if (isNumber) settings.BaseAngleDegrees = number; break;
                    case "slant": if (isNumber) settings.SlantLength = number; break;
                    case "size": settings.SizePipes = value != "0"; break;
                    case "factor": if (isNumber) settings.CondensateFactor = number; break;
                    case "slope": settings.Slope = value == "50" ? DrainSlope.OneIn50 : DrainSlope.OneIn100; break;
                    case "labels": settings.WriteLabels = value != "0"; break;
                    case "labelHeight": if (isNumber) settings.LabelHeight = number; break;
                    case "labelFormat": settings.LabelFormat = value; break;
                    case "labelLayer": settings.LabelLayer = value; break;
                    case "radius": if (isNumber) settings.CapacityRadius = number; break;
                }
            }

            string error;
            if (!settings.IsValid(out error))
                return new DrainSettings();
            return settings;
        }

        private static bool TryNumber(string value, out double number)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                && !double.IsNaN(number) && !double.IsInfinity(number);
        }

        private static void Add(StringBuilder text, string key, string value)
        {
            text.Append(key).Append('=').Append(value).Append('\n');
        }

        private static string Num(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
