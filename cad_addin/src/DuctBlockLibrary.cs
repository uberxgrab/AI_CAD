using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using HvacAiCadAddin.Commands;

namespace HvacAiCadAddin.Ducts
{
    // Non-interactive AutoCAD side of the duct commands: finding, importing and creating duct
    // block sizes, inserting and resizing duct references, and writing fitting polylines. It
    // needs only AcDbMgd and AcCoreMgd, so a headless accoreconsole test can link it
    // (tests/DuctAcadTests). Nothing here prompts or shows graphics.

    /// <summary>A duct reference read from the drawing, in plan terms.</summary>
    internal sealed class DuctInfo
    {
        public ObjectId Id;
        public DuctSize Size;
        public DuctGeometry Geometry;
        public double Elevation;
        public string Layer;
        /// <summary>Distance3. For a uniformly scaled reference it is in drawing units, not block units (checked headless: scale 2, Distance3 1000 draws 1000 long).</summary>
        public double Distance3;
        /// <summary>The length of the BlockTransform X axis: 1 for ordinary and mirrored references.</summary>
        public double XScale;
    }

    /// <summary>Duct sizes and layers found in the template, read once per template file.</summary>
    internal sealed class TemplateCatalog
    {
        private static readonly Dictionary<string, TemplateCatalog> Cache =
            new Dictionary<string, TemplateCatalog>(StringComparer.OrdinalIgnoreCase);

        public string Path;
        public List<DuctSize> Sizes = new List<DuctSize>();
        public List<string> DuctLayers = new List<string>();

        // Reads the template in a side database (nothing runs on opening a DWG) and keeps the
        // result for the session, keyed by path and write time. Returns null if unreadable.
        public static TemplateCatalog Read(string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                error = "Template drawing not found: " + path;
                return null;
            }

            string key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            TemplateCatalog cached;
            if (Cache.TryGetValue(key, out cached))
                return cached;

            TemplateCatalog catalog = new TemplateCatalog { Path = path };
            using (Database database = new Database(false, true))
            {
                try
                {
                    database.ReadDwgFile(path, FileShare.Read, true, string.Empty);
                    database.CloseInput(true);
                }
                catch (System.Exception ex)
                {
                    error = "Could not read template " + path + ": " + ex.Message;
                    return null;
                }

                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    BlockTable blocks = (BlockTable)transaction.GetObject(
                        database.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId id in blocks)
                    {
                        BlockTableRecord record = (BlockTableRecord)transaction.GetObject(
                            id, OpenMode.ForRead);
                        DuctSize size;
                        if (!record.IsAnonymous && DuctSize.TryParseBlockName(record.Name, out size))
                            catalog.Sizes.Add(size);
                    }

                    LayerTable layers = (LayerTable)transaction.GetObject(
                        database.LayerTableId, OpenMode.ForRead);
                    foreach (ObjectId id in layers)
                    {
                        LayerTableRecord layer = (LayerTableRecord)transaction.GetObject(
                            id, OpenMode.ForRead);
                        if (DuctLayerFilter.IsDuctSystemLayer(layer.Name))
                            catalog.DuctLayers.Add(layer.Name);
                    }

                    transaction.Commit();
                }
            }

            catalog.Sizes.Sort(CompareSizes);
            catalog.DuctLayers.Sort(StringComparer.OrdinalIgnoreCase);
            Cache[key] = catalog;
            return catalog;
        }

        internal static int CompareSizes(DuctSize a, DuctSize b)
        {
            int byShape = a.IsRound.CompareTo(b.IsRound);
            if (byShape != 0)
                return byShape;
            int byWidth = a.Width.CompareTo(b.Width);
            return byWidth != 0 ? byWidth : a.Height.CompareTo(b.Height);
        }
    }

    internal static class DuctBlockLibrary
    {
        /// <summary>The xdata application that tags generated fittings; HVACQTO skips curves carrying it.</summary>
        internal const string FittingAppName = "HVACAI_FITTING";
        internal const string DistanceProperty = "Distance3";
        private const string CentreLinetype = "CENTER";
        private const short CentreColorIndex = 8;

        // ------------------------------------------------------------------ block lookup

        /// <summary>Sizes of the duct block definitions present in the drawing.</summary>
        internal static List<DuctSize> ListSizes(Database db)
        {
            List<DuctSize> sizes = new List<DuctSize>();
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)transaction.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    BlockTableRecord record = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
                    DuctSize size;
                    if (!record.IsAnonymous && DuctSize.TryParseBlockName(record.Name, out size))
                        sizes.Add(size);
                }

                transaction.Commit();
            }

            sizes.Sort(TemplateCatalog.CompareSizes);
            return sizes;
        }

        internal static bool TryFindBlock(Database db, DuctSize size, out ObjectId id)
        {
            id = ObjectId.Null;
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)transaction.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (table.Has(size.BlockName))
                    id = table[size.BlockName];
                transaction.Commit();
            }

            return !id.IsNull;
        }

        /// <summary>
        /// Makes sure the drawing has a dynamic block for this size, and returns its definition id.
        /// Looks in the drawing, then the template, and otherwise clones the nearest size of the same
        /// shape and moves its outline to the new width. Call with no transaction open.
        /// </summary>
        /// <param name="template">May be null when the template is unavailable.</param>
        /// <param name="templateSizes">The sizes the template holds; may be null.</param>
        internal static ObjectId EnsureBlock(
            Database db,
            DuctSize size,
            TemplateSource template,
            IList<DuctSize> templateSizes,
            out string note,
            out string error)
        {
            note = null;
            error = null;

            ObjectId id;
            if (TryFindBlock(db, size, out id))
            {
                if (!IsDynamic(db, id))
                {
                    error = "Block " + size.BlockName + " exists but is not a dynamic duct block.";
                    return ObjectId.Null;
                }

                return id;
            }

            if (template != null && template.HasBlock(size.BlockName))
            {
                List<string> missing;
                Dictionary<string, ObjectId> imported = template.ImportBlocks(
                    db, new[] { size.BlockName }, out missing);
                if (imported.TryGetValue(size.BlockName, out id))
                {
                    note = "Imported " + size.BlockName + " from the template.";
                    return id;
                }
            }

            // Not available anywhere: clone the nearest size of the same shape.
            DuctSize nearest = FindNearest(size, ListSizes(db), templateSizes);
            if (nearest == null)
            {
                error = "No " + (size.IsRound ? "round" : "rectangular")
                    + " duct block exists to copy for " + size.BlockName + ".";
                return ObjectId.Null;
            }

            ObjectId masterId;
            if (!TryFindBlock(db, nearest, out masterId))
            {
                if (template == null)
                {
                    error = "The template is needed to copy " + nearest.BlockName + ".";
                    return ObjectId.Null;
                }

                List<string> missing;
                Dictionary<string, ObjectId> imported = template.ImportBlocks(
                    db, new[] { nearest.BlockName }, out missing);
                if (!imported.TryGetValue(nearest.BlockName, out masterId))
                {
                    error = "Could not import " + nearest.BlockName + " from the template.";
                    return ObjectId.Null;
                }
            }

            ObjectId created = CloneRename(db, masterId, size.BlockName);
            if (Math.Abs(nearest.Width - size.Width) > 1e-9)
                SetOutlineHalfWidth(db, created, size.Width / 2);

            note = "Created " + size.BlockName + " from " + nearest.BlockName + ".";
            return created;
        }

        // Nearest by plan width, then by the other dimension. Round sizes only compete with
        // round sizes, rectangular with rectangular.
        internal static DuctSize FindNearest(
            DuctSize wanted, IList<DuctSize> inDrawing, IList<DuctSize> inTemplate)
        {
            DuctSize best = null;
            foreach (IList<DuctSize> source in new[] { inDrawing, inTemplate })
            {
                if (source == null)
                    continue;
                foreach (DuctSize candidate in source)
                {
                    if (candidate.IsRound != wanted.IsRound)
                        continue;
                    if (best == null || IsCloser(wanted, candidate, best))
                        best = candidate;
                }
            }

            return best;
        }

        private static bool IsCloser(DuctSize wanted, DuctSize candidate, DuctSize best)
        {
            double byWidth = Math.Abs(candidate.Width - wanted.Width);
            double bestByWidth = Math.Abs(best.Width - wanted.Width);
            if (Math.Abs(byWidth - bestByWidth) > 1e-9)
                return byWidth < bestByWidth;
            return Math.Abs(candidate.Height - wanted.Height) < Math.Abs(best.Height - wanted.Height);
        }

        private static bool IsDynamic(Database db, ObjectId id)
        {
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord record = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
                bool dynamic = record.IsDynamicBlock;
                transaction.Commit();
                return dynamic;
            }
        }

        // Recipe verified in accoreconsole (CLAUDE.md, "New duct sizes"): clone into a scratch
        // database, rename it there, clone it back. AutoCAD rewrites the TrueName xdata itself.
        private static ObjectId CloneRename(Database db, ObjectId masterId, string newName)
        {
            using (Database scratch = new Database(true, true))
            {
                IdMapping toScratch = new IdMapping();
                db.WblockCloneObjects(
                    new ObjectIdCollection(new[] { masterId }),
                    scratch.BlockTableId,
                    toScratch,
                    DuplicateRecordCloning.Ignore,
                    false);
                ObjectId cloneId = toScratch[masterId].Value;

                using (Transaction transaction = scratch.TransactionManager.StartTransaction())
                {
                    BlockTableRecord clone = (BlockTableRecord)transaction.GetObject(
                        cloneId, OpenMode.ForWrite);
                    clone.Name = newName;
                    transaction.Commit();
                }

                IdMapping fromScratch = new IdMapping();
                scratch.WblockCloneObjects(
                    new ObjectIdCollection(new[] { cloneId }),
                    db.BlockTableId,
                    fromScratch,
                    DuplicateRecordCloning.Ignore,
                    false);
                return fromScratch[cloneId].Value;
            }
        }

        // The visible outline is one closed polyline whose vertices sit at y = +-W/2. Moving
        // them with SetPointAt keeps the vertex count and order, so the dynamic parameters
        // and actions still apply.
        private static void SetOutlineHalfWidth(Database db, ObjectId definitionId, double newHalf)
        {
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord definition = (BlockTableRecord)transaction.GetObject(
                    definitionId, OpenMode.ForRead);
                foreach (ObjectId entityId in definition)
                {
                    Polyline outline = transaction.GetObject(entityId, OpenMode.ForRead) as Polyline;
                    if (outline == null || !outline.Visible || !outline.Closed)
                        continue;

                    double oldHalf = 0;
                    for (int i = 0; i < outline.NumberOfVertices; i++)
                        oldHalf = Math.Max(oldHalf, Math.Abs(outline.GetPoint2dAt(i).Y));
                    if (Math.Abs(oldHalf - newHalf) < 1e-9)
                        continue;

                    outline.UpgradeOpen();
                    for (int i = 0; i < outline.NumberOfVertices; i++)
                    {
                        Point2d vertex = outline.GetPoint2dAt(i);
                        if (Math.Abs(Math.Abs(vertex.Y) - oldHalf) < 1e-6)
                        {
                            outline.SetPointAt(
                                i, new Point2d(vertex.X, vertex.Y > 0 ? newHalf : -newHalf));
                        }
                    }
                }

                transaction.Commit();
            }
        }

        // ------------------------------------------------------------------ layers

        /// <summary>Makes sure the layer exists, importing it from the template when it does not.</summary>
        internal static bool EnsureLayer(
            Database db, string layerName, TemplateSource template, out string error)
        {
            error = null;
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                LayerTable table = (LayerTable)transaction.GetObject(db.LayerTableId, OpenMode.ForRead);
                bool exists = table.Has(layerName);
                transaction.Commit();
                if (exists)
                    return true;
            }

            if (template == null)
            {
                error = "Layer '" + layerName + "' is not in the drawing and the template is unavailable.";
                return false;
            }

            List<string> missing;
            template.ImportLayers(db, new[] { layerName }, out missing);
            if (missing.Count > 0)
            {
                error = "Layer '" + layerName + "' is in neither the drawing nor the template.";
                return false;
            }

            return true;
        }

        /// <summary>Layer names that look like duct systems, from the drawing.</summary>
        internal static List<string> ListDuctLayers(Database db)
        {
            List<string> names = new List<string>();
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                LayerTable table = (LayerTable)transaction.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    LayerTableRecord layer = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForRead);
                    if (DuctLayerFilter.IsDuctSystemLayer(layer.Name))
                        names.Add(layer.Name);
                }

                transaction.Commit();
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        // ------------------------------------------------------------------ reading ducts

        /// <summary>
        /// Reads a duct reference: block name <c>REC_WxH</c> or <c>Round Dd</c>, with a
        /// <c>Distance3</c> property. Start is Position, axis the BlockTransform X axis (right for
        /// mirrored inserts too), length is Distance3 (drawing units), width W (or d) times the Y scale.
        /// </summary>
        internal static bool TryReadDuct(Transaction transaction, BlockReference block, out DuctInfo info)
        {
            info = null;
            if (block == null || block.IsErased)
                return false;

            ObjectId definitionId = block.IsDynamicBlock
                ? block.DynamicBlockTableRecord
                : block.BlockTableRecord;
            BlockTableRecord definition = transaction.GetObject(definitionId, OpenMode.ForRead) as BlockTableRecord;
            DuctSize size;
            if (definition == null || !DuctSize.TryParseBlockName(definition.Name, out size))
                return false;

            double distance;
            if (!DynamicBlockHelpers.TryGetDistancePropertyValue(block, DistanceProperty, out distance))
                return false;

            CoordinateSystem3d system = block.BlockTransform.CoordinateSystem3d;
            double xScale = system.Xaxis.Length;
            double yScale = system.Yaxis.Length;
            Vec2 axis = new Vec2(system.Xaxis.X, system.Xaxis.Y);
            if (xScale < 1e-9 || yScale < 1e-9 || axis.Length < 1e-9)
                return false;

            Point3d position = block.Position;
            info = new DuctInfo
            {
                Id = block.ObjectId,
                Size = size,
                Geometry = new DuctGeometry(
                    new Vec2(position.X, position.Y), axis, distance, size.Width * yScale),
                Elevation = position.Z,
                Layer = block.Layer,
                Distance3 = distance,
                XScale = xScale
            };
            return true;
        }

        internal static bool TryReadDuct(Database db, ObjectId id, out DuctInfo info)
        {
            info = null;
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockReference block = transaction.GetObject(id, OpenMode.ForRead, true) as BlockReference;
                bool ok = TryReadDuct(transaction, block, out info);
                transaction.Commit();
                return ok;
            }
        }

        /// <summary>
        /// Finds the duct edge nearest to a point in the current space. Faces beat walls at a tie.
        /// <paramref name="skip"/> lists references to ignore, such as the ones the run just drew.
        /// </summary>
        internal static bool FindDuctAt(
            Database db,
            Vec2 point,
            double tolerance,
            ICollection<ObjectId> skip,
            out DuctInfo info,
            out DuctHit hit)
        {
            info = null;
            hit = null;
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(
                    db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    if (id.ObjectClass.DxfName != "INSERT" || (skip != null && skip.Contains(id)))
                        continue;

                    BlockReference block = transaction.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (block == null || !block.IsDynamicBlock || block is Table)
                        continue;

                    // GeometricExtents is too big for duct blocks (invisible geometry), never too
                    // small, so it is a safe cheap filter before the exact test.
                    try
                    {
                        Extents3d box = block.GeometricExtents;
                        if (point.X < box.MinPoint.X - tolerance || point.X > box.MaxPoint.X + tolerance
                            || point.Y < box.MinPoint.Y - tolerance || point.Y > box.MaxPoint.Y + tolerance)
                            continue;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                    }

                    DuctInfo candidate;
                    if (!TryReadDuct(transaction, block, out candidate))
                        continue;

                    DuctHit candidateHit = DuctHitTest.Classify(candidate.Geometry, point, tolerance);
                    if (candidateHit == null)
                        continue;

                    if (hit == null || candidateHit.Distance < hit.Distance)
                    {
                        info = candidate;
                        hit = candidateHit;
                    }
                }

                transaction.Commit();
            }

            return hit != null;
        }

        // ------------------------------------------------------------------ writing ducts

        /// <summary>
        /// Appends a duct reference. The order matters: AppendEntity, AddNewlyCreatedDBObject, and only
        /// then the dynamic property (a property set earlier is silently ignored).
        /// </summary>
        internal static ObjectId InsertDuct(
            Transaction transaction,
            BlockTableRecord space,
            ObjectId definitionId,
            Vec2 start,
            double elevation,
            Vec2 direction,
            string layer,
            double length)
        {
            BlockReference block = new BlockReference(
                new Point3d(start.X, start.Y, elevation), definitionId);
            block.Rotation = direction.Angle;
            if (!string.IsNullOrEmpty(layer))
                block.Layer = layer;

            space.AppendEntity(block);
            transaction.AddNewlyCreatedDBObject(block, true);

            if (!DynamicBlockHelpers.TrySetDistancePropertyValue(block, DistanceProperty, length, null, false))
                throw new InvalidOperationException("Could not set Distance3 on the new duct.");
            return block.ObjectId;
        }

        /// <summary>
        /// Shortens a duct by <paramref name="amount"/> (negative extends) from one end. When the start
        /// end moves, Position moves too, along the axis.
        /// </summary>
        internal static bool AdjustDuctEnd(
            Transaction transaction,
            ObjectId id,
            bool atStart,
            double amount,
            out string error)
        {
            error = null;
            BlockReference block = transaction.GetObject(id, OpenMode.ForWrite, true) as BlockReference;
            double distance;
            if (block == null
                || !DynamicBlockHelpers.TryGetDistancePropertyValue(block, DistanceProperty, out distance))
            {
                error = "Not a duct reference.";
                return false;
            }

            CoordinateSystem3d system = block.BlockTransform.CoordinateSystem3d;
            Vector3d axis = system.Xaxis.GetNormal();
            Point3d position = block.Position;
            double newDistance = distance - amount;
            if (newDistance < -1e-9)
            {
                error = "The duct would end up shorter than zero.";
                return false;
            }

            if (!DynamicBlockHelpers.TrySetDistancePropertyValue(
                block, DistanceProperty, Math.Max(0, newDistance), null, false))
            {
                error = "Could not set Distance3.";
                return false;
            }

            if (atStart)
                block.Position = position + axis * amount;
            return true;
        }

        /// <summary>Sets Position and Distance3 together (used by align and undo).</summary>
        internal static bool SetDuctState(
            Transaction transaction, ObjectId id, Point3d position, double distance3)
        {
            BlockReference block = transaction.GetObject(id, OpenMode.ForWrite, true) as BlockReference;
            if (block == null)
                return false;
            bool ok = DynamicBlockHelpers.TrySetDistancePropertyValue(
                block, DistanceProperty, distance3, null, false);
            block.Position = position;
            return ok;
        }

        // ------------------------------------------------------------------ fittings

        internal static void EnsureFittingApp(Transaction transaction, Database db)
        {
            RegAppTable table = (RegAppTable)transaction.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (table.Has(FittingAppName))
                return;

            table.UpgradeOpen();
            RegAppTableRecord record = new RegAppTableRecord { Name = FittingAppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        /// <summary>
        /// Writes a fitting as a plain polyline on the duct's system layer, colour and linetype ByLayer
        /// (a round elbow's centreline arc is grey and CENTER, like the Round D blocks), tagged with
        /// HVACAI_FITTING xdata: the kind, then a size text.
        /// </summary>
        internal static ObjectId AddFitting(
            Database db,
            Transaction transaction,
            BlockTableRecord space,
            Outline outline,
            double elevation,
            string layer,
            string kind,
            string sizeText)
        {
            EnsureFittingApp(transaction, db);

            Polyline polyline = new Polyline();
            for (int i = 0; i < outline.Vertices.Count; i++)
            {
                OutlineVertex vertex = outline.Vertices[i];
                polyline.AddVertexAt(i, new Point2d(vertex.X, vertex.Y), vertex.Bulge, 0, 0);
            }

            polyline.Closed = outline.Closed;
            polyline.Elevation = elevation;
            if (!string.IsNullOrEmpty(layer))
                polyline.Layer = layer;

            if (outline.IsCentreline)
            {
                polyline.ColorIndex = CentreColorIndex;
                LinetypeTable linetypes = (LinetypeTable)transaction.GetObject(
                    db.LinetypeTableId, OpenMode.ForRead);
                if (linetypes.Has(CentreLinetype))
                    polyline.LinetypeId = linetypes[CentreLinetype];
            }

            using (ResultBuffer tag = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, FittingAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, kind),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, sizeText ?? string.Empty)))
            {
                polyline.XData = tag;
            }

            space.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            return polyline.ObjectId;
        }

        /// <summary>Reads the HVACAI_FITTING tag back: kind and size text; null when the entity has none.</summary>
        internal static bool TryReadFittingTag(Entity entity, out string kind, out string sizeText)
        {
            kind = null;
            sizeText = null;
            using (ResultBuffer data = entity.GetXDataForApplication(FittingAppName))
            {
                if (data == null)
                    return false;
                TypedValue[] values = data.AsArray();
                if (values.Length < 3)
                    return false;
                kind = Convert.ToString(values[1].Value);
                sizeText = Convert.ToString(values[2].Value);
                return true;
            }
        }
    }
}
