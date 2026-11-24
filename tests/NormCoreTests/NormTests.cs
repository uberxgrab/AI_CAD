using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HvacAiCadAddin.Norm;
using HvacAiCadAddin.Takeoff;
using PipeCoreTests;

namespace NormCoreTests
{
    internal static class NormTests
    {
        private static readonly Func<string, bool> AllLayers = name => true;
        private static readonly Func<string, bool> NoLayers = name => false;

        public static void Run()
        {
            Settings();
            ParsesTheRealResult();
            PlanFromTheRealResult();
            MoveOnlyWhenTheLayerExists();
            ConfidenceThreshold();
            OverridesAndBlocks();
            MapFiles();
            RequestJson();
        }

        private static NormResult Fixture()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "norm_fixture.json");
            return NormExchangeCore.ParseResult(File.ReadAllText(path));
        }

        private static void Settings()
        {
            Check.Case("n settings", () =>
            {
                NormSettings s = new NormSettings
                {
                    MapName = "YEV tender", MinConfidence = "medium", FixOverrides = false, EraseDuplicates = false,
                    MoveToStandard = false, PythonExe = @"C:\py\python.exe", ScriptPath = @"C:\r\mep_normalize.py"
                };
                NormSettings back = NormSettings.Deserialize(s.Serialize());
                Check.Equal("n map", "YEV tender", back.MapName);
                Check.Equal("n min", "medium", back.MinConfidence);
                Check.False("n overrides", back.FixOverrides);
                Check.False("n duplicates", back.EraseDuplicates);
                Check.False("n move", back.MoveToStandard);
                Check.Equal("n python", @"C:\py\python.exe", back.PythonExe);
                NormSettings bad = NormSettings.Deserialize("map=../x\nmin=none\njunk\n");
                Check.Equal("n bad map name ignored", "", bad.MapName);
                Check.Equal("n bad confidence ignored", "low", bad.MinConfidence);
                Check.True("n defaults fix", bad.FixOverrides && bad.EraseDuplicates && bad.MoveToStandard);
            });
        }

        private static void ParsesTheRealResult()
        {
            Check.Case("n parse fixture", () =>
            {
                NormResult r = Fixture();
                Check.True("n ok", r.IsOk);
                Check.Equal("n entities", 8, r.Entities.Length);
                Check.Equal("n map WS", "WS", r.Map["M-P-WS"].System);
                Check.False("n not proposed", r.MapProposed);
                Check.True("n stats", r.Stats.LengthM > 30);
                Check.True("n issue counts", r.IssueCounts.ContainsKey("override") && r.IssueCounts.ContainsKey("gap"));
                NormEntity trunk = r.Entities.First(e => e.Groups.Length == 4);
                Check.Equal("n trunk size", "D63", trunk.Groups[0].SizeText);
                Check.Equal("n trunk material", "PPR", trunk.Groups[0].Material);
                Check.Near("n trunk start", 0, trunk.Groups[0].Start[0]);
                Check.True("n path", trunk.Groups[0].Path.Length >= 2);
                Check.Near("n size", 63, trunk.Groups[0].Size.Size.Value);
                NormEntity duct = r.Entities.First(e => e.IsDuct && e.Groups.Length == 2);
                Check.Near("n duct width", 600, duct.Groups[0].Size.Width.Value);
                Check.Equal("n duct target", "EIA_1 SUPPLY AIR", duct.Groups[0].TargetLayer);
                Check.True("n gap point", r.Issues.Any(i => i.Type == "gap" && i.Point.Length == 2 && i.GapMm > 70));
            });
            Check.Case("n parse errors", () =>
            {
                NormResult err = NormExchangeCore.ParseResult("{\"version\":1,\"status\":\"error\",\"message\":\"boom\"}");
                Check.False("n error status", err.IsOk);
                Check.Equal("n error message", "boom", err.Message);
                Check.Equal("n empty entities", 0, err.Entities.Length);
                bool threw = false;
                try { NormExchangeCore.ParseResult("not json"); }
                catch (FormatException) { threw = true; }
                Check.True("n bad json throws FormatException", threw);
            });
        }

        private static void PlanFromTheRealResult()
        {
            Check.Case("n plan fixture", () =>
            {
                NormSummary summary;
                List<EntityPlan> plans = NormPlanner.Plan(Fixture(), new NormSettings(), AllLayers, out summary);

                EntityPlan dup = plans.Single(p => p.EraseWhole);
                Check.Equal("n duplicate handle", "35", dup.Handle);

                EntityPlan trunk = plans.Single(p => p.Handle == "33");
                Check.Equal("n trunk cut into two", 2, trunk.Pieces.Count);
                Check.True("n trunk splits", trunk.Splits);
                Check.True("n first piece has no cut", trunk.Pieces[0].SplitAt == null);
                Check.Near("n cut at the tee", 10000, trunk.Pieces[1].SplitAt[0], 1);
                Check.Equal("n WS keeps its layer", null, trunk.Pieces[0].TargetLayer);
                Check.Equal("n tag prefix", "WS", trunk.Pieces[0].Tag.Prefix);
                Check.Equal("n tag size text", "D63", trunk.Pieces[0].Tag.SizeText);
                Check.Near("n tag size", 50, trunk.Pieces[1].Tag.SizeMm);
                Check.Equal("n tag kind", SizeTagKind.Pipe, trunk.Pieces[1].Tag.Kind);
                Check.Equal("n worst confidence kept", "medium", trunk.Pieces[0].Confidence);

                EntityPlan branch = plans.Single(p => p.Handle == "34");
                Check.True("n override reset", branch.ResetOverrides);

                EntityPlan duct = plans.Single(p => p.Handle == "3C");
                Check.Equal("n duct pieces", 2, duct.Pieces.Count);
                Check.True("n ducts are never moved", duct.Pieces.All(p => p.TargetLayer == null));
                Check.Equal("n duct tag", SizeTagKind.Duct, duct.Pieces[0].Tag.Kind);
                Check.Near("n duct tag height", 300, duct.Pieces[0].Tag.Size2Mm);
                Check.Near("n cut halfway between the labels", 6000, duct.Pieces[1].SplitAt[0], 100);

                EntityPlan oa = plans.Single(p => p.Handle == "40");
                Check.Equal("n label system wins", "OA", oa.Pieces[0].Tag.System);

                Check.False("n catalogue layer untouched", plans.Any(p => p.Handle == "43"));
                Check.Equal("n summary erased", 1, summary.Erased);
                Check.Equal("n summary splits", 2, summary.Splits);
                Check.Equal("n summary reset", 1, summary.Reset);
                Check.Equal("n nothing moved", 0, summary.Moved);
            });
        }

        private static NormResult Chws(string confidence, string target = "EIA_1 CHWS DN50", string material = null)
        {
            return new NormResult
            {
                Status = "ok",
                Entities = new[]
                {
                    new NormEntity
                    {
                        Handle = "A1", Layer = "M-HVAC-CHWS", Kind = "pipe", System = "CHWS", Overrides = new string[0],
                        Groups = new[]
                        {
                            new NormGroup
                            {
                                Start = new[] { 0.0, 0 }, End = new[] { 1000.0, 0 }, LengthMm = 1000, Confidence = confidence,
                                Size = new NormSize { Kind = "pipe", Size = 50, Prefix = "DN" }, SizeText = "DN50",
                                Material = material, TargetLayer = target, System = "CHWS",
                                Path = new[] { new[] { 0.0, 0 }, new[] { 1000.0, 0 } }
                            }
                        }
                    }
                }
            };
        }

        private static void MoveOnlyWhenTheLayerExists()
        {
            Check.Case("n move", () =>
            {
                NormSummary summary;
                EntityPlan moved = NormPlanner.Plan(Chws("high"), new NormSettings(), AllLayers, out summary).Single();
                Check.Equal("n moved to EIA_1", "EIA_1 CHWS DN50", moved.Pieces[0].TargetLayer);
                Check.True("n moved has no tag", moved.Pieces[0].Tag == null);
                Check.True("n layer to import listed", summary.TargetLayers.Contains("EIA_1 CHWS DN50"));

                EntityPlan tagged = NormPlanner.Plan(Chws("high"), new NormSettings(), NoLayers, out summary).Single();
                Check.True("n no layer: not moved", tagged.Pieces[0].TargetLayer == null);
                Check.Equal("n no layer: tagged", "CHWS", tagged.Pieces[0].Tag.Prefix);

                EntityPlan off = NormPlanner.Plan(Chws("high"), new NormSettings { MoveToStandard = false }, AllLayers, out summary).Single();
                Check.True("n move off", off.Pieces[0].TargetLayer == null);
                Check.True("n move off tags", off.Pieces[0].Tag != null);

                EntityPlan other = NormPlanner.Plan(Chws("high", null, "PPR"), new NormSettings(), AllLayers, out summary).Single();
                Check.Equal("n other material tagged", "PPR", other.Pieces[0].Tag.Material);
            });
        }

        private static void ConfidenceThreshold()
        {
            Check.Case("n threshold", () =>
            {
                NormSummary summary;
                List<EntityPlan> none = NormPlanner.Plan(Chws("low"), new NormSettings { MinConfidence = "medium" }, AllLayers, out summary);
                Check.Equal("n low not applied", 0, none.Count);
                Check.Near("n below threshold metres", 1.0, summary.BelowThresholdM);
                List<EntityPlan> some = NormPlanner.Plan(Chws("medium"), new NormSettings { MinConfidence = "medium" }, AllLayers, out summary);
                Check.Equal("n medium applied", 1, some.Count);
                List<EntityPlan> unsized = NormPlanner.Plan(Chws("none"), new NormSettings(), AllLayers, out summary);
                Check.Equal("n none never applied", 0, unsized.Count);
                Check.Near("n unsized metres", 1.0, summary.UnsizedM);
                Check.Equal("n ranks", "0,1,2,3,4", string.Join(",", new[] { "layer", "high", "medium", "low", "none" }.Select(NormPlanner.Rank)));
            });
        }

        private static void OverridesAndBlocks()
        {
            Check.Case("n overrides and blocks", () =>
            {
                NormResult r = Chws("none");
                r.Entities[0].Overrides = new[] { "color=1" };
                NormSummary summary;
                EntityPlan reset = NormPlanner.Plan(r, new NormSettings(), AllLayers, out summary).Single();
                Check.True("n reset only", reset.ResetOverrides && reset.Pieces.Count == 1 && reset.Pieces[0].Tag == null);
                Check.Equal("n reset off", 0, NormPlanner.Plan(r, new NormSettings { FixOverrides = false }, AllLayers, out summary).Count);
                r.Entities[0].InBlock = true;
                Check.Equal("n in block skipped", 0, NormPlanner.Plan(r, new NormSettings(), AllLayers, out summary).Count);
                Check.Equal("n in block counted", 1, summary.InBlock);
            });
        }

        private static void MapFiles()
        {
            Check.Case("n map files", () =>
            {
                string folder = Path.Combine(Path.GetTempPath(), "NormCoreTests_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Check.Equal("n no folder no maps", 0, NormMapStore.List(folder).Count);
                    NormMapStore.Save(folder, "YEV tender", new Dictionary<string, string> { { "M-P-WS", "ws" }, { "Blank", "" } });
                    Check.Equal("n listed", "YEV tender", string.Join("|", NormMapStore.List(folder)));
                    NormMap map = NormMapStore.Load(folder, "YEV tender");
                    Check.Equal("n saved name", "YEV tender", map.Name);
                    Check.Equal("n code upper", "WS", map.Systems()["m-p-ws"]);
                    Check.False("n blank dropped", map.Layers.ContainsKey("Blank"));
                    string json = File.ReadAllText(Path.Combine(folder, "YEV tender.json"));
                    Check.Contains("n python-readable layers object", json, "\"layers\":{\"M-P-WS\":{\"system\":\"WS\"}}");
                    Check.False("n bad names", NormMapStore.IsValidName("../x") || NormMapStore.IsValidName("a/b")
                        || NormMapStore.IsValidName(" lead") || NormMapStore.IsValidName(new string('a', 61)));
                    Check.True("n good name", NormMapStore.IsValidName("Dự án 2.1_A-B"));
                }
                finally
                {
                    if (Directory.Exists(folder))
                        Directory.Delete(folder, true);
                }
            });
        }

        private static void RequestJson()
        {
            Check.Case("n request json", () =>
            {
                string empty = NormExchangeCore.ToJson(new NormRequest());
                Check.Contains("n version", empty, "\"version\":1");
                Check.False("n no map means propose", empty.Contains("\"map\""));
                NormRequest withMap = new NormRequest
                {
                    Map = NormMapStore.FromSystems("P", new Dictionary<string, string> { { "M-P-WS", "WS" } })
                };
                Check.Contains("n inline map", NormExchangeCore.ToJson(withMap), "\"layers\":{\"M-P-WS\":{\"system\":\"WS\"}}");
                string dir = Path.Combine(Path.GetTempPath(), "NormFind_" + Guid.NewGuid().ToString("N"));
                string deep = Path.Combine(dir, "a", "b");
                Directory.CreateDirectory(deep);
                File.WriteAllText(Path.Combine(dir, NormExchangeCore.ScriptName), "");
                Check.Equal("n script found upward", Path.Combine(dir, NormExchangeCore.ScriptName), NormExchangeCore.FindScript(deep));
                Check.Equal("n venv python", Path.Combine(dir, "venv", "Scripts", "python.exe"),
                    NormExchangeCore.DefaultPython(Path.Combine(dir, NormExchangeCore.ScriptName)));
                Directory.Delete(dir, true);
            });
        }
    }
}
