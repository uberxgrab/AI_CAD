using System;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Commands
{
    // HVACDUCTALIGN (Revit "Align" for ducts): pick a reference edge, then the edge of a duct to align,
    // and the duct moves perpendicular to the edge until the two are collinear. When the reference
    // belongs to a duct whose end faces the moved duct, the two can be joined. The maths and the edit
    // live in DuctAlignMath and DuctAligner (headless-tested); this class only prompts.
    // Perpendicular ducts are out of scope in this version.
    public class HvacDuctAlignCommand
    {
        [CommandMethod("HVACDUCTALIGN")]
        public void HvacDuctAlign()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            try
            {
                Run(document);
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage("\nHVACDUCTALIGN stopped: " + ex.Message);
            }
        }

        private sealed class Reference
        {
            public Vec2 From;
            public Vec2 To;
            public DuctInfo Duct;
        }

        private static void Run(Document document)
        {
            Database db = document.Database;
            Editor editor = document.Editor;
            FittingSettings fittings = DuctSettingsStore.Load().Fittings;

            editor.WriteMessage(
                "\nHVACDUCTALIGN: pick a reference edge, then the side edge of a duct to align to it.");
            editor.WriteMessage("\nPerpendicular ducts are not supported in this version.");

            while (true)
            {
                Reference reference = PickReference(editor, db);
                if (reference == null)
                    return;

                bool multiple = false;
                while (true)
                {
                    PromptNestedEntityOptions options = new PromptNestedEntityOptions(
                        "\nSelect the edge to align" + (multiple ? " (Enter for a new reference)" : "") + ": ");
                    options.AllowNone = true;
                    if (!multiple)
                        options.Keywords.Add("Multiple");

                    PromptNestedEntityResult result = editor.GetNestedEntity(options);
                    if (result.Status == PromptStatus.Keyword)
                    {
                        multiple = true;
                        editor.WriteMessage("\nThe same reference stays active for further picks.");
                        continue;
                    }

                    if (result.Status == PromptStatus.Cancel || result.Status == PromptStatus.Error)
                        return;
                    if (result.Status != PromptStatus.OK)
                        break;

                    bool aligned = AlignOne(editor, db, reference, result, fittings);
                    if (aligned && !multiple)
                        break;
                }
            }
        }

        // Returns null when the user presses Enter or Esc at the reference prompt.
        private static Reference PickReference(Editor editor, Database db)
        {
            while (true)
            {
                PromptNestedEntityOptions options = new PromptNestedEntityOptions(
                    "\nSelect the reference edge (a line, polyline segment or duct edge): ");
                options.AllowNone = true;
                PromptNestedEntityResult result = editor.GetNestedEntity(options);
                if (result.Status != PromptStatus.OK)
                    return null;

                Vec2 from;
                Vec2 to;
                string error;
                if (!ReadEdge(db, result, out from, out to, out error))
                {
                    editor.WriteMessage("\n" + error);
                    continue;
                }

                return new Reference { From = from, To = to, Duct = FindDuct(db, result) };
            }
        }

        private static bool ReadEdge(
            Database db, PromptNestedEntityResult result, out Vec2 from, out Vec2 to, out string error)
        {
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                bool ok = DuctAligner.TryGetEdge(
                    transaction, result.ObjectId, result.Transform, result.PickedPoint,
                    out from, out to, out error);
                transaction.Commit();
                return ok;
            }
        }

        // The duct reference that contains a picked entity, if any.
        private static DuctInfo FindDuct(Database db, PromptNestedEntityResult result)
        {
            foreach (ObjectId container in result.GetContainers())
            {
                DuctInfo info;
                if (DuctBlockLibrary.TryReadDuct(db, container, out info))
                    return info;
            }

            return null;
        }

        private static bool AlignOne(
            Editor editor,
            Database db,
            Reference reference,
            PromptNestedEntityResult pick,
            FittingSettings fittings)
        {
            DuctInfo duct = FindDuct(db, pick);
            if (duct == null)
            {
                editor.WriteMessage("\nThat edge is not inside a duct block (REC_ or Round D). Pick again.");
                return false;
            }

            Vec2 from;
            Vec2 to;
            string error;
            if (!ReadEdge(db, pick, out from, out to, out error))
            {
                editor.WriteMessage("\n" + error);
                return false;
            }

            AlignPlan plan = DuctAligner.Plan(
                reference.From, reference.To, reference.Duct, duct, from, to, fittings);
            if (plan.Error != null)
            {
                editor.WriteMessage("\n" + plan.Error + " Pick again.");
                return false;
            }

            bool join = false;
            if (plan.Join.Kind != JoinKind.None)
            {
                editor.WriteMessage("\nThe two ducts have facing ends: " + Describe(plan.Join));
                PromptKeywordOptions joinOptions = new PromptKeywordOptions(
                    "\nJoin? [Yes/No] <Yes>: ", "Yes No");
                joinOptions.AllowNone = true;
                joinOptions.Keywords.Default = "Yes";
                PromptResult answer = editor.GetKeywords(joinOptions);
                if (answer.Status == PromptStatus.Cancel)
                    return false;
                join = answer.Status == PromptStatus.None
                    || string.Equals(answer.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
            }
            else if (plan.Note != null)
            {
                editor.WriteMessage("\n" + plan.Note);
            }

            string message;
            bool ok = DuctAligner.Apply(db, plan, join, out message);
            editor.WriteMessage("\n" + message);
            return ok;
        }

        private static string Describe(JoinPlan join)
        {
            string length = join.NewLength.ToString("0.#", CultureInfo.InvariantCulture);
            if (join.Kind == JoinKind.Direct)
                return "the second duct will end up " + length + " mm long.";
            return "an eccentric transition " + join.TransitionLength.ToString("0.#", CultureInfo.InvariantCulture)
                + " mm long is added, and the second duct will end up " + length + " mm long.";
        }
    }
}
