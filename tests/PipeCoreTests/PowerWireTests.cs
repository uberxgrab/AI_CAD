using System;
using System.Collections.Generic;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Pipes;

namespace PipeCoreTests
{
    /// <summary>
    /// HVACPOWERWIRE core: the settings, the series order of the units, the hook geometry (a drop, a 45 degree
    /// slant and a rise whose top is level with the drop's), the last unit, the plain-drop fallback, the
    /// text placement and the cleaning of rough lines. Expected values are worked out by hand.
    /// </summary>
    internal static class PowerWireTests
    {
        private const double S = 300;
        private const string Wire = "EIA_5 SUPPLY WIRE";

        public static void Run()
        {
            SettingsDefaultsAndRoundTrip();
            SettingsBadInput();
            SettingsValidation();
            HookGeometryFunction();
            SeriesOrderAndHooks();
            HookGeometryProperties();
            BranchesOnBothSides();
            LastUnitDropOnly();
            SingleUnitCases();
            ShortBranchFallsBackToPlainDrop();
            CloseNextJunctionFallsBack();
            BranchNotAtRightAngle();
            TrunkEndOvershootRule();
            SubTrunk();
            TextPlacement();
            TextAcrossSides();
            RotatedBaseAngle();
            RoughLinesAreStraightened();
            ErrorsAndOtherNetworks();
        }

        // ------------------------------------------------------------------ helpers

        private static RefrigPath Path(params double[] xy)
        {
            RefrigPath path = new RefrigPath { Layer = Wire, Elevation = 0, SourceCurve = 0 };
            for (int i = 0; i + 1 < xy.Length; i += 2)
                path.Points.Add(Support.V(xy[i], xy[i + 1]));
            return path;
        }

        private static PowerWireSettings Settings()
        {
            return new PowerWireSettings { HookSize = S, EndUnitTail = 1000 };
        }

        private static PowerWireResult Plan(Vec2 root, params RefrigPath[] paths)
        {
            return PowerWirePlanner.Plan(paths, Settings(), root, 500);
        }

        private static bool SamePoints(IList<Vec2> actual, params double[] xy)
        {
            if (actual.Count * 2 != xy.Length)
                return false;
            for (int i = 0; i < actual.Count; i++)
            {
                if (Math.Abs(actual[i].X - xy[2 * i]) > 1e-6 || Math.Abs(actual[i].Y - xy[2 * i + 1]) > 1e-6)
                    return false;
            }

            return true;
        }

        private static string Show(IList<Vec2> points)
        {
            List<string> parts = new List<string>();
            foreach (Vec2 point in points)
                parts.Add(point.ToString());
            return string.Join(" ", parts.ToArray());
        }

        private static PowerWireRun FirstWire(PowerWireResult result)
        {
            foreach (PowerWireRun run in result.Runs)
            {
                if (run.Kind == PowerWireRunKind.Wire)
                    return run;
            }

            return null;
        }

        // A trunk along +x from the origin, branches down at 2000, 5000 and 8000, the trunk ends at 10000.
        private static PowerWireResult ThreeBranchTrunk()
        {
            return Plan(
                Support.V(0, 0),
                Path(0, 0, 2000, 0),
                Path(2000, 0, 5000, 0),
                Path(5000, 0, 8000, 0),
                Path(8000, 0, 10000, 0),
                Path(2000, 0, 2000, -1000),
                Path(5000, 0, 5000, -1000),
                Path(8000, 0, 8000, -1000));
        }

        // ------------------------------------------------------------------ settings

        private static void SettingsDefaultsAndRoundTrip()
        {
            Check.Case("power wire settings defaults and round trip", () =>
            {
                PowerWireSettings d = new PowerWireSettings();
                string error;
                Check.True("defaults are valid", d.IsValid(out error));
                Check.Near("weld", 50, d.WeldTolerance);
                Check.Near("angle", 10, d.AngleToleranceDegrees);
                Check.True("UCS base angle", d.UseUcsBaseAngle);
                Check.Near("hook size", 300, d.HookSize);
                Check.Equal("layer", "EIA_5 SUPPLY WIRE", d.LayerName);
                Check.True("text on", d.WriteText);
                Check.Equal("cable text", "2x1.5 MM2", d.CableText);
                Check.Near("text height", 275, d.TextHeight);

                PowerWireSettings s = new PowerWireSettings
                {
                    WeldTolerance = 12.5,
                    AngleToleranceDegrees = 7,
                    UseUcsBaseAngle = false,
                    BaseAngleDegrees = 33.25,
                    HookSize = 250,
                    EndUnitTail = 1500,
                    Layer = PowerWireLayerKind.Signal,
                    WriteText = false,
                    CableText = "3x2.5 MM2 (CU)",
                    TextHeight = 200
                };
                string text = s.Serialize();
                Check.Contains("point decimal", text, "weld=12.5");
                PowerWireSettings back = PowerWireSettings.Deserialize(text);
                Check.Near("weld back", 12.5, back.WeldTolerance);
                Check.Near("angle back", 7, back.AngleToleranceDegrees);
                Check.False("UCS back", back.UseUcsBaseAngle);
                Check.Near("base angle back", 33.25, back.BaseAngleDegrees);
                Check.Near("hook back", 250, back.HookSize);
                Check.Near("tail back", 1500, back.EndUnitTail);
                Check.Equal("layer back", PowerWireLayerKind.Signal, back.Layer);
                Check.Equal("signal layer name", "EIA_5 SIGNAL WIRE", back.LayerName);
                Check.False("text back", back.WriteText);
                Check.Equal("cable text back", "3x2.5 MM2 (CU)", back.CableText);
                Check.Near("text height back", 200, back.TextHeight);
            });
        }

        private static void SettingsBadInput()
        {
            Check.Case("power wire settings bad input", () =>
            {
                Check.Near("empty text keeps defaults", 300, PowerWireSettings.Deserialize("").HookSize);
                Check.Near("null text keeps defaults", 300, PowerWireSettings.Deserialize(null).HookSize);
                PowerWireSettings back = PowerWireSettings.Deserialize(
                    "hook=abc\nunknown=1\nnonsense\n=3\nweld=20\ntext=1\r\n");
                Check.Near("bad number keeps the default", 300, back.HookSize);
                Check.Near("good number survives", 20, back.WeldTolerance);
                Check.Near("invalid value gives all defaults", 50,
                    PowerWireSettings.Deserialize("weld=20\nhook=-5\n").WeldTolerance);
            });
        }

        private static void SettingsValidation()
        {
            Check.Case("power wire settings validation", () =>
            {
                string error;
                Check.False("zero weld", new PowerWireSettings { WeldTolerance = 0 }.IsValid(out error));
                Check.False("huge angle", new PowerWireSettings { AngleToleranceDegrees = 30 }.IsValid(out error));
                Check.False("zero hook", new PowerWireSettings { HookSize = 0 }.IsValid(out error));
                Check.False("negative tail", new PowerWireSettings { EndUnitTail = -1 }.IsValid(out error));
                Check.False("zero text height", new PowerWireSettings { TextHeight = 0 }.IsValid(out error));
                Check.False("empty text", new PowerWireSettings { CableText = "" }.IsValid(out error));
                Check.False("two-line text", new PowerWireSettings { CableText = "a\nb" }.IsValid(out error));
                Check.True("empty text is fine when off", new PowerWireSettings { CableText = "", WriteText = false }.IsValid(out error));
            });
        }

        // ------------------------------------------------------------------ geometry

        private static void HookGeometryFunction()
        {
            Check.Case("hook points", () =>
            {
                // Trunk along +x, the unit below it: T, P, the slant's top Q and the rise's top R.
                List<Vec2> hook = PowerWirePlanner.HookPoints(
                    Support.V(100, 50), Support.V(1, 0), Support.V(0, -1), 922, 200);
                Check.True("template legend: drop 922, slant 200 x 200, rise 722",
                    SamePoints(hook, 100, 50, 100, -872, 300, -672, 300, 50), Show(hook));
                Check.Near("drop", 922, hook[0].DistanceTo(hook[1]));
                Check.Near("slant", 200 * Math.Sqrt(2), hook[1].DistanceTo(hook[2]));
                Check.Near("rise", 722, hook[2].DistanceTo(hook[3]));
            });
        }

        private static void SeriesOrderAndHooks()
        {
            Check.Case("series order and hooks on a trunk with three branches", () =>
            {
                PowerWireResult r = ThreeBranchTrunk();
                Check.True("ok", r.Ok, r.Error);
                Check.Equal("one wire", 1, r.Wires);
                Check.Equal("one run", 1, r.Runs.Count);
                Check.Equal("three hooks", 3, r.Hooks);
                Check.Equal("four units", 4, r.Units.Count);

                // One continuous polyline: each junction is replaced by its hook, the trunk goes on from the
                // top of the rise leg, and the trunk end is the last unit.
                PowerWireRun wire = FirstWire(r);
                Check.True("the wire runs along the trunk and hooks each unit",
                    SamePoints(wire.Points,
                        0, 0,
                        2000, 0, 2000, -1000, 2300, -700, 2300, 0,
                        5000, 0, 5000, -1000, 5300, -700, 5300, 0,
                        8000, 0, 8000, -1000, 8300, -700, 8300, 0,
                        10000, 0),
                    Show(wire.Points));

                Check.Equal("U1 number", 1, r.Units[0].Number);
                Check.True("U1 is the first branch", Support.Near(Support.V(2000, -1000), r.Units[0].Terminal));
                Check.True("U2 is the second branch", Support.Near(Support.V(5000, -1000), r.Units[1].Terminal));
                Check.True("U3 is the third branch", Support.Near(Support.V(8000, -1000), r.Units[2].Terminal));
                Check.True("U4 is the trunk end", Support.Near(Support.V(10000, 0), r.Units[3].Terminal));
                Check.Equal("U1 hook", PowerWireUnitKind.Hook, r.Units[0].Kind);
                Check.Equal("U3 hook", PowerWireUnitKind.Hook, r.Units[2].Kind);
                Check.Equal("U4 trunk end", PowerWireUnitKind.TrunkEnd, r.Units[3].Kind);
                Check.Equal("labels", "U4", r.Units[3].Label);
                Check.Equal("no warnings", 0, r.Warnings.Count);
                Check.True("the root is the supply end", Support.Near(Support.V(0, 0), r.Root));
            });

            Check.Case("series order does not depend on the order of the paths", () =>
            {
                PowerWireResult r = Plan(
                    Support.V(0, 0),
                    Path(8000, 0, 8000, -1000),
                    Path(5000, 0, 8000, 0),
                    Path(2000, 0, 2000, -1000),
                    Path(8000, 0, 10000, 0),
                    Path(5000, 0, 5000, -1000),
                    Path(2000, 0, 5000, 0),
                    Path(0, 0, 2000, 0));
                Check.True("ok", r.Ok, r.Error);
                Check.True("same order along the trunk",
                    Support.Near(Support.V(2000, -1000), r.Units[0].Terminal)
                    && Support.Near(Support.V(5000, -1000), r.Units[1].Terminal)
                    && Support.Near(Support.V(8000, -1000), r.Units[2].Terminal)
                    && Support.Near(Support.V(10000, 0), r.Units[3].Terminal));
            });

            Check.Case("the root can be at either end", () =>
            {
                // Supply at the far end: the order is reversed, and the units follow the new direction.
                PowerWireResult r = Plan(
                    Support.V(10000, 0),
                    Path(0, 0, 2000, 0),
                    Path(2000, 0, 5000, 0),
                    Path(5000, 0, 8000, 0),
                    Path(8000, 0, 10000, 0),
                    Path(2000, 0, 2000, -1000),
                    Path(5000, 0, 5000, -1000),
                    Path(8000, 0, 8000, -1000));
                Check.True("ok", r.Ok, r.Error);
                Check.True("U1 is the nearest branch to the supply", Support.Near(Support.V(8000, -1000), r.Units[0].Terminal));
                Check.True("U3 is the farthest branch", Support.Near(Support.V(2000, -1000), r.Units[2].Terminal));
                Check.True("U4 is the trunk end", Support.Near(Support.V(0, 0), r.Units[3].Terminal));

                // Going in -x the slant goes toward -x: Q = P + s along - s down.
                Check.True("the first hook",
                    SamePoints(FirstWire(r).Points.GetRange(0, 5), 10000, 0, 8000, 0, 8000, -1000, 7700, -700, 7700, 0),
                    Show(FirstWire(r).Points));
            });
        }

        private static void HookGeometryProperties()
        {
            Check.Case("every hook is two parallel legs a hook size apart with a 45 degree slant", () =>
            {
                PowerWireResult r = ThreeBranchTrunk();
                List<Vec2> p = FirstWire(r).Points;
                // Points 1..4, 5..8 and 9..12 are T, P, Q, R of the three hooks.
                for (int hook = 0; hook < 3; hook++)
                {
                    int i = 1 + 4 * hook;
                    Vec2 t = p[i];
                    Vec2 terminal = p[i + 1];
                    Vec2 q = p[i + 2];
                    Vec2 top = p[i + 3];
                    string name = "hook " + (hook + 1);

                    Vec2 drop = terminal - t;
                    Vec2 rise = top - q;
                    Vec2 slant = q - terminal;
                    Check.Near(name + ": drop and rise are parallel", 0, drop.Normalized().Cross(rise.Normalized()));
                    Check.True(name + ": drop and rise point opposite ways", drop.Dot(rise) < 0);
                    Check.Near(name + ": the legs are one hook size apart", S, Math.Abs(drop.Normalized().Cross(top - t)));
                    Check.Near(name + ": the rise is the drop less one hook size", drop.Length - S, rise.Length);
                    Check.Near(name + ": the slant is 45 degrees to the trunk", Math.PI / 4,
                        Math.Atan2(Math.Abs(slant.Y), Math.Abs(slant.X)), 1e-9);
                    Check.Near(name + ": the slant is hook size times root two", S * Math.Sqrt(2), slant.Length);
                    Check.Near(name + ": the slant goes s along the trunk", S, slant.X);
                    Check.Near(name + ": and s back toward it", S, slant.Y);
                    Check.Near(name + ": the tops are level on the trunk", 0, top.Y);
                    Check.Near(name + ": the drop starts on the trunk", 0, t.Y);
                    Check.Near(name + ": the rise top is a hook size past the junction", t.X + S, top.X);
                }
            });
        }

        private static void BranchesOnBothSides()
        {
            Check.Case("branches on both sides, in order along the trunk", () =>
            {
                PowerWireResult r = Plan(
                    Support.V(0, 0),
                    Path(0, 0, 2000, 0),
                    Path(2000, 0, 4000, 0),
                    Path(4000, 0, 7000, 0),
                    Path(2000, 0, 2000, 1500),
                    Path(4000, 0, 4000, -1000));
                Check.True("ok", r.Ok, r.Error);
                Check.Equal("units", 3, r.Units.Count);
                Check.True("U1 is above the trunk", Support.Near(Support.V(2000, 1500), r.Units[0].Terminal));
                Check.True("U2 is below the trunk", Support.Near(Support.V(4000, -1000), r.Units[1].Terminal));
                Check.True("the upper hook is the mirror image: Q = P + s along - s toward the trunk",
                    SamePoints(FirstWire(r).Points.GetRange(0, 5), 0, 0, 2000, 0, 2000, 1500, 2300, 1200, 2300, 0),
                    Show(FirstWire(r).Points));
                Check.True("the lower hook",
                    SamePoints(FirstWire(r).Points.GetRange(5, 4), 4000, 0, 4000, -1000, 4300, -700, 4300, 0),
                    Show(FirstWire(r).Points));
            });
        }

        private static void LastUnitDropOnly()
        {
            Check.Case("the last unit gets only the drop leg, and the trunk beyond it is trimmed", () =>
            {
                // The trunk runs 400 past the last branch: shorter than the least trunk end, so it is trimmed.
                PowerWireResult r = Plan(
                    Support.V(0, 0),
                    Path(0, 0, 2000, 0),
                    Path(2000, 0, 5000, 0),
                    Path(5000, 0, 5400, 0),
                    Path(2000, 0, 2000, -1000),
                    Path(5000, 0, 5000, -1000));
                Check.True("ok", r.Ok, r.Error);
                Check.Equal("two units", 2, r.Units.Count);
                Check.Equal("U1 hook", PowerWireUnitKind.Hook, r.Units[0].Kind);
                Check.Equal("U2 drop", PowerWireUnitKind.Drop, r.Units[1].Kind);
                Check.Equal("one hook", 1, r.Hooks);
                Check.True("the wire ends at the last unit with no slant or rise",
                    SamePoints(FirstWire(r).Points,
                        0, 0, 2000, 0, 2000, -1000, 2300, -700, 2300, 0, 5000, 0, 5000, -1000),
                    Show(FirstWire(r).Points));
                bool noted = false;
                foreach (string note in r.Notes)
                    noted |= note.IndexOf("trimmed", StringComparison.Ordinal) >= 0;
                Check.True("the trimmed end is reported", noted);
            });
        }

        private static void SingleUnitCases()
        {
            Check.Case("a single straight run is one unit at its end", () =>
            {
                PowerWireResult r = Plan(Support.V(0, 0), Path(0, 0, 5000, 0));
                Check.True("ok", r.Ok, r.Error);
                Check.Equal("one unit", 1, r.Units.Count);
                Check.Equal("trunk end", PowerWireUnitKind.TrunkEnd, r.Units[0].Kind);
                Check.True("the wire is the run", SamePoints(FirstWire(r).Points, 0, 0, 5000, 0), Show(FirstWire(r).Points));
                Check.Equal("no hooks", 0, r.Hooks);
            });

            Check.Case("a run with a bend is one unit and the wire follows the bend", () =>
            {
                PowerWireResult r = Plan(Support.V(0, 0), Path(0, 0, 2000, 0, 2000, -1000));
                Check.True("ok", r.Ok, r.Error);
                Check.Equal("one unit", 1, r.Units.Count);
                Check.True("the wire keeps the corner",
                    SamePoints(FirstWire(r).Points, 0, 0, 2000, 0, 2000, -1000), Show(FirstWire(r).Points));
            });

            Check.Case("one branch and a short overshoot: one unit, drop leg only", () =>
            {
                PowerWireResult r = Plan(
                    Support.V(0, 0),
                    Path(0, 0, 2000, 0),
                    Path(2000, 0, 2200, 0),
                    Path(2000, 0, 2000, -1000));
                Check.True("ok", r.Ok, r.Error);
                Check.Equal("one unit", 1, r.Units.Count);
                Check.Equal("drop", PowerWireUnitKind.Drop, r.Units[0].Kind);
                Check.True("the wire turns down the branch at the junction",
                    SamePoints(FirstWire(r).Points, 0, 0, 2000, 0, 2000, -1000), Show(FirstWire(r).Points));
                Check.Equal("no hook", 0, r.Hooks);
            });

            Check.Case("a last unit with a bent branch keeps the bend", () =>
            {
                PowerWireResult r = Plan(
                    Support.V(0, 0),
                    Path(0, 0, 2000, 0),
                    Path(2000, 0, 2200, 0),
                    Path(2000, 0, 2000, -1000, 3000, -1000));
                Check.True("ok", r.Ok, r.Error);
                Check.Equal("drop", PowerWireUnitKind.Drop, r.Units[0].Kind);
                Check.True("the wire follows the branch to the unit",
                    SamePoints(FirstWire(r).Points, 0, 0, 2000, 0, 2000, -1000, 3000, -1000), Show(FirstWire(r).Points));
                Check.Equal("no warning for a last unit", 0, r.Warnings.Count);
            });
        }

        private static void ShortBranchFallsBackToPlainDrop()
        {
            Check.Case("a branch shorter than the hook keeps a plain drop, circled and warned", () =>
            {
                // The middle branch is 300 long; a hook needs 300 plus a 50 margin.
                PowerWireResult r = Plan(
                    Support.V(0, 0),
                    Path(0, 0, 2000, 0),
                    Path(2000, 0, 5000, 0),
                    Path(5000, 0, 8000, 0),
                    Path(2000, 0, 2000, -1000),
                    Path(5000, 0, 5000, -300),
                    Path(8000, 0, 8000, -1000),
                    Path(8000, 0, 10000, 0));
                Check.True("ok", r.Ok, r.Error);
                Check.Equal("four units", 4, r.Units.Count);
                Check.Equal("U2 is a plain drop", PowerWireUnitKind.Spur, r.Units[1].Kind);
                Check.Equal("U1 and U3 are still hooks", 2, r.Hooks);
                Check.Equal("two runs", 2, r.Runs.Count);
                Check.Equal("one wire", 1, r.Wires);
                Check.True("the wire runs straight past the short branch",
                    SamePoints(FirstWire(r).Points,
                        0, 0, 2000, 0, 2000, -1000, 2300, -700, 2300, 0, 8000, 0, 8000, -1000, 8300, -700, 8300, 0, 10000, 0),
                    Show(FirstWire(r).Points));
                PowerWireRun spur = r.Runs[1];
                Check.Equal("the plain drop is a separate run", PowerWireRunKind.Spur, spur.Kind);
                Check.True("it is the branch as drawn", SamePoints(spur.Points, 5000, 0, 5000, -300), Show(spur.Points));
                Check.Equal("one warning", 1, r.Warnings.Count);
                Check.Contains("the warning names the unit", r.Warnings[0], "U2");
                Check.Contains("and says why", r.Warnings[0], "shorter");
                Check.Equal("one circle", 1, r.WarningPoints.Count);
                Check.True("at the junction", Support.Near(Support.V(5000, 0), r.WarningPoints[0]));
            });
        }

        private static void CloseNextJunctionFallsBack()
        {
            Check.Case("a next junction closer than the hook keeps the first branch a plain drop", () =>
            {
                // 2000 and 2200 are 200 apart; the hook at the first needs 350.
                PowerWireResult r = Plan(
                    Support.V(0, 0),
                    Path(0, 0, 2000, 0),
                    Path(2000, 0, 2200, 0),
                    Path(2200, 0, 6000, 0),
                    Path(2000, 0, 2000, 1000),
                    Path(2200, 0, 2200, -1000));
                Check.True("ok", r.Ok, r.Error);
                Check.Equal("U1 is a plain drop", PowerWireUnitKind.Spur, r.Units[0].Kind);
                Check.Contains("the warning says why", r.Warnings[0], "next bend or junction");
                Check.True("U2 is hooked: the trunk goes on 3800 from it",
                    r.Units[1].Kind == PowerWireUnitKind.Hook);
            });

            Check.Case("a bend closer than the hook keeps the branch a plain drop", () =>
            {
                PowerWireResult r = Plan(
                    Support.V(0, 0),
                    Path(0, 0, 2000, 0),
                    Path(2000, 0, 2200, 0, 2200, -3000),
                    Path(2000, 0, 2000, 1000));
                Check.True("ok", r.Ok, r.Error);
                // The trunk end is 3200 past the junction, so it is a unit; the branch cannot be hooked.
                Check.Equal("U1 is a plain drop", PowerWireUnitKind.Spur, r.Units[0].Kind);
                Check.Equal("U2 is the trunk end", PowerWireUnitKind.TrunkEnd, r.Units[1].Kind);
            });
        }

        private static void BranchNotAtRightAngle()
        {
            Check.Case("a branch that is not at right angles keeps a plain drop", () =>
            {
                PowerWireResult r = Plan(
                    Support.V(0, 0),
                    Path(0, 0, 2000, 0),
                    Path(2000, 0, 6000, 0),
                    Path(2000, 0, 2500, -1000));
                Check.True("ok", r.Ok, r.Error);
                Check.Equal("U1 is a plain drop", PowerWireUnitKind.Spur, r.Units[0].Kind);
                Check.Contains("the warning says why", r.Warnings[0], "right angle");
            });

            Check.Case("a branch with a bend keeps a plain drop", () =>
            {
                PowerWireResult r = Plan(
                    Support.V(0, 0),
                    Path(0, 0, 2000, 0),
                    Path(2000, 0, 6000, 0),
                    Path(2000, 0, 2000, -1000, 3000, -1000));
                Check.True("ok", r.Ok, r.Error);
                Check.Equal("U1 is a plain drop", PowerWireUnitKind.Spur, r.Units[0].Kind);
                Check.Contains("the warning says why", r.Warnings[0], "bend");
            });
        }

        private static void TrunkEndOvershootRule()
        {
            Check.Case("a trunk end is a unit only when it is long enough", () =>
            {
                // 900 past the last branch: below the default 1000, so it is trimmed.
                PowerWireResult shortTail = Plan(
                    Support.V(0, 0),
                    Path(0, 0, 3000, 0),
                    Path(3000, 0, 3900, 0),
                    Path(3000, 0, 3000, -1000));
                Check.Equal("an overshoot of 900 is no unit", 1, shortTail.Units.Count);

                PowerWireResult longTail = Plan(
                    Support.V(0, 0),
                    Path(0, 0, 3000, 0),
                    Path(3000, 0, 4000, 0),
                    Path(3000, 0, 3000, -1000));
                Check.Equal("1000 is a unit", 2, longTail.Units.Count);
                Check.Equal("the branch is hooked", PowerWireUnitKind.Hook, longTail.Units[0].Kind);
                Check.Equal("the trunk end is last", PowerWireUnitKind.TrunkEnd, longTail.Units[1].Kind);

                PowerWireSettings low = Settings();
                low.EndUnitTail = 0;
                PowerWireResult lowThreshold = PowerWirePlanner.Plan(
                    new[] { Path(0, 0, 3000, 0), Path(3000, 0, 3200, 0), Path(3000, 0, 3000, -1000) },
                    low, Support.V(0, 0), 500);
                // 200 is shorter than a hook (350), whatever the setting.
                Check.Equal("never shorter than a hook", 1, lowThreshold.Units.Count);

                low.EndUnitTail = 500;
                PowerWireResult mid = PowerWirePlanner.Plan(
                    new[] { Path(0, 0, 3000, 0), Path(3000, 0, 3600, 0), Path(3000, 0, 3000, -1000) },
                    low, Support.V(0, 0), 500);
                Check.Equal("600 is a unit when the least is 500", 2, mid.Units.Count);
            });
        }

        private static void SubTrunk()
        {
            Check.Case("a branch with junctions of its own gets its own wire, numbered in depth-first order", () =>
            {
                PowerWireResult r = Plan(
                    Support.V(0, 0),
                    Path(0, 0, 3000, 0),
                    Path(3000, 0, 8000, 0),
                    Path(3000, 0, 3000, -2000),
                    Path(3000, -2000, 3000, -5000),
                    Path(3000, -2000, 1500, -2000));
                Check.True("ok", r.Ok, r.Error);
                Check.Equal("two wires", 2, r.Wires);
                Check.Equal("three units", 3, r.Units.Count);
                Check.True("U1 is the sub-trunk's branch", Support.Near(Support.V(1500, -2000), r.Units[0].Terminal));
                Check.True("U2 is the end of the sub-trunk", Support.Near(Support.V(3000, -5000), r.Units[1].Terminal));
                Check.True("U3 is the end of the main trunk", Support.Near(Support.V(8000, 0), r.Units[2].Terminal));
                Check.True("the main wire is the straight trunk",
                    SamePoints(r.Runs[0].Points, 0, 0, 8000, 0), Show(r.Runs[0].Points));
                Check.True("the sub-trunk's wire starts at the trunk and hooks its branch",
                    SamePoints(r.Runs[1].Points,
                        3000, 0, 3000, -2000, 1500, -2000, 1800, -2300, 3000, -2300, 3000, -5000),
                    Show(r.Runs[1].Points));
                Check.Equal("one hook", 1, r.Hooks);
                bool noted = false;
                foreach (string note in r.Notes)
                    noted |= note.IndexOf("junctions of its own", StringComparison.Ordinal) >= 0;
                Check.True("the sub-trunk is reported", noted);
            });
        }

        // ------------------------------------------------------------------ text

        private static void TextPlacement()
        {
            Check.Case("cable text beside the rise leg, along it, one text height away", () =>
            {
                PowerWireResult r = ThreeBranchTrunk();
                PowerWireText t = r.Units[0].Text;
                // Q of the first hook is (2300, -700); the rise leg goes up (+y) from there. The text runs up
                // the leg from 0.25 h above Q, with its glyphs on the +x side, one height (275) from the leg.
                Check.Near("rotation is 90 degrees", Math.PI / 2, t.Rotation, 1e-9);
                Check.False("it starts at the anchor", t.RightJustified);
                Check.Near("anchor x: the leg at 2300 plus two heights (a height gap, then the glyphs hang back)", 2300 + 550, t.Anchor.X);
                Check.Near("anchor y: 0.25 h above the slant's top", -700 + 0.25 * 275, t.Anchor.Y);
            });

            Check.Case("text beside a drop-only unit and a trunk-end unit", () =>
            {
                PowerWireResult drop = Plan(
                    Support.V(0, 0),
                    Path(0, 0, 2000, 0),
                    Path(2000, 0, 2200, 0),
                    Path(2000, 0, 2000, -1000));
                PowerWireText t = drop.Units[0].Text;
                // The drop leg is x = 2000 going down; the text goes on the +x side (the way the trunk went on).
                Check.Near("drop: rotation", Math.PI / 2, t.Rotation, 1e-9);
                Check.Near("drop: anchor x", 2000 + 550, t.Anchor.X);
                Check.Near("drop: anchor y", -1000 + 0.25 * 275, t.Anchor.Y);

                PowerWireResult end = Plan(Support.V(0, 0), Path(0, 0, 5000, 0));
                PowerWireText e = end.Units[0].Text;
                Check.Near("trunk end: rotation along the wire", 0, e.Rotation, 1e-9);
                Check.True("trunk end: the text ends near the wire end", e.RightJustified);
                Check.Near("trunk end: anchor x, 0.25 h before the end", 5000 - 0.25 * 275, e.Anchor.X);
                Check.Near("trunk end: anchor y, a text height below the wire", -550, e.Anchor.Y);
            });
        }

        private static void TextAcrossSides()
        {
            Check.Case("text for a branch above the trunk reads upright and ends at the leg", () =>
            {
                PowerWireResult r = Plan(
                    Support.V(0, 0),
                    Path(0, 0, 2000, 0),
                    Path(2000, 0, 6000, 0),
                    Path(2000, 0, 2000, 1500));
                PowerWireUnit unit = r.Units[0];
                Check.Equal("hook", PowerWireUnitKind.Hook, unit.Kind);
                // Q = (2300, 1200); the leg runs down (-y) toward the trunk at y = 0. Reading downward would be
                // upside down, so the text reads upward and ends 0.25 h below Q... on the trunk side.
                PowerWireText t = unit.Text;
                Check.Near("rotation is 90 degrees", Math.PI / 2, t.Rotation, 1e-9);
                Check.True("right justified", t.RightJustified);
                Check.Near("anchor x", 2300 + 550, t.Anchor.X);
                Check.Near("anchor y", 1200 - 0.25 * 275, t.Anchor.Y);
            });
        }

        // ------------------------------------------------------------------ cleaning and rotation

        private static List<PipeCurve> Curves(double angle, params double[][] lines)
        {
            List<PipeCurve> curves = new List<PipeCurve>();
            for (int i = 0; i < lines.Length; i++)
            {
                double[] l = lines[i];
                Vec2 a = Support.Rotate(Support.V(l[0], l[1]), angle);
                Vec2 b = Support.Rotate(Support.V(l[2], l[3]), angle);
                curves.Add(Support.Line(i, "0", a.X, a.Y, b.X, b.Y));
            }

            return curves;
        }

        private static void RotatedBaseAngle()
        {
            Check.Case("a network drawn at 30 degrees with the base angle at 30 degrees gets rotated hooks", () =>
            {
                double angle = 30 * Math.PI / 180.0;
                List<PipeCurve> curves = Curves(
                    angle,
                    new[] { 0.0, 0.0, 5000.0, 0.0 },
                    new[] { 5000.0, 0.0, 9000.0, 0.0 },
                    new[] { 5000.0, 0.0, 5000.0, -1000.0 });
                PowerWireResult r = PowerWirePlanner.Run(
                    curves, Settings(), angle, Support.Rotate(Support.V(0, 0), angle), 500);
                Check.True("ok", r.Ok, r.Error);
                Check.Equal("a hook and the trunk end", 2, r.Units.Count);
                Check.Equal("hook", PowerWireUnitKind.Hook, r.Units[0].Kind);

                // In the local frame: (0,0) (5000,0) (5000,-1000) (5300,-700) (5300,0) (9000,0).
                double[] local = { 0, 0, 5000, 0, 5000, -1000, 5300, -700, 5300, 0, 9000, 0 };
                List<Vec2> points = FirstWire(r).Points;
                bool same = points.Count * 2 == local.Length;
                for (int i = 0; same && i < points.Count; i++)
                {
                    Vec2 expected = Support.Rotate(Support.V(local[2 * i], local[2 * i + 1]), angle);
                    same = Support.Near(expected, points[i], 1e-6);
                }

                Check.True("the wire is the local hook turned by 30 degrees", same, Show(points));

                // The text is turned too. The rise leg runs toward the trunk at 120 degrees here; text along it
                // would read upside down, so it reads the other way, at 300 degrees (-60), ending at the leg.
                Check.Near("text rotation follows the base angle", 300 * Math.PI / 180.0, r.Units[0].Text.Rotation, 1e-9);
                Check.True("and is right justified", r.Units[0].Text.RightJustified);
            });

            Check.Case("with the base angle left at 0 a 30 degree network is not straightened onto the axes", () =>
            {
                double angle = 30 * Math.PI / 180.0;
                List<PipeCurve> curves = Curves(
                    angle,
                    new[] { 0.0, 0.0, 5000.0, 0.0 },
                    new[] { 5000.0, 0.0, 9000.0, 0.0 },
                    new[] { 5000.0, 0.0, 5000.0, -1000.0 });
                PowerWireResult r = PowerWirePlanner.Run(curves, Settings(), 0, Support.V(0, 0), 500);
                Check.True("ok", r.Ok, r.Error);
                // It stays as drawn (class "other"), still at 30 degrees: the hook is built on the drawn directions.
                Vec2 first = FirstWire(r).Points[1] - FirstWire(r).Points[0];
                Check.Near("the trunk keeps its 30 degrees", angle, first.Angle, 1e-9);
            });
        }

        private static void RoughLinesAreStraightened()
        {
            Check.Case("rough lines: the branch is straightened onto its unit and the trunk node follows", () =>
            {
                // The trunk is exact. The first branch is drawn from its unit at (2010, -1000) up to (2000, 3): a
                // little off vertical, touching the trunk's interior. The unit end never moves, so the T joint
                // moves to x = 2010 and the branch becomes vertical.
                List<PipeCurve> curves = Curves(
                    0,
                    new[] { 0.0, 0.0, 10000.0, 0.0 },
                    new[] { 2010.0, -1000.0, 2000.0, 3.0 },
                    new[] { 6000.0, -1000.0, 6000.0, 0.0 });
                PowerWireResult r = PowerWirePlanner.Run(curves, Settings(), 0, Support.V(0, 0), 500);
                Check.True("ok", r.Ok, r.Error);
                Check.Equal("three units", 3, r.Units.Count);
                Check.True("U1 is the first branch", Support.Near(Support.V(2010, -1000), r.Units[0].Terminal));
                Check.True("U2 is the second branch", Support.Near(Support.V(6000, -1000), r.Units[1].Terminal));
                Check.True("U3 is the trunk end", Support.Near(Support.V(10000, 0), r.Units[2].Terminal));
                Check.True("the first hook sits on the straightened branch",
                    SamePoints(FirstWire(r).Points.GetRange(0, 5), 0, 0, 2010, 0, 2010, -1000, 2310, -700, 2310, 0),
                    Show(FirstWire(r).Points));
                Check.Equal("no warnings", 0, r.Warnings.Count);
                Check.True("the cleaning result is kept", r.Clean != null && r.Clean.Ok);
                Check.Equal("the cleaner placed no refnets", 0, r.Clean.Refnets.Count);
            });

            Check.Case("the cleaner is run without a root, so there are no refnet reports or rule checks", () =>
            {
                List<PipeCurve> curves = Curves(
                    0,
                    new[] { 0.0, 0.0, 10000.0, 0.0 },
                    new[] { 2000.0, -1000.0, 2000.0, 0.0 });
                PowerWireResult r = PowerWirePlanner.Run(curves, Settings(), 0, Support.V(0, 0), 500);
                Check.True("ok", r.Ok, r.Error);
                Check.Equal("no refnets", 0, r.Clean.Refnets.Count);
                Check.Equal("no rule checks", 0, r.Clean.RuleChecks.Count);
                Check.Equal("no warnings", 0, r.Clean.Warnings.Count);
            });
        }

        // ------------------------------------------------------------------ refusals

        private static void ErrorsAndOtherNetworks()
        {
            Check.Case("the supply point must be a wire end", () =>
            {
                PowerWireResult r = Plan(Support.V(2500, 4000), Path(0, 0, 5000, 0));
                Check.False("not ok", r.Ok);
                Check.Contains("the message is about wires", r.Error, "supply");
                Check.Contains("and about the distance", r.Error, "500");
            });

            Check.Case("a joint of four wires is refused", () =>
            {
                PowerWireResult r = Plan(
                    Support.V(0, 0),
                    Path(0, 0, 2000, 0),
                    Path(2000, 0, 4000, 0),
                    Path(2000, 0, 2000, 1000),
                    Path(2000, 0, 2000, -1000));
                Check.False("not ok", r.Ok);
                Check.Contains("the message names the joint", r.Error, "4 wires");
                Check.Equal("the joint is marked", 1, r.ErrorPoints.Count);
            });

            Check.Case("a loop is refused", () =>
            {
                PowerWireResult r = Plan(
                    Support.V(0, 0),
                    Path(0, 0, 2000, 0),
                    Path(2000, 0, 2000, 1000, 4000, 1000, 4000, 0, 2000, 0),
                    Path(4000, 0, 6000, 0));
                Check.False("not ok", r.Ok);
                Check.Contains("the message says loop", r.Error, "loop");
                Check.Contains("and names this command", r.Error, "HVACPOWERWIRE");
            });

            Check.Case("lines of another network are left unchanged and reported", () =>
            {
                PowerWireResult r = Plan(
                    Support.V(0, 0),
                    Path(0, 0, 5000, 0),
                    Path(0, 9000, 3000, 9000));
                Check.True("ok", r.Ok, r.Error);
                Check.Equal("one unit", 1, r.Units.Count);
                Check.Equal("the other network is kept as cleaned", 1, r.Unreached.Count);
                Check.True("it is the other line", SamePoints(r.Unreached[0].Points, 0, 9000, 3000, 9000));
                Check.True("it is reported", r.Notes.Count > 0);
            });

            Check.Case("invalid settings are refused", () =>
            {
                PowerWireSettings bad = Settings();
                bad.HookSize = 0;
                PowerWireResult r = PowerWirePlanner.Plan(new[] { Path(0, 0, 5000, 0) }, bad, Support.V(0, 0), 500);
                Check.False("not ok", r.Ok);
                Check.Contains("the message names the setting", r.Error, "hook size");
            });

            Check.Case("no lines is refused", () =>
            {
                PowerWireResult r = PowerWirePlanner.Run(new List<PipeCurve>(), Settings(), 0, Support.V(0, 0), 500);
                Check.False("not ok", r.Ok);
            });
        }
    }
}
