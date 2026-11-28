using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Pipes;
using HvacAiCadAddin.Rooms;

namespace HvacAiCadAddin.Commands
{
    // HVACROOMS: finds the rooms of one floor plan, semi-automatically. The user selects the plan
    // (window/crossing; bound xrefs and blocks are fine) and ticks which layers are walls in the
    // dialog. The selection is copied to a DXF snapshot and arch_rooms.py runs on it as a separate
    // python.exe process (Python is never loaded into AutoCAD). The rooms it returns are previewed as
    // transient graphics; on Yes each room becomes a closed LWPOLYLINE on HVAC-ROOM (shafts, found by
    // their X mark or a HKT/gen/shaft text, in colour 6) with an MText "name / area" on
    // HVAC-ROOM-TEXT, in one undo group. Lines on HVAC-ROOM-SPLIT always count as walls, so the user
    // can split an open space and run the command again; a new run replaces the earlier rooms inside
    // the selected area. No changes are made before Yes.
    public class HvacRoomsCommand
    {
        private const int PythonTimeoutMs = 300000;

        [CommandMethod("HVACROOMS", CommandFlags.UsePickSet)]
        public void HvacRooms()
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
                document.Editor.WriteMessage("\nHVACROOMS stopped: " + ex.Message);
            }
        }

        private static void Run(Document document)
        {
            Database db = document.Database;
            Editor editor = document.Editor;

            // 1. The plan: the pickfirst set or a new selection. Window/crossing areas give the region.
            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                PromptSelectionOptions options = new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect one floor plan (window or crossing around it): "
                };
                selection = editor.GetSelection(options);
                if (selection.Status != PromptStatus.OK)
                    return;
            }

            List<ObjectId> ids = new List<ObjectId>(selection.Value.GetObjectIds());
            double[][] region = RoomsExchangeCore.RegionFromPoints(SelectionAreaPoints(selection.Value));

            // 2. Which layers are walls.
            SortedDictionary<string, int> layers = RoomsExchange.CollectLayers(db, ids);
            RoomsSettings settings;
            string script;
            string python;
            string addinFolder = Path.GetDirectoryName(typeof(HvacRoomsCommand).Assembly.Location);
            using (RoomsSettingsForm form = new RoomsSettingsForm(layers, addinFolder))
            {
                if (DialogHost.ShowModal(form) != System.Windows.Forms.DialogResult.OK)
                    return;
                settings = form.Settings;
                script = form.ResolvedScript;
                python = form.ResolvedPython;
            }

            // 3. Snapshot, request, Python.
            string folder = RoomsExchange.NewWorkFolder();
            string requestPath = Path.Combine(folder, "request.json");
            RoomsRequest request = RoomsRequest.From(settings, region);
            editor.WriteMessage("\nHVACROOMS: copying {0} objects to {1}", ids.Count, folder);
            RoomsExchange.WriteSnapshot(db, ids, Path.Combine(folder, request.Dxf));
            RoomsExchange.WriteRequest(requestPath, request);

            editor.WriteMessage("\nHVACROOMS: finding rooms (Python)... ");
            System.Windows.Forms.Application.DoEvents();
            string output;
            int exitCode = RoomsExchange.RunPython(python, script, requestPath, PythonTimeoutMs, out output);
            string resultPath = Path.Combine(folder, request.Out);
            if (!File.Exists(resultPath))
            {
                editor.WriteMessage("\nPython wrote no result (exit code {0}).\n{1}", exitCode, Shorten(output));
                return;
            }

            RoomsResult result = RoomsExchangeCore.ParseResult(File.ReadAllText(resultPath));
            if (!result.IsOk)
            {
                editor.WriteMessage("\nPython reported an error: {0}\nFiles kept in {1}", result.Message, folder);
                return;
            }

            foreach (string warning in result.Warnings)
                editor.WriteMessage("\nWarning: " + warning);

            List<RoomItem> rooms = new List<RoomItem>();
            foreach (RoomItem room in result.Rooms)
            {
                if (RoomsExchangeCore.IsUsable(room))
                    rooms.Add(room);
            }

            if (rooms.Count == 0)
            {
                editor.WriteMessage("\nNo rooms were found. Tick more wall layers (facade, glass, doors) or draw "
                    + "the missing walls on {0}, then run HVACROOMS again.", RoomsSettings.SplitLayer);
                return;
            }

            int shafts = 0;
            double total = 0;
            foreach (RoomItem room in rooms)
            {
                total += room.AreaM2;
                if (room.IsShaft)
                    shafts++;
            }

            // 4. Preview and confirm.
            bool write;
            using (PipeTransients preview = new PipeTransients())
            {
                foreach (RoomItem room in rooms)
                {
                    Outline outline = new Outline { Closed = true };
                    foreach (double[] p in room.Polygon)
                        outline.Add(new Vec2(p[0], p[1]));
                    preview.AddOutline(outline, 0, room.IsShaft ? RoomsSettings.ShaftColor : (short)3, 0);
                    Point2d at = RoomsWriter.LabelPoint(room);
                    string name = string.IsNullOrWhiteSpace(room.Name) ? (room.IsShaft ? "HKT" : room.Id) : room.Name;
                    preview.AddText(new Vec2(at.X, at.Y), name + " " + room.AreaM2.ToString("0.0", CultureInfo.InvariantCulture),
                        settings.TextHeight, 2, 0);
                }

                PromptKeywordOptions confirm = new PromptKeywordOptions(string.Format(
                    CultureInfo.InvariantCulture,
                    "\nFound {0} rooms and {1} shafts, {2:0.0} m2 in total ({3} openings closed). Write them? [Yes/No] <Yes>: ",
                    rooms.Count - shafts, shafts, total, result.Stats != null ? result.Stats.Bridges : 0));
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

            // 5. Write (one undo group), replacing earlier rooms inside the same area.
            Extents3d? replaceBox = ReplaceBox(region, rooms);
            int written;
            int erased;
            RoomsWriter.Write(db, db.CurrentSpaceId, rooms, settings, EarlierRoomEntities(editor),
                replaceBox, out written, out erased);
            editor.WriteMessage("\nHVACROOMS: wrote {0} rooms on {1}{2}.", written, RoomsSettings.RoomLayer,
                erased > 0 ? string.Format(CultureInfo.InvariantCulture, " and replaced {0} objects of an earlier run", erased) : "");
        }

        // Corners of the window/crossing areas of the selection, on the WCS XY plane.
        private static List<double[]> SelectionAreaPoints(SelectionSet set)
        {
            List<double[]> points = new List<double[]>();
            foreach (SelectedObject selected in set)
            {
                CrossingOrWindowSelectedObject byArea = selected as CrossingOrWindowSelectedObject;
                if (byArea == null)
                    continue;
                PickPointDescriptor[] picks = byArea.GetPickPoints();
                if (picks == null)
                    continue;
                foreach (PickPointDescriptor pick in picks)
                {
                    Point3d point = pick.PointOnLine;
                    Vector3d direction = pick.Direction;
                    if (Math.Abs(direction.Z) > 1e-9)
                        point = point + direction * (-point.Z / direction.Z);
                    points.Add(new[] { point.X, point.Y });
                }
            }

            return points;
        }

        private static List<ObjectId> EarlierRoomEntities(Editor editor)
        {
            SelectionFilter filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoomsSettings.XDataApp)
            });
            PromptSelectionResult all = editor.SelectAll(filter);
            return all.Status == PromptStatus.OK && all.Value != null
                ? new List<ObjectId>(all.Value.GetObjectIds())
                : new List<ObjectId>();
        }

        // The selected area, or the box around the new rooms when the plan was picked object by object.
        private static Extents3d? ReplaceBox(double[][] region, List<RoomItem> rooms)
        {
            List<double[]> points = new List<double[]>();
            if (region != null)
                points.AddRange(region);
            else
            {
                foreach (RoomItem room in rooms)
                    points.AddRange(room.Polygon);
            }

            double[][] box = RoomsExchangeCore.RegionFromPoints(points);
            if (box == null)
                return null;
            return new Extents3d(new Point3d(box[0][0], box[0][1], 0), new Point3d(box[2][0], box[2][1], 0));
        }

        private static string Shorten(string text)
        {
            text = text ?? "";
            return text.Length <= 1500 ? text : text.Substring(text.Length - 1500);
        }
    }
}
