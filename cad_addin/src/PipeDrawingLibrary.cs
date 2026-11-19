using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using HvacAiCadAddin.Commands;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Pipes
{
    // Non-interactive AutoCAD side of HVACPIPESIZE: reading LINEs and LWPOLYLINEs into the plain
    // pipe core, making sure a pipe layer exists, and splitting curves at the run boundaries. It
    // needs only AcDbMgd and AcCoreMgd, so a headless accoreconsole test can link it. Nothing
    // here prompts or shows graphics. See AGENTS.md, "HVACPIPESIZE: size a pipe run".

    /// <summary>A drawing curve read into the pipe core.</summary>
    internal sealed class PipeSource
    {
        public ObjectId Id;
        /// <summary><see cref="PipeCurve.Id"/> is the index of this source in the list it came from.</summary>
        public PipeCurve Curve;
        /// <summary>The curve's layer is locked, so the curve cannot be changed.</summary>
        public bool Locked;
    }

    internal sealed class ApplySummary
    {
        /// <summary>Curves that were cut or moved onto the new layer.</summary>
        public int Curves;
        /// <summary>Pieces that went onto the new layer.</summary>
        public int RunPieces;
        /// <summary>Pieces created by cutting (new entities).</summary>
        public int NewEntities;
    }

    /// <summary>Opens the template only when a layer or block is missing, and remembers a failure.</summary>
    internal sealed class PipeTemplateHolder : IDisposable
    {
        private readonly Editor _editor;
        private bool _tried;

        public PipeTemplateHolder(Editor editor)
        {
            _editor = editor;
        }

        public TemplateSource Source { get; private set; }

        public TemplateSource Open()
        {
            if (Source != null || _tried)
                return Source;
            _tried = true;

            string path = TemplateLocator.GetTemplatePath(_editor, false);
            if (string.IsNullOrEmpty(path))
            {
                _editor.WriteMessage("\nNo template chosen.");
                return null;
            }

            _editor.WriteMessage("\nReading the template...");
            string error;
            Source = TemplateSource.TryOpen(path, out error);
            if (Source == null)
                _editor.WriteMessage("\n" + error);
            return Source;
        }

        public void Dispose()
        {
            if (Source != null)
                Source.Dispose();
        }
    }

    internal static class PipeDrawingLibrary
    {
        private const double ParameterEpsilon = 1e-9;

        // ------------------------------------------------------------------ layers

        /// <summary>The drawing's layers whose names look like pipe layers.</summary>
        internal static HashSet<string> ListPipeLayers(Database db)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                LayerTable table = (LayerTable)transaction.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    LayerTableRecord layer = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForRead);
                    if (PipeCatalog.IsPipeLayer(layer.Name))
                        names.Add(layer.Name);
                }

                transaction.Commit();
            }

            return names;
        }

        internal static bool LayerExists(Database db, string layerName)
        {
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                LayerTable table = (LayerTable)transaction.GetObject(db.LayerTableId, OpenMode.ForRead);
                bool exists = table.Has(layerName);
                transaction.Commit();
                return exists;
            }
        }

        /// <summary>
        /// Makes sure the catalogue layer for a type and size exists. A layer the template holds is
        /// imported. A new CWS, CWR, HWS or HWR layer is created: its colour comes from the CHW table by
        /// size, and its linetype is copied from the template (the CWS/CWR linetypes of
        /// <c>EIA_1 COOLING WATER SUPPLY</c>/<c>RETURN</c>, or those of <c>EIA_1 HOT WATER SUPPLY</c>/<c>RETURN</c>).
        /// Call with no transaction open.
        /// </summary>
        /// <param name="template">Returns the opened template, or null when it is unavailable.</param>
        internal static bool EnsureLayer(
            Database db,
            PipeKind kind,
            string sizeText,
            Func<TemplateSource> template,
            out string layerName,
            out string note,
            out string error)
        {
            note = null;
            error = null;
            layerName = PipeCatalog.LayerName(kind, sizeText);
            if (layerName == null)
            {
                error = "Size " + sizeText + " is not offered for " + PipeCatalog.Label(kind) + ".";
                return false;
            }

            if (LayerExists(db, layerName))
                return true;

            TemplateSource source = template == null ? null : template();
            if (source == null)
            {
                error = "Layer '" + layerName + "' is not in the drawing and the template is unavailable.";
                return false;
            }

            List<string> missing;
            source.ImportLayers(db, new[] { layerName }, out missing);
            if (missing.Count == 0)
            {
                note = "Imported layer " + layerName + " from the template.";
                return true;
            }

            if (!PipeCatalog.IsCreatedByCommand(kind))
            {
                error = "Layer '" + layerName + "' is in neither the drawing nor the template.";
                return false;
            }

            return CreateWaterLayer(db, source, kind, sizeText, layerName, out note, out error);
        }

        private static bool CreateWaterLayer(
            Database db,
            TemplateSource template,
            PipeKind kind,
            string sizeText,
            string layerName,
            out string note,
            out string error)
        {
            error = null;
            string sourceLayer = PipeCatalog.LinetypeSourceLayer(kind);
            string linetypeName = template.GetLayerLinetypeName(sourceLayer);

            ObjectId linetypeId = ObjectId.Null;
            if (linetypeName != null)
            {
                List<string> missing;
                Dictionary<string, ObjectId> imported = template.ImportLinetypes(
                    db, new[] { linetypeName }, out missing);
                if (!imported.TryGetValue(linetypeName, out linetypeId))
                    linetypeId = ObjectId.Null;
            }

            short colour = PipeCatalog.WaterColour(sizeText);
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                LayerTable table = (LayerTable)transaction.GetObject(db.LayerTableId, OpenMode.ForWrite);
                LayerTableRecord record = new LayerTableRecord
                {
                    Name = layerName,
                    Color = Color.FromColorIndex(ColorMethod.ByAci, colour)
                };
                if (!linetypeId.IsNull)
                    record.LinetypeObjectId = linetypeId;
                table.Add(record);
                transaction.AddNewlyCreatedDBObject(record, true);
                transaction.Commit();
            }

            note = "Created layer " + layerName + " (colour " + colour + ", linetype "
                + (linetypeId.IsNull ? "Continuous; the template has no layer " + sourceLayer : linetypeName)
                + ").";
            return true;
        }

        // ------------------------------------------------------------------ reading curves

        /// <summary>The ids of every entity in the current space.</summary>
        internal static List<ObjectId> CurrentSpaceIds(Database db)
        {
            List<ObjectId> ids = new List<ObjectId>();
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(
                    db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                    ids.Add(id);
                transaction.Commit();
            }

            return ids;
        }

        /// <summary>
        /// Reads LINEs and LWPOLYLINEs into pipe curves, in plan. Anything else is ignored.
        /// A polyline whose normal is not +Z, or with fewer than two vertices, counts as skipped.
        /// </summary>
        /// <param name="keep">Which layers to read; null reads all.</param>
        internal static List<PipeSource> ReadCurves(
            Database db,
            IEnumerable<ObjectId> ids,
            Func<string, bool> keep,
            out int skipped)
        {
            skipped = 0;
            List<PipeSource> sources = new List<PipeSource>();
            Dictionary<string, bool> locked = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                LayerTable layers = (LayerTable)transaction.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in ids)
                {
                    if (id.IsNull || id.IsErased)
                        continue;
                    string dxf = id.ObjectClass.DxfName;
                    if (dxf != "LINE" && dxf != "LWPOLYLINE")
                        continue;

                    Entity entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
                    if (entity == null)
                        continue;
                    string layer = entity.Layer;
                    if (keep != null && !keep(layer))
                        continue;

                    PipeCurve curve = new PipeCurve { Id = sources.Count, Layer = layer };
                    Line line = entity as Line;
                    if (line != null)
                    {
                        curve.Elevation = line.StartPoint.Z;
                        curve.Vertices.Add(new OutlineVertex(line.StartPoint.X, line.StartPoint.Y, 0));
                        curve.Vertices.Add(new OutlineVertex(line.EndPoint.X, line.EndPoint.Y, 0));
                    }
                    else
                    {
                        Polyline polyline = entity as Polyline;
                        if (polyline == null)
                            continue;
                        if (polyline.NumberOfVertices < 2 || polyline.Normal.Z < 0.999999)
                        {
                            skipped++;
                            continue;
                        }

                        curve.Elevation = polyline.Elevation;
                        curve.Closed = polyline.Closed;
                        for (int i = 0; i < polyline.NumberOfVertices; i++)
                        {
                            Point2d point = polyline.GetPoint2dAt(i);
                            curve.Vertices.Add(new OutlineVertex(point.X, point.Y, polyline.GetBulgeAt(i)));
                        }
                    }

                    bool isLocked;
                    if (!locked.TryGetValue(layer, out isLocked))
                    {
                        isLocked = false;
                        if (layers.Has(layer))
                        {
                            LayerTableRecord record = (LayerTableRecord)transaction.GetObject(
                                layers[layer], OpenMode.ForRead);
                            isLocked = record.IsLocked;
                        }

                        locked[layer] = isLocked;
                    }

                    sources.Add(new PipeSource { Id = id, Curve = curve, Locked = isLocked });
                }

                transaction.Commit();
            }

            return sources;
        }

        // ------------------------------------------------------------------ applying a size

        /// <summary>
        /// Moves a run onto a layer. Each curve of the plan is cut at the plan's points with
        /// GetSplitCurves (parameters sorted, points snapped to the curve first). Run pieces take the
        /// layer with colour and linetype ByLayer; the others keep everything. The original entity is
        /// reused for the longest piece so its handle survives, and the other pieces are appended.
        /// All of it happens in one transaction: on any failure nothing is changed.
        /// </summary>
        internal static bool ApplyRun(
            Database db,
            IList<PipeSource> sources,
            IList<CurveSplitPlan> plans,
            string layerName,
            out ApplySummary summary,
            out string error)
        {
            summary = new ApplySummary();
            error = null;
            List<DBObject> pending = new List<DBObject>();
            try
            {
                using (Transaction transaction = db.TransactionManager.StartTransaction())
                {
                    foreach (CurveSplitPlan plan in plans)
                    {
                        PipeSource source = sources[plan.CurveIndex];
                        Curve curve = (Curve)transaction.GetObject(source.Id, OpenMode.ForWrite);
                        if (plan.WholeCurve)
                        {
                            AssignRun(db, curve, layerName);
                            summary.Curves++;
                            summary.RunPieces++;
                        }
                        else
                        {
                            SplitCurve(transaction, db, curve, plan, layerName, summary, pending);
                        }
                    }

                    transaction.Commit();
                }

                return true;
            }
            catch (System.Exception ex)
            {
                foreach (DBObject unused in pending)
                {
                    try
                    {
                        unused.Dispose();
                    }
                    catch (System.Exception)
                    {
                    }
                }

                summary = new ApplySummary();
                error = "Could not change the pipe layers: " + ex.Message;
                return false;
            }
        }

        private static void AssignRun(Database db, Entity entity, string layerName)
        {
            entity.Layer = layerName;
            entity.ColorIndex = 256;
            entity.LinetypeId = db.ByLayerLinetype;
        }

        private static double Elevation(Curve curve)
        {
            Line line = curve as Line;
            if (line != null)
                return line.StartPoint.Z;
            Polyline polyline = curve as Polyline;
            return polyline != null ? polyline.Elevation : 0.0;
        }

        private static double LengthOf(Curve curve)
        {
            return curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam);
        }

        private static void SplitCurve(
            Transaction transaction,
            Database db,
            Curve curve,
            CurveSplitPlan plan,
            string layerName,
            ApplySummary summary,
            List<DBObject> pending)
        {
            double z = Elevation(curve);
            List<double> raw = new List<double>();
            foreach (Vec2 cut in plan.CutPoints)
            {
                // GetParameterAtPoint throws unless the point is on the curve, so snap it first.
                Point3d snapped = curve.GetClosestPointTo(new Point3d(cut.X, cut.Y, z), false);
                raw.Add(curve.GetParameterAtPoint(snapped));
            }

            raw.Sort();
            double start = curve.StartParam;
            double end = curve.EndParam;
            bool closed = curve.Closed;
            List<double> parameters = new List<double>();
            foreach (double value in raw)
            {
                double p = value;
                if (closed && p >= end - ParameterEpsilon)
                    p = start;
                else if (!closed && (p <= start + ParameterEpsilon || p >= end - ParameterEpsilon))
                    continue;

                if (parameters.Count > 0 && p - parameters[parameters.Count - 1] <= ParameterEpsilon)
                    continue;
                parameters.Add(p);
            }

            parameters.Sort();
            if (parameters.Count == 0)
            {
                // Every cut fell on an end: the curve is not divided, so it is all run or none of it.
                double mid = (start + end) / 2;
                Point3d middle = curve.GetPointAtParameter(mid);
                if (plan.IsRunAt(new Vec2(middle.X, middle.Y)))
                {
                    AssignRun(db, curve, layerName);
                    summary.Curves++;
                    summary.RunPieces++;
                }

                return;
            }

            double originalLength = LengthOf(curve);
            DBObjectCollection pieces = curve.GetSplitCurves(new DoubleCollection(parameters.ToArray()));
            foreach (DBObject piece in pieces)
                pending.Add(piece);

            double total = 0;
            int longest = 0;
            double longestLength = -1;
            bool[] inRun = new bool[pieces.Count];
            for (int i = 0; i < pieces.Count; i++)
            {
                Curve piece = (Curve)pieces[i];
                double length = LengthOf(piece);
                total += length;
                if (length > longestLength)
                {
                    longestLength = length;
                    longest = i;
                }

                Point3d middle = piece.GetPointAtParameter((piece.StartParam + piece.EndParam) / 2);
                inRun[i] = plan.IsRunAt(new Vec2(middle.X, middle.Y));
            }

            if (Math.Abs(total - originalLength) > 1e-6 * Math.Max(1.0, originalLength))
            {
                throw new InvalidOperationException(
                    "splitting a curve gave pieces of the wrong total length (" + total.ToString("0.###",
                        System.Globalization.CultureInfo.InvariantCulture) + " instead of "
                    + originalLength.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ")");
            }

            BlockTableRecord owner = (BlockTableRecord)transaction.GetObject(curve.BlockId, OpenMode.ForWrite);
            Curve reusedPiece = (Curve)pieces[longest];
            bool reused = TryReplaceGeometry(curve, reusedPiece);
            if (!reused)
                curve.Erase();

            for (int i = 0; i < pieces.Count; i++)
            {
                Curve piece = (Curve)pieces[i];
                if (reused && i == longest)
                {
                    if (inRun[i])
                    {
                        AssignRun(db, curve, layerName);
                        summary.RunPieces++;
                    }

                    pending.Remove(piece);
                    piece.Dispose();
                    continue;
                }

                if (inRun[i])
                {
                    AssignRun(db, piece, layerName);
                    summary.RunPieces++;
                }

                owner.AppendEntity(piece);
                transaction.AddNewlyCreatedDBObject(piece, true);
                pending.Remove(piece);
                summary.NewEntities++;
            }

            summary.Curves++;
        }

        // Moves the original entity's geometry onto one of the pieces, so the original keeps its handle,
        // its xdata and its properties. Only LINE to LINE and polyline to polyline are handled.
        private static bool TryReplaceGeometry(Curve original, Curve piece)
        {
            Line originalLine = original as Line;
            Line pieceLine = piece as Line;
            if (originalLine != null && pieceLine != null)
            {
                originalLine.StartPoint = pieceLine.StartPoint;
                originalLine.EndPoint = pieceLine.EndPoint;
                return true;
            }

            Polyline originalPolyline = original as Polyline;
            Polyline piecePolyline = piece as Polyline;
            if (originalPolyline != null && piecePolyline != null)
            {
                // Add the new vertices after the old ones, then drop the old ones, so the polyline is
                // never empty.
                int oldCount = originalPolyline.NumberOfVertices;
                for (int i = 0; i < piecePolyline.NumberOfVertices; i++)
                {
                    originalPolyline.AddVertexAt(
                        originalPolyline.NumberOfVertices,
                        piecePolyline.GetPoint2dAt(i),
                        piecePolyline.GetBulgeAt(i),
                        piecePolyline.GetStartWidthAt(i),
                        piecePolyline.GetEndWidthAt(i));
                }

                for (int i = 0; i < oldCount; i++)
                    originalPolyline.RemoveVertexAt(0);
                originalPolyline.Closed = piecePolyline.Closed;
                return true;
            }

            return false;
        }
    }
}
