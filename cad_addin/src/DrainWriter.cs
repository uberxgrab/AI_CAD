using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Pipes
{
    // Non-interactive AutoCAD side of HVACDRAIN: writes the straightened drain network in one transaction.
    // Each segment becomes one polyline (upstream to downstream, with its 45 degree chamfers and slants) on
    // its size layer (colour, linetype and lineweight ByLayer; the template's condensate layers carry the
    // Broken Line linetype), the originals are erased, the capacity of each indoor unit is stored as
    // HVACAI_UNIT xdata on the pipe that ends there, and each segment gets a size label. The layers must
    // already exist (the command imports them first). It needs only AcDbMgd, and reuses the label and
    // xdata helpers of RefrigSizeWriter. See AGENTS.md, "HVACDRAIN: draw and size a condensate network".

    internal sealed class DrainWriteRequest
    {
        public IList<PipeSource> Sources;
        public DrainPlan Plan;
        public DrainSettings Settings;

        /// <summary>The size of each segment; null when sizing is off (the pipes keep their layers).</summary>
        public int[] SegmentSizes;

        /// <summary>The layer of each size that is in use.</summary>
        public IDictionary<int, string> LayerBySize = new Dictionary<int, string>();

        /// <summary>The capacity in kW of each indoor unit (0 or less: none); null when sizing is off.</summary>
        public double[] UnitKw;

        public bool WriteLabels;

        /// <summary>The label layer; null puts each label on its own pipe's layer.</summary>
        public string LabelLayer;

        /// <summary>The label text style; <see cref="ObjectId.Null"/> uses the current style.</summary>
        public ObjectId LabelStyle = ObjectId.Null;
    }

    internal static class DrainWriter
    {
        /// <summary>The registered application name that tags the size labels this command writes.</summary>
        internal const string LabelApp = "HVACAI_DRAINLABEL";

        /// <summary>Labels a previous run wrote within this many label heights of the network are replaced.</summary>
        private const double LabelCleanHeights = 4;
        private const double LabelCleanMinimum = 600;

        /// <summary>
        /// Writes the network in one transaction. On any failure nothing is changed.
        /// </summary>
        internal static bool Write(Database db, DrainWriteRequest request, out WriteSummary summary, out string error)
        {
            summary = new WriteSummary();
            error = null;

            DrainPlan plan = request.Plan;
            DrainTree tree = plan.Tree;
            bool sized = request.SegmentSizes != null;

            try
            {
                string[] layers = new string[tree.Segments.Count];
                for (int i = 0; i < layers.Length; i++)
                {
                    if (!sized)
                    {
                        layers[i] = tree.Segments[i].Layer;
                        continue;
                    }

                    string layer;
                    if (i >= request.SegmentSizes.Length || !request.LayerBySize.TryGetValue(request.SegmentSizes[i], out layer))
                    {
                        error = "Segment S" + (i + 1) + " has no size with a layer.";
                        return false;
                    }

                    layers[i] = layer;
                }

                using (Transaction transaction = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = (BlockTableRecord)transaction.GetObject(
                        db.CurrentSpaceId, OpenMode.ForWrite);

                    bool labels = sized && request.WriteLabels;
                    if (sized && request.UnitKw != null)
                        RefrigSizeWriter.EnsureRegApp(transaction, db, RefrigCapacityReader.UnitApp);
                    if (labels)
                        RefrigSizeWriter.EnsureRegApp(transaction, db, LabelApp);

                    Dictionary<int, PipeSource> byCurveId = new Dictionary<int, PipeSource>();
                    foreach (PipeSource source in request.Sources)
                        byCurveId[source.Curve.Id] = source;

                    // Where the old and the new pipes are, for finding the labels of an earlier run.
                    List<IList<Vec2>> geometry = new List<IList<Vec2>>();
                    foreach (PipeSource source in request.Sources)
                    {
                        List<Vec2> old = new List<Vec2>();
                        foreach (OutlineVertex vertex in source.Curve.Vertices)
                            old.Add(new Vec2(vertex.X, vertex.Y));
                        geometry.Add(old);
                    }

                    for (int i = 0; i < tree.Segments.Count; i++)
                    {
                        DrainSegment segment = tree.Segments[i];
                        List<Vec2> points = plan.Geometry.Segments[i];
                        Polyline polyline = NewPolyline(points, segment.Elevation);
                        CopyProperties(transaction, byCurveId, segment.SourceCurve, polyline);
                        polyline.Layer = layers[i];
                        if (sized)
                        {
                            polyline.ColorIndex = 256;
                            polyline.LinetypeId = db.ByLayerLinetype;
                            polyline.LineWeight = LineWeight.ByLayer;
                        }

                        DrainNode up = tree.Nodes[segment.UpNode];
                        if (sized && request.UnitKw != null && up.Kind == DrainNodeKind.Unit && up.Unit < request.UnitKw.Length)
                        {
                            double kw = request.UnitKw[up.Unit];
                            if (kw > 0 && !double.IsInfinity(kw))
                            {
                                using (ResultBuffer buffer = RefrigSizeWriter.UnitXData(VrvTables.NearestIndex(kw), kw))
                                    polyline.XData = buffer;
                            }
                        }

                        space.AppendEntity(polyline);
                        transaction.AddNewlyCreatedDBObject(polyline, true);
                        summary.Paths++;
                        if (sized)
                            summary.Sized++;
                        geometry.Add(points);
                    }

                    // Paths no segment reaches (another network) stay as they were.
                    foreach (RefrigPath path in tree.Unreached)
                    {
                        Polyline polyline = NewPolyline(path.Points, path.Elevation);
                        CopyProperties(transaction, byCurveId, path.SourceCurve, polyline);
                        polyline.Layer = path.Layer;
                        space.AppendEntity(polyline);
                        transaction.AddNewlyCreatedDBObject(polyline, true);
                        summary.Paths++;
                    }

                    foreach (PipeSource source in request.Sources)
                    {
                        Entity original = transaction.GetObject(source.Id, OpenMode.ForWrite) as Entity;
                        if (original != null && !original.IsErased)
                        {
                            original.Erase();
                            summary.Erased++;
                        }
                    }

                    if (labels)
                    {
                        double height = request.Settings.LabelHeight;
                        RefrigSizeWriter.RemoveOldLabels(
                            transaction,
                            space,
                            geometry,
                            Math.Max(LabelCleanMinimum, LabelCleanHeights * height),
                            summary,
                            LabelApp);

                        SizedWriteOptions options = new SizedWriteOptions
                        {
                            WriteLabels = true,
                            LabelHeight = height,
                            LabelLayer = request.LabelLayer,
                            LabelStyle = request.LabelStyle
                        };
                        for (int i = 0; i < tree.Segments.Count; i++)
                        {
                            SizeLabelPlacement placement;
                            if (!SizeLabels.TryPlace(
                                plan.Geometry.Segments[i],
                                height,
                                request.Settings.LabelText(request.SegmentSizes[i]),
                                out placement))
                                continue;

                            RefrigSizeWriter.AddLabel(
                                transaction,
                                db,
                                space,
                                placement,
                                tree.Segments[i].Elevation,
                                options,
                                LabelApp,
                                request.LabelLayer ?? layers[i]);
                            summary.Labels++;
                        }
                    }

                    transaction.Commit();
                }

                return true;
            }
            catch (System.Exception ex)
            {
                summary = new WriteSummary();
                error = "Could not write the drain network: " + ex.Message;
                return false;
            }
        }

        private static Polyline NewPolyline(IList<Vec2> points, double elevation)
        {
            Polyline polyline = new Polyline();
            for (int i = 0; i < points.Count; i++)
                polyline.AddVertexAt(i, new Point2d(points[i].X, points[i].Y), 0, 0, 0);
            polyline.Elevation = elevation;
            return polyline;
        }

        private static void CopyProperties(
            Transaction transaction, Dictionary<int, PipeSource> byCurveId, int curve, Polyline target)
        {
            PipeSource origin;
            if (!byCurveId.TryGetValue(curve, out origin))
                return;
            Entity original = (Entity)transaction.GetObject(origin.Id, OpenMode.ForRead);
            target.SetPropertiesFrom(original);
        }
    }
}
