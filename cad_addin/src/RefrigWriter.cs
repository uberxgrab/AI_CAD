using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using HvacAiCadAddin.Commands;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Pipes
{
    // Non-interactive AutoCAD side of HVACREFRIG: finding refnet blocks already in the drawing,
    // making sure the refnet block exists, and writing the cleaned network. It needs only AcDbMgd and
    // AcCoreMgd, so a headless accoreconsole test can link it. See AGENTS.md, "HVACREFRIG".

    internal sealed class WriteSummary
    {
        public int Paths;
        public int Refnets;
        public int Erased;
    }

    internal static class RefrigWriter
    {
        /// <summary>
        /// The three port points (inlet, main outlet, branch outlet) of every refnet block already in
        /// the current space, so the core can leave a node that already has one alone.
        /// </summary>
        internal static List<Vec2> FindRefnetPorts(Database db)
        {
            List<Vec2> ports = new List<Vec2>();
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(
                    db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    if (id.ObjectClass.DxfName != "INSERT")
                        continue;

                    BlockReference block = transaction.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (block == null || block is Table)
                        continue;

                    ObjectId definitionId = block.IsDynamicBlock
                        ? block.DynamicBlockTableRecord
                        : block.BlockTableRecord;
                    BlockTableRecord definition = transaction.GetObject(
                        definitionId, OpenMode.ForRead) as BlockTableRecord;
                    if (definition == null
                        || !string.Equals(
                            definition.Name, RefrigSettings.RefnetBlockName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    Matrix3d transform = block.BlockTransform;
                    AddPort(ports, transform, 0, 0);
                    AddPort(ports, transform, RefrigSettings.RefnetLength, 0);
                    AddPort(ports, transform, RefrigSettings.RefnetLength, -RefrigSettings.RefnetBranchOffset);
                }

                transaction.Commit();
            }

            return ports;
        }

        private static void AddPort(List<Vec2> ports, Matrix3d transform, double x, double y)
        {
            Point3d point = new Point3d(x, y, 0).TransformBy(transform);
            ports.Add(new Vec2(point.X, point.Y));
        }

        /// <summary>
        /// Makes sure the drawing has the refnet block (<c>Branch Connection (1)</c>), importing it from the
        /// template when it does not. Call with no transaction open.
        /// </summary>
        /// <param name="template">Returns the opened template, or null when it is unavailable.</param>
        internal static bool EnsureRefnetBlock(
            Database db,
            Func<TemplateSource> template,
            out ObjectId definition,
            out string note,
            out string error)
        {
            note = null;
            error = null;
            definition = ObjectId.Null;
            string name = RefrigSettings.RefnetBlockName;

            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)transaction.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (table.Has(name))
                    definition = table[name];
                transaction.Commit();
            }

            if (!definition.IsNull)
                return true;

            TemplateSource source = template == null ? null : template();
            if (source == null)
            {
                error = "Block '" + name + "' is not in the drawing and the template is unavailable.";
                return false;
            }

            List<string> missing;
            Dictionary<string, ObjectId> imported = source.ImportBlocks(db, new[] { name }, out missing);
            if (!imported.TryGetValue(name, out definition))
            {
                error = "Block '" + name + "' is in neither the drawing nor the template.";
                return false;
            }

            note = "Imported block " + name + " from the template.";
            return true;
        }

        /// <summary>
        /// Writes the cleaned network in one transaction: one new polyline per path (properties copied
        /// from the source curve that gave it most of its length, layer from the path), the refnet
        /// blocks on their upstream layers, and the original curves erased. On any failure nothing
        /// is changed.
        /// </summary>
        /// <param name="refnetDefinition">The refnet block definition; ignored when there are no refnets.</param>
        internal static bool Write(
            Database db,
            IList<PipeSource> sources,
            RefrigResult result,
            ObjectId refnetDefinition,
            out WriteSummary summary,
            out string error)
        {
            summary = new WriteSummary();
            error = null;

            try
            {
                using (Transaction transaction = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = (BlockTableRecord)transaction.GetObject(
                        db.CurrentSpaceId, OpenMode.ForWrite);

                    Dictionary<int, PipeSource> byCurveId = new Dictionary<int, PipeSource>();
                    foreach (PipeSource source in sources)
                        byCurveId[source.Curve.Id] = source;

                    foreach (RefrigPath path in result.Paths)
                    {
                        Polyline polyline = new Polyline();
                        for (int i = 0; i < path.Points.Count; i++)
                            polyline.AddVertexAt(i, new Point2d(path.Points[i].X, path.Points[i].Y), 0, 0, 0);
                        polyline.Elevation = path.Elevation;

                        PipeSource origin;
                        if (byCurveId.TryGetValue(path.SourceCurve, out origin))
                        {
                            Entity original = (Entity)transaction.GetObject(origin.Id, OpenMode.ForRead);
                            polyline.SetPropertiesFrom(original);
                        }

                        polyline.Layer = path.Layer;
                        space.AppendEntity(polyline);
                        transaction.AddNewlyCreatedDBObject(polyline, true);
                        summary.Paths++;
                    }

                    foreach (RefnetPlacement refnet in result.Refnets)
                    {
                        InsertRefnet(transaction, space, refnetDefinition, refnet);
                        summary.Refnets++;
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

                    transaction.Commit();
                }

                return true;
            }
            catch (System.Exception ex)
            {
                summary = new WriteSummary();
                error = "Could not write the cleaned network: " + ex.Message;
                return false;
            }
        }

        // Order matters: AppendEntity and AddNewlyCreatedDBObject first, then the attributes.
        private static void InsertRefnet(
            Transaction transaction, BlockTableRecord space, ObjectId definitionId, RefnetPlacement refnet)
        {
            BlockReference block = new BlockReference(
                new Point3d(refnet.Inlet.X, refnet.Inlet.Y, refnet.Elevation), definitionId);
            block.Rotation = refnet.Rotation;
            block.ScaleFactors = new Scale3d(refnet.XScale, refnet.Scale, refnet.Scale);
            block.Layer = refnet.Layer;

            space.AppendEntity(block);
            transaction.AddNewlyCreatedDBObject(block, true);

            BlockTableRecord definition = (BlockTableRecord)transaction.GetObject(
                definitionId, OpenMode.ForRead);
            if (!definition.HasAttributeDefinitions)
                return;

            foreach (ObjectId id in definition)
            {
                AttributeDefinition attribute = transaction.GetObject(id, OpenMode.ForRead) as AttributeDefinition;
                if (attribute == null || attribute.Constant)
                    continue;

                AttributeReference reference = new AttributeReference();
                reference.SetAttributeFromBlock(attribute, block.BlockTransform);
                reference.TextString = attribute.TextString;
                block.AttributeCollection.AppendAttribute(reference);
                transaction.AddNewlyCreatedDBObject(reference, true);
            }
        }
    }
}
