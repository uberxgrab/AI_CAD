using System;
using System.Collections.Generic;
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
            Check.Equal("defaults: mode", "best", s.Mode);
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
            Check.Equal("bad input keeps mode", "best", bad.Mode);
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
    }
}
