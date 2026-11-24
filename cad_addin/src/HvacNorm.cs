using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Norm;
using HvacAiCadAddin.Pipes;
using HvacAiCadAddin.Rooms;

namespace HvacAiCadAddin.Commands
{
    // HVACNORM: normalises the pipes and single-line ducts of a drawing from another office. The user
    // selects them with their size labels; the selection is copied to a DXF snapshot and mep_normalize.py
    // runs on it as a separate python.exe process (never inside AutoCAD). With the project map (saved here
    // or by the mep_map MCP tool) or a proposal, it gives each pipe piece a size from its layer or the
    // nearest label. The dialog shows the system of each layer; after OK the result is previewed (green =
    // moved to an EIA_1 layer, cyan = size tag, red = erased duplicate, magenta = override reset only,
    // grey = no size, yellow circles = gaps). On Yes, in one undo group: duplicates are erased, overrides
    // reset to ByLayer, curves cut where the size changes, pipes of the BOQ material moved to their
    // existing EIA_1 layer (imported from the template when the drawing lacks it) and all others tagged
    // (HVACAI_PIPE / HVACAI_DUCT) so that HVACQTO counts them with a BOQ prefix. No new layer is created.
    public class HvacNormCommand
    {
        private const int PythonTimeoutMs = 300000;
        private const int MaxListedIssues = 12;

        [CommandMethod("HVACNORM", CommandFlags.UsePickSet)]
        public void HvacNorm()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            try
            {
                Run(document);
            }
            catch (System.Exception ex)
            {
                document.Editor.WriteMessage("\nHVACNORM stopped: " + ex.Message);
            }
        }

        private static void Run(Document document)
        {
            Database db = document.Database;
            Editor editor = document.Editor;

            // 1. The pipes, ducts and their labels.
            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                PromptSelectionOptions options = new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect the pipes / single-line ducts and their size labels (window or crossing): "
                };
                selection = editor.GetSelection(options);
                if (selection.Status != PromptStatus.OK)
                    return;
            }

            List<ObjectId> ids = new List<ObjectId>(selection.Value.GetObjectIds());
            string addinFolder = Path.GetDirectoryName(typeof(HvacNormCommand).Assembly.Location);
            string mapsFolder = NormMapStore.DefaultFolder;
            NormSettings last = NormSettingsStore.Load();

            // 2. Snapshot, then a first Python run with the saved map (or a proposal) to fill the dialog.
            string folder = NormExchange.NewWorkFolder();
            NormRequest request = new NormRequest();
            editor.WriteMessage("\nHVACNORM: copying {0} objects to {1}", ids.Count, folder);
            Dictionary<string, ObjectId> handles = NormExchange.WriteSnapshot(db, ids, Path.Combine(folder, request.Dxf));
            SortedDictionary<string, int> layers = RoomsExchange.CollectLayers(db, ids);

            string script;
            string python;
            NormSettingsStore.Resolve(last, addinFolder, out script, out python);
            NormResult first = null;
            if (script != null && File.Exists(script) && python != null && File.Exists(python))
            {
                NormMap saved = null;
                if (NormMapStore.IsValidName(last.MapName) && NormMapStore.List(mapsFolder).Contains(last.MapName, StringComparer.OrdinalIgnoreCase))
                    saved = NormMapStore.Load(mapsFolder, last.MapName);
                request.Map = saved;
                editor.WriteMessage("\nHVACNORM: reading layers and size labels (Python)... ");
                first = RunPython(editor, folder, request, python, script);
                if (first == null)
                    return;
            }
            else
            {
                editor.WriteMessage("\nPython or " + NormExchangeCore.ScriptName + " was not found; set the paths in the dialog.");
            }

            NormSettings settings;
            Dictionary<string, string> systems;
            string saveName;
            using (NormForm form = new NormForm(layers, first, mapsFolder, addinFolder, last))
            {
                if (Application.ShowModalDialog(form) != System.Windows.Forms.DialogResult.OK)
                    return;
                settings = form.Settings;
                systems = form.Systems;
                saveName = form.SaveName;
                script = form.ResolvedScript;
                python = form.ResolvedPython;
            }

            if (saveName != null)
            {
                string path = NormMapStore.Save(mapsFolder, saveName, systems);
                editor.WriteMessage("\nProject map saved: {0}", path);
            }

            // 3. Run again when the map changed.
            NormResult result = first;
            if (result == null || !SameSystems(result.Map, systems))
            {
                request.Map = NormMapStore.FromSystems(saveName ?? "", systems);
                editor.WriteMessage("\nHVACNORM: sizing with the map (Python)... ");
                result = RunPython(editor, folder, request, python, script);
                if (result == null)
                    return;
            }

            Report(editor, result);

            // 4. Plan against the layers that exist in the drawing or in the template.
            TemplateSource template = null;
            string error;
            string templatePath = TemplateLocator.GetTemplatePath(editor, false);
            if (templatePath != null)
                template = TemplateSource.TryOpen(templatePath, out error);
            try
            {
                HashSet<string> templateLayers = new HashSet<string>(
                    template != null ? template.GetLayerNames() : new List<string>(), StringComparer.OrdinalIgnoreCase);
                HashSet<string> drawingLayers = DrawingLayers(db);
                if (template == null && settings.MoveToStandard)
                    editor.WriteMessage("\nTemplate drawing not available: only EIA_1 layers already in this drawing are used.");
                Func<string, bool> available = name => drawingLayers.Contains(name) || templateLayers.Contains(name);

                NormSummary summary;
                List<EntityPlan> plans = NormPlanner.Plan(result, settings, available, out summary);
                if (plans.Count == 0)
                {
                    editor.WriteMessage("\nNothing to change{0}.", summary.UnsizedM > 0
                        ? string.Format(CultureInfo.InvariantCulture, " ({0:0.#} m have no size: label them or lower the confidence)", summary.UnsizedM)
                        : "");
                    return;
                }

                // 5. Preview and confirm.
                if (!Confirm(editor, result, plans, summary))
                {
                    editor.WriteMessage("\nNothing was written.");
                    return;
                }

                // 6. Template layers first (same undo group), then the changes.
                List<string> import = summary.TargetLayers.Where(n => !drawingLayers.Contains(n)).ToList();
                if (import.Count > 0 && template != null)
                {
                    List<string> missing;
                    template.ImportLayers(db, import, out missing);
                    editor.WriteMessage("\nImported {0} layers from the template.", import.Count - missing.Count);
                }

                NormWriteSummary written = NormWriter.Write(db, plans, handles);
                editor.WriteMessage(
                    "\nHVACNORM: {0} moved to EIA_1 layers, {1} size tags, {2} overrides reset, {3} duplicates erased, {4} curves cut.",
                    written.Moved, written.Tagged, written.Reset, written.Erased, written.Split);
                if (written.Locked > 0)
                    editor.WriteMessage("\n{0} objects are on locked layers and were left alone.", written.Locked);
                if (written.Missing > 0)
                    editor.WriteMessage("\n{0} objects were not found (changed since the snapshot?).", written.Missing);
                if (written.SplitFailed > 0)
                    editor.WriteMessage("\n{0} curves could not be cut where the size changes and were left alone.", written.SplitFailed);
                if (summary.InBlock > 0)
                    editor.WriteMessage("\n{0} curves inside blocks or xrefs were counted but not changed.", summary.InBlock);
                editor.WriteMessage("\nRun HVACQTO on the same objects for the BOQ (tagged pipes come out as \"<system> - <pipe>\").");
            }
            finally
            {
                if (template != null)
                    template.Dispose();
            }
        }

        private static NormResult RunPython(Editor editor, string folder, NormRequest request, string python, string script)
        {
            string requestPath = Path.Combine(folder, "request.json");
            NormExchange.WriteRequest(requestPath, request);
            string resultPath = Path.Combine(folder, request.Out);
            if (File.Exists(resultPath))
                File.Delete(resultPath);
            System.Windows.Forms.Application.DoEvents();
            string output;
            int exitCode = RoomsExchange.RunPython(python, script, requestPath, PythonTimeoutMs, out output);
            if (!File.Exists(resultPath))
            {
                editor.WriteMessage("\nPython wrote no result (exit code {0}).\n{1}", exitCode, Shorten(output));
                return null;
            }

            NormResult result = NormExchangeCore.ParseResult(File.ReadAllText(resultPath));
            if (!result.IsOk)
            {
                editor.WriteMessage("\nPython reported an error: {0}\nFiles kept in {1}", result.Message, folder);
                return null;
            }

            return result;
        }

        private static bool SameSystems(Dictionary<string, NormMapEntry> map, Dictionary<string, string> systems)
        {
            Dictionary<string, string> had = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, NormMapEntry> pair in map ?? new Dictionary<string, NormMapEntry>())
            {
                if (pair.Value != null && !string.IsNullOrEmpty(pair.Value.System))
                    had[pair.Key] = pair.Value.System.ToUpperInvariant();
            }

            if (had.Count != systems.Count)
                return false;
            foreach (KeyValuePair<string, string> pair in systems)
            {
                string code;
                if (!had.TryGetValue(pair.Key, out code) || !string.Equals(code, pair.Value, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private static void Report(Editor editor, NormResult result)
        {
            foreach (string warning in result.Warnings)
                editor.WriteMessage("\nWarning: " + warning);
            if (result.Stats != null)
            {
                editor.WriteMessage("\n{0:0.#} m of pipe/duct, {1:0.#} m drawn twice; size labels used {2}/{3}.",
                    result.Stats.LengthM, result.Stats.DuplicateM, result.Stats.LabelsUsed, result.Stats.Labels);
                if (result.Stats.ByConfidencePct != null)
                {
                    editor.WriteMessage("\nSized by: " + string.Join(", ", result.Stats.ByConfidencePct
                        .Select(p => p.Key + " " + p.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%")));
                }
            }

            if (result.IssueCounts.Count > 0)
            {
                editor.WriteMessage("\nIssues: " + string.Join(", ", result.IssueCounts.Select(p => p.Key + " " + p.Value)));
                foreach (NormIssue issue in result.Issues.Where(i => i.Type != "curve_type" && i.Type != "in_block").Take(MaxListedIssues))
                {
                    string at = issue.Point != null && issue.Point.Length >= 2
                        ? string.Format(CultureInfo.InvariantCulture, " at {0:0},{1:0}", issue.Point[0], issue.Point[1])
                        : "";
                    editor.WriteMessage("\n  {0}{1}: {2}", issue.Type, at, issue.Detail ?? issue.Text ?? issue.Layer ?? "");
                }
            }
        }

        private static bool Confirm(Editor editor, NormResult result, List<EntityPlan> plans, NormSummary summary)
        {
            using (PipeTransients preview = new PipeTransients())
            {
                foreach (EntityPlan plan in plans)
                {
                    foreach (PiecePlan piece in plan.Pieces)
                    {
                        if (piece.Path.Count < 2)
                            continue;
                        short color = piece.Erase ? (short)1
                            : piece.TargetLayer != null ? (short)3
                            : piece.Tag != null ? (short)4
                            : plan.ResetOverrides ? (short)6 : (short)8;
                        preview.AddPolyline(piece.Path.Select(p => new Vec2(p[0], p[1])).ToList(), 0, color, 0);
                    }
                }

                foreach (NormIssue issue in result.Issues)
                {
                    if (issue.Type == "gap" && issue.Point != null && issue.Point.Length >= 2)
                        preview.AddCircle(new Vec2(issue.Point[0], issue.Point[1]), 300, 0, 2);
                }

                PromptKeywordOptions confirm = new PromptKeywordOptions(string.Format(CultureInfo.InvariantCulture,
                    "\n{0} objects: {1} pieces to EIA_1 layers, {2} size tags, {3} overrides reset, {4} duplicates erased, "
                    + "{5} cut at size changes; {6:0.#} m without size, {7:0.#} m below the confidence. Apply? [Yes/No] <Yes>: ",
                    summary.Entities, summary.Moved, summary.Tagged, summary.Reset, summary.Erased, summary.Splits,
                    summary.UnsizedM, summary.BelowThresholdM));
                confirm.Keywords.Add("Yes");
                confirm.Keywords.Add("No");
                confirm.Keywords.Default = "Yes";
                confirm.AllowNone = true;
                PromptResult answer = editor.GetKeywords(confirm);
                return answer.Status == PromptStatus.None
                    || (answer.Status == PromptStatus.OK && answer.StringResult == "Yes");
            }
        }

        private static HashSet<string> DrawingLayers(Database db)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (Transaction transaction = db.TransactionManager.StartOpenCloseTransaction())
            {
                LayerTable table = (LayerTable)transaction.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    LayerTableRecord record = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForRead);
                    names.Add(record.Name);
                }

                transaction.Commit();
            }

            return names;
        }

        private static string Shorten(string text)
        {
            text = text ?? "";
            return text.Length <= 1500 ? text : text.Substring(text.Length - 1500);
        }
    }
}
