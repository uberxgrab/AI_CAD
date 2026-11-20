using System;
using System.Collections.Generic;
using HvacAiCadAddin.ControlWires;
using HvacAiCadAddin.Ducts;

namespace PipeCoreTests
{
    /// <summary>
    /// HVACCTRLWIRE: the remembered values, the block-local terminal transform, and the wire layout
    /// (parallel offsets, separate start points, exits at the nearest route point, both sides, no
    /// crossings, warnings, arcs refused). Expected values are worked out by hand from the approved
    /// specification (AGENTS.md, "HVACCTRLWIRE").
    /// </summary>
    internal static class ControlWireTests
    {
        public static void Run()
        {
            SettingsDefaults();
            SettingsRoundTrip();
            SettingsBadInput();
            SettingsValidation();
            RouteChecks();
            AffineMapping();
            StraightLeft();
            StraightRight();
            RouteAlongY();
            CustomSpacing();
            ManyUnitsOneSide();
            BothSides();
            LShapedRoute();
            ExitBeyondTheEnds();
            DuplicateRoutePoints();
            TerminalOnTheRoute();
            OverlapIsReported();
            SharpCornerIsReported();
            CableTextPlacement();
            NoUnits();
            SeparateStartsAndNoCrossingsStress();
        }

        // ------------------------------------------------------------------ helpers

        private static Vec2 V(double x, double y)
        {
            return new Vec2(x, y);
        }

        private static IList<OutlineVertex> Route(params double[] xy)
        {
            List<OutlineVertex> route = new List<OutlineVertex>();
            for (int i = 0; i + 1 < xy.Length; i += 2)
                route.Add(new OutlineVertex(xy[i], xy[i + 1], 0));
            return route;
        }

        private static List<ControlWireUnit> Units(params double[] xy)
        {
            List<ControlWireUnit> units = new List<ControlWireUnit>();
            for (int i = 0; i + 1 < xy.Length; i += 2)
                units.Add(new ControlWireUnit { Name = "U" + (i / 2), Terminal = V(xy[i], xy[i + 1]) });
            return units;
        }

        private static ControlWirePlan Plan(IList<OutlineVertex> route, List<ControlWireUnit> units, double spacing = 150)
        {
            return ControlWirePlanner.Plan(new ControlWireInput { Route = route, Units = units, Spacing = spacing });
        }

        private static ControlWire WireOf(ControlWirePlan plan, int unitIndex)
        {
            foreach (ControlWire wire in plan.Wires)
            {
                if (wire.UnitIndex == unitIndex)
                    return wire;
            }

            return null;
        }

        /// <summary>True when the wire's vertices are exactly these points, in order.</summary>
        private static bool Is(ControlWire wire, params double[] xy)
        {
            if (wire == null || wire.Points.Count * 2 != xy.Length)
                return false;
            for (int i = 0; i < wire.Points.Count; i++)
            {
                if (wire.Points[i].DistanceTo(V(xy[2 * i], xy[2 * i + 1])) > 1e-6)
                    return false;
            }

            return true;
        }

        private static string Show(ControlWire wire)
        {
            if (wire == null)
                return "(no wire)";
            List<string> parts = new List<string>();
            foreach (Vec2 point in wire.Points)
                parts.Add(point.ToString());
            return string.Join(" ", parts);
        }

        private static int Warned(ControlWirePlan plan, string fragment)
        {
            int count = 0;
            foreach (ControlWireWarning warning in plan.Warnings)
            {
                if (warning.Message.IndexOf(fragment, StringComparison.Ordinal) >= 0)
                    count++;
            }

            return count;
        }

        // Independent of the planner: orientation tests, proper crossings only (strict).
        private static double Orient(Vec2 a, Vec2 b, Vec2 c)
        {
            return (b - a).Cross(c - a);
        }

        private static bool ProperCross(Vec2 p1, Vec2 p2, Vec2 p3, Vec2 p4)
        {
            double d1 = Orient(p3, p4, p1);
            double d2 = Orient(p3, p4, p2);
            double d3 = Orient(p1, p2, p3);
            double d4 = Orient(p1, p2, p4);
            return ((d1 > 1e-9 && d2 < -1e-9) || (d1 < -1e-9 && d2 > 1e-9))
                && ((d3 > 1e-9 && d4 < -1e-9) || (d3 < -1e-9 && d4 > 1e-9));
        }

        private static int CrossingPairs(ControlWirePlan plan)
        {
            int pairs = 0;
            for (int i = 0; i < plan.Wires.Count; i++)
            {
                for (int j = i + 1; j < plan.Wires.Count; j++)
                {
                    bool crossed = false;
                    List<Vec2> a = plan.Wires[i].Points;
                    List<Vec2> b = plan.Wires[j].Points;
                    for (int s = 0; s + 1 < a.Count && !crossed; s++)
                    {
                        for (int t = 0; t + 1 < b.Count; t++)
                        {
                            if (ProperCross(a[s], a[s + 1], b[t], b[t + 1]))
                            {
                                crossed = true;
                                break;
                            }
                        }
                    }

                    if (crossed)
                        pairs++;
                }
            }

            return pairs;
        }

        private static int TemplateCrossings(ControlWirePlan plan)
        {
            int count = 0;
            foreach (ControlWire wire in plan.Wires)
            {
                for (int s = 0; s + 1 < wire.Points.Count; s++)
                {
                    for (int t = 0; t + 1 < plan.Route.Count; t++)
                    {
                        if (ProperCross(wire.Points[s], wire.Points[s + 1], plan.Route[t], plan.Route[t + 1]))
                            count++;
                    }
                }
            }

            return count;
        }

        // ------------------------------------------------------------------ settings

        private static void SettingsDefaults()
        {
            Check.Case("control wire settings defaults", () =>
            {
                ControlWireSettings settings = new ControlWireSettings();
                Check.Near("spacing", 150, settings.Spacing);
                Check.True("text on", settings.WriteText);
                Check.Equal("text", "2x0.75 MM2", settings.TextString);
                Check.Near("text height", 275, settings.TextHeight);
                Check.Equal("layer", "EIA_5 CONTROL WIRE", ControlWireSettings.LayerName);
                Check.Equal("linetype", "Broken Line", ControlWireSettings.LinetypeName);
                Check.Equal("text style", "T_unicode", ControlWireSettings.TextStyleName);
                Check.Equal("xdata app", "HVACAI_CTRLWIRE", ControlWireSettings.XDataApp);
                string error;
                Check.True("valid", settings.IsValid(out error));
                Check.Near("empty text keeps defaults", 150, ControlWireSettings.Deserialize("").Spacing);
                Check.Near("null text keeps defaults", 150, ControlWireSettings.Deserialize(null).Spacing);
            });
        }

        private static void SettingsRoundTrip()
        {
            Check.Case("control wire settings round trip", () =>
            {
                ControlWireSettings settings = new ControlWireSettings
                {
                    Spacing = 200.5,
                    WriteText = false,
                    TextString = "3x1.5 MM2",
                    TextHeight = 300
                };
                ControlWireSettings back = ControlWireSettings.Deserialize(settings.Serialize());
                Check.Near("spacing", 200.5, back.Spacing);
                Check.False("text off", back.WriteText);
                Check.Equal("text", "3x1.5 MM2", back.TextString);
                Check.Near("height", 300, back.TextHeight);
                Check.Contains("spacing is written with a point", settings.Serialize(), "spacing=200.5");
            });
        }

        private static void SettingsBadInput()
        {
            Check.Case("control wire settings bad input", () =>
            {
                ControlWireSettings back = ControlWireSettings.Deserialize(
                    "spacing=abc\nheight=-4\ntext=  \nunknown=1\nnonsense\n=3\nwritetext=1\n");
                Check.Near("bad spacing keeps default", 150, back.Spacing);
                Check.Near("negative height keeps default", 275, back.TextHeight);
                Check.Equal("blank text keeps default", "2x0.75 MM2", back.TextString);
                Check.True("write text", back.WriteText);

                ControlWireSettings multiline = new ControlWireSettings { TextString = "a\r\nb" };
                Check.Equal("a line break is flattened", "a  b",
                    ControlWireSettings.Deserialize(multiline.Serialize()).TextString);
            });
        }

        private static void SettingsValidation()
        {
            Check.Case("control wire settings validation", () =>
            {
                string error;
                Check.False("zero spacing", new ControlWireSettings { Spacing = 0 }.IsValid(out error));
                Check.Contains("spacing message", error, "spacing");
                Check.False("negative spacing", new ControlWireSettings { Spacing = -5 }.IsValid(out error));
                Check.False("NaN spacing", new ControlWireSettings { Spacing = double.NaN }.IsValid(out error));
                Check.False("huge spacing", new ControlWireSettings { Spacing = 10001 }.IsValid(out error));
                Check.False("empty text", new ControlWireSettings { TextString = " " }.IsValid(out error));
                Check.False("zero height", new ControlWireSettings { TextHeight = 0 }.IsValid(out error));
                Check.True("height ignored when the text is off",
                    new ControlWireSettings { WriteText = false, TextHeight = 0, TextString = "" }.IsValid(out error));
            });
        }

        // ------------------------------------------------------------------ route checks

        private static void RouteChecks()
        {
            Check.Case("an arc in the template is refused", () =>
            {
                List<OutlineVertex> route = new List<OutlineVertex>
                {
                    new OutlineVertex(0, 0, 0.4142),
                    new OutlineVertex(1000, 0, 0),
                    new OutlineVertex(1000, 1000, 0)
                };
                ControlWirePlan plan = Plan(route, Units(500, 500));
                Check.False("not ok", plan.Ok);
                Check.Contains("message", plan.Error, "arcs are not supported");
                Check.Equal("no wires", 0, plan.Wires.Count);

                string error;
                Check.False("CheckRoute agrees", ControlWirePlanner.CheckRoute(route, false, out error));
                Check.Contains("CheckRoute message", error, "arcs are not supported");
            });

            Check.Case("an arc on a middle vertex is refused", () =>
            {
                List<OutlineVertex> route = new List<OutlineVertex>
                {
                    new OutlineVertex(0, 0, 0),
                    new OutlineVertex(1000, 0, -1),
                    new OutlineVertex(1000, 1000, 0)
                };
                Check.False("not ok", Plan(route, Units(500, 500)).Ok);
            });

            Check.Case("the bulge of the last vertex belongs to no segment", () =>
            {
                List<OutlineVertex> route = new List<OutlineVertex>
                {
                    new OutlineVertex(0, 0, 0),
                    new OutlineVertex(5000, 0, 0.5)
                };
                Check.True("ok", Plan(route, Units(2000, 1500)).Ok);
            });

            Check.Case("a closed or too-short route is refused", () =>
            {
                string error;
                Check.False("closed", ControlWirePlanner.CheckRoute(Route(0, 0, 1000, 0, 1000, 1000), true, out error));
                Check.Contains("closed message", error, "closed");
                Check.False("one point", ControlWirePlanner.CheckRoute(Route(0, 0), false, out error));
                Check.False("null", ControlWirePlanner.CheckRoute(null, false, out error));
                ControlWirePlan plan = Plan(Route(100, 100, 100, 100), Units(500, 500));
                Check.False("a route with no length", plan.Ok);
                Check.Contains("no length message", plan.Error, "no length");
            });

            Check.Case("a bad spacing is refused", () =>
            {
                Check.False("zero", Plan(Route(0, 0, 5000, 0), Units(2000, 1000), 0).Ok);
                Check.False("negative", Plan(Route(0, 0, 5000, 0), Units(2000, 1000), -1).Ok);
                Check.False("null input", ControlWirePlanner.Plan(null).Ok);
            });
        }

        // ------------------------------------------------------------------ terminal transform

        private static void AffineMapping()
        {
            Check.Case("terminal transform: translation and rotation", () =>
            {
                // Template unit at (1000, 2000) turned 90 degrees; another at (5000, 0) not turned.
                ControlWireAffine template = new ControlWireAffine(0, -1, 1, 0, 1000, 2000);
                ControlWireAffine other = new ControlWireAffine(1, 0, 0, 1, 5000, 0);
                // Block-local (100, 50) is at (1000 - 50, 2000 + 100) for the template unit.
                Vec2 world = template.Apply(V(100, 50));
                Check.True("template terminal in the world", world.DistanceTo(V(950, 2100)) < 1e-9);
                Vec2 terminal;
                Check.True("mapped", ControlWireAffine.TryMapTerminal(world, template, other, out terminal));
                Check.True("same local point on the other unit", terminal.DistanceTo(V(5100, 50)) < 1e-9);
            });

            Check.Case("terminal transform: mirrored and rotated unit", () =>
            {
                // X scale -1 and a 30 degree turn, like the mirrored duct inserts in the template.
                double c = Math.Cos(Math.PI / 6);
                double s = Math.Sin(Math.PI / 6);
                ControlWireAffine template = new ControlWireAffine(c, -s, s, c, 100, 200);
                ControlWireAffine mirrored = new ControlWireAffine(-c, -s, -s, c, 3000, 4000);
                Vec2 local = V(120, -35);
                Vec2 world = template.Apply(local);
                Vec2 terminal;
                Check.True("mapped", ControlWireAffine.TryMapTerminal(world, template, mirrored, out terminal));
                Vec2 expected = mirrored.Apply(local);
                Check.True("the mirrored unit's own local point", terminal.DistanceTo(expected) < 1e-9);
            });

            Check.Case("terminal transform: the inverse undoes the map", () =>
            {
                ControlWireAffine map = new ControlWireAffine(2, 1, -1, 3, 7, -4);
                ControlWireAffine inverse;
                Check.True("invertible", map.TryInverse(out inverse));
                Vec2 back = inverse.Apply(map.Apply(V(12.5, -8)));
                Check.True("round trip", back.DistanceTo(V(12.5, -8)) < 1e-9);
            });

            Check.Case("terminal transform: a zero scale cannot be inverted", () =>
            {
                ControlWireAffine flat = new ControlWireAffine(0, 0, 0, 1, 10, 10);
                ControlWireAffine inverse;
                Check.False("not invertible", flat.TryInverse(out inverse));
                Vec2 terminal;
                Check.False("no mapping", ControlWireAffine.TryMapTerminal(
                    V(1, 1), flat, ControlWireAffine.Identity, out terminal));
            });

            Check.Case("terminal transform: from a Matrix3d array", () =>
            {
                // Row-major 4x4: rotation 90 degrees, translated by (10, 20, 0).
                double[] matrix =
                {
                    0, -1, 0, 10,
                    1, 0, 0, 20,
                    0, 0, 1, 0,
                    0, 0, 0, 1
                };
                ControlWireAffine affine = ControlWireAffine.FromMatrixArray(matrix);
                Check.True("applied", affine.Apply(V(3, 4)).DistanceTo(V(6, 23)) < 1e-9);
                bool threw = false;
                try
                {
                    ControlWireAffine.FromMatrixArray(new double[3]);
                }
                catch (ArgumentException)
                {
                    threw = true;
                }

                Check.True("a wrong length is refused", threw);
            });
        }

        // ------------------------------------------------------------------ one wire

        private static void StraightLeft()
        {
            Check.Case("one unit on the left of a straight route", () =>
            {
                ControlWirePlan plan = Plan(Route(0, 0, 10000, 0), Units(2000, 1500));
                Check.True("ok", plan.Ok);
                Check.Equal("one wire", 1, plan.Wires.Count);
                ControlWire wire = plan.Wires[0];
                Check.True("starts at its offset position, runs parallel, then exits straight out: " + Show(wire),
                    Is(wire, 0, 150, 2000, 150, 2000, 1500));
                Check.Equal("left", 1, wire.Side);
                Check.Equal("k", 1, wire.K);
                Check.Near("offset", 150, wire.Offset);
                Check.Near("leaves the route 2 m from the thermostat", 2000, wire.ExitDistance);
                Check.Equal("exit vertex", 1, wire.ExitIndex);
                Check.Equal("label", "W1", wire.Label);
                Check.Equal("unit name", "U0", wire.UnitName);
                Check.Near("length", 2000 + 1350, wire.Length);
                Check.Equal("no warnings", 0, plan.Warnings.Count);
                Check.Equal("route as used", 2, plan.Route.Count);
            });
        }

        private static void StraightRight()
        {
            Check.Case("one unit on the right of a straight route", () =>
            {
                ControlWirePlan plan = Plan(Route(0, 0, 10000, 0), Units(2000, -1500));
                Check.True("ok", plan.Ok);
                ControlWire wire = plan.Wires[0];
                Check.True("wire: " + Show(wire), Is(wire, 0, -150, 2000, -150, 2000, -1500));
                Check.Equal("right", -1, wire.Side);
                Check.Near("offset", -150, wire.Offset);
                Check.Equal("no warnings", 0, plan.Warnings.Count);
            });
        }

        private static void RouteAlongY()
        {
            Check.Case("a route running along Y has its left on the minus X side", () =>
            {
                ControlWirePlan plan = Plan(Route(0, 0, 0, 10000), Units(-1500, 3000, 1500, 6000));
                Check.True("ok", plan.Ok);
                ControlWire left = WireOf(plan, 0);
                ControlWire right = WireOf(plan, 1);
                Check.Equal("left side", 1, left.Side);
                Check.Equal("right side", -1, right.Side);
                Check.True("left wire: " + Show(left), Is(left, -150, 0, -150, 3000, -1500, 3000));
                Check.True("right wire: " + Show(right), Is(right, 150, 0, 150, 6000, 1500, 6000));
            });
        }

        private static void CustomSpacing()
        {
            Check.Case("the offset follows the spacing", () =>
            {
                ControlWirePlan plan = Plan(Route(0, 0, 10000, 0), Units(2000, 1500, 4000, 2500), 200);
                Check.True("ok", plan.Ok);
                Check.True("the later wire is d off: " + Show(WireOf(plan, 1)),
                    Is(WireOf(plan, 1), 0, 200, 4000, 200, 4000, 2500));
                Check.True("the earlier wire is 2 d off: " + Show(WireOf(plan, 0)),
                    Is(WireOf(plan, 0), 0, 400, 2000, 400, 2000, 1500));
            });
        }

        // ------------------------------------------------------------------ several wires

        private static void ManyUnitsOneSide()
        {
            Check.Case("four units on one side: the earliest exit is outermost, starts are d apart", () =>
            {
                ControlWirePlan plan = Plan(
                    Route(0, 0, 10000, 0), Units(2000, 1500, 4000, 2500, 6000, 1200, 7500, 3000));
                Check.True("ok", plan.Ok);
                Check.Equal("four wires", 4, plan.Wires.Count);
                Check.Equal("k of the first to leave", 4, WireOf(plan, 0).K);
                Check.Equal("k of the second", 3, WireOf(plan, 1).K);
                Check.Equal("k of the third", 2, WireOf(plan, 2).K);
                Check.Equal("k of the last to leave", 1, WireOf(plan, 3).K);

                Check.True("outermost: " + Show(WireOf(plan, 0)),
                    Is(WireOf(plan, 0), 0, 600, 2000, 600, 2000, 1500));
                Check.True("second: " + Show(WireOf(plan, 1)),
                    Is(WireOf(plan, 1), 0, 450, 4000, 450, 4000, 2500));
                Check.True("third: " + Show(WireOf(plan, 2)),
                    Is(WireOf(plan, 2), 0, 300, 6000, 300, 6000, 1200));
                Check.True("innermost: " + Show(WireOf(plan, 3)),
                    Is(WireOf(plan, 3), 0, 150, 7500, 150, 7500, 3000));

                // Each wire starts at its own point; there is no common start.
                for (int i = 0; i < plan.Wires.Count; i++)
                {
                    for (int j = i + 1; j < plan.Wires.Count; j++)
                    {
                        Check.True("starts " + i + " and " + j + " differ",
                            plan.Wires[i].Points[0].DistanceTo(plan.Wires[j].Points[0]) > 100);
                    }
                }

                Check.Near("start spacing is d", 150,
                    WireOf(plan, 1).Points[0].DistanceTo(WireOf(plan, 2).Points[0]));
                Check.Equal("no crossing wires", 0, CrossingPairs(plan));
                Check.Equal("no crossing of the template", 0, TemplateCrossings(plan));
                Check.Equal("no warnings", 0, plan.Warnings.Count);

                Check.Equal("W1 is the first to leave", "W1", WireOf(plan, 0).Label);
                Check.Equal("W4 is the last to leave", "W4", WireOf(plan, 3).Label);
            });
        }

        private static void BothSides()
        {
            Check.Case("units on both sides number their wires apart", () =>
            {
                ControlWirePlan plan = Plan(
                    Route(0, 0, 10000, 0), Units(3000, 1000, 3000, -1000, 5000, -2000));
                Check.True("ok", plan.Ok);
                Check.Equal("left unit k", 1, WireOf(plan, 0).K);
                Check.Equal("the earlier right unit k", 2, WireOf(plan, 1).K);
                Check.Equal("the later right unit k", 1, WireOf(plan, 2).K);
                Check.Equal("left side", 1, WireOf(plan, 0).Side);
                Check.Equal("right side", -1, WireOf(plan, 1).Side);

                Check.True("left: " + Show(WireOf(plan, 0)), Is(WireOf(plan, 0), 0, 150, 3000, 150, 3000, 1000));
                Check.True("right, outer: " + Show(WireOf(plan, 1)),
                    Is(WireOf(plan, 1), 0, -300, 3000, -300, 3000, -1000));
                Check.True("right, inner: " + Show(WireOf(plan, 2)),
                    Is(WireOf(plan, 2), 0, -150, 5000, -150, 5000, -2000));
                Check.Equal("no crossings", 0, CrossingPairs(plan));
                Check.Equal("no warnings", 0, plan.Warnings.Count);

                Check.Equal("same exit distance: left first", "W1", WireOf(plan, 0).Label);
                Check.Equal("then the right one", "W2", WireOf(plan, 1).Label);
                Check.Equal("then the later exit", "W3", WireOf(plan, 2).Label);
            });
        }

        private static void LShapedRoute()
        {
            Check.Case("an L-shaped route: mitred offsets, exits on both legs and at the corner", () =>
            {
                // From the thermostat (0,0) east to (5000,0), then north to (5000,4000): a left turn, so
                // the left side is inside the corner.
                ControlWirePlan plan = Plan(
                    Route(0, 0, 5000, 0, 5000, 4000),
                    Units(2000, 1500, 4000, 2800, 7000, 2000, 3000, -1200, 6500, -800));
                Check.True("ok", plan.Ok);

                ControlWire a = WireOf(plan, 0);
                ControlWire b = WireOf(plan, 1);
                ControlWire c = WireOf(plan, 2);
                ControlWire d = WireOf(plan, 3);
                ControlWire e = WireOf(plan, 4);

                Check.Equal("A (left, first leg) k", 2, a.K);
                Check.Equal("B (left, second leg) k", 1, b.K);
                Check.Equal("D (right, first leg) k", 3, d.K);
                Check.Equal("E (right, at the corner) k", 2, e.K);
                Check.Equal("C (right, second leg) k", 1, c.K);

                Check.True("A: " + Show(a), Is(a, 0, 300, 2000, 300, 2000, 1500));
                Check.True("B follows the mitred inside corner: " + Show(b),
                    Is(b, 0, 150, 4850, 150, 4850, 2800, 4000, 2800));
                Check.True("D: " + Show(d), Is(d, 0, -450, 3000, -450, 3000, -1200));
                Check.True("E leaves at the corner, two legs: " + Show(e),
                    Is(e, 0, -300, 5000, -300, 5000, -800, 6500, -800));
                Check.True("C follows the mitred outside corner: " + Show(c),
                    Is(c, 0, -150, 5150, -150, 5150, 2000, 7000, 2000));

                Check.Near("B leaves 7.8 m along the route", 7800, b.ExitDistance);
                Check.Near("E leaves at the corner, 5 m along", 5000, e.ExitDistance);
                Check.Equal("no crossing wires", 0, CrossingPairs(plan));
                Check.Equal("no crossing of the template", 0, TemplateCrossings(plan));
                Check.Equal("no warnings", 0, plan.Warnings.Count);

                // Each wire starts at the thermostat shifted by k * d along the first segment's normal.
                foreach (ControlWire wire in plan.Wires)
                {
                    Check.True("start of " + wire.Label + " is T0 + offset along the normal",
                        wire.Points[0].DistanceTo(V(0, wire.Offset)) < 1e-9);
                }
            });
        }

        // ------------------------------------------------------------------ exits

        private static void ExitBeyondTheEnds()
        {
            Check.Case("a terminal beyond the end of the route: the wire leaves at the end with two legs", () =>
            {
                ControlWirePlan plan = Plan(Route(0, 0, 4000, 0), Units(5000, 1000));
                Check.True("ok", plan.Ok);
                ControlWire wire = plan.Wires[0];
                Check.True("wire: " + Show(wire), Is(wire, 0, 150, 4000, 150, 4000, 1000, 5000, 1000));
                Check.Equal("exit vertex", 1, wire.ExitIndex);
                Check.Near("leaves at the end", 4000, wire.ExitDistance);
                Check.Equal("near enough: no warning", 0, plan.Warnings.Count);
            });

            Check.Case("a terminal far beyond the end is reported", () =>
            {
                ControlWirePlan plan = Plan(Route(0, 0, 4000, 0), Units(9000, 1000));
                Check.True("ok", plan.Ok);
                Check.True("wire: " + Show(plan.Wires[0]),
                    Is(plan.Wires[0], 0, 150, 4000, 150, 4000, 1000, 9000, 1000));
                Check.Equal("one far warning", 1, Warned(plan, "beyond the end of the template route"));
                Check.True("the warning is at the terminal", plan.Warnings[0].Point.DistanceTo(V(9000, 1000)) < 1e-9);
            });

            Check.Case("a terminal behind the start leaves at the start", () =>
            {
                ControlWirePlan plan = Plan(Route(0, 0, 4000, 0), Units(-1000, 800));
                Check.True("ok", plan.Ok);
                ControlWire wire = plan.Wires[0];
                Check.True("wire: " + Show(wire), Is(wire, 0, 150, 0, 800, -1000, 800));
                Check.Equal("exit vertex", 0, wire.ExitIndex);
                Check.Near("leaves at the thermostat", 0, wire.ExitDistance);
            });

            Check.Case("the exit is at the route point nearest the terminal, even on a slanted route", () =>
            {
                // Route at 45 degrees; the unit is straight out from the point 3000 along it.
                double s = Math.Sqrt(0.5);
                Vec2 along = V(3000 * s, 3000 * s);
                Vec2 normal = V(-s, s);
                Vec2 unit = along + normal * 2000;
                ControlWirePlan plan = Plan(Route(0, 0, 8000 * s, 8000 * s), Units(unit.X, unit.Y));
                Check.True("ok", plan.Ok);
                ControlWire wire = plan.Wires[0];
                Check.Near("leaves 3 m along", 3000, wire.ExitDistance, 1e-6);
                Check.True("exit point is the foot moved d out",
                    wire.Points[wire.ExitIndex].DistanceTo(along + normal * 150) < 1e-6);
                Check.True("starts at T0 plus d along the normal", wire.Points[0].DistanceTo(normal * 150) < 1e-6);
                Check.True("ends at the terminal", wire.Points[wire.Points.Count - 1].DistanceTo(unit) < 1e-9);
                Check.Equal("a straight exit: three vertices", 3, wire.Points.Count);
            });
        }

        private static void DuplicateRoutePoints()
        {
            Check.Case("repeated route points are dropped", () =>
            {
                ControlWirePlan plan = Plan(Route(0, 0, 0, 0, 5000, 0, 5000, 0), Units(2000, 1500));
                Check.True("ok", plan.Ok);
                Check.Equal("two route points", 2, plan.Route.Count);
                Check.True("wire: " + Show(plan.Wires[0]), Is(plan.Wires[0], 0, 150, 2000, 150, 2000, 1500));
            });
        }

        private static void TerminalOnTheRoute()
        {
            Check.Case("a terminal on the route goes to the side with fewer wires and is reported", () =>
            {
                ControlWirePlan plan = Plan(Route(0, 0, 10000, 0), Units(3000, 0));
                Check.True("ok", plan.Ok);
                Check.Equal("left", 1, plan.Wires[0].Side);
                Check.True("wire: " + Show(plan.Wires[0]), Is(plan.Wires[0], 0, 150, 3000, 150, 3000, 0));
                Check.Equal("the connection turns back", 1, Warned(plan, "closer to the route"));
            });

            Check.Case("a terminal on the route evens out the sides", () =>
            {
                ControlWirePlan plan = Plan(Route(0, 0, 10000, 0), Units(3000, 1500, 6000, 0));
                Check.True("ok", plan.Ok);
                Check.Equal("the one on the route goes to the empty right side", -1, WireOf(plan, 1).Side);
            });
        }

        // ------------------------------------------------------------------ warnings

        private static void OverlapIsReported()
        {
            Check.Case("two exits at the same point overlap and are reported", () =>
            {
                ControlWirePlan plan = Plan(Route(0, 0, 10000, 0), Units(3000, 1000, 3000, 2000));
                Check.True("ok", plan.Ok);
                Check.Equal("two wires", 2, plan.Wires.Count);
                Check.True("reported", Warned(plan, "cross or overlap") >= 1);
                bool inPlace = false;
                foreach (ControlWireWarning warning in plan.Warnings)
                {
                    if (Math.Abs(warning.Point.X - 3000) < 1e-6)
                        inPlace = true;
                }

                Check.True("the warning point is on the shared leg", inPlace);
            });

            Check.Case("an order that crosses is replaced by one that does not", () =>
            {
                // U0 is only 100 mm off the route, nearer than its offset: with the usual order (earliest
                // exit outermost, 2 d = 300 mm) its connection turns back across the wire of U1 at
                // 150 mm. Swapping the two avoids the crossing, so the planner keeps the swap.
                ControlWirePlan plan = Plan(Route(0, 0, 10000, 0), Units(2000, 100, 4000, 1500));
                Check.True("ok", plan.Ok);
                Check.Equal("no crossing wires in the result", 0, CrossingPairs(plan));
                Check.Equal("U0 took the inner place", 1, WireOf(plan, 0).K);
                Check.Equal("U1 took the outer place", 2, WireOf(plan, 1).K);
                Check.Equal("nothing cross or overlap is reported", 0, Warned(plan, "cross or overlap"));
                Check.Equal("the close unit is still flagged", 1, Warned(plan, "closer to the route"));
            });
        }

        private static void SharpCornerIsReported()
        {
            Check.Case("an offset that breaks down at a hairpin is reported", () =>
            {
                // A U-turn route 100 mm wide: the inside offset of 150 mm runs backwards on the short leg.
                ControlWirePlan plan = Plan(Route(0, 0, 1000, 0, 1000, 100, 0, 100), Units(200, 60));
                Check.True("ok", plan.Ok);
                Check.True("wire: " + Show(plan.Wires[0]),
                    Is(plan.Wires[0], 0, 150, 850, 150, 850, -50, 200, -50, 200, 60));
                Check.Equal("breaks down at the corner", 1, Warned(plan, "breaks down"));
                Check.True("the warning is at the corner",
                    plan.Warnings[0].Point.DistanceTo(V(1000, 100)) < 1e-9);
                Check.Equal("it also cuts the template", 1, Warned(plan, "crosses the template route"));
            });
        }

        // ------------------------------------------------------------------ cable text

        private static void CableTextPlacement()
        {
            Check.Case("cable text sits one text height beside the exit leg, readable", () =>
            {
                ControlWireInput input = new ControlWireInput
                {
                    Route = Route(0, 0, 10000, 0),
                    Units = Units(2000, 1500, 2000, -1500),
                    Spacing = 150,
                    TextHeight = 275
                };
                ControlWirePlan plan = ControlWirePlanner.Plan(input);
                Check.True("ok", plan.Ok);
                ControlWire up = WireOf(plan, 0);
                ControlWire down = WireOf(plan, 1);
                Check.Near("a leg running up reads at 90 degrees", Math.PI / 2, up.TextRotation, 1e-9);
                Check.True("beside the middle of the leg, on its left: " + up.TextPoint,
                    up.TextPoint.DistanceTo(V(2000 - 275, 825)) < 1e-6);
                Check.Near("a leg running down is turned to read upward too", Math.PI / 2, down.TextRotation, 1e-9);
                Check.True("down leg text: " + down.TextPoint,
                    down.TextPoint.DistanceTo(V(2000 - 275, -825)) < 1e-6);
            });

            Check.Case("cable text on a leg running toward minus X reads left to right", () =>
            {
                ControlWireInput input = new ControlWireInput
                {
                    Route = Route(0, 0, 0, 10000),
                    Units = Units(-1500, 3000),
                    TextHeight = 275
                };
                ControlWirePlan plan = ControlWirePlanner.Plan(input);
                Check.True("ok", plan.Ok);
                Check.Near("rotation", 0, plan.Wires[0].TextRotation, 1e-9);
                Check.True("text point: " + plan.Wires[0].TextPoint,
                    plan.Wires[0].TextPoint.DistanceTo(V(-825, 3275)) < 1e-6);
            });

            Check.Case("with two legs the text goes on the longer one", () =>
            {
                ControlWireInput input = new ControlWireInput
                {
                    Route = Route(0, 0, 4000, 0),
                    Units = Units(5000, 1000),
                    TextHeight = 275
                };
                ControlWirePlan plan = ControlWirePlanner.Plan(input);
                Check.True("ok", plan.Ok);
                Check.Near("horizontal leg", 0, plan.Wires[0].TextRotation, 1e-9);
                Check.True("text point: " + plan.Wires[0].TextPoint,
                    plan.Wires[0].TextPoint.DistanceTo(V(4500, 1275)) < 1e-6);
            });
        }

        private static void NoUnits()
        {
            Check.Case("no units gives an empty plan", () =>
            {
                ControlWirePlan plan = Plan(Route(0, 0, 4000, 0), new List<ControlWireUnit>());
                Check.True("ok", plan.Ok);
                Check.Equal("no wires", 0, plan.Wires.Count);
            });
        }

        // ------------------------------------------------------------------ many random layouts

        private static void SeparateStartsAndNoCrossingsStress()
        {
            Check.Case("random straight routes: separate starts, k by exit order, no crossings", () =>
            {
                Random random = new Random(20261120);
                int layoutsWithWires = 0;
                for (int trial = 0; trial < 200; trial++)
                {
                    double angle = random.NextDouble() * 2 * Math.PI;
                    Vec2 direction = Vec2.FromAngle(angle);
                    Vec2 normal = direction.Left;
                    Vec2 origin = V(random.NextDouble() * 1e6, random.NextDouble() * 1e6);
                    double length = 10000 + random.NextDouble() * 10000;
                    Vec2 end = origin + direction * length;

                    int count = 2 + random.Next(7);
                    List<ControlWireUnit> units = new List<ControlWireUnit>();
                    List<double> positions = new List<double>();
                    for (int i = 0; i < count; i++)
                    {
                        double along;
                        bool tooClose;
                        do
                        {
                            along = random.NextDouble() * length;
                            tooClose = false;
                            foreach (double other in positions)
                            {
                                if (Math.Abs(other - along) < 5)
                                    tooClose = true;
                            }
                        }
                        while (tooClose);

                        positions.Add(along);
                        double lateral = 1500 + random.NextDouble() * 2500;
                        if (random.Next(2) == 0)
                            lateral = -lateral;
                        Vec2 terminal = origin + direction * along + normal * lateral;
                        units.Add(new ControlWireUnit { Name = "U" + i, Terminal = terminal });
                    }

                    ControlWirePlan plan = Plan(Route(origin.X, origin.Y, end.X, end.Y), units);
                    Check.True("trial " + trial + " ok", plan.Ok);
                    if (!plan.Ok)
                        continue;
                    layoutsWithWires++;

                    Check.Equal("trial " + trial + " wires", count, plan.Wires.Count);
                    Check.Equal("trial " + trial + " warnings", 0, plan.Warnings.Count);
                    Check.Equal("trial " + trial + " crossing wires", 0, CrossingPairs(plan));
                    Check.Equal("trial " + trial + " template crossings", 0, TemplateCrossings(plan));

                    foreach (ControlWire wire in plan.Wires)
                    {
                        // The start is the thermostat point shifted k * d along the route's normal.
                        Check.True("trial " + trial + " " + wire.Label + " start",
                            wire.Points[0].DistanceTo(origin + normal * wire.Offset) < 1e-6);
                        Check.True("trial " + trial + " " + wire.Label + " ends on its unit",
                            wire.Points[wire.Points.Count - 1].DistanceTo(units[wire.UnitIndex].Terminal) < 1e-9);
                        Check.True("trial " + trial + " " + wire.Label + " has no third leg",
                            wire.Points.Count == 3);
                    }

                    // Per side: exiting earlier means a larger k; the k values are 1..n.
                    for (int side = -1; side <= 1; side += 2)
                    {
                        List<ControlWire> onSide = new List<ControlWire>();
                        foreach (ControlWire wire in plan.Wires)
                        {
                            if (wire.Side == side)
                                onSide.Add(wire);
                        }

                        bool[] used = new bool[onSide.Count + 1];
                        bool ordered = true;
                        foreach (ControlWire wire in onSide)
                        {
                            if (wire.K < 1 || wire.K > onSide.Count || used[wire.K])
                                ordered = false;
                            else
                                used[wire.K] = true;
                            foreach (ControlWire other in onSide)
                            {
                                if (wire.ExitDistance < other.ExitDistance && wire.K <= other.K)
                                    ordered = false;
                            }
                        }

                        Check.True("trial " + trial + " side " + side + " ordering", ordered);
                    }
                }

                Check.Equal("every layout was planned", 200, layoutsWithWires);
            });
        }
    }
}
