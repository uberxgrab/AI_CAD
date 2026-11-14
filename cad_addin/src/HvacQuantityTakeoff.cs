using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using HvacAiCadAddin.Takeoff;

namespace HvacAiCadAddin.Commands
{
    /// <summary>
    /// Creates an Excel-compatible SpreadsheetML workbook from the pickfirst set or
    /// a new selection. Drawing units are assumed to be millimetres.
    /// </summary>
    public class HvacQuantityTakeoffCommand
    {
        private const int MaxReportedLayers = 10;

        [CommandMethod("HVACQTO", CommandFlags.UsePickSet)]
        public void CreateQuantityTakeoff()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                SelectionFilter filter = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "INSERT,LINE,LWPOLYLINE,POLYLINE")
                });
                selection = editor.GetSelection(filter);
                if (selection.Status != PromptStatus.OK)
                    return;
            }

            TakeoffOptions options;
            using (TakeoffSettingsForm form = new TakeoffSettingsForm())
            {
                if (Application.ShowModalDialog(form) != System.Windows.Forms.DialogResult.OK)
                    return;
                options = form.Options;
            }

            PromptSaveFileOptions saveOptions = new PromptSaveFileOptions(
                "\nSave HVAC quantity workbook")
            {
                Filter = "Excel XML Workbook (*.xml)|*.xml",
                DialogCaption = "Save HVAC quantity takeoff",
                InitialFileName = "HVAC_Quantity_Takeoff.xml"
            };
            PromptFileNameResult file = editor.GetFileNameForSave(saveOptions);
            if (file.Status != PromptStatus.OK)
                return;

            var blocks = new List<TakeoffBlockInput>();
            var curves = new List<TakeoffCurveInput>();

            Database database = document.Database;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null || selected.ObjectId.IsNull)
                        continue;

                    Entity entity = transaction.GetObject(selected.ObjectId, OpenMode.ForRead) as Entity;
                    if (entity == null || entity is Table)
                        continue;

                    BlockReference block = entity as BlockReference;
                    if (block != null)
                    {
                        blocks.Add(ReadBlock(block, transaction));
                        continue;
                    }

                    double length;
                    if (entity is Line)
                        length = ((Line)entity).Length;
                    else if (entity is Polyline)
                        length = ((Polyline)entity).Length;
                    else if (entity is Polyline2d || entity is Polyline3d)
                        length = GetCurveLength(entity);
                    else
                        continue;

                    curves.Add(new TakeoffCurveInput(entity.Layer, length));
                }
                transaction.Commit();
            }

            TakeoffResult result = TakeoffAggregator.Aggregate(blocks, curves, options);
            try
            {
                TakeoffWorkbookWriter.Write(file.StringResult, result);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCould not write workbook: {0}", exception.Message);
                return;
            }

            editor.WriteMessage(
                "\nHVAC takeoff saved: {0}\nEquipment rows: {1}; duct sizes: {2}; copper rows: {3}; uPVC rows: {4}; steel rows: {5}",
                file.StringResult, result.EquipmentRows.Count, result.DuctSizeRows.Count,
                result.CopperRows.Count, result.UpvcRows.Count, result.SteelRows.Count);
            editor.WriteMessage("\nCurves not exported: {0}", result.NotExportedCurveCount);
            if (result.NotExportedLayers.Count > 0)
            {
                editor.WriteMessage(" (layers: {0})",
                    string.Join(", ", result.NotExportedLayers.Take(MaxReportedLayers)));
            }
            editor.WriteMessage("\nHVAC_PUMP blocks excluded: {0}", result.ExcludedPumpBlockCount);
            editor.WriteMessage("\nDucts below the first band: {0}", result.DuctsBelowFirstBandCount);
            editor.WriteMessage("\nContingency factor: {0}; shaft rows added: {1}",
                TakeoffClassifier.FormatNumber(options.ContingencyFactor), options.ShaftPipes.Count);
        }

        private static TakeoffBlockInput ReadBlock(BlockReference block, Transaction transaction)
        {
            TakeoffBlockInput input = new TakeoffBlockInput(GetEffectiveBlockName(block, transaction));
            foreach (ObjectId id in block.AttributeCollection)
            {
                AttributeReference attribute = transaction.GetObject(id, OpenMode.ForRead) as AttributeReference;
                if (attribute != null)
                    input.AddAttribute(attribute.Tag, attribute.TextString);
            }
            if (!block.IsDynamicBlock)
                return input;

            foreach (DynamicBlockReferenceProperty property in block.DynamicBlockReferencePropertyCollection)
            {
                try
                {
                    input.AddDynamicProperty(property.PropertyName,
                        Convert.ToString(property.Value, CultureInfo.InvariantCulture));
                    if (string.Equals(property.PropertyName, "Distance3", StringComparison.OrdinalIgnoreCase))
                        input.Distance3 = Convert.ToDouble(property.Value, CultureInfo.InvariantCulture);
                }
                catch
                {
                    // A property value that AutoCAD cannot expose as text is omitted.
                }
            }
            return input;
        }

        private static string GetEffectiveBlockName(BlockReference block, Transaction transaction)
        {
            ObjectId nameId = block.IsDynamicBlock ? block.DynamicBlockTableRecord : block.BlockTableRecord;
            BlockTableRecord definition = transaction.GetObject(nameId, OpenMode.ForRead) as BlockTableRecord;
            return definition == null ? "(unknown block)" : definition.Name;
        }

        private static double GetCurveLength(Entity entity)
        {
            Curve curve = entity as Curve;
            if (curve == null)
                return 0;
            try
            {
                return curve.GetDistanceAtParameter(curve.EndParam) -
                    curve.GetDistanceAtParameter(curve.StartParam);
            }
            catch
            {
                return 0;
            }
        }
    }
}
