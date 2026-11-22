using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace HvacAiCadAddin.Rooms
{
    // AutoCAD-free core of HVACROOMS: the remembered dialog values, the JSON contract with the Python
    // back end (arch_rooms.py, run as a separate process, never inside AutoCAD), the wall-layer
    // suggestions and the room label text. A console test project links this file.
    //
    // Contract, version 1 (see arch_rooms.py):
    //   request.json  {version, dxf, out, wall_layers[], region[[x,y]...]|null, method, max_opening,
    //                  min_room_area_m2, min_room_width}
    //   rooms.json    {version, status "ok"|"error", message?, rooms[{id, kind "room"|"shaft", name?,
    //                  area_m2, label_area_m2?, texts[], label_point[x,y], polygon[[x,y]...]}],
    //                  envelope[[[x,y]...]], stats{...}, warnings[]}
    // Coordinates are WCS drawing units (mm); polygons are open rings (first vertex not repeated).

    /// <summary>HVACROOMS values remembered between runs.</summary>
    public sealed class RoomsSettings
    {
        /// <summary>Layer of the room boundary polylines this command writes.</summary>
        public const string RoomLayer = "HVAC-ROOM";

        /// <summary>Layer of the room labels.</summary>
        public const string TextLayer = "HVAC-ROOM-TEXT";

        /// <summary>Lines the user draws on this layer always count as walls (to split open spaces).</summary>
        public const string SplitLayer = "HVAC-ROOM-SPLIT";

        /// <summary>Extended-data application name of the polylines and labels this command writes.</summary>
        public const string XDataApp = "HVACAI_ROOM";

        public const short RoomLayerColor = 3;
        public const short TextLayerColor = 2;
        public const short SplitLayerColor = 1;

        /// <summary>Colour of shaft boundaries (rooms take the layer colour).</summary>
        public const short ShaftColor = 6;

        public List<string> WallLayers = new List<string>();

        /// <summary>Widest opening bridged between two facing wall ends (mm).</summary>
        public double MaxOpening = 1800;

        public double MinRoomAreaM2 = 1.0;

        /// <summary>Faces narrower than this are wall cavities, not rooms (mm).</summary>
        public double MinRoomWidth = 400;

        public double TextHeight = 250;

        /// <summary>python.exe; empty means the repository's venv next to arch_rooms.py.</summary>
        public string PythonExe = "";

        /// <summary>arch_rooms.py; empty means found by walking up from the add-in folder.</summary>
        public string ScriptPath = "";

        public bool IsValid(out string error)
        {
            error = null;
            if (!InRange(MaxOpening, 100, 10000))
                error = "The widest opening must be between 100 and 10000 mm.";
            else if (!InRange(MinRoomAreaM2, 0.1, 1000))
                error = "The smallest room must be between 0.1 and 1000 m2.";
            else if (!InRange(MinRoomWidth, 50, 5000))
                error = "The narrowest room must be between 50 and 5000 mm.";
            else if (!InRange(TextHeight, 1, 100000))
                error = "The text height must be between 1 and 100000.";
            else if (WallLayers.Count == 0)
                error = "Tick at least one wall layer.";
            return error == null;
        }

        private static bool InRange(double value, double low, double high)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= low && value <= high;
        }

        public string Serialize()
        {
            StringBuilder text = new StringBuilder();
            Add(text, "walls", string.Join("|", WallLayers)); // '|' cannot appear in a layer name
            Add(text, "maxopening", Num(MaxOpening));
            Add(text, "minarea", Num(MinRoomAreaM2));
            Add(text, "minwidth", Num(MinRoomWidth));
            Add(text, "textheight", Num(TextHeight));
            Add(text, "python", OneLine(PythonExe));
            Add(text, "script", OneLine(ScriptPath));
            return text.ToString();
        }

        /// <summary>Never throws: unknown keys and bad values keep the defaults.</summary>
        public static RoomsSettings Deserialize(string text)
        {
            RoomsSettings settings = new RoomsSettings();
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
                    case "walls":
                        settings.WallLayers = new List<string>();
                        foreach (string name in value.Split('|'))
                        {
                            if (name.Trim().Length > 0)
                                settings.WallLayers.Add(name.Trim());
                        }
                        break;
                    case "maxopening":
                        if (TryNumber(value, out number) && InRange(number, 100, 10000))
                            settings.MaxOpening = number;
                        break;
                    case "minarea":
                        if (TryNumber(value, out number) && InRange(number, 0.1, 1000))
                            settings.MinRoomAreaM2 = number;
                        break;
                    case "minwidth":
                        if (TryNumber(value, out number) && InRange(number, 50, 5000))
                            settings.MinRoomWidth = number;
                        break;
                    case "textheight":
                        if (TryNumber(value, out number) && InRange(number, 1, 100000))
                            settings.TextHeight = number;
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

        private static bool TryNumber(string value, out double number)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                && !double.IsNaN(number) && !double.IsInfinity(number);
        }

        private static void Add(StringBuilder text, string key, string value)
        {
            text.Append(key).Append('=').Append(value).Append('\n');
        }

        private static string Num(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    [DataContract]
    public sealed class RoomsRequest
    {
        [DataMember(Name = "version", Order = 0)] public int Version = 1;
        [DataMember(Name = "dxf", Order = 1)] public string Dxf = "snapshot.dxf";
        [DataMember(Name = "out", Order = 2)] public string Out = "rooms.json";
        [DataMember(Name = "wall_layers", Order = 3)] public string[] WallLayers = new string[0];
        [DataMember(Name = "region", Order = 4)] public double[][] Region;
        [DataMember(Name = "method", Order = 5)] public string Method = "polygonize";
        [DataMember(Name = "max_opening", Order = 6)] public double MaxOpening = 1800;
        [DataMember(Name = "min_room_area_m2", Order = 7)] public double MinRoomAreaM2 = 1.0;
        [DataMember(Name = "min_room_width", Order = 8)] public double MinRoomWidth = 400;

        public static RoomsRequest From(RoomsSettings settings, double[][] region)
        {
            List<string> layers = new List<string>(settings.WallLayers);
            return new RoomsRequest
            {
                WallLayers = layers.ToArray(),
                Region = region,
                MaxOpening = settings.MaxOpening,
                MinRoomAreaM2 = settings.MinRoomAreaM2,
                MinRoomWidth = settings.MinRoomWidth
            };
        }
    }

    [DataContract]
    public sealed class RoomItem
    {
        [DataMember(Name = "id")] public string Id;
        [DataMember(Name = "kind")] public string Kind;
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "area_m2")] public double AreaM2;
        [DataMember(Name = "label_area_m2")] public double? LabelAreaM2;
        [DataMember(Name = "texts")] public string[] Texts;
        [DataMember(Name = "label_point")] public double[] LabelPoint;
        [DataMember(Name = "polygon")] public double[][] Polygon;

        public bool IsShaft
        {
            get { return string.Equals(Kind, "shaft", StringComparison.Ordinal); }
        }
    }

    [DataContract]
    public sealed class RoomsStats
    {
        [DataMember(Name = "wall_curves")] public int WallCurves;
        [DataMember(Name = "bridges")] public int Bridges;
        [DataMember(Name = "x_marks")] public int XMarks;
        [DataMember(Name = "rooms")] public int Rooms;
        [DataMember(Name = "shafts")] public int Shafts;
        [DataMember(Name = "total_area_m2")] public double TotalAreaM2;
    }

    [DataContract]
    public sealed class RoomsResult
    {
        [DataMember(Name = "version")] public int Version;
        [DataMember(Name = "status")] public string Status;
        [DataMember(Name = "message")] public string Message;
        [DataMember(Name = "rooms")] public RoomItem[] Rooms;
        [DataMember(Name = "stats")] public RoomsStats Stats;
        [DataMember(Name = "warnings")] public string[] Warnings;

        public bool IsOk
        {
            get { return string.Equals(Status, "ok", StringComparison.Ordinal); }
        }
    }

    public static class RoomsExchangeCore
    {
        public const string ScriptName = "arch_rooms.py";

        // Same hints as arch_extract.DEFAULT_ARCH_CONFIG, matched on the accent-free, lower-cased
        // layer name without its bound-xref prefix: walls, columns, glass, concrete, doors, windows.
        private static readonly Regex WallHint = new Regex(
            @"\b(tuong|walls?|cot|columns?|vach|kinh|glass|be ?tong|cua|doors?|windows?)\b|墙|柱|门|窗",
            RegexOptions.CultureInvariant);

        private static readonly Regex NotWallHint = new Regex(
            @"hatch|\bdim\b|\btext\b|kich thuoc", RegexOptions.CultureInvariant);

        public static string ToJson(RoomsRequest request)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(RoomsRequest));
            using (MemoryStream stream = new MemoryStream())
            {
                serializer.WriteObject(stream, request);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        /// <summary>Parses rooms.json; throws <see cref="FormatException"/> when it is not valid JSON.</summary>
        public static RoomsResult ParseResult(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new FormatException("The result file is empty.");
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(RoomsResult));
            try
            {
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json.TrimStart('﻿'))))
                {
                    RoomsResult result = (RoomsResult)serializer.ReadObject(stream);
                    if (result == null)
                        throw new FormatException("The result file is empty.");
                    if (result.Rooms == null)
                        result.Rooms = new RoomItem[0];
                    if (result.Warnings == null)
                        result.Warnings = new string[0];
                    return result;
                }
            }
            catch (SerializationException ex)
            {
                throw new FormatException("The result file is not valid JSON: " + ex.Message, ex);
            }
        }

        /// <summary>A room is usable when it has at least three finite vertices.</summary>
        public static bool IsUsable(RoomItem room)
        {
            if (room == null || room.Polygon == null || room.Polygon.Length < 3)
                return false;
            foreach (double[] point in room.Polygon)
            {
                if (point == null || point.Length < 2 || !Finite(point[0]) || !Finite(point[1]))
                    return false;
            }

            return true;
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>Layer names the dialog ticks by default.</summary>
        public static bool SuggestWall(string layer)
        {
            string key = LayerKey(layer);
            return key.Length > 0 && !NotWallHint.IsMatch(key) && WallHint.IsMatch(key);
        }

        /// <summary>`XR plan$0$CJSC.2-Tuong` becomes `cjsc 2 tuong`.</summary>
        public static string LayerKey(string layer)
        {
            string name = layer ?? "";
            int prefix = name.LastIndexOf("$0$", StringComparison.Ordinal);
            if (prefix >= 0)
                name = name.Substring(prefix + 3);
            string plain = RemoveAccents(name).ToLowerInvariant();
            return Regex.Replace(plain, @"[^\w]+", " ", RegexOptions.CultureInvariant).Trim();
        }

        public static string RemoveAccents(string text)
        {
            string decomposed = (text ?? "").Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
            StringBuilder plain = new StringBuilder(decomposed.Length);
            foreach (char ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    plain.Append(ch);
            }

            return plain.ToString().Normalize(NormalizationForm.FormC);
        }

        /// <summary>Layers the dialog never lists: the command's own output.</summary>
        public static bool IsOwnLayer(string layer)
        {
            return string.Equals(layer, RoomsSettings.RoomLayer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layer, RoomsSettings.TextLayer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layer, RoomsSettings.SplitLayer, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>MText contents: the name (or HKT / the id) and the area on a second line.</summary>
        public static string LabelText(RoomItem room)
        {
            string name = room.Name;
            if (string.IsNullOrWhiteSpace(name))
                name = room.IsShaft ? "HKT" : room.Id;
            string area = room.AreaM2.ToString("0.0", CultureInfo.InvariantCulture) + " m²";
            return EscapeMText(name.Trim()) + "\\P" + area;
        }

        public static string EscapeMText(string text)
        {
            return (text ?? "").Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}")
                .Replace("\r", " ").Replace("\n", " ");
        }

        /// <summary>An axis-aligned box around the points, as a closed-ring-free polygon, or null.</summary>
        public static double[][] RegionFromPoints(IList<double[]> points)
        {
            if (points == null || points.Count < 2)
                return null;
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (double[] p in points)
            {
                minX = Math.Min(minX, p[0]);
                minY = Math.Min(minY, p[1]);
                maxX = Math.Max(maxX, p[0]);
                maxY = Math.Max(maxY, p[1]);
            }

            if (maxX - minX < 1e-6 || maxY - minY < 1e-6)
                return null;
            return new[]
            {
                new[] { minX, minY }, new[] { maxX, minY }, new[] { maxX, maxY }, new[] { minX, maxY }
            };
        }

        /// <summary>arch_rooms.py in <paramref name="startDir"/> or one of its parents, or null.</summary>
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

        /// <summary>Command line arguments for the script, quoted for Process.Start.</summary>
        public static string Arguments(string scriptPath, string requestPath)
        {
            return Quote(scriptPath) + " --request " + Quote(requestPath);
        }

        private static string Quote(string path)
        {
            return "\"" + (path ?? "").Replace("\"", "") + "\"";
        }
    }
}
