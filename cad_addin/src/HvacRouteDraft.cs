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
using HvacAiCadAddin.Rooms;
using HvacAiCadAddin.Routes;

namespace HvacAiCadAddin.Commands
{
    // HVACROUTEDRAFT: drafts rough orthogonal routes from the selected indoor units (block references) to
    // a target point (the riser or outdoor-unit connection, or the centre of a shaft found by HVACROOMS),
    // ready for HVACREFRIG, HVACDRAIN and HVACPOWERWIRE. Each draft is one connected tree of LWPOLYLINEs
    // whose leaves touch the unit insertion points exactly and whose root is the target point. The trees
    // are previewed as transient graphics; on Yes they are written on HVAC-DRAFT-REFRIG / -DRAIN / -POWER
    // in one undo group, replacing earlier drafts of the same kind that touch these units. Walls are
    // ignored: the user edits the draft before running the cleaner.
    public class HvacRouteDraftCommand
    {
        private sealed class Shaft
        {
            public string Name;
            public Vec2 Centre;
        }

        [CommandMethod("HVACROUTEDRAFT", CommandFlags.UsePickSet)]
        public void HvacRouteDraft()
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
                document.Editor.WriteMessage("\nHVACROUTEDRAFT stopped: " + ex.Message);
            }
        }

        private static void Run(Document document)
        {
            Database db = document.Database;
            Editor editor = document.Editor;

            // 1. The indoor units: the pickfirst set or a new selection, block references only.
            List<Vec2> units = SelectUnits(editor, db);
            if (units == null || units.Count == 0)
                return;
            Vec2 centroid = RouteDraftCore.Centroid(units);

            // 2. Settings.
            RouteDraftSettings settings;
            using (RouteDraftForm form = new RouteDraftForm())
            {
                if (Application.ShowModalDialog(form) != System.Windows.Forms.DialogResult.OK)
                    return;
                settings = form.Settings;
            }

            // 3. Targets.
            Vec2 target;
            if (!PickTarget(editor, db, "Pick the riser or outdoor-unit connection point", centroid, out target))
                return;
            Vec2 drainTarget = target;
            Vec2 powerTarget = target;
            if (settings.DrawDrain && settings.SeparateDrainTarget
                && !PickTarget(editor, db, "Pick the drain target", centroid, out drainTarget))
                return;
            if (settings.DrawPower && settings.SeparatePowerTarget
                && !PickTarget(editor, db, "Pick the power supply point", centroid, out powerTarget))
                return;

            // 4. Build the trees.
            RouteDraftOptions options = RouteDraftOptions.From(settings);
            List<RouteDraftSet> sets = new List<RouteDraftSet>();
            if (settings.DrawRefrig)
                sets.Add(new RouteDraftSet { Kind = "refrig", Result = RouteDraftCore.Build(target, units, options) });
            if (settings.DrawDrain)
                sets.Add(new RouteDraftSet { Kind = "drain", Result = RouteDraftCore.Build(drainTarget, units, options) });
            if (settings.DrawPower)
                sets.Add(new RouteDraftSet { Kind = "power", Result = RouteDraftCore.Build(powerTarget, units, options) });

            int pieces = 0;
            foreach (RouteDraftSet set in sets)
            {
                RouteDraftResult r = set.Result;
                pieces += r.Polylines.Count;
                double farthest = 0;
                foreach (double d in r.UnitPathLengths)
                {
                    if (d > farthest)
                        farthest = d;
                }

                editor.WriteMessage(string.Format(CultureInfo.InvariantCulture,
                    "\n{0}: {1} polyline(s), {2:0.0} m of pipe, farthest unit {3:0.0} m away ({4}).",
                    Describe(set.Kind), r.Polylines.Count, r.TotalLength / 1000.0, farthest / 1000.0, r.ModeUsed));
                foreach (string warning in r.Warnings)
                    editor.WriteMessage("\n  Warning: " + warning);
            }

            if (pieces == 0)
            {
                editor.WriteMessage("\nNothing to draw: every unit lies on its target.");
                return;
            }

            // 5. Preview and confirm.
            bool write;
            using (PipeTransients preview = new PipeTransients())
            {
                double marker = PipeTransients.ViewSize() / 60.0;
                foreach (RouteDraftSet set in sets)
                {
                    foreach (List<Vec2> line in set.Result.Polylines)
                        preview.AddPolyline(line, 0, RouteDraftWriter.ColorOf(set.Kind), 0);
                    preview.AddCircle(set.Result.Root, marker, 0, 2);
                }

                foreach (Vec2 u in units)
                    preview.AddCircle(u, marker / 2, 0, 7);

                PromptKeywordOptions confirm = new PromptKeywordOptions(string.Format(
                    CultureInfo.InvariantCulture,
                    "\nDraw {0} draft route(s) for {1} unit(s)? [Yes/No] <Yes>: ", sets.Count, units.Count));
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

            // 6. Write (one undo group), replacing earlier drafts that touch these units.
            int written;
            int erased;
            bool ok = RouteDraftWriter.Write(db, db.CurrentSpaceId, sets, units, EarlierDrafts(editor), out written, out erased);
            if (!ok)
            {
                editor.WriteMessage("\nNothing was written.");
                return;
            }

            editor.WriteMessage("\nHVACROUTEDRAFT: wrote {0} polyline(s){1}.", written,
                erased > 0 ? string.Format(CultureInfo.InvariantCulture, " and replaced {0} of an earlier run", erased) : "");
            foreach (RouteDraftSet set in sets)
            {
                if (set.Result.Polylines.Count == 0)
                    continue;
                editor.WriteMessage(string.Format(CultureInfo.InvariantCulture,
                    "\n  Run {0} on the {1} lines and pick the root at {2:0.###},{3:0.###}.",
                    CleanerCommand(set.Kind), RouteDraftWriter.LayerOf(set.Kind), set.Result.Root.X, set.Result.Root.Y));
            }
        }

        private static string Describe(string kind)
        {
            switch (kind)
            {
                case "drain":
                    return "Drain";
                case "power":
                    return "Power wire";
                default:
                    return "Refrigerant";
            }
        }

        private static string CleanerCommand(string kind)
        {
            switch (kind)
            {
                case "drain":
                    return "HVACDRAIN";
                case "power":
                    return "HVACPOWERWIRE";
                default:
                    return "HVACREFRIG";
            }
        }

        // The insertion points of the selected block references (the pickfirst set when it has any).
        private static List<Vec2> SelectUnits(Editor editor, Database db)
        {
            PromptSelectionResult selection = editor.SelectImplied();
            List<Vec2> units = selection.Status == PromptStatus.OK && selection.Value != null
                ? InsertPoints(db, selection.Value.GetObjectIds(), editor)
                : new List<Vec2>();
            if (units.Count > 0)
                return units;

            PromptSelectionOptions options = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect the indoor units (blocks): "
            };
            SelectionFilter filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") });
            selection = editor.GetSelection(options, filter);
            if (selection.Status != PromptStatus.OK)
                return null;
            units = InsertPoints(db, selection.Value.GetObjectIds(), editor);
            if (units.Count == 0)
                editor.WriteMessage("\nNo blocks were selected.");
            return units;
        }

        private static List<Vec2> InsertPoints(Database db, ObjectId[] ids, Editor editor)
        {
            List<Vec2> points = new List<Vec2>();
            int others = 0;
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    BlockReference block = transaction.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (block == null || block.IsErased)
                    {
                        others++;
                        continue;
                    }

                    points.Add(new Vec2(block.Position.X, block.Position.Y));
                }

                transaction.Commit();
            }

            if (others > 0)
                editor.WriteMessage("\n{0} selected object(s) were not blocks and are left out.", others);
            return points;
        }

        // The target point, or the centre of the shaft nearest to the units when the user types Shaft.
        private static bool PickTarget(Editor editor, Database db, string prompt, Vec2 centroid, out Vec2 target)
        {
            target = new Vec2(0, 0);
            while (true)
            {
                PromptPointOptions options = new PromptPointOptions("\n" + prompt + " or [Shaft]: ");
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

                    List<Vec2> centres = shafts.ConvertAll(s => s.Centre);
                    int nearest = RouteDraftCore.NearestIndex(centroid, centres);
                    for (int i = 0; i < shafts.Count; i++)
                    {
                        editor.WriteMessage(string.Format(CultureInfo.InvariantCulture,
                            "\n  {0}{1}: {2:0.###},{3:0.###}", i == nearest ? "> " : "  ",
                            string.IsNullOrWhiteSpace(shafts[i].Name) ? "shaft " + (i + 1) : shafts[i].Name,
                            shafts[i].Centre.X, shafts[i].Centre.Y));
                    }

                    target = shafts[nearest].Centre;
                    editor.WriteMessage("\nUsing the centre of the shaft nearest to the units (>).");
                    return true;
                }

                if (result.Status != PromptStatus.OK)
                    return false;

                Point3d wcs = result.Value.TransformBy(editor.CurrentUserCoordinateSystem);
                target = new Vec2(wcs.X, wcs.Y);
                return true;
            }
        }

        // Shaft outlines written by HVACROOMS: closed polylines with HVACAI_ROOM xdata of kind "shaft".
        private static List<Shaft> ReadShafts(Editor editor, Database db)
        {
            List<Shaft> shafts = new List<Shaft>();
            SelectionFilter filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoomsSettings.XDataApp),
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
            });
            PromptSelectionResult all = editor.SelectAll(filter);
            if (all.Status != PromptStatus.OK || all.Value == null)
                return shafts;

            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in all.Value.GetObjectIds())
                {
                    Polyline polyline = transaction.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (polyline == null || polyline.IsErased || polyline.NumberOfVertices < 3)
                        continue;

                    string kind = null;
                    string name = "";
                    using (ResultBuffer data = polyline.GetXDataForApplication(RoomsSettings.XDataApp))
                    {
                        if (data == null)
                            continue;
                        // [1001 app][1000 id][1000 kind][1000 name][1040 area]
                        int text = 0;
                        foreach (TypedValue value in data)
                        {
                            if (value.TypeCode != (int)DxfCode.ExtendedDataAsciiString)
                                continue;
                            text++;
                            if (text == 2)
                                kind = value.Value as string;
                            else if (text == 3)
                                name = value.Value as string ?? "";
                        }
                    }

                    if (!string.Equals(kind, "shaft", StringComparison.Ordinal))
                        continue;

                    List<Vec2> ring = new List<Vec2>();
                    for (int i = 0; i < polyline.NumberOfVertices; i++)
                    {
                        Point2d p = polyline.GetPoint2dAt(i);
                        ring.Add(new Vec2(p.X, p.Y));
                    }

                    shafts.Add(new Shaft { Name = name, Centre = RouteDraftCore.PolygonCentre(ring) });
                }

                transaction.Commit();
            }

            return shafts;
        }

        private static List<ObjectId> EarlierDrafts(Editor editor)
        {
            SelectionFilter filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RouteDraftSettings.XDataApp)
            });
            PromptSelectionResult all = editor.SelectAll(filter);
            return all.Status == PromptStatus.OK && all.Value != null
                ? new List<ObjectId>(all.Value.GetObjectIds())
                : new List<ObjectId>();
        }
    }
}
