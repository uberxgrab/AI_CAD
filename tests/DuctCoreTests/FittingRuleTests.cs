using System;
using HvacAiCadAddin.Ducts;

namespace DuctCoreTests
{
    /// <summary>
    /// Section "Fitting dimensions". Every expected number is worked out by hand from the table.
    /// </summary>
    internal static class FittingRuleTests
    {
        public static void Run()
        {
            Elbows();
            Transitions();
            Taps();
        }

        private static void Elbows()
        {
            FittingSettings f = new FittingSettings();
            DuctSize w600 = DuctSize.Rect(600, 300);

            Check.Case("elbow radius and takeups, W=600", () =>
            {
                // R = 0.75 * 600 = 450; takeup = R * tan(theta / 2)
                Check.Near("R", 450, DuctFittings.ElbowRadius(w600, f, false));
                Check.Near("90 deg takeup 450 * tan(45)", 450, DuctFittings.ElbowTakeup(450, Math.PI / 2));
                Check.Near("60 deg takeup 450 * tan(30)", 259.80762113533, DuctFittings.ElbowTakeup(450, Math.PI / 3), 1e-6);
                Check.Near("45 deg takeup 450 * tan(22.5)", 186.39610306789, DuctFittings.ElbowTakeup(450, Math.PI / 4), 1e-6);
                Check.Near("R = W/2 fallback", 300, DuctFittings.ElbowRadius(w600, f, true));
                Check.Near("R = W/2 fallback 90 deg takeup", 300, DuctFittings.ElbowTakeup(300, Math.PI / 2));
            });

            Check.Case("elbow radius for other sizes", () =>
            {
                Check.Near("W=400 -> 300", 300, DuctFittings.ElbowRadius(DuctSize.Rect(400, 250), f, false));
                // Width in plan is the first number, not the larger one.
                Check.Near("300x600 uses the plan width 300", 225, DuctFittings.ElbowRadius(DuctSize.Rect(300, 600), f, false));
                Check.Near("round D250 -> 1.5 d = 375", 375, DuctFittings.ElbowRadius(DuctSize.Round(250), f, false));
                Check.Near("round fallback d/2", 125, DuctFittings.ElbowRadius(DuctSize.Round(250), f, true));
            });

            Check.Case("elbow radius never drops below W/2", () =>
            {
                FittingSettings tight = new FittingSettings { RectElbowRadiusFactor = 0.3 };
                Check.Near("factor 0.3 * 600 = 180 < 300 -> 300", 300, DuctFittings.ElbowRadius(w600, tight, false));
                FittingSettings wide = new FittingSettings { RectElbowRadiusFactor = 1.5 };
                Check.Near("factor 1.5 * 600 = 900", 900, DuctFittings.ElbowRadius(w600, wide, false));
            });
        }

        private static void Transitions()
        {
            FittingSettings f = new FittingSettings();
            Check.Case("concentric transition lengths", () =>
            {
                // L = max(W_large / 2, 1.207 * dW, 1.207 * dH)
                Check.Near("1000x300 -> 500x300: max(500, 603.5, 0)", 603.5,
                    DuctFittings.ConcentricLength(DuctSize.Rect(1000, 300), DuctSize.Rect(500, 300), f), 1e-9);
                Check.Near("600x300 -> 400x300: max(300, 241.4)", 300,
                    DuctFittings.ConcentricLength(DuctSize.Rect(600, 300), DuctSize.Rect(400, 300), f));
                Check.Near("same width, 300 -> 200 high: max(300, 120.7)", 300,
                    DuctFittings.ConcentricLength(DuctSize.Rect(600, 300), DuctSize.Rect(600, 200), f));
                Check.Near("600x300 -> 300x600: dW = dH = 300, 362.1", 362.1,
                    DuctFittings.ConcentricLength(DuctSize.Rect(600, 300), DuctSize.Rect(300, 600), f), 1e-9);
                Check.Near("symmetric: 500x300 -> 1000x300", 603.5,
                    DuctFittings.ConcentricLength(DuctSize.Rect(500, 300), DuctSize.Rect(1000, 300), f), 1e-9);
                Check.Near("round D400 -> D250: max(200, 181.05)", 200,
                    DuctFittings.ConcentricLength(DuctSize.Round(400), DuctSize.Round(250), f));
                Check.Near("round D200 -> D500: max(250, 362.1)", 362.1,
                    DuctFittings.ConcentricLength(DuctSize.Round(200), DuctSize.Round(500), f), 1e-9);
                // Rectangular to round uses d in place of the width: dW = 250, dH = 50.
                Check.Near("500x300 -> D250: max(250, 301.75)", 301.75,
                    DuctFittings.ConcentricLength(DuctSize.Rect(500, 300), DuctSize.Round(250), f), 1e-9);
            });

            Check.Case("eccentric transition lengths", () =>
            {
                // L = max(W_large / 2, 1.732 * dW)
                Check.Near("600 -> 400: max(300, 346.4)", 346.4, DuctFittings.EccentricLength(600, 400, f), 1e-9);
                Check.Near("800 -> 700: max(400, 173.2)", 400, DuctFittings.EccentricLength(800, 700, f));
                Check.Near("400 -> 600 is symmetric", 346.4, DuctFittings.EccentricLength(400, 600, f), 1e-9);
            });
        }

        private static void Taps()
        {
            FittingSettings f = new FittingSettings();
            Check.Case("rectangular shoe tap lead and opening", () =>
            {
                Check.Near("Wb=350 lead max(175, 102)", 175, DuctFittings.RectTapLead(350, f));
                Check.Near("Wb=350 opening Wb + L", 525, DuctFittings.RectTapOpening(350, f));
                Check.Near("Wb=150 lead max(75, 102)", 102, DuctFittings.RectTapLead(150, f));
                Check.Near("Wb=150 opening", 252, DuctFittings.RectTapOpening(150, f));
                Check.Near("Wb=204 lead exactly 102", 102, DuctFittings.RectTapLead(204, f));
                Check.Near("Wb=800 lead 400", 400, DuctFittings.RectTapLead(800, f));
            });

            Check.Case("round conical tap", () =>
            {
                Check.Near("branch starts 152 + 51 = 203", 203, DuctFittings.RoundTapLead(f));
                Check.Near("D250 opening d + 51", 301, DuctFittings.TapOpening(DuctSize.Round(250), f));
                Check.Near("rect branch opening via TapOpening", 525, DuctFittings.TapOpening(DuctSize.Rect(350, 250), f));
                Check.Near("rect branch lead via TapLead", 175, DuctFittings.TapLead(DuctSize.Rect(350, 250), f));
                Check.Near("round branch lead via TapLead", 203, DuctFittings.TapLead(DuctSize.Round(250), f));
            });

            Check.Case("adjusted tap factors", () =>
            {
                FittingSettings custom = new FittingSettings { TapLeadFactor = 0.25, TapMinLead = 100 };
                Check.Near("Wb=800 * 0.25 = 200", 200, DuctFittings.RectTapLead(800, custom));
                Check.Near("Wb=200 -> minimum 100", 100, DuctFittings.RectTapLead(200, custom));
            });
        }
    }
}
