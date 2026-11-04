using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace HvacAiCadAddin.Commands
{
    /// <summary>
    /// Creates an Excel-compatible SpreadsheetML workbook from objects selected
    /// by one crossing window. Drawing units are assumed to be millimetres.
    /// </summary>
    public class HvacQuantityTakeoffCommand
    {
        private sealed class EquipmentRow
        {
            public string Name;
            public string Attributes;
            public int Count;
        }

        private sealed class DuctRow
        {
            public string Name;
            public string Size;
            public double Length;
            public double Area;
            public int Count;
        }

        private sealed class PipeRow
        {
            public string Layer;
            public double Length;
            public int Count;
        }

        private static readonly Regex RectangularSize = new Regex(
            @"(?<!\d)(\d+(?:[.,]\d+)?)\s*[xX×*]\s*(\d+(?:[.,]\d+)?)(?!\d)",
            RegexOptions.Compiled);
        private static readonly Regex RoundSize = new Regex(
            @"(?<!\d)(?:Ø|D|DN)?\s*(\d+(?:[.,]\d+)?)(?!\d)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        [CommandMethod("HVACQTO")]
        public void CreateQuantityTakeoff()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            PromptPointResult first = editor.GetPoint("\nSpecify first corner of takeoff crossing window: ");
            if (first.Status != PromptStatus.OK)
                return;

            PromptCornerOptions cornerOptions = new PromptCornerOptions(
                "\nSpecify opposite corner: ", first.Value);
            PromptPointResult second = editor.GetCorner(cornerOptions);
            if (second.Status != PromptStatus.OK)
                return;

            Point3d lowerLeft = new Point3d(
                Math.Min(first.Value.X, second.Value.X),
                Math.Min(first.Value.Y, second.Value.Y),
                Math.Min(first.Value.Z, second.Value.Z));
            Point3d upperRight = new Point3d(
                Math.Max(first.Value.X, second.Value.X),
                Math.Max(first.Value.Y, second.Value.Y),
                Math.Max(first.Value.Z, second.Value.Z));

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

            SelectionFilter filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "INSERT,LINE,LWPOLYLINE,POLYLINE")
            });
            PromptSelectionResult selection = editor.SelectCrossingWindow(
                lowerLeft, upperRight, filter);
            if (selection.Status != PromptStatus.OK || selection.Value == null)
            {
                editor.WriteMessage("\nNo supported objects found in the selected area.");
                return;
            }

            var equipment = new Dictionary<string, EquipmentRow>(StringComparer.OrdinalIgnoreCase);
            var ducts = new Dictionary<string, DuctRow>(StringComparer.OrdinalIgnoreCase);
            var pipes = new Dictionary<string, PipeRow>(StringComparer.OrdinalIgnoreCase);
            int unclassifiedBlocks = 0;

            Database database = document.Database;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null || selected.ObjectId.IsNull)
                        continue;

                    Entity entity = transaction.GetObject(selected.ObjectId, OpenMode.ForRead) as Entity;
                    if (entity == null)
                        continue;

                    BlockReference block = entity as BlockReference;
                    if (block != null)
                    {
                        string blockName = GetEffectiveBlockName(block, transaction);
                        double ductLength;
                        string ductSize;
                        double ductArea;
                        if (TryReadDuct(block, blockName, transaction,
                            out ductLength, out ductSize, out ductArea))
                        {
                            string key = blockName + "|" + ductSize;
                            DuctRow row;
                            if (!ducts.TryGetValue(key, out row))
                            {
                                row = new DuctRow { Name = blockName, Size = ductSize };
                                ducts.Add(key, row);
                            }
                            row.Count++;
                            row.Length += ductLength;
                            row.Area += ductArea;
                        }
                        else
                        {
                            string attributes = ReadAttributes(block, transaction);
                            string key = blockName + "|" + attributes;
                            EquipmentRow row;
                            if (!equipment.TryGetValue(key, out row))
                            {
                                row = new EquipmentRow { Name = blockName, Attributes = attributes };
                                equipment.Add(key, row);
                            }
                            row.Count++;
                            if (string.IsNullOrEmpty(attributes))
                                unclassifiedBlocks++;
                        }
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

                    string layer = entity.Layer ?? "";
                    PipeRow pipe;
                    if (!pipes.TryGetValue(layer, out pipe))
                    {
                        pipe = new PipeRow { Layer = layer };
                        pipes.Add(layer, pipe);
                    }
                    pipe.Count++;
                    pipe.Length += length;
                }
                transaction.Commit();
            }

            try
            {
                WriteWorkbook(file.StringResult, equipment.Values, ducts.Values, pipes.Values);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCould not write workbook: {0}", exception.Message);
                return;
            }

            editor.WriteMessage(
                "\nHVAC takeoff saved: {0}\nEquipment groups: {1}; duct groups: {2}; pipe layers: {3}; unclassified blocks: {4}",
                file.StringResult, equipment.Count, ducts.Count, pipes.Count, unclassifiedBlocks);
        }

        private static string GetEffectiveBlockName(BlockReference block, Transaction transaction)
        {
            ObjectId nameId = block.IsDynamicBlock ? block.DynamicBlockTableRecord : block.BlockTableRecord;
            BlockTableRecord definition = transaction.GetObject(nameId, OpenMode.ForRead) as BlockTableRecord;
            return definition == null ? "(unknown block)" : definition.Name;
        }

        private static bool TryReadDuct(BlockReference block, string name,
            Transaction transaction, out double length, out string size, out double area)
        {
            length = 0;
            size = "";
            area = 0;
            if (!block.IsDynamicBlock)
                return false;

            bool foundLength = false;
            foreach (DynamicBlockReferenceProperty property in block.DynamicBlockReferencePropertyCollection)
            {
                if (!string.Equals(property.PropertyName, "Distance3", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    length = Convert.ToDouble(property.Value, CultureInfo.InvariantCulture);
                    foundLength = true;
                }
                catch
                {
                    return false;
                }
                break;
            }
            if (!foundLength || length <= 0)
                return false;

            Match rectangular = RectangularSize.Match(name);
            if (rectangular.Success)
            {
                double width = ParseNumber(rectangular.Groups[1].Value);
                double height = ParseNumber(rectangular.Groups[2].Value);
                if (width <= 0 || height <= 0)
                    return false;
                size = width.ToString("0.##", CultureInfo.InvariantCulture) + " x " +
                    height.ToString("0.##", CultureInfo.InvariantCulture) + " mm";
                area = (2 * (width + height) * length) / 1000000.0;
                return true;
            }

            Match round = RoundSize.Match(name);
            if (round.Success)
            {
                double diameter = ParseNumber(round.Groups[1].Value);
                if (diameter <= 0)
                    return false;
                size = "Ø" + diameter.ToString("0.##", CultureInfo.InvariantCulture) + " mm";
                area = (Math.PI * diameter * length) / 1000000.0;
                return true;
            }
            return false;
        }

        private static double ParseNumber(string value)
        {
            return double.Parse(value.Replace(',', '.'), CultureInfo.InvariantCulture);
        }

        private static string ReadAttributes(BlockReference block, Transaction transaction)
        {
            var values = new List<string>();
            foreach (ObjectId id in block.AttributeCollection)
            {
                AttributeReference attribute = transaction.GetObject(id, OpenMode.ForRead) as AttributeReference;
                if (attribute != null)
                    values.Add(attribute.Tag + "=" + attribute.TextString);
            }
            if (block.IsDynamicBlock)
            {
                foreach (DynamicBlockReferenceProperty property in block.DynamicBlockReferencePropertyCollection)
                {
                    try
                    {
                        values.Add("Dynamic:" + property.PropertyName + "=" +
                            Convert.ToString(property.Value, CultureInfo.InvariantCulture));
                    }
                    catch
                    {
                        // A property value that AutoCAD cannot expose as text is omitted.
                    }
                }
            }
            return string.Join("; ", values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
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

        private static void WriteWorkbook(string path, IEnumerable<EquipmentRow> equipment,
            IEnumerable<DuctRow> ducts, IEnumerable<PipeRow> pipes)
        {
            using (XmlWriter writer = XmlWriter.Create(path, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(true),
                Indent = true
            }))
            {
                writer.WriteStartDocument();
                writer.WriteProcessingInstruction("mso-application", "progid=\"Excel.Sheet\"");
                writer.WriteStartElement("Workbook", "urn:schemas-microsoft-com:office:spreadsheet");
                writer.WriteAttributeString("xmlns", "ss", null,
                    "urn:schemas-microsoft-com:office:spreadsheet");
                WriteWorksheet(writer, "Thiet bi", new[] { "Tên block", "Số lượng", "Thuộc tính (tag=giá trị)" },
                    equipment.OrderBy(row => row.Name).Select(row => new object[] { row.Name, row.Count, row.Attributes }));
                WriteWorksheet(writer, "Ong gio", new[] { "Tên block", "Kích thước", "Số lượng", "Chiều dài (m)", "Diện tích bề mặt (m2)" },
                    ducts.OrderBy(row => row.Name).ThenBy(row => row.Size).Select(row => new object[]
                    { row.Name, row.Size, row.Count, row.Length / 1000.0, row.Area }));
                WriteWorksheet(writer, "Ong nuoc lanh", new[] { "Layer / kích thước", "Số lượng đối tượng", "Chiều dài (m)" },
                    pipes.OrderBy(row => row.Layer).Select(row => new object[]
                    { row.Layer, row.Count, row.Length / 1000.0 }));
                WriteWorksheet(writer, "Ghi chu", new[] { "Quy ước", "Mô tả" }, new[]
                {
                    new object[] { "Phạm vi", "Đối tượng được chọn hoặc cắt qua cửa sổ Crossing." },
                    new object[] { "Đơn vị", "Giả định đơn vị bản vẽ là mm; chiều dài xuất m, diện tích xuất m2." },
                    new object[] { "Ống gió", "Tên block phải chứa 'duct'; kích thước nhận dạng dạng W x H hoặc D; chiều dài lấy từ thuộc tính dynamic Distance3." },
                    new object[] { "Diện tích ống gió", "Ống chữ nhật: 2 x (W + H) x L; ống tròn: pi x D x L. Chưa trừ hao hụt hoặc phụ kiện." },
                    new object[] { "Thiết bị", "Mỗi block không nhận dạng là ống gió được đếm; toàn bộ Attribute tag/value được xuất để giữ công suất, cột áp và dữ liệu sẵn có." },
                    new object[] { "Ống nước lạnh", "Line, Polyline, Polyline2d/3d được nhóm theo tên layer; tên layer được dùng làm kích thước." }
                });
                writer.WriteEndElement();
                writer.WriteEndDocument();
            }
        }

        private static void WriteWorksheet(XmlWriter writer, string name, string[] headers,
            IEnumerable<object[]> rows)
        {
            const string spreadsheet = "urn:schemas-microsoft-com:office:spreadsheet";
            writer.WriteStartElement("Worksheet", spreadsheet);
            writer.WriteAttributeString("ss", "Name", spreadsheet, name);
            writer.WriteStartElement("Table", spreadsheet);
            WriteRow(writer, headers.Cast<object>().ToArray());
            foreach (object[] row in rows)
                WriteRow(writer, row);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        private static void WriteRow(XmlWriter writer, object[] values)
        {
            const string spreadsheet = "urn:schemas-microsoft-com:office:spreadsheet";
            writer.WriteStartElement("Row", spreadsheet);
            foreach (object value in values)
            {
                bool numeric = value is int || value is double || value is decimal;
                writer.WriteStartElement("Cell", spreadsheet);
                writer.WriteStartElement("Data", spreadsheet);
                writer.WriteAttributeString("ss", "Type", spreadsheet, numeric ? "Number" : "String");
                writer.WriteString(numeric
                    ? Convert.ToString(value, CultureInfo.InvariantCulture)
                    : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
    }
}
