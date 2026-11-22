using System;
using System.Collections.Generic;
using System.IO;
using HvacAiCadAddin.Rooms;
using PipeCoreTests;

namespace RoomsCoreTests
{
    internal static class RoomsTests
    {
        // A rooms.json written by arch_rooms.py for the tests/test_arch_rooms.py plan (two rooms kept),
        // plus a Vietnamese name and the extra keys the C# side ignores.
        private const string SampleResult =
            "{\"version\": 1, \"status\": \"ok\", \"units\": \"mm\", \"method\": \"polygonize\", \"rooms\": ["
            + "{\"id\": \"R1\", \"kind\": \"shaft\", \"name\": null, \"area_m2\": 1.44, \"label_area_m2\": null, "
            + "\"label_error_pct\": null, \"texts\": [], \"label_point\": [800.0, 5200.0], "
            + "\"polygon\": [[200.0, 4600.0], [200.0, 5800.0], [1400.0, 5800.0], [1400.0, 4600.0]]}, "
            + "{\"id\": \"R2\", \"kind\": \"room\", \"name\": \"PHÒNG KHÁCH {1}\", \"area_m2\": 25.44, \"label_area_m2\": 25.4, "
            + "\"label_error_pct\": 0.2, \"texts\": [\"PHÒNG KHÁCH\", \"DT. 25,4M2\"], \"label_point\": [2500.0, 3500.0], "
            + "\"polygon\": [[200.0, 200.0], [5000.0, 200.0], [5000.0, 5800.0], [1400.0, 5800.0], [1400.0, 4600.0], [200.0, 4600.0]]}], "
            + "\"envelope\": [[[0.0, 0.0], [0.0, 6000.0], [10000.0, 6000.0], [10000.0, 0.0]]], "
            + "\"stats\": {\"wall_curves\": 13, \"bridges\": 4, \"x_marks\": 1, \"faces\": 13, \"rooms\": 4, "
            + "\"shafts\": 1, \"total_area_m2\": 64.68, \"labelled\": 1, \"labelled_within_5pct\": 1}, \"warnings\": []}";

        public static void Run()
        {
            Check.Case("settings round trip", SettingsRoundTrip);
            Check.Case("settings bad values keep defaults", SettingsBadValues);
            Check.Case("settings validation", SettingsValidation);
            Check.Case("request json", RequestJson);
            Check.Case("parse python result", ParseResult);
            Check.Case("parse error result", ParseError);
            Check.Case("parse invalid json", ParseInvalid);
            Check.Case("usable polygons", Usable);
            Check.Case("wall layer suggestions", Suggestions);
            Check.Case("label text", Labels);
            Check.Case("region from points", Region);
            Check.Case("find script and venv", FindScript);
        }

        private static void SettingsRoundTrip()
        {
            RoomsSettings settings = new RoomsSettings
            {
                MaxOpening = 1250.5,
                MinRoomAreaM2 = 0.8,
                MinRoomWidth = 350,
                TextHeight = 175,
                PythonExe = @"C:\Py 3\python.exe",
                ScriptPath = @"D:\repo\arch_rooms.py"
            };
            settings.WallLayers.AddRange(new[] { "A-Tuong", "XR plan$0$CJSC.2-Tuong", "Tường gạch", "Net cat" });
            RoomsSettings back = RoomsSettings.Deserialize(settings.Serialize());
            Check.Equal("walls", string.Join(";", settings.WallLayers), string.Join(";", back.WallLayers));
            Check.Near("opening", 1250.5, back.MaxOpening);
            Check.Near("area", 0.8, back.MinRoomAreaM2);
            Check.Near("width", 350, back.MinRoomWidth);
            Check.Near("height", 175, back.TextHeight);
            Check.Equal("python", @"C:\Py 3\python.exe", back.PythonExe);
            Check.Equal("script", @"D:\repo\arch_rooms.py", back.ScriptPath);
            Check.Contains("invariant numbers", settings.Serialize(), "maxopening=1250.5");
        }

        private static void SettingsBadValues()
        {
            RoomsSettings back = RoomsSettings.Deserialize("maxopening=abc\nminarea=-5\nminwidth=1e9\ntextheight=\nwalls=||\nnoise");
            RoomsSettings defaults = new RoomsSettings();
            Check.Near("opening", defaults.MaxOpening, back.MaxOpening);
            Check.Near("area", defaults.MinRoomAreaM2, back.MinRoomAreaM2);
            Check.Near("width", defaults.MinRoomWidth, back.MinRoomWidth);
            Check.Near("height", defaults.TextHeight, back.TextHeight);
            Check.Equal("no walls", 0, back.WallLayers.Count);
            Check.Equal("empty text", 0, RoomsSettings.Deserialize(null).WallLayers.Count);
        }

        private static void SettingsValidation()
        {
            string error;
            RoomsSettings settings = new RoomsSettings();
            Check.False("no wall layer is invalid", settings.IsValid(out error));
            Check.Contains("message", error, "wall layer");
            settings.WallLayers.Add("A-Tuong");
            Check.True("defaults with a layer are valid", settings.IsValid(out error), error);
            settings.MaxOpening = 50;
            Check.False("opening too small", settings.IsValid(out error));
            settings.MaxOpening = double.NaN;
            Check.False("opening NaN", settings.IsValid(out error));
        }

        private static void RequestJson()
        {
            RoomsSettings settings = new RoomsSettings { MaxOpening = 1500.5, MinRoomAreaM2 = 1.25, MinRoomWidth = 400 };
            settings.WallLayers.AddRange(new[] { "A-Tuong", "XR$0$Cot", "Tường" });
            double[][] region = { new[] { 0.5, 0.0 }, new[] { 10.0, 0.0 }, new[] { 10.0, 6.0 }, new[] { 0.5, 6.0 } };
            string json = RoomsExchangeCore.ToJson(RoomsRequest.From(settings, region));
            Check.Contains("version", json, "\"version\":1");
            Check.Contains("dxf", json, "\"dxf\":\"snapshot.dxf\"");
            Check.Contains("out", json, "\"out\":\"rooms.json\"");
            Check.Contains("layers", json, "\"wall_layers\":[\"A-Tuong\",\"XR$0$Cot\",\"Tường\"]");
            Check.Contains("region uses a decimal point", json, "[0.5,0]");
            Check.Contains("opening uses a decimal point", json, "\"max_opening\":1500.5");
            Check.Contains("area", json, "\"min_room_area_m2\":1.25");
            Check.Contains("method", json, "\"method\":\"polygonize\"");
            string none = RoomsExchangeCore.ToJson(RoomsRequest.From(settings, null));
            Check.Contains("no region is null", none, "\"region\":null");
        }

        private static void ParseResult()
        {
            RoomsResult result = RoomsExchangeCore.ParseResult("\uFEFF" + SampleResult);
            Check.True("ok", result.IsOk);
            Check.Equal("rooms", 2, result.Rooms.Length);
            RoomItem shaft = result.Rooms[0];
            Check.True("shaft kind", shaft.IsShaft);
            Check.Equal("shaft name null", null, shaft.Name);
            Check.False("no label area", shaft.LabelAreaM2.HasValue);
            Check.Near("shaft area", 1.44, shaft.AreaM2);
            Check.Equal("vertices", 4, shaft.Polygon.Length);
            Check.Near("label point", 5200.0, shaft.LabelPoint[1]);
            RoomItem room = result.Rooms[1];
            Check.Equal("unicode name", "PHÒNG KHÁCH {1}", room.Name);
            Check.Near("label area", 25.4, room.LabelAreaM2.Value);
            Check.Equal("texts", 2, room.Texts.Length);
            Check.Equal("bridges", 4, result.Stats.Bridges);
            Check.Near("total", 64.68, result.Stats.TotalAreaM2);
            Check.Equal("warnings", 0, result.Warnings.Length);
        }

        private static void ParseError()
        {
            RoomsResult result = RoomsExchangeCore.ParseResult(
                "{\"version\": 1, \"status\": \"error\", \"message\": \"FileNotFoundError: missing.dxf\", \"detail\": \"...\"}");
            Check.False("not ok", result.IsOk);
            Check.Contains("message", result.Message, "missing.dxf");
            Check.Equal("no rooms", 0, result.Rooms.Length);
            Check.Equal("no warnings", 0, result.Warnings.Length);
        }

        private static void ParseInvalid()
        {
            foreach (string bad in new[] { "", "   ", "{\"rooms\": [", "not json" })
            {
                bool threw = false;
                try
                {
                    RoomsExchangeCore.ParseResult(bad);
                }
                catch (FormatException)
                {
                    threw = true;
                }

                Check.True("FormatException for " + Check.Show(bad), threw);
            }
        }

        private static void Usable()
        {
            Check.True("square", RoomsExchangeCore.IsUsable(new RoomItem
            {
                Polygon = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 0.0 }, new[] { 1.0, 1.0 } }
            }));
            Check.False("two points", RoomsExchangeCore.IsUsable(new RoomItem
            {
                Polygon = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 0.0 } }
            }));
            Check.False("NaN", RoomsExchangeCore.IsUsable(new RoomItem
            {
                Polygon = new[] { new[] { 0.0, 0.0 }, new[] { double.NaN, 0.0 }, new[] { 1.0, 1.0 } }
            }));
            Check.False("null", RoomsExchangeCore.IsUsable(null));
        }

        private static void Suggestions()
        {
            Dictionary<string, bool> cases = new Dictionary<string, bool>
            {
                { "A-Tuong", true }, { "aH-Wall", true }, { "Cot", true }, { "VACH KINH", true },
                { "cua-doors", true }, { "A-Cua", true }, { "XR nhà$0$Xr_ MB$0$CJSC.2-Tuong", true },
                { "CJSC.1-Be tong", true }, { "Tường xây", true }, { "墙体", true }, { ".5.DOOR", true },
                { "A-HatchVatlieu", false }, { "CJSC.5-Dim", false }, { "noi that", false }, { "A-Text", false },
                { "Kich thuoc", false }, { "A-kihieucot", false }, { "Net cat", false }, { "0", false }, { "", false }
            };
            foreach (KeyValuePair<string, bool> pair in cases)
                Check.Equal("suggest " + pair.Key, pair.Value, RoomsExchangeCore.SuggestWall(pair.Key));
            Check.Equal("layer key", "cjsc 2 tuong", RoomsExchangeCore.LayerKey("XR$0$CJSC.2-Tường"));
            Check.True("own layer", RoomsExchangeCore.IsOwnLayer("hvac-room"));
            Check.True("own split layer", RoomsExchangeCore.IsOwnLayer(RoomsSettings.SplitLayer));
            Check.False("not own", RoomsExchangeCore.IsOwnLayer("HVAC-ROOMS"));
        }

        private static void Labels()
        {
            RoomsResult result = RoomsExchangeCore.ParseResult(SampleResult);
            Check.Equal("shaft label", "HKT\\P1.4 m\u00B2", RoomsExchangeCore.LabelText(result.Rooms[0]));
            Check.Equal("escaped name", "PHÒNG KHÁCH \\{1\\}\\P25.4 m\u00B2", RoomsExchangeCore.LabelText(result.Rooms[1]));
            RoomItem unnamed = new RoomItem { Id = "R7", Kind = "room", AreaM2 = 12.345 };
            Check.Equal("id when no name", "R7\\P12.3 m\u00B2", RoomsExchangeCore.LabelText(unnamed));
            Check.Equal("backslash", "a\\\\b", RoomsExchangeCore.EscapeMText("a\\b"));
        }

        private static void Region()
        {
            double[][] box = RoomsExchangeCore.RegionFromPoints(new List<double[]>
            {
                new[] { 5.0, 1.0 }, new[] { -2.0, 7.0 }, new[] { 3.0, -4.0 }
            });
            Check.Equal("four corners", 4, box.Length);
            Check.Near("min x", -2.0, box[0][0]);
            Check.Near("min y", -4.0, box[0][1]);
            Check.Near("max x", 5.0, box[2][0]);
            Check.Near("max y", 7.0, box[2][1]);
            Check.True("one point is no region", RoomsExchangeCore.RegionFromPoints(new List<double[]> { new[] { 1.0, 1.0 } }) == null);
            Check.True("a line is no region", RoomsExchangeCore.RegionFromPoints(
                new List<double[]> { new[] { 0.0, 0.0 }, new[] { 5.0, 0.0 } }) == null);
            Check.True("null", RoomsExchangeCore.RegionFromPoints(null) == null);
        }

        private static void FindScript()
        {
            string root = Path.Combine(Path.GetTempPath(), "RoomsCoreTests-" + Guid.NewGuid().ToString("N"));
            string deep = Path.Combine(root, "cad_addin", "bin", "x64", "Debug", "net48");
            Directory.CreateDirectory(deep);
            try
            {
                Check.True("not found yet", RoomsExchangeCore.FindScript(deep) == null);
                string script = Path.Combine(root, RoomsExchangeCore.ScriptName);
                File.WriteAllText(script, "# test");
                Check.Equal("found five levels up", script, RoomsExchangeCore.FindScript(deep));
                Check.Equal("venv python", Path.Combine(root, "venv", "Scripts", "python.exe"),
                    RoomsExchangeCore.DefaultPython(script));
                Check.True("too deep", RoomsExchangeCore.FindScript(deep, 2) == null);
                Check.Equal("arguments", "\"C:\\a b\\arch_rooms.py\" --request \"D:\\t m\\request.json\"",
                    RoomsExchangeCore.Arguments(@"C:\a b\arch_rooms.py", @"D:\t m\request.json"));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
