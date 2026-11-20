using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Pipes;

namespace HvacAiCadAddin.Commands
{
    // HVACDRAIN: draws and sizes a condensate drain network. The user draws rough LINEs and polylines from
    // the indoor units to the drain point (a riser or floor drain); the command straightens them with the
    // HVACREFRIG cleaner (no root, so no refnets and none of the refnet rules), builds the tree toward the
    // drain point, cuts every 90 degree bend and every right-angle T with 45 degree pieces (a 300 mm slant,
    // the branch landing downstream of the T point), and sizes each pipe from the indoor-unit capacities
    // (Daikin Si39: 0.71 L/h per kW, D27 for one unit, D34 to D110 by the total kW and the slope, never
    // smaller toward the drain). The capacity of each unit is read from the drawing (HVACAI_UNIT xdata, a
    // block name with "x kW", or an HVAC-NAME OF EQUIPMENTS label) and edited in SizingForm, the shared
    // sizing dialog; on Apply each pipe goes on its EIA_1 Pipe condensated layer with a D<size>-<slope>%
    // label. With "Size pipes" off it only straightens and draws the joints, after a Yes/No preview.
    // This class only prompts and previews; DrainPlanner, DrainNetwork, DrainGeometry and DrainSizing
    // (AutoCAD-free) do the work, RefrigCapacityReader reads the capacities and DrainWriter writes the
    // result in one transaction. See AGENTS.md, "HVACDRAIN: draw and size a condensate network".
    public class HvacDrainCommand
    {
        [CommandMethod("HVACDRAIN", CommandFlags.UsePickSet)]
        public void HvacDrain()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            try
            {
                Run(document);
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage("\nHVACDRAIN stopped: " + ex.Message);
            }
        }

        private static void Run(Document document)
        {
            Database db = document.Database;
            Editor editor = document.Editor;

            // 1. The rough network: the pickfirst set, or a new selection.
            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                SelectionFilter filter = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "LINE,LWPOLYLINE")
                });
                selection = editor.GetSelection(filter);
                if (selection.Status != PromptStatus.OK)
                    return;
            }

            int skipped;
            List<PipeSource> sources = PipeDrawingLibrary.ReadCurves(
                db, selection.Value.GetObjectIds(), null, out skipped);
            if (sources.Count == 0)
            {
                editor.WriteMessage("\nNo LINEs or polylines were selected.");
                return;
            }

            if (skipped > 0)
                editor.WriteMessage("\n{0} polyline(s) not in the XY plane were left out of the selection.", skipped);

            foreach (PipeSource source in sources)
            {
                if (source.Locked)
                {
                    editor.WriteMessage(
                        "\nThe selection includes a curve on the locked layer {0}. Unlock it first.",
                        source.Curve.Layer);
                    return;
                }
            }

            // 2. The root: the drain point (the end of the pipe that reaches the riser or floor drain).
            PromptPointOptions rootOptions = new PromptPointOptions(
                "\nPick the drain end of the network (the riser or floor drain, the root): ");
            PromptPointResult rootResult = editor.GetPoint(rootOptions);
            if (rootResult.Status != PromptStatus.OK)
                return;

            Matrix3d ucs = editor.CurrentUserCoordinateSystem;
            Point3d root = rootResult.Value.TransformBy(ucs);

            // 3. Settings.
            DrainSettings settings;
            using (DrainSettingsForm form = new DrainSettingsForm())
            {
                if (Application.ShowModalDialog(form) != System.Windows.Forms.DialogResult.OK)
                    return;
                settings = form.Settings;
            }

            double baseAngle = settings.UseUcsBaseAngle
                ? Math.Atan2(ucs.CoordinateSystem3d.Xaxis.Y, ucs.CoordinateSystem3d.Xaxis.X)
                : settings.BaseAngleDegrees * Math.PI / 180.0;

            // 4. Clean, build the tree and make the joints.
            DrainPlan plan = DrainPlanner.Run(
                sources.ConvertAll(s => s.Curve),
                settings,
                baseAngle,
                new Vec2(root.X, root.Y),
                Math.Max(PipeTransients.ViewSize() / 20.0, settings.WeldTolerance * 3));
            double elevation = sources[0].Curve.Elevation;

            if (!plan.Ok)
            {
                editor.WriteMessage("\n" + plan.Error);
                MarkProblems(editor, plan.ErrorPoints, elevation, 1);
                return;
            }

            foreach (string report in plan.Reports)
                editor.WriteMessage("\nWarning: " + report);

            DrainGeometryResult geometry = plan.Geometry;
            editor.WriteMessage(
                "\nDrain network: {0} pipe segment(s), {1} indoor unit(s). {2} bend(s) chamfered, "
                + "{3} branch(es) slanted, {4} corner(s) or joint(s) kept as drawn.",
                plan.Tree.Segments.Count, plan.Tree.Units.Count, geometry.Chamfers, geometry.Slants, geometry.Kept);

            if (settings.SizePipes)
                SizeAndWrite(document, sources, plan, settings, elevation);
            else
                ConfirmAndWrite(document, sources, plan, settings, elevation);
        }

        // ------------------------------------------------------------------ sizing on

        // Sizing: read the capacities, show the dialog, and on Apply write the straightened and sized network.
        // Cancel in the dialog writes nothing.
        private static void SizeAndWrite(
            Document document, List<PipeSource> sources, DrainPlan plan, DrainSettings settings, double elevation)
        {
            Database db = document.Database;
            Editor editor = document.Editor;

            List<Vec2> endPoints = plan.Tree.Units.ConvertAll(u => u.Point);
            UnitReading[] readings = RefrigCapacityReader.Read(
                db, sources, endPoints, settings.WeldTolerance, settings.CapacityRadius);
            int read = 0;
            foreach (UnitReading reading in readings)
            {
                if (reading != null)
                    read++;
            }

            editor.WriteMessage(
                "\n{0} indoor unit(s) found; a capacity was read for {1}.", endPoints.Count, read);

            string applyNote = string.Format(
                CultureInfo.InvariantCulture,
                "Apply writes {0} pipe polyline(s) on the EIA_1 Pipe condensated layers{1} and replaces the "
                + "selected curves. Cancel writes nothing.",
                plan.Tree.Segments.Count,
                settings.WriteLabels ? ", with size labels" : "");

            DrainSizingOutcome outcome;
            using (DrainSizingSession session = new DrainSizingSession(editor, plan, settings, readings, elevation))
            {
                outcome = session.Run(applyNote);
            }

            if (outcome == null)
            {
                editor.WriteMessage("\nCancelled: nothing was changed.");
                return;
            }

            int[] sizes = new int[outcome.Result.Segments.Count];
            for (int i = 0; i < sizes.Length; i++)
                sizes[i] = outcome.Result.Segments[i].Size;

            using (PipeTemplateHolder template = new PipeTemplateHolder(editor))
            {
                string error;
                string note;

                // Every size layer must be in the drawing before the write; the template's layers bring their
                // Broken Line linetype with them.
                DrainWriteRequest request = new DrainWriteRequest
                {
                    Sources = sources,
                    Plan = plan,
                    Settings = settings,
                    SegmentSizes = sizes,
                    UnitKw = outcome.UnitKw,
                    WriteLabels = settings.WriteLabels
                };
                foreach (int size in sizes)
                {
                    if (request.LayerBySize.ContainsKey(size))
                        continue;
                    string layerName;
                    if (!PipeDrawingLibrary.EnsureLayer(
                        db,
                        PipeKind.Condensate,
                        "DN" + size.ToString(CultureInfo.InvariantCulture),
                        template.Open,
                        out layerName,
                        out note,
                        out error))
                    {
                        editor.WriteMessage("\n" + error + " Nothing was changed.");
                        return;
                    }

                    request.LayerBySize[size] = layerName;
                    if (note != null)
                        editor.WriteMessage("\n" + note);
                }

                if (settings.WriteLabels && !string.IsNullOrEmpty(settings.LabelLayer))
                {
                    if (PipeDrawingLibrary.EnsureNamedLayer(db, settings.LabelLayer, template.Open, out note, out error))
                    {
                        request.LabelLayer = settings.LabelLayer;
                        if (note != null)
                            editor.WriteMessage("\n" + note);
                    }
                    else
                    {
                        editor.WriteMessage("\n" + error + " The size labels were not written.");
                        request.WriteLabels = false;
                    }
                }

                if (request.WriteLabels)
                {
                    request.LabelStyle = PipeDrawingLibrary.FindTextStyle(db, RefrigSizeWriter.TemplateLabelStyle);
                    if (request.LabelStyle.IsNull)
                    {
                        editor.WriteMessage(
                            "\nText style {0} is not in the drawing: the labels use the current style.",
                            RefrigSizeWriter.TemplateLabelStyle);
                    }
                }

                WriteSummary written;
                if (!DrainWriter.Write(db, request, out written, out error))
                {
                    editor.WriteMessage("\n" + error + " Nothing was changed.");
                    return;
                }

                editor.WriteMessage(
                    "\nWrote {0} polyline(s) ({1} sized), replacing {2} original curve(s).",
                    written.Paths, written.Sized, written.Erased);
                if (request.WriteLabels)
                {
                    editor.WriteMessage(
                        "\nSize labels: {0} written (height {1} mm, on {2}), {3} of an earlier run replaced.",
                        written.Labels,
                        settings.LabelHeight.ToString("0.###", CultureInfo.InvariantCulture),
                        request.LabelLayer ?? "each pipe's own layer",
                        written.LabelsRemoved);
                    if (written.LabelsLocked > 0)
                    {
                        editor.WriteMessage(
                            "\n{0} earlier label(s) on a locked layer were left in place.", written.LabelsLocked);
                    }
                }

                WriteSizingSummary(editor, outcome.Result);
                WriteCleaningSummary(editor, plan);
                foreach (string warning in outcome.Result.Warnings)
                    editor.WriteMessage("\nWarning: " + warning);
            }
        }

        // One line per segment, so the result can be checked against the drawing.
        private static void WriteSizingSummary(Editor editor, DrainSizingResult sizing)
        {
            double total = 0;
            if (sizing.Segments.Count > 0)
                total = sizing.Segments[0].TotalKw;
            editor.WriteMessage(
                "\nTotal capacity {0} kW. Sizes by segment (S1 is the pipe at the drain point):",
                total.ToString("0.##", CultureInfo.InvariantCulture));
            foreach (DrainSegmentSizing row in sizing.Segments)
            {
                editor.WriteMessage(
                    "\n  S{0}: {1} unit(s), {2} kW, {3}{4}",
                    row.Index + 1,
                    row.UnitCount,
                    row.TotalKw.ToString("0.##", CultureInfo.InvariantCulture),
                    row.SizeText,
                    row.Manual ? " (set by hand)" : "");
            }
        }

        private static void WriteCleaningSummary(Editor editor, DrainPlan plan)
        {
            RefrigResult clean = plan.Clean;
            editor.WriteMessage(
                "\nJoints moved: {0}. Collinear nodes merged: {1}. Jogs merged: {2}. Zero-length pipes removed: {3}. "
                + "Duplicate pipes removed: {4}.",
                clean.NodesMoved, clean.NodesDissolved, clean.JogsMerged, clean.ZeroLengthRemoved,
                clean.DuplicatesRemoved);
            if (plan.Reports.Count > 0)
                editor.WriteMessage("\n{0} case(s) were reported above and left unchanged.", plan.Reports.Count);
        }

        // ------------------------------------------------------------------ sizing off

        // No sizing: show the straightened network, ask, and write it on the layers the pipes already have.
        private static void ConfirmAndWrite(
            Document document, List<PipeSource> sources, DrainPlan plan, DrainSettings settings, double elevation)
        {
            Database db = document.Database;
            Editor editor = document.Editor;

            if (!Confirm(editor, plan, elevation))
            {
                editor.WriteMessage("\nCancelled: nothing was changed.");
                return;
            }

            DrainWriteRequest request = new DrainWriteRequest { Sources = sources, Plan = plan, Settings = settings };
            WriteSummary written;
            string error;
            if (!DrainWriter.Write(db, request, out written, out error))
            {
                editor.WriteMessage("\n" + error + " Nothing was changed.");
                return;
            }

            editor.WriteMessage(
                "\nWrote {0} polyline(s), replacing {1} original curve(s). The pipes keep their layers (sizing is off).",
                written.Paths, written.Erased);
            WriteCleaningSummary(editor, plan);
        }

        // Shows the straightened pipes (green) and the cases left alone (yellow circles), then asks. Yes is the default.
        private static bool Confirm(Editor editor, DrainPlan plan, double elevation)
        {
            using (PipeTransients preview = new PipeTransients())
            {
                DrainPreviewDrawer.Draw(preview, plan, null, null, null, elevation);
                editor.WriteMessage(
                    "\nPreview: {0} pipe run(s) in green, the drain point circled in red, {1} case(s) circled in yellow.",
                    plan.Tree.Segments.Count, plan.ReportPoints.Count);

                PromptKeywordOptions options = new PromptKeywordOptions(
                    "\nApply the straightened network? [Yes/No] <Yes>: ", "Yes No");
                options.Keywords.Default = "Yes";
                options.AllowNone = true;
                PromptResult answer = editor.GetKeywords(options);
                if (answer.Status == PromptStatus.None)
                    return true;
                return answer.Status == PromptStatus.OK
                    && string.Equals(answer.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
            }
        }

        // Circles the points a check complained about, until the user presses Enter.
        private static void MarkProblems(Editor editor, IList<Vec2> points, double elevation, short color)
        {
            if (points.Count == 0)
                return;

            using (PipeTransients markers = new PipeTransients())
            {
                double radius = PipeTransients.ViewSize() / 40.0;
                foreach (Vec2 point in points)
                    markers.AddCircle(point, radius, elevation, color);

                PromptStringOptions pause = new PromptStringOptions("\nThe cases are circled. Press Enter to clear them: ");
                pause.AllowSpaces = true;
                editor.GetString(pause);
            }
        }
    }
}
