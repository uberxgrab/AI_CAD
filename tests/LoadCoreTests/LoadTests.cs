using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using HvacAiCadAddin.Load;
using PipeCoreTests;

namespace LoadCoreTests
{
    internal static class LoadTests
    {
        // load.json written by heat_load.py for the sample request below (heat_load_table.json as of
        // writing: bedroom 150, hotel 170 W/m2, safety factor 1.1), plus extra keys the C# side ignores.
        private const string SampleResult =
            "{\"version\": 1, \"status\": \"ok\", \"safety_factor\": 1.1, \"table_note\": \"x\", \"rooms\": ["
            + "{\"id\": \"R1\", \"name\": \"PHÒNG NGỦ\", \"room_type\": \"bedroom\", \"label_vi\": \"Phòng ngủ\", \"cooled\": true, \"w_per_m2\": 150.0, \"area_m2\": 20.0, \"load_kw\": 3.3}, "
            + "{\"id\": \"R2\", \"name\": \"SUP.04\", \"room_type\": \"hotel\", \"label_vi\": \"Phòng khách sạn\", \"cooled\": true, \"w_per_m2\": 170.0, \"area_m2\": 25.0, \"load_kw\": 4.675}, "
            + "{\"id\": \"R3\", \"name\": \"WC\", \"room_type\": \"wc\", \"label_vi\": \"WC\", \"cooled\": false, \"w_per_m2\": 0.0, \"area_m2\": 4.0, \"load_kw\": 0.0}, "
            + "{\"id\": \"R4\", \"name\": \"\", \"room_type\": \"shaft\", \"label_vi\": \"HKT\", \"cooled\": false, \"w_per_m2\": 0.0, \"area_m2\": 1.44, \"load_kw\": 0.0}], "
            + "\"total_kw\": 7.975, \"warnings\": []}";

        private static string TablePath([CallerFilePath] string here = "")
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", "..", LoadCore.TableName));
        }

        public static void Run()
        {
            Check.Case("settings round trip", SettingsRoundTrip);
            Check.Case("settings bad values keep defaults", SettingsBad);
            Check.Case("request json", RequestJson);
            Check.Case("parse python result", ParseResult);
            Check.Case("parse error result", ParseError);
            Check.Case("parse invalid json", ParseInvalid);
            Check.Case("table file and round trip", TableFile);
            Check.Case("formula matches python sample", FormulaMatchesPython);
            Check.Case("label load line", Labels);
            Check.Case("csv", CsvText);
            Check.Case("find script and venv", FindScript);
        }

        private static void SettingsRoundTrip()
        {
            LoadSettings settings = new LoadSettings { SafetyFactor = 1.25, SaveTable = true, PythonExe = "C:\\py\\python.exe", ScriptPath = "C:\\r\\heat_load.py" };
            LoadSettings back = LoadSettings.Deserialize(settings.Serialize());
            Check.Near("safety", 1.25, back.SafetyFactor);
            Check.True("save table", back.SaveTable);
            Check.Equal("python", "C:\\py\\python.exe", back.PythonExe);
            Check.Equal("script", "C:\\r\\heat_load.py", back.ScriptPath);
            Check.Contains("culture-free number", settings.Serialize(), "safety=1.25\n");
        }

        private static void SettingsBad()
        {
            LoadSettings back = LoadSettings.Deserialize("safety=abc\nsavetable=0\nunknown=1\nsafety=9\n");
            Check.Near("default safety", 1.1, back.SafetyFactor);
            Check.False("default save", back.SaveTable);
            Check.Near("empty text", 1.1, LoadSettings.Deserialize(null).SafetyFactor);
            string error;
            Check.False("invalid safety", new LoadSettings { SafetyFactor = 0.1 }.IsValid(out error));
            Check.True("valid", new LoadSettings().IsValid(out error));
        }

        private static void RequestJson()
        {
            LoadRequest request = new LoadRequest
            {
                SafetyFactor = 1.2,
                Rooms = new[] { new LoadRoomIn { Id = "R1", Name = "Phòng ngủ", Kind = "room", AreaM2 = 20.5, RoomTypeOverride = "office" } }
            };
            string json = LoadCore.ToJson(request);
            Check.Contains("version", json, "\"version\":1");
            Check.Contains("table", json, "\"table\":\"heat_load_table.json\"");
            Check.Contains("safety", json, "\"safety_factor\":1.2");
            Check.Contains("area uses a dot", json, "\"area_m2\":20.5");
            Check.Contains("override", json, "\"room_type_override\":\"office\"");
            Check.True("no override when unset", !LoadCore.ToJson(new LoadRequest { Rooms = new[] { new LoadRoomIn { Id = "R" } } }).Contains("room_type_override"));
        }

        private static void ParseResult()
        {
            LoadResult result = LoadCore.ParseResult(SampleResult);
            Check.True("ok", result.IsOk);
            Check.Equal("rooms", 4, result.Rooms.Length);
            Check.Equal("type", "hotel", result.Rooms[1].RoomType);
            Check.Near("kw", 4.675, result.Rooms[1].LoadKw);
            Check.False("wc not cooled", result.Rooms[2].Cooled);
            Check.Near("total", 7.975, result.TotalKw);
            Check.Equal("unicode name", "PHÒNG NGỦ", result.Rooms[0].Name);
        }

        private static void ParseError()
        {
            LoadResult result = LoadCore.ParseResult("{\"version\":1,\"status\":\"error\",\"message\":\"Table file not found: x\",\"rooms\":[],\"total_kw\":0.0,\"warnings\":[]}");
            Check.False("not ok", result.IsOk);
            Check.Contains("message", result.Message, "not found");
            Check.Equal("no rooms", 0, result.Rooms.Length);
        }

        private static void ParseInvalid()
        {
            Check.Case("bad json throws FormatException", () =>
            {
                try
                {
                    LoadCore.ParseResult("{not json");
                    Check.Fail("bad json", "FormatException", "no exception");
                }
                catch (FormatException)
                {
                }
            });
            Check.Case("empty throws FormatException", () =>
            {
                try
                {
                    LoadCore.ParseResult("");
                    Check.Fail("empty", "FormatException", "no exception");
                }
                catch (FormatException)
                {
                }
            });
        }

        private static void TableFile()
        {
            LoadTable table = LoadCore.ParseTable(File.ReadAllText(TablePath()));
            Check.True("has bedroom", table.Types.ContainsKey("bedroom"));
            Check.True("has other", table.Types.ContainsKey("other"));
            Check.Near("safety", 1.1, table.SafetyFactor);
            Check.False("wc not cooled", table.Types["wc"].Cooled);
            Check.Equal("first key keeps file order", "wc", table.Keys()[0]);
            Check.Equal("vietnamese label", "Phòng ngủ", table.Types["bedroom"].LabelVi);
            Check.True("keywords", Array.IndexOf(table.Types["hotel"].Keywords, "sup") >= 0);

            LoadTable back = LoadCore.ParseTable(LoadCore.TableToJson(table));
            Check.Equal("round trip keys", string.Join(",", table.Keys()), string.Join(",", back.Keys()));
            Check.Near("round trip w", table.Types["office"].WPerM2, back.Types["office"].WPerM2);
            back.Types["office"].WPerM2 = 99;
            Check.Near("clone is independent", table.Types["office"].WPerM2 == 99 ? -1 : table.Types["office"].WPerM2, table.Types["office"].WPerM2);

            Check.Case("empty types throws", () =>
            {
                try
                {
                    LoadCore.ParseTable("{\"version\":1,\"types\":{}}");
                    Check.Fail("empty types", "FormatException", "no exception");
                }
                catch (FormatException)
                {
                }
            });
        }

        // The C# preview and the Python reference must agree: same formula over the sample rooms.
        private static void FormulaMatchesPython()
        {
            LoadTable table = LoadCore.ParseTable(File.ReadAllText(TablePath()));
            LoadResult python = LoadCore.ParseResult(SampleResult);
            double total = 0;
            foreach (LoadRoomOut room in python.Rooms)
            {
                LoadTypeDef def = table.Types[room.RoomType];
                bool shaft = room.RoomType == "shaft";
                double kw = LoadCore.Compute(room.AreaM2, shaft ? 0 : def.WPerM2, def.Cooled && !shaft, python.SafetyFactor);
                Check.Near("kw " + room.Id, room.LoadKw, kw, 1e-9);
                total += kw;
            }

            Check.Near("total", python.TotalKw, total, 1e-9);
            Check.Near("not cooled is zero", 0, LoadCore.Compute(50, 150, false, 1.1));
        }

        private static void Labels()
        {
            Check.Equal("append", "A\\P20.0 m²\\P3.3 kW", LoadCore.LabelWithLoad("A\\P20.0 m²", 3.3, true));
            Check.Equal("replace on rerun", "A\\P20.0 m²\\P4.0 kW", LoadCore.LabelWithLoad("A\\P20.0 m²\\P3.3 kW", 3.96, true));
            Check.Equal("twice stays one line", "A\\P20.0 m²\\P5.5 kW",
                LoadCore.LabelWithLoad(LoadCore.LabelWithLoad("A\\P20.0 m²", 3.3, true), 5.5, true));
            Check.Equal("not cooled removes line", "A\\P20.0 m²", LoadCore.LabelWithLoad("A\\P20.0 m²\\P3.3 kW", 0, false));
            Check.Equal("one decimal, dot", "12.3", LoadCore.KwText(12.34));
            Check.Equal("null contents", "\\P1.0 kW", LoadCore.LabelWithLoad(null, 1, true));
        }

        private static void CsvText()
        {
            LoadResult result = LoadCore.ParseResult(SampleResult);
            string csv = LoadCore.Csv(result.Rooms, result.TotalKw);
            string[] lines = csv.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            Check.Equal("rows", 6, lines.Length);
            Check.Equal("header", "id,name,type,area_m2,w_per_m2,load_kw,total_kw", lines[0]);
            Check.Equal("row", "R2,SUP.04,hotel,25.00,170,4.675,", lines[2]);
            Check.Equal("total", "TOTAL,,,,,,7.975", lines[5]);
            LoadRoomOut quoted = new LoadRoomOut { Id = "R9", Name = "a,\"b\"", RoomType = "other", AreaM2 = 1, WPerM2 = 180, LoadKw = 0.2 };
            Check.Contains("quoting", LoadCore.Csv(new[] { quoted }, 0.2), "R9,\"a,\"\"b\"\"\",other");
        }

        private static void FindScript()
        {
            string root = Path.Combine(Path.GetTempPath(), "LoadCoreTests_find_" + Guid.NewGuid().ToString("N"));
            try
            {
                string deep = Path.Combine(root, "a", "b");
                Directory.CreateDirectory(deep);
                Check.Equal("none", (string)null, LoadCore.FindScript(deep));
                File.WriteAllText(Path.Combine(root, LoadCore.ScriptName), "");
                Check.Equal("found above", Path.Combine(root, LoadCore.ScriptName), LoadCore.FindScript(deep));
                Check.Equal("venv", Path.Combine(root, "venv", "Scripts", "python.exe"),
                    LoadCore.DefaultPython(Path.Combine(root, LoadCore.ScriptName)));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
