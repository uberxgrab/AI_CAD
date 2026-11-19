using System;
using System.Globalization;
using System.Text;

namespace HvacAiCadAddin.Pipes
{
    // What the HVACPIPESIZE and HVACREFRIG dialogs remember between runs, as plain text
    // (key=value per line). Deserialisation never throws: unknown keys and bad numbers keep the
    // defaults. Numbers are always written and read with the invariant culture.

    /// <summary>HVACPIPESIZE: the last pipe type and size, and the endpoint weld tolerance.</summary>
    public sealed class PipeSizeSettings
    {
        public PipeKind Kind = PipeKind.Chws;
        public string SizeText = "DN100";

        /// <summary>Endpoints closer than this are one node when the run is found (mm).</summary>
        public double WeldTolerance = 1.0;

        public bool IsValid(out string error)
        {
            error = null;
            if (!PipeCatalog.IsKnownSize(Kind, SizeText))
                error = "Size " + SizeText + " is not offered for " + PipeCatalog.Label(Kind) + ".";
            else if (double.IsNaN(WeldTolerance) || double.IsInfinity(WeldTolerance)
                || WeldTolerance < 0.001 || WeldTolerance > 1000)
                error = "The weld tolerance must be between 0.001 and 1000 mm.";
            return error == null;
        }

        public string Serialize()
        {
            StringBuilder text = new StringBuilder();
            Add(text, "type", Kind.ToString());
            Add(text, "size", SizeText ?? "");
            Add(text, "weld", Num(WeldTolerance));
            return text.ToString();
        }

        public static PipeSizeSettings Deserialize(string text)
        {
            PipeSizeSettings settings = new PipeSizeSettings();
            if (string.IsNullOrEmpty(text))
                return settings;

            foreach (string rawLine in text.Split('\n'))
            {
                string key;
                string value;
                if (!SplitLine(rawLine, out key, out value))
                    continue;

                double number;
                switch (key)
                {
                    case "type":
                        PipeKind kind;
                        if (Enum.TryParse(value, true, out kind) && Enum.IsDefined(typeof(PipeKind), kind))
                            settings.Kind = kind;
                        break;
                    case "size":
                        settings.SizeText = value;
                        break;
                    case "weld":
                        if (TryNumber(value, out number) && number >= 0.001 && number <= 1000)
                            settings.WeldTolerance = number;
                        break;
                }
            }

            string error;
            if (!settings.IsValid(out error))
            {
                // A remembered size from a different type: fall back to that type's first size.
                PipeKind kind = Enum.IsDefined(typeof(PipeKind), settings.Kind) ? settings.Kind : PipeKind.Chws;
                settings.Kind = kind;
                settings.SizeText = PipeCatalog.SizeTexts(kind)[0];
                if (!settings.IsValid(out error))
                    settings = new PipeSizeSettings();
            }

            return settings;
        }

        internal static bool SplitLine(string rawLine, out string key, out string value)
        {
            key = null;
            value = null;
            string line = rawLine.Trim('\r', ' ');
            int equals = line.IndexOf('=');
            if (equals <= 0)
                return false;
            key = line.Substring(0, equals);
            value = line.Substring(equals + 1);
            return true;
        }

        internal static bool TryNumber(string value, out double number)
        {
            return double.TryParse(
                value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                && !double.IsNaN(number) && !double.IsInfinity(number);
        }

        internal static void Add(StringBuilder text, string key, string value)
        {
            text.Append(key).Append('=').Append(value).Append('\n');
        }

        internal static string Num(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>HVACREFRIG settings with the approved defaults (AGENTS.md, "HVACREFRIG").</summary>
    public sealed class RefrigSettings
    {
        /// <summary>Block inserted at each refnet.</summary>
        public const string RefnetBlockName = "Branch Connection (1)";

        /// <summary>Block-local distance from the inlet to the main outlet, at scale 1.</summary>
        public const double RefnetLength = 750;

        /// <summary>Block-local sideways distance from the main outlet to the branch outlet, at scale 1.</summary>
        public const double RefnetBranchOffset = 200;

        public double WeldTolerance = 50;
        public double AngleToleranceDegrees = 10;
        public double AlignTolerance = 200;
        public double JogTolerance = 200;
        public double RefnetScale = 1.24;

        /// <summary>True: the base angle is the UCS X axis. False: <see cref="BaseAngleDegrees"/>, from world X.</summary>
        public bool UseUcsBaseAngle = true;
        public double BaseAngleDegrees = 0;

        public bool IsValid(out string error)
        {
            error = null;
            if (!Finite(WeldTolerance) || WeldTolerance <= 0 || WeldTolerance > 1000)
                error = "The weld tolerance must be above 0 and at most 1000 mm.";
            else if (!Finite(AngleToleranceDegrees) || AngleToleranceDegrees <= 0 || AngleToleranceDegrees > 22.5)
                error = "The angle tolerance must be above 0 and at most 22.5 degrees "
                    + "(the horizontal, vertical and 45 degree classes must not overlap).";
            else if (!Finite(AlignTolerance) || AlignTolerance < 0 || AlignTolerance > 100000)
                error = "The align tolerance must be 0 or more.";
            else if (!Finite(JogTolerance) || JogTolerance < 0 || JogTolerance > 100000)
                error = "The jog tolerance must be 0 or more.";
            else if (!Finite(RefnetScale) || RefnetScale <= 0 || RefnetScale > 100)
                error = "The refnet scale must be above 0 and at most 100.";
            else if (!Finite(BaseAngleDegrees))
                error = "The base angle must be a number.";
            return error == null;
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        public string Serialize()
        {
            StringBuilder text = new StringBuilder();
            PipeSizeSettings.Add(text, "weld", PipeSizeSettings.Num(WeldTolerance));
            PipeSizeSettings.Add(text, "angle", PipeSizeSettings.Num(AngleToleranceDegrees));
            PipeSizeSettings.Add(text, "align", PipeSizeSettings.Num(AlignTolerance));
            PipeSizeSettings.Add(text, "jog", PipeSizeSettings.Num(JogTolerance));
            PipeSizeSettings.Add(text, "scale", PipeSizeSettings.Num(RefnetScale));
            PipeSizeSettings.Add(text, "ucs", UseUcsBaseAngle ? "1" : "0");
            PipeSizeSettings.Add(text, "baseAngle", PipeSizeSettings.Num(BaseAngleDegrees));
            return text.ToString();
        }

        public static RefrigSettings Deserialize(string text)
        {
            RefrigSettings settings = new RefrigSettings();
            if (string.IsNullOrEmpty(text))
                return settings;

            foreach (string rawLine in text.Split('\n'))
            {
                string key;
                string value;
                if (!PipeSizeSettings.SplitLine(rawLine, out key, out value))
                    continue;

                double number;
                bool isNumber = PipeSizeSettings.TryNumber(value, out number);
                switch (key)
                {
                    case "weld": if (isNumber) settings.WeldTolerance = number; break;
                    case "angle": if (isNumber) settings.AngleToleranceDegrees = number; break;
                    case "align": if (isNumber) settings.AlignTolerance = number; break;
                    case "jog": if (isNumber) settings.JogTolerance = number; break;
                    case "scale": if (isNumber) settings.RefnetScale = number; break;
                    case "ucs": settings.UseUcsBaseAngle = value != "0"; break;
                    case "baseAngle": if (isNumber) settings.BaseAngleDegrees = number; break;
                }
            }

            string error;
            if (!settings.IsValid(out error))
                return new RefrigSettings();
            return settings;
        }
    }
}
