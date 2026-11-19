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
    // HVACPIPESIZE: pick a pipe, the command finds the run it belongs to, highlights it, asks for a
    // pipe type and size, and moves the run onto that size's layer. This class only prompts and shows
    // graphics; PipeNetwork and PipeRunFinder (AutoCAD-free) find the run and PipeDrawingLibrary
    // cuts the curves. See AGENTS.md, "HVACPIPESIZE: size a pipe run".
    public class HvacPipeSizeCommand
    {
        [CommandMethod("HVACPIPESIZE")]
        public void HvacPipeSize()
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
                editor.WriteMessage("\nHVACPIPESIZE stopped: " + ex.Message);
            }
        }

        private static void Run(Document document)
        {
            Editor editor = document.Editor;
            PipeSizeSettings settings = PipeSizeSettingsStore.Load();

            using (PipeTemplateHolder template = new PipeTemplateHolder(editor))
            {
                while (true)
                {
                    PromptEntityOptions options = new PromptEntityOptions(
                        "\nPick a pipe (LINE or polyline) to size, or [Weld] to set the weld tolerance (now "
                        + Number(settings.WeldTolerance) + " mm); Enter to finish: ");
                    options.SetRejectMessage("\nPick a LINE or a polyline.");
                    options.AddAllowedClass(typeof(Line), true);
                    options.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline), true);
                    options.Keywords.Add("Weld");
                    options.AllowNone = true;

                    PromptEntityResult picked = editor.GetEntity(options);
                    if (picked.Status == PromptStatus.Keyword)
                    {
                        AskWeldTolerance(editor, settings);
                        continue;
                    }

                    if (picked.Status != PromptStatus.OK)
                        return;

                    try
                    {
                        SizeOneRun(document, template, settings, picked);
                    }
                    catch (System.Exception ex)
                    {
                        // Report and go on to the next pick: one bad run must not end the command.
                        editor.WriteMessage("\nThat run was not changed: " + ex.Message);
                    }
                }
            }
        }

        private static void AskWeldTolerance(Editor editor, PipeSizeSettings settings)
        {
            PromptDoubleOptions options = new PromptDoubleOptions(
                "\nEndpoints closer than this are one joint (mm) <" + Number(settings.WeldTolerance) + ">: ");
            options.AllowNone = true;
            options.AllowNegative = false;
            options.AllowZero = false;
            PromptDoubleResult result = editor.GetDouble(options);
            if (result.Status != PromptStatus.OK)
                return;

            PipeSizeSettings candidate = new PipeSizeSettings
            {
                Kind = settings.Kind,
                SizeText = settings.SizeText,
                WeldTolerance = result.Value
            };
            string error;
            if (!candidate.IsValid(out error))
            {
                editor.WriteMessage("\n" + error);
                return;
            }

            settings.WeldTolerance = result.Value;
            PipeSizeSettingsStore.Save(settings);
        }

        private static void SizeOneRun(
            Document document,
            PipeTemplateHolder template,
            PipeSizeSettings settings,
            PromptEntityResult picked)
        {
            Database db = document.Database;
            Editor editor = document.Editor;

            // Every LINE and LWPOLYLINE on a pipe layer (the drawing's catalogue and unsized layers), plus
            // the picked curve's own layer.
            string pickedLayer = LayerOf(db, picked.ObjectId);
            HashSet<string> pipeLayers = PipeDrawingLibrary.ListPipeLayers(db);
            int skipped;
            List<PipeSource> sources = PipeDrawingLibrary.ReadCurves(
                db,
                PipeDrawingLibrary.CurrentSpaceIds(db),
                layer => pipeLayers.Contains(layer)
                    || string.Equals(layer, pickedLayer, StringComparison.OrdinalIgnoreCase),
                out skipped);

            int pickedIndex = sources.FindIndex(s => s.Id == picked.ObjectId);
            if (pickedIndex < 0)
            {
                editor.WriteMessage(
                    "\nThat curve cannot be used. Polylines must lie in the XY plane and have two or more vertices.");
                return;
            }

            if (skipped > 0)
                editor.WriteMessage("\n{0} polyline(s) not in the XY plane were ignored.", skipped);

            List<PipeCurve> curves = sources.ConvertAll(s => s.Curve);
            PipeNetwork net = PipeNetwork.Build(curves, settings.WeldTolerance);
            Vec2 point = PickedPointOnCurve(db, editor, picked);
            int edge = PipeRunFinder.FindEdge(net, pickedIndex, point);
            if (edge < 0)
            {
                editor.WriteMessage("\nThat curve is too short to be a pipe segment.");
                return;
            }

            PipeRun run = PipeRunFinder.Find(net, edge);
            foreach (int index in run.Edges)
            {
                PipeSource source = sources[net.Edges[index].Curve];
                if (source.Locked)
                {
                    editor.WriteMessage(
                        "\nThe run includes a curve on the locked layer {0}. Unlock it first.", source.Curve.Layer);
                    return;
                }
            }

            List<CurveSplitPlan> plans = PipeRunFinder.PlanSplits(net, run);
            double elevation = sources[pickedIndex].Curve.Elevation;
            string summary = string.Format(
                CultureInfo.InvariantCulture,
                "Run: {0} segment(s), {1} m, {2} curve(s).",
                run.Edges.Count,
                (run.Length / 1000.0).ToString("0.##", CultureInfo.InvariantCulture),
                plans.Count);

            PipeKind kind;
            string size;
            using (PipeTransients highlight = new PipeTransients())
            {
                double width = PipeTransients.ViewSize() / 300.0;
                foreach (Outline outline in PipeRunFinder.Outlines(net, run))
                    highlight.AddOutline(outline, elevation, 6, width);

                editor.WriteMessage("\n" + summary);
                PipeSizeForm form = new PipeSizeForm(settings, summary, run.Layer);
                if (Application.ShowModalDialog(form) != System.Windows.Forms.DialogResult.OK)
                    return;
                kind = form.ChosenKind;
                size = form.ChosenSize;
            }

            string layerName;
            string note;
            string error;
            if (!PipeDrawingLibrary.EnsureLayer(db, kind, size, template.Open, out layerName, out note, out error))
            {
                editor.WriteMessage("\n" + error);
                return;
            }

            if (note != null)
                editor.WriteMessage("\n" + note);

            if (string.Equals(run.Layer, layerName, StringComparison.OrdinalIgnoreCase))
            {
                editor.WriteMessage("\nThe run is already on layer {0}: nothing was changed.", layerName);
                return;
            }

            ApplySummary result;
            if (!PipeDrawingLibrary.ApplyRun(db, sources, plans, layerName, out result, out error))
            {
                editor.WriteMessage("\n" + error);
                return;
            }

            editor.WriteMessage(
                "\nMoved {0} m of pipe to layer {1}: {2} curve(s) changed, {3} new piece(s) created.",
                (run.Length / 1000.0).ToString("0.##", CultureInfo.InvariantCulture),
                layerName,
                result.Curves,
                result.NewEntities);
        }

        private static string LayerOf(Database db, ObjectId id)
        {
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                Entity entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                string layer = entity.Layer;
                transaction.Commit();
                return layer;
            }
        }

        // The picked point, as a plan point. PickedPoint is in WCS for GetEntity; if the UCS is not the
        // WCS and the point read as UCS lies nearer the curve, use that reading instead.
        private static Vec2 PickedPointOnCurve(Database db, Editor editor, PromptEntityResult picked)
        {
            Point3d asWorld = picked.PickedPoint;
            Point3d asUcs = asWorld.TransformBy(editor.CurrentUserCoordinateSystem);
            Point3d chosen = asWorld;

            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                Curve curve = transaction.GetObject(picked.ObjectId, OpenMode.ForRead) as Curve;
                if (curve != null)
                {
                    double world = curve.GetClosestPointTo(asWorld, false).DistanceTo(asWorld);
                    double ucs = curve.GetClosestPointTo(asUcs, false).DistanceTo(asUcs);
                    if (ucs < world)
                        chosen = asUcs;
                }

                transaction.Commit();
            }

            return new Vec2(chosen.X, chosen.Y);
        }

        private static string Number(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
