using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using HvacAiCadAddin.Pipes;

namespace HvacAiCadAddin.Commands
{
    /// <summary>Reads and writes the HVACREFRIG values in %APPDATA%\HvacAiCadAddin\HVACREFRIG.settings.</summary>
    internal static class RefrigSettingsStore
    {
        private static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HvacAiCadAddin",
                    "HVACREFRIG.settings");
            }
        }

        internal static RefrigSettings Load()
        {
            if (TestMode.Enabled)
                return TestMode.LoadSettings("HVACREFRIG.settings", RefrigSettings.Deserialize, new RefrigSettings());

            try
            {
                if (File.Exists(SettingsPath))
                    return RefrigSettings.Deserialize(File.ReadAllText(SettingsPath));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return new RefrigSettings();
        }

        internal static void Save(RefrigSettings settings)
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
    /// HVACREFRIG dialog: the cleaning tolerances, the refnet scale, the refnet piping rules (R1, R2, R3),
    /// the pipe sizing options (size pipes, size labels and their text height, the capacity search radius)
    /// and the base angle. The last accepted values are remembered between runs.
    /// </summary>
    public sealed class RefrigSettingsForm : Form
    {
        private readonly TextBox weldBox = new TextBox { Width = 90 };
        private readonly TextBox angleBox = new TextBox { Width = 90 };
        private readonly TextBox alignBox = new TextBox { Width = 90 };
        private readonly TextBox jogBox = new TextBox { Width = 90 };
        private readonly TextBox scaleBox = new TextBox { Width = 90 };
        private readonly TextBox spacingBox = new TextBox { Width = 90 };
        private readonly TextBox straightBox = new TextBox { Width = 90 };
        private readonly TextBox maxRunBox = new TextBox { Width = 90 };
        private readonly TextBox allowanceBox = new TextBox { Width = 90 };
        private readonly RadioButton ucsRadio = new RadioButton { Text = "UCS X axis", AutoSize = true };
        private readonly RadioButton customRadio = new RadioButton { Text = "Angle (degrees from world X)", AutoSize = true };
        private readonly TextBox baseAngleBox = new TextBox { Width = 90 };
        private readonly CheckBox sizeCheck = new CheckBox { Text = "Size pipes", AutoSize = true };
        private readonly CheckBox labelsCheck = new CheckBox { Text = "Write size labels", AutoSize = true };
        private readonly TextBox labelHeightBox = new TextBox { Width = 90 };
        private readonly TextBox radiusBox = new TextBox { Width = 90 };
        private readonly Label errorLabel = new Label();

        internal RefrigSettings Settings { get; private set; }

        internal RefrigSettingsForm()
        {
            RefrigSettings last = RefrigSettingsStore.Load();

            Text = "HVACREFRIG: clean up a refrigerant network";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(500, 640);
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
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddRow(main, "Weld tolerance (mm)", weldBox);
            AddRow(main, "Angle tolerance (degrees)", angleBox);
            AddRow(main, "Align tolerance (mm)", alignBox);
            AddRow(main, "Jog tolerance (mm)", jogBox);
            AddRow(main, "Refnet scale", scaleBox);

            Label rules = new Label
            {
                Text = "Refnet piping rules",
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(3, 14, 3, 4)
            };
            main.Controls.Add(rules);
            main.SetColumnSpan(rules, 2);
            AddRow(main, "R1: refnet outlet to next refnet inlet, min (mm)", spacingBox);
            AddRow(main, "R2: straight pipe after an outlet, min (mm)", straightBox);
            AddRow(main, "R3: first refnet inlet to farthest unit, max (mm)", maxRunBox);
            AddRow(main, "R3: construction allowance, warn within (mm)", allowanceBox);

            Label sizing = new Label
            {
                Text = "Pipe sizing (Daikin VRV IV)",
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(3, 14, 3, 4)
            };
            main.Controls.Add(sizing);
            main.SetColumnSpan(sizing, 2);
            main.Controls.Add(sizeCheck);
            main.SetColumnSpan(sizeCheck, 2);
            main.Controls.Add(labelsCheck);
            main.SetColumnSpan(labelsCheck, 2);
            AddRow(main, "Label height (mm)", labelHeightBox);
            AddRow(main, "Capacity search radius for labels (mm)", radiusBox);

            Label header = new Label
            {
                Text = "Base angle (horizontal is along it, vertical across it)",
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(3, 14, 3, 4)
            };
            main.Controls.Add(header);
            main.SetColumnSpan(header, 2);
            main.Controls.Add(ucsRadio);
            main.SetColumnSpan(ucsRadio, 2);
            FlowLayoutPanel custom = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            custom.Controls.Add(customRadio);
            custom.Controls.Add(baseAngleBox);
            main.Controls.Add(custom);
            main.SetColumnSpan(custom, 2);

            errorLabel.Dock = DockStyle.Bottom;
            errorLabel.Height = 48;
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

            weldBox.Text = Format(last.WeldTolerance);
            angleBox.Text = Format(last.AngleToleranceDegrees);
            alignBox.Text = Format(last.AlignTolerance);
            jogBox.Text = Format(last.JogTolerance);
            scaleBox.Text = Format(last.RefnetScale);
            spacingBox.Text = Format(last.RefnetSpacing);
            straightBox.Text = Format(last.OutletStraight);
            maxRunBox.Text = Format(last.MaxUnitRun);
            allowanceBox.Text = Format(last.RunAllowance);
            baseAngleBox.Text = Format(last.BaseAngleDegrees);
            sizeCheck.Checked = last.SizePipes;
            labelsCheck.Checked = last.WriteLabels;
            labelHeightBox.Text = Format(last.LabelHeight);
            radiusBox.Text = Format(last.LabelSearchRadius);
            sizeCheck.CheckedChanged += (sender, args) => UpdateSizingControls();
            labelsCheck.CheckedChanged += (sender, args) => UpdateSizingControls();
            UpdateSizingControls();
            ucsRadio.Checked = last.UseUcsBaseAngle;
            customRadio.Checked = !last.UseUcsBaseAngle;
        }

        // The label options only matter while pipes are sized (labels) or labels are written (height).
        private void UpdateSizingControls()
        {
            labelsCheck.Enabled = sizeCheck.Checked;
            labelHeightBox.Enabled = sizeCheck.Checked && labelsCheck.Checked;
            radiusBox.Enabled = sizeCheck.Checked;
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
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

        private static bool TryNumber(string text, out double value)
        {
            return double.TryParse(
                (text ?? "").Trim().Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value) && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            RefrigSettings settings = new RefrigSettings();
            if (!TryNumber(weldBox.Text, out settings.WeldTolerance)
                || !TryNumber(angleBox.Text, out settings.AngleToleranceDegrees)
                || !TryNumber(alignBox.Text, out settings.AlignTolerance)
                || !TryNumber(jogBox.Text, out settings.JogTolerance)
                || !TryNumber(scaleBox.Text, out settings.RefnetScale)
                || !TryNumber(spacingBox.Text, out settings.RefnetSpacing)
                || !TryNumber(straightBox.Text, out settings.OutletStraight)
                || !TryNumber(maxRunBox.Text, out settings.MaxUnitRun)
                || !TryNumber(allowanceBox.Text, out settings.RunAllowance)
                || !TryNumber(labelHeightBox.Text, out settings.LabelHeight)
                || !TryNumber(radiusBox.Text, out settings.LabelSearchRadius))
            {
                errorLabel.Text = "Every value must be a number.";
                return;
            }

            settings.UseUcsBaseAngle = ucsRadio.Checked;
            settings.SizePipes = sizeCheck.Checked;
            settings.WriteLabels = labelsCheck.Checked;
            if (!TryNumber(baseAngleBox.Text, out settings.BaseAngleDegrees))
            {
                if (customRadio.Checked)
                {
                    errorLabel.Text = "The base angle must be a number.";
                    return;
                }

                settings.BaseAngleDegrees = 0;
            }

            string error;
            if (!settings.IsValid(out error))
            {
                errorLabel.Text = error;
                return;
            }

            RefrigSettingsStore.Save(settings);
            Settings = settings;
            DialogResult = DialogResult.OK;
        }
    }
}
