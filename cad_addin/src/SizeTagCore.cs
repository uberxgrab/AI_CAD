using System;
using System.Collections.Generic;
using System.Globalization;

namespace HvacAiCadAddin.Takeoff
{
    // The size tag HVACNORM writes on a pipe or a single-line duct whose layer does not say its size
    // (user decision of 2026-11-23: no new layers; a pipe of another material keeps its layer and gets a
    // BOQ prefix). HVACQTO reads it back. AutoCAD-free: the xdata is handled as (group code, value)
    // pairs, so the console tests link this file.
    //
    // HVACAI_PIPE: [1000 "1"][1000 system][1000 material][1000 BOQ prefix][1040 size mm][1040 second size mm]
    //              [1000 size text][1040 slope %][1000 confidence][1000 source layer][1000 label]
    // HVACAI_DUCT: [1000 "1"][1000 system][1040 width or diameter mm][1040 height mm][1070 round 0/1]
    //              [1000 size text][1000 confidence][1000 source layer][1000 label]
    // The second pipe size is the liquid side of a refrigerant pair (0 otherwise); slope 0 = none.

    public enum SizeTagKind
    {
        Pipe,
        Duct
    }

    public sealed class SizeTag
    {
        public const string PipeApp = "HVACAI_PIPE";
        public const string DuctApp = "HVACAI_DUCT";
        private const string Version = "1";
        private const int MaxString = 250; // xdata strings are limited to 255 bytes

        public SizeTagKind Kind;
        public string System = "";
        public string Material = "";
        public string Prefix = "";
        public double SizeMm;
        public double Size2Mm;
        public bool Round;
        public string SizeText = "";
        public double Slope;
        public string Confidence = "";
        public string SourceLayer = "";
        public string Label = "";

        public string App
        {
            get { return Kind == SizeTagKind.Duct ? DuctApp : PipeApp; }
        }

        /// <summary>The values after the application name, as (group code, value) pairs.</summary>
        public List<KeyValuePair<short, object>> ToValues()
        {
            List<KeyValuePair<short, object>> values = new List<KeyValuePair<short, object>>();
            AddText(values, Version);
            AddText(values, System);
            if (Kind == SizeTagKind.Pipe)
            {
                AddText(values, Material);
                AddText(values, Prefix);
                values.Add(new KeyValuePair<short, object>(1040, SizeMm));
                values.Add(new KeyValuePair<short, object>(1040, Size2Mm));
                AddText(values, SizeText);
                values.Add(new KeyValuePair<short, object>(1040, Slope));
            }
            else
            {
                values.Add(new KeyValuePair<short, object>(1040, SizeMm));
                values.Add(new KeyValuePair<short, object>(1040, Size2Mm));
                values.Add(new KeyValuePair<short, object>(1070, (short)(Round ? 1 : 0)));
                AddText(values, SizeText);
            }

            AddText(values, Confidence);
            AddText(values, SourceLayer);
            AddText(values, Label);
            return values;
        }

        /// <summary>Reads the values after the application name; false for anything else.</summary>
        public static bool TryParse(string app, IList<KeyValuePair<short, object>> values, out SizeTag tag)
        {
            tag = null;
            if (values == null || values.Count < 2)
                return false;
            SizeTagKind kind;
            if (string.Equals(app, PipeApp, StringComparison.OrdinalIgnoreCase))
                kind = SizeTagKind.Pipe;
            else if (string.Equals(app, DuctApp, StringComparison.OrdinalIgnoreCase))
                kind = SizeTagKind.Duct;
            else
                return false;
            if (!string.Equals(Text(values, 0), Version, StringComparison.Ordinal))
                return false;

            SizeTag parsed = new SizeTag { Kind = kind, System = Text(values, 1) };
            int next;
            if (kind == SizeTagKind.Pipe)
            {
                if (values.Count < 11)
                    return false;
                parsed.Material = Text(values, 2);
                parsed.Prefix = Text(values, 3);
                parsed.SizeMm = Number(values, 4);
                parsed.Size2Mm = Number(values, 5);
                parsed.SizeText = Text(values, 6);
                parsed.Slope = Number(values, 7);
                next = 8;
            }
            else
            {
                if (values.Count < 9)
                    return false;
                parsed.SizeMm = Number(values, 2);
                parsed.Size2Mm = Number(values, 3);
                parsed.Round = Number(values, 4) != 0;
                parsed.SizeText = Text(values, 5);
                next = 6;
            }

            parsed.Confidence = Text(values, next);
            parsed.SourceLayer = Text(values, next + 1);
            parsed.Label = Text(values, next + 2);
            if (double.IsNaN(parsed.SizeMm) || parsed.SizeMm <= 0)
                return false;
            tag = parsed;
            return true;
        }

        private static void AddText(List<KeyValuePair<short, object>> values, string text)
        {
            string value = (text ?? "").Replace('\r', ' ').Replace('\n', ' ');
            if (value.Length > MaxString)
                value = value.Substring(0, MaxString);
            values.Add(new KeyValuePair<short, object>(1000, value));
        }

        private static string Text(IList<KeyValuePair<short, object>> values, int index)
        {
            return index < values.Count && values[index].Value != null
                ? Convert.ToString(values[index].Value, CultureInfo.InvariantCulture)
                : "";
        }

        private static double Number(IList<KeyValuePair<short, object>> values, int index)
        {
            if (index >= values.Count || values[index].Value == null)
                return double.NaN;
            try
            {
                return Convert.ToDouble(values[index].Value, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return double.NaN;
            }
            catch (InvalidCastException)
            {
                return double.NaN;
            }
        }
    }
}
