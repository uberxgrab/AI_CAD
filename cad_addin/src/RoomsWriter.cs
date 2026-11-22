using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace HvacAiCadAddin.Rooms
{
    // Writes the HVACROOMS result in one transaction (one undo group): a closed LWPOLYLINE per room on
    // HVAC-ROOM (shafts in colour 6) and an MText label on HVAC-ROOM-TEXT, both tagged with the
    // HVACAI_ROOM xdata (id, kind, name, area). Room polylines and labels of an earlier run inside the
    // same area are erased first, so running the command again replaces them.
    internal static class RoomsWriter
    {
        /// <summary>Creates the three HVACROOMS layers when missing (the split layer is for the user).</summary>
        internal static void EnsureLayers(Database db)
        {
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                EnsureLayer(transaction, db, RoomsSettings.RoomLayer, RoomsSettings.RoomLayerColor);
                EnsureLayer(transaction, db, RoomsSettings.TextLayer, RoomsSettings.TextLayerColor);
                EnsureLayer(transaction, db, RoomsSettings.SplitLayer, RoomsSettings.SplitLayerColor);
                transaction.Commit();
            }
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

        private static void EnsureLayer(Transaction transaction, Database db, string name, short colour)
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

        /// <summary>
        /// Erases the given earlier HVACROOMS entities whose extents centre lies in the box, then writes
        /// the rooms. Returns false (writing nothing) when there is nothing usable.
        /// </summary>
        internal static bool Write(
            Database db, ObjectId spaceId, IList<RoomItem> rooms, RoomsSettings settings,
            IEnumerable<ObjectId> earlier, Extents3d? replaceBox, out int written, out int erased)
        {
            written = 0;
            erased = 0;
            if (rooms.Count == 0)
                return false;

            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                EnsureLayer(transaction, db, RoomsSettings.RoomLayer, RoomsSettings.RoomLayerColor);
                EnsureLayer(transaction, db, RoomsSettings.TextLayer, RoomsSettings.TextLayerColor);
                EnsureLayer(transaction, db, RoomsSettings.SplitLayer, RoomsSettings.SplitLayerColor);
                EnsureRegApp(transaction, db, RoomsSettings.XDataApp);

                if (replaceBox.HasValue)
                    erased = EraseEarlier(transaction, earlier, replaceBox.Value);

                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(spaceId, OpenMode.ForWrite);
                foreach (RoomItem room in rooms)
                {
                    if (!RoomsExchangeCore.IsUsable(room))
                        continue;
                    AddRoom(transaction, db, space, room, settings);
                    written++;
                }

                transaction.Commit();
            }

            return written > 0;
        }

        private static int EraseEarlier(Transaction transaction, IEnumerable<ObjectId> earlier, Extents3d box)
        {
            int erased = 0;
            foreach (ObjectId id in earlier)
            {
                Entity entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null || entity.IsErased)
                    continue;
                Extents3d extents;
                try
                {
                    extents = entity.GeometricExtents;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    continue;
                }

                double x = (extents.MinPoint.X + extents.MaxPoint.X) / 2;
                double y = (extents.MinPoint.Y + extents.MaxPoint.Y) / 2;
                if (x < box.MinPoint.X || x > box.MaxPoint.X || y < box.MinPoint.Y || y > box.MaxPoint.Y)
                    continue;
                entity.UpgradeOpen();
                entity.Erase();
                erased++;
            }

            return erased;
        }

        private static void AddRoom(Transaction transaction, Database db, BlockTableRecord space, RoomItem room, RoomsSettings settings)
        {
            Polyline polyline = new Polyline();
            polyline.SetDatabaseDefaults(db);
            for (int i = 0; i < room.Polygon.Length; i++)
                polyline.AddVertexAt(i, new Point2d(room.Polygon[i][0], room.Polygon[i][1]), 0, 0, 0);
            polyline.Closed = true;
            polyline.Layer = RoomsSettings.RoomLayer;
            if (room.IsShaft)
                polyline.ColorIndex = RoomsSettings.ShaftColor;
            space.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            using (ResultBuffer tag = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoomsSettings.XDataApp),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, room.Id ?? ""),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, room.Kind ?? "room"),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, room.Name ?? ""),
                new TypedValue((int)DxfCode.ExtendedDataReal, room.AreaM2)))
            {
                polyline.XData = tag;
            }

            Point2d at = LabelPoint(room);
            MText label = new MText();
            label.SetDatabaseDefaults(db);
            label.Location = new Point3d(at.X, at.Y, 0);
            label.TextHeight = settings.TextHeight;
            label.Attachment = AttachmentPoint.MiddleCenter;
            label.Contents = RoomsExchangeCore.LabelText(room);
            label.Layer = RoomsSettings.TextLayer;
            space.AppendEntity(label);
            transaction.AddNewlyCreatedDBObject(label, true);
            using (ResultBuffer tag = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoomsSettings.XDataApp),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, room.Id ?? ""),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "label")))
            {
                label.XData = tag;
            }
        }

        internal static Point2d LabelPoint(RoomItem room)
        {
            if (room.LabelPoint != null && room.LabelPoint.Length >= 2)
                return new Point2d(room.LabelPoint[0], room.LabelPoint[1]);
            double x = 0, y = 0;
            foreach (double[] p in room.Polygon)
            {
                x += p[0];
                y += p[1];
            }

            return new Point2d(x / room.Polygon.Length, y / room.Polygon.Length);
        }
    }
}
