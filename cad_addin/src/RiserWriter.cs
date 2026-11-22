using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace HvacAiCadAddin.Vrv
{
    internal sealed class RiserWriteResult
    {
        public int Markers;
        public int Erased;
        /// <summary>True when the schedule is an AutoCAD table, false when it is an MText.</summary>
        public bool UsedTable;
        public readonly List<string> Notes = new List<string>();
    }

    // Writes the HVACRISER result in one transaction (one undo group): per floor a circle and an MText on
    // HVAC-RISER at the floor's riser point, and the schedule (a Table, or an MText when a table cannot be
    // made) at the picked point. All are tagged HVACAI_RISER: [1001][1000 riser name][1000 floor]
    // [1070 floor order][1071 floor index][1071 cumulative index][1000 gas/liquid text]; the schedule has
    // floor "SCHEDULE" and order -1. Entities of the same riser name that an earlier run wrote are erased
    // first, wherever they are in the space.
    internal static class RiserWriter
    {
        internal static RiserWriteResult Write(
            Database db, ObjectId spaceId, RiserPlan plan, RiserSettings settings, double scheduleX, double scheduleY)
        {
            RiserWriteResult result = new RiserWriteResult();
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                VrvUnitReader.EnsureLayer(transaction, db, RiserSettings.Layer, RiserSettings.LayerColor);
                VrvUnitReader.EnsureRegApp(transaction, db, RiserSettings.RiserApp);
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(spaceId, OpenMode.ForWrite);

                result.Erased = EraseEarlier(transaction, space, plan.Name);

                foreach (RiserFloor floor in plan.Floors)
                {
                    Circle marker = new Circle(new Point3d(floor.X, floor.Y, 0), Vector3d.ZAxis, settings.MarkerRadius);
                    marker.SetDatabaseDefaults(db);
                    marker.Layer = RiserSettings.Layer;
                    space.AppendEntity(marker);
                    transaction.AddNewlyCreatedDBObject(marker, true);
                    Tag(marker, plan, floor.Name, floor.Order, floor.FloorIndex, floor.Cumulative, floor.HasPair ? PlainPair(floor) : "-");

                    MText text = new MText();
                    text.SetDatabaseDefaults(db);
                    text.Layer = RiserSettings.Layer;
                    text.TextHeight = settings.TextHeight;
                    text.Attachment = AttachmentPoint.MiddleLeft;
                    text.Location = new Point3d(floor.X + settings.MarkerRadius + settings.TextHeight * 0.5, floor.Y, 0);
                    text.Contents = plan.MarkerText(floor);
                    space.AppendEntity(text);
                    transaction.AddNewlyCreatedDBObject(text, true);
                    Tag(text, plan, floor.Name, floor.Order, floor.FloorIndex, floor.Cumulative, floor.HasPair ? PlainPair(floor) : "-");
                    result.Markers++;
                }

                Entity schedule = null;
                try
                {
                    schedule = BuildTable(db, plan, settings, scheduleX, scheduleY);
                    result.UsedTable = true;
                }
                catch (System.Exception ex)
                {
                    result.Notes.Add("The schedule table could not be made (" + ex.Message + "); an MText is used.");
                }

                if (schedule == null)
                    schedule = BuildMText(db, plan, settings, scheduleX, scheduleY);

                schedule.Layer = RiserSettings.Layer;
                space.AppendEntity(schedule);
                transaction.AddNewlyCreatedDBObject(schedule, true);
                RiserFloor last = plan.Floors[plan.Floors.Count - 1];
                Tag(schedule, plan, "SCHEDULE", -1, plan.TotalIndex, plan.TotalIndex, PlainPair(last));

                transaction.Commit();
            }

            return result;
        }

        private static string PlainPair(RiserFloor floor)
        {
            return floor.HasPair
                ? floor.Pair.Gas.ToString("0.0", CultureInfo.InvariantCulture) + "/"
                    + floor.Pair.Liquid.ToString("0.0", CultureInfo.InvariantCulture)
                : "-";
        }

        private static void Tag(Entity entity, RiserPlan plan, string floor, int order, int floorIndex, int cumulative, string pair)
        {
            using (ResultBuffer tag = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RiserSettings.RiserApp),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, plan.Name),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, floor),
                new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)order),
                new TypedValue((int)DxfCode.ExtendedDataInteger32, floorIndex),
                new TypedValue((int)DxfCode.ExtendedDataInteger32, cumulative),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, pair)))
            {
                entity.XData = tag;
            }
        }

        private static Table BuildTable(Database db, RiserPlan plan, RiserSettings settings, double x, double y)
        {
            List<string[]> rows = plan.ScheduleRows();
            int columns = RiserPlan.ScheduleHeader.Length;
            Table table = new Table();
            try
            {
                table.SetDatabaseDefaults(db);
                table.TableStyle = db.Tablestyle;
                table.SetSize(rows.Count + 2, columns);
                table.Position = new Point3d(x, y, 0);

                double rowHeight = settings.TextHeight * 2;
                table.SetRowHeight(rowHeight);
                double[] widths = new double[columns];
                for (int c = 0; c < columns; c++)
                    widths[c] = RiserPlan.ScheduleHeader[c].Length;
                foreach (string[] row in rows)
                {
                    for (int c = 0; c < columns; c++)
                        widths[c] = Math.Max(widths[c], row[c].Length);
                }

                for (int c = 0; c < columns; c++)
                    table.Columns[c].Width = (widths[c] + 3) * settings.TextHeight * 0.75;

                table.MergeCells(CellRange.Create(table, 0, 0, 0, columns - 1));
                table.Cells[0, 0].TextString = plan.ScheduleTitle;
                for (int c = 0; c < columns; c++)
                    table.Cells[1, c].TextString = RiserPlan.ScheduleHeader[c];
                for (int r = 0; r < rows.Count; r++)
                {
                    for (int c = 0; c < columns; c++)
                        table.Cells[r + 2, c].TextString = rows[r][c];
                }

                for (int r = 0; r < rows.Count + 2; r++)
                {
                    for (int c = 0; c < columns; c++)
                        table.Cells[r, c].TextHeight = settings.TextHeight;
                }

                table.GenerateLayout();
                return table;
            }
            catch
            {
                table.Dispose();
                throw;
            }
        }

        private static MText BuildMText(Database db, RiserPlan plan, RiserSettings settings, double x, double y)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            text.Append(plan.ScheduleTitle);
            text.Append("\\P").Append(string.Join(" | ", RiserPlan.ScheduleHeader));
            foreach (string[] row in plan.ScheduleRows())
                text.Append("\\P").Append(string.Join(" | ", row));
            MText mtext = new MText();
            mtext.SetDatabaseDefaults(db);
            mtext.TextHeight = settings.TextHeight;
            mtext.Attachment = AttachmentPoint.TopLeft;
            mtext.Location = new Point3d(x, y, 0);
            mtext.Contents = text.ToString();
            return mtext;
        }

        // Erases circles, texts and tables tagged HVACAI_RISER with this riser name.
        private static int EraseEarlier(Transaction transaction, BlockTableRecord space, string name)
        {
            int erased = 0;
            foreach (ObjectId id in space)
            {
                string dxf = id.ObjectClass.DxfName;
                if (dxf != "CIRCLE" && dxf != "MTEXT" && dxf != "ACAD_TABLE")
                    continue;
                Entity entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null || entity.IsErased)
                    continue;
                IList<object> values = VrvUnitReader.ReadValues(entity, RiserSettings.RiserApp);
                if (values.Count < 2 || !string.Equals(values[1] as string, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                entity.UpgradeOpen();
                entity.Erase();
                erased++;
            }

            return erased;
        }
    }
}
