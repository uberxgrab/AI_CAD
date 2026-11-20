using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Pipes
{
    // Non-interactive AutoCAD side of HVACREFRIG sizing: writes the cleaned network with every segment on
    // its RP layer, the refnets on their inlet pipe's layer, the capacity xdata on the pipes that end at a
    // unit, and the size labels, all in one transaction. The layers must already exist (the command
    // imports them first). It needs only AcDbMgd. See AGENTS.md, "HVACREFRIG", "Pipe sizing".

    internal sealed class SizedWriteOptions
    {
        public bool WriteLabels;
        /// <summary>Text height of the labels; they sit this far to the side of the pipe.</summary>
        public double LabelHeight = 150;
        /// <summary>The label layer; null skips the labels.</summary>
        public string LabelLayer;
        /// <summary>The label text style; <see cref="ObjectId.Null"/> uses the current style.</summary>
        public ObjectId LabelStyle = ObjectId.Null;
        /// <summary>Labels a previous run wrote within this distance of the network are replaced (mm).</summary>
        public double LabelCleanRadius = 600;
        /// <summary>The refnet blocks already in the drawing (their layer follows their inlet pipe).</summary>
        public IList<ExistingRefnet> ExistingRefnets = new List<ExistingRefnet>();
    }

    internal static class RefrigSizeWriter
    {
        /// <summary>The registered application name that tags the size labels this command writes.</summary>
        internal const string LabelApp = "HVACAI_RPLABEL";

        /// <summary>The label layer and text style of the template's size labels.</summary>
        internal const string TemplateLabelLayer = "EIA_1 GAS ANNOTATIONS";
        internal const string TemplateLabelStyle = "T_unicode";

        /// <summary>
        /// Writes the sized network in one transaction. On any failure nothing is changed.
        /// </summary>
        /// <param name="unitIndexes">Capacity index per indoor unit, stored on the pipe that ends there.</param>
        /// <param name="unitKw">Capacity in kW per indoor unit.</param>
        /// <param name="refnetDefinition">The refnet block definition; ignored when there are no new refnets.</param>
        internal static bool Write(
            Database db,
            IList<PipeSource> sources,
            RefrigResult clean,
            NetworkTopology topology,
            SizingResult sizing,
            int[] unitIndexes,
            double[] unitKw,
            ObjectId refnetDefinition,
            SizedWriteOptions options,
            out WriteSummary summary,
            out string error)
        {
            summary = new WriteSummary();
            error = null;

            try
            {
                string[] layers = new string[topology.Segments.Count];
                for (int i = 0; i < layers.Length; i++)
                {
                    layers[i] = i < sizing.Segments.Count && sizing.Segments[i].HasChosen
                        ? RefrigCatalog.LayerFor(sizing.Segments[i].Chosen)
                        : null;
                    if (layers[i] == null)
                    {
                        error = "Segment S" + (i + 1) + " has no size with a layer.";
                        return false;
                    }
                }

                using (Transaction transaction = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = (BlockTableRecord)transaction.GetObject(
                        db.CurrentSpaceId, OpenMode.ForWrite);

                    EnsureRegApp(transaction, db, RefrigCapacityReader.UnitApp);
                    if (options.WriteLabels && options.LabelLayer != null)
                        EnsureRegApp(transaction, db, LabelApp);

                    Dictionary<int, PipeSource> byCurveId = new Dictionary<int, PipeSource>();
                    foreach (PipeSource source in sources)
                        byCurveId[source.Curve.Id] = source;

                    // Where the old and the new pipes are, for finding labels of an earlier run.
                    List<IList<Vec2>> geometry = new List<IList<Vec2>>();
                    foreach (PipeSource source in sources)
                    {
                        List<Vec2> old = new List<Vec2>();
                        foreach (OutlineVertex vertex in source.Curve.Vertices)
                            old.Add(new Vec2(vertex.X, vertex.Y));
                        geometry.Add(old);
                    }

                    // The sized segments.
                    for (int i = 0; i < topology.Segments.Count; i++)
                    {
                        TopologySegment segment = topology.Segments[i];
                        Polyline polyline = NewPolyline(segment.Points, segment.Elevation);
                        CopyProperties(transaction, byCurveId, segment.SourceCurve, polyline);
                        polyline.Layer = layers[i];
                        polyline.ColorIndex = 256;
                        polyline.LinetypeId = db.ByLayerLinetype;
                        polyline.LineWeight = LineWeight.ByLayer;

                        TopologyNode down = topology.Nodes[segment.DownNode];
                        if (down.Kind == TopologyNodeKind.End && down.EndIndex < unitIndexes.Length)
                        {
                            int index = unitIndexes[down.EndIndex];
                            if (index > 0)
                            {
                                using (ResultBuffer buffer = UnitXData(index, unitKw[down.EndIndex]))
                                    polyline.XData = buffer;
                            }
                        }

                        space.AppendEntity(polyline);
                        transaction.AddNewlyCreatedDBObject(polyline, true);
                        summary.Paths++;
                        summary.Sized++;
                        geometry.Add(segment.Points);
                    }

                    // Paths no segment reaches (another network) stay as they were.
                    foreach (RefrigPath path in topology.Unreached)
                    {
                        Polyline polyline = NewPolyline(path.Points, path.Elevation);
                        CopyProperties(transaction, byCurveId, path.SourceCurve, polyline);
                        polyline.Layer = path.Layer;
                        space.AppendEntity(polyline);
                        transaction.AddNewlyCreatedDBObject(polyline, true);
                        summary.Paths++;
                    }

                    // New refnets go on the layer of their inlet pipe; the plain placement layer is the
                    // fallback for a refnet no segment reached.
                    Dictionary<int, string> plannedLayer = new Dictionary<int, string>();
                    foreach (TopologyRefnet refnet in topology.Refnets)
                    {
                        if (refnet.Planned && refnet.UpSegment >= 0)
                            plannedLayer[refnet.SourceIndex] = layers[refnet.UpSegment];
                    }

                    for (int i = 0; i < clean.Refnets.Count; i++)
                    {
                        RefnetPlacement placement = clean.Refnets[i];
                        string layer;
                        if (plannedLayer.TryGetValue(i, out layer))
                            placement.Layer = layer;
                        RefrigWriter.InsertRefnet(transaction, space, refnetDefinition, placement);
                        summary.Refnets++;
                    }

                    // Refnet blocks already in the drawing follow their inlet pipe too.
                    foreach (TopologyRefnet refnet in topology.Refnets)
                    {
                        if (refnet.Planned || refnet.UpSegment < 0)
                            continue;
                        foreach (ExistingRefnet existing in options.ExistingRefnets)
                        {
                            if (existing.Ports.Id != refnet.SourceIndex)
                                continue;
                            if (RelayerBlock(transaction, db, existing.Id, layers[refnet.UpSegment]))
                                summary.RefnetsRelayered++;
                            break;
                        }
                    }

                    foreach (PipeSource source in sources)
                    {
                        Entity original = transaction.GetObject(source.Id, OpenMode.ForWrite) as Entity;
                        if (original != null && !original.IsErased)
                        {
                            original.Erase();
                            summary.Erased++;
                        }
                    }

                    if (options.WriteLabels && options.LabelLayer != null)
                    {
                        RemoveOldLabels(transaction, space, geometry, options.LabelCleanRadius, summary);
                        for (int i = 0; i < topology.Segments.Count; i++)
                        {
                            TopologySegment segment = topology.Segments[i];
                            SizeLabelPlacement placement;
                            if (!SizeLabels.TryPlace(
                                segment.Points, options.LabelHeight, sizing.Segments[i].Chosen.LabelText, out placement))
                                continue;

                            AddLabel(transaction, db, space, placement, segment.Elevation, options);
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
                error = "Could not write the sized network: " + ex.Message;
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

        internal static ResultBuffer UnitXData(int index, double kw)
        {
            return new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RefrigCapacityReader.UnitApp),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "UNIT"),
                new TypedValue((int)DxfCode.ExtendedDataInteger32, index),
                new TypedValue((int)DxfCode.ExtendedDataReal, kw));
        }

        internal static void EnsureRegApp(Transaction transaction, Database db, string name)
        {
            RegAppTable table = (RegAppTable)transaction.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (table.Has(name))
                return;

            table.UpgradeOpen();
            RegAppTableRecord record = new RegAppTableRecord { Name = name };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static bool IsLocked(Transaction transaction, Database db, string layerName)
        {
            LayerTable table = (LayerTable)transaction.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!table.Has(layerName))
                return false;
            LayerTableRecord record = (LayerTableRecord)transaction.GetObject(table[layerName], OpenMode.ForRead);
            return record.IsLocked;
        }

        // Puts an existing block on a layer. A block on a locked layer is left alone.
        private static bool RelayerBlock(Transaction transaction, Database db, ObjectId id, string layer)
        {
            Entity entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
            if (entity == null || entity.IsErased
                || string.Equals(entity.Layer, layer, StringComparison.OrdinalIgnoreCase)
                || IsLocked(transaction, db, entity.Layer))
                return false;

            entity.UpgradeOpen();
            entity.Layer = layer;
            return true;
        }

        // ------------------------------------------------------------------ labels

        private static double DistanceToPolyline(Vec2 point, IList<Vec2> points)
        {
            double best = double.MaxValue;
            for (int i = 0; i + 1 < points.Count; i++)
            {
                Vec2 a = points[i];
                Vec2 b = points[i + 1];
                Vec2 d = b - a;
                double length2 = d.Dot(d);
                double t = length2 < 1e-18 ? 0 : Math.Max(0, Math.Min(1, (point - a).Dot(d) / length2));
                best = Math.Min(best, point.DistanceTo(a + d * t));
            }

            return best;
        }

        // Removes the size labels an earlier run wrote near this network, so a second run replaces them.
        internal static void RemoveOldLabels(
            Transaction transaction,
            BlockTableRecord space,
            List<IList<Vec2>> geometry,
            double radius,
            WriteSummary summary,
            string app = LabelApp)
        {
            Database db = space.Database;
            foreach (ObjectId id in space)
            {
                if (id.ObjectClass.DxfName != "TEXT")
                    continue;

                DBText text = transaction.GetObject(id, OpenMode.ForRead) as DBText;
                if (text == null || text.IsErased)
                    continue;

                using (ResultBuffer tag = text.GetXDataForApplication(app))
                {
                    if (tag == null)
                        continue;
                }

                Vec2 at = new Vec2(text.Position.X, text.Position.Y);
                bool near = false;
                foreach (IList<Vec2> line in geometry)
                {
                    if (DistanceToPolyline(at, line) <= radius)
                    {
                        near = true;
                        break;
                    }
                }

                if (!near)
                    continue;

                if (IsLocked(transaction, db, text.Layer))
                {
                    summary.LabelsLocked++;
                    continue;
                }

                text.UpgradeOpen();
                text.Erase();
                summary.LabelsRemoved++;
            }
        }

        internal static void AddLabel(
            Transaction transaction,
            Database db,
            BlockTableRecord space,
            SizeLabelPlacement placement,
            double elevation,
            SizedWriteOptions options,
            string app = LabelApp,
            string layer = null)
        {
            Point3d at = new Point3d(placement.Position.X, placement.Position.Y, elevation);
            DBText text = new DBText();
            text.SetDatabaseDefaults(db);
            text.Layer = layer ?? options.LabelLayer;
            if (!options.LabelStyle.IsNull)
                text.TextStyleId = options.LabelStyle;
            text.Height = options.LabelHeight;
            text.TextString = placement.Text;
            text.Rotation = placement.Rotation;
            text.Position = at;
            text.HorizontalMode = TextHorizontalMode.TextCenter;
            text.VerticalMode = TextVerticalMode.TextVerticalMid;
            text.AlignmentPoint = at;
            using (ResultBuffer buffer = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, app),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "SIZE")))
            {
                text.XData = buffer;
            }

            space.AppendEntity(text);
            transaction.AddNewlyCreatedDBObject(text, true);
            try
            {
                // Settles the base point of centred text; the label is placed correctly without it.
                text.AdjustAlignment(db);
            }
            catch (System.Exception)
            {
            }
        }
    }
}
