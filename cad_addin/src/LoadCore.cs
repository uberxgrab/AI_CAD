using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace HvacAiCadAddin.Load
{
    // AutoCAD-free core of HVACLOAD (area-based cooling load): the remembered dialog values, the JSON
    // contract with the Python back end (heat_load.py, run as a separate process, never inside
    // AutoCAD), the load formula, the label text and the CSV summary. A console test project links
    // this file.
    //
    // Contract, version 1 (see heat_load.py):
    //   request.json {version, out, table, safety_factor, rooms[{id, name, kind, area_m2,
    //                 room_type_override?}]}
    //   load.json    {version, status "ok"|"error", message?, safety_factor, rooms[{id, name,
    //                 room_type, label_vi, cooled, w_per_m2, area_m2, load_kw}], total_kw, warnings[]}
    //   heat_load_table.json {version, note, safety_factor, types{key: {w_per_m2, cooled, label_vi,
    //                 keywords[]}}}; the key order is the keyword priority.
    //
    // Extended data written on each room polyline (the contract tier 2 reads):
    //   [1001 HVACAI_LOAD][1000 room_type][1040 w_per_m2][1040 load_kw][1040 safety_factor]

    /// <summary>HVACLOAD values remembered between runs.</summary>
    public sealed class LoadSettings
    {
        /// <summary>Extended-data application name written on each room polyline.</summary>
        public const string XDataApp = "HVACAI_LOAD";

        public double SafetyFactor = 1.1;

        /// <summary>Write the edited W/m2 values back to heat_load_table.json on OK.</summary>
        public bool SaveTable;

        /// <summary>python.exe; empty means the repository's venv next to heat_load.py.</summary>
        public string PythonExe = "";

        /// <summary>heat_load.py; empty means found by walking up from the add-in folder.</summary>
        public string ScriptPath = "";

        public bool IsValid(out string error)
        {
            error = null;
            if (double.IsNaN(SafetyFactor) || double.IsInfinity(SafetyFactor) || SafetyFactor < 0.5 || SafetyFactor > 3)
                error = "The safety factor must be between 0.5 and 3.";
            return error == null;
        }

        public string Serialize()
        {
            StringBuilder text = new StringBuilder();
            Add(text, "safety", SafetyFactor.ToString("R", CultureInfo.InvariantCulture));
            Add(text, "savetable", SaveTable ? "1" : "0");
            Add(text, "python", OneLine(PythonExe));
            Add(text, "script", OneLine(ScriptPath));
            return text.ToString();
        }

        /// <summary>Never throws: unknown keys and bad values keep the defaults.</summary>
        public static LoadSettings Deserialize(string text)
        {
            LoadSettings settings = new LoadSettings();
            if (string.IsNullOrEmpty(text))
                return settings;

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                int equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;
                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1);
                double number;
                switch (key)
                {
                    case "safety":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                            && !double.IsNaN(number) && !double.IsInfinity(number) && number >= 0.5 && number <= 3)
                            settings.SafetyFactor = number;
                        break;
                    case "savetable":
                        settings.SaveTable = value.Trim() == "1";
                        break;
                    case "python":
                        settings.PythonExe = value.Trim();
                        break;
                    case "script":
                        settings.ScriptPath = value.Trim();
                        break;
                }
            }

            return settings;
        }

        private static string OneLine(string value)
        {
            return (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static void Add(StringBuilder text, string key, string value)
        {
            text.Append(key).Append('=').Append(value).Append('\n');
        }
    }

    [DataContract]
    public sealed class LoadTypeDef
    {
        [DataMember(Name = "w_per_m2", Order = 0)] public double WPerM2;
        [DataMember(Name = "cooled", Order = 1)] public bool Cooled = true;
        [DataMember(Name = "label_vi", Order = 2)] public string LabelVi;
        [DataMember(Name = "keywords", Order = 3)] public string[] Keywords = new string[0];
    }

    /// <summary>heat_load_table.json. Types keep the file order (keyword priority).</summary>
    [DataContract]
    public sealed class LoadTable
    {
        [DataMember(Name = "version", Order = 0)] public int Version = 1;
        [DataMember(Name = "note", Order = 1)] public string Note;
        [DataMember(Name = "safety_factor", Order = 2)] public double SafetyFactor = 1.0;
        [DataMember(Name = "types", Order = 3)] public Dictionary<string, LoadTypeDef> Types = new Dictionary<string, LoadTypeDef>();

        /// <summary>Type keys in table order.</summary>
        public List<string> Keys()
        {
            return new List<string>(Types.Keys);
        }

        public LoadTable Clone()
        {
            return LoadCore.ParseTable(LoadCore.TableToJson(this));
        }
    }

    [DataContract]
    public sealed class LoadRoomIn
    {
        [DataMember(Name = "id", Order = 0)] public string Id;
        [DataMember(Name = "name", Order = 1)] public string Name;
        [DataMember(Name = "kind", Order = 2)] public string Kind;
        [DataMember(Name = "area_m2", Order = 3)] public double AreaM2;
        [DataMember(Name = "room_type_override", Order = 4, EmitDefaultValue = false)] public string RoomTypeOverride;
    }

    [DataContract]
    public sealed class LoadRequest
    {
        [DataMember(Name = "version", Order = 0)] public int Version = 1;
        [DataMember(Name = "out", Order = 1)] public string Out = "load.json";
        [DataMember(Name = "table", Order = 2)] public string Table = "heat_load_table.json";
        [DataMember(Name = "safety_factor", Order = 3, EmitDefaultValue = false)] public double? SafetyFactor;
        [DataMember(Name = "rooms", Order = 4)] public LoadRoomIn[] Rooms = new LoadRoomIn[0];
    }

    [DataContract]
    public sealed class LoadRoomOut
    {
        [DataMember(Name = "id")] public string Id;
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "room_type")] public string RoomType;
        [DataMember(Name = "label_vi")] public string LabelVi;
        [DataMember(Name = "cooled")] public bool Cooled;
        [DataMember(Name = "w_per_m2")] public double WPerM2;
        [DataMember(Name = "area_m2")] public double AreaM2;
        [DataMember(Name = "load_kw")] public double LoadKw;
    }

    [DataContract]
    public sealed class LoadResult
    {
        [DataMember(Name = "version")] public int Version;
        [DataMember(Name = "status")] public string Status;
        [DataMember(Name = "message")] public string Message;
        [DataMember(Name = "safety_factor")] public double SafetyFactor;
        [DataMember(Name = "rooms")] public LoadRoomOut[] Rooms;
        [DataMember(Name = "total_kw")] public double TotalKw;
        [DataMember(Name = "warnings")] public string[] Warnings;

        public bool IsOk
        {
            get { return string.Equals(Status, "ok", StringComparison.Ordinal); }
        }
    }

    public static class LoadCore
    {
        public const string ScriptName = "heat_load.py";
        public const string TableName = "heat_load_table.json";

        private static readonly DataContractJsonSerializerSettings DictionarySettings =
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true };

        /// <summary>Load in kW: area x W/m2 x safety factor / 1000, 0 when the type is not cooled.</summary>
        public static double Compute(double areaM2, double wPerM2, bool cooled, double safety)
        {
            return cooled ? Math.Round(areaM2 * wPerM2 * safety / 1000.0, 4, MidpointRounding.AwayFromZero) : 0.0;
        }

        public static string ToJson(LoadRequest request)
        {
            return Write(typeof(LoadRequest), request, false);
        }

        public static string TableToJson(LoadTable table)
        {
            return Write(typeof(LoadTable), table, true);
        }

        private static string Write(Type type, object value, bool indent)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(type, DictionarySettings);
            using (MemoryStream stream = new MemoryStream())
            {
                if (indent)
                {
                    using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true, "  "))
                    {
                        serializer.WriteObject(writer, value);
                    }
                }
                else
                {
                    serializer.WriteObject(stream, value);
                }

                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        /// <summary>Parses the W/m2 table; throws <see cref="FormatException"/> when it is unusable.</summary>
        public static LoadTable ParseTable(string json)
        {
            LoadTable table = Read<LoadTable>(json, "The table file");
            if (table.Types == null || table.Types.Count == 0)
                throw new FormatException("The table file has no types.");
            foreach (KeyValuePair<string, LoadTypeDef> pair in table.Types)
            {
                if (pair.Value == null)
                    throw new FormatException("The table file has an empty type: " + pair.Key);
                if (pair.Value.Keywords == null)
                    pair.Value.Keywords = new string[0];
                if (string.IsNullOrEmpty(pair.Value.LabelVi))
                    pair.Value.LabelVi = pair.Key;
            }

            return table;
        }

        /// <summary>Parses load.json; throws <see cref="FormatException"/> when it is not valid JSON.</summary>
        public static LoadResult ParseResult(string json)
        {
            LoadResult result = Read<LoadResult>(json, "The result file");
            if (result.Rooms == null)
                result.Rooms = new LoadRoomOut[0];
            if (result.Warnings == null)
                result.Warnings = new string[0];
            return result;
        }

        private static T Read<T>(string json, string what) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new FormatException(what + " is empty.");
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T), DictionarySettings);
            try
            {
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json.TrimStart('﻿'))))
                {
                    T value = serializer.ReadObject(stream) as T;
                    if (value == null)
                        throw new FormatException(what + " is empty.");
                    return value;
                }
            }
            catch (SerializationException ex)
            {
                throw new FormatException(what + " is not valid JSON: " + ex.Message, ex);
            }
        }

        private static readonly Regex LoadLine = new Regex(@"\\P[^\\]*? kW\s*$", RegexOptions.CultureInvariant);

        /// <summary>One decimal, invariant culture: `12.3`.</summary>
        public static string KwText(double kw)
        {
            return kw.ToString("0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The room label with the load line: `name\Parea m2\P12.3 kW`. An earlier kW line is replaced,
        /// never appended twice. A room that is not cooled gets no kW line (an earlier one is removed).
        /// </summary>
        public static string LabelWithLoad(string contents, double loadKw, bool cooled)
        {
            string text = contents ?? "";
            while (LoadLine.IsMatch(text))
                text = LoadLine.Replace(text, "");
            return cooled ? text + "\\P" + KwText(loadKw) + " kW" : text;
        }

        /// <summary>CSV summary: id, name, type, area, W/m2, kW, and the total on the last row.</summary>
        public static string Csv(IList<LoadRoomOut> rooms, double totalKw)
        {
            StringBuilder csv = new StringBuilder();
            csv.Append("id,name,type,area_m2,w_per_m2,load_kw,total_kw\r\n");
            foreach (LoadRoomOut room in rooms)
            {
                csv.Append(Field(room.Id)).Append(',').Append(Field(room.Name)).Append(',')
                    .Append(Field(room.RoomType)).Append(',')
                    .Append(room.AreaM2.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
                    .Append(room.WPerM2.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                    .Append(room.LoadKw.ToString("0.000", CultureInfo.InvariantCulture)).Append(",\r\n");
            }

            csv.Append("TOTAL,,,,,,").Append(totalKw.ToString("0.000", CultureInfo.InvariantCulture)).Append("\r\n");
            return csv.ToString();
        }

        private static string Field(string value)
        {
            string text = value ?? "";
            if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
                return "\"" + text.Replace("\"", "\"\"") + "\"";
            return text;
        }

        /// <summary>heat_load.py in <paramref name="startDir"/> or one of its parents, or null.</summary>
        public static string FindScript(string startDir, int maxLevels = 8)
        {
            DirectoryInfo dir = string.IsNullOrEmpty(startDir) ? null : new DirectoryInfo(startDir);
            for (int level = 0; dir != null && level <= maxLevels; level++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, ScriptName);
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        /// <summary>The venv interpreter next to the script: &lt;repo&gt;\venv\Scripts\python.exe.</summary>
        public static string DefaultPython(string scriptPath)
        {
            if (string.IsNullOrEmpty(scriptPath))
                return null;
            return Path.Combine(Path.GetDirectoryName(scriptPath), "venv", "Scripts", "python.exe");
        }
    }
}
