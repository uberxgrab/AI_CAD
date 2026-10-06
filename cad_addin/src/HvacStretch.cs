using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace HvacAiCadAddin.Commands
{
    public class HvacStretchCommand
    {
        [CommandMethod("HVACSTRETCH")]
        public void HvacStretch()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            PromptSelectionOptions selectionOptions = new PromptSelectionOptions();
            selectionOptions.MessageForAdding = "\nSelect objects: ";

            PromptSelectionResult selectionResult = ed.GetSelection(selectionOptions);
            if (selectionResult.Status != PromptStatus.OK)
                return;

            ObjectId[] dynamicBlockIds;
            bool onlyDynamicBlocks = TryGetDynamicBlockIds(
                db,
                selectionResult.Value,
                out dynamicBlockIds);

            if (!onlyDynamicBlocks)
            {
                StartNativeStretch(doc, ed);
                return;
            }

            PromptPointOptions ppo = new PromptPointOptions("\nSpecify base point: ");
            PromptPointResult baseResult = ed.GetPoint(ppo);
            if (baseResult.Status != PromptStatus.OK)
                return;

            Point3d basePoint = baseResult.Value;

            PromptPointOptions ppo2 = new PromptPointOptions("\nSpecify second point: ");
            ppo2.UseBasePoint = true;
            ppo2.BasePoint = basePoint;
            ppo2.UseDashedLine = true;

            PromptPointResult secondResult = ed.GetPoint(ppo2);
            if (secondResult.Status != PromptStatus.OK)
                return;

            Vector3d displacement = secondResult.Value - basePoint;
            int changedCount = 0;

            foreach (ObjectId blockId in dynamicBlockIds)
            {
                if (StretchDynamicBlock(db, blockId, displacement, ed))
                    changedCount++;
            }

            if (changedCount == 0)
            {
                ed.WriteMessage("\nCould not find a suitable Dynamic Block property.");
                return;
            }

            ed.WriteMessage("\nHVACSTRETCH: Updated {0} Dynamic Block(s).", changedCount);
            ed.WriteMessage(
                "\nDisplacement = X:{0:F3}, Y:{1:F3}, Z:{2:F3}",
                displacement.X,
                displacement.Y,
                displacement.Z);
        }

        private bool TryGetDynamicBlockIds(
            Database db,
            SelectionSet selectionSet,
            out ObjectId[] dynamicBlockIds)
        {
            ObjectId[] selectedIds = selectionSet.GetObjectIds();
            dynamicBlockIds = new ObjectId[selectedIds.Length];
            int dynamicBlockCount = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId objectId in selectedIds)
                {
                    BlockReference block = tr.GetObject(objectId, OpenMode.ForRead) as BlockReference;
                    if (block == null || !block.IsDynamicBlock)
                    {
                        dynamicBlockIds = new ObjectId[0];
                        return false;
                    }

                    dynamicBlockIds[dynamicBlockCount] = objectId;
                    dynamicBlockCount++;
                }

                tr.Commit();
            }

            return dynamicBlockCount > 0;
        }

        private void StartNativeStretch(Document doc, Editor ed)
        {
            ed.WriteMessage(
                "\nUsing native STRETCH. At the next prompt, use Crossing " +
                "selection to choose the objects to stretch.");

            doc.SendStringToExecute("_.STRETCH ", true, false, false);
        }

        private bool StretchDynamicBlock(
            Database db,
            ObjectId blockId,
            Vector3d displacement,
            Editor ed)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockReference block = tr.GetObject(blockId, OpenMode.ForWrite) as BlockReference;
                if (block == null || !block.IsDynamicBlock)
                    return false;

                DynamicBlockReferencePropertyCollection properties =
                    block.DynamicBlockReferencePropertyCollection;
                if (properties == null)
                    return false;

                foreach (DynamicBlockReferenceProperty property in properties)
                {
                    string name = property.PropertyName;
                    ed.WriteMessage("\nDynamic property: {0}", name);

                    if (property.ReadOnly || property.Value == null || !IsNumeric(property.Value))
                        continue;

                    bool isLengthProperty =
                        name.Equals("Distance3", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Length", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Width", StringComparison.OrdinalIgnoreCase);

                    if (!isLengthProperty)
                        continue;

                    double currentValue;
                    try
                    {
                        currentValue = Convert.ToDouble(property.Value);
                    }
                    catch
                    {
                        continue;
                    }

                    double newValue = currentValue + displacement.X;

                    try
                    {
                        property.Value = newValue;
                        tr.Commit();
                        ed.WriteMessage(
                            "\nProperty '{0}': {1:F2} -> {2:F2}",
                            name,
                            currentValue,
                            newValue);
                        return true;
                    }
                    catch (System.Exception ex)
                    {
                        ed.WriteMessage(
                            "\nFailed to set property '{0}': {1}",
                            name,
                            ex.Message);
                        return false;
                    }
                }

                return false;
            }
        }

        private bool IsNumeric(object value)
        {
            return value is byte
                || value is short
                || value is int
                || value is long
                || value is float
                || value is double
                || value is decimal;
        }
    }
}
