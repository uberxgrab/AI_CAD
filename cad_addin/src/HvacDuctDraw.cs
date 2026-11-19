using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Commands
{
    // HVACDUCT: draws duct runs as dynamic duct blocks plus elbow, transition and tap fittings.
    // This class only prompts and previews; DuctRouteSession does the solving and writing, and is
    // tested headless in tests/DuctAcadTests. See AGENTS.md, "HVACDUCT: draw duct runs".
    public class HvacDuctDrawCommand
    {
        [CommandMethod("HVACDUCT")]
        public void HvacDuct()
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
                editor.WriteMessage("\nHVACDUCT stopped: " + ex.Message);
            }
        }

        // Opens the template only when the drawing lacks a layer or block the next point needs.
        private sealed class TemplateHolder : IDisposable
        {
            private readonly string _path;
            private bool _failed;

            public TemplateHolder(string path)
            {
                _path = path;
            }

            public TemplateSource Source { get; private set; }

            public TemplateSource Open(Editor editor)
            {
                if (Source != null || _failed || string.IsNullOrEmpty(_path))
                    return Source;

                editor.WriteMessage("\nReading the template...");
                string error;
                Source = TemplateSource.TryOpen(_path, out error);
                if (Source == null)
                {
                    _failed = true;
                    editor.WriteMessage("\n" + error);
                }

                return Source;
            }

            public void Dispose()
            {
                if (Source != null)
                    Source.Dispose();
            }
        }

        private static void Run(Document document)
        {
            Database db = document.Database;
            Editor editor = document.Editor;

            string templatePath = TemplateLocator.GetTemplatePath(editor, false);
            TemplateCatalog catalog = null;
            if (templatePath != null)
            {
                string catalogError;
                catalog = TemplateCatalog.Read(templatePath, out catalogError);
                if (catalog == null)
                    editor.WriteMessage("\n" + catalogError);
            }
            else
            {
                editor.WriteMessage(
                    "\nNo template chosen: only the sizes and layers already in this drawing are offered.");
            }

            List<DuctSize> sizes = DuctBlockLibrary.ListSizes(db);
            List<string> layers = DuctBlockLibrary.ListDuctLayers(db);
            if (catalog != null)
            {
                foreach (DuctSize size in catalog.Sizes)
                {
                    if (!sizes.Contains(size))
                        sizes.Add(size);
                }

                foreach (string layer in catalog.DuctLayers)
                {
                    if (!layers.Contains(layer))
                        layers.Add(layer);
                }

                sizes.Sort(TemplateCatalog.CompareSizes);
                layers.Sort(StringComparer.OrdinalIgnoreCase);
            }

            if (layers.Count == 0)
            {
                editor.WriteMessage("\nNo duct system layers were found in the template or the drawing.");
                return;
            }

            DuctDrawForm form = new DuctDrawForm(sizes, layers);
            if (Application.ShowModalDialog(form) != System.Windows.Forms.DialogResult.OK)
                return;

            DuctDrawSettings settings = form.Settings;
            DuctSize startSize = form.ChosenSize;
            Matrix3d ucs = editor.CurrentUserCoordinateSystem;

            using (TemplateHolder template = new TemplateHolder(templatePath))
            {
                DuctRouteSession session = new DuctRouteSession(
                    db,
                    null,
                    catalog == null ? null : catalog.Sizes,
                    settings.Fittings,
                    settings.Layer,
                    startSize);

                if (!ChooseStart(document, session, template, settings, startSize, ucs))
                    return;

                DrawRun(document, session, template, settings, ucs);
            }
        }

        // ------------------------------------------------------------------ start point

        private static bool ChooseStart(
            Document document,
            DuctRouteSession session,
            TemplateHolder template,
            DuctDrawSettings settings,
            DuctSize size,
            Matrix3d ucs)
        {
            Database db = document.Database;
            Editor editor = document.Editor;

            while (true)
            {
                PromptPointOptions options = new PromptPointOptions(
                    "\nSpecify start point (an end face continues that duct, a side wall starts a branch): ");
                options.AllowNone = true;
                PromptPointResult result = editor.GetPoint(options);
                if (result.Status != PromptStatus.OK)
                    return false;

                Point3d world = result.Value.TransformBy(ucs);
                Vec2 point = new Vec2(world.X, world.Y);
                session.CurrentSize = size;

                DuctInfo duct;
                DuctHit hit;
                string error;
                if (!DuctBlockLibrary.FindDuctAt(db, point, PickTolerance(), null, out duct, out hit))
                {
                    session.StartFree(point, world.Z);
                    return true;
                }

                if (hit.Kind == DuctHitKind.EndFace || hit.Kind == DuctHitKind.StartFace)
                {
                    if (session.StartContinue(duct, hit.Kind == DuctHitKind.EndFace, out error))
                    {
                        editor.WriteMessage("\nContinuing from the {0} of a {1} duct.",
                            hit.Kind == DuctHitKind.EndFace ? "end" : "start", duct.Size.Label);
                        return true;
                    }

                    editor.WriteMessage("\n" + error);
                    continue;
                }

                if (session.StartTap(duct, hit, out error))
                {
                    editor.WriteMessage("\nBranch from the side wall of a {0} duct, with a tap.", duct.Size.Label);
                    return true;
                }

                editor.WriteMessage("\n" + error);
            }
        }

        // ------------------------------------------------------------------ the loop

        private static void DrawRun(
            Document document,
            DuctRouteSession session,
            TemplateHolder template,
            DuctDrawSettings settings,
            Matrix3d ucs)
        {
            Database db = document.Database;
            Editor editor = document.Editor;
            double tolerance = PickTolerance();

            using (DuctPreview preview = new DuctPreview(editor, session))
            {
                while (true)
                {
                    Vec2 current = session.CurrentPoint;
                    PromptPointOptions options = new PromptPointOptions(
                        "\nNext point or [Size/Undo] <" + Number(settings.SegmentLength) + ">: ");
                    options.Keywords.Add("Size");
                    options.Keywords.Add("Undo");
                    options.AllowNone = true;
                    options.UseBasePoint = true;
                    options.BasePoint = new Point3d(current.X, current.Y, session.Elevation)
                        .TransformBy(ucs.Inverse());
                    options.UseDashedLine = true;

                    PromptPointResult result;
                    preview.Begin(ucs);
                    try
                    {
                        result = editor.GetPoint(options);
                    }
                    finally
                    {
                        preview.End();
                    }

                    Vec2 point;
                    if (result.Status == PromptStatus.Keyword)
                    {
                        if (string.Equals(result.StringResult, "Undo", StringComparison.OrdinalIgnoreCase))
                            UndoLast(editor, session);
                        else
                            ChangeSize(editor, session, template, settings.Layer, db);
                        continue;
                    }

                    if (result.Status == PromptStatus.OK)
                    {
                        Point3d world = result.Value.TransformBy(ucs);
                        point = new Vec2(world.X, world.Y);
                    }
                    else if (result.Status == PromptStatus.None)
                    {
                        // Enter: the dialog length toward the cursor, else along the last segment, else +X.
                        Vec2 direction = new Vec2(1, 0);
                        if (preview.LastCursor.HasValue
                            && (preview.LastCursor.Value - current).Length > 1e-6)
                            direction = (preview.LastCursor.Value - current).Normalized();
                        else if (session.LastDirection.HasValue)
                            direction = session.LastDirection.Value;
                        if (session.ForcedDirection.HasValue)
                            direction = session.ForcedDirection.Value;
                        point = current + direction * settings.SegmentLength;
                    }
                    else
                    {
                        return;
                    }

                    point = session.Constrain(point);
                    PrepareTemplate(editor, db, session, template, settings.Layer);

                    string error;
                    List<string> notes;

                    // Optional: a point on another duct's side wall ends the run with a tap.
                    DuctInfo main;
                    DuctHit wall;
                    if (DuctBlockLibrary.FindDuctAt(db, point, tolerance, session.OwnIds, out main, out wall)
                        && (wall.Kind == DuctHitKind.LeftWall || wall.Kind == DuctHitKind.RightWall)
                        && session.TryFinishOnWall(main, wall, out error, out notes))
                    {
                        Report(editor, notes);
                        editor.WriteMessage("\nThe run ends with a tap on the side wall of a {0} duct.",
                            main.Size.Label);
                        return;
                    }

                    if (session.TryAddPoint(point, out error, out notes))
                        Report(editor, notes);
                    else
                        editor.WriteMessage("\nPoint refused: " + error);
                }
            }
        }

        private static void UndoLast(Editor editor, DuctRouteSession session)
        {
            string message;
            if (session.Undo(out message))
                editor.WriteMessage("\nLast segment and its fittings removed.");
            else
                editor.WriteMessage("\n" + message);
        }

        private static void ChangeSize(
            Editor editor,
            DuctRouteSession session,
            TemplateHolder template,
            string layer,
            Database db)
        {
            PromptStringOptions options = new PromptStringOptions(
                "\nNew size (600x300 or D350) <" + session.CurrentSize.Label + ">: ");
            options.AllowSpaces = true;
            PromptResult result = editor.GetString(options);
            if (result.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(result.StringResult))
                return;

            DuctSize size;
            if (!DuctSize.TryParseAny(result.StringResult, session.CurrentSize.IsRound, out size))
            {
                editor.WriteMessage("\nNot a duct size: {0}", result.StringResult);
                return;
            }

            session.CurrentSize = size;
            editor.WriteMessage(
                "\nThe next segment is {0}; a transition is drawn at the current point.", size.Label);
        }

        private static void PrepareTemplate(
            Editor editor,
            Database db,
            DuctRouteSession session,
            TemplateHolder template,
            string layer)
        {
            if (session.Template != null)
                return;

            ObjectId id;
            if (DuctBlockLibrary.TryFindBlock(db, session.CurrentSize, out id)
                && DuctBlockLibrary.LayerExists(db, layer))
                return;

            session.Template = template.Open(editor);
        }

        private static void Report(Editor editor, List<string> notes)
        {
            foreach (string note in notes)
                editor.WriteMessage("\n" + note);
        }

        // About one percent of the view height: a pick box in drawing units.
        private static double PickTolerance()
        {
            try
            {
                double view = Convert.ToDouble(
                    Application.GetSystemVariable("VIEWSIZE"), CultureInfo.InvariantCulture);
                return Math.Max(view / 100.0, 5.0);
            }
            catch (System.Exception)
            {
                return 25.0;
            }
        }

        private static string Number(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ preview

        // Shows the pending duct and the fittings at the previous vertex as transient graphics while
        // GetPoint waits. A failure here must never stop the user picking a point.
        private sealed class DuctPreview : IDisposable
        {
            private readonly Editor _editor;
            private readonly DuctRouteSession _session;
            private readonly List<Entity> _shown = new List<Entity>();
            private bool _attached;
            private bool _ortho;
            private Vec2 _uAxis = new Vec2(1, 0);

            public DuctPreview(Editor editor, DuctRouteSession session)
            {
                _editor = editor;
                _session = session;
            }

            /// <summary>The last cursor point, for Enter.</summary>
            public Vec2? LastCursor { get; private set; }

            public void Begin(Matrix3d ucs)
            {
                LastCursor = null;
                try
                {
                    _ortho = Convert.ToInt32(
                        Application.GetSystemVariable("ORTHOMODE"), CultureInfo.InvariantCulture) != 0;
                }
                catch (System.Exception)
                {
                    _ortho = false;
                }

                Vector3d x = ucs.CoordinateSystem3d.Xaxis;
                _uAxis = new Vec2(x.X, x.Y).Normalized();
                if (_uAxis.Length < 0.5)
                    _uAxis = new Vec2(1, 0);

                _editor.PointMonitor += OnPointMonitor;
                _attached = true;
            }

            public void End()
            {
                if (_attached)
                {
                    _editor.PointMonitor -= OnPointMonitor;
                    _attached = false;
                }

                Clear();
            }

            private void OnPointMonitor(object sender, PointMonitorEventArgs e)
            {
                try
                {
                    if (!e.Context.PointComputed)
                        return;

                    Point3d computed = e.Context.ComputedPoint;
                    Vec2 cursor = new Vec2(computed.X, computed.Y);
                    Vec2 current = _session.CurrentPoint;

                    // Ortho is applied here for the preview unless AutoCAD already did, or an object
                    // snap or typed input decided the point.
                    PointHistoryBits history = e.Context.History;
                    bool decided = (history & (PointHistoryBits.ObjectSnapped
                        | PointHistoryBits.Ortho | PointHistoryBits.FromKeyboard)) != 0;
                    if (_ortho && !decided)
                    {
                        Vec2 v = _uAxis.Left;
                        Vec2 d = cursor - current;
                        double a = d.Dot(_uAxis);
                        double b = d.Dot(v);
                        cursor = Math.Abs(a) >= Math.Abs(b) ? current + _uAxis * a : current + v * b;
                    }

                    cursor = _session.Constrain(cursor);
                    LastCursor = cursor;

                    Clear();
                    if ((cursor - current).Length < 1e-6)
                        return;

                    PreviewShapes shapes = _session.Preview(cursor);
                    foreach (Outline outline in shapes.Ducts)
                        Show(outline, 3);
                    foreach (Outline outline in shapes.Fittings)
                        Show(outline, 2);
                }
                catch (System.Exception)
                {
                    Clear();
                }
            }

            private void Show(Outline outline, short colorIndex)
            {
                Polyline polyline = new Polyline();
                for (int i = 0; i < outline.Vertices.Count; i++)
                {
                    OutlineVertex vertex = outline.Vertices[i];
                    polyline.AddVertexAt(i, new Point2d(vertex.X, vertex.Y), vertex.Bulge, 0, 0);
                }

                polyline.Closed = outline.Closed;
                polyline.Elevation = _session.Elevation;
                polyline.ColorIndex = colorIndex;
                Autodesk.AutoCAD.GraphicsInterface.TransientManager.CurrentTransientManager.AddTransient(
                    polyline,
                    Autodesk.AutoCAD.GraphicsInterface.TransientDrawingMode.DirectShortTerm,
                    128,
                    new IntegerCollection());
                _shown.Add(polyline);
            }

            private void Clear()
            {
                foreach (Entity entity in _shown)
                {
                    try
                    {
                        Autodesk.AutoCAD.GraphicsInterface.TransientManager.CurrentTransientManager
                            .EraseTransient(entity, new IntegerCollection());
                    }
                    catch (System.Exception)
                    {
                    }

                    entity.Dispose();
                }

                _shown.Clear();
            }

            public void Dispose()
            {
                End();
            }
        }
    }
}
