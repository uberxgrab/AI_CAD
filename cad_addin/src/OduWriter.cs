using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using HvacAiCadAddin.Commands;

namespace HvacAiCadAddin.Vrv
{
    /// <summary>The indoor units read from a selection, and what was left out and why.</summary>
    internal sealed class IndoorReadResult
    {
        public readonly List<OduIndoorUnit> Units = new List<OduIndoorUnit>();
        public readonly List<string> Skipped = new List<string>();
    }

    // Reads the capacity of each selected indoor unit block, for HVACPLACEODU and HVACRISER. The sources
    // are the ones HVACREFRIG reads (RefrigCapacityReader.cs), but by block instead of by pipe end: the
    // HVACAI_PLACED data of HVACPLACEUNITS, a block name with "<number> kW" (FCU-CAS-2.8 kW), and a
    // HVAC-NAME OF EQUIPMENTS label (its PROPERTIES attribute has "<number> kW"). Outdoor units tagged
    // HVACAI_ODU and blocks without a capacity are skipped and reported.
    internal static class VrvUnitReader
    {
        internal static IndoorReadResult Read(Database db, IEnumerable<ObjectId> ids)
        {
            IndoorReadResult result = new IndoorReadResult();
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    if (id.IsNull || id.IsErased)
                        continue;
                    BlockReference block = transaction.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (block == null || block is Table)
                        continue;

                    string name = EffectiveName(transaction, block);
                    string handle = block.Handle.ToString();
                    if (HasApp(block, OduSettings.OduApp))
                    {
                        result.Skipped.Add(handle + " " + name + ": an outdoor unit");
                        continue;
                    }

                    OduIndoorUnit unit = null;
                    IList<object> placed = ReadValues(block, IndoorCapacity.PlacedApp);
                    if (placed.Count > 0)
                        IndoorCapacity.TryFromPlacedTag(placed, name, out unit);
                    if (unit == null && !string.IsNullOrEmpty(name))
                    {
                        if (string.Equals(name, IndoorCapacity.LabelBlockName, StringComparison.OrdinalIgnoreCase))
                        {
                            string title = null;
                            string properties = null;
                            foreach (ObjectId attributeId in block.AttributeCollection)
                            {
                                AttributeReference attribute =
                                    transaction.GetObject(attributeId, OpenMode.ForRead) as AttributeReference;
                                if (attribute == null)
                                    continue;
                                if (string.Equals(attribute.Tag, "TITLE", StringComparison.OrdinalIgnoreCase))
                                    title = attribute.TextString;
                                else if (string.Equals(attribute.Tag, "PROPERTIES", StringComparison.OrdinalIgnoreCase))
                                    properties = attribute.TextString;
                            }

                            IndoorCapacity.TryFromLabel(title, properties, out unit);
                        }
                        else if (IndoorCapacity.TryFromName(name, out unit))
                        {
                            unit.BlockName = name;
                        }
                    }

                    if (unit == null)
                    {
                        result.Skipped.Add(handle + " " + (string.IsNullOrEmpty(name) ? "(unnamed block)" : name)
                            + ": no capacity (no HVACAI_PLACED data, no \"n kW\" in the name or label)");
                        continue;
                    }

                    unit.Handle = handle;
                    unit.X = block.Position.X;
                    unit.Y = block.Position.Y;
                    result.Units.Add(unit);
                }

                transaction.Commit();
            }

            return result;
        }

        internal static string EffectiveName(Transaction transaction, BlockReference block)
        {
            try
            {
                ObjectId definitionId = block.IsDynamicBlock ? block.DynamicBlockTableRecord : block.BlockTableRecord;
                BlockTableRecord definition = transaction.GetObject(definitionId, OpenMode.ForRead) as BlockTableRecord;
                return definition == null ? null : definition.Name;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        internal static bool HasApp(Entity entity, string app)
        {
            using (ResultBuffer buffer = entity.GetXDataForApplication(app))
                return buffer != null;
        }

        /// <summary>The xdata values of an application in order, the 1001 name first; empty when there are none.</summary>
        internal static IList<object> ReadValues(Entity entity, string app)
        {
            List<object> values = new List<object>();
            using (ResultBuffer buffer = entity.GetXDataForApplication(app))
            {
                if (buffer == null)
                    return values;
                foreach (TypedValue value in buffer)
                    values.Add(value.Value);
            }

            return values;
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

        internal static void EnsureLayer(Transaction transaction, Database db, string name, short colour)
        {
            LayerTable table = (LayerTable)transaction.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (table.Has(name))
                return;
            table.UpgradeOpen();
            LayerTableRecord record = new LayerTableRecord
            {
                Name = name,
                Color = Color.FromColorIndex(ColorMethod.ByAci, colour)
            };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }
    }

    internal sealed class OduWriteRequest
    {
        public int Hp;
        /// <summary>The block to insert (the template's own or the nearest one).</summary>
        public string BlockName;
        /// <summary>True when no template block can be used and a simple block is drawn under BlockName.</summary>
        public bool Fallback;
        public double X;
        public double Y;
        public double Rotation;
        public OduPlan Plan;
        public string LabelText;
        public bool WriteLabel;
        public double TextHeight;
        /// <summary>An earlier tagged outdoor unit to replace (with its label), or ObjectId.Null.</summary>
        public ObjectId ReplaceId;
    }

    internal sealed class OduWriteResult
    {
        public bool Replaced;
        public string Handle;
        public readonly List<string> Notes = new List<string>();
    }

    // Writes the HVACPLACEODU result in one transaction (one undo group). The block and the two layers
    // come from the template drawing (CAD\MEP_DWT_3.dwg) before the transaction; when the template does not
    // have the block, a simple block of the same name is drawn (rectangle, fan circles and the name) with
    // the template's base point (middle of the top edge, body below). The outdoor unit is a BlockReference
    // on EIA_1 MECHANICAL EQUIPMENT tagged HVACAI_ODU: [1001][1070 HP][1071 outdoor index][1071 indoor
    // total index][1040 ratio %][1070 indoor unit count]. The label is an MText on the template's
    // annotation layer tagged HVACAI_ODULABEL: [1001][1000 handle of the block]; it moves with a replace.
    internal static class OduWriter
    {
        internal static OduWriteResult Write(Database db, ObjectId spaceId, OduWriteRequest request, TemplateSource template)
        {
            OduWriteResult result = new OduWriteResult();

            if (template != null)
            {
                if (!request.Fallback)
                {
                    List<string> missing;
                    template.ImportBlocks(db, new[] { request.BlockName }, out missing);
                    if (missing.Count > 0)
                        result.Notes.Add("Not in the template, drawn as a simple block: " + string.Join(", ", missing));
                }

                List<string> missingLayers;
                template.ImportLayers(db, new[] { OduSettings.UnitLayer, OduSettings.LabelLayer }, out missingLayers);
            }
            else
            {
                result.Notes.Add("Template drawing not available: a simple block is used if the block is missing.");
            }

            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                VrvUnitReader.EnsureLayer(transaction, db, OduSettings.UnitLayer, OduSettings.LayerColor);
                VrvUnitReader.EnsureLayer(transaction, db, OduSettings.LabelLayer, OduSettings.LayerColor);
                VrvUnitReader.EnsureRegApp(transaction, db, OduSettings.OduApp);
                VrvUnitReader.EnsureRegApp(transaction, db, OduSettings.LabelApp);

                BlockTable blockTable = (BlockTable)transaction.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(spaceId, OpenMode.ForWrite);

                if (!request.ReplaceId.IsNull)
                    result.Replaced = EraseEarlier(transaction, space, request.ReplaceId);

                ObjectId definition;
                if (blockTable.Has(request.BlockName))
                    definition = blockTable[request.BlockName];
                else
                {
                    result.Notes.Add(request.BlockName + " is drawn as a simple block.");
                    definition = CreateFallback(transaction, db, blockTable, request.BlockName, request.Hp);
                }

                BlockReference reference = new BlockReference(new Point3d(request.X, request.Y, 0), definition);
                reference.SetDatabaseDefaults(db);
                reference.Layer = OduSettings.UnitLayer;
                reference.Rotation = request.Rotation;
                space.AppendEntity(reference);
                transaction.AddNewlyCreatedDBObject(reference, true);
                OduPlan plan = request.Plan;
                using (ResultBuffer tag = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, OduSettings.OduApp),
                    new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)plan.Hp),
                    new TypedValue((int)DxfCode.ExtendedDataInteger32, plan.OduIndex),
                    new TypedValue((int)DxfCode.ExtendedDataInteger32, plan.IndoorTotal),
                    new TypedValue((int)DxfCode.ExtendedDataReal, plan.Ratio),
                    new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)plan.UnitCount)))
                {
                    reference.XData = tag;
                }

                result.Handle = reference.Handle.ToString();

                if (request.WriteLabel)
                {
                    double width, height;
                    OduPlanner.Footprint(request.Hp, out width, out height);
                    double cos = Math.Cos(request.Rotation);
                    double sin = Math.Sin(request.Rotation);
                    double localX = width / 2 + 200;
                    double localY = -height / 2 + request.TextHeight * 1.5;
                    MText label = new MText();
                    label.SetDatabaseDefaults(db);
                    label.Layer = OduSettings.LabelLayer;
                    label.TextHeight = request.TextHeight;
                    label.Attachment = AttachmentPoint.TopLeft;
                    label.Location = new Point3d(
                        request.X + localX * cos - localY * sin, request.Y + localX * sin + localY * cos, 0);
                    label.Contents = request.LabelText;
                    space.AppendEntity(label);
                    transaction.AddNewlyCreatedDBObject(label, true);
                    using (ResultBuffer tag = new ResultBuffer(
                        new TypedValue((int)DxfCode.ExtendedDataRegAppName, OduSettings.LabelApp),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, result.Handle)))
                    {
                        label.XData = tag;
                    }
                }

                transaction.Commit();
            }

            return result;
        }

        // Erases the outdoor unit and its label (the MText tagged with the unit's handle).
        private static bool EraseEarlier(Transaction transaction, BlockTableRecord space, ObjectId oldId)
        {
            Entity old = transaction.GetObject(oldId, OpenMode.ForRead) as Entity;
            if (old == null || old.IsErased)
                return false;
            string handle = old.Handle.ToString();

            foreach (ObjectId id in space)
            {
                if (id.ObjectClass.DxfName != "MTEXT")
                    continue;
                MText text = transaction.GetObject(id, OpenMode.ForRead) as MText;
                if (text == null || text.IsErased)
                    continue;
                IList<object> values = VrvUnitReader.ReadValues(text, OduSettings.LabelApp);
                if (values.Count >= 2 && string.Equals(values[1] as string, handle, StringComparison.OrdinalIgnoreCase))
                {
                    text.UpgradeOpen();
                    text.Erase();
                }
            }

            old.UpgradeOpen();
            old.Erase();
            return true;
        }

        /// <summary>The HP stored on an outdoor unit tagged HVACAI_ODU, or 0.</summary>
        internal static int ReadHp(Entity entity)
        {
            IList<object> values = VrvUnitReader.ReadValues(entity, OduSettings.OduApp);
            if (values.Count < 2)
                return 0;
            return values[1] is short ? (short)values[1] : values[1] is int ? (int)values[1] : 0;
        }

        // A plain plan symbol with the template ODU blocks' base point (middle of the top edge, body below).
        private static ObjectId CreateFallback(
            Transaction transaction, Database db, BlockTable blockTable, string name, int hp)
        {
            double width, height;
            OduPlanner.Footprint(hp, out width, out height);

            blockTable.UpgradeOpen();
            BlockTableRecord record = new BlockTableRecord { Name = name, Origin = Point3d.Origin };
            ObjectId id = blockTable.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);

            double hw = width / 2, top = 0, bottom = -height;
            Polyline outline = new Polyline();
            outline.SetDatabaseDefaults(db);
            outline.AddVertexAt(0, new Point2d(-hw, bottom), 0, 0, 0);
            outline.AddVertexAt(1, new Point2d(hw, bottom), 0, 0, 0);
            outline.AddVertexAt(2, new Point2d(hw, top), 0, 0, 0);
            outline.AddVertexAt(3, new Point2d(-hw, top), 0, 0, 0);
            outline.Closed = true;
            Append(transaction, record, outline);

            // One fan circle per 930 mm of width.
            int fans = Math.Max(1, (int)Math.Round(width / 930.0));
            double cell = width / fans;
            double radius = Math.Min(cell, height) * 0.38;
            for (int i = 0; i < fans; i++)
            {
                double cx = -hw + cell * (i + 0.5);
                Append(transaction, record, new Circle(new Point3d(cx, -height / 2, 0), Vector3d.ZAxis, radius));
            }

            DBText label = new DBText();
            label.SetDatabaseDefaults(db);
            label.Height = 100;
            label.TextString = name;
            label.Position = new Point3d(-hw + 50, bottom + 50, 0);
            Append(transaction, record, label);
            return id;
        }

        private static void Append(Transaction transaction, BlockTableRecord record, Entity entity)
        {
            record.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
        }
    }
}
