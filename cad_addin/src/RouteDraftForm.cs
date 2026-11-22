using System;
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
    /// trunk axis and the shortest stub. The last accepted values are remembered between runs.
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
        private readonly Label errorLabel = new Label();

        private static readonly string[] ModeValues =
        {
            RouteDraftSettings.ModeBest, RouteDraftSettings.ModeTrunk, RouteDraftSettings.ModeMst
        };

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
            ClientSize = new Size(520, 430);
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
                "Best of trunk and rectilinear tree", "Trunk with branches", "Rectilinear tree (L bends)"
            });
            axisBox.Items.AddRange(new object[] { "Automatic", "Horizontal", "Vertical" });
            AddRow(main, "Tree mode", modeBox);
            AddRow(main, "Trunk direction", axisBox);
            AddRow(main, "Shortest stub at the target (mm)", stubBox);

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
            axisBox.Enabled = ModeValues[Math.Max(0, modeBox.SelectedIndex)] != RouteDraftSettings.ModeMst;
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

            double stub;
            if (!double.TryParse((stubBox.Text ?? "").Trim().Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out stub))
            {
                errorLabel.Text = "The shortest stub must be a number.";
                return;
            }

            settings.MinStub = stub;
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
