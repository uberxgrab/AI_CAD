using System;
using System.Collections.Generic;
using HvacAiCadAddin.Units;
using PipeCoreTests;

namespace UnitCoreTests
{
    internal static class UnitTests
    {
        private const double W = 950, H = 950;

        public static void Run()
        {
            Check.Case("catalog", CatalogChecks);
            Check.Case("choose one unit", ChooseOne);
            Check.Case("choose tolerance", ChooseTolerance);
            Check.Case("choose split", ChooseSplit);
            Check.Case("choose limits", ChooseLimits);
            Check.Case("settings round trip", SettingsRoundTrip);
            Check.Case("settings bad values", SettingsBad);
            Check.Case("room and load tags", Tags);
            Check.Case("dominant angle", DominantAngle);
            Check.Case("pole", Pole);
            Check.Case("place one in rectangle", OneInRectangle);
            Check.Case("place two in long room", TwoInLongRoom);
            Check.Case("place in rotated room", RotatedRoom);
            Check.Case("place in L room", LRoom);
            Check.Case("place in narrow room", NarrowRoom);
            Check.Case("place in tiny room", TinyRoom);
            Check.Case("insertion point", Insertion);
        }

        private static double[][] Rect(double x0, double y0, double x1, double y1)
        {
            return new[] { new[] { x0, y0 }, new[] { x1, y0 }, new[] { x1, y1 }, new[] { x0, y1 } };
        }

        private static void AssertAllInside(string name, double[][] polygon, List<PlacedUnitPose> poses, double clearance)
        {
            for (int i = 0; i < poses.Count; i++)
            {
                double[] centre = { poses[i].X, poses[i].Y };
                // Rotate into the wall frame by testing the four rotated corners with the clearance added.
                double[][] corners = UnitPlacer.Corners(poses[i], W + 2 * clearance, H + 2 * clearance);
                bool inside = true;
                foreach (double[] corner in corners)
                    inside &= UnitPlacer.Inside(polygon, corner[0], corner[1]);
                Check.True(name + " unit " + i + " inside with clearance", inside,
                    "centre " + centre[0] + "," + centre[1]);
                Check.True(name + " unit " + i + " fits flag", poses[i].Fits);
                for (int j = i + 1; j < poses.Count; j++)
                {
                    double d = Math.Sqrt(Math.Pow(poses[i].X - poses[j].X, 2) + Math.Pow(poses[i].Y - poses[j].Y, 2));
                    Check.True(name + " units " + i + "," + j + " apart", d >= W + UnitPlacer.UnitGap - 1,
                        d.ToString("0.0"));
                }
            }
        }

        private static void CatalogChecks()
        {
            UnitFamily f = UnitCatalog.Cassette;
            Check.Equal("9 models", 9, f.Models.Length);
            Check.Equal("first", "FCU-CAS-2.8 kW", f.Models[0].BlockName);
            Check.Equal("14 name", "FCU-CAS-14 kW", f.Models[7].BlockName);
            Check.Equal("9.0 name", "FCU-CAS-9.0 kW", f.Models[5].BlockName);
            for (int i = 1; i < f.Models.Length; i++)
                Check.True("ascending " + i, f.Models[i].Index > f.Models[i - 1].Index && f.Models[i].Kw > f.Models[i - 1].Kw);
            Check.True("unknown family falls back", ReferenceEquals(UnitCatalog.Find("nope"), UnitCatalog.Cassette));
            Check.True("find case-insensitive", ReferenceEquals(UnitCatalog.Find("fcu-cas"), UnitCatalog.Cassette));
        }

        private static void ChooseOne()
        {
            UnitSettings s = new UnitSettings();
            UnitFamily f = UnitCatalog.Cassette;
            Check.Equal("2.0 -> 25", 25, UnitSelector.Choose(2.0, f, s, 0).Model.Index);
            Check.Equal("2.8 -> 25", 25, UnitSelector.Choose(2.8, f, s, 0).Model.Index);
            Check.Equal("3.0 -> 32", 32, UnitSelector.Choose(3.0, f, s, 0).Model.Index);
            Check.Equal("5.6 -> 50", 50, UnitSelector.Choose(5.6, f, s, 0).Model.Index);
            Check.Equal("7.5 -> 80", 80, UnitSelector.Choose(7.5, f, s, 0).Model.Index);
            Check.Equal("16 -> 140", 140, UnitSelector.Choose(16, f, s, 0).Model.Index);
            Check.Equal("units", 1, UnitSelector.Choose(5.0, f, s, 0).Units);
            Check.True("no load", UnitSelector.Choose(0, f, s, 0) == null);
            Check.True("negative load", UnitSelector.Choose(-1, f, s, 0) == null);
            Check.True("NaN load", UnitSelector.Choose(double.NaN, f, s, 0) == null);
        }

        private static void ChooseTolerance()
        {
            UnitFamily f = UnitCatalog.Cassette;
            UnitSettings s = new UnitSettings { Tolerance = 0.03 };
            Check.Equal("2.85 within 3 percent -> 25", 25, UnitSelector.Choose(2.85, f, s, 0).Model.Index);
            Check.Equal("2.95 over 3 percent -> 32", 32, UnitSelector.Choose(2.95, f, s, 0).Model.Index);
            s.Tolerance = 0;
            Check.Equal("2.85 no tolerance -> 32", 32, UnitSelector.Choose(2.85, f, s, 0).Model.Index);
        }

        private static void ChooseSplit()
        {
            UnitFamily f = UnitCatalog.Cassette;
            UnitSettings s = new UnitSettings();
            UnitChoice two = UnitSelector.Choose(17, f, s, 0);
            Check.Equal("17 kW -> 2 units", 2, two.Units);
            Check.Equal("17 kW -> 80 each", 80, two.Model.Index);
            Check.Near("per unit", 8.5, two.PerUnitKw);
            UnitChoice three = UnitSelector.Choose(40, f, s, 0);
            Check.Equal("40 kW -> 3 units", 3, three.Units);
            Check.Equal("40 kW -> 125 each", 125, three.Model.Index);
            Check.False("not undersized", three.Undersized);
            UnitChoice forced = UnitSelector.Choose(10, f, s, 4);
            Check.Equal("override units", 4, forced.Units);
            Check.Equal("override index", 25, forced.Model.Index);

            s.MaxIndex = 100;
            UnitChoice capped = UnitSelector.Choose(17, f, s, 0);
            Check.Equal("max index 100: 2 units", 2, capped.Units);
            Check.Equal("max index 100: index", 80, capped.Model.Index);
            UnitChoice capped2 = UnitSelector.Choose(30, f, s, 0);
            Check.Equal("max index 100, 30 kW: 3 units", 3, capped2.Units);
            Check.Equal("max index 100, 30 kW: index", 100, capped2.Model.Index);
        }

        private static void ChooseLimits()
        {
            UnitFamily f = UnitCatalog.Cassette;
            UnitSettings s = new UnitSettings { MaxUnitsPerRoom = 2 };
            UnitChoice c = UnitSelector.Choose(60, f, s, 0);
            Check.Equal("capped units", 2, c.Units);
            Check.Equal("largest model", 140, c.Model.Index);
            Check.True("flagged undersized", c.Undersized);
            s.MaxIndex = 10;
            Check.Equal("tiny max index keeps smallest", 25, UnitSelector.Choose(1, f, s, 0).Model.Index);
        }

        private static void SettingsRoundTrip()
        {
            UnitSettings s = new UnitSettings { Clearance = 450.5, MaxIndex = 100, Tolerance = 0.05, MaxUnitsPerRoom = 4, Family = "FCU-CAS" };
            UnitSettings back = UnitSettings.Deserialize(s.Serialize());
            Check.Near("clearance", 450.5, back.Clearance);
            Check.Equal("max index", 100, back.MaxIndex);
            Check.Near("tolerance", 0.05, back.Tolerance);
            Check.Equal("units", 4, back.MaxUnitsPerRoom);
            Check.Equal("family", "FCU-CAS", back.Family);
            string error;
            Check.True("valid", back.IsValid(out error), error);
        }

        private static void SettingsBad()
        {
            UnitSettings d = new UnitSettings();
            UnitSettings s = UnitSettings.Deserialize("clearance=abc\nmaxindex=-4\ntolerance=9\nmaxunits=0\nfoo\n=3\nfamily=");
            Check.Near("clearance default", d.Clearance, s.Clearance);
            Check.Equal("index default", d.MaxIndex, s.MaxIndex);
            Check.Near("tolerance default", d.Tolerance, s.Tolerance);
            Check.Equal("units default", d.MaxUnitsPerRoom, s.MaxUnitsPerRoom);
            Check.Equal("family default", d.Family, s.Family);
            Check.Near("null text", d.Clearance, UnitSettings.Deserialize(null).Clearance);
            string error;
            Check.False("invalid clearance", new UnitSettings { Clearance = -1 }.IsValid(out error));
            Check.False("invalid units", new UnitSettings { MaxUnitsPerRoom = 0 }.IsValid(out error));
        }

        private static void Tags()
        {
            RoomTags.RoomTag room = RoomTags.ParseRoom(new object[] { "HVACAI_ROOM", "R7", "room", "PHÒNG KHÁCH", 25.44 });
            Check.True("room parsed", room != null);
            Check.Equal("room id", "R7", room.Id);
            Check.False("room not shaft", room.IsShaft);
            Check.Near("room area", 25.44, room.AreaM2);
            Check.True("shaft", RoomTags.ParseRoom(new object[] { "HVACAI_ROOM", "R2", "shaft", "", 1.4 }).IsShaft);
            Check.True("short room tag", RoomTags.ParseRoom(new object[] { "HVACAI_ROOM", "R2" }) == null);
            Check.True("bad area", RoomTags.ParseRoom(new object[] { "HVACAI_ROOM", "R2", "room", "x", "n" }) == null);
            Check.True("null values", RoomTags.ParseRoom(null) == null);

            RoomTags.LoadTag load = RoomTags.ParseLoad(new object[] { "HVACAI_LOAD", "office", 130.0, 3.9, 1.1 });
            Check.True("load parsed", load != null);
            Check.Equal("room type", "office", load.RoomType);
            Check.Near("w per m2", 130, load.WattsPerM2);
            Check.Near("load kw", 3.9, load.LoadKw);
            Check.Near("safety", 1.1, load.SafetyFactor);
            Check.True("short load tag", RoomTags.ParseLoad(new object[] { "HVACAI_LOAD", "office", 1.0 }) == null);
        }

        private static void DominantAngle()
        {
            Check.Near("axis aligned", 0, UnitPlacer.DominantAngle(Rect(0, 0, 8000, 3000)), 1e-9);
            double a = 30 * Math.PI / 180;
            double[][] rotated = RotatedRect(0, 0, 8000, 3000, a);
            Check.Near("rotated 30", a, UnitPlacer.DominantAngle(rotated), 1e-6);
            double b = 60 * Math.PI / 180; // equals -30 degrees modulo 90
            Check.Near("60 degrees folds to -30", b - Math.PI / 2, UnitPlacer.DominantAngle(RotatedRect(0, 0, 8000, 3000, b)), 1e-6);
            // A short chamfer does not turn the room.
            double[][] chamfer =
            {
                new[] { 0.0, 0.0 }, new[] { 8000.0, 0.0 }, new[] { 8000.0, 2500.0 }, new[] { 7500.0, 3000.0 }, new[] { 0.0, 3000.0 }
            };
            Check.Near("chamfer ignored", 0, UnitPlacer.DominantAngle(chamfer), 0.05);
        }

        private static double[][] RotatedRect(double x0, double y0, double x1, double y1, double angle)
        {
            double[][] r = Rect(x0, y0, x1, y1);
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            for (int i = 0; i < r.Length; i++)
                r[i] = new[] { r[i][0] * cos - r[i][1] * sin + 1000, r[i][0] * sin + r[i][1] * cos + 500 };
            return r;
        }

        private static void Pole()
        {
            double[] pole = UnitPlacer.Pole(Rect(0, 0, 4000, 4000));
            Check.Near("square pole x", 2000, pole[0], 60);
            Check.Near("square pole y", 2000, pole[1], 60);
            Check.Near("square pole distance", 2000, pole[2], 60);
            double[] strip = UnitPlacer.Pole(Rect(0, 0, 10000, 2000));
            Check.Near("strip pole y", 1000, strip[1], 60);
            Check.Near("strip pole distance", 1000, strip[2], 60);
        }

        private static void OneInRectangle()
        {
            double[][] room = Rect(0, 0, 5000, 4000);
            List<PlacedUnitPose> poses = UnitPlacer.Place(room, 1, W, H, 600);
            Check.Equal("count", 1, poses.Count);
            Check.Near("x centre", 2500, poses[0].X, 100);
            Check.Near("y centre", 2000, poses[0].Y, 100);
            Check.Near("rotation", 0, poses[0].Rotation, 1e-9);
            AssertAllInside("rect", room, poses, 600);
        }

        private static void TwoInLongRoom()
        {
            double[][] room = Rect(0, 0, 10000, 4000);
            List<PlacedUnitPose> poses = UnitPlacer.Place(room, 2, W, H, 600);
            Check.Equal("count", 2, poses.Count);
            Check.Near("first x", 2500, poses[0].X, 150);
            Check.Near("second x", 7500, poses[1].X, 150);
            Check.Near("same y", poses[0].Y, poses[1].Y, 150);
            AssertAllInside("long", room, poses, 600);

            List<PlacedUnitPose> three = UnitPlacer.Place(room, 3, W, H, 600);
            Check.Equal("three count", 3, three.Count);
            AssertAllInside("long3", room, three, 600);
            double s1 = three[1].X - three[0].X, s2 = three[2].X - three[1].X;
            Check.Near("equal spacing", s1, s2, 250);

            // Tall room: units spread along y.
            double[][] tall = Rect(0, 0, 3500, 9000);
            List<PlacedUnitPose> column = UnitPlacer.Place(tall, 2, W, H, 600);
            Check.True("second above first", column[1].Y - column[0].Y > 3000, (column[1].Y - column[0].Y).ToString("0"));
            AssertAllInside("tall", tall, column, 600);
        }

        private static void RotatedRoom()
        {
            double a = 30 * Math.PI / 180;
            double[][] room = RotatedRect(0, 0, 8000, 4000, a);
            List<PlacedUnitPose> poses = UnitPlacer.Place(room, 2, W, H, 600);
            Check.Equal("count", 2, poses.Count);
            Check.Near("rotation follows walls", a, poses[0].Rotation, 1e-6);
            AssertAllInside("rotated", room, poses, 600);
            double[][] corners = UnitPlacer.Corners(poses[0], W, H);
            double edge = Math.Atan2(corners[1][1] - corners[0][1], corners[1][0] - corners[0][0]);
            Check.Near("body edge direction", a, edge, 1e-6);
        }

        private static void LRoom()
        {
            // L: 8000 x 6000 with the upper-right 4000 x 3000 corner cut out.
            double[][] room =
            {
                new[] { 0.0, 0.0 }, new[] { 8000.0, 0.0 }, new[] { 8000.0, 3000.0 }, new[] { 4000.0, 3000.0 },
                new[] { 4000.0, 6000.0 }, new[] { 0.0, 6000.0 }
            };
            List<PlacedUnitPose> one = UnitPlacer.Place(room, 1, W, H, 600);
            AssertAllInside("L one", room, one, 600);
            List<PlacedUnitPose> two = UnitPlacer.Place(room, 2, W, H, 600);
            Check.Equal("L two count", 2, two.Count);
            AssertAllInside("L two", room, two, 600);
            List<PlacedUnitPose> three = UnitPlacer.Place(room, 3, W, H, 600);
            AssertAllInside("L three", room, three, 600);
            foreach (PlacedUnitPose pose in three)
                Check.False("not in the cut-out", pose.X > 4000 && pose.Y > 3000);
        }

        private static void NarrowRoom()
        {
            // 1900 wide: 950 + 2 x 600 does not fit, so the clearance drops to zero and the unit is flagged.
            double[][] room = Rect(0, 0, 9000, 1900);
            List<PlacedUnitPose> poses = UnitPlacer.Place(room, 2, W, H, 600);
            Check.Equal("count", 2, poses.Count);
            foreach (PlacedUnitPose pose in poses)
            {
                Check.False("flagged tight", pose.Fits);
                Check.Near("centred across", 950, pose.Y, 60);
            }

            Check.True("apart", Math.Abs(poses[1].X - poses[0].X) >= W + UnitPlacer.UnitGap - 1);

            // 3000 wide corridor: fits with clearance.
            double[][] corridor = Rect(0, 0, 12000, 2400);
            List<PlacedUnitPose> ok = UnitPlacer.Place(corridor, 3, W, H, 600);
            AssertAllInside("corridor", corridor, ok, 600);
        }

        private static void TinyRoom()
        {
            double[][] room = Rect(0, 0, 900, 900);
            List<PlacedUnitPose> poses = UnitPlacer.Place(room, 1, W, H, 600);
            Check.Equal("count", 1, poses.Count);
            Check.False("does not fit", poses[0].Fits);
            Check.Near("still at the middle", 450, poses[0].X, 60);
            Check.Equal("empty polygon", 0, UnitPlacer.Place(new double[0][], 1, W, H, 600).Count);
            Check.Equal("zero units", 0, UnitPlacer.Place(room, 0, W, H, 600).Count);
        }

        private static void Insertion()
        {
            UnitFamily f = UnitCatalog.Cassette;
            double[] p0 = UnitPlacer.InsertionPoint(new PlacedUnitPose { X = 1000, Y = 2000, Rotation = 0 }, f);
            Check.Near("x", 1000, p0[0]);
            Check.Near("y", 2475, p0[1]);
            // Rotated 90 degrees: the body (0,-475) maps to (475, 0) from the base point.
            double[] p90 = UnitPlacer.InsertionPoint(new PlacedUnitPose { X = 1000, Y = 2000, Rotation = Math.PI / 2 }, f);
            Check.Near("x 90", 525, p90[0], 1e-6);
            Check.Near("y 90", 2000, p90[1], 1e-6);
        }
    }
}
