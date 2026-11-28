using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Pipes;

namespace HvacAiCadAddin.Commands
{
    // HVACDUCTLINE: draws a single-line duct network (centrelines, as other offices draw them) as duct
    // blocks and fittings, like HVACREFRIG / HVACDRAIN redraw rough pipe networks. The user selects the
    // lines of one system and picks the fan / AHU end. Sizes come from the HVACNORM tags (untagged lines
    // get the dialog's size), the layer from the tag's system (SA -> EIA_1 SUPPLY AIR ...). DuctLinePlanner
    // straightens bends and hops, joins ends and tees, grows the tree and solves each run with the
    // HVACDUCT RouteSolver (elbows, transitions; branches start with a tap on the main's side wall). The
    // result is previewed (green ducts, cyan fittings, red runs that do not fit, yellow reported points)
    // and written on Yes in one undo group; the single lines are erased or kept without their size tag.
    public class HvacDuctLineCommand
    {
        [CommandMethod("HVACDUCTLINE", CommandFlags.UsePickSet)]
        public void HvacDuctLine()
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
                document.Editor.WriteMessage("\nHVACDUCTLINE stopped: " + ex.Message);
            }
        }

        private static void Run(Document document)
        {
            Database db = document.Database;
            Editor editor = document.Editor;

            // 1. The single lines of one system.
            SelectionFilter filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "LINE,LWPOLYLINE,POLYLINE,ARC,SPLINE")
            });
            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                PromptSelectionOptions options = new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect the single-line ducts of one system: "
                };
                selection = editor.GetSelection(options, filter);
                if (selection.Status != PromptStatus.OK)
                    return;
            }

            List<DuctLineCurve> curves = new List<DuctLineCurve>();
            Dictionary<int, ObjectId> curveIds = new Dictionary<int, ObjectId>();
            using (Transaction transaction = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    Entity entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
                    if (entity == null)
                        continue;
                    DuctLineCurve curve = DuctLineReader.Read(entity, curves.Count);
                    if (curve == null || curve.Vertices.Count < 2)
                        continue;
                    curveIds[curve.Id] = id;
                    curves.Add(curve);
                }

                transaction.Commit();
            }

            if (curves.Count == 0)
            {
                editor.WriteMessage("\nNo lines or polylines were selected.");
                return;
            }

            PromptPointResult rootPick = editor.GetPoint("\nPick the start of the network (the fan / AHU end): ");
            if (rootPick.Status != PromptStatus.OK)
                return;
            Point3d rootWcs = rootPick.Value.TransformBy(editor.CurrentUserCoordinateSystem);

            // 2. Template catalogue (sizes, layers) and the dialog.
            string templatePath = TemplateLocator.GetTemplatePath(editor, false);
            TemplateCatalog catalog = null;
            if (templatePath != null)
            {
                string catalogError;
                catalog = TemplateCatalog.Read(templatePath, out catalogError);
                if (catalog == null)
                    editor.WriteMessage("\n" + catalogError);
            }

            List<string> layers = DuctBlockLibrary.ListDuctLayers(db);
            if (catalog != null)
            {
                foreach (string layer in catalog.DuctLayers)
                {
                    if (!layers.Contains(layer))
                        layers.Add(layer);
                }
            }

            layers.Sort(StringComparer.OrdinalIgnoreCase);
            int tagged = curves.Count(c => c.Size != null);
            string systems = string.Join(", ", curves.Where(c => c.System.Length > 0).Select(c => c.System.ToUpperInvariant()).Distinct());
            string summary = string.Format(CultureInfo.InvariantCulture,
                "{0} lines selected, {1} with an HVACNORM size tag{2}.", curves.Count, tagged,
                systems.Length > 0 ? " (systems " + systems + ")" : "");

            DuctLineOptions options2;
            using (DuctLineForm form = new DuctLineForm(layers, summary))
            {
                if (DialogHost.ShowModal(form) != System.Windows.Forms.DialogResult.OK)
                    return;
                options2 = form.Options;
            }

            DuctSize defaultSize;
            options2.TryGetSize(out defaultSize);
            DuctLineSettings settings = new DuctLineSettings
            {
                WeldTolerance = options2.WeldTolerance,
                DefaultSize = defaultSize,
                Fittings = DuctSettingsStore.Load().Fittings
            };

            // 3. Plan.
            DuctLinePlan plan = DuctLinePlanner.Plan(curves, new Vec2(rootWcs.X, rootWcs.Y), settings);
            foreach (string report in plan.Reports.Distinct().Take(15))
                editor.WriteMessage("\n  " + report);
            if (!plan.Ok)
            {
                editor.WriteMessage("\n" + plan.Error);
                return;
            }

            if (plan.UntaggedCurves > 0)
                editor.WriteMessage("\n{0} lines have no size tag and use {1}.", plan.UntaggedCurves, defaultSize.Label);
            Func<DuctLineRun, string> layerOf = run =>
                DuctLinePlanner.LayerForSystem(run.Group) ?? options2.Layer;

            // 4. Preview and confirm.
            if (!Confirm(editor, plan, settings.Fittings))
            {
                editor.WriteMessage("\nNothing was drawn.");
                return;
            }

            // 5. Write.
            TemplateSource template = null;
            string error;
            if (templatePath != null)
                template = TemplateSource.TryOpen(templatePath, out error);
            try
            {
                DuctLineWriteSummary written = DuctLineWriter.Write(db, plan, layerOf, template,
                    catalog == null ? null : catalog.Sizes, settings.Fittings, curveIds, options2.EraseLines);
                foreach (string note in written.Notes.Distinct().Take(10))
                    editor.WriteMessage("\n  " + note);
                foreach (string problem in written.Errors.Distinct())
                    editor.WriteMessage("\n  Not drawn: " + problem);
                editor.WriteMessage("\nHVACDUCTLINE: {0} runs, {1} ducts, {2} elbows/transitions, {3} taps; single lines {4}.",
                    written.Runs, written.Ducts, written.Fittings, written.Taps,
                    options2.EraseLines
                        ? written.LinesErased + " erased"
                        : "kept (" + written.TagsCleared + " size tags removed so HVACQTO counts only the blocks)");
            }
            finally
            {
                if (template != null)
                    template.Dispose();
            }
        }

        private static bool Confirm(Editor editor, DuctLinePlan plan, FittingSettings fittingSettings)
        {
            int ducts = 0;
            int fittings = 0;
            int failed = 0;
            using (PipeTransients preview = new PipeTransients())
            {
                foreach (DuctLineRun run in plan.Runs)
                {
                    if (!run.Ok)
                    {
                        failed++;
                        preview.AddPolyline(run.Points, run.Elevation, 1, 0);
                        continue;
                    }

                    foreach (RouteStraight straight in run.Result.Straights)
                    {
                        if (straight.Length <= 1e-6)
                            continue;
                        preview.AddOutline(FittingShapes.Straight(straight.Start, straight.Direction, straight.Length,
                            straight.Size.Width), run.Elevation, 3, 0);
                        ducts++;
                    }

                    foreach (RouteFitting fitting in run.Result.Fittings)
                    {
                        preview.AddOutline(fitting.Shape, run.Elevation, 4, 0);
                        fittings++;
                    }

                    if (run.StartsWithTap)
                    {
                        preview.AddOutline(FittingShapes.Tap(run.TapWallPoint, run.TapMainAxis, run.TapOutward,
                            run.Sizes[0], fittingSettings), run.Elevation, 4, 0);
                    }
                }

                foreach (Vec2 point in plan.ReportPoints)
                    preview.AddCircle(point, 400, 0, 2);

                PromptKeywordOptions confirm = new PromptKeywordOptions(string.Format(CultureInfo.InvariantCulture,
                    "\n{0} runs: {1} ducts, {2} elbows/transitions, {3} tees; {4} runs do not fit (red). Draw them? [Yes/No] <Yes>: ",
                    plan.Runs.Count - failed, ducts, fittings, plan.Tees, failed));
                confirm.Keywords.Add("Yes");
                confirm.Keywords.Add("No");
                confirm.Keywords.Default = "Yes";
                confirm.AllowNone = true;
                PromptResult answer = editor.GetKeywords(confirm);
                return answer.Status == PromptStatus.None
                    || (answer.Status == PromptStatus.OK && answer.StringResult == "Yes");
            }
        }
    }
}
