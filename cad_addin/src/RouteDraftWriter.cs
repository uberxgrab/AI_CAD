using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Routes
{
    /// <summary>One draft to write: the kind ("refrig", "drain" or "power") and its tree.</summary>
    internal sealed class RouteDraftSet
    {
        public string Kind;
        public RouteDraftResult Result;

        /// <summary>The points the pipes end at (unit ports or insertion points), in unit order.</summary>
        public List<Vec2> Points = new List<Vec2>();
    }

    // Writes the HVACROUTEDRAFT result in one transaction (one undo group): one LWPOLYLINE per tree piece
    // on HVAC-DRAFT-REFRIG / -DRAIN / -POWER (created when missing), each tagged with the HVACAI_DRAFT
    // xdata [1001 app][1000 kind]. Earlier drafts of the same kind that touch one of the unit points are
    // erased first, so running the command again on the same units replaces them.
    internal static class RouteDraftWriter
    {
        /// <summary>A draft piece this close to a unit point (mm) counts as touching it.</summary>
        internal const double TouchTolerance = 1.0;

        internal static string LayerOf(string kind)
        {
            switch (kind)
            {
                case "drain":
                    return RouteDraftSettings.DrainLayer;
                case "power":
                    return RouteDraftSettings.PowerLayer;
                default:
                    return RouteDraftSettings.RefrigLayer;
            }
        }

        internal static short ColorOf(string kind)
        {
            switch (kind)
            {
                case "drain":
                    return RouteDraftSettings.DrainColor;
                case "power":
                    return RouteDraftSettings.PowerColor;
                default:
                    return RouteDraftSettings.RefrigColor;
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
        /// Erases the earlier drafts (entities carrying the HVACAI_DRAFT xdata) of the written kinds that touch
        /// a unit point, then writes the new trees. Returns false (writing nothing) when there is nothing to write.
        /// </summary>
        internal static bool Write(
            Database db, ObjectId spaceId, IList<RouteDraftSet> sets, IList<Vec2> unitPoints,
            IEnumerable<ObjectId> earlier, out int written, out int erased)
        {
            written = 0;
            erased = 0;
            int pieces = 0;
            foreach (RouteDraftSet set in sets)
                pieces += set.Result.Polylines.Count;
            if (pieces == 0)
                return false;

            HashSet<string> kinds = new HashSet<string>();
            foreach (RouteDraftSet set in sets)
                kinds.Add(set.Kind);

            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                foreach (RouteDraftSet set in sets)
                    EnsureLayer(transaction, db, LayerOf(set.Kind), ColorOf(set.Kind));
                EnsureRegApp(transaction, db, RouteDraftSettings.XDataApp);

                erased = EraseEarlier(transaction, earlier, kinds, unitPoints);

                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(spaceId, OpenMode.ForWrite);
                foreach (RouteDraftSet set in sets)
                {
                    foreach (List<Vec2> line in set.Result.Polylines)
                    {
                        if (line.Count < 2)
                            continue;
                        AddPolyline(transaction, db, space, set.Kind, line);
                        written++;
                    }
                }

                transaction.Commit();
            }

            return written > 0;
        }

        private static void AddPolyline(Transaction transaction, Database db, BlockTableRecord space, string kind, List<Vec2> line)
        {
            Polyline polyline = new Polyline();
            polyline.SetDatabaseDefaults(db);
            for (int i = 0; i < line.Count; i++)
                polyline.AddVertexAt(i, new Point2d(line[i].X, line[i].Y), 0, 0, 0);
            polyline.Layer = LayerOf(kind);
            space.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            using (ResultBuffer tag = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RouteDraftSettings.XDataApp),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, kind)))
            {
                polyline.XData = tag;
            }
        }

        private static int EraseEarlier(
            Transaction transaction, IEnumerable<ObjectId> earlier, HashSet<string> kinds, IList<Vec2> unitPoints)
        {
            int erased = 0;
            foreach (ObjectId id in earlier)
            {
                Entity entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null || entity.IsErased)
                    continue;
                string kind = KindOf(entity);
                if (kind == null || !kinds.Contains(kind))
                    continue;
                if (!Touches(entity, unitPoints))
                    continue;
                entity.UpgradeOpen();
                entity.Erase();
                erased++;
            }

            return erased;
        }

        /// <summary>The kind stored in the HVACAI_DRAFT xdata, or null when the entity has none.</summary>
        internal static string KindOf(Entity entity)
        {
            using (ResultBuffer data = entity.GetXDataForApplication(RouteDraftSettings.XDataApp))
            {
                if (data == null)
                    return null;
                bool first = true;
                foreach (TypedValue value in data)
                {
                    if (first)
                    {
                        first = false; // the application name
                        continue;
                    }

                    if (value.TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                        return value.Value as string;
                }
            }

            return null;
        }

        private static bool Touches(Entity entity, IList<Vec2> unitPoints)
        {
            List<Point2d> vertices = new List<Point2d>();
            Polyline polyline = entity as Polyline;
            Line line = entity as Line;
            if (polyline != null)
            {
                for (int i = 0; i < polyline.NumberOfVertices; i++)
                    vertices.Add(polyline.GetPoint2dAt(i));
            }
            else if (line != null)
            {
                vertices.Add(new Point2d(line.StartPoint.X, line.StartPoint.Y));
                vertices.Add(new Point2d(line.EndPoint.X, line.EndPoint.Y));
            }

            foreach (Point2d v in vertices)
            {
                foreach (Vec2 u in unitPoints)
                {
                    if (Math.Abs(v.X - u.X) <= TouchTolerance && Math.Abs(v.Y - u.Y) <= TouchTolerance)
                        return true;
                }
            }

            return false;
        }
    }
}
