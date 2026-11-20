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
    // HVACPOWERWIRE: draws the indoor-unit power supply wire as a series (daisy-chain) wire. The user draws
    // a rough tree of LINEs and polylines: a trunk from the supply end, with a short branch off it to each
    // indoor unit (the end of the branch is the unit's terminal). The command straightens the lines with
    // the HVACREFRIG cleaner (no root, so no refnets), builds the tree from the supply end, and redraws it
    // as one continuous polyline: at each branch the wire runs down the branch to the unit, slants 45
    // degrees (300 mm along the trunk and 300 across), rises back to the trunk parallel to the branch and
    // goes on along the trunk, like the hook glyph of the template. The last unit gets only the drop leg.
    // A branch that is too short or too near the next one keeps a plain drop, circled and warned. The new
    // wire goes on EIA_5 SUPPLY WIRE (or SIGNAL WIRE) and can carry the cable text (default 2x1.5 MM2,
    // T_unicode, 275 mm) beside each unit's rise leg. The units are numbered U1, U2, ... in series order in
    // the preview. This class only prompts and previews; PowerWirePlanner (AutoCAD-free) does the work and
    // PowerWireWriter writes the result in one transaction.
    // See AGENTS.md, "HVACPOWERWIRE: draw the indoor-unit power wire".
    public class HvacPowerWireCommand
    {
        [CommandMethod("HVACPOWERWIRE", CommandFlags.UsePickSet)]
        public void HvacPowerWire()
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
                editor.WriteMessage("\nHVACPOWERWIRE stopped: " + ex.Message);
            }
        }

        private static void Run(Document document)
        {
            Database db = document.Database;
            Editor editor = document.Editor;

            // 1. The rough tree: the pickfirst set, or a new selection.
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

            // 2. The root: the supply end of the wire.
            PromptPointOptions rootOptions = new PromptPointOptions(
                "\nPick the supply end of the wire (where the power comes in, the root): ");
            PromptPointResult rootResult = editor.GetPoint(rootOptions);
            if (rootResult.Status != PromptStatus.OK)
                return;

            Matrix3d ucs = editor.CurrentUserCoordinateSystem;
            Point3d root = rootResult.Value.TransformBy(ucs);

            // 3. Settings.
            PowerWireSettings settings;
            using (PowerWireSettingsForm form = new PowerWireSettingsForm())
            {
                if (Application.ShowModalDialog(form) != System.Windows.Forms.DialogResult.OK)
                    return;
                settings = form.Settings;
            }

            double baseAngle = settings.UseUcsBaseAngle
                ? Math.Atan2(ucs.CoordinateSystem3d.Xaxis.Y, ucs.CoordinateSystem3d.Xaxis.X)
                : settings.BaseAngleDegrees * Math.PI / 180.0;

            // 4. Clean the lines, build the tree and plan the wire.
            PowerWireResult result = PowerWirePlanner.Run(
                sources.ConvertAll(s => s.Curve),
                settings,
                baseAngle,
                new Vec2(root.X, root.Y),
                Math.Max(PipeTransients.ViewSize() / 20.0, settings.WeldTolerance * 3));
            double elevation = sources[0].Curve.Elevation;

            if (!result.Ok)
            {
                editor.WriteMessage("\n" + result.Error);
                MarkProblems(editor, result.ErrorPoints, elevation, 1);
                return;
            }

            foreach (string note in result.Notes)
                editor.WriteMessage("\n" + note);
            foreach (string warning in result.Warnings)
                editor.WriteMessage("\nWarning: " + warning);

            editor.WriteMessage(
                "\nPower wire: {0} indoor unit(s) in series order U1 to U{0}, {1} hook(s), {2} wire(s){3}.",
                result.Units.Count,
                result.Hooks,
                result.Wires,
                result.Warnings.Count > 0 ? ", " + result.Warnings.Count + " plain drop(s)" : "");

            // 5. Preview and confirm.
            if (!Confirm(editor, result, elevation))
            {
                editor.WriteMessage("\nCancelled: nothing was changed.");
                return;
            }

            // 6. Write.
            using (PipeTemplateHolder template = new PipeTemplateHolder(editor))
            {
                string layerName = settings.LayerName;
                string note;
                string error;
                if (!PipeDrawingLibrary.EnsureNamedLayer(db, layerName, template.Open, out note, out error))
                {
                    editor.WriteMessage("\n" + error + " Nothing was changed.");
                    return;
                }

                if (note != null)
                    editor.WriteMessage("\n" + note);

                PowerWireWriteRequest request = new PowerWireWriteRequest
                {
                    Sources = sources,
                    Result = result,
                    Settings = settings,
                    LayerName = layerName
                };
                if (settings.WriteText)
                {
                    request.TextStyle = PipeDrawingLibrary.FindTextStyle(db, PowerWireSettings.TextStyleName);
                    if (request.TextStyle.IsNull)
                    {
                        editor.WriteMessage(
                            "\nText style {0} is not in the drawing: the cable text uses the current style.",
                            PowerWireSettings.TextStyleName);
                    }
                }

                PowerWireWriteSummary written;
                if (!PowerWireWriter.Write(db, request, out written, out error))
                {
                    editor.WriteMessage("\n" + error + " Nothing was changed.");
                    return;
                }

                editor.WriteMessage(
                    "\nWrote {0} wire polyline(s) on {1}{2}, replacing {3} original curve(s).",
                    written.Wires,
                    layerName,
                    written.Spurs > 0 ? " and " + written.Spurs + " plain drop(s)" : "",
                    written.Erased);
                if (settings.WriteText)
                {
                    editor.WriteMessage(
                        "\nCable text '{0}': {1} written (height {2} mm), {3} of an earlier run replaced.",
                        settings.CableText,
                        written.Texts,
                        settings.TextHeight.ToString("0.###", CultureInfo.InvariantCulture),
                        written.TextsRemoved);
                    if (written.TextsLocked > 0)
                    {
                        editor.WriteMessage(
                            "\n{0} earlier cable text(s) on a locked layer were left in place.", written.TextsLocked);
                    }
                }

                if (written.OtherNetworks > 0)
                    editor.WriteMessage("\n{0} line(s) of another network were kept as cleaned.", written.OtherNetworks);

                RefrigResult clean = result.Clean;
                if (clean != null)
                {
                    editor.WriteMessage(
                        "\nJoints moved: {0}. Collinear nodes merged: {1}. Jogs merged: {2}. Zero-length lines removed: {3}. "
                        + "Duplicate lines removed: {4}.",
                        clean.NodesMoved, clean.NodesDissolved, clean.JogsMerged, clean.ZeroLengthRemoved,
                        clean.DuplicatesRemoved);
                }

                if (result.Warnings.Count > 0)
                {
                    editor.WriteMessage(
                        "\n{0} unit(s) kept a plain drop instead of a hook; check these:", result.Warnings.Count);
                    foreach (string warning in result.Warnings)
                        editor.WriteMessage("\n  " + warning);
                }
            }
        }

        // Shows the wire (green), the supply end (red circle), each unit's number and the plain drops
        // (yellow circles), then asks. Yes is the default.
        private static bool Confirm(Editor editor, PowerWireResult result, double elevation)
        {
            double view = PipeTransients.ViewSize();
            using (PipeTransients preview = new PipeTransients())
            {
                foreach (PowerWireRun run in result.Runs)
                    preview.AddPolyline(run.Points, elevation, 3, view / 400.0);

                preview.AddCircle(result.Root, view / 40.0, elevation, 1);

                foreach (PowerWireUnit unit in result.Units)
                {
                    preview.AddText(
                        unit.Terminal + new Vec2(view / 80.0, view / 80.0), unit.Label, view / 60.0, 7, elevation);
                }

                foreach (Vec2 point in result.WarningPoints)
                    preview.AddCircle(point, view / 40.0, elevation, 2);

                editor.WriteMessage(
                    "\nPreview: the wire in green from the supply end (red circle), {0} unit(s) numbered in series order, "
                    + "{1} plain drop(s) circled in yellow.",
                    result.Units.Count, result.WarningPoints.Count);

                PromptKeywordOptions options = new PromptKeywordOptions(
                    "\nDraw the power wire? [Yes/No] <Yes>: ", "Yes No");
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
