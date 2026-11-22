using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Pipes;
using HvacAiCadAddin.Routes;
using PipeCoreTests;

namespace RouteCoreTests
{
    internal static class RouteTests
    {
        private const double Stub = 1500;

        public static void Run()
        {
            Check.Case("single unit", SingleUnit);
            Check.Case("two units on a line", TwoUnitsOnALine);
            Check.Case("units on both sides", BothSides);
            Check.Case("target in the middle", TargetInTheMiddle);
            Check.Case("duplicate unit points", Duplicates);
            Check.Case("aligned with target", Aligned);
            Check.Case("unit on the target", UnitOnTarget);
            Check.Case("large coordinates", LargeCoordinates);
            Check.Case("forced modes and axes", ForcedModes);
            Check.Case("random invariants", RandomTrees);
            Check.Case("no units", NoUnits);
            Check.Case("settings", Settings);
            Check.Case("shaft helpers", ShaftHelpers);
            Check.Case("cleaner accepts drafts", CleanerAccepts);
            Check.Case("port transform", PortTransform);
            Check.Case("port rules", PortRules);
            Check.Case("corridor names", CorridorNames);
            Check.Case("corridor settings", CorridorSettings);
            Check.Case("corridor U scene", CorridorUScene);
            Check.Case("corridor without rooms falls back", CorridorFallback);
            Check.Case("corridor random scenes", CorridorRandom);
            Check.Case("corridor stub and ports", CorridorStubAndPorts);
            Check.Case("corridor large area coarsens", CorridorCoarsens);
            Check.Case("corridor performance", CorridorPerformance);
            Check.Case("corridor cleaner acceptance", CorridorCleaner);
        }

        private static Vec2 V(double x, double y)
        {
            return new Vec2(x, y);
        }

        private static RouteDraftResult Draft(Vec2 target, IList<Vec2> units, string mode = "best", string axis = "auto")
        {
            return RouteDraftCore.Build(target, units, new RouteDraftOptions { Mode = mode, TrunkAxis = axis, MinStub = Stub });
        }

        private static string Key(Vec2 p)
        {
            return (p.X + 0.0).ToString("R", CultureInfo.InvariantCulture) + "," + (p.Y + 0.0).ToString("R", CultureInfo.InvariantCulture);
        }

        private static double Manhattan(Vec2 a, Vec2 b)
        {
            return Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
        }

        /// <summary>
        /// The invariants of every draft: axis aligned, no duplicate or zero-length pieces, one tree, the
        /// unit points exact nodes (leaves unless the result admits otherwise), the target a node, lengths
        /// consistent, and the total no longer than the individual Manhattan paths plus the stub slack.
        /// </summary>
        private static void Invariants(string name, Vec2 target, IList<Vec2> units, RouteDraftResult r, double slack = 2 * Stub)
        {
            Dictionary<string, int> degree = new Dictionary<string, int>();
            Dictionary<string, Vec2> point = new Dictionary<string, Vec2>();
            HashSet<string> edges = new HashSet<string>();
            double length = 0;
            bool axisAligned = true, noZero = true, noDuplicate = true;
            foreach (List<Vec2> line in r.Polylines)
            {
                Check.True(name + ": polyline has >= 2 vertices", line.Count >= 2);
                for (int i = 0; i + 1 < line.Count; i++)
                {
                    Vec2 a = line[i], b = line[i + 1];
                    if (a.X != b.X && a.Y != b.Y)
                        axisAligned = false;
                    if (a.X == b.X && a.Y == b.Y)
                        noZero = false;
                    string ka = Key(a), kb = Key(b);
                    string edge = string.CompareOrdinal(ka, kb) < 0 ? ka + ">" + kb : kb + ">" + ka;
                    if (!edges.Add(edge))
                        noDuplicate = false;
                    length += a.DistanceTo(b);
                    degree[ka] = (degree.ContainsKey(ka) ? degree[ka] : 0) + 1;
                    degree[kb] = (degree.ContainsKey(kb) ? degree[kb] : 0) + 1;
                    point[ka] = a;
                    point[kb] = b;
                }
            }

            Check.True(name + ": segments axis aligned", axisAligned);
            Check.True(name + ": no zero-length pieces", noZero);
            Check.True(name + ": no duplicate pieces", noDuplicate);
            Check.Near(name + ": total length", length, r.TotalLength, 1e-6 * Math.Max(1, length) + 1e-6);

            bool any = false;
            foreach (Vec2 u in units)
            {
                if (Manhattan(u, target) > 1e-6)
                    any = true;
            }

            if (!any)
            {
                Check.Equal(name + ": no polylines", 0, r.Polylines.Count);
                return;
            }

            // One tree: edges = nodes - 1 and connected (walk from the target through the pieces).
            Check.Equal(name + ": edges = nodes - 1", point.Count - 1, edges.Count);
            Dictionary<string, List<string>> adjacent = new Dictionary<string, List<string>>();
            foreach (string e in edges)
            {
                string[] parts = e.Split('>');
                if (!adjacent.ContainsKey(parts[0]))
                    adjacent[parts[0]] = new List<string>();
                if (!adjacent.ContainsKey(parts[1]))
                    adjacent[parts[1]] = new List<string>();
                adjacent[parts[0]].Add(parts[1]);
                adjacent[parts[1]].Add(parts[0]);
            }

            Check.True(name + ": target is a node", point.ContainsKey(Key(target)));
            HashSet<string> seen = new HashSet<string> { Key(target) };
            Stack<string> stack = new Stack<string>();
            stack.Push(Key(target));
            while (stack.Count > 0)
            {
                string n = stack.Pop();
                List<string> next;
                if (!adjacent.TryGetValue(n, out next))
                    continue;
                foreach (string m in next)
                {
                    if (seen.Add(m))
                        stack.Push(m);
                }
            }

            Check.Equal(name + ": connected", point.Count, seen.Count);

            if (r.RootIsLeaf)
                Check.Equal(name + ": target is a leaf", 1, degree[Key(target)]);

            int nonLeaf = 0;
            double sumManhattan = 0;
            for (int i = 0; i < units.Count; i++)
            {
                Vec2 u = units[i];
                if (Manhattan(u, target) <= 1e-6)
                    continue;
                Check.True(name + ": unit " + i + " is an exact node", point.ContainsKey(Key(u)));
                if (point.ContainsKey(Key(u)) && degree[Key(u)] != 1)
                    nonLeaf++;
                sumManhattan += Manhattan(u, target);
                Check.True(name + ": unit " + i + " path >= Manhattan",
                    r.UnitPathLengths[i] >= Manhattan(u, target) - 1e-6,
                    r.UnitPathLengths[i].ToString("R", CultureInfo.InvariantCulture));
            }

            Check.True(name + ": every unit is a leaf", nonLeaf == 0 || r.NonLeafUnits > 0);
            Check.True(name + ": total <= sum of Manhattan paths + slack",
                r.TotalLength <= sumManhattan + slack + 1e-6,
                r.TotalLength.ToString("R", CultureInfo.InvariantCulture) + " > " + sumManhattan.ToString("R", CultureInfo.InvariantCulture));
        }

        private static List<Vec2> L(params double[] xy)
        {
            List<Vec2> list = new List<Vec2>();
            for (int i = 0; i + 1 < xy.Length; i += 2)
                list.Add(V(xy[i], xy[i + 1]));
            return list;
        }

        private static void SingleUnit()
        {
            Vec2 t = V(0, 0);
            List<Vec2> units = L(3000, 2000);
            RouteDraftResult r = Draft(t, units);
            Invariants("one", t, units, r);
            Check.Near("one: total is the Manhattan distance", 5000, r.TotalLength);
            Check.Near("one: unit path", 5000, r.UnitPathLengths[0]);
            Check.True("one: root leaf", r.RootIsLeaf);
            Check.Equal("one: no non-leaf units", 0, r.NonLeafUnits);
            Check.Equal("one: no warnings", 0, r.Warnings.Count);
        }

        private static void TwoUnitsOnALine()
        {
            Vec2 t = V(0, 0);
            List<Vec2> units = L(4000, 0, 8000, 0);
            RouteDraftResult r = Draft(t, units);
            Invariants("line2", t, units, r);
            Check.Equal("line2: both units are pipe ends", 0, r.NonLeafUnits);
            Check.Near("line2: near unit path", 4000 + 2 * Stub, r.UnitPathLengths[0], 4000 + 2 * Stub);
            Check.True("line2: far unit path at least 8000", r.UnitPathLengths[1] >= 8000 - 1e-6);

            // Two units stacked in a column with a horizontal-only trunk still work (units become pipe ends
            // by moving the trunk aside).
            List<Vec2> column = L(0, 3000, 0, 6000);
            RouteDraftResult c = Draft(t, column);
            Invariants("column", t, column, c);
        }

        private static void BothSides()
        {
            Vec2 t = V(0, 0);
            List<Vec2> units = L(3000, 2000, 6000, -1500, 9000, 2500, 12000, -3000, 5000, 3000);
            RouteDraftResult r = Draft(t, units);
            Invariants("both", t, units, r);
            Check.Equal("both: no non-leaf units", 0, r.NonLeafUnits);
            Check.True("both: shorter than the star of Manhattan paths", r.TotalLength < 3000 + 2000 + 6000 + 1500 + 9000 + 2500 + 12000 + 3000 + 5000 + 3000);
            Check.True("both: polylines are whole pieces", r.Polylines.Count >= units.Count);
        }

        private static void TargetInTheMiddle()
        {
            Vec2 t = V(5000, 0);
            List<Vec2> units = L(0, 2000, 1000, -2000, 9000, 1500, 10000, -2500, 5000, 4000);
            RouteDraftResult r = Draft(t, units);
            Invariants("middle", t, units, r);
            Check.True("middle: target is at a pipe end", r.RootIsLeaf);
        }

        private static void Duplicates()
        {
            Vec2 t = V(0, 0);
            List<Vec2> units = L(5000, 3000, 5000, 3000, 9000, -2000, 5000, 3000);
            RouteDraftResult r = Draft(t, units);
            Invariants("dup", t, units, r);
            Check.Near("dup: same unit, same path 0-1", r.UnitPathLengths[0], r.UnitPathLengths[1]);
            Check.Near("dup: same unit, same path 0-3", r.UnitPathLengths[0], r.UnitPathLengths[3]);

            RouteDraftResult single = Draft(t, L(5000, 3000, 9000, -2000));
            Check.Near("dup: the same tree as without duplicates", single.TotalLength, r.TotalLength);
        }

        private static void Aligned()
        {
            Vec2 t = V(0, 0);
            List<Vec2> units = L(0, 5000);
            RouteDraftResult r = Draft(t, units);
            Invariants("aligned", t, units, r);
            Check.Equal("aligned: one polyline", 1, r.Polylines.Count);
            Check.Equal("aligned: two vertices", 2, r.Polylines[0].Count);
            Check.Near("aligned: straight", 5000, r.TotalLength);

            // Units aligned with the target in x and y at once.
            List<Vec2> both = L(0, 5000, 7000, 0);
            RouteDraftResult b = Draft(t, both);
            Invariants("aligned2", t, both, b);
        }

        private static void UnitOnTarget()
        {
            Vec2 t = V(100, 100);
            List<Vec2> units = L(100, 100, 4100, 100);
            RouteDraftResult r = Draft(t, units);
            Invariants("on target", t, units, r);
            Check.Near("on target: path 0", 0, r.UnitPathLengths[0]);
            Check.Equal("on target: a warning", 1, r.Warnings.Count);

            List<Vec2> only = L(100, 100);
            RouteDraftResult n = Draft(t, only);
            Check.Equal("only on target: no polylines", 0, n.Polylines.Count);
            Check.Near("only on target: length 0", 0, n.TotalLength);
        }

        private static void LargeCoordinates()
        {
            Vec2 t = V(4000000.123, 4100000.456);
            List<Vec2> units = L(
                4003500.5, 4101200.25,
                4007200.75, 4098800.125,
                4010100.0, 4102500.5,
                4004800.375, 4103900.625);
            RouteDraftResult r = Draft(t, units);
            Invariants("large", t, units, r, 2 * Stub + 1e-3);
            Check.Equal("large: no non-leaf units", 0, r.NonLeafUnits);
            foreach (RouteDraftResult m in new[] { Draft(t, units, "mst"), Draft(t, units, "trunk", "vertical") })
                Invariants("large " + m.ModeUsed, t, units, m, 2 * Stub + 1e-3);
        }

        private static void ForcedModes()
        {
            Vec2 t = V(0, 0);
            List<Vec2> units = L(4000, 3000, 8000, -2000, 12000, 2500);
            RouteDraftResult h = Draft(t, units, "trunk", "horizontal");
            RouteDraftResult v = Draft(t, units, "trunk", "vertical");
            RouteDraftResult m = Draft(t, units, "mst");
            RouteDraftResult best = Draft(t, units);
            Check.Equal("mode name horizontal", "trunk horizontal", h.ModeUsed);
            Check.Equal("mode name vertical", "trunk vertical", v.ModeUsed);
            Check.Equal("mode name mst", "mst", m.ModeUsed);
            Invariants("forced h", t, units, h);
            Invariants("forced v", t, units, v);
            Invariants("forced mst", t, units, m);
            Invariants("best", t, units, best);
            Check.True("best is not longer than any forced mode",
                best.TotalLength <= Math.Min(h.TotalLength, Math.Min(v.TotalLength, m.TotalLength)) + 1e-6);

            // The horizontal trunk of a wide, flat layout has horizontal polylines longer than vertical ones.
            bool horizontalTrunk = false;
            foreach (List<Vec2> line in h.Polylines)
            {
                if (line.Count == 2 && line[0].Y == line[1].Y && Math.Abs(line[0].X - line[1].X) > 3000)
                    horizontalTrunk = true;
            }

            Check.True("forced horizontal has a long horizontal piece", horizontalTrunk);
        }

        private static void RandomTrees()
        {
            Random random = new Random(20260721);
            for (int trial = 0; trial < 120; trial++)
            {
                int n = 1 + random.Next(12);
                Vec2 t = V(random.Next(-20, 20) * 500, random.Next(-20, 20) * 500);
                List<Vec2> units = new List<Vec2>();
                for (int i = 0; i < n; i++)
                    units.Add(V(random.Next(-30, 30) * 250, random.Next(-30, 30) * 250));
                RouteDraftResult r = Draft(t, units);
                Invariants("random " + trial, t, units, r);
                RouteDraftResult m = Draft(t, units, "mst");
                Invariants("random mst " + trial, t, units, m);
                RouteDraftResult tr = Draft(t, units, "trunk");
                Invariants("random trunk " + trial, t, units, tr);
            }
        }

        private static void NoUnits()
        {
            RouteDraftResult r = Draft(V(0, 0), new List<Vec2>());
            Check.Equal("no units: nothing drawn", 0, r.Polylines.Count);
            Check.Equal("no units: mode none", "none", r.ModeUsed);
        }

        private static void Settings()
        {
            RouteDraftSettings s = new RouteDraftSettings();
            Check.True("defaults: refrigerant", s.DrawRefrig);
            Check.True("defaults: drain", s.DrawDrain);
            Check.False("defaults: power off", s.DrawPower);
            Check.Equal("defaults: mode", "corridor", s.Mode);
            Check.Equal("defaults: axis", "auto", s.TrunkAxis);

            s.DrawRefrig = false;
            s.DrawPower = true;
            s.Mode = "mst";
            s.TrunkAxis = "vertical";
            s.MinStub = 750.5;
            s.SeparateDrainTarget = true;
            RouteDraftSettings back = RouteDraftSettings.Deserialize(s.Serialize());
            Check.False("round trip: refrigerant", back.DrawRefrig);
            Check.True("round trip: power", back.DrawPower);
            Check.Equal("round trip: mode", "mst", back.Mode);
            Check.Equal("round trip: axis", "vertical", back.TrunkAxis);
            Check.Near("round trip: stub", 750.5, back.MinStub);
            Check.True("round trip: drain target", back.SeparateDrainTarget);
            Check.False("round trip: power target", back.SeparatePowerTarget);

            RouteDraftSettings bad = RouteDraftSettings.Deserialize("mode=zig\naxis=diagonal\nminstub=-5\nminstub=abc\nrubbish\n=x\r\nrefrig=1\r\n");
            Check.Equal("bad input keeps mode", "corridor", bad.Mode);
            Check.Equal("bad input keeps axis", "auto", bad.TrunkAxis);
            Check.Near("bad input keeps stub", 1500, bad.MinStub);
            Check.True("never throws on empty", RouteDraftSettings.Deserialize(null).DrawRefrig);

            string error;
            Check.True("valid defaults", new RouteDraftSettings().IsValid(out error));
            RouteDraftSettings none = new RouteDraftSettings { DrawRefrig = false, DrawDrain = false };
            Check.False("no draft is invalid", none.IsValid(out error));
            Check.Contains("no draft message", error, "at least one");
        }

        private static void ShaftHelpers()
        {
            List<Vec2> square = L(4000000, 4000000, 4001000, 4000000, 4001000, 4002000, 4000000, 4002000);
            Vec2 c = RouteDraftCore.PolygonCentre(square);
            Check.Near("centre x", 4000500, c.X, 1e-6);
            Check.Near("centre y", 4001000, c.Y, 1e-6);

            // A clockwise L shape keeps a sensible centroid (sign of the area cancels).
            List<Vec2> ell = L(0, 0, 0, 2000, 1000, 2000, 1000, 1000, 2000, 1000, 2000, 0);
            Vec2 e = RouteDraftCore.PolygonCentre(ell);
            Check.Near("L centre x", 2500.0 / 3 + 0, e.X, 1e-6);

            Vec2 line = RouteDraftCore.PolygonCentre(L(0, 0, 100, 0));
            Check.Near("degenerate polygon falls back to the box centre", 50, line.X);

            List<Vec2> centres = L(0, 0, 10000, 0, 5000, 5000);
            Check.Equal("nearest centre", 2, RouteDraftCore.NearestIndex(V(5200, 4000), centres));
            Check.Equal("nearest of none", -1, RouteDraftCore.NearestIndex(V(0, 0), new List<Vec2>()));

            Vec2 centroid = RouteDraftCore.Centroid(L(0, 0, 4000, 0, 4000, 2000, 0, 2000));
            Check.Near("centroid x", 2000, centroid.X);
            Check.Near("centroid y", 1000, centroid.Y);
        }

        // ------------------------------------------------------------------ the HVACREFRIG cleaner

        private static RefrigResult Clean(Vec2 root, RouteDraftResult draft)
        {
            RefrigInput input = new RefrigInput { RootPoint = root };
            int id = 0;
            foreach (List<Vec2> line in draft.Polylines)
            {
                PipeCurve curve = new PipeCurve { Id = id++, Layer = RouteDraftSettings.RefrigLayer };
                foreach (Vec2 p in line)
                    curve.Vertices.Add(new OutlineVertex(p.X, p.Y, 0));
                input.Curves.Add(curve);
            }

            return RefrigCleaner.Run(input);
        }

        private static void CleanerAccepts()
        {
            Vec2 t = V(0, 0);
            List<List<Vec2>> scenes = new List<List<Vec2>>
            {
                L(3000, 2000),
                L(4000, 0, 8000, 0),
                L(3000, 2000, 6000, -1500, 9000, 2500, 12000, -3000, 5000, 3000),
                L(0, 5000, 7000, 0, -4000, 3000)
            };
            int scene = 0;
            foreach (List<Vec2> units in scenes)
            {
                foreach (string mode in new[] { "trunk", "mst", "best" })
                {
                    string name = "cleaner " + scene + " " + mode;
                    RouteDraftResult draft = Draft(t, units, mode);
                    RefrigResult result = Clean(t, draft);
                    Check.True(name + ": cleaner ok", result.Ok, result.Error);
                    Check.True(name + ": root found", result.HasRoot);
                    Check.Near(name + ": root at the target", 0, Manhattan(result.Root, t), 1.0);
                    Check.Equal(name + ": one network", 1, result.Components);
                    foreach (string report in result.Reports)
                        Check.False(name + ": no degree-4 junction", report.Contains("Degree-4"), report);

                    // Every unit is an end of a cleaned path.
                    int ends = 0;
                    foreach (Vec2 u in units)
                    {
                        bool isEnd = false;
                        foreach (RefrigPath path in result.Paths)
                        {
                            if (Manhattan(path.Points[0], u) < 1.0 || Manhattan(path.Points[path.Points.Count - 1], u) < 1.0)
                                isEnd = true;
                        }

                        if (isEnd)
                            ends++;
                    }

                    if (draft.NonLeafUnits == 0)
                        Check.Equal(name + ": every unit is a pipe end", units.Count, ends);

                    NetworkTopology topology = NetworkTopologyBuilder.Build(new TopologyInput
                    {
                        Paths = result.Paths,
                        PlannedRefnets = result.Refnets,
                        Root = result.Root,
                        Tolerance = 50
                    });
                    Check.True(name + ": topology ok", topology.Ok, topology.Error);
                    if (draft.NonLeafUnits == 0)
                        Check.Equal(name + ": topology ends = units", units.Count, topology.Ends.Count);
                }

                scene++;
            }
        }

        // ------------------------------------------------------------------ ports

        private static void PortTransform()
        {
            Vec2 p, o;
            RoutePorts.Resolve(V(1000, 2000), 0, 1, 1, 475, -300, "+X", out p, out o);
            Check.Near("plain x", 1475, p.X, 1e-9);
            Check.Near("plain y", 1700, p.Y, 1e-9);
            Check.Near("plain out x", 1, o.X);
            Check.Near("plain out y", 0, o.Y);

            RoutePorts.Resolve(V(1000, 2000), Math.PI / 2, 1, 1, 475, -300, "+X", out p, out o);
            Check.Near("rot 90 x", 1300, p.X, 1e-9);
            Check.Near("rot 90 y", 2475, p.Y, 1e-9);
            Check.Near("rot 90 out x", 0, o.X);
            Check.Near("rot 90 out y", 1, o.Y);

            RoutePorts.Resolve(V(1000, 2000), Math.PI, 1, 1, 475, -300, "+X", out p, out o);
            Check.Near("rot 180 x", 525, p.X, 1e-9);
            Check.Near("rot 180 y", 2300, p.Y, 1e-9);
            Check.Near("rot 180 out x", -1, o.X);

            // Mirrored in X: the port swaps to the other side and the pipe leaves the other way.
            RoutePorts.Resolve(V(1000, 2000), 0, -1, 1, 475, -300, "+X", out p, out o);
            Check.Near("mirror x", 525, p.X, 1e-9);
            Check.Near("mirror y", 1700, p.Y, 1e-9);
            Check.Near("mirror out x", -1, o.X);

            // Mirrored in Y keeps +X, flips the vertical offset.
            RoutePorts.Resolve(V(1000, 2000), 0, 1, -1, 475, -300, "+Y", out p, out o);
            Check.Near("mirror y: y", 2300, p.Y, 1e-9);
            Check.Near("mirror y: out", -1, o.Y);

            // Scale 2 and rotation 90 together: scale first, then rotate.
            RoutePorts.Resolve(V(0, 0), Math.PI / 2, 2, 2, 475, -300, "-Y", out p, out o);
            Check.Near("scaled rot x", 600, p.X, 1e-9);
            Check.Near("scaled rot y", 950, p.Y, 1e-9);
            Check.Near("scaled rot out x", 1, o.X);
            Check.Near("scaled rot out y", 0, o.Y);

            // Large coordinates and an arbitrary angle keep the offset length.
            RoutePorts.Resolve(V(4000000, 4100000), 0.3, 1, 1, 475, -300, "", out p, out o);
            Check.Near("length kept", Math.Sqrt(475.0 * 475 + 300.0 * 300), p.DistanceTo(V(4000000, 4100000)), 1e-6);
            Check.Near("no direction", 0, o.Length);
        }

        private static void PortRules()
        {
            List<PortRule> rules = RoutePorts.DefaultRules();
            Check.True("defaults match FCU-CAS-5.6 kW", RoutePorts.Find(rules, "FCU-CAS-5.6 kW") != null);
            Check.True("matching ignores case", RoutePorts.Find(rules, "fcu-cas-16 kw") != null);
            Check.True("IU-CAS matches", RoutePorts.Find(rules, "IU-CAS 14.0 kW") != null);
            Check.True("other blocks do not match", RoutePorts.Find(rules, "Water Cassette Unit_6.2 kW") == null);

            rules.Add(new PortRule { Pattern = "FCU-CAS-16", RefrigX = 1, RefrigY = 2, DrainX = 3, DrainY = 4 });
            Check.Equal("longest pattern wins", "FCU-CAS-16", RoutePorts.Find(rules, "FCU-CAS-16 kW").Pattern);

            PortRule r = rules[0];
            PortRule back = PortRule.Parse(r.Format());
            Check.True("rule round trip parses", back != null);
            Check.Equal("rule round trip pattern", r.Pattern, back.Pattern);
            Check.Near("rule round trip refrig y", r.RefrigY, back.RefrigY);
            Check.Equal("rule round trip drain dir", r.DrainDir, back.DrainDir);
            Check.True("bad rule: too few fields", PortRule.Parse("a|1|2") == null);
            Check.True("bad rule: not a number", PortRule.Parse("a|x|2|+X|3|4|+X") == null);
            Check.True("bad rule: direction", PortRule.Parse("a|1|2|up|3|4|+X") == null);
            Check.True("empty direction allowed", PortRule.Parse("a|1|2||3|4|") != null);
        }

        private static void CorridorNames()
        {
            Check.True("hanh lang", RouteRoom.IsCorridor("Hành lang", ""));
            Check.True("HANH LANG capitals", RouteRoom.IsCorridor("HÀNH LANG T1", ""));
            Check.True("sanh", RouteRoom.IsCorridor("Sảnh thang máy", ""));
            Check.True("SANH capital", RouteRoom.IsCorridor("SẢNH", ""));
            Check.True("lobby", RouteRoom.IsCorridor("Main Lobby", ""));
            Check.True("corridor", RouteRoom.IsCorridor("corridor 2", ""));
            Check.True("hall", RouteRoom.IsCorridor("Hall", ""));
            Check.True("type lobby", RouteRoom.IsCorridor("P.101", "lobby"));
            Check.True("type lobby case", RouteRoom.IsCorridor("", "Lobby"));
            Check.False("office", RouteRoom.IsCorridor("Phòng họp", "office"));
            Check.False("wall is not hall", RouteRoom.IsCorridor("Marshall room", ""));
            Check.False("empty", RouteRoom.IsCorridor(null, null));
        }

        private static void CorridorSettings()
        {
            RouteDraftSettings s = new RouteDraftSettings();
            Check.Equal("default port mode", "port", s.PortMode);
            Check.Near("default cell", 200, s.CellSize);
            Check.Near("default room penalty", 5, s.RoomPenalty);
            Check.Near("default outside penalty", 3, s.OutsidePenalty);
            Check.Near("default boundary penalty", 2, s.BoundaryPenalty);
            Check.Near("default bend penalty", 2, s.BendPenalty);
            Check.True("default port rules", s.PortRules.Count >= 1 && s.PortRules[0].Pattern == "FCU-CAS");

            s.PortMode = "insertion";
            s.Mode = "corridor";
            s.CellSize = 150.5;
            s.RoomPenalty = 7;
            s.OutsidePenalty = 4;
            s.BoundaryPenalty = 3;
            s.BendPenalty = 1.5;
            s.PortRules = new List<PortRule>
            {
                new PortRule { Pattern = "FCU-CAS", RefrigX = 100.5, RefrigY = -20, RefrigDir = "-X", DrainX = 7, DrainY = 8, DrainDir = "" },
                new PortRule { Pattern = "Odd name", RefrigX = 1, RefrigY = 2, RefrigDir = "+Y", DrainX = 3, DrainY = 4, DrainDir = "-Y" }
            };
            RouteDraftSettings back = RouteDraftSettings.Deserialize(s.Serialize());
            Check.Equal("round trip port mode", "insertion", back.PortMode);
            Check.Near("round trip cell", 150.5, back.CellSize);
            Check.Near("round trip room penalty", 7, back.RoomPenalty);
            Check.Near("round trip outside penalty", 4, back.OutsidePenalty);
            Check.Near("round trip boundary penalty", 3, back.BoundaryPenalty);
            Check.Near("round trip bend penalty", 1.5, back.BendPenalty);
            Check.Equal("round trip rule count", 2, back.PortRules.Count);
            Check.Near("round trip rule x", 100.5, back.PortRules[0].RefrigX);
            Check.Equal("round trip rule dir", "-X", back.PortRules[0].RefrigDir);
            Check.Equal("round trip empty dir", "", back.PortRules[0].DrainDir);
            Check.Equal("round trip second pattern", "Odd name", back.PortRules[1].Pattern);

            s.PortRules = new List<PortRule>();
            RouteDraftSettings none = RouteDraftSettings.Deserialize(s.Serialize());
            Check.Equal("an empty rule list stays empty", 0, none.PortRules.Count);

            RouteDraftSettings old = RouteDraftSettings.Deserialize("mode=best\nrefrig=1\n");
            Check.Equal("an old file keeps its mode", "best", old.Mode);
            Check.True("an old file gets the default ports", old.PortRules.Count >= 1);

            RouteDraftSettings bad = RouteDraftSettings.Deserialize("cell=5\nroompenalty=0\nbendpenalty=-1\nportmode=x\nport=a|b\n");
            Check.Near("bad cell keeps default", 200, bad.CellSize);
            Check.Near("bad room penalty keeps default", 5, bad.RoomPenalty);
            Check.Near("bad bend keeps default", 2, bad.BendPenalty);
            Check.Equal("bad port mode keeps default", "port", bad.PortMode);

            string error;
            Check.True("valid defaults", new RouteDraftSettings().IsValid(out error));
            RouteDraftSettings badCell = new RouteDraftSettings { CellSize = 5 };
            Check.False("tiny cell invalid", badCell.IsValid(out error));
            RouteDraftSettings badRule = new RouteDraftSettings();
            badRule.PortRules.Add(new PortRule { Pattern = "" });
            Check.False("empty pattern invalid", badRule.IsValid(out error));
        }

        // ------------------------------------------------------------------ corridor mode

        private static RouteRoom Rect(double x0, double y0, double x1, double y1, bool corridor = false, string name = "")
        {
            return new RouteRoom
            {
                Name = name,
                Corridor = corridor,
                Ring = L(x0, y0, x1, y0, x1, y1, x0, y1)
            };
        }

        private static RouteDraftResult Corridor(Vec2 target, IList<Vec2> units, IList<Vec2> outs, IList<RouteRoom> rooms,
            string mode = "corridor", double stub = Stub, double cell = 200)
        {
            return RouteDraftCore.Build(target, units, new RouteDraftOptions
            {
                Mode = mode,
                MinStub = stub,
                Rooms = rooms,
                UnitOut = outs,
                CellSize = cell
            });
        }

        private static void Dump(string name, RouteDraftResult r)
        {
            if (Environment.GetEnvironmentVariable("ROUTE_DEBUG") != "1" || Check.Scope != "en-US")
                return;
            Console.WriteLine("   dump " + name + " mode " + r.ModeUsed + " total " + r.TotalLength);
            foreach (List<Vec2> line in r.Polylines)
                Console.WriteLine("     " + string.Join(" ", line.ConvertAll(v => v.ToString())));
        }

        private static bool InCorridor(IList<RouteRoom> rooms, Vec2 p)
        {
            foreach (RouteRoom r in rooms)
            {
                if (r.Corridor && r.Contains(p))
                    return true;
            }

            return false;
        }

        private static bool InAnyRoom(IList<RouteRoom> rooms, Vec2 p)
        {
            foreach (RouteRoom r in rooms)
            {
                if (r.Contains(p))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// A U-shaped corridor (left leg, bottom, right leg) around three rooms that open to the bottom
        /// part of it; one unit per room near the room's lower edge, pipes leaving downward.
        /// </summary>
        private static List<RouteRoom> UScene()
        {
            return new List<RouteRoom>
            {
                Rect(0, 0, 2000, 16000, true, "Hanh lang"),
                Rect(0, 0, 30000, 2000, true, "Hanh lang"),
                Rect(28000, 0, 30000, 16000, true, "Hanh lang"),
                Rect(2000, 2000, 10000, 16000, false, "P1"),
                Rect(10000, 2000, 20000, 16000, false, "P2"),
                Rect(20000, 2000, 28000, 16000, false, "P3")
            };
        }

        private static void CorridorUScene()
        {
            List<RouteRoom> rooms = UScene();
            Vec2 t = V(1000, 15000);
            List<Vec2> units = L(6000, 4500, 15000, 4500, 24000, 4500);
            List<Vec2> outs = L(0, -1, 0, -1, 0, -1);

            RouteDraftResult r = Corridor(t, units, outs, rooms);
            Dump("U", r);
            Invariants("U", t, units, r, 1e12);
            Check.Equal("U: mode", "corridor", r.ModeUsed);
            Check.True("U: root is a leaf", r.RootIsLeaf);
            Check.Equal("U: every unit is a pipe end", 0, r.NonLeafUnits);
            Check.Equal("U: no four-way junctions", 0, r.CrossJunctions);
            Check.Near("U: cell size", 200, r.CellSizeUsed);

            // Every piece that does not end at a unit runs inside the corridor; the pieces that end at a unit
            // start in the corridor and only they enter the rooms.
            foreach (List<Vec2> line in r.Polylines)
            {
                bool endsAtUnit = false;
                foreach (Vec2 u in units)
                {
                    if (Manhattan(line[0], u) < 1e-6 || Manhattan(line[line.Count - 1], u) < 1e-6)
                        endsAtUnit = true;
                }

                if (!endsAtUnit)
                {
                    for (int i = 0; i + 1 < line.Count; i++)
                    {
                        for (int k = 0; k <= 10; k++)
                        {
                            Vec2 q = line[i] + (line[i + 1] - line[i]) * (k / 10.0);
                            Check.True("U: trunk sample in the corridor " + q, InCorridor(rooms, q));
                        }
                    }
                }
                else
                {
                    bool startsInCorridor = InCorridor(rooms, line[0]) || InCorridor(rooms, line[line.Count - 1]);
                    Check.True("U: unit branch starts in the corridor", startsInCorridor);
                    double inRooms = 0;
                    for (int i = 0; i + 1 < line.Count; i++)
                    {
                        int steps = 40;
                        for (int k = 0; k < steps; k++)
                        {
                            Vec2 q = line[i] + (line[i + 1] - line[i]) * ((k + 0.5) / steps);
                            if (InAnyRoom(rooms, q) && !InCorridor(rooms, q))
                                inRooms += line[i].DistanceTo(line[i + 1]) / steps;
                        }
                    }

                    Check.True("U: the branch into a room is short (" + inRooms.ToString("0") + ")", inRooms <= 4500);
                }
            }

            // Cheaper than the wall-blind modes under the same cost model, and the trunk is not a straight cut.
            RouteDraftResult straight = Corridor(t, units, outs, rooms, "best");
            RouteDraftResult mst = Corridor(t, units, outs, rooms, "mst");
            Check.True("U: wall-blind result has a cost", straight.RouteCost > 0);
            Check.True("U: corridor cost " + r.RouteCost.ToString("0") + " < best " + straight.RouteCost.ToString("0"),
                r.RouteCost < straight.RouteCost);
            Check.True("U: corridor cost < mst", r.RouteCost < mst.RouteCost);
            Check.True("U: the corridor tree is longer than the shortcut", r.TotalLength > straight.TotalLength);

            // The same scene mirrored left-right gives the mirrored cost (the algorithm has no side bias).
            List<RouteRoom> flipped = new List<RouteRoom>();
            foreach (RouteRoom room in rooms)
            {
                RouteRoom f = new RouteRoom { Name = room.Name, Corridor = room.Corridor };
                foreach (Vec2 v in room.Ring)
                    f.Ring.Add(V(30000 - v.X, v.Y));
                flipped.Add(f);
            }

            List<Vec2> fu = new List<Vec2>();
            foreach (Vec2 u in units)
                fu.Add(V(30000 - u.X, u.Y));
            RouteDraftResult m = Corridor(V(30000 - t.X, t.Y), fu, outs, flipped);
            Dump("U mirrored", m);
            Invariants("U mirrored", V(30000 - t.X, t.Y), fu, m, 1e12);
            Check.True("U mirrored: about the same length (" + r.TotalLength.ToString("0") + " / " + m.TotalLength.ToString("0") + ")",
                Math.Abs(r.TotalLength - m.TotalLength) <= 0.05 * r.TotalLength);
            Check.Near("U mirrored: about the same cost", r.RouteCost, m.RouteCost, 0.05 * r.RouteCost);
        }

        private static void CorridorFallback()
        {
            Vec2 t = V(0, 0);
            List<Vec2> units = L(4000, 3000, 8000, -2000);
            RouteDraftResult none = RouteDraftCore.Build(t, units, new RouteDraftOptions { Mode = "corridor", MinStub = Stub });
            Check.True("no rooms: a classic mode is used", none.ModeUsed != "corridor" && none.ModeUsed != "");
            Invariants("fallback", t, units, none);

            RouteDraftResult empty = RouteDraftCore.Build(t, units, new RouteDraftOptions
            {
                Mode = "corridor",
                MinStub = Stub,
                Rooms = new List<RouteRoom>()
            });
            Check.True("empty rooms: a classic mode is used", empty.ModeUsed != "corridor");
            Check.Near("no rooms: no cost", 0, none.RouteCost);

            // A forced classic mode ignores the rooms.
            RouteDraftResult mst = Corridor(t, units, null, UScene(), "mst");
            Check.Equal("forced mst", "mst", mst.ModeUsed);
        }

        private static void CorridorRandom()
        {
            Random random = new Random(9071);
            for (int trial = 0; trial < 60; trial++)
            {
                // A 4 x 2 block of rooms 6 m wide with a corridor row between them, random units inside.
                List<RouteRoom> rooms = new List<RouteRoom>();
                rooms.Add(Rect(0, 6000, 24000, 8000, true, "Corridor"));
                for (int c = 0; c < 4; c++)
                {
                    rooms.Add(Rect(c * 6000, 0, c * 6000 + 6000, 6000, false, "Room " + c));
                    rooms.Add(Rect(c * 6000, 8000, c * 6000 + 6000, 14000, false, "Room " + (c + 4)));
                }

                int n = 1 + random.Next(14);
                List<Vec2> units = new List<Vec2>();
                List<Vec2> outs = new List<Vec2>();
                for (int i = 0; i < n; i++)
                {
                    double x = random.Next(0, 96) * 250 + 125;
                    double y = random.Next(0, 56) * 250 + (random.Next(2) == 0 ? 0 : 8000) + 125;
                    units.Add(V(x, y));
                    int axis = random.Next(5);
                    outs.Add(axis == 0 ? V(1, 0) : axis == 1 ? V(-1, 0) : axis == 2 ? V(0, 1) : axis == 3 ? V(0, -1) : V(0, 0));
                }

                Vec2 t = V(random.Next(0, 96) * 250, random.Next(0, 56) * 250);
                double stub = random.Next(4) == 0 ? 0 : 1500;
                RouteDraftResult r = Corridor(t, units, outs, rooms, "corridor", stub, random.Next(2) == 0 ? 200 : 250);
                Invariants("corridor random " + trial, t, units, r, 1e12);
                Check.Equal("corridor random " + trial + ": mode", "corridor", r.ModeUsed);
                Check.Equal("corridor random " + trial + ": four-way junctions", 0, r.CrossJunctions);
            }
        }

        private static void CorridorStubAndPorts()
        {
            List<RouteRoom> rooms = UScene();
            Vec2 t = V(1000, 15000);
            List<Vec2> units = L(6000, 4500, 15000, 4500);
            List<Vec2> outs = L(0, -1, 0, -1);
            RouteDraftResult r = Corridor(t, units, outs, rooms);

            // The target is a leaf and the first piece is straight for at least the stub.
            List<Vec2> rootLine = null;
            foreach (List<Vec2> line in r.Polylines)
            {
                if (Manhattan(line[0], t) < 1e-6 || Manhattan(line[line.Count - 1], t) < 1e-6)
                    rootLine = line;
            }

            Dump("stub", r);
            Check.True("stub: a polyline starts at the target", rootLine != null);
            if (rootLine != null)
            {
                Vec2 a = Manhattan(rootLine[0], t) < 1e-6 ? rootLine[0] : rootLine[rootLine.Count - 1];
                Vec2 b = Manhattan(rootLine[0], t) < 1e-6 ? rootLine[1] : rootLine[rootLine.Count - 2];
                Check.True("stub: first leg at least the stub", a.DistanceTo(b) >= Stub - 1e-6, a.DistanceTo(b).ToString("0.###"));
            }

            // Each unit leaves its port straight along its direction for the lead (400 mm).
            foreach (Vec2 u in units)
            {
                foreach (List<Vec2> line in r.Polylines)
                {
                    int end = Manhattan(line[0], u) < 1e-6 ? 0 : Manhattan(line[line.Count - 1], u) < 1e-6 ? line.Count - 1 : -1;
                    if (end < 0)
                        continue;
                    Vec2 next = line[end == 0 ? 1 : line.Count - 2];
                    Check.Near("port lead x", u.X, next.X, 1e-6);
                    Check.True("port lead goes downward", next.Y <= u.Y - 400 + 1e-6, next.ToString());
                }
            }

            // A unit with no direction still ends at its exact point.
            RouteDraftResult plain = Corridor(t, units, null, rooms);
            Invariants("ports without directions", t, units, plain, 1e12);
            Check.Equal("ports without directions: leaves", 0, plain.NonLeafUnits);

            // Two units whose anchors share a grid cell, and a unit on the target.
            List<Vec2> close = L(6000, 4500, 6050, 4520, 1000, 15000);
            RouteDraftResult c = Corridor(t, close, L(0, -1, 0, -1, 0, 0), rooms);
            Invariants("close units", t, close, c, 1e12);
            Check.Equal("close units: no non-leaf", 0, c.NonLeafUnits);
        }

        private static void CorridorCoarsens()
        {
            List<RouteRoom> rooms = new List<RouteRoom> { Rect(0, 0, 400000, 150000, true, "Hall") };
            Vec2 t = V(0, 0);
            List<Vec2> units = L(390000, 140000, 200000, 70000, 10000, 100000);
            Stopwatch watch = Stopwatch.StartNew();
            RouteDraftResult r = Corridor(t, units, null, rooms);
            watch.Stop();
            Invariants("coarse", t, units, r, 1e12);
            Check.True("coarse: the cell grew (" + r.CellSizeUsed.ToString("0") + ")", r.CellSizeUsed > 200);
            Check.True("coarse: grid within the cap", (400000 / r.CellSizeUsed + 30) * (150000 / r.CellSizeUsed + 30) <= 120000 * 1.5);
            Check.True("coarse: fast (" + watch.ElapsedMilliseconds + " ms)", watch.ElapsedMilliseconds < 3000);
        }

        // 40 x 15 m floor: a corridor along the middle, ten rooms on each side, 30 units.
        private static void PerfScene(out List<RouteRoom> rooms, out List<Vec2> units, out List<Vec2> outs, out Vec2 target)
        {
            rooms = new List<RouteRoom>();
            rooms.Add(Rect(0, 6500, 40000, 8500, true, "Hanh lang"));
            units = new List<Vec2>();
            outs = new List<Vec2>();
            Random random = new Random(5);
            for (int c = 0; c < 10; c++)
            {
                rooms.Add(Rect(c * 4000, 0, c * 4000 + 4000, 6500, false, "Phong " + c));
                rooms.Add(Rect(c * 4000, 8500, c * 4000 + 4000, 15000, false, "Phong " + (c + 10)));
                for (int k = 0; k < 3; k++)
                {
                    if (units.Count >= 30)
                        break;
                    bool top = (c + k) % 2 == 0;
                    double x = c * 4000 + 800 + random.Next(0, 5) * 450;
                    double y = top ? 11000 + random.Next(0, 8) * 250 : 2500 + random.Next(0, 8) * 250;
                    units.Add(V(x, y));
                    outs.Add(V(0, top ? -1 : 1));
                }
            }

            target = V(500, 7500);
        }

        private static void CorridorPerformance()
        {
            List<RouteRoom> rooms;
            List<Vec2> units, outs;
            Vec2 t;
            PerfScene(out rooms, out units, out outs, out t);
            Check.Equal("perf: 30 units", 30, units.Count);

            Stopwatch cold = Stopwatch.StartNew();
            RouteDraftResult r = Corridor(t, units, outs, rooms);
            cold.Stop();
            Stopwatch warm = Stopwatch.StartNew();
            RouteDraftResult again = Corridor(t, units, outs, rooms);
            warm.Stop();
            Console.WriteLine("   perf: 30 units, 40 x 15 m, 200 mm cells: first run {0} ms, second run {1} ms, "
                + "{2} polylines, {3:0.0} m of pipe, cost {4:0}", cold.ElapsedMilliseconds, warm.ElapsedMilliseconds,
                r.Polylines.Count, r.TotalLength / 1000, r.RouteCost);
            Check.True("perf: first run under 2 s (" + cold.ElapsedMilliseconds + " ms)", cold.ElapsedMilliseconds < 2000);
            Check.True("perf: second run under 2 s (" + warm.ElapsedMilliseconds + " ms)", warm.ElapsedMilliseconds < 2000);
            Dump("perf", r);
            Invariants("perf", t, units, r, 1e12);
            Check.Equal("perf: mode", "corridor", r.ModeUsed);
            Check.Equal("perf: no non-leaf units", 0, r.NonLeafUnits);
            Check.Equal("perf: deterministic", r.TotalLength.ToString("R", CultureInfo.InvariantCulture),
                again.TotalLength.ToString("R", CultureInfo.InvariantCulture));

            Console.WriteLine("   perf: share of the pipe inside corridors: default costs {0:0}%", 100 * CorridorShare(rooms, r));
            RouteDraftResult sticky = RouteDraftCore.Build(t, units, new RouteDraftOptions
            {
                Mode = "corridor", MinStub = Stub, Rooms = rooms, UnitOut = outs, BoundaryPenalty = 12, RoomPenalty = 8
            });
            Console.WriteLine("   perf: share of the pipe inside corridors: room penalty 8, boundary penalty 12: {0:0}%",
                100 * CorridorShare(rooms, sticky));
            Check.True("perf: dearer rooms keep more pipe in the corridor", CorridorShare(rooms, sticky) >= CorridorShare(rooms, r) - 1e-9);

            RouteDraftResult straight = Corridor(t, units, outs, rooms, "best");
            Check.True("perf: corridor cost " + r.RouteCost.ToString("0") + " < wall-blind " + straight.RouteCost.ToString("0"),
                r.RouteCost < straight.RouteCost);
        }

        private static double CorridorShare(IList<RouteRoom> rooms, RouteDraftResult r)
        {
            double inside = 0, total = 0;
            foreach (List<Vec2> line in r.Polylines)
            {
                for (int i = 0; i + 1 < line.Count; i++)
                {
                    double length = line[i].DistanceTo(line[i + 1]);
                    int steps = Math.Max(1, (int)Math.Ceiling(length / 100));
                    for (int k = 0; k < steps; k++)
                    {
                        Vec2 q = line[i] + (line[i + 1] - line[i]) * ((k + 0.5) / steps);
                        total += length / steps;
                        if (InCorridor(rooms, q))
                            inside += length / steps;
                    }
                }
            }

            return total <= 0 ? 0 : inside / total;
        }

        private static void CorridorCleaner()
        {
            List<RouteRoom> rooms;
            List<Vec2> units, outs;
            Vec2 t;
            PerfScene(out rooms, out units, out outs, out t);
            List<Vec2> few = units.GetRange(0, 6);
            List<Vec2> fewOut = outs.GetRange(0, 6);

            List<RouteRoom> u = UScene();
            Vec2 ut = V(1000, 15000);
            List<Vec2> uu = L(6000, 4500, 15000, 4500, 24000, 4500);
            List<Vec2> uo = L(0, -1, 0, -1, 0, -1);

            object[][] scenes = { new object[] { t, few, fewOut, rooms }, new object[] { ut, uu, uo, u }, new object[] { t, units, outs, rooms } };
            int scene = 0;
            foreach (object[] sc in scenes)
            {
                Vec2 target = (Vec2)sc[0];
                List<Vec2> us = (List<Vec2>)sc[1];
                RouteDraftResult draft = Corridor(target, us, (List<Vec2>)sc[2], (List<RouteRoom>)sc[3]);
                string name = "corridor cleaner " + scene++;
                RefrigResult result = Clean(target, draft);
                Dump(name + " draft", draft);
                if (Environment.GetEnvironmentVariable("ROUTE_DEBUG") == "1" && Check.Scope == "en-US")
                    foreach (RefrigPath path in result.Paths)
                        Console.WriteLine("     path " + string.Join(" ", path.Points.ConvertAll(v => v.ToString())));
                Check.True(name + ": cleaner ok", result.Ok, result.Error);
                Check.True(name + ": root found", result.HasRoot);
                Check.Near(name + ": root at the target", 0, Manhattan(result.Root, target), 1.0);
                Check.Equal(name + ": one network", 1, result.Components);
                foreach (string report in result.Reports)
                    Check.False(name + ": no degree-4 junction", report.Contains("Degree-4"), report);

                int ends = 0;
                foreach (Vec2 p in us)
                {
                    foreach (RefrigPath path in result.Paths)
                    {
                        if (Manhattan(path.Points[0], p) < 1.0 || Manhattan(path.Points[path.Points.Count - 1], p) < 1.0)
                        {
                            ends++;
                            break;
                        }
                    }
                }

                Check.Equal(name + ": every unit is a pipe end", us.Count, ends);
                NetworkTopology topology = NetworkTopologyBuilder.Build(new TopologyInput
                {
                    Paths = result.Paths,
                    PlannedRefnets = result.Refnets,
                    Root = result.Root,
                    Tolerance = 50
                });
                Check.True(name + ": topology ok", topology.Ok, topology.Error);
                Check.Equal(name + ": topology ends = units", us.Count, topology.Ends.Count);
            }
        }
    }
}
