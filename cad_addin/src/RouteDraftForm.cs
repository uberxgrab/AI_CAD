using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using HvacAiCadAddin.Routes;

namespace HvacAiCadAddin.Commands
{
    /// <summary>Reads and writes the HVACROUTEDRAFT values in %APPDATA%\HvacAiCadAddin\HVACROUTEDRAFT.settings.</summary>
    internal static class RouteDraftSettingsStore
    {
        private static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HvacAiCadAddin",
                    "HVACROUTEDRAFT.settings");
            }
        }

        internal static RouteDraftSettings Load()
        {
            if (TestMode.Enabled)
                return TestMode.LoadSettings("HVACROUTEDRAFT.settings", RouteDraftSettings.Deserialize, new RouteDraftSettings());

            try
            {
                if (File.Exists(SettingsPath))
                    return RouteDraftSettings.Deserialize(File.ReadAllText(SettingsPath));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return new RouteDraftSettings();
        }

        internal static void Save(RouteDraftSettings settings)
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

    /// <summary>
    /// HVACROUTEDRAFT dialog: which drafts to draw (refrigerant, drain, power wire), the tree mode, the
    /// trunk axis and the shortest stub, the corridor costs, and where the pipes end on the unit blocks
    /// (insertion point or connection port, with the port offsets per block-name pattern). The last
    /// accepted values are remembered between runs.
    /// </summary>
    public sealed class RouteDraftForm : Form
    {
        private readonly CheckBox refrigCheck = new CheckBox { Text = "Refrigerant pipe (HVAC-DRAFT-REFRIG)", AutoSize = true };
        private readonly CheckBox drainCheck = new CheckBox { Text = "Condensate drain (HVAC-DRAFT-DRAIN)", AutoSize = true };
        private readonly CheckBox powerCheck = new CheckBox { Text = "Power wire (HVAC-DRAFT-POWER)", AutoSize = true };
        private readonly CheckBox drainTargetCheck = new CheckBox { Text = "Pick a separate target for the drain", AutoSize = true };
        private readonly CheckBox powerTargetCheck = new CheckBox { Text = "Pick a separate supply point for the power wire", AutoSize = true };
        private readonly ComboBox modeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
        private readonly ComboBox axisBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
        private readonly TextBox stubBox = new TextBox { Width = 90 };
        private readonly ComboBox portModeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        private readonly TextBox cellBox = new TextBox { Width = 90 };
        private readonly TextBox roomBox = new TextBox { Width = 90 };
        private readonly TextBox outsideBox = new TextBox { Width = 90 };
        private readonly TextBox boundaryBox = new TextBox { Width = 90 };
        private readonly TextBox bendBox = new TextBox { Width = 90 };
        private readonly DataGridView portGrid = new DataGridView();
        private readonly Label errorLabel = new Label();

        private static readonly string[] ModeValues =
        {
            RouteDraftSettings.ModeCorridor, RouteDraftSettings.ModeBest, RouteDraftSettings.ModeTrunk, RouteDraftSettings.ModeMst
        };

        private static readonly string[] PortModeValues =
        {
            RouteDraftSettings.PortsBlock, RouteDraftSettings.PortsInsertion
        };

        private static readonly string[] DirectionChoices = { "+X", "-X", "+Y", "-Y", "" };

        private static readonly string[] AxisValues =
        {
            RouteDraftSettings.AxisAuto, RouteDraftSettings.AxisHorizontal, RouteDraftSettings.AxisVertical
        };

        internal RouteDraftSettings Settings { get; private set; }

        internal RouteDraftForm()
        {
            RouteDraftSettings last = RouteDraftSettingsStore.Load();

            Text = "HVACROUTEDRAFT: draft routes to the units";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(600, 700);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            TableLayoutPanel main = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(10),
                AutoScroll = true
            };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddHeader(main, "Drafts to draw");
            AddWide(main, refrigCheck);
            AddWide(main, drainCheck);
            AddWide(main, powerCheck);
            AddWide(main, drainTargetCheck);
            AddWide(main, powerTargetCheck);

            AddHeader(main, "Route");
            modeBox.Items.AddRange(new object[]
            {
                "Along corridors (HVAC-ROOM polygons)", "Best of trunk and rectilinear tree", "Trunk with branches",
                "Rectilinear tree (L bends)"
            });
            axisBox.Items.AddRange(new object[] { "Automatic", "Horizontal", "Vertical" });
            AddRow(main, "Tree mode", modeBox);
            AddRow(main, "Trunk direction", axisBox);
            AddRow(main, "Shortest stub at the target (mm)", stubBox);

            AddHeader(main, "Corridor mode costs (corridor = 1 per cell)");
            AddRow(main, "Cell size (mm)", cellBox);
            AddRow(main, "Cell inside a room", roomBox);
            AddRow(main, "Cell outside every room", outsideBox);
            AddRow(main, "Each room wall crossed", boundaryBox);
            AddRow(main, "Each bend", bendBox);

            AddHeader(main, "Where the pipes end on a unit");
            portModeBox.Items.AddRange(new object[] { "Connection port of the block (rules below)", "Insertion point" });
            AddRow(main, "Pipe end", portModeBox);
            Label portNote = new Label
            {
                Text = "Offsets are in the block's own coordinates (before scale, rotation and mirroring). The longest "
                    + "matching block-name part wins; blocks without a rule end at the insertion point. The template "
                    + "cassettes (FCU-CAS, body 950 x 950, origin at the middle of the top edge) have no connection "
                    + "marks, so the default ports are assumed: both on the +X side. The power wire always ends at "
                    + "the insertion point.",
                AutoSize = false,
                Width = 560,
                Height = 78,
                Margin = new Padding(3, 3, 3, 3)
            };
            AddWide(main, portNote);
            ConfigurePortGrid();
            AddWide(main, portGrid);

            Label note = new Label
            {
                Text = "The drafts are rough, wall-blind trees. Run HVACREFRIG, HVACDRAIN or HVACPOWERWIRE on them and "
                    + "pick the root end the command prints.",
                AutoSize = false,
                Width = 480,
                Height = 48,
                Margin = new Padding(3, 14, 3, 3)
            };
            AddWide(main, note);

            errorLabel.Dock = DockStyle.Bottom;
            errorLabel.Height = 32;
            errorLabel.Padding = new Padding(10, 4, 10, 0);
            errorLabel.ForeColor = Color.Firebrick;

            Button ok = new Button { Text = "OK" };
            ok.Click += OnOkClick;
            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(4)
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);

            Controls.Add(main);
            Controls.Add(errorLabel);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;

            refrigCheck.Checked = last.DrawRefrig;
            drainCheck.Checked = last.DrawDrain;
            powerCheck.Checked = last.DrawPower;
            drainTargetCheck.Checked = last.SeparateDrainTarget;
            powerTargetCheck.Checked = last.SeparatePowerTarget;
            modeBox.SelectedIndex = Math.Max(0, Array.IndexOf(ModeValues, last.Mode));
            portModeBox.SelectedIndex = Math.Max(0, Array.IndexOf(PortModeValues, last.PortMode));
            cellBox.Text = Num(last.CellSize);
            roomBox.Text = Num(last.RoomPenalty);
            outsideBox.Text = Num(last.OutsidePenalty);
            boundaryBox.Text = Num(last.BoundaryPenalty);
            bendBox.Text = Num(last.BendPenalty);
            foreach (PortRule rule in last.PortRules)
            {
                portGrid.Rows.Add(rule.Pattern, Num(rule.RefrigX), Num(rule.RefrigY), rule.RefrigDir,
                    Num(rule.DrainX), Num(rule.DrainY), rule.DrainDir);
            }

            axisBox.SelectedIndex = Math.Max(0, Array.IndexOf(AxisValues, last.TrunkAxis));
            stubBox.Text = last.MinStub.ToString("0.###", CultureInfo.InvariantCulture);
            drainCheck.CheckedChanged += (sender, args) => UpdateControls();
            powerCheck.CheckedChanged += (sender, args) => UpdateControls();
            modeBox.SelectedIndexChanged += (sender, args) => UpdateControls();
            UpdateControls();
        }

        private void UpdateControls()
        {
            drainTargetCheck.Enabled = drainCheck.Checked;
            powerTargetCheck.Enabled = powerCheck.Checked;
            string mode = ModeValues[Math.Max(0, modeBox.SelectedIndex)];
            axisBox.Enabled = mode != RouteDraftSettings.ModeMst && mode != RouteDraftSettings.ModeCorridor;
            bool corridor = mode == RouteDraftSettings.ModeCorridor;
            cellBox.Enabled = corridor;
            roomBox.Enabled = corridor;
            outsideBox.Enabled = corridor;
            boundaryBox.Enabled = corridor;
            bendBox.Enabled = corridor;
        }

        private static string Num(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private void ConfigurePortGrid()
        {
            portGrid.Width = 560;
            portGrid.Height = 130;
            portGrid.AllowUserToAddRows = true;
            portGrid.AllowUserToDeleteRows = true;
            portGrid.RowHeadersVisible = false;
            portGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            portGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Block name contains", FillWeight = 160 });
            portGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Refrig. X", FillWeight = 70 });
            portGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Refrig. Y", FillWeight = 70 });
            portGrid.Columns.Add(DirectionColumn("Leaves"));
            portGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Drain X", FillWeight = 70 });
            portGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Drain Y", FillWeight = 70 });
            portGrid.Columns.Add(DirectionColumn("Leaves"));
        }

        private static DataGridViewComboBoxColumn DirectionColumn(string header)
        {
            DataGridViewComboBoxColumn column = new DataGridViewComboBoxColumn { HeaderText = header, FillWeight = 60 };
            column.Items.AddRange(DirectionChoices);
            return column;
        }

        private static string CellText(DataGridViewRow row, int column)
        {
            object value = row.Cells[column].Value;
            return value == null ? "" : value.ToString().Trim();
        }

        // The rules of the grid (rows left empty are skipped); null and the error text when one is bad.
        private List<PortRule> ReadRules(out string error)
        {
            error = null;
            List<PortRule> rules = new List<PortRule>();
            foreach (DataGridViewRow row in portGrid.Rows)
            {
                if (row.IsNewRow)
                    continue;
                string pattern = CellText(row, 0);
                if (pattern.Length == 0)
                    continue;
                double[] numbers = new double[4];
                int[] columns = { 1, 2, 4, 5 };
                for (int i = 0; i < 4; i++)
                {
                    if (!double.TryParse(CellText(row, columns[i]).Replace(',', '.'), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out numbers[i]))
                    {
                        error = "Port offsets must be numbers (block pattern '" + pattern + "').";
                        return null;
                    }
                }

                PortRule rule = new PortRule
                {
                    Pattern = pattern,
                    RefrigX = numbers[0],
                    RefrigY = numbers[1],
                    RefrigDir = CellText(row, 3),
                    DrainX = numbers[2],
                    DrainY = numbers[3],
                    DrainDir = CellText(row, 6)
                };
                if (!rule.IsValid(out error))
                    return null;
                rules.Add(rule);
            }

            return rules;
        }

        private static void AddHeader(TableLayoutPanel table, string text)
        {
            Label header = new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
                Margin = new Padding(3, 10, 3, 4)
            };
            AddWide(table, header);
        }

        private static void AddWide(TableLayoutPanel table, Control control)
        {
            table.Controls.Add(control);
            table.SetColumnSpan(control, 2);
        }

        private static void AddRow(TableLayoutPanel table, string label, Control control)
        {
            table.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Margin = new Padding(3, 7, 3, 3)
            });
            table.Controls.Add(control);
        }

        private static bool TryNumber(TextBox box, out double value)
        {
            return double.TryParse((box.Text ?? "").Trim().Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value);
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            RouteDraftSettings settings = new RouteDraftSettings
            {
                DrawRefrig = refrigCheck.Checked,
                DrawDrain = drainCheck.Checked,
                DrawPower = powerCheck.Checked,
                SeparateDrainTarget = drainTargetCheck.Checked,
                SeparatePowerTarget = powerTargetCheck.Checked,
                Mode = ModeValues[Math.Max(0, modeBox.SelectedIndex)],
                TrunkAxis = AxisValues[Math.Max(0, axisBox.SelectedIndex)]
            };

            double stub, cell, room, outside, boundary, bend;
            if (!TryNumber(stubBox, out stub))
            {
                errorLabel.Text = "The shortest stub must be a number.";
                return;
            }

            if (!TryNumber(cellBox, out cell) || !TryNumber(roomBox, out room) || !TryNumber(outsideBox, out outside)
                || !TryNumber(boundaryBox, out boundary) || !TryNumber(bendBox, out bend))
            {
                errorLabel.Text = "The corridor costs must be numbers.";
                return;
            }

            string ruleError;
            List<PortRule> rules = ReadRules(out ruleError);
            if (rules == null)
            {
                errorLabel.Text = ruleError;
                return;
            }

            settings.MinStub = stub;
            settings.CellSize = cell;
            settings.RoomPenalty = room;
            settings.OutsidePenalty = outside;
            settings.BoundaryPenalty = boundary;
            settings.BendPenalty = bend;
            settings.PortMode = PortModeValues[Math.Max(0, portModeBox.SelectedIndex)];
            settings.PortRules = rules;
            string error;
            if (!settings.IsValid(out error))
            {
                errorLabel.Text = error;
                return;
            }

            RouteDraftSettingsStore.Save(settings);
            Settings = settings;
            DialogResult = DialogResult.OK;
        }
    }
}
