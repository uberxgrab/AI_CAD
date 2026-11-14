using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using HvacAiCadAddin.Takeoff;

namespace HvacQtoCoreTests
{
    /// <summary>
    /// Specification section 5. The workbook is written to a temp file and parsed back.
    /// Header strings and style values were read from vrv.xlsx sheet 1, columns A to O.
    /// </summary>
    internal static class WorkbookTests
    {
        private const string SheetMain = "DIEU HOA THONG GIO";
        private const string SheetAppendix = "PHU LUC ONG GIO";
        private const string TimesNewRoman = "Times New Roman";
        private const string QuantityFormat = "_(* #,##0.00_);_(* \\(#,##0.00\\);_(* \"-\"??_);_(@_)";
        private const string Columns = "ABCDEFGHIJKLMNO";
        private const string Hair = "Continuous/0";
        private const string Thin = "Continuous/1";
        private const string Double = "Double/3";

        private static readonly double[] TemplateWidths =
        {
            8.7, 41.3, 38.0, 29.7, 12.1, 15.1, 18.3, 17.0, 20.3, 20.3, 20.3, 18.0, 16.7, 19.7, 17.9
        };

        private static readonly Dictionary<char, string> HeaderRow7 = new Dictionary<char, string>
        {
            { 'A', "STT\n序号" },
            { 'B', "NỘI DUNG CÔNG VIỆC" },
            { 'C', "工作内容" },
            { 'D', "MÃ HIỆU \nSẢN PHẨM\n产品型号" },
            { 'E', "ĐƠN VỊ\n单位" },
            { 'F', "KHỐI LƯỢNG\n数量" },
            { 'G', "ĐƠN GIÁ\n单价 (VND)" },
            { 'I', "TỔNG PHỤ\n小计 (VND)" },
            { 'K', "TỔNG CỘNG\n合计 (VND)" },
            { 'M', "ĐƠN GIÁ\n单价 (USD)" },
            { 'N', "THÀNH TIỀN\n金额 (USD)" },
            { 'O', "GHI CHÚ\n备注" },
        };

        private static readonly Dictionary<char, string> HeaderRow8 = new Dictionary<char, string>
        {
            { 'G', "VẬT LIỆU\n材料" },
            { 'H', "NHÂN CÔNG\n人工" },
            { 'I', "VẬT LIỆU\n材料" },
            { 'J', "NHÂN CÔNG\n人工" },
            { 'K', "ĐƠN GIÁ\n单价" },
            { 'L', "THÀNH TIỀN\n金额" },
        };

        private sealed class ExpectedRow
        {
            public string A;
            public string B;
            public string E;
            public double? F;
            public bool IsData;

            public static ExpectedRow Heading(string a, string b)
            {
                return new ExpectedRow { A = a, B = b };
            }

            public static ExpectedRow Data(int number, string b, string e, double f)
            {
                return new ExpectedRow
                {
                    A = number.ToString(CultureInfo.InvariantCulture), B = b, E = e, F = f, IsData = true
                };
            }
        }

        public static void Run(string directory)
        {
            WorkbookModel full = null;
            WorkbookModel empty = null;
            Check.Case("s5 workbook loads", () =>
            {
                full = Save(Fixtures.Full(), directory, "full.xml");
                empty = Save(Fixtures.Empty(), directory, "empty.xml");
            });
            if (full == null || empty == null)
                return;

            Check.Case("s5 structure", () => Structure(full, Path.Combine(directory, "full.xml")));
            Check.Case("s5 column widths", () => ColumnWidths(full));
            Check.Case("s5 fixed rows", () => FixedRows(full));
            Check.Case("s5 header", () => Header(full));
            Check.Case("s5 row 9", () => SectionRow(full));
            Check.Case("s5 full row sequence", () => FullRowSequence(full));
            Check.Case("s5 row styles", () => RowStyles(full));
            Check.Case("s5 font everywhere", () => FontEverywhere(full));
            Check.Case("s5 empty input", () => EmptyInput(empty));
            Check.Case("s5 subgroup omission", () => SubgroupOmission(directory));
            Check.Case("s5 custom gauge text", () => CustomGauge(directory));
            Check.Case("s5 multi-line and heights", () => RowHeights(directory));
            Check.Case("s5 line breaks", () => LineBreaks(full, Path.Combine(directory, "full.xml")));
            Check.Case("s5 appendix", () => Appendix(full));
            Check.Case("s5 file and stream writers", () => Writers(directory));
            Check.Case("s5 XML escaping", () => Escaping(directory));
        }

        private static WorkbookModel Save(TakeoffResult result, string directory, string fileName)
        {
            string path = Path.Combine(directory, fileName);
            TakeoffWorkbookWriter.Write(path, result);
            return WorkbookModel.Load(path);
        }

        private static void Structure(WorkbookModel workbook, string path)
        {
            XElement root = workbook.Document.Root;
            Check.Equal("s5 root element", "Workbook", root.Name.LocalName);
            Check.Equal("s5 SpreadsheetML namespace", WorkbookModel.Ss.NamespaceName, root.Name.NamespaceName);
            Check.True("s5 mso-application instruction",
                workbook.Document.Nodes().OfType<XProcessingInstruction>().Any(node =>
                    node.Target == "mso-application" && node.Data.Contains("Excel.Sheet")));

            Check.Sequence("s5 exact sheet names", new[] { SheetMain, SheetAppendix }, workbook.Sheets.Select(sheet => sheet.Name));

            string raw = File.ReadAllText(path, Encoding.UTF8);
            Check.False("s5 no old notes sheet", raw.IndexOf("Ghi chu", StringComparison.OrdinalIgnoreCase) >= 0);

            List<string> order = root.Elements().Select(element => element.Name.LocalName).ToList();
            int styles = order.IndexOf("Styles");
            int firstSheet = order.IndexOf("Worksheet");
            Check.True("s5 Styles present", styles >= 0);
            Check.True("s5 Styles before Worksheets", styles >= 0 && styles < firstSheet,
                string.Join(",", order));

            foreach (SheetInfo sheet in workbook.Sheets)
            {
                foreach (RowInfo row in sheet.Rows.Values)
                {
                    foreach (CellInfo cell in row.DeclaredOrder)
                    {
                        Check.True("s5 style exists " + sheet.Name + " r" + row.Index + "c" + cell.Column,
                            cell.StyleId == null || workbook.Styles.ContainsKey(cell.StyleId), cell.StyleId);
                    }
                }
            }
        }

        private static void ColumnWidths(WorkbookModel workbook)
        {
            SheetInfo sheet = workbook.Main;
            Check.Equal("s5 15 columns defined, A to O only", 15, sheet.ColumnWidths.Count);
            for (int index = 0; index < TemplateWidths.Length && index < sheet.ColumnWidths.Count; index++)
            {
                // Spec widths have one decimal, so allow that rounding after the 5.25 factor.
                Check.Near("s5 width " + Columns[index], TemplateWidths[index] * 5.25, sheet.ColumnWidths[index], 0.27);
            }
        }

        private static void FixedRows(WorkbookModel workbook)
        {
            SheetInfo sheet = workbook.Main;
            double[] heights = { 43.5, 25.5, 25.5, 24.95, 24.95, 21, 33, 30, 37.5 };
            for (int index = 0; index < heights.Length; index++)
                Check.Near("s5 height row " + (index + 1), heights[index], sheet[index + 1].Height ?? -1, 1e-9);

            RowInfo title = sheet[1];
            Check.Equal("s5 title text", "BẢNG BÁO GIÁ CHI TIẾT\n报价明细表", title.Text('A'));
            Check.Equal("s5 title merged A:O", 14, title['A'].MergeAcross);
            Check.True("s5 title bold", title['A'].Style.Bold);
            Check.Equal("s5 title size", (double?)14, title['A'].Style.FontSize);
            Check.Equal("s5 title centered", "Center", title['A'].Style.Horizontal);
            Check.True("s5 title wraps", title['A'].Style.Wrap);

            string[] labels = { "Dự án:", "项目:", "Hạng mục:", "项目:" };
            for (int index = 0; index < labels.Length; index++)
            {
                RowInfo row = sheet[index + 2];
                Check.Equal("s5 label row " + row.Index, labels[index], row.Text('A'));
                Check.True("s5 label bold row " + row.Index, row['A'].Style.Bold);
                Check.Equal("s5 label size row " + row.Index, (double?)12, row['A'].Style.FontSize);
                Check.Equal("s5 label row has one value " + row.Index, 1, row.Cells.Values.Count(cell => cell.HasData));
            }

            // Row 6 holds only the contingency factor: label in D:E, value in F.
            RowInfo factor = sheet[6];
            Check.Equal("s5 row 6 two values", 2, factor.Cells.Values.Count(cell => cell.HasData));
            Check.Equal("s5 factor label", "Hệ số dự phòng:", factor.Text('D'));
            Check.Equal("s5 factor label merged D:E", 1, factor['D'].MergeAcross);
            Check.Equal("s5 factor default value", "1", factor.Text('F'));
            Check.Equal("s5 factor value is a number", "Number", factor['F'].DataType);
            Check.Equal("s5 factor fill", "FFF2CC", factor['F'].Style.Fill);
        }

        private static void Header(WorkbookModel workbook)
        {
            RowInfo top = workbook.Main[7];
            RowInfo bottom = workbook.Main[8];

            foreach (KeyValuePair<char, string> item in HeaderRow7)
                Check.Equal("s5 header " + item.Key + "7", item.Value, top.Text(item.Key));
            foreach (KeyValuePair<char, string> item in HeaderRow8)
                Check.Equal("s5 header " + item.Key + "8", item.Value, bottom.Text(item.Key));

            foreach (char column in "ABCDEFMNO")
                Check.Equal("s5 merged down " + column, 1, top[column].MergeDown);
            foreach (char column in "GIK")
                Check.Equal("s5 merged across " + column, 1, top[column].MergeAcross);

            // Cells after a gap carry ss:Index, and covered cells are not declared.
            foreach (char column in "IKM")
            {
                Check.True("s5 ss:Index on " + column + "7", top[column].ExplicitIndex);
            }
            Check.Sequence("s5 row 7 declared columns", "ABCDEFGIKMNO".Select(column => column - 'A' + 1),
                top.DeclaredOrder.Select(cell => cell.Column));
            Check.Sequence("s5 row 8 declared columns", "GHIJKL".Select(column => column - 'A' + 1),
                bottom.DeclaredOrder.Select(cell => cell.Column));
            Check.True("s5 ss:Index on G8", bottom['G'].ExplicitIndex);

            // M7 and N7 use the template's theme 3 tint 0.9, not the other header colour.
            foreach (char column in "MN")
                Check.Equal("s5 header fill " + column + "7", "E2ECF8", top[column].Style.Fill);

            foreach (char column in "ABCDEFGIKO")
            {
                StyleInfo style = top[column].Style;
                Check.Equal("s5 header fill " + column + "7", "DAF2D0", style.Fill);
                Check.True("s5 header bold " + column + "7", style.Bold);
                Check.Equal("s5 header size " + column + "7", (double?)12, style.FontSize);
                Check.Equal("s5 header centered " + column + "7", "Center", style.Horizontal);
                Check.Equal("s5 header vertical " + column + "7", "Center", style.Vertical);
                Check.True("s5 header wraps " + column + "7", style.Wrap);
                Check.Equal("s5 header top " + column + "7", Double, style.Border("Top"));
                Check.Equal("s5 header bottom " + column + "7", Thin, style.Border("Bottom"));
            }
            Check.Equal("s5 header left A7", Double, top['A'].Style.Border("Left"));
            Check.Equal("s5 header right A7", Thin, top['A'].Style.Border("Right"));
            Check.Equal("s5 header right O7", Double, top['O'].Style.Border("Right"));
            Check.Equal("s5 header left O7", Thin, top['O'].Style.Border("Left"));
            Check.Equal("s5 header left B7", Thin, top['B'].Style.Border("Left"));

            foreach (char column in "GHIJKL")
            {
                StyleInfo style = bottom[column].Style;
                Check.Equal("s5 header fill " + column + "8", "DAF2D0", style.Fill);
                Check.True("s5 header bold " + column + "8", style.Bold);
                foreach (string position in new[] { "Left", "Right", "Top", "Bottom" })
                    Check.Equal("s5 header " + position + " " + column + "8", Thin, style.Border(position));
            }
        }

        private static void SectionRow(WorkbookModel workbook)
        {
            RowInfo row = workbook.Main[9];
            Check.Equal("s5 row 9 A", "I", row.Text('A'));
            Check.Equal("s5 row 9 B", "HỆ THỐNG ĐIỀU HÒA THÔNG GIÓ", row.Text('B'));
            foreach (char column in Columns)
            {
                Check.True("s5 row 9 cell present " + column, row[column] != null);
                Check.Equal("s5 row 9 fill " + column, "F2DCDB", row[column].Style.Fill);
            }
            Check.True("s5 row 9 A bold", row['A'].Style.Bold);
            Check.True("s5 row 9 B bold", row['B'].Style.Bold);
            Check.Equal("s5 row 9 size", (double?)12, row['B'].Style.FontSize);
            Check.Equal("s5 row 9 only two values", 2, row.Cells.Values.Count(cell => cell.HasData));
            Check.Equal("s5 row 9 left", Double, row['A'].Style.Border("Left"));
            Check.Equal("s5 row 9 right", Double, row['O'].Style.Border("Right"));
        }

        private static List<ExpectedRow> ExpectedFullRows()
        {
            const string pump = "Bơm tuần hoàn nước lạnh:\n- Lưu lượng nước: 10050 (LPM)\n- Cột áp: 40 (mH2O)";
            double roundArea = Math.PI * 250 * 1500 / 1000000.0;
            return new List<ExpectedRow>
            {
                ExpectedRow.Heading("I.1", "Thiết bị"),
                ExpectedRow.Data(1, "Dàn nóng ODU 6 HP", "bộ", 2),
                ExpectedRow.Data(2, "Dàn nóng ODU 10 HP", "bộ", 1),
                ExpectedRow.Data(3, "Dàn lạnh VRV Cassette âm trần Inverter 16 kW", "bộ", 3),
                ExpectedRow.Data(4, "Chiller giải nhiệt nước:\n- Công suất lạnh: 420 RT", "bộ", 1),
                ExpectedRow.Data(5, pump, "bộ", 2),
                ExpectedRow.Data(6, "AHU-01\nTAG=A1", "cái", 1),
                ExpectedRow.Data(7, "FCU-2.2 kW", "cái", 1),
                ExpectedRow.Heading("I.2", "Ống gió"),
                ExpectedRow.Data(8, "Ống gió tôn mạ kẽm 0.58mm", "m2", roundArea + 0.26),
                ExpectedRow.Data(9, "Ống gió tôn mạ kẽm 0.75mm", "m2", 12.6),
                ExpectedRow.Data(10, "Ống gió tôn mạ kẽm 0.95mm", "m2", 0),
                ExpectedRow.Data(11, "Ống gió tôn mạ kẽm 1.15mm", "m2", 5.6),
                ExpectedRow.Heading("I.3", "Ống nước"),
                ExpectedRow.Heading("*", "Ống đồng"),
                ExpectedRow.Data(12, "Ống đồng D9.5 dày 0.8mm, kèm bảo ôn 19mm", "m", 12),
                ExpectedRow.Data(13, "Ống đồng D15.9 dày 1.0mm, kèm bảo ôn 19mm", "m", 12),
                ExpectedRow.Heading("*", "Ống thoát nước ngưng uPVC"),
                ExpectedRow.Data(14, "Ống thoát nước ngưng uPVC Class 1 D27, kèm bảo ôn 13mm", "m", 5),
                ExpectedRow.Data(15, "Ống thoát nước ngưng uPVC Class 1, kèm bảo ôn 13mm (layer: EIA_1 Pipe condensated)", "m", 1),
                ExpectedRow.Heading("*", "Ống thép hàn mạ kẽm"),
                ExpectedRow.Data(16, "Ống thép hàn mạ kẽm DN15, SCH40", "m", 1),
                ExpectedRow.Data(17, "Ống thép hàn mạ kẽm DN100, SCH40", "m", 12),
                ExpectedRow.Data(18, "Ống thép hàn mạ kẽm, SCH40 (layer: HWS MAIN)", "m", 2.5),
                ExpectedRow.Heading(null, "TỔNG CỘNG (CHƯA VAT)"),
            };
        }

        private static void CompareRows(string prefix, SheetInfo sheet, List<ExpectedRow> expected)
        {
            int rowNumber = 10;
            foreach (ExpectedRow item in expected)
            {
                RowInfo row;
                if (!sheet.Rows.TryGetValue(rowNumber, out row))
                {
                    Check.Fail(prefix + " row " + rowNumber, Check.Show(item.B), "(row missing)");
                    rowNumber++;
                    continue;
                }

                string name = prefix + " row " + rowNumber + " ";
                Check.Equal(name + "A", item.A, row.Text('A'));
                Check.Equal(name + "B", item.B, row.Text('B'));
                Check.Equal(name + "E", item.E, row.Text('E'));
                if (item.F.HasValue)
                {
                    string text = row.Text('F');
                    Check.True(name + "F has a value", text != null, "(empty)");
                    Check.Equal(name + "F type", "Number", row['F'] == null ? null : row['F'].DataType);
                    if (text != null)
                        Check.Near(name + "F value", item.F.Value, WorkbookModel.ParseDouble(text), 1e-6);
                }
                else
                {
                    Check.Equal(name + "F empty", null, row.Text('F'));
                }

                foreach (char column in Columns)
                {
                    Check.True(name + "cell present " + column, row[column] != null);
                    if ("CDGHIJKLMNO".IndexOf(column) >= 0)
                        Check.Equal(name + "empty " + column, null, row.Text(column));
                }
                rowNumber++;
            }

            int lastRow = sheet.Rows.Keys.Max();
            Check.Equal(prefix + " no rows after closing row", rowNumber - 1, lastRow);
        }

        private static void FullRowSequence(WorkbookModel workbook)
        {
            CompareRows("s5 full", workbook.Main, ExpectedFullRows());
        }

        private static void RowStyles(WorkbookModel workbook)
        {
            SheetInfo sheet = workbook.Main;
            List<ExpectedRow> expected = ExpectedFullRows();
            for (int index = 0; index < expected.Count; index++)
            {
                RowInfo row = sheet[10 + index];
                ExpectedRow item = expected[index];
                string name = "s5 style row " + row.Index + " ";

                if (item.A != null && item.A.StartsWith("I.", StringComparison.Ordinal))
                {
                    Check.Near(name + "group height", 29.25, row.Height ?? -1, 1e-9);
                    foreach (char column in Columns)
                        Check.Equal(name + "group fill " + column, "DCE6F1", row[column].Style.Fill);
                    Check.True(name + "group A bold", row['A'].Style.Bold);
                    Check.True(name + "group B bold", row['B'].Style.Bold);
                    Check.Equal(name + "group size", (double?)12, row['B'].Style.FontSize);
                    OuterEdges(name, row);
                }
                else if (item.A == "*")
                {
                    Check.Near(name + "subgroup height", 29.25, row.Height ?? -1, 1e-9);
                    foreach (char column in Columns)
                        Check.Equal(name + "subgroup no fill " + column, null, row[column].Style.Fill);
                    Check.True(name + "subgroup A bold", row['A'].Style.Bold);
                    Check.True(name + "subgroup B bold", row['B'].Style.Bold);
                    // Template row 51: only A, B and C are bold.
                    Check.Sequence(name + "subgroup bold columns", "ABC", BoldColumns(row));
                    Check.Equal(name + "subgroup A centered", "Center", row['A'].Style.Horizontal);
                    OuterEdges(name, row);
                    DataBorders(name, row);
                }
                else if (item.IsData)
                {
                    foreach (char column in Columns)
                    {
                        Check.Equal(name + "data no fill " + column, null, row[column].Style.Fill);
                        Check.False(name + "data not bold " + column, row[column].Style.Bold);
                        Check.Equal(name + "data vertical " + column, "Center", row[column].Style.Vertical);
                    }
                    Check.Equal(name + "quantity format", QuantityFormat, row['F'].Style.Format);
                    Check.Equal(name + "quantity centered", "Center", row['F'].Style.Horizontal);
                    Check.Equal(name + "number centered", "Center", row['A'].Style.Horizontal);
                    Check.Equal(name + "unit centered", "Center", row['E'].Style.Horizontal);
                    Check.True(name + "description wraps", row['B'].Style.Wrap);
                    Check.True(name + "height at least minimum", (row.Height ?? -1) >= 29.25, Check.Show(row.Height));
                    OuterEdges(name, row);
                    DataBorders(name, row);
                }
                else
                {
                    Check.Near(name + "closing height", 34.5, row.Height ?? -1, 1e-9);
                    foreach (char column in Columns)
                    {
                        // A109 is theme 5 with tint 0.6, not a literal RGB. Real Excel resolves it to
                        // E6B8B7; the same method gives the DCE6F1 and F2DCDB the spec states.
                        Check.Equal(name + "closing fill " + column, "E6B8B7", row[column].Style.Fill);
                        Check.Equal(name + "closing bottom " + column, Double, row[column].Style.Border("Bottom"));
                        // Template row 108 has a thin bottom edge. The closing row's top draws it.
                        Check.Equal(name + "closing top " + column, "Continuous/1", row[column].Style.Border("Top"));
                    }
                    // Template row 109: every cell is bold except D, E and F.
                    Check.Sequence(name + "closing bold columns", "ABCGHIJKLMNO", BoldColumns(row));
                    Check.Equal(name + "closing left", Double, row['A'].Style.Border("Left"));
                    Check.Equal(name + "closing right", Double, row['O'].Style.Border("Right"));
                    Check.Equal(name + "closing has one value", 1, row.Cells.Values.Count(cell => cell.HasData));
                }
            }
        }

        private static IEnumerable<char> BoldColumns(RowInfo row)
        {
            return Columns.Where(column => row[column].Style.Bold);
        }

        private static void OuterEdges(string name, RowInfo row)
        {
            Check.Equal(name + "A left double", Double, row['A'].Style.Border("Left"));
            Check.Equal(name + "A right thin", Thin, row['A'].Style.Border("Right"));
            Check.Equal(name + "O left thin", Thin, row['O'].Style.Border("Left"));
            Check.Equal(name + "O right double", Double, row['O'].Style.Border("Right"));
        }

        private static void DataBorders(string name, RowInfo row)
        {
            foreach (char column in "BCDEFGHIJKLMN")
            {
                Check.Equal(name + column + " left thin", Thin, row[column].Style.Border("Left"));
                Check.Equal(name + column + " right thin", Thin, row[column].Style.Border("Right"));
            }
            foreach (char column in Columns)
            {
                Check.Equal(name + column + " top hair", Hair, row[column].Style.Border("Top"));
                Check.Equal(name + column + " bottom hair", Hair, row[column].Style.Border("Bottom"));
            }
        }

        private static void FontEverywhere(WorkbookModel workbook)
        {
            foreach (StyleInfo style in workbook.Styles.Values)
            {
                Check.Equal("s5 style font " + style.Id, TimesNewRoman, style.FontName);
            }

            foreach (SheetInfo sheet in workbook.Sheets)
            {
                foreach (RowInfo row in sheet.Rows.Values)
                {
                    foreach (CellInfo cell in row.DeclaredOrder)
                    {
                        string name = "s5 font " + sheet.Name + " r" + row.Index + "c" + cell.Column;
                        Check.Equal(name, TimesNewRoman, cell.Style.FontName);
                        bool isTitle = sheet == workbook.Main && row.Index == 1;
                        Check.Equal(name + " size", (double?)(isTitle ? 14 : 12), cell.Style.FontSize);
                    }
                }
            }
        }

        private static void EmptyInput(WorkbookModel workbook)
        {
            Check.Sequence("s5 empty sheet names", new[] { SheetMain, SheetAppendix }, workbook.Sheets.Select(sheet => sheet.Name));
            CompareRows("s5 empty", workbook.Main, new List<ExpectedRow>
            {
                ExpectedRow.Heading("I.1", "Thiết bị"),
                ExpectedRow.Heading("I.2", "Ống gió"),
                ExpectedRow.Data(1, "Ống gió tôn mạ kẽm 0.58mm", "m2", 0),
                ExpectedRow.Data(2, "Ống gió tôn mạ kẽm 0.75mm", "m2", 0),
                ExpectedRow.Data(3, "Ống gió tôn mạ kẽm 0.95mm", "m2", 0),
                ExpectedRow.Data(4, "Ống gió tôn mạ kẽm 1.15mm", "m2", 0),
                ExpectedRow.Heading("I.3", "Ống nước"),
                ExpectedRow.Heading(null, "TỔNG CỘNG (CHƯA VAT)"),
            });

            Check.Equal("s5 empty appendix header only", 1,
                workbook.Appendix.Rows.Values.Count(row => row.Cells.Values.Any(cell => cell.HasData)));
        }

        private static void SubgroupOmission(string directory)
        {
            WorkbookModel workbook = Save(Fixtures.SteelOnly(), directory, "steel.xml");
            CompareRows("s5 steel only", workbook.Main, new List<ExpectedRow>
            {
                ExpectedRow.Heading("I.1", "Thiết bị"),
                ExpectedRow.Heading("I.2", "Ống gió"),
                ExpectedRow.Data(1, "Ống gió tôn mạ kẽm 0.58mm", "m2", 0),
                ExpectedRow.Data(2, "Ống gió tôn mạ kẽm 0.75mm", "m2", 0),
                ExpectedRow.Data(3, "Ống gió tôn mạ kẽm 0.95mm", "m2", 0),
                ExpectedRow.Data(4, "Ống gió tôn mạ kẽm 1.15mm", "m2", 0),
                ExpectedRow.Heading("I.3", "Ống nước"),
                ExpectedRow.Heading("*", "Ống thép hàn mạ kẽm"),
                ExpectedRow.Data(5, "Ống thép hàn mạ kẽm DN100, SCH40", "m", 12),
                ExpectedRow.Heading(null, "TỔNG CỘNG (CHƯA VAT)"),
            });
        }

        private static void CustomGauge(string directory)
        {
            WorkbookModel workbook = Save(Fixtures.CustomGauge(), directory, "custom.xml");
            CompareRows("s5 custom", workbook.Main, new List<ExpectedRow>
            {
                ExpectedRow.Heading("I.1", "Thiết bị"),
                ExpectedRow.Heading("I.2", "Ống gió"),
                ExpectedRow.Data(1, "Ống gió tôn mạ kẽm 0.5mm", "m2", 0),
                ExpectedRow.Data(2, "Ống gió tôn mạ kẽm 0.6mm", "m2", 1.1),
                ExpectedRow.Data(3, "Ống gió tôn mạ kẽm 0.8mm", "m2", 0),
                ExpectedRow.Data(4, "Ống gió tôn mạ kẽm 1.2mm", "m2", 0),
                ExpectedRow.Heading("I.3", "Ống nước"),
                ExpectedRow.Heading(null, "TỔNG CỘNG (CHƯA VAT)"),
            });
        }

        private static void RowHeights(string directory)
        {
            string short3 = "A1\nline two\nline three";
            string short4 = "A2\nline two\nline three\nline four";
            string short2 = "A3\nline two";
            string one = "A4 short";
            string forty = "A9" + new string('x', 38);
            string forty5 = "A5" + new string('x', 43);
            string ninety = "A6" + new string('x', 88);
            string oneEighty = "A7" + new string('x', 178);
            string mixed = "A8" + new string('x', 88) + "\nline two";

            WorkbookModel workbook = Save(
                Fixtures.RowHeights(new[] { short3, short4, short2, one, forty, forty5, ninety, oneEighty, mixed }),
                directory, "heights.xml");

            Height(workbook, short3, 47.25);
            Height(workbook, short4, 63);
            Height(workbook, short2, 31.5);
            Height(workbook, one, 29.25);
            Height(workbook, forty, 29.25);
            Height(workbook, forty5, 31.5);
            Height(workbook, ninety, 47.25);
            Height(workbook, oneEighty, 78.75);
            Height(workbook, mixed, 63);
        }

        private static void Height(WorkbookModel workbook, string text, double expected)
        {
            RowInfo row = workbook.Main.Rows.Values.FirstOrDefault(candidate => candidate.Text('B') == text);
            string name = "s5 row height for `" + (text.Length > 12 ? text.Substring(0, 12) + "..." : text) +
                "` (" + text.Length.ToString(CultureInfo.InvariantCulture) + " chars)";
            Check.True(name + " found", row != null);
            if (row != null)
                Check.Near(name, expected, row.Height ?? -1, 1e-9);
        }

        private static void LineBreaks(WorkbookModel workbook, string path)
        {
            RowInfo chiller = workbook.Main.Rows.Values.First(row => (row.Text('B') ?? "").StartsWith("Chiller", StringComparison.Ordinal));
            Check.Equal("s5 LF kept in description", "Chiller giải nhiệt nước:\n- Công suất lạnh: 420 RT", chiller.Text('B'));
            Check.Equal("s5 LF kept in title", "BẢNG BÁO GIÁ CHI TIẾT\n报价明细表", workbook.Main[1].Text('A'));

            string raw = File.ReadAllText(path, Encoding.UTF8);
            Check.False("s5 no encoded CR", raw.Contains("&#13;") || raw.IndexOf("&#xD;", StringComparison.OrdinalIgnoreCase) >= 0);
            int start = raw.IndexOf("Chiller gi", StringComparison.Ordinal);
            int end = raw.IndexOf("</", start, StringComparison.Ordinal);
            string data = raw.Substring(start, end - start);
            Check.False("s5 no raw CR in cell text", data.Contains("\r"), Check.Show(data));
        }

        private static void Appendix(WorkbookModel workbook)
        {
            SheetInfo sheet = workbook.Appendix;
            List<RowInfo> rows = sheet.Rows.Values.Where(row => row.Cells.Values.Any(cell => cell.HasData)).ToList();
            RowInfo header = rows[0];
            Check.Sequence("s5 appendix columns exact",
                new[] { "Kích thước", "Số lượng", "Tổng chiều dài (m)", "Diện tích (m2)" },
                header.DeclaredOrder.Select(cell => cell.Text));
            Check.Sequence("s5 appendix header in A to D", new[] { 1, 2, 3, 4 }, header.DeclaredOrder.Select(cell => cell.Column));

            foreach (CellInfo cell in header.DeclaredOrder)
            {
                Check.Equal("s5 appendix header fill c" + cell.Column, "DAF2D0", cell.Style.Fill);
                Check.Equal("s5 appendix header font c" + cell.Column, TimesNewRoman, cell.Style.FontName);
            }

            foreach (RowInfo row in rows)
            {
                Check.Equal("s5 appendix four cells row " + row.Index, 4, row.Cells.Count);
                foreach (CellInfo cell in row.DeclaredOrder)
                {
                    foreach (string position in new[] { "Left", "Right", "Top", "Bottom" })
                    {
                        Check.Equal("s5 appendix " + position + " r" + row.Index + "c" + cell.Column, Thin,
                            cell.Style.Border(position));
                    }
                }
            }

            string headerText = string.Join("|", header.DeclaredOrder.Select(cell => cell.Text));
            Check.False("s5 appendix has no thickness column", headerText.IndexOf("dày", StringComparison.OrdinalIgnoreCase) >= 0);

            List<RowInfo> data = rows.Skip(1).ToList();
            var expected = new Dictionary<string, double[]>
            {
                { "80 x 50 mm", new[] { 1, 1, 0.26 } },
                { "600 x 300 mm", new[] { 1, 2, 3.6 } },
                { "1000 x 500 mm", new[] { 1, 3, 9.0 } },
                { "2000 x 800 mm", new[] { 1, 1, 5.6 } },
                { "Ø250 mm", new[] { 1, 1.5, Math.PI * 250 * 1500 / 1000000.0 } },
            };
            Check.Equal("s5 appendix one row per size", expected.Count, data.Count);
            foreach (KeyValuePair<string, double[]> item in expected)
            {
                RowInfo row = data.FirstOrDefault(candidate => candidate.Text('A') == item.Key);
                Check.True("s5 appendix row `" + item.Key + "`", row != null);
                if (row == null)
                    continue;
                Check.Near("s5 appendix count " + item.Key, item.Value[0], WorkbookModel.ParseDouble(row.Text('B')));
                Check.Near("s5 appendix length " + item.Key, item.Value[1], WorkbookModel.ParseDouble(row.Text('C')));
                Check.Near("s5 appendix area " + item.Key, item.Value[2], WorkbookModel.ParseDouble(row.Text('D')));
            }

            // Whether shapes interleave is open in the spec. Each shape must be ascending.
            Check.Sequence("s5 appendix rectangular rows ascending",
                new[] { "80 x 50 mm", "600 x 300 mm", "1000 x 500 mm", "2000 x 800 mm" },
                data.Select(row => row.Text('A')).Where(text => !text.StartsWith("Ø", StringComparison.Ordinal)));

            double bandTotal = workbook.Main.Rows.Values
                .Where(row => (row.Text('B') ?? "").StartsWith("Ống gió tôn mạ kẽm", StringComparison.Ordinal))
                .Sum(row => WorkbookModel.ParseDouble(row.Text('F')));
            double appendixTotal = data.Sum(row => WorkbookModel.ParseDouble(row.Text('D')));
            Check.Near("s5 main total equals appendix total", appendixTotal, bandTotal, 1e-6);
        }

        private static void Writers(string directory)
        {
            string path = Path.Combine(directory, "overwrite.xml");
            TakeoffWorkbookWriter.Write(path, Fixtures.Full());
            TakeoffWorkbookWriter.Write(path, Fixtures.Empty());
            WorkbookModel workbook = WorkbookModel.Load(path);
            Check.Equal("s5 overwrite leaves no old content", 2, workbook.Sheets.Count);
            Check.False("s5 overwrite shorter file",
                workbook.Main.Rows.Values.Any(row => row.Text('B') == "Dàn nóng ODU 6 HP"));

            using (var memory = new MemoryStream())
            {
                TakeoffWorkbookWriter.Write(memory, Fixtures.Empty());
                byte[] fromStream = memory.ToArray();
                byte[] fromFile = File.ReadAllBytes(path);
                Check.True("s5 stream and file output match", fromStream.SequenceEqual(fromFile),
                    fromStream.Length + " vs " + fromFile.Length + " bytes");
            }
        }

        private static void Escaping(string directory)
        {
            const string name = "A&B <x> \"q\" 'r'";
            WorkbookModel workbook = Save(Fixtures.RowHeights(new[] { name }), directory, "escape.xml");
            Check.True("s5 XML special characters survive",
                workbook.Main.Rows.Values.Any(row => row.Text('B') == name));
        }
    }
}
