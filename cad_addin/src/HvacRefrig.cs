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
    // HVACREFRIG: cleans a rough refrigerant LINE/LWPOLYLINE network (weld, orthogonalise, collapse
    // jogs, merge collinear pipes) and puts a Branch Connection (1) refnet at each branch, set back from
    // the T node. This class only prompts and previews; RefrigCleaner (AutoCAD-free) does the work and
    // RefrigWriter writes the result in one transaction. It reports the refnet piping rules (R1, R2, R3)
    // as warnings, before the confirmation and again in the summary. It does not size pipes: use
    // HVACPIPESIZE with the RP layers.
    // See AGENTS.md, "HVACREFRIG: clean up a rough refrigerant network".
    public class HvacRefrigCommand
    {
        [CommandMethod("HVACREFRIG", CommandFlags.UsePickSet)]
        public void HvacRefrig()
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
                editor.WriteMessage("\nHVACREFRIG stopped: " + ex.Message);
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

            // 2. The root: the outdoor-unit end.
            PromptPointOptions rootOptions = new PromptPointOptions(
                "\nPick the outdoor-unit end of the network (the root): ");
            PromptPointResult rootResult = editor.GetPoint(rootOptions);
            if (rootResult.Status != PromptStatus.OK)
                return;

            Matrix3d ucs = editor.CurrentUserCoordinateSystem;
            Point3d root = rootResult.Value.TransformBy(ucs);

            // 3. Settings.
            RefrigSettings settings;
            using (RefrigSettingsForm form = new RefrigSettingsForm())
            {
                if (Application.ShowModalDialog(form) != System.Windows.Forms.DialogResult.OK)
                    return;
                settings = form.Settings;
            }

            double baseAngle = settings.UseUcsBaseAngle
                ? Math.Atan2(ucs.CoordinateSystem3d.Xaxis.Y, ucs.CoordinateSystem3d.Xaxis.X)
                : settings.BaseAngleDegrees * Math.PI / 180.0;

            // 4. Clean.
            RefrigInput input = new RefrigInput
            {
                Curves = sources.ConvertAll(s => s.Curve),
                Settings = settings,
                BaseAngle = baseAngle,
                RootPoint = new Vec2(root.X, root.Y),
                RootTolerance = Math.Max(PipeTransients.ViewSize() / 20.0, settings.WeldTolerance * 3),
                ExistingRefnetPorts = RefrigWriter.FindRefnetPorts(db)
            };
            RefrigResult result = RefrigCleaner.Run(input);
            double elevation = sources[0].Curve.Elevation;

            if (!result.Ok)
            {
                editor.WriteMessage("\n" + result.Error);
                MarkProblems(editor, result.ErrorPoints, elevation, 1);
                return;
            }

            foreach (string report in result.Reports)
                editor.WriteMessage("\n" + report);

            WriteRules(editor, result, settings);

            if (!result.Changed)
            {
                editor.WriteMessage("\nThe network is already clean and needs no refnets: nothing was changed.");
                MarkProblems(editor, CirclePoints(result), elevation, 2);
                return;
            }

            // 5. Preview and confirm.
            if (!Confirm(editor, result, elevation))
            {
                editor.WriteMessage("\nCancelled: nothing was changed.");
                return;
            }

            // 6. Write.
            ObjectId refnetBlock = ObjectId.Null;
            using (PipeTemplateHolder template = new PipeTemplateHolder(editor))
            {
                string error;
                if (result.Refnets.Count > 0)
                {
                    string note;
                    if (!RefrigWriter.EnsureRefnetBlock(db, template.Open, out refnetBlock, out note, out error))
                    {
                        editor.WriteMessage("\n" + error + " Nothing was changed.");
                        return;
                    }

                    if (note != null)
                        editor.WriteMessage("\n" + note);
                }

                WriteSummary written;
                if (!RefrigWriter.Write(db, sources, result, refnetBlock, out written, out error))
                {
                    editor.WriteMessage("\n" + error + " Nothing was changed.");
                    return;
                }

                editor.WriteMessage(
                    "\nWrote {0} polyline(s) and {1} refnet(s), replacing {2} original curve(s).",
                    written.Paths, written.Refnets, written.Erased);
                editor.WriteMessage(
                    "\nJoints moved: {0}. Collinear nodes merged: {1}. Jogs merged: {2}. Zero-length pipes removed: {3}. "
                    + "Duplicate pipes removed: {4}.",
                    result.NodesMoved, result.NodesDissolved, result.JogsMerged, result.ZeroLengthRemoved,
                    result.DuplicatesRemoved);
                if (result.Reports.Count > 0)
                    editor.WriteMessage("\n{0} case(s) were reported above and left unchanged.", result.Reports.Count);
                WriteWarningSummary(editor, result);
            }
        }

        // The R3 distance (when there is a refnet) and every rule warning, one line each.
        private static void WriteRules(Editor editor, RefrigResult result, RefrigSettings settings)
        {
            if (result.FarthestUnitDistance >= 0)
            {
                editor.WriteMessage(
                    "\nR3: the farthest indoor unit is {0} m of pipe from the inlet of the first refnet "
                    + "(limit {1} m, warning above {2} m).",
                    Metres(result.FarthestUnitDistance),
                    Metres(settings.MaxUnitRun),
                    Metres(settings.MaxUnitRun - settings.RunAllowance));
            }

            foreach (string warning in result.Warnings)
                editor.WriteMessage("\nWarning: " + warning);
        }

        private static void WriteWarningSummary(Editor editor, RefrigResult result)
        {
            if (result.Warnings.Count == 0)
                return;

            editor.WriteMessage(
                "\n{0} piping rule warning(s). The refnets were still placed; check these:", result.Warnings.Count);
            foreach (string warning in result.Warnings)
                editor.WriteMessage("\n  " + warning);
        }

        private static string Metres(double millimetres)
        {
            return (millimetres / 1000.0).ToString("0.00", CultureInfo.InvariantCulture);
        }

        // Where a circle goes: the cases left alone and the rule warnings.
        private static List<Vec2> CirclePoints(RefrigResult result)
        {
            List<Vec2> points = new List<Vec2>(result.ReportPoints);
            points.AddRange(result.WarningPoints);
            return points;
        }

        // Shows the cleaned pipes (green), the refnet bodies (magenta) and the cases left alone (yellow
        // circles), then asks. Yes is the default.
        private static bool Confirm(Editor editor, RefrigResult result, double elevation)
        {
            double view = PipeTransients.ViewSize();
            using (PipeTransients preview = new PipeTransients())
            {
                foreach (RefrigPath path in result.Paths)
                    preview.AddPolyline(path.Points, path.Elevation, 3, view / 400.0);

                foreach (RefnetPlacement refnet in result.Refnets)
                {
                    preview.AddPolyline(
                        new List<Vec2> { refnet.Inlet, refnet.MainPort, refnet.BranchPort },
                        refnet.Elevation, 6, view / 150.0);
                }

                foreach (Vec2 point in CirclePoints(result))
                    preview.AddCircle(point, view / 40.0, elevation, 2);

                editor.WriteMessage(
                    "\nPreview: {0} pipe run(s) in green, {1} refnet(s) in magenta, {2} case(s) left alone and "
                    + "{3} rule warning(s) circled in yellow.",
                    result.Paths.Count, result.Refnets.Count, result.ReportPoints.Count, result.WarningPoints.Count);

                PromptKeywordOptions options = new PromptKeywordOptions(
                    "\nApply the cleaned network? [Yes/No] <Yes>: ", "Yes No");
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
