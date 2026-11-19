using System.Globalization;
using HvacAiCadAddin.Ducts;

namespace DuctCoreTests
{
    /// <summary>Dialog settings persistence: culture-independent text, tolerant reading.</summary>
    internal static class SettingsTests
    {
        public static void Run()
        {
            RoundTrip();
            TolerantReading();
            Validation();
        }

        private static void RoundTrip()
        {
            Check.Case("settings round trip with decimals, whatever the culture", () =>
            {
                DuctDrawSettings settings = new DuctDrawSettings();
                settings.IsRound = true;
                settings.SizeText = "D350";
                settings.Layer = "EIA_1 SUPPLY AIR";
                settings.SegmentLength = 1234.5;
                settings.Fittings.RectElbowRadiusFactor = 1.25;
                settings.Fittings.ConcentricSlope = 1.207;
                settings.Fittings.TapMinLead = 110.5;

                string text = settings.Serialize();
                Check.Contains("invariant decimal point", text, "length=1234.5");
                Check.Contains("shape", text, "shape=round");
                Check.Contains("layer", text, "layer=EIA_1 SUPPLY AIR");
                Check.True("no decimal comma", !text.Contains("1234,5"));

                DuctDrawSettings back = DuctDrawSettings.Deserialize(text);
                Check.True("round", back.IsRound);
                Check.Equal("size", "D350", back.SizeText);
                Check.Equal("layer", "EIA_1 SUPPLY AIR", back.Layer);
                Check.Near("length", 1234.5, back.SegmentLength);
                Check.Near("rect elbow", 1.25, back.Fittings.RectElbowRadiusFactor);
                Check.Near("concentric slope", 1.207, back.Fittings.ConcentricSlope);
                Check.Near("tap minimum", 110.5, back.Fittings.TapMinLead);
                Check.Near("untouched default", 1.5, back.Fittings.RoundElbowRadiusFactor);
            });

            Check.Case("defaults match the approved table", () =>
            {
                FittingSettings f = new DuctDrawSettings().Fittings;
                Check.Near("rect R factor", 0.75, f.RectElbowRadiusFactor);
                Check.Near("round R factor", 1.5, f.RoundElbowRadiusFactor);
                Check.Near("concentric slope", 1.207, f.ConcentricSlope);
                Check.Near("eccentric slope", 1.732, f.EccentricSlope);
                Check.Near("tap factor", 0.5, f.TapLeadFactor);
                Check.Near("tap minimum", 102, f.TapMinLead);
                Check.Near("round tap base", 51, f.RoundTapBaseExtra);
                Check.Near("round tap cone", 152, f.RoundTapConeLength);
                Check.Near("round tap collar", 51, f.RoundTapCollar);
                Check.Near("default length", 1000, new DuctDrawSettings().SegmentLength);
            });
        }

        private static void TolerantReading()
        {
            Check.Case("empty, garbage and unknown keys keep the defaults", () =>
            {
                Check.Near("null", 1000, DuctDrawSettings.Deserialize(null).SegmentLength);
                Check.Near("empty", 1000, DuctDrawSettings.Deserialize("").SegmentLength);
                DuctDrawSettings s = DuctDrawSettings.Deserialize(
                    "garbage\r\nlength=abc\r\nfuture=1\r\nrectElbow=NaN\r\nsize=500x250\r\n=3\r\n");
                Check.Near("bad number keeps default", 1000, s.SegmentLength);
                Check.Near("NaN keeps default", 0.75, s.Fittings.RectElbowRadiusFactor);
                Check.Equal("valid line still read", "500x250", s.SizeText);
            });

            Check.Case("CRLF lines are read", () =>
            {
                DuctDrawSettings s = DuctDrawSettings.Deserialize("length=750\r\nshape=rect\r\n");
                Check.Near("length", 750, s.SegmentLength);
                Check.False("rect", s.IsRound);
            });

            Check.Case("out-of-range fitting factors reset to the defaults", () =>
            {
                DuctDrawSettings s = DuctDrawSettings.Deserialize("rectElbow=-3\r\nconcentricSlope=1.5\r\n");
                Check.Near("reset rect", 0.75, s.Fittings.RectElbowRadiusFactor);
                Check.Near("whole set reset", 1.207, s.Fittings.ConcentricSlope);
            });
        }

        private static void Validation()
        {
            Check.Case("FittingSettings.IsValid", () =>
            {
                string error;
                Check.True("defaults valid", new FittingSettings().IsValid(out error));
                Check.False("zero radius factor", new FittingSettings { RectElbowRadiusFactor = 0 }.IsValid(out error));
                Check.True("message given", !string.IsNullOrEmpty(error));
                Check.False("huge slope", new FittingSettings { EccentricSlope = 100 }.IsValid(out error));
                Check.False("negative tap minimum", new FittingSettings { TapMinLead = -1 }.IsValid(out error));
            });
        }
    }
}
