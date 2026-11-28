using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Pipes;
using HvacAiCadAddin.Units;

namespace HvacAiCadAddin.Commands
{
    // HVACPLACEUNITS: chooses and places VRV indoor units (Daikin cassette, FCU-CAS-n kW blocks of the
    // template) in the rooms HVACROOMS found. It reads the HVAC-ROOM polylines (HVACAI_ROOM xdata) and,
    // when the heat-load command has run, their HVACAI_LOAD load; a room without a load can be given a kW
    // in the dialog. Per room the smallest unit that covers the load is chosen, or the load is split into
    // equal units when it exceeds the largest allowed unit. The units are previewed as transient outlines;
    // on Yes they are inserted on the template's indoor-unit layer in one undo group and tagged with the
    // HVACAI_PLACED xdata. A new run replaces the units earlier tagged for the same rooms. Nothing is
    // changed before Yes. Pre-selected room polylines limit the run to those rooms.
    public class HvacPlaceUnitsCommand
    {
        [CommandMethod("HVACPLACEUNITS", CommandFlags.UsePickSet)]
        public void HvacPlaceUnits()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            try
            {
                Run(document);
            }
            catch (System.Exception ex)
            {
                document.Editor.WriteMessage("\nHVACPLACEUNITS stopped: " + ex.Message);
            }
        }

        private static void Run(Document document)
        {
            Database db = document.Database;
            Editor editor = document.Editor;

            // 1. The rooms: the pickfirst set when it holds room polylines, otherwise every room of the space.
            List<ObjectId> candidates = new List<ObjectId>();
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null)
                candidates.AddRange(implied.Value.GetObjectIds());
            int shafts;
            List<RoomUnitInput> rooms = ReadRooms(db, candidates, out shafts);
            if (rooms.Count == 0 && candidates.Count > 0)
                rooms = ReadRooms(db, AllRoomPolylines(editor), out shafts);
            if (rooms.Count == 0 && candidates.Count == 0)
                rooms = ReadRooms(db, AllRoomPolylines(editor), out shafts);
            if (rooms.Count == 0)
            {
                editor.WriteMessage("\nNo rooms found. Run HVACROOMS first (closed polylines on {0} with {1} data).",
                    UnitSettings.RoomLayer, UnitSettings.RoomApp);
                return;
            }

            editor.WriteMessage("\nHVACPLACEUNITS: {0} rooms read{1}.", rooms.Count,
                shafts > 0 ? string.Format(CultureInfo.InvariantCulture, " ({0} shafts skipped)", shafts) : "");

            // 2. Dialog.
            UnitSettings settings;
            List<RoomUnitPlan> plans;
            using (UnitPlacementForm form = new UnitPlacementForm(rooms))
            {
                if (DialogHost.ShowModal(form) != System.Windows.Forms.DialogResult.OK)
                    return;
                settings = form.Settings;
                plans = form.Plans;
            }

            // 3. Place inside each polygon.
            UnitFamily family = UnitCatalog.Find(settings.Family);
            List<PlannedUnit> units = new List<PlannedUnit>();
            List<string> roomIds = new List<string>();
            foreach (RoomUnitPlan plan in plans)
            {
                roomIds.Add(plan.Room.Id);
                List<PlacedUnitPose> poses = UnitPlacer.Place(
                    plan.Room.Polygon, plan.Choice.Units, family.Width, family.Height, settings.Clearance);
                int tight = 0;
                for (int i = 0; i < poses.Count; i++)
                {
                    if (!poses[i].Fits)
                        tight++;
                    units.Add(new PlannedUnit
                    {
                        RoomId = plan.Room.Id,
                        Family = family,
                        Model = plan.Choice.Model,
                        Number = i + 1,
                        OfCount = poses.Count,
                        Pose = poses[i]
                    });
                }

                if (tight > 0)
                    editor.WriteMessage("\nWarning: room {0}: {1} unit(s) do not fit with the {2:0} mm clearance (placed anyway).",
                        plan.Room.Id, tight, settings.Clearance);
                if (plan.Choice.Undersized)
                    editor.WriteMessage("\nWarning: room {0}: the units are smaller than the load.", plan.Room.Id);
            }

            // 4. Preview and confirm.
            bool write;
            using (PipeTransients preview = new PipeTransients())
            {
                foreach (PlannedUnit unit in units)
                {
                    double[][] corners = UnitPlacer.Corners(unit.Pose, family.Width, family.Height);
                    Outline outline = new Outline { Closed = true };
                    foreach (double[] corner in corners)
                        outline.Add(new Vec2(corner[0], corner[1]));
                    short colour = unit.Pose.Fits ? (short)4 : (short)1;
                    preview.AddOutline(outline, 0, colour, 0);
                    preview.AddPolyline(new[] { new Vec2(corners[0][0], corners[0][1]), new Vec2(corners[2][0], corners[2][1]) }, 0, colour, 0);
                    preview.AddPolyline(new[] { new Vec2(corners[1][0], corners[1][1]), new Vec2(corners[3][0], corners[3][1]) }, 0, colour, 0);
                    preview.AddText(new Vec2(unit.Pose.X - family.Width / 2 + 40, unit.Pose.Y - 60),
                        unit.Model.Kw.ToString("0.0", CultureInfo.InvariantCulture) + " kW", 120, colour, 0);
                }

                PromptKeywordOptions confirm = new PromptKeywordOptions(string.Format(
                    CultureInfo.InvariantCulture,
                    "\nPlace {0} indoor units in {1} rooms? [Yes/No] <Yes>: ", units.Count, plans.Count));
                confirm.Keywords.Add("Yes");
                confirm.Keywords.Add("No");
                confirm.Keywords.Default = "Yes";
                confirm.AllowNone = true;
                PromptResult answer = editor.GetKeywords(confirm);
                write = answer.Status == PromptStatus.None
                    || (answer.Status == PromptStatus.OK && answer.StringResult == "Yes");
            }

            if (!write)
            {
                editor.WriteMessage("\nNothing was written.");
                return;
            }

            // 5. Write in one undo group; the template supplies missing blocks and the layer.
            string error;
            TemplateSource template = null;
            string templatePath = TemplateLocator.GetTemplatePath(editor, false);
            if (templatePath != null)
                template = TemplateSource.TryOpen(templatePath, out error);
            else
                editor.WriteMessage("\nTemplate drawing not selected: missing unit blocks are drawn as simple blocks.");

            UnitWriteResult result;
            try
            {
                result = UnitPlacementWriter.Write(db, db.CurrentSpaceId, units, roomIds, template);
            }
            finally
            {
                if (template != null)
                    template.Dispose();
            }

            foreach (string note in result.Notes)
                editor.WriteMessage("\nNote: " + note);
            editor.WriteMessage("\nHVACPLACEUNITS: inserted {0} indoor units on {1}{2}.", result.Inserted, UnitSettings.UnitLayer,
                result.Erased > 0
                    ? string.Format(CultureInfo.InvariantCulture, " and replaced {0} units of an earlier run", result.Erased)
                    : "");
        }

        private static List<ObjectId> AllRoomPolylines(Editor editor)
        {
            SelectionFilter filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, UnitSettings.RoomApp),
                new TypedValue((int)DxfCode.LayerName, UnitSettings.RoomLayer)
            });
            PromptSelectionResult all = editor.SelectAll(filter);
            return all.Status == PromptStatus.OK && all.Value != null
                ? new List<ObjectId>(all.Value.GetObjectIds())
                : new List<ObjectId>();
        }

        // Closed polylines that carry HVACAI_ROOM data. Shafts are counted, not returned.
        private static List<RoomUnitInput> ReadRooms(Database db, IList<ObjectId> ids, out int shafts)
        {
            shafts = 0;
            List<RoomUnitInput> rooms = new List<RoomUnitInput>();
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Polyline polyline = transaction.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (polyline == null || polyline.IsErased || polyline.NumberOfVertices < 3)
                        continue;

                    RoomTags.RoomTag room = RoomTags.ParseRoom(ReadXData(polyline, UnitSettings.RoomApp));
                    if (room == null)
                        continue;
                    if (room.IsShaft)
                    {
                        shafts++;
                        continue;
                    }

                    double[][] polygon = ToPolygon(polyline);
                    if (polygon.Length < 3)
                        continue;

                    RoomUnitInput input = new RoomUnitInput
                    {
                        Id = room.Id,
                        Name = room.Name,
                        AreaM2 = room.AreaM2,
                        Polygon = polygon
                    };
                    RoomTags.LoadTag load = RoomTags.ParseLoad(ReadXData(polyline, UnitSettings.LoadApp));
                    if (load != null && load.LoadKw > 0)
                        input.LoadKw = load.LoadKw;
                    rooms.Add(input);
                }

                transaction.Commit();
            }

            rooms.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
            return rooms;
        }

        private static IList<object> ReadXData(Entity entity, string app)
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

        private static double[][] ToPolygon(Polyline polyline)
        {
            List<double[]> points = new List<double[]>();
            for (int i = 0; i < polyline.NumberOfVertices; i++)
            {
                Point2d point = polyline.GetPoint2dAt(i);
                points.Add(new[] { point.X, point.Y });
            }

            if (points.Count > 1)
            {
                double[] first = points[0], last = points[points.Count - 1];
                if (Math.Abs(first[0] - last[0]) < 1e-9 && Math.Abs(first[1] - last[1]) < 1e-9)
                    points.RemoveAt(points.Count - 1);
            }

            return points.ToArray();
        }
    }
}
