using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HvacAiCadAddin.Takeoff;

namespace HvacAiCadAddin.Commands
{
    /// <summary>
    /// Asks for the contingency factor, the duct sheet gauge bands and the shaft risers that the
    /// drawing does not show. The last accepted values are remembered between runs.
    /// </summary>
    public sealed class TakeoffSettingsForm : Form
    {
        private readonly TextBox factorBox = new TextBox();
        private readonly DataGridView gaugeGrid = new DataGridView();
        private readonly DataGridView shaftGrid = new DataGridView();
        private readonly Label errorLabel = new Label();

        public TakeoffSettingsForm()
        {
            Text = "Thiết lập bóc khối lượng";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(620, 600);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            TakeoffOptions last = LoadLastOptions();

            Label factorLabel = new Label
            {
                Text = "Hệ số dự phòng (nhân với khối lượng ống gió và ống nước):",
                AutoSize = true,
                Margin = new Padding(3, 8, 3, 3)
            };
            factorBox.Width = 80;
            factorBox.Text = TakeoffClassifier.FormatNumber(last.ContingencyFactor);
            FlowLayoutPanel factorPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 36,
                Padding = new Padding(6, 4, 6, 0)
            };
            factorPanel.Controls.Add(factorLabel);
            factorPanel.Controls.Add(factorBox);

            ConfigureGaugeGrid(last.Gauge);
            GroupBox gaugeGroup = new GroupBox
            {
                Text = "Chiều dày tôn ống gió",
                Dock = DockStyle.Top,
                Height = 170,
                Padding = new Padding(6)
            };
            gaugeGroup.Controls.Add(gaugeGrid);

            ConfigureShaftGrid(last.ShaftPipes);
            GroupBox shaftGroup = new GroupBox
            {
                Text = "Ống đi trong trục kỹ thuật (cộng thêm vào BOQ; chọn dòng và nhấn Delete để xóa)",
                Dock = DockStyle.Fill,
                Padding = new Padding(6)
            };
            shaftGroup.Controls.Add(shaftGrid);

            errorLabel.Dock = DockStyle.Bottom;
            errorLabel.Height = 40;
            errorLabel.Padding = new Padding(6, 4, 6, 0);
            errorLabel.ForeColor = Color.Firebrick;

            Button okButton = new Button { Text = "OK" };
            okButton.Click += OnOkClick;
            Button cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(4)
            };
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(okButton);

            Controls.Add(shaftGroup);
            Controls.Add(gaugeGroup);
            Controls.Add(factorPanel);
            Controls.Add(errorLabel);
            Controls.Add(buttons);
            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        public TakeoffOptions Options { get; private set; }

        private static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HvacAiCadAddin", "HVACQTO.settings");
            }
        }

        private static TakeoffOptions LoadLastOptions()
        {
            if (TestMode.Enabled)
                return TestMode.LoadSettings("HVACQTO.settings", TakeoffOptions.Deserialize, TakeoffOptions.CreateDefault());

            try
            {
                if (File.Exists(SettingsPath))
                    return TakeoffOptions.Deserialize(File.ReadAllText(SettingsPath));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return TakeoffOptions.CreateDefault();
        }

        private static void SaveOptions(TakeoffOptions options)
        {
            if (TestMode.Enabled)
                return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath, options.Serialize());
            }
            catch (IOException)
            {
                // Remembering values is a convenience; the takeoff itself still runs.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void ConfigureGrid(DataGridView grid)
        {
            grid.Dock = DockStyle.Fill;
            grid.AllowUserToResizeRows = false;
            grid.AllowUserToOrderColumns = false;
            grid.RowHeadersVisible = false;
            grid.MultiSelect = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        }

        private static void AddTextColumn(DataGridView grid, string header)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }

        private void ConfigureGaugeGrid(DuctGaugeSettings gauge)
        {
            ConfigureGrid(gaugeGrid);
            gaugeGrid.AllowUserToAddRows = false;
            gaugeGrid.AllowUserToDeleteRows = false;
            gaugeGrid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            foreach (string header in new[] { "Từ (mm)", "Đến (mm)", "Chiều dày tôn (mm)" })
                AddTextColumn(gaugeGrid, header);

            foreach (DuctGaugeBand band in gauge.Bands)
            {
                gaugeGrid.Rows.Add(
                    TakeoffClassifier.FormatNumber(band.From),
                    band.To.HasValue ? TakeoffClassifier.FormatNumber(band.To.Value) : "",
                    TakeoffClassifier.FormatNumber(band.Thickness));
            }

            DataGridViewCell lastTo = gaugeGrid.Rows[gaugeGrid.Rows.Count - 1].Cells[1];
            lastTo.ReadOnly = true;
            lastTo.Style.BackColor = SystemColors.Control;
        }

        private void ConfigureShaftGrid(IEnumerable<ShaftPipeInput> shafts)
        {
            ConfigureGrid(shaftGrid);
            shaftGrid.AllowUserToAddRows = true;
            shaftGrid.AllowUserToDeleteRows = true;
            shaftGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            shaftGrid.EditMode = DataGridViewEditMode.EditOnEnter;

            DataGridViewComboBoxColumn kind = new DataGridViewComboBoxColumn
            {
                HeaderText = "Loại ống",
                FlatStyle = FlatStyle.Flat,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            kind.Items.AddRange(TakeoffOptions.ShaftKindNames.Cast<object>().ToArray());
            shaftGrid.Columns.Add(kind);
            AddTextColumn(shaftGrid, "Kích thước (600x300, D250, DN100, D34)");
            AddTextColumn(shaftGrid, "Chiều cao (m)");
            AddTextColumn(shaftGrid, "Số đoạn");
            shaftGrid.Columns[1].FillWeight = 180;

            foreach (ShaftPipeInput shaft in shafts)
            {
                shaftGrid.Rows.Add(
                    TakeoffOptions.GetShaftKindName(shaft.Kind),
                    shaft.Size,
                    TakeoffClassifier.FormatNumber(shaft.HeightMeters),
                    shaft.Count.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static List<string[]> ReadRows(DataGridView grid)
        {
            return grid.Rows.Cast<DataGridViewRow>()
                .Where(row => !row.IsNewRow)
                .Select(row => row.Cells.Cast<DataGridViewCell>()
                    .Select(cell => Convert.ToString(cell.Value, CultureInfo.InvariantCulture))
                    .ToArray())
                .ToList();
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            gaugeGrid.EndEdit();
            shaftGrid.EndEdit();

            TakeoffOptions options;
            string error;
            if (!TakeoffOptions.TryParse(factorBox.Text, ReadRows(gaugeGrid), ReadRows(shaftGrid), out options, out error))
            {
                errorLabel.Text = error;
                return;
            }

            SaveOptions(options);
            Options = options;
            DialogResult = DialogResult.OK;
        }
    }
}
