using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using HvacAiCadAddin.Load;
using HvacAiCadAddin.Rooms;

namespace HvacAiCadAddin.Commands
{
    // HVACLOAD: area-based cooling load of the rooms HVACROOMS found. The user selects HVAC-ROOM
    // polylines; heat_load.py (a separate python.exe process, never loaded into AutoCAD) classifies
    // each room by its name and computes area x W/m2 x safety factor. The dialog shows the guessed room
    // types and the W/m2 table for correction; on OK Python runs again with the final types and its
    // numbers are written in one undo group: the HVACAI_LOAD xdata on each room polyline and a kW line
    // on its label. A CSV summary is written next to the drawing. Nothing is changed before OK.
    public class HvacLoadCommand
    {
        private const int PythonTimeoutMs = 120000;

        [CommandMethod("HVACLOAD", CommandFlags.UsePickSet)]
        public void HvacLoad()
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
                document.Editor.WriteMessage("\nHVACLOAD stopped: " + ex.Message);
            }
        }

        private static void Run(Document document)
        {
            Database db = document.Database;
            Editor editor = document.Editor;

            // 1. The rooms: the pickfirst set or a new selection of HVAC-ROOM polylines.
            List<LoadRoomRef> rooms = new List<LoadRoomRef>();
            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status == PromptStatus.OK && selection.Value != null)
                rooms = ReadRooms(db, selection.Value.GetObjectIds());
            if (rooms.Count == 0)
            {
                PromptSelectionOptions options = new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect room outlines on " + RoomsSettings.RoomLayer + " (made by HVACROOMS): "
                };
                SelectionFilter filter = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE"),
                    new TypedValue((int)DxfCode.LayerName, RoomsSettings.RoomLayer)
                });
                selection = editor.GetSelection(options, filter);
                if (selection.Status != PromptStatus.OK)
                    return;
                rooms = ReadRooms(db, selection.Value.GetObjectIds());
            }

            if (rooms.Count == 0)
            {
                editor.WriteMessage("\nNo room outlines with {0} data were selected. Run HVACROOMS first.", RoomsSettings.XDataApp);
                return;
            }

            // 2. Script, interpreter and the W/m2 table.
            LoadSettings settings = LoadSettingsStore.Load();
            string addinFolder = Path.GetDirectoryName(typeof(HvacLoadCommand).Assembly.Location);
            string script = settings.ScriptPath.Length > 0 ? settings.ScriptPath : LoadCore.FindScript(addinFolder);
            if (script == null || !File.Exists(script))
            {
                editor.WriteMessage("\n{0} was not found next to the add-in or above it.", LoadCore.ScriptName);
                return;
            }

            string python = settings.PythonExe.Length > 0 ? settings.PythonExe : LoadCore.DefaultPython(script);
            if (python == null || !File.Exists(python))
            {
                editor.WriteMessage("\npython.exe was not found at {0}. Set python= in %APPDATA%\\HvacAiCadAddin\\HVACLOAD.settings.", python ?? "(none)");
                return;
            }

            string tablePath = Path.Combine(Path.GetDirectoryName(script), LoadCore.TableName);
            LoadTable table;
            try
            {
                table = LoadCore.ParseTable(File.ReadAllText(tablePath));
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage("\nThe table {0} cannot be read: {1}", tablePath, ex.Message);
                return;
            }

            // 3. First Python run: the room types by name and the loads with the table as it is.
            string folder = NewWorkFolder();
            LoadResult first = RunPython(editor, python, script, folder, "request1.json", "load1.json", tablePath, null, rooms, null);
            if (first == null)
                return;
            string[] guessed = new string[rooms.Count];
            for (int i = 0; i < rooms.Count; i++)
                guessed[i] = first.Rooms[i].RoomType;

            // 4. Dialog.
            LoadTable edited;
            double safety;
            bool saveTable;
            string[] types;
            string note = (table.Note ?? "").IndexOf("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) >= 0
                ? "The W/m2 table holds PLACEHOLDER values: check them before using the result."
                : "";
            using (LoadSettingsForm form = new LoadSettingsForm(rooms, table, guessed, settings, note))
            {
                if (Application.ShowModalDialog(form) != System.Windows.Forms.DialogResult.OK)
                {
                    editor.WriteMessage("\nNothing was written.");
                    return;
                }

                edited = form.Table;
                safety = form.SafetyFactor;
                saveTable = form.SaveTable;
                types = form.Types;
            }

            // 5. Final Python run with the user's table, types and safety factor.
            string finalTable = Path.Combine(folder, "table_final.json");
            File.WriteAllText(finalTable, LoadCore.TableToJson(edited), new UTF8Encoding(false));
            LoadResult result = RunPython(editor, python, script, folder, "request2.json", "load2.json", finalTable, safety, rooms, types);
            if (result == null)
                return;
            if (saveTable)
            {
                try
                {
                    File.WriteAllText(tablePath, LoadCore.TableToJson(edited), new UTF8Encoding(false));
                    editor.WriteMessage("\nHVACLOAD: saved the W/m2 table to {0}.", tablePath);
                }
                catch (System.Exception ex)
                {
                    editor.WriteMessage("\nThe table could not be saved: {0}", ex.Message);
                }
            }

            // 6. Write to the drawing (one undo group) and the CSV.
            List<KeyValuePair<LoadRoomRef, LoadRoomOut>> loads = new List<KeyValuePair<LoadRoomRef, LoadRoomOut>>();
            for (int i = 0; i < rooms.Count; i++)
                loads.Add(new KeyValuePair<LoadRoomRef, LoadRoomOut>(rooms[i], result.Rooms[i]));

            int labels;
            int tagged = LoadWriter.Write(db, loads, result.SafetyFactor, LabelCandidates(editor), out labels);
            editor.WriteMessage(
                "\nHVACLOAD: {0} rooms tagged ({1} labels updated), total {2} kW.",
                tagged, labels, LoadCore.KwText(result.TotalKw));

            string csvPath = CsvPath(db, folder);
            try
            {
                File.WriteAllText(csvPath, LoadCore.Csv(result.Rooms, result.TotalKw), new UTF8Encoding(true));
            }
            catch (System.Exception)
            {
                csvPath = Path.Combine(folder, "HVACLOAD.csv");
                File.WriteAllText(csvPath, LoadCore.Csv(result.Rooms, result.TotalKw), new UTF8Encoding(true));
            }

            editor.WriteMessage("\nHVACLOAD: summary written to {0}", csvPath);
        }

        private static List<LoadRoomRef> ReadRooms(Database db, ObjectId[] ids)
        {
            List<LoadRoomRef> rooms = new List<LoadRoomRef>();
            using (Transaction transaction = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    LoadRoomRef room = LoadWriter.ReadRoom(transaction, id);
                    if (room != null)
                        rooms.Add(room);
                }

                transaction.Commit();
            }

            return rooms;
        }

        private static List<ObjectId> LabelCandidates(Editor editor)
        {
            SelectionFilter filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "MTEXT"),
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoomsSettings.XDataApp)
            });
            PromptSelectionResult all = editor.SelectAll(filter);
            return all.Status == PromptStatus.OK && all.Value != null
                ? new List<ObjectId>(all.Value.GetObjectIds())
                : new List<ObjectId>();
        }

        private static string NewWorkFolder()
        {
            string folder = Path.Combine(
                Path.GetTempPath(), "HvacAiCadAddin", "load",
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static string CsvPath(Database db, string folder)
        {
            string drawing = db.Filename;
            if (!string.IsNullOrEmpty(drawing) && File.Exists(drawing))
            {
                return Path.Combine(
                    Path.GetDirectoryName(drawing),
                    Path.GetFileNameWithoutExtension(drawing) + "_HVACLOAD.csv");
            }

            return Path.Combine(folder, "HVACLOAD.csv");
        }

        // Writes the request, runs Python and returns its parsed result, or null (after a message).
        private static LoadResult RunPython(
            Editor editor, string python, string script, string folder, string requestName, string outName,
            string tablePath, double? safety, IList<LoadRoomRef> rooms, string[] types)
        {
            LoadRequest request = new LoadRequest { Out = outName, Table = tablePath, SafetyFactor = safety };
            request.Rooms = new LoadRoomIn[rooms.Count];
            for (int i = 0; i < rooms.Count; i++)
            {
                request.Rooms[i] = new LoadRoomIn
                {
                    Id = rooms[i].Id,
                    Name = rooms[i].Name,
                    Kind = rooms[i].Kind,
                    AreaM2 = rooms[i].AreaM2,
                    RoomTypeOverride = types == null ? null : types[i]
                };
            }

            string requestPath = Path.Combine(folder, requestName);
            File.WriteAllText(requestPath, LoadCore.ToJson(request), new UTF8Encoding(false));
            string output;
            int exitCode = RoomsExchange.RunPython(python, script, requestPath, PythonTimeoutMs, out output);
            string resultPath = Path.Combine(folder, outName);
            if (!File.Exists(resultPath))
            {
                editor.WriteMessage("\nPython wrote no result (exit code {0}).\n{1}", exitCode, output);
                return null;
            }

            LoadResult result = LoadCore.ParseResult(File.ReadAllText(resultPath));
            if (!result.IsOk)
            {
                editor.WriteMessage("\nPython reported an error: {0}\nFiles kept in {1}", result.Message, folder);
                return null;
            }

            if (result.Rooms.Length != rooms.Count)
            {
                editor.WriteMessage("\nPython returned {0} rooms for {1} requested.", result.Rooms.Length, rooms.Count);
                return null;
            }

            foreach (string warning in result.Warnings)
                editor.WriteMessage("\nWarning: " + warning);
            return result;
        }
    }
}
