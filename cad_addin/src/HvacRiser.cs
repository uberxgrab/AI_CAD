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
using HvacAiCadAddin.Routes;
using HvacAiCadAddin.Vrv;

namespace HvacAiCadAddin.Commands
{
    /// <summary>Reads and writes the HVACRISER values in %APPDATA%\HvacAiCadAddin\HVACRISER.settings.</summary>
    internal static class RiserSettingsStore
    {
        private static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HvacAiCadAddin",
                    "HVACRISER.settings");
            }
        }

        internal static RiserSettings Load()
        {
            if (TestMode.Enabled)
                return TestMode.LoadSettings("HVACRISER.settings", RiserSettings.Deserialize, new RiserSettings());

            try
            {
                if (File.Exists(SettingsPath))
                    return RiserSettings.Deserialize(File.ReadAllText(SettingsPath));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return new RiserSettings();
        }

        internal static void Save(RiserSettings settings)
        {
            if (TestMode.Enabled)
                return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath, settings.Serialize());
            }
            catch (IOException)
            {
                // Remembering values is a convenience; the command still runs.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // HVACRISER: a multi-floor refrigerant riser through a shaft. The floor plans of one building sit side
    // by side in model space, so the riser is drawn as a marker on each floor plan plus a schedule. The
    // user goes from the floor farthest from the outdoor unit to the nearest: per floor the indoor units
    // are selected (their capacity is read like HVACPLACEODU does), the riser point is picked (the
    // Shaft keyword takes the centre of the HVAC-ROOM shaft nearest to the units) and the floor is named.
    // Enter ends the loop. The outdoor unit (an HVACAI_ODU block, or a typed HP) fixes the main pipe;
    // otherwise the smallest HP covering the total is used. RiserCore sizes each riser segment from the
    // cumulative index and picks the refnet at each floor junction with the HVACREFRIG tables. After a
    // preview and Yes, circles, labels (layer HVAC-RISER) and a schedule are written in one undo group; a
    // rerun with the same riser name replaces the earlier ones. Nothing is changed before Yes.
    public class HvacRiserCommand
    {
        [CommandMethod("HVACRISER")]
        public void HvacRiser()
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
                document.Editor.WriteMessage("\nHVACRISER stopped: " + ex.Message);
            }
        }

        private sealed class Shaft
        {
            public string Name;
            public Vec2 Centre;
        }

        private static void Run(Document document)
        {
            Database db = document.Database;
            Editor editor = document.Editor;
            RiserSettings settings = RiserSettingsStore.Load();

            // 1. Riser name and optional settings.
            PromptStringOptions nameOptions = new PromptStringOptions("\nRiser name <" + settings.LastName + ">: ")
            {
                AllowSpaces = false,
                DefaultValue = settings.LastName,
                UseDefaultValue = true
            };
            PromptResult nameResult = editor.GetString(nameOptions);
            if (nameResult.Status != PromptStatus.OK)
                return;
            string riserName = string.IsNullOrWhiteSpace(nameResult.StringResult) ? settings.LastName : nameResult.StringResult.Trim();

            PromptKeywordOptions change = new PromptKeywordOptions(string.Format(CultureInfo.InvariantCulture,
                "\nText height {0:0.##}, marker radius {1:0.##}, floor height {2:0.##} mm. Change them? [Yes/No] <No>: ",
                settings.TextHeight, settings.MarkerRadius, settings.FloorHeight));
            change.Keywords.Add("Yes");
            change.Keywords.Add("No");
            change.Keywords.Default = "No";
            change.AllowNone = true;
            PromptResult changeAnswer = editor.GetKeywords(change);
            if (changeAnswer.Status != PromptStatus.OK && changeAnswer.Status != PromptStatus.None)
                return;
            if (changeAnswer.Status == PromptStatus.OK && changeAnswer.StringResult == "Yes")
            {
                if (!AskNumber(editor, "Text height", ref settings.TextHeight)
                    || !AskNumber(editor, "Marker radius", ref settings.MarkerRadius)
                    || !AskNumber(editor, "Floor-to-floor height", ref settings.FloorHeight))
                    return;
                string settingsError;
                if (!settings.IsValid(out settingsError))
                {
                    editor.WriteMessage("\n" + settingsError);
                    return;
                }
            }

            settings.LastName = riserName;
            RiserSettingsStore.Save(settings);

            // 2. The floors, farthest from the outdoor unit first.
            editor.WriteMessage("\nHVACRISER: go from the floor FARTHEST from the outdoor unit to the nearest one; the pipe of a floor carries the units of that floor and of every floor before it. Press Enter at the unit prompt to finish.");
            List<RiserFloorInput> floors = new List<RiserFloorInput>();
            while (true)
            {
                int order = floors.Count;
                PromptSelectionOptions selectOptions = new PromptSelectionOptions
                {
                    MessageForAdding = "\nFloor " + (order + 1).ToString(CultureInfo.InvariantCulture)
                        + ": select its indoor units (Enter to finish): "
                };
                SelectionFilter filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") });
                PromptSelectionResult selected = editor.GetSelection(selectOptions, filter);
                if (selected.Status != PromptStatus.OK || selected.Value == null)
                {
                    if (floors.Count == 0)
                    {
                        editor.WriteMessage("\nNo floor was entered.");
                        return;
                    }

                    break;
                }

                IndoorReadResult read = VrvUnitReader.Read(db, selected.Value.GetObjectIds());
                foreach (string skipped in read.Skipped)
                    editor.WriteMessage("\nSkipped " + skipped + ".");
                if (read.Units.Count == 0)
                {
                    editor.WriteMessage("\nNo indoor unit with a readable capacity: select the floor's units again.");
                    continue;
                }

                int floorIndex = OduPlanner.Sum(read.Units);
                Vec2 centroid = Centroid(read.Units);

                Vec2 riserPoint;
                if (!PickRiserPoint(editor, db, centroid, out riserPoint))
                {
                    if (floors.Count == 0)
                        return;
                    break;
                }

                string defaultName = RiserPlanner.DefaultFloorName(order);
                PromptStringOptions floorName = new PromptStringOptions("\nFloor name <" + defaultName + ">: ")
                {
                    AllowSpaces = true,
                    DefaultValue = defaultName,
                    UseDefaultValue = true
                };
                PromptResult floorNameResult = editor.GetString(floorName);
                if (floorNameResult.Status != PromptStatus.OK)
                    return;
                string name = string.IsNullOrWhiteSpace(floorNameResult.StringResult)
                    ? defaultName
                    : floorNameResult.StringResult.Trim();

                floors.Add(new RiserFloorInput
                {
                    Name = name,
                    UnitCount = read.Units.Count,
                    FloorIndex = floorIndex,
                    X = riserPoint.X,
                    Y = riserPoint.Y
                });
                editor.WriteMessage("\n{0}: {1} units, index {2}.", name, read.Units.Count, floorIndex);
            }

            // 3. The outdoor unit.
            int hp = AskOdu(editor, db);

            // 4. The plan; the up-size rule is asked only when the riser is long.
            RiserPlan plan = RiserPlanner.Plan(riserName, floors, hp, settings.FloorHeight, false);
            if (plan.UpsizeSuggested)
            {
                PromptKeywordOptions upsize = new PromptKeywordOptions(string.Format(CultureInfo.InvariantCulture,
                    "\nThe riser's equivalent length is about {0:0} m (90 m or more). Up-size the main pipe one step? [Yes/No] <No>: ",
                    plan.EquivalentLength / 1000));
                upsize.Keywords.Add("Yes");
                upsize.Keywords.Add("No");
                upsize.Keywords.Default = "No";
                upsize.AllowNone = true;
                PromptResult upsizeAnswer = editor.GetKeywords(upsize);
                if (upsizeAnswer.Status == PromptStatus.OK && upsizeAnswer.StringResult == "Yes")
                    plan = RiserPlanner.Plan(riserName, floors, hp, settings.FloorHeight, true);
            }

            ReportPlan(editor, plan);

            // 5. The schedule point, preview, confirmation.
            PromptPointOptions schedulePoint = new PromptPointOptions("\nTop-left point of the riser schedule: ");
            PromptPointResult scheduleResult = editor.GetPoint(schedulePoint);
            if (scheduleResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\nNothing was written.");
                return;
            }

            Point3d schedule = scheduleResult.Value.TransformBy(editor.CurrentUserCoordinateSystem);

            bool write;
            using (PipeTransients preview = new PipeTransients())
            {
                foreach (RiserFloor floor in plan.Floors)
                {
                    preview.AddCircle(new Vec2(floor.X, floor.Y), settings.MarkerRadius, 0, 6);
                    preview.AddText(
                        new Vec2(floor.X + settings.MarkerRadius + settings.TextHeight * 0.5, floor.Y - settings.TextHeight / 2),
                        PreviewText(plan, floor), settings.TextHeight, 6, 0);
                }

                double lineY = schedule.Y;
                preview.AddText(new Vec2(schedule.X, lineY - settings.TextHeight), plan.ScheduleTitle, settings.TextHeight, 6, 0);
                foreach (string[] row in plan.ScheduleRows())
                {
                    lineY -= settings.TextHeight * 2;
                    preview.AddText(new Vec2(schedule.X, lineY - settings.TextHeight), string.Join("  |  ", row),
                        settings.TextHeight, 6, 0);
                }

                PromptKeywordOptions confirm = new PromptKeywordOptions(string.Format(CultureInfo.InvariantCulture,
                    "\nDraw riser {0} ({1} floors, index {2}, ODU {3} HP)? [Yes/No] <Yes>: ",
                    plan.Name, plan.Floors.Count, plan.TotalIndex, plan.Hp));
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

            // 6. Write in one undo group.
            RiserWriteResult result = RiserWriter.Write(db, db.CurrentSpaceId, plan, settings, schedule.X, schedule.Y);
            foreach (string note in result.Notes)
                editor.WriteMessage("\nNote: " + note);
            editor.WriteMessage("\nHVACRISER: riser {0}: {1} markers on {2}, schedule as {3}{4}.",
                plan.Name, result.Markers, RiserSettings.Layer, result.UsedTable ? "a table" : "an MText",
                result.Erased > 0
                    ? string.Format(CultureInfo.InvariantCulture, "; replaced {0} objects of an earlier run", result.Erased)
                    : "");
            editor.WriteMessage("\nNext: on each floor run HVACROUTEDRAFT with the floor's riser point as the target, then HVACREFRIG with the root at that point; each floor's network carries only that floor's units. Floors are joined by the riser sizes in the schedule.");
        }

        private static string PreviewText(RiserPlan plan, RiserFloor floor)
        {
            // "%%C" is not shown by a DBText transient in every case, so the preview uses the diameter sign.
            return plan.MarkerText(floor).Replace("%%C", "Ø").Replace("%%c", "Ø");
        }

        private static Vec2 Centroid(IList<OduIndoorUnit> units)
        {
            double x = 0, y = 0;
            foreach (OduIndoorUnit unit in units)
            {
                x += unit.X;
                y += unit.Y;
            }

            return new Vec2(x / units.Count, y / units.Count);
        }

        private static bool AskNumber(Editor editor, string caption, ref double value)
        {
            PromptDoubleOptions options = new PromptDoubleOptions("\n" + caption + " (mm) <"
                + value.ToString("0.##", CultureInfo.InvariantCulture) + ">: ")
            {
                DefaultValue = value,
                UseDefaultValue = true,
                AllowNegative = false
            };
            PromptDoubleResult result = editor.GetDouble(options);
            if (result.Status != PromptStatus.OK)
                return false;
            value = result.Value;
            return true;
        }

        // The riser point of a floor: picked, or the centre of the shaft nearest to the floor's units.
        private static bool PickRiserPoint(Editor editor, Database db, Vec2 centroid, out Vec2 point)
        {
            point = new Vec2(0, 0);
            while (true)
            {
                PromptPointOptions options = new PromptPointOptions("\nRiser point of this floor (shaft) or [Shaft]: ");
                options.Keywords.Add("Shaft");
                options.AllowNone = false;
                PromptPointResult result = editor.GetPoint(options);
                if (result.Status == PromptStatus.Keyword)
                {
                    List<Shaft> shafts = ReadShafts(editor, db);
                    if (shafts.Count == 0)
                    {
                        editor.WriteMessage("\nNo shafts found. Run HVACROOMS first, or pick the point.");
                        continue;
                    }

                    int nearest = RouteDraftCore.NearestIndex(centroid, shafts.ConvertAll(s => s.Centre));
                    point = shafts[nearest].Centre;
                    editor.WriteMessage(string.Format(CultureInfo.InvariantCulture,
                        "\nUsing the centre of the shaft nearest to the units: {0} ({1:0.###}, {2:0.###}).",
                        string.IsNullOrWhiteSpace(shafts[nearest].Name) ? "shaft" : shafts[nearest].Name, point.X, point.Y));
                    return true;
                }

                if (result.Status != PromptStatus.OK)
                    return false;

                Point3d wcs = result.Value.TransformBy(editor.CurrentUserCoordinateSystem);
                point = new Vec2(wcs.X, wcs.Y);
                return true;
            }
        }

        // Shaft outlines written by HVACROOMS: closed polylines with HVACAI_ROOM xdata of kind "shaft".
        private static List<Shaft> ReadShafts(Editor editor, Database db)
        {
            List<Shaft> shafts = new List<Shaft>();
            SelectionFilter filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, UnitsApp),
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
            });
            PromptSelectionResult all = editor.SelectAll(filter);
            if (all.Status != PromptStatus.OK || all.Value == null)
                return shafts;

            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in all.Value.GetObjectIds())
                {
                    Autodesk.AutoCAD.DatabaseServices.Polyline polyline =
                        transaction.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Polyline;
                    if (polyline == null || polyline.IsErased || polyline.NumberOfVertices < 3)
                        continue;

                    HvacAiCadAddin.Units.RoomTags.RoomTag room =
                        HvacAiCadAddin.Units.RoomTags.ParseRoom(VrvUnitReader.ReadValues(polyline, UnitsApp));
                    if (room == null || !room.IsShaft)
                        continue;

                    List<Vec2> ring = new List<Vec2>();
                    for (int i = 0; i < polyline.NumberOfVertices; i++)
                    {
                        Point2d p = polyline.GetPoint2dAt(i);
                        ring.Add(new Vec2(p.X, p.Y));
                    }

                    shafts.Add(new Shaft { Name = room.Name, Centre = RouteDraftCore.PolygonCentre(ring) });
                }

                transaction.Commit();
            }

            return shafts;
        }

        private const string UnitsApp = "HVACAI_ROOM";

        // The outdoor unit's HP: from a picked HVACAI_ODU block (or an ODU-n HP block), typed, or 0 for
        // the smallest HP that covers the total.
        private static int AskOdu(Editor editor, Database db)
        {
            while (true)
            {
                PromptEntityOptions options = new PromptEntityOptions(
                    "\nSelect the outdoor unit (HVACPLACEODU block), or [Type the HP/Auto] <Auto>: ");
                options.Keywords.Add("Type");
                options.Keywords.Add("Auto");
                options.AllowNone = true;
                options.SetRejectMessage("\nNot a block.");
                options.AddAllowedClass(typeof(BlockReference), true);
                PromptEntityResult picked = editor.GetEntity(options);
                if (picked.Status == PromptStatus.None
                    || (picked.Status == PromptStatus.Keyword && picked.StringResult == "Auto"))
                    return 0;
                if (picked.Status == PromptStatus.Keyword)
                {
                    PromptIntegerOptions hpOptions = new PromptIntegerOptions("\nOutdoor unit HP: ") { AllowNegative = false, AllowZero = false };
                    PromptIntegerResult hpResult = editor.GetInteger(hpOptions);
                    if (hpResult.Status != PromptStatus.OK)
                        return 0;
                    if (!VrvTables.IsHp(hpResult.Value))
                    {
                        editor.WriteMessage("\nNot an outdoor unit size; use one of: {0}.", string.Join(" ", Array.ConvertAll(VrvTables.HpChoices, v => v.ToString(CultureInfo.InvariantCulture))));
                        continue;
                    }

                    return hpResult.Value;
                }

                if (picked.Status != PromptStatus.OK)
                    return 0;

                int hp = 0;
                using (Transaction transaction = db.TransactionManager.StartTransaction())
                {
                    BlockReference block = transaction.GetObject(picked.ObjectId, OpenMode.ForRead) as BlockReference;
                    if (block != null)
                    {
                        hp = OduWriter.ReadHp(block);
                        if (hp == 0)
                            OduPlanner.TryParseHp(VrvUnitReader.EffectiveName(transaction, block), out hp);
                    }

                    transaction.Commit();
                }

                if (hp == 0)
                {
                    editor.WriteMessage("\nThat block is not an outdoor unit (no HVACAI_ODU data, name not ODU-n HP).");
                    continue;
                }

                editor.WriteMessage("\nOutdoor unit: {0} HP.", hp);
                return hp;
            }
        }

        private static void ReportPlan(Editor editor, RiserPlan plan)
        {
            editor.WriteMessage(string.Format(CultureInfo.InvariantCulture,
                "\nRiser {0}: total index {1}, ODU {2} HP{3} (index {4}, ratio {5:0} %), main pipe {6}.",
                plan.Name, plan.TotalIndex, plan.Hp, plan.HpIsDefault ? " (smallest covering)" : "", plan.OduIndex,
                plan.Ratio, plan.MainPair));
            foreach (RiserFloor floor in plan.Floors)
            {
                editor.WriteMessage(string.Format(CultureInfo.InvariantCulture,
                    "\n  {0}: {1} units, floor index {2}, cumulative {3}, riser {4}, refnet {5}",
                    floor.Name, floor.UnitCount, floor.FloorIndex, floor.Cumulative, floor.PairText, floor.Kit ?? "-"));
            }

            foreach (string warning in plan.Warnings)
                editor.WriteMessage("\nWarning: " + warning);
            foreach (string note in plan.Notes)
                editor.WriteMessage("\nNote: " + note);
        }
    }
}
