using HvacAiCadAddin.Pipes;

namespace PipeCoreTests
{
    /// <summary>The remembered dialog values: defaults, round trips and bad input.</summary>
    internal static class SettingsTests
    {
        public static void Run()
        {
            PipeSizeDefaults();
            PipeSizeRoundTrip();
            PipeSizeBadInput();
            RefrigDefaults();
            RefrigRoundTrip();
            RefrigBadInput();
            RefrigValidation();
        }

        private static void PipeSizeDefaults()
        {
            Check.Case("pipe size defaults", () =>
            {
                PipeSizeSettings settings = new PipeSizeSettings();
                Check.Equal("kind", PipeKind.Chws, settings.Kind);
                Check.Equal("size", "DN100", settings.SizeText);
                Check.Near("weld", 1.0, settings.WeldTolerance);
                string error;
                Check.True("valid", settings.IsValid(out error));
                Check.Equal("empty text keeps defaults", "DN100", PipeSizeSettings.Deserialize("").SizeText);
                Check.Equal("null text keeps defaults", "DN100", PipeSizeSettings.Deserialize(null).SizeText);
            });
        }

        private static void PipeSizeRoundTrip()
        {
            Check.Case("pipe size round trip", () =>
            {
                PipeSizeSettings settings = new PipeSizeSettings
                {
                    Kind = PipeKind.Refrigerant,
                    SizeText = "28.6-12.7 (33.6-44.8)",
                    WeldTolerance = 2.5
                };
                PipeSizeSettings back = PipeSizeSettings.Deserialize(settings.Serialize());
                Check.Equal("kind", PipeKind.Refrigerant, back.Kind);
                Check.Equal("size", "28.6-12.7 (33.6-44.8)", back.SizeText);
                Check.Near("weld", 2.5, back.WeldTolerance);
                Check.Contains("weld is written with a point", settings.Serialize(), "weld=2.5");
            });
        }

        private static void PipeSizeBadInput()
        {
            Check.Case("pipe size bad input", () =>
            {
                PipeSizeSettings back = PipeSizeSettings.Deserialize(
                    "type=Hws\nsize=DN32\nweld=abc\nunknown=1\nnonsense\n=3\n");
                Check.Equal("kind survives", PipeKind.Hws, back.Kind);
                Check.Equal("size survives", "DN32", back.SizeText);
                Check.Near("bad weld keeps default", 1.0, back.WeldTolerance);

                PipeSizeSettings mismatch = PipeSizeSettings.Deserialize("type=Condensate\nsize=DN100\n");
                Check.Equal("kind kept", PipeKind.Condensate, mismatch.Kind);
                Check.Equal("size falls back to the type's first", "DN21", mismatch.SizeText);

                PipeSizeSettings badType = PipeSizeSettings.Deserialize("type=99\nsize=DN40\n");
                string error;
                Check.True("bad type still valid", badType.IsValid(out error));

                PipeSizeSettings outOfRange = PipeSizeSettings.Deserialize("weld=5000\n");
                Check.Near("out of range weld keeps default", 1.0, outOfRange.WeldTolerance);
            });
        }

        private static void RefrigDefaults()
        {
            Check.Case("refrigerant defaults", () =>
            {
                RefrigSettings s = new RefrigSettings();
                Check.Near("weld", 50, s.WeldTolerance);
                Check.Near("angle", 10, s.AngleToleranceDegrees);
                Check.Near("align", 200, s.AlignTolerance);
                Check.Near("jog", 200, s.JogTolerance);
                Check.Near("scale", 1.24, s.RefnetScale);
                Check.Near("R1 refnet spacing", 1000, s.RefnetSpacing);
                Check.Near("R2 straight pipe after an outlet", 500, s.OutletStraight);
                Check.Near("R3 longest run to an indoor unit", 40000, s.MaxUnitRun);
                Check.Near("R3 construction allowance", 3000, s.RunAllowance);
                Check.True("UCS X", s.UseUcsBaseAngle);
                Check.True("size pipes is on", s.SizePipes);
                Check.True("size labels are on", s.WriteLabels);
                Check.Near("label height", 150, s.LabelHeight);
                Check.Near("capacity search radius", 1500, s.LabelSearchRadius);
                string error;
                Check.True("valid", s.IsValid(out error));
                Check.Equal("block name", "Branch Connection (1)", RefrigSettings.RefnetBlockName);
                Check.Near("inlet to main outlet", 750, RefrigSettings.RefnetLength);
                Check.Near("branch offset", 200, RefrigSettings.RefnetBranchOffset);
            });
        }

        private static void RefrigRoundTrip()
        {
            Check.Case("refrigerant round trip", () =>
            {
                RefrigSettings s = new RefrigSettings
                {
                    WeldTolerance = 35.5,
                    AngleToleranceDegrees = 7.5,
                    AlignTolerance = 150,
                    JogTolerance = 120.25,
                    RefnetScale = 1.35,
                    UseUcsBaseAngle = false,
                    BaseAngleDegrees = 12.5,
                    RefnetSpacing = 1250.5,
                    OutletStraight = 600,
                    MaxUnitRun = 45000,
                    RunAllowance = 2500,
                    SizePipes = false,
                    WriteLabels = false,
                    LabelHeight = 187.5,
                    LabelSearchRadius = 2250.5
                };
                RefrigSettings back = RefrigSettings.Deserialize(s.Serialize());
                Check.Near("weld", 35.5, back.WeldTolerance);
                Check.Near("angle", 7.5, back.AngleToleranceDegrees);
                Check.Near("align", 150, back.AlignTolerance);
                Check.Near("jog", 120.25, back.JogTolerance);
                Check.Near("scale", 1.35, back.RefnetScale);
                Check.False("custom base angle", back.UseUcsBaseAngle);
                Check.Near("base angle", 12.5, back.BaseAngleDegrees);
                Check.Near("R1", 1250.5, back.RefnetSpacing);
                Check.Near("R2", 600, back.OutletStraight);
                Check.Near("R3 limit", 45000, back.MaxUnitRun);
                Check.Near("R3 allowance", 2500, back.RunAllowance);
                Check.False("size pipes off is remembered", back.SizePipes);
                Check.False("size labels off is remembered", back.WriteLabels);
                Check.Near("label height", 187.5, back.LabelHeight);
                Check.Near("capacity search radius", 2250.5, back.LabelSearchRadius);
                Check.Contains("label height is written with a point", s.Serialize(), "labelHeight=187.5");
                Check.Contains("scale is written with a point", s.Serialize(), "scale=1.35");
                Check.Contains("R1 is written with a point", s.Serialize(), "spacing=1250.5");

                RefrigSettings old = RefrigSettings.Deserialize("weld=40\nscale=1.3\n");
                Check.Near("a file from before the rules keeps the R1 default", 1000, old.RefnetSpacing);
                Check.Near("a file from before the rules keeps the R3 default", 40000, old.MaxUnitRun);
                Check.Near("and its own values", 40, old.WeldTolerance);
                Check.True("a file from before sizing keeps sizing on", old.SizePipes);
                Check.True("and the labels on", old.WriteLabels);
                Check.Near("and the 150 mm label height", 150, old.LabelHeight);
                Check.Near("and the 1500 mm search radius", 1500, old.LabelSearchRadius);
            });
        }

        private static void RefrigBadInput()
        {
            Check.Case("refrigerant bad input", () =>
            {
                RefrigSettings back = RefrigSettings.Deserialize("weld=abc\nscale=2\nangle=30\n");
                // An invalid value drops the whole file back to the defaults.
                Check.Near("weld default", 50, back.WeldTolerance);
                Check.Near("scale default", 1.24, back.RefnetScale);
                Check.Near("angle default", 10, back.AngleToleranceDegrees);

                RefrigSettings ok = RefrigSettings.Deserialize("weld=abc\nscale=2\nunknown=7\n");
                Check.Near("good scale kept", 2, ok.RefnetScale);
                Check.Near("bad weld ignored", 50, ok.WeldTolerance);
            });
        }

        private static void Invalid(string label, System.Action<RefrigSettings> change, string fragment)
        {
            Check.Case(label, () =>
            {
                RefrigSettings s = new RefrigSettings();
                change(s);
                string error;
                Check.False(label, s.IsValid(out error));
                Check.Contains(label + " message", error, fragment);
            });
        }

        private static void RefrigValidation()
        {
            Invalid("zero weld", s => s.WeldTolerance = 0, "weld tolerance");
            Invalid("huge weld", s => s.WeldTolerance = 5000, "weld tolerance");
            Invalid("zero angle", s => s.AngleToleranceDegrees = 0, "angle tolerance");
            Invalid("angle above 22.5", s => s.AngleToleranceDegrees = 22.6, "angle tolerance");
            Invalid("negative align", s => s.AlignTolerance = -1, "align tolerance");
            Invalid("negative jog", s => s.JogTolerance = -1, "jog tolerance");
            Invalid("zero scale", s => s.RefnetScale = 0, "refnet scale");
            Invalid("NaN scale", s => s.RefnetScale = double.NaN, "refnet scale");
            Invalid("NaN base angle", s => s.BaseAngleDegrees = double.NaN, "base angle");
            Invalid("negative R1", s => s.RefnetSpacing = -1, "refnet spacing");
            Invalid("negative R2", s => s.OutletStraight = -1, "straight pipe");
            Invalid("zero R3 limit", s => s.MaxUnitRun = 0, "longest run");
            Invalid("NaN R3 limit", s => s.MaxUnitRun = double.NaN, "longest run");
            Invalid("allowance as big as the limit", s => s.RunAllowance = 40000, "allowance");
            Invalid("negative allowance", s => s.RunAllowance = -5, "allowance");
            Invalid("zero label height", s => s.LabelHeight = 0, "label height");
            Invalid("negative label height", s => s.LabelHeight = -150, "label height");
            Invalid("NaN label height", s => s.LabelHeight = double.NaN, "label height");
            Invalid("zero search radius", s => s.LabelSearchRadius = 0, "search radius");

            Check.Case("edge values are valid", () =>
            {
                RefrigSettings s = new RefrigSettings { AngleToleranceDegrees = 22.5, AlignTolerance = 0, JogTolerance = 0 };
                string error;
                Check.True("22.5 degrees, zero tolerances", s.IsValid(out error));
            });
        }
    }
}
