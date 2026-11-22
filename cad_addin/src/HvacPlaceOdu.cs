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
using HvacAiCadAddin.Vrv;

namespace HvacAiCadAddin.Commands
{
    // HVACPLACEODU: chooses and places the outdoor unit (Daikin VRV IV, template block ODU-n HP) of one
    // VRV system. The user selects the indoor units of the system (any floors; pickfirst works), the
    // command reads each unit's capacity (HVACAI_PLACED data, "n kW" in the block name, or an equipment
    // label), sums the Daikin indexes and proposes the smallest HP that covers the total (HVACREFRIG's
    // rule; the connection ratio should be 50-130 %). The dialog lets the user change the HP, the model
    // text and the label height. The user then picks the insertion point (roof or terrace) and, with the
    // Rotate keyword, rotates the unit by 90 degrees; a transient outline previews it. On Yes the block
    // and an MText label "ODU-n HP / sigma index / ratio" are written in one undo group, tagged
    // HVACAI_ODU. The Replace keyword replaces an outdoor unit this command wrote earlier. A total above
    // the largest outdoor unit is reported with a proposal for splitting the units into several
    // systems; nothing is split automatically. Nothing is changed before Yes.
    public class HvacPlaceOduCommand
    {
        [CommandMethod("HVACPLACEODU", CommandFlags.UsePickSet)]
        public void HvacPlaceOdu()
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
                document.Editor.WriteMessage("\nHVACPLACEODU stopped: " + ex.Message);
            }
        }

        private static void Run(Document document)
        {
            Database db = document.Database;
            Editor editor = document.Editor;

            // 1. The indoor units of one system.
            List<ObjectId> ids = SelectBlocks(editor, "\nSelect the indoor units of one VRV system: ");
            if (ids == null || ids.Count == 0)
                return;

            IndoorReadResult read = VrvUnitReader.Read(db, ids);
            foreach (string skipped in read.Skipped)
                editor.WriteMessage("\nSkipped " + skipped + ".");
            if (read.Units.Count == 0)
            {
                editor.WriteMessage("\nNo indoor unit with a readable capacity was selected.");
                return;
            }

            int total = OduPlanner.Sum(read.Units);
            editor.WriteMessage("\nHVACPLACEODU: {0} indoor units, total index {1}.", read.Units.Count, total);
            OduPlan overview = OduPlanner.Plan(read.Units.Count, total, 0);
            if (overview.OverLargest)
                ReportSplit(editor, read.Units, VrvTables.OduIndex(VrvTables.HpChoices[VrvTables.HpChoices.Length - 1]));

            // 2. The template (for the block and the layers), then the dialog.
            string error;
            TemplateSource template = null;
            string templatePath = TemplateLocator.GetTemplatePath(editor, false);
            if (templatePath != null)
                template = TemplateSource.TryOpen(templatePath, out error);
            else
                editor.WriteMessage("\nTemplate drawing not selected: a simple block is drawn if the ODU block is missing.");

            try
            {
                Dictionary<int, OduBlockChoice> cache = new Dictionary<int, OduBlockChoice>();
                Func<string, bool> has = name => (template != null && template.HasBlock(name)) || DatabaseHasBlock(db, name);
                Func<int, OduBlockChoice> resolve = hp =>
                {
                    OduBlockChoice choice;
                    if (!cache.TryGetValue(hp, out choice))
                    {
                        choice = OduPlanner.ResolveBlock(hp, has);
                        cache[hp] = choice;
                    }

                    return choice;
                };

                OduSettings settings;
                OduPlan plan;
                OduBlockChoice block;
                using (OduForm form = new OduForm(read.Units, resolve))
                {
                    if (Application.ShowModalDialog(form) != System.Windows.Forms.DialogResult.OK)
                        return;
                    settings = form.Settings;
                    plan = form.Plan;
                    block = form.Block;
                }

                foreach (string warning in plan.Warnings)
                    editor.WriteMessage("\nWarning: " + warning);
                if (block.Warning != null)
                    editor.WriteMessage("\nWarning: " + block.Warning);
                foreach (string note in plan.Notes)
                    editor.WriteMessage("\nNote: " + note);

                // 3. Insertion point and rotation, preview, confirmation.
                double width, height;
                OduPlanner.Footprint(plan.Hp, out width, out height);
                ObjectId replaceId = ObjectId.Null;
                double rotation = 0;
                bool haveDefault = false;
                Vec2 defaultPoint = new Vec2(0, 0);
                Vec2 at;

                while (true)
                {
                    PromptPointOptions options = new PromptPointOptions(string.Format(CultureInfo.InvariantCulture,
                        "\nInsertion point of the outdoor unit (middle of its top edge) [Rotate90/Replace]{0}: ",
                        haveDefault ? " <keep position>" : ""));
                    editor.WriteMessage("\nRotation now " + (rotation * 180 / Math.PI).ToString("0", CultureInfo.InvariantCulture) + " degrees.");
                    options.Keywords.Add("Rotate90");
                    options.Keywords.Add("Replace");
                    options.AllowNone = haveDefault;
                    PromptPointResult picked = editor.GetPoint(options);
                    if (picked.Status == PromptStatus.Keyword)
                    {
                        if (picked.StringResult == "Rotate90")
                        {
                            rotation = NormalizeAngle(rotation + Math.PI / 2);
                        }
                        else if (PickEarlier(editor, db, out replaceId, out defaultPoint, out rotation))
                        {
                            haveDefault = true;
                            editor.WriteMessage("\nThe outdoor unit will be replaced.");
                        }

                        continue;
                    }

                    if (picked.Status == PromptStatus.None && haveDefault)
                    {
                        at = defaultPoint;
                        break;
                    }

                    if (picked.Status != PromptStatus.OK)
                    {
                        editor.WriteMessage("\nNothing was written.");
                        return;
                    }

                    Point3d wcs = picked.Value.TransformBy(editor.CurrentUserCoordinateSystem);
                    at = new Vec2(wcs.X, wcs.Y);
                    break;
                }

                string label = OduPlanner.LabelText(plan, settings.ModelText);
                bool write = false;
                while (true)
                {
                    using (PipeTransients preview = new PipeTransients())
                    {
                        double[][] corners = OduPlanner.Corners(at.X, at.Y, rotation, width, height);
                        Outline outline = new Outline { Closed = true };
                        foreach (double[] corner in corners)
                            outline.Add(new Vec2(corner[0], corner[1]));
                        short colour = plan.RatioOutside || block.Substitute ? (short)1 : (short)4;
                        preview.AddOutline(outline, 0, colour, 0);
                        preview.AddPolyline(new[] { new Vec2(corners[0][0], corners[0][1]), new Vec2(corners[2][0], corners[2][1]) }, 0, colour, 0);
                        preview.AddPolyline(new[] { new Vec2(corners[1][0], corners[1][1]), new Vec2(corners[3][0], corners[3][1]) }, 0, colour, 0);
                        if (settings.WriteLabel)
                        {
                            double cos = Math.Cos(rotation), sin = Math.Sin(rotation);
                            double lx = width / 2 + 200, ly = -height / 2;
                            preview.AddText(new Vec2(at.X + lx * cos - ly * sin, at.Y + lx * sin + ly * cos),
                                plan.BlockName + "  " + plan.Ratio.ToString("0", CultureInfo.InvariantCulture) + " %",
                                settings.TextHeight, colour, 0);
                        }

                        PromptKeywordOptions confirm = new PromptKeywordOptions(string.Format(CultureInfo.InvariantCulture,
                            "\n{0} outdoor unit {1} ({2} indoor units, index {3}, ratio {4:0} %)? [Yes/No/Rotate90] <Yes>: ",
                            replaceId.IsNull ? "Place" : "Replace", block.BlockName, plan.UnitCount, plan.IndoorTotal, plan.Ratio));
                        confirm.Keywords.Add("Yes");
                        confirm.Keywords.Add("No");
                        confirm.Keywords.Add("Rotate90");
                        confirm.Keywords.Default = "Yes";
                        confirm.AllowNone = true;
                        PromptResult answer = editor.GetKeywords(confirm);
                        if (answer.Status == PromptStatus.OK && answer.StringResult == "Rotate90")
                        {
                            rotation = NormalizeAngle(rotation + Math.PI / 2);
                            continue;
                        }

                        write = answer.Status == PromptStatus.None
                            || (answer.Status == PromptStatus.OK && answer.StringResult == "Yes");
                        break;
                    }
                }

                if (!write)
                {
                    editor.WriteMessage("\nNothing was written.");
                    return;
                }

                // 4. Write in one undo group.
                OduWriteResult result = OduWriter.Write(db, db.CurrentSpaceId, new OduWriteRequest
                {
                    Hp = plan.Hp,
                    BlockName = block.BlockName,
                    Fallback = block.Fallback,
                    X = at.X,
                    Y = at.Y,
                    Rotation = rotation,
                    Plan = plan,
                    LabelText = label,
                    WriteLabel = settings.WriteLabel,
                    TextHeight = settings.TextHeight,
                    ReplaceId = replaceId
                }, template);

                foreach (string note in result.Notes)
                    editor.WriteMessage("\nNote: " + note);
                editor.WriteMessage("\nHVACPLACEODU: {0} {1} on {2} (handle {3}), indoor index {4} of {5}, ratio {6:0} %.",
                    result.Replaced ? "replaced with" : "placed", block.BlockName, OduSettings.UnitLayer, result.Handle,
                    plan.IndoorTotal, plan.OduIndex, plan.Ratio);
                editor.WriteMessage("\nUse this ODU's HP in HVACREFRIG (Outdoor unit) and HVACRISER (pick it).");
            }
            finally
            {
                if (template != null)
                    template.Dispose();
            }
        }

        private static double NormalizeAngle(double angle)
        {
            while (angle >= 2 * Math.PI - 1e-9)
                angle -= 2 * Math.PI;
            return angle;
        }

        // The pickfirst set or a new selection, blocks only.
        internal static List<ObjectId> SelectBlocks(Editor editor, string message)
        {
            List<ObjectId> ids = new List<ObjectId>();
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null)
                ids.AddRange(implied.Value.GetObjectIds());
            if (ids.Count == 0)
            {
                PromptSelectionOptions options = new PromptSelectionOptions { MessageForAdding = message };
                SelectionFilter filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") });
                PromptSelectionResult selected = editor.GetSelection(options, filter);
                if (selected.Status != PromptStatus.OK || selected.Value == null)
                    return null;
                ids.AddRange(selected.Value.GetObjectIds());
            }

            return ids;
        }

        private static bool DatabaseHasBlock(Database db, string name)
        {
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)transaction.GetObject(db.BlockTableId, OpenMode.ForRead);
                bool found = table.Has(name);
                transaction.Commit();
                return found;
            }
        }

        // Picks an outdoor unit this command wrote earlier; its position and rotation become the defaults.
        private static bool PickEarlier(Editor editor, Database db, out ObjectId id, out Vec2 position, out double rotation)
        {
            id = ObjectId.Null;
            position = new Vec2(0, 0);
            rotation = 0;
            PromptEntityOptions options = new PromptEntityOptions("\nSelect the outdoor unit to replace: ");
            options.SetRejectMessage("\nNot a block.");
            options.AddAllowedClass(typeof(BlockReference), true);
            PromptEntityResult picked = editor.GetEntity(options);
            if (picked.Status != PromptStatus.OK)
                return false;

            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockReference block = transaction.GetObject(picked.ObjectId, OpenMode.ForRead) as BlockReference;
                bool tagged = block != null && VrvUnitReader.HasApp(block, OduSettings.OduApp);
                if (tagged)
                {
                    id = picked.ObjectId;
                    position = new Vec2(block.Position.X, block.Position.Y);
                    rotation = block.Rotation;
                }

                transaction.Commit();
                if (!tagged)
                    editor.WriteMessage("\nThat block has no HVACAI_ODU data (it was not placed by HVACPLACEODU).");
                return tagged;
            }
        }

        // Prints the systems the units could be split into.
        private static void ReportSplit(Editor editor, IList<OduIndoorUnit> units, int maxIndex)
        {
            List<List<int>> groups = OduPlanner.SplitProposal(units, maxIndex);
            editor.WriteMessage(
                "\nThe indoor total is above the largest outdoor unit. Proposal: split into {0} systems and run HVACPLACEODU once per system:",
                groups.Count);
            for (int g = 0; g < groups.Count; g++)
            {
                int sum = 0;
                List<string> names = new List<string>();
                foreach (int position in groups[g])
                {
                    sum += units[position].Index;
                    names.Add(units[position].Handle);
                }

                OduPlan sub = OduPlanner.Plan(groups[g].Count, sum, 0);
                editor.WriteMessage(string.Format(CultureInfo.InvariantCulture,
                    "\n  System {0}: {1} units, index {2}, {3} HP ({4:0} %). Handles: {5}",
                    g + 1, groups[g].Count, sum, sub.Hp, sub.Ratio, string.Join(" ", names)));
            }
        }
    }
}
