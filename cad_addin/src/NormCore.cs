using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using HvacAiCadAddin.Takeoff;

namespace HvacAiCadAddin.Norm
{
    // AutoCAD-free core of HVACNORM: the remembered dialog values, the JSON contract with the Python back
    // end (mep_normalize.py, run as a separate process, never inside AutoCAD), the project map files shared
    // with the mep_map MCP tool, and the planner that turns the Python result into per-entity actions.
    // The console tests link this file.
    //
    // Contract, version 1 (see mep_normalize.run_request):
    //   request.json {version, dxf, out, map?{name, layers{layer: {system, material?, prefix?}}}, region?}
    //   norm.json    {version, status, message?, stats{...}, warnings[], issue_counts{}, issues[],
    //                 entities[{handle, layer, type, system, kind, in_block, overrides[], fixes[],
    //                 groups[{start, end, from_mm, to_mm, length_mm, size?, size_text, material, confidence,
    //                 duplicate, label, target_layer, target_in_drawing, target_in_template, boq_prefix,
    //                 system, path[[x,y]]}]}], map{}, map_proposed, proposal[]}
    // Handles are those of the snapshot DXF; the command maps them back to the drawing.
    //
    // User decisions (2026-11-23): no new layers; a pipe whose material is the BOQ material moves to its
    // existing EIA_1 layer (imported from the template when the drawing lacks it, never invented); any
    // other material keeps its layer and gets a size tag with a BOQ prefix; colour/linetype/lineweight
    // overrides are errors and are reset to ByLayer. Single-line ducts keep their layer and get a size
    // tag (HVACDUCTLINE draws them as duct blocks later; moving the lines onto the duct block layers would
    // count them twice).

    public sealed class NormSettings
    {
        public const string Confidences = "high|medium|low";

        /// <summary>The project map used last time (a file in the maps folder); empty = propose one.</summary>
        public string MapName = "";

        /// <summary>Lowest confidence whose size is applied: high, medium or low.</summary>
        public string MinConfidence = "low";

        public bool FixOverrides = true;
        public bool EraseDuplicates = true;
        public bool MoveToStandard = true;

        public string PythonExe = "";
        public string ScriptPath = "";

        public string Serialize()
        {
            StringBuilder text = new StringBuilder();
            Add(text, "map", MapName);
            Add(text, "min", MinConfidence);
            Add(text, "overrides", FixOverrides ? "1" : "0");
            Add(text, "duplicates", EraseDuplicates ? "1" : "0");
            Add(text, "move", MoveToStandard ? "1" : "0");
            Add(text, "python", PythonExe);
            Add(text, "script", ScriptPath);
            return text.ToString();
        }

        /// <summary>Never throws: unknown keys and bad values keep the defaults.</summary>
        public static NormSettings Deserialize(string text)
        {
            NormSettings settings = new NormSettings();
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;
                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Trim();
                switch (key)
                {
                    case "map":
                        settings.MapName = NormMapStore.IsValidName(value) ? value : "";
                        break;
                    case "min":
                        if (NormPlanner.IsConfidence(value))
                            settings.MinConfidence = value;
                        break;
                    case "overrides":
                        settings.FixOverrides = value != "0";
                        break;
                    case "duplicates":
                        settings.EraseDuplicates = value != "0";
                        break;
                    case "move":
                        settings.MoveToStandard = value != "0";
                        break;
                    case "python":
                        settings.PythonExe = value;
                        break;
                    case "script":
                        settings.ScriptPath = value;
                        break;
                }
            }

            return settings;
        }

        private static void Add(StringBuilder text, string key, string value)
        {
            text.Append(key).Append('=').Append((value ?? "").Replace('\r', ' ').Replace('\n', ' ')).Append('\n');
        }
    }

    // ------------------------------------------------------------------------------------------ contract

    [DataContract]
    public sealed class NormMapEntry
    {
        [DataMember(Name = "system", Order = 0)] public string System;
        [DataMember(Name = "material", Order = 1, EmitDefaultValue = false)] public string Material;
        [DataMember(Name = "prefix", Order = 2, EmitDefaultValue = false)] public string Prefix;
    }

    [DataContract]
    public sealed class NormMap
    {
        [DataMember(Name = "version", Order = 0)] public int Version = 1;
        [DataMember(Name = "name", Order = 1)] public string Name = "";
        [DataMember(Name = "saved", Order = 2, EmitDefaultValue = false)] public string Saved;
        [DataMember(Name = "layers", Order = 3)] public Dictionary<string, NormMapEntry> Layers =
            new Dictionary<string, NormMapEntry>();

        /// <summary>Layer -> system code, upper case, without blank entries.</summary>
        public Dictionary<string, string> Systems()
        {
            Dictionary<string, string> systems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, NormMapEntry> pair in Layers ?? new Dictionary<string, NormMapEntry>())
            {
                string code = pair.Value == null ? null : (pair.Value.System ?? "").Trim().ToUpperInvariant();
                if (!string.IsNullOrEmpty(code))
                    systems[pair.Key] = code;
            }

            return systems;
        }
    }

    [DataContract]
    public sealed class NormRequest
    {
        [DataMember(Name = "version", Order = 0)] public int Version = 1;
        [DataMember(Name = "dxf", Order = 1)] public string Dxf = "snapshot.dxf";
        [DataMember(Name = "out", Order = 2)] public string Out = "norm.json";
        [DataMember(Name = "map", Order = 3, EmitDefaultValue = false)] public NormMap Map;
        [DataMember(Name = "region", Order = 4, EmitDefaultValue = false)] public double[][] Region;
    }

    [DataContract]
    public sealed class NormSize
    {
        [DataMember(Name = "kind")] public string Kind;
        [DataMember(Name = "system")] public string System;
        [DataMember(Name = "material")] public string Material;
        [DataMember(Name = "size")] public double? Size;
        [DataMember(Name = "prefix")] public string Prefix;
        [DataMember(Name = "pair")] public double[] Pair;
        [DataMember(Name = "width")] public double? Width;
        [DataMember(Name = "height")] public double? Height;
        [DataMember(Name = "slope")] public double? Slope;
    }

    [DataContract]
    public sealed class NormGroup
    {
        [DataMember(Name = "start")] public double[] Start;
        [DataMember(Name = "end")] public double[] End;
        [DataMember(Name = "from_mm")] public double FromMm;
        [DataMember(Name = "to_mm")] public double ToMm;
        [DataMember(Name = "length_mm")] public double LengthMm;
        [DataMember(Name = "size")] public NormSize Size;
        [DataMember(Name = "size_text")] public string SizeText;
        [DataMember(Name = "material")] public string Material;
        [DataMember(Name = "confidence")] public string Confidence;
        [DataMember(Name = "duplicate")] public bool Duplicate;
        [DataMember(Name = "label")] public string Label;
        [DataMember(Name = "target_layer")] public string TargetLayer;
        [DataMember(Name = "target_in_drawing")] public bool TargetInDrawing;
        [DataMember(Name = "target_in_template")] public bool TargetInTemplate;
        [DataMember(Name = "boq_prefix")] public string BoqPrefix;
        [DataMember(Name = "system")] public string System;
        [DataMember(Name = "path")] public double[][] Path;
    }

    [DataContract]
    public sealed class NormEntity
    {
        [DataMember(Name = "handle")] public string Handle;
        [DataMember(Name = "layer")] public string Layer;
        [DataMember(Name = "type")] public string Type;
        [DataMember(Name = "system")] public string System;
        [DataMember(Name = "kind")] public string Kind;
        [DataMember(Name = "in_block")] public bool InBlock;
        [DataMember(Name = "overrides")] public string[] Overrides;
        [DataMember(Name = "fixes")] public string[] Fixes;
        [DataMember(Name = "groups")] public NormGroup[] Groups;

        public bool IsDuct
        {
            get { return string.Equals(Kind, "duct", StringComparison.OrdinalIgnoreCase); }
        }
    }

    [DataContract]
    public sealed class NormIssue
    {
        [DataMember(Name = "type")] public string Type;
        [DataMember(Name = "handle")] public string Handle;
        [DataMember(Name = "handles")] public string[] Handles;
        [DataMember(Name = "layer")] public string Layer;
        [DataMember(Name = "point")] public double[] Point;
        [DataMember(Name = "detail")] public string Detail;
        [DataMember(Name = "text")] public string Text;
        [DataMember(Name = "gap_mm")] public double? GapMm;
        [DataMember(Name = "length_mm")] public double? LengthMm;
    }

    [DataContract]
    public sealed class NormStats
    {
        [DataMember(Name = "curves")] public int Curves;
        [DataMember(Name = "length_m")] public double LengthM;
        [DataMember(Name = "duplicate_m")] public double DuplicateM;
        [DataMember(Name = "by_confidence_pct")] public Dictionary<string, double> ByConfidencePct;
        [DataMember(Name = "labels")] public int Labels;
        [DataMember(Name = "labels_used")] public int LabelsUsed;
    }

    [DataContract]
    public sealed class NormProposalRow
    {
        [DataMember(Name = "layer")] public string Layer;
        [DataMember(Name = "proposed_system")] public string ProposedSystem;
        [DataMember(Name = "kind")] public string Kind;
        [DataMember(Name = "label_hits")] public int LabelHits;
        [DataMember(Name = "length_m")] public double LengthM;
        [DataMember(Name = "sample_labels")] public string[] SampleLabels;
    }

    [DataContract]
    public sealed class NormResult
    {
        [DataMember(Name = "version")] public int Version;
        [DataMember(Name = "status")] public string Status;
        [DataMember(Name = "message")] public string Message;
        [DataMember(Name = "stats")] public NormStats Stats;
        [DataMember(Name = "warnings")] public string[] Warnings;
        [DataMember(Name = "issue_counts")] public Dictionary<string, int> IssueCounts;
        [DataMember(Name = "issues")] public NormIssue[] Issues;
        [DataMember(Name = "entities")] public NormEntity[] Entities;
        [DataMember(Name = "map")] public Dictionary<string, NormMapEntry> Map;
        [DataMember(Name = "map_proposed")] public bool MapProposed;
        [DataMember(Name = "proposal")] public NormProposalRow[] Proposal;

        public bool IsOk
        {
            get { return string.Equals(Status, "ok", StringComparison.Ordinal); }
        }
    }

    public static class NormExchangeCore
    {
        public const string ScriptName = "mep_normalize.py";

        private static DataContractJsonSerializerSettings Json
        {
            get { return new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }; }
        }

        public static string ToJson<T>(T value)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T), Json);
            using (MemoryStream stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public static T FromJson<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new FormatException("The file is empty.");
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T), Json);
            try
            {
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json.TrimStart('﻿'))))
                {
                    T value = serializer.ReadObject(stream) as T;
                    if (value == null)
                        throw new FormatException("The file is empty.");
                    return value;
                }
            }
            catch (SerializationException ex)
            {
                throw new FormatException("The file is not valid JSON: " + ex.Message, ex);
            }
        }

        /// <summary>Parses norm.json; missing arrays become empty.</summary>
        public static NormResult ParseResult(string json)
        {
            NormResult result = FromJson<NormResult>(json);
            if (result.Entities == null)
                result.Entities = new NormEntity[0];
            if (result.Issues == null)
                result.Issues = new NormIssue[0];
            if (result.Warnings == null)
                result.Warnings = new string[0];
            if (result.Map == null)
                result.Map = new Dictionary<string, NormMapEntry>();
            if (result.IssueCounts == null)
                result.IssueCounts = new Dictionary<string, int>();
            foreach (NormEntity entity in result.Entities)
            {
                if (entity.Groups == null)
                    entity.Groups = new NormGroup[0];
                if (entity.Overrides == null)
                    entity.Overrides = new string[0];
            }

            return result;
        }

        /// <summary>mep_normalize.py in <paramref name="startDir"/> or one of its parents, or null.</summary>
        public static string FindScript(string startDir, int maxLevels = 8)
        {
            DirectoryInfo dir = string.IsNullOrEmpty(startDir) ? null : new DirectoryInfo(startDir);
            for (int level = 0; dir != null && level <= maxLevels; level++, dir = dir.Parent)
            {
                string candidate = System.IO.Path.Combine(dir.FullName, ScriptName);
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        public static string DefaultPython(string scriptPath)
        {
            return string.IsNullOrEmpty(scriptPath)
                ? null
                : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(scriptPath), "venv", "Scripts", "python.exe");
        }
    }

    /// <summary>The project maps, shared with the mep_map MCP tool: %LOCALAPPDATA%\HvacAiCad\mep_maps\*.json.</summary>
    public static class NormMapStore
    {
        public static string DefaultFolder
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return System.IO.Path.Combine(local, "HvacAiCad", "mep_maps");
            }
        }

        /// <summary>Same rule as aitools/mep.py: 1-60 letters, digits, spaces, '-', '_' or '.'.</summary>
        public static bool IsValidName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 60 || name.Trim() != name || name.Contains(".."))
                return false;
            foreach (char ch in name)
            {
                if (!(char.IsLetterOrDigit(ch) || ch == ' ' || ch == '-' || ch == '_' || ch == '.'))
                    return false;
            }

            return true;
        }

        public static List<string> List(string folder)
        {
            if (!Directory.Exists(folder))
                return new List<string>();
            return Directory.GetFiles(folder, "*.json")
                .Select(System.IO.Path.GetFileNameWithoutExtension)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static NormMap Load(string folder, string name)
        {
            if (!IsValidName(name))
                throw new ArgumentException("Invalid map name: " + name);
            string path = System.IO.Path.Combine(folder, name + ".json");
            NormMap map = NormExchangeCore.FromJson<NormMap>(File.ReadAllText(path, Encoding.UTF8));
            if (map.Layers == null)
                map.Layers = new Dictionary<string, NormMapEntry>();
            return map;
        }

        public static string Save(string folder, string name, IDictionary<string, string> systems)
        {
            if (!IsValidName(name))
                throw new ArgumentException("Invalid map name: " + name);
            NormMap map = FromSystems(name, systems);
            map.Saved = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            Directory.CreateDirectory(folder);
            string path = System.IO.Path.Combine(folder, name + ".json");
            File.WriteAllText(path, NormExchangeCore.ToJson(map), new UTF8Encoding(false));
            return path;
        }

        public static NormMap FromSystems(string name, IDictionary<string, string> systems)
        {
            NormMap map = new NormMap { Name = name ?? "" };
            foreach (KeyValuePair<string, string> pair in systems)
            {
                string code = (pair.Value ?? "").Trim().ToUpperInvariant();
                if (code.Length > 0)
                    map.Layers[pair.Key] = new NormMapEntry { System = code };
            }

            return map;
        }
    }

    /// <summary>The system codes mep_normalize.py knows (mirrors its SYSTEMS table).</summary>
    public static class NormSystems
    {
        public static readonly string[][] Codes =
        {
            new[] { "CHWS", "Chilled water supply" }, new[] { "CHWR", "Chilled water return" },
            new[] { "CWS", "Condenser water supply" }, new[] { "CWR", "Condenser water return" },
            new[] { "HWS", "Heating hot water supply" }, new[] { "HWR", "Heating hot water return" },
            new[] { "COND", "AC condensate" }, new[] { "RP", "Refrigerant" },
            new[] { "WS", "Domestic cold water" }, new[] { "DHW", "Domestic hot water" },
            new[] { "WW", "Waste water" }, new[] { "SW", "Soil water" }, new[] { "RW", "Rain water" },
            new[] { "VP", "Vent pipe" }, new[] { "PW", "Process waste water" }, new[] { "FP", "Fire protection" },
            new[] { "SP", "Sprinkler" }, new[] { "CA", "Compressed air" }, new[] { "N2", "Nitrogen" },
            new[] { "AR", "Argon" }, new[] { "GAS", "Fuel gas" },
            new[] { "SA", "Supply air duct" }, new[] { "RA", "Return air duct" }, new[] { "OA", "Outside air duct" },
            new[] { "EA", "Exhaust air duct" }, new[] { "TA", "Toilet exhaust duct" }, new[] { "KE", "Kitchen exhaust duct" },
            new[] { "SE", "Smoke exhaust duct" }, new[] { "PA", "Stair pressurisation duct" },
            new[] { "MA", "Make-up air duct" }, new[] { "PE", "Process exhaust duct" }
        };

        public static bool IsKnown(string code)
        {
            return Codes.Any(c => string.Equals(c[0], code, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ------------------------------------------------------------------------------------------ planner

    public sealed class PiecePlan
    {
        /// <summary>Where this piece starts on the curve; null for the first piece.</summary>
        public double[] SplitAt;
        public string TargetLayer;
        public SizeTag Tag;
        public bool Erase;
        public double LengthMm;
        public string Confidence;
        /// <summary>The preview path of the piece (WCS x, y).</summary>
        public List<double[]> Path = new List<double[]>();
    }

    public sealed class EntityPlan
    {
        public string Handle;
        public string Layer;
        public bool IsDuct;
        public bool EraseWhole;
        public bool ResetOverrides;
        public List<PiecePlan> Pieces = new List<PiecePlan>();

        public bool Splits
        {
            get { return !EraseWhole && Pieces.Count > 1; }
        }
    }

    public sealed class NormSummary
    {
        public int Entities;
        public int Moved;
        public int Tagged;
        public int Reset;
        public int Erased;
        public int Splits;
        public int InBlock;
        public double UnsizedM;
        public double BelowThresholdM;
        public SortedSet<string> TargetLayers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public static class NormPlanner
    {
        public static bool IsConfidence(string value)
        {
            return value == "high" || value == "medium" || value == "low";
        }

        /// <summary>layer 0, high 1, medium 2, low 3, none (or unknown) 4.</summary>
        public static int Rank(string confidence)
        {
            switch (confidence)
            {
                case "layer": return 0;
                case "high": return 1;
                case "medium": return 2;
                case "low": return 3;
                default: return 4;
            }
        }

        /// <summary>
        /// Per-entity actions. <paramref name="layerAvailable"/> says whether a template layer exists in
        /// the drawing or can be imported from the template (no layer is ever invented).
        /// </summary>
        public static List<EntityPlan> Plan(NormResult result, NormSettings settings, Func<string, bool> layerAvailable,
            out NormSummary summary)
        {
            summary = new NormSummary();
            List<EntityPlan> plans = new List<EntityPlan>();
            int threshold = Rank(settings.MinConfidence);
            foreach (NormEntity entity in result.Entities)
            {
                if (entity.InBlock || string.IsNullOrEmpty(entity.Handle) || entity.Groups.Length == 0)
                {
                    if (entity.InBlock)
                        summary.InBlock++;
                    continue;
                }

                EntityPlan plan = new EntityPlan
                {
                    Handle = entity.Handle,
                    Layer = entity.Layer,
                    IsDuct = entity.IsDuct,
                    ResetOverrides = settings.FixOverrides && entity.Overrides.Length > 0
                };
                if (settings.EraseDuplicates && entity.Groups.All(g => g.Duplicate))
                {
                    plan.EraseWhole = true;
                    PiecePlan whole = new PiecePlan { Erase = true, LengthMm = entity.Groups.Sum(g => g.LengthMm) };
                    foreach (NormGroup g in entity.Groups)
                        AddPath(whole, g);
                    plan.Pieces.Add(whole);
                    summary.Erased++;
                    summary.Entities++;
                    plans.Add(plan);
                    continue;
                }

                foreach (NormGroup g in entity.Groups)
                {
                    PiecePlan piece = PieceFor(entity, g, settings, threshold, layerAvailable, summary);
                    PiecePlan last = plan.Pieces.Count > 0 ? plan.Pieces[plan.Pieces.Count - 1] : null;
                    if (last != null && SameAction(last, piece))
                    {
                        last.LengthMm += piece.LengthMm;
                        last.Path.AddRange(piece.Path.Skip(1));
                        if (Rank(piece.Confidence) > Rank(last.Confidence))
                            last.Confidence = piece.Confidence;
                        continue;
                    }

                    if (last != null)
                        piece.SplitAt = g.Start;
                    plan.Pieces.Add(piece);
                }

                bool acts = plan.ResetOverrides || plan.Pieces.Count > 1
                    || plan.Pieces.Any(p => p.Erase || p.TargetLayer != null || p.Tag != null);
                if (!acts)
                    continue;
                summary.Entities++;
                if (plan.ResetOverrides)
                    summary.Reset++;
                if (plan.Splits)
                    summary.Splits++;
                foreach (PiecePlan piece in plan.Pieces)
                {
                    if (piece.Erase)
                        summary.Erased++;
                    else if (piece.TargetLayer != null)
                    {
                        summary.Moved++;
                        summary.TargetLayers.Add(piece.TargetLayer);
                    }

                    if (piece.Tag != null && !piece.Erase)
                        summary.Tagged++;
                }

                plans.Add(plan);
            }

            return plans;
        }

        private static PiecePlan PieceFor(NormEntity entity, NormGroup g, NormSettings settings, int threshold,
            Func<string, bool> layerAvailable, NormSummary summary)
        {
            PiecePlan piece = new PiecePlan { LengthMm = g.LengthMm, Confidence = g.Confidence ?? "none" };
            AddPath(piece, g);
            if (g.Duplicate)
            {
                piece.Erase = settings.EraseDuplicates;
                return piece;
            }

            if (g.Size == null || Rank(g.Confidence) > 3)
            {
                summary.UnsizedM += g.LengthMm / 1000.0;
                return piece;
            }

            if (Rank(g.Confidence) > threshold)
            {
                summary.BelowThresholdM += g.LengthMm / 1000.0;
                return piece;
            }

            bool onTarget = g.TargetLayer != null
                && string.Equals(g.TargetLayer, entity.Layer, StringComparison.OrdinalIgnoreCase);
            if (onTarget)
                return piece; // a catalogue layer already says the size

            if (!entity.IsDuct && settings.MoveToStandard && g.TargetLayer != null && layerAvailable(g.TargetLayer))
            {
                piece.TargetLayer = g.TargetLayer;
                return piece;
            }

            piece.Tag = TagFor(entity, g);
            return piece;
        }

        private static void AddPath(PiecePlan piece, NormGroup g)
        {
            if (g.Path == null)
                return;
            foreach (double[] p in g.Path)
            {
                if (p != null && p.Length >= 2)
                    piece.Path.Add(new[] { p[0], p[1] });
            }
        }

        private static bool SameAction(PiecePlan a, PiecePlan b)
        {
            return a.Erase == b.Erase
                && string.Equals(a.TargetLayer, b.TargetLayer, StringComparison.OrdinalIgnoreCase)
                && TagKey(a.Tag) == TagKey(b.Tag);
        }

        private static string TagKey(SizeTag tag)
        {
            return tag == null ? "" : tag.App + "|" + tag.System + "|" + tag.Material + "|" + tag.Prefix + "|" + tag.SizeText;
        }

        public static SizeTag TagFor(NormEntity entity, NormGroup g)
        {
            NormSize size = g.Size;
            string system = !string.IsNullOrEmpty(g.System) ? g.System : entity.System;
            SizeTag tag = new SizeTag
            {
                System = system ?? "",
                Material = g.Material ?? "",
                SizeText = g.SizeText ?? "",
                Confidence = g.Confidence ?? "",
                SourceLayer = entity.Layer ?? "",
                Label = g.Label ?? ""
            };
            if (entity.IsDuct)
            {
                tag.Kind = SizeTagKind.Duct;
                if (size.Width.HasValue && size.Height.HasValue)
                {
                    tag.SizeMm = size.Width.Value;
                    tag.Size2Mm = size.Height.Value;
                }
                else
                {
                    tag.SizeMm = size.Size ?? 0;
                    tag.Round = true;
                }
            }
            else
            {
                tag.Kind = SizeTagKind.Pipe;
                tag.Prefix = !string.IsNullOrEmpty(g.BoqPrefix) ? g.BoqPrefix : tag.System;
                if (size.Pair != null && size.Pair.Length == 2)
                {
                    tag.SizeMm = size.Pair[0];
                    tag.Size2Mm = size.Pair[1];
                }
                else
                {
                    tag.SizeMm = size.Size ?? 0;
                }

                tag.Slope = size.Slope ?? 0;
            }

            return tag;
        }
    }
}
