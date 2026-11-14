using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace HvacQtoCoreTests
{
    internal sealed class BorderInfo
    {
        public string LineStyle;
        public int Weight;

        public override string ToString()
        {
            return LineStyle + "/" + Weight.ToString(CultureInfo.InvariantCulture);
        }
    }

    internal sealed class StyleInfo
    {
        public string Id;
        public string FontName;
        public double? FontSize;
        public bool Bold;
        public string Fill;
        public string Horizontal;
        public string Vertical;
        public bool Wrap;
        public string Format;
        public readonly Dictionary<string, BorderInfo> Borders = new Dictionary<string, BorderInfo>();

        public string Border(string position)
        {
            BorderInfo border;
            return Borders.TryGetValue(position, out border) ? border.ToString() : "none";
        }
    }

    internal sealed class CellInfo
    {
        public int Column;
        public bool ExplicitIndex;
        public int MergeAcross;
        public int MergeDown;
        public string StyleId;
        public string DataType;
        public string Text;
        public string Formula;
        public StyleInfo Style;

        public bool HasData
        {
            get { return Text != null; }
        }
    }

    internal sealed class RowInfo
    {
        public int Index;
        public double? Height;
        public readonly Dictionary<int, CellInfo> Cells = new Dictionary<int, CellInfo>();
        public readonly List<CellInfo> DeclaredOrder = new List<CellInfo>();

        public CellInfo this[char column]
        {
            get
            {
                CellInfo cell;
                return Cells.TryGetValue(column - 'A' + 1, out cell) ? cell : null;
            }
        }

        public string Text(char column)
        {
            CellInfo cell = this[column];
            return cell == null ? null : cell.Text;
        }
    }

    internal sealed class SheetInfo
    {
        public string Name;
        public readonly List<double> ColumnWidths = new List<double>();
        public readonly SortedDictionary<int, RowInfo> Rows = new SortedDictionary<int, RowInfo>();

        public RowInfo this[int row]
        {
            get
            {
                RowInfo info;
                if (!Rows.TryGetValue(row, out info))
                    throw new KeyNotFoundException("sheet `" + Name + "` has no row " + row);
                return info;
            }
        }
    }

    /// <summary>Independent reader for the SpreadsheetML 2003 subset the workbook uses.</summary>
    internal sealed class WorkbookModel
    {
        public static readonly XNamespace Ss = "urn:schemas-microsoft-com:office:spreadsheet";

        public XDocument Document;
        public readonly Dictionary<string, StyleInfo> Styles = new Dictionary<string, StyleInfo>();
        public readonly List<SheetInfo> Sheets = new List<SheetInfo>();

        public SheetInfo Main
        {
            get { return Sheets[0]; }
        }

        public SheetInfo Appendix
        {
            get { return Sheets[1]; }
        }

        public static WorkbookModel Load(string path)
        {
            var model = new WorkbookModel { Document = XDocument.Load(path, LoadOptions.PreserveWhitespace) };
            model.ReadStyles();
            foreach (XElement worksheet in model.Document.Root.Elements(Ss + "Worksheet"))
                model.Sheets.Add(model.ReadSheet(worksheet));
            return model;
        }

        private void ReadStyles()
        {
            XElement styles = Document.Root.Element(Ss + "Styles");
            if (styles == null)
                return;

            foreach (XElement element in styles.Elements(Ss + "Style"))
            {
                var style = new StyleInfo { Id = (string)element.Attribute(Ss + "ID") };
                XElement font = element.Element(Ss + "Font");
                if (font != null)
                {
                    style.FontName = (string)font.Attribute(Ss + "FontName");
                    string size = (string)font.Attribute(Ss + "Size");
                    style.FontSize = size == null ? (double?)null : ParseDouble(size);
                    style.Bold = (string)font.Attribute(Ss + "Bold") == "1";
                }
                XElement interior = element.Element(Ss + "Interior");
                if (interior != null && (string)interior.Attribute(Ss + "Pattern") == "Solid")
                    style.Fill = ((string)interior.Attribute(Ss + "Color") ?? "").TrimStart('#').ToUpperInvariant();
                XElement alignment = element.Element(Ss + "Alignment");
                if (alignment != null)
                {
                    style.Horizontal = (string)alignment.Attribute(Ss + "Horizontal");
                    style.Vertical = (string)alignment.Attribute(Ss + "Vertical");
                    style.Wrap = (string)alignment.Attribute(Ss + "WrapText") == "1";
                }
                XElement format = element.Element(Ss + "NumberFormat");
                if (format != null)
                    style.Format = (string)format.Attribute(Ss + "Format");
                XElement borders = element.Element(Ss + "Borders");
                if (borders != null)
                {
                    foreach (XElement border in borders.Elements(Ss + "Border"))
                    {
                        string weight = (string)border.Attribute(Ss + "Weight");
                        style.Borders[(string)border.Attribute(Ss + "Position")] = new BorderInfo
                        {
                            LineStyle = (string)border.Attribute(Ss + "LineStyle"),
                            Weight = weight == null ? 0 : int.Parse(weight, CultureInfo.InvariantCulture)
                        };
                    }
                }
                Styles[style.Id] = style;
            }
        }

        private SheetInfo ReadSheet(XElement worksheet)
        {
            var sheet = new SheetInfo { Name = (string)worksheet.Attribute(Ss + "Name") };
            XElement table = worksheet.Element(Ss + "Table");
            if (table == null)
                return sheet;

            foreach (XElement column in table.Elements(Ss + "Column"))
            {
                string span = (string)column.Attribute(Ss + "Span");
                int repeat = 1 + (span == null ? 0 : int.Parse(span, CultureInfo.InvariantCulture));
                double width = ParseDouble((string)column.Attribute(Ss + "Width"));
                for (int index = 0; index < repeat; index++)
                    sheet.ColumnWidths.Add(width);
            }

            int rowIndex = 0;
            foreach (XElement element in table.Elements(Ss + "Row"))
            {
                string explicitRow = (string)element.Attribute(Ss + "Index");
                rowIndex = explicitRow == null ? rowIndex + 1 : int.Parse(explicitRow, CultureInfo.InvariantCulture);
                string height = (string)element.Attribute(Ss + "Height");
                var row = new RowInfo { Index = rowIndex, Height = height == null ? (double?)null : ParseDouble(height) };

                int nextColumn = 1;
                foreach (XElement cellElement in element.Elements(Ss + "Cell"))
                {
                    string explicitColumn = (string)cellElement.Attribute(Ss + "Index");
                    var cell = new CellInfo
                    {
                        Column = explicitColumn == null ? nextColumn : int.Parse(explicitColumn, CultureInfo.InvariantCulture),
                        ExplicitIndex = explicitColumn != null,
                        MergeAcross = ReadInt(cellElement, "MergeAcross"),
                        MergeDown = ReadInt(cellElement, "MergeDown"),
                        StyleId = (string)cellElement.Attribute(Ss + "StyleID"),
                        Formula = (string)cellElement.Attribute(Ss + "Formula")
                    };
                    XElement data = cellElement.Element(Ss + "Data");
                    if (data != null)
                    {
                        cell.DataType = (string)data.Attribute(Ss + "Type");
                        cell.Text = data.Value;
                    }
                    cell.Style = Resolve(cell.StyleId);
                    row.Cells[cell.Column] = cell;
                    row.DeclaredOrder.Add(cell);
                    nextColumn = cell.Column + cell.MergeAcross + 1;
                }
                sheet.Rows[rowIndex] = row;
            }
            return sheet;
        }

        private StyleInfo Resolve(string id)
        {
            StyleInfo style;
            if (id != null && Styles.TryGetValue(id, out style))
                return style;
            return Styles.TryGetValue("Default", out style) ? style : new StyleInfo { Id = "(missing)" };
        }

        private static int ReadInt(XElement element, string attribute)
        {
            string value = (string)element.Attribute(Ss + attribute);
            return value == null ? 0 : int.Parse(value, CultureInfo.InvariantCulture);
        }

        public static double ParseDouble(string value)
        {
            return double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
