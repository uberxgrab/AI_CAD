using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using HvacAiCadAddin.Commands;

namespace HvacAiCadAddin.Units
{
    /// <summary>One indoor unit the command is about to insert.</summary>
    internal sealed class PlannedUnit
    {
        public string RoomId;
        public UnitFamily Family;
        public UnitModel Model;
        public int Number;
        public int OfCount;
        public PlacedUnitPose Pose;
    }

    internal sealed class UnitWriteResult
    {
        public int Inserted;
        public int Erased;
        public List<string> Notes = new List<string>();
    }

    // Writes the HVACPLACEUNITS result in one transaction (one undo group). Missing blocks are imported from
    // the template drawing (CAD\MEP_DWT_3.dwg) before the transaction; a block the template does not have is
    // drawn as a simple fallback (square, diagonals and a circle) with the same base point as the template
    // cassettes. Each unit is a BlockReference on the template's indoor-unit layer, tagged with the
    // HVACAI_PLACED xdata: [room id][1071 Daikin index][1040 nominal kW][1070 unit number][1070 units in
    // room]. Units a previous run tagged for the same room ids are erased first.
    internal static class UnitPlacementWriter
    {
        internal static UnitWriteResult Write(
            Database db, ObjectId spaceId, IList<PlannedUnit> units, ICollection<string> roomIds, TemplateSource template)
        {
            UnitWriteResult result = new UnitWriteResult();
            List<string> blockNames = new List<string>();
            foreach (PlannedUnit unit in units)
            {
                if (!blockNames.Contains(unit.Model.BlockName))
                    blockNames.Add(unit.Model.BlockName);
            }

            // Template imports use their own transactions and must run before ours.
            if (template != null)
            {
                List<string> missing;
                template.ImportBlocks(db, blockNames, out missing);
                if (missing.Count > 0)
                    result.Notes.Add("Not in the template, drawn as simple blocks: " + string.Join(", ", missing));
                List<string> missingLayer;
                template.ImportLayers(db, new[] { UnitSettings.UnitLayer }, out missingLayer);
            }
            else
            {
                result.Notes.Add("Template drawing not available: simple fallback blocks are used for missing unit blocks.");
            }

            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                EnsureLayer(transaction, db);
                EnsureRegApp(transaction, db, UnitSettings.PlacedApp);

                BlockTable blockTable = (BlockTable)transaction.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(spaceId, OpenMode.ForWrite);
                result.Erased = EraseEarlier(transaction, space, roomIds);

                Dictionary<string, ObjectId> definitions = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
                foreach (PlannedUnit unit in units)
                {
                    string name = unit.Model.BlockName;
                    if (!definitions.ContainsKey(name))
                    {
                        if (blockTable.Has(name))
                            definitions[name] = blockTable[name];
                        else
                        {
                            definitions[name] = CreateFallback(transaction, db, blockTable, unit.Family, name);
                        }
                    }

                    double[] at = UnitPlacer.InsertionPoint(unit.Pose, unit.Family);
                    BlockReference reference = new BlockReference(new Point3d(at[0], at[1], 0), definitions[name]);
                    reference.SetDatabaseDefaults(db);
                    reference.Layer = UnitSettings.UnitLayer;
                    reference.Rotation = unit.Pose.Rotation;
                    space.AppendEntity(reference);
                    transaction.AddNewlyCreatedDBObject(reference, true);
                    using (ResultBuffer tag = new ResultBuffer(
                        new TypedValue((int)DxfCode.ExtendedDataRegAppName, UnitSettings.PlacedApp),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, unit.RoomId ?? ""),
                        new TypedValue((int)DxfCode.ExtendedDataInteger32, unit.Model.Index),
                        new TypedValue((int)DxfCode.ExtendedDataReal, unit.Model.Kw),
                        new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)unit.Number),
                        new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)unit.OfCount)))
                    {
                        reference.XData = tag;
                    }

                    result.Inserted++;
                }

                transaction.Commit();
            }

            return result;
        }

        private static void EnsureLayer(Transaction transaction, Database db)
        {
            LayerTable table = (LayerTable)transaction.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (table.Has(UnitSettings.UnitLayer))
                return;
            table.UpgradeOpen();
            LayerTableRecord record = new LayerTableRecord
            {
                Name = UnitSettings.UnitLayer,
                Color = Color.FromColorIndex(ColorMethod.ByAci, UnitSettings.UnitLayerColor)
            };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static void EnsureRegApp(Transaction transaction, Database db, string name)
        {
            RegAppTable table = (RegAppTable)transaction.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (table.Has(name))
                return;
            table.UpgradeOpen();
            RegAppTableRecord record = new RegAppTableRecord { Name = name };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        // Erases the block references tagged HVACAI_PLACED for one of the room ids.
        private static int EraseEarlier(Transaction transaction, BlockTableRecord space, ICollection<string> roomIds)
        {
            int erased = 0;
            foreach (ObjectId id in space)
            {
                if (id.ObjectClass.DxfName != "INSERT")
                    continue;
                BlockReference reference = transaction.GetObject(id, OpenMode.ForRead) as BlockReference;
                if (reference == null || reference.IsErased)
                    continue;
                string roomId = PlacedRoomId(reference);
                if (roomId == null || !roomIds.Contains(roomId))
                    continue;
                reference.UpgradeOpen();
                reference.Erase();
                erased++;
            }

            return erased;
        }

        /// <summary>The room id of an HVACAI_PLACED tag on an entity, or null when it has none.</summary>
        internal static string PlacedRoomId(Entity entity)
        {
            using (ResultBuffer buffer = entity.GetXDataForApplication(UnitSettings.PlacedApp))
            {
                if (buffer == null)
                    return null;
                TypedValue[] values = buffer.AsArray();
                return values.Length >= 2 ? values[1].Value as string : null;
            }
        }

        // A plain plan symbol with the template cassettes' base point (middle of the top edge, body below).
        private static ObjectId CreateFallback(
            Transaction transaction, Database db, BlockTable blockTable, UnitFamily family, string name)
        {
            blockTable.UpgradeOpen();
            BlockTableRecord record = new BlockTableRecord { Name = name, Origin = Point3d.Origin };
            ObjectId id = blockTable.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);

            double hw = family.Width / 2, top = 0, bottom = -family.Height;
            Polyline outline = new Polyline();
            outline.SetDatabaseDefaults(db);
            outline.AddVertexAt(0, new Point2d(-hw, bottom), 0, 0, 0);
            outline.AddVertexAt(1, new Point2d(hw, bottom), 0, 0, 0);
            outline.AddVertexAt(2, new Point2d(hw, top), 0, 0, 0);
            outline.AddVertexAt(3, new Point2d(-hw, top), 0, 0, 0);
            outline.Closed = true;
            Append(transaction, record, outline);
            Append(transaction, record, new Line(new Point3d(-hw, bottom, 0), new Point3d(hw, top, 0)));
            Append(transaction, record, new Line(new Point3d(-hw, top, 0), new Point3d(hw, bottom, 0)));
            Append(transaction, record, new Circle(new Point3d(0, family.CentreY, 0), Vector3d.ZAxis, family.Width / 4));
            DBText label = new DBText();
            label.SetDatabaseDefaults(db);
            label.Height = 100;
            label.TextString = name;
            label.Position = new Point3d(-hw + 50, family.CentreY - family.Width / 4 - 150, 0);
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
