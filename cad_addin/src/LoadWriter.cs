using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using HvacAiCadAddin.Rooms;

namespace HvacAiCadAddin.Load
{
    /// <summary>A room polyline picked in the drawing, with what HVACROOMS stored on it.</summary>
    internal sealed class LoadRoomRef
    {
        internal ObjectId Polyline;
        internal string Id;
        internal string Kind;
        internal string Name;
        internal double AreaM2;
    }

    // Writes the HVACLOAD result in one transaction (one undo group): the HVACAI_LOAD xdata on each
    // room polyline [1001 app][1000 room_type][1040 w_per_m2][1040 load_kw][1040 safety_factor] and the
    // load line on the room label ("name\Parea m2\Pload kW"; an earlier kW line is replaced).
    internal static class LoadWriter
    {
        /// <summary>Reads the HVACROOMS xdata of a polyline; null when it is not a room.</summary>
        internal static LoadRoomRef ReadRoom(Transaction transaction, ObjectId id)
        {
            Polyline polyline = transaction.GetObject(id, OpenMode.ForRead) as Polyline;
            if (polyline == null || !string.Equals(polyline.Layer, RoomsSettings.RoomLayer, StringComparison.OrdinalIgnoreCase))
                return null;
            using (ResultBuffer buffer = polyline.GetXDataForApplication(RoomsSettings.XDataApp))
            {
                if (buffer == null)
                    return null;
                TypedValue[] values = buffer.AsArray();
                if (values.Length < 3)
                    return null;
                LoadRoomRef room = new LoadRoomRef
                {
                    Polyline = id,
                    Id = values[1].Value as string ?? "",
                    Kind = values[2].Value as string ?? "room",
                    Name = values.Length > 3 ? values[3].Value as string ?? "" : "",
                    AreaM2 = values.Length > 4 && values[4].Value is double ? (double)values[4].Value : 0
                };
                return room;
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

        /// <summary>
        /// Tags each room and updates its label. <paramref name="loads"/> pairs a room with its result.
        /// Returns the number of polylines tagged; <paramref name="labels"/> counts labels updated.
        /// </summary>
        internal static int Write(
            Database db, IList<KeyValuePair<LoadRoomRef, LoadRoomOut>> loads, double safety,
            IEnumerable<ObjectId> labelCandidates, out int labels)
        {
            labels = 0;
            int tagged = 0;
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                EnsureRegApp(transaction, db, LoadSettings.XDataApp);
                Dictionary<string, List<MText>> byId = LabelsById(transaction, labelCandidates);

                foreach (KeyValuePair<LoadRoomRef, LoadRoomOut> pair in loads)
                {
                    Polyline polyline = (Polyline)transaction.GetObject(pair.Key.Polyline, OpenMode.ForWrite);
                    using (ResultBuffer tag = new ResultBuffer(
                        new TypedValue((int)DxfCode.ExtendedDataRegAppName, LoadSettings.XDataApp),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, pair.Value.RoomType ?? "other"),
                        new TypedValue((int)DxfCode.ExtendedDataReal, pair.Value.WPerM2),
                        new TypedValue((int)DxfCode.ExtendedDataReal, pair.Value.LoadKw),
                        new TypedValue((int)DxfCode.ExtendedDataReal, safety)))
                    {
                        polyline.XData = tag; // replaces only this application's data
                    }

                    tagged++;
                    MText label = NearestLabel(byId, pair.Key.Id, polyline);
                    if (label != null)
                    {
                        label.UpgradeOpen();
                        label.Contents = LoadCore.LabelWithLoad(label.Contents, pair.Value.LoadKw, pair.Value.Cooled);
                        labels++;
                    }
                }

                transaction.Commit();
            }

            return tagged;
        }

        // Labels carry [HVACAI_ROOM][id]["label"]; read them for write so Contents can change.
        private static Dictionary<string, List<MText>> LabelsById(Transaction transaction, IEnumerable<ObjectId> candidates)
        {
            Dictionary<string, List<MText>> byId = new Dictionary<string, List<MText>>(StringComparer.Ordinal);
            foreach (ObjectId id in candidates)
            {
                MText text = transaction.GetObject(id, OpenMode.ForRead) as MText;
                if (text == null || text.IsErased)
                    continue;
                using (ResultBuffer buffer = text.GetXDataForApplication(RoomsSettings.XDataApp))
                {
                    if (buffer == null)
                        continue;
                    TypedValue[] values = buffer.AsArray();
                    if (values.Length < 3 || !string.Equals(values[2].Value as string, "label", StringComparison.Ordinal))
                        continue;
                    string room = values[1].Value as string ?? "";
                    List<MText> list;
                    if (!byId.TryGetValue(room, out list))
                        byId[room] = list = new List<MText>();
                    list.Add(text);
                }
            }

            return byId;
        }

        // Several plans in one drawing can reuse an id (R1...): take the label closest to the room.
        private static MText NearestLabel(Dictionary<string, List<MText>> byId, string id, Polyline room)
        {
            List<MText> list;
            if (!byId.TryGetValue(id ?? "", out list) || list.Count == 0)
                return null;
            if (list.Count == 1)
                return list[0];
            Extents3d box = room.GeometricExtents;
            Point3d centre = new Point3d(
                (box.MinPoint.X + box.MaxPoint.X) / 2, (box.MinPoint.Y + box.MaxPoint.Y) / 2, 0);
            MText best = null;
            double bestDistance = double.MaxValue;
            foreach (MText text in list)
            {
                double dx = text.Location.X - centre.X;
                double dy = text.Location.Y - centre.Y;
                double distance = dx * dx + dy * dy;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = text;
                }
            }

            return best;
        }
    }
}
