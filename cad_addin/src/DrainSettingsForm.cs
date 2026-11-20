using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using HvacAiCadAddin.Pipes;

namespace HvacAiCadAddin.Commands
{
    /// <summary>Reads and writes the HVACDRAIN values in %APPDATA%\HvacAiCadAddin\HVACDRAIN.settings.</summary>
    internal static class DrainSettingsStore
    {
        private static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HvacAiCadAddin",
                    "HVACDRAIN.settings");
            }
        }

        internal static DrainSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return DrainSettings.Deserialize(File.ReadAllText(SettingsPath));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return new DrainSettings();
        }

        internal static void Save(DrainSettings settings)
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
    /// HVACDRAIN dialog: the cleaning tolerances, the base angle, the 45 degree slant length, the sizing basis
    /// (condensate factor, slope, capacity search radius) and the size labels. The last accepted values are
    /// remembered between runs.
    /// </summary>
    public sealed class DrainSettingsForm : Form
    {
        private readonly TextBox weldBox = new TextBox { Width = 90 };
        private readonly TextBox angleBox = new TextBox { Width = 90 };
        private readonly TextBox alignBox = new TextBox { Width = 90 };
        private readonly TextBox jogBox = new TextBox { Width = 90 };
        private readonly RadioButton ucsRadio = new RadioButton { Text = "UCS X axis", AutoSize = true };
        private readonly RadioButton customRadio = new RadioButton { Text = "Angle (degrees from world X)", AutoSize = true };
        private readonly TextBox baseAngleBox = new TextBox { Width = 90 };
        private readonly TextBox slantBox = new TextBox { Width = 90 };
        private readonly CheckBox sizeBox = new CheckBox { Text = "Size pipes (Daikin Si39 basis)", AutoSize = true };
        private readonly TextBox factorBox = new TextBox { Width = 90 };
        private readonly RadioButton slope100 = new RadioButton { Text = "1/100", AutoSize = true };
        private readonly RadioButton slope50 = new RadioButton { Text = "1/50", AutoSize = true };
        private readonly TextBox radiusBox = new TextBox { Width = 90 };
        private readonly CheckBox labelBox = new CheckBox { Text = "Write size labels", AutoSize = true };
        private readonly TextBox heightBox = new TextBox { Width = 90 };
        private readonly TextBox formatBox = new TextBox { Width = 160 };
        private readonly TextBox layerBox = new TextBox { Width = 220 };
        private readonly Label errorLabel = new Label();

        internal DrainSettings Settings { get; private set; }

        internal DrainSettingsForm()
        {
            DrainSettings last = DrainSettingsStore.Load();

            Text = "HVACDRAIN: draw and size a condensate network";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(560, 660);
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
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddHeader(main, "Cleaning");
            AddRow(main, "Weld tolerance (mm)", weldBox);
            AddRow(main, "Angle tolerance (degrees)", angleBox);
            AddRow(main, "Align tolerance (mm)", alignBox);
            AddRow(main, "Jog tolerance (mm)", jogBox);

            AddHeader(main, "Base angle (horizontal is along it, vertical across it)");
            main.Controls.Add(ucsRadio);
            main.SetColumnSpan(ucsRadio, 2);
            FlowLayoutPanel custom = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            custom.Controls.Add(customRadio);
            custom.Controls.Add(baseAngleBox);
            main.Controls.Add(custom);
            main.SetColumnSpan(custom, 2);

            AddHeader(main, "45 degree joints");
            AddRow(main, "Slant length (mm); the cut is 0.707 of it", slantBox);

            AddHeader(main, "Sizing");
            main.Controls.Add(sizeBox);
            main.SetColumnSpan(sizeBox, 2);
            AddRow(main, "Condensate factor (L/h per kW)", factorBox);
            FlowLayoutPanel slope = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            slope.Controls.Add(slope100);
            slope.Controls.Add(slope50);
            AddRow(main, "Slope of the drain pipes", slope);
            AddRow(main, "Capacity search radius from a pipe end (mm)", radiusBox);

            AddHeader(main, "Size labels");
            main.Controls.Add(labelBox);
            main.SetColumnSpan(labelBox, 2);
            AddRow(main, "Label height (mm); the label sits this far off the pipe", heightBox);
            AddRow(main, "Label format ({size}, {slope} in percent)", formatBox);
            AddRow(main, "Label layer (blank: the pipe's own layer)", layerBox);

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
            baseAngleBox.Text = Format(last.BaseAngleDegrees);
            ucsRadio.Checked = last.UseUcsBaseAngle;
            customRadio.Checked = !last.UseUcsBaseAngle;
            slantBox.Text = Format(last.SlantLength);
            sizeBox.Checked = last.SizePipes;
            factorBox.Text = Format(last.CondensateFactor);
            slope100.Checked = last.Slope == DrainSlope.OneIn100;
            slope50.Checked = last.Slope == DrainSlope.OneIn50;
            radiusBox.Text = Format(last.CapacityRadius);
            labelBox.Checked = last.WriteLabels;
            heightBox.Text = Format(last.LabelHeight);
            formatBox.Text = last.LabelFormat;
            layerBox.Text = last.LabelLayer;
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static void AddHeader(TableLayoutPanel table, string text)
        {
            Label header = new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font(table.Font, FontStyle.Bold),
                Margin = new Padding(3, 14, 3, 4)
            };
            table.Controls.Add(header);
            table.SetColumnSpan(header, 2);
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
            DrainSettings settings = new DrainSettings();
            if (!TryNumber(weldBox.Text, out settings.WeldTolerance)
                || !TryNumber(angleBox.Text, out settings.AngleToleranceDegrees)
                || !TryNumber(alignBox.Text, out settings.AlignTolerance)
                || !TryNumber(jogBox.Text, out settings.JogTolerance)
                || !TryNumber(slantBox.Text, out settings.SlantLength)
                || !TryNumber(factorBox.Text, out settings.CondensateFactor)
                || !TryNumber(radiusBox.Text, out settings.CapacityRadius)
                || !TryNumber(heightBox.Text, out settings.LabelHeight))
            {
                errorLabel.Text = "Every value must be a number.";
                return;
            }

            settings.UseUcsBaseAngle = ucsRadio.Checked;
            if (!TryNumber(baseAngleBox.Text, out settings.BaseAngleDegrees))
            {
                if (customRadio.Checked)
                {
                    errorLabel.Text = "The base angle must be a number.";
                    return;
                }

                settings.BaseAngleDegrees = 0;
            }

            settings.SizePipes = sizeBox.Checked;
            settings.Slope = slope50.Checked ? DrainSlope.OneIn50 : DrainSlope.OneIn100;
            settings.WriteLabels = labelBox.Checked;
            settings.LabelFormat = formatBox.Text.Trim();
            settings.LabelLayer = layerBox.Text.Trim();

            string error;
            if (!settings.IsValid(out error))
            {
                errorLabel.Text = error;
                return;
            }

            DrainSettingsStore.Save(settings);
            Settings = settings;
            DialogResult = DialogResult.OK;
        }
    }
}
