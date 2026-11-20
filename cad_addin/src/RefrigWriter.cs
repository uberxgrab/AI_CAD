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
        /// <summary>Sized segments written (HVACREFRIG with sizing).</summary>
        public int Sized;
        public int Labels;
        public int LabelsRemoved;
        /// <summary>Existing refnet blocks moved onto their inlet pipe's layer.</summary>
        public int RefnetsRelayered;
        /// <summary>Old size labels left in place because their layer is locked.</summary>
        public int LabelsLocked;
    }

    /// <summary>A refnet block already in the drawing, with its three port points.</summary>
    internal sealed class ExistingRefnet
    {
        public ObjectId Id;
        /// <summary><see cref="RefnetPortGroup.Id"/> is this refnet's index in the list it came from.</summary>
        public RefnetPortGroup Ports;
    }

    internal static class RefrigWriter
    {
        /// <summary>
        /// The three port points (inlet, main outlet, branch outlet) of every refnet block already in
        /// the current space, so the core can leave a node that already has one alone. Three consecutive
        /// points are one block.
        /// </summary>
        internal static List<Vec2> FindRefnetPorts(Database db)
        {
            return PortsOf(FindRefnets(db));
        }

        /// <summary>The ports of the refnets as a flat list, three per block: inlet, main outlet, branch outlet.</summary>
        internal static List<Vec2> PortsOf(IEnumerable<ExistingRefnet> refnets)
        {
            List<Vec2> ports = new List<Vec2>();
            foreach (ExistingRefnet refnet in refnets)
            {
                ports.Add(refnet.Ports.Inlet);
                ports.Add(refnet.Ports.Main);
                ports.Add(refnet.Ports.Branch);
            }

            return ports;
        }

        /// <summary>The refnet blocks already in the current space, each with its ports.</summary>
        internal static List<ExistingRefnet> FindRefnets(Database db)
        {
            List<ExistingRefnet> refnets = new List<ExistingRefnet>();
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
                    refnets.Add(new ExistingRefnet
                    {
                        Id = id,
                        Ports = new RefnetPortGroup
                        {
                            Id = refnets.Count,
                            Inlet = Port(transform, 0, 0),
                            Main = Port(transform, RefrigSettings.RefnetLength, 0),
                            Branch = Port(transform, RefrigSettings.RefnetLength, -RefrigSettings.RefnetBranchOffset)
                        }
                    });
                }

                transaction.Commit();
            }

            return refnets;
        }

        private static Vec2 Port(Matrix3d transform, double x, double y)
        {
            Point3d point = new Point3d(x, y, 0).TransformBy(transform);
            return new Vec2(point.X, point.Y);
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
        internal static void InsertRefnet(
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
