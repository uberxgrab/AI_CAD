using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Pipes;

namespace HvacAiCadAddin.ControlWires
{
    // Non-interactive AutoCAD side of HVACCTRLWIRE: reading the indoor-unit block references into plain
    // data, and writing the planned wires and cable texts in one transaction. It needs only AcDbMgd, so a
    // headless accoreconsole test can link it. Nothing here prompts or shows graphics. The template route
    // is never modified. See AGENTS.md, "HVACCTRLWIRE: draw control wires from a template route".

    /// <summary>An indoor-unit block reference read into plain data.</summary>
    internal sealed class ControlWireBlock
    {
        public ObjectId Id;

        /// <summary>The effective block name: the dynamic block's real name, never the anonymous *U name.</summary>
        public string Name;

        /// <summary>Block name and handle, for messages and labels.</summary>
        public string DisplayName;

        public Vec2 Position;

        /// <summary>The plan part of BlockTransform: block-local to world.</summary>
        public ControlWireAffine Transform;

        public bool HasExtents;
        public Vec2 Min;
        public Vec2 Max;

        /// <summary>Distance from a point to the block's extents (0 inside), or to its insertion point.</summary>
        public double DistanceTo(Vec2 point)
        {
            if (!HasExtents)
                return Position.DistanceTo(point);
            double dx = Math.Max(Math.Max(Min.X - point.X, 0), point.X - Max.X);
            double dy = Math.Max(Math.Max(Min.Y - point.Y, 0), point.Y - Max.Y);
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    internal sealed class ControlWireWriteRequest
    {
        public ControlWirePlan Plan;
        public ControlWireSettings Settings;

        /// <summary>Wires and texts are written at this elevation (the template's).</summary>
        public double Elevation;

        /// <summary>The wire layer, which must exist.</summary>
        public string LayerName;

        /// <summary>The cable text style; <see cref="ObjectId.Null"/> uses the current style.</summary>
        public ObjectId TextStyle = ObjectId.Null;

        /// <summary>Handle text of each unit, by <see cref="ControlWireUnit"/> index, for the xdata tag.</summary>
        public IList<string> UnitHandles;
    }

    internal sealed class ControlWireWriteSummary
    {
        public int Wires;
        public int Texts;
    }

    internal static class ControlWireWriter
    {
        // ------------------------------------------------------------------ reading blocks

        /// <summary>
        /// Reads the block references among the ids (anything else is ignored). Dynamic blocks are named
        /// by their real definition. Extents that cannot be measured are left out.
        /// </summary>
        internal static List<ControlWireBlock> ReadBlocks(Database db, IEnumerable<ObjectId> ids)
        {
            List<ControlWireBlock> blocks = new List<ControlWireBlock>();
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    if (id.IsNull || id.IsErased || id.ObjectClass.DxfName != "INSERT")
                        continue;

                    BlockReference block = transaction.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (block == null || block is Table)
                        continue;

                    ObjectId definitionId = block.IsDynamicBlock
                        ? block.DynamicBlockTableRecord
                        : block.BlockTableRecord;
                    BlockTableRecord definition = transaction.GetObject(
                        definitionId, OpenMode.ForRead) as BlockTableRecord;
                    string name = definition == null ? block.Name : definition.Name;

                    ControlWireBlock info = new ControlWireBlock
                    {
                        Id = id,
                        Name = name,
                        DisplayName = name + " (" + id.Handle.ToString() + ")",
                        Position = new Vec2(block.Position.X, block.Position.Y),
                        Transform = ControlWireAffine.FromMatrixArray(block.BlockTransform.ToArray())
                    };

                    try
                    {
                        Extents3d extents = block.GeometricExtents;
                        info.HasExtents = true;
                        info.Min = new Vec2(extents.MinPoint.X, extents.MinPoint.Y);
                        info.Max = new Vec2(extents.MaxPoint.X, extents.MaxPoint.Y);
                    }
                    catch (System.Exception)
                    {
                        info.HasExtents = false;
                    }

                    blocks.Add(info);
                }

                transaction.Commit();
            }

            return blocks;
        }

        /// <summary>The ids of every block reference in the current space.</summary>
        internal static List<ObjectId> BlockIdsInCurrentSpace(Database db)
        {
            List<ObjectId> ids = new List<ObjectId>();
            foreach (ObjectId id in PipeDrawingLibrary.CurrentSpaceIds(db))
            {
                if (id.ObjectClass.DxfName == "INSERT")
                    ids.Add(id);
            }

            return ids;
        }

        // ------------------------------------------------------------------ earlier runs

        /// <summary>
        /// How many wires (polylines) in the current space carry the HVACAI_CTRLWIRE tag: those of
        /// earlier runs. They are left alone; this only lets the command say so.
        /// </summary>
        internal static int CountTaggedWires(Database db)
        {
            int count = 0;
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                RegAppTable apps = (RegAppTable)transaction.GetObject(db.RegAppTableId, OpenMode.ForRead);
                if (apps.Has(ControlWireSettings.XDataApp))
                {
                    BlockTableRecord space = (BlockTableRecord)transaction.GetObject(
                        db.CurrentSpaceId, OpenMode.ForRead);
                    foreach (ObjectId id in space)
                    {
                        if (id.ObjectClass.DxfName != "LWPOLYLINE")
                            continue;
                        Entity entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
                        if (entity == null || entity.IsErased)
                            continue;
                        using (ResultBuffer tag = entity.GetXDataForApplication(ControlWireSettings.XDataApp))
                        {
                            if (tag != null)
                                count++;
                        }
                    }
                }

                transaction.Commit();
            }

            return count;
        }

        // ------------------------------------------------------------------ writing

        /// <summary>
        /// Writes the wires in one transaction: one open LWPOLYLINE per wire on the wire layer (colour,
        /// linetype and lineweight ByLayer, the template's elevation) and, when asked, one TEXT per wire
        /// beside its exit leg. Everything is tagged with HVACAI_CTRLWIRE xdata. On any failure nothing
        /// is changed.
        /// </summary>
        internal static bool Write(
            Database db, ControlWireWriteRequest request, out ControlWireWriteSummary summary, out string error)
        {
            summary = new ControlWireWriteSummary();
            error = null;

            try
            {
                using (Transaction transaction = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = (BlockTableRecord)transaction.GetObject(
                        db.CurrentSpaceId, OpenMode.ForWrite);
                    RefrigSizeWriter.EnsureRegApp(transaction, db, ControlWireSettings.XDataApp);

                    foreach (ControlWire wire in request.Plan.Wires)
                    {
                        string handle = request.UnitHandles != null && wire.UnitIndex < request.UnitHandles.Count
                            ? request.UnitHandles[wire.UnitIndex]
                            : string.Empty;

                        AddWire(transaction, db, space, wire, request, handle);
                        summary.Wires++;

                        if (request.Settings.WriteText)
                        {
                            AddText(transaction, db, space, wire, request);
                            summary.Texts++;
                        }
                    }

                    transaction.Commit();
                }

                return true;
            }
            catch (System.Exception ex)
            {
                summary = new ControlWireWriteSummary();
                error = "Could not write the control wires: " + ex.Message;
                return false;
            }
        }

        private static void AddWire(
            Transaction transaction,
            Database db,
            BlockTableRecord space,
            ControlWire wire,
            ControlWireWriteRequest request,
            string unitHandle)
        {
            Polyline polyline = new Polyline();
            polyline.SetDatabaseDefaults(db);
            for (int i = 0; i < wire.Points.Count; i++)
                polyline.AddVertexAt(i, new Point2d(wire.Points[i].X, wire.Points[i].Y), 0, 0, 0);
            polyline.Closed = false;
            polyline.Elevation = request.Elevation;
            polyline.Layer = request.LayerName;
            polyline.ColorIndex = 256;
            polyline.LinetypeId = db.ByLayerLinetype;
            polyline.LineWeight = LineWeight.ByLayer;

            using (ResultBuffer tag = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, ControlWireSettings.XDataApp),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "WIRE"),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, wire.Label ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, unitHandle ?? string.Empty)))
            {
                polyline.XData = tag;
            }

            space.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
        }

        // Middle-centre text beside the exit leg. Order matters: AppendEntity and AddNewlyCreatedDBObject
        // first, then AdjustAlignment.
        private static void AddText(
            Transaction transaction,
            Database db,
            BlockTableRecord space,
            ControlWire wire,
            ControlWireWriteRequest request)
        {
            ControlWireSettings settings = request.Settings;
            Point3d at = new Point3d(wire.TextPoint.X, wire.TextPoint.Y, request.Elevation);

            DBText text = new DBText();
            text.SetDatabaseDefaults(db);
            text.Layer = request.LayerName;
            text.ColorIndex = 256;
            text.LinetypeId = db.ByLayerLinetype;
            if (!request.TextStyle.IsNull)
                text.TextStyleId = request.TextStyle;
            text.Height = settings.TextHeight;
            text.TextString = settings.TextString;
            text.Rotation = wire.TextRotation;
            text.Position = at;
            text.HorizontalMode = TextHorizontalMode.TextCenter;
            text.VerticalMode = TextVerticalMode.TextVerticalMid;
            text.AlignmentPoint = at;

            using (ResultBuffer tag = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, ControlWireSettings.XDataApp),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "TEXT"),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, wire.Label ?? string.Empty)))
            {
                text.XData = tag;
            }

            space.AppendEntity(text);
            transaction.AddNewlyCreatedDBObject(text, true);
            try
            {
                // Settles the base point of aligned text; the text is placed correctly without it.
                text.AdjustAlignment(db);
            }
            catch (System.Exception)
            {
            }
        }
    }
}
