using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using HvacAiCadAddin.Pipes;

namespace HvacAiCadAddin.Commands
{
    /// <summary>Reads and writes the HVACPOWERWIRE values in %APPDATA%\HvacAiCadAddin\HVACPOWERWIRE.settings.</summary>
    internal static class PowerWireSettingsStore
    {
        private static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HvacAiCadAddin",
                    "HVACPOWERWIRE.settings");
            }
        }

        internal static PowerWireSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return PowerWireSettings.Deserialize(File.ReadAllText(SettingsPath));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return new PowerWireSettings();
        }

        internal static void Save(PowerWireSettings settings)
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
    /// HVACPOWERWIRE dialog: the cleaning tolerances, the base angle, the hook size, the wire layer and the
    /// cable text. The last accepted values are remembered between runs.
    /// </summary>
    public sealed class PowerWireSettingsForm : Form
    {
        private readonly TextBox weldBox = new TextBox { Width = 90 };
        private readonly TextBox angleBox = new TextBox { Width = 90 };
        private readonly RadioButton ucsRadio = new RadioButton { Text = "UCS X axis", AutoSize = true };
        private readonly RadioButton customRadio = new RadioButton { Text = "Angle (degrees from world X)", AutoSize = true };
        private readonly TextBox baseAngleBox = new TextBox { Width = 90 };
        private readonly TextBox hookBox = new TextBox { Width = 90 };
        private readonly TextBox tailBox = new TextBox { Width = 90 };
        private readonly RadioButton supplyRadio = new RadioButton
        {
            Text = PowerWireSettings.SupplyLayerName,
            AutoSize = true
        };
        private readonly RadioButton signalRadio = new RadioButton
        {
            Text = PowerWireSettings.SignalLayerName,
            AutoSize = true
        };
        private readonly CheckBox textCheck = new CheckBox { Text = "Write cable text", AutoSize = true };
        private readonly TextBox cableBox = new TextBox { Width = 200 };
        private readonly TextBox heightBox = new TextBox { Width = 90 };
        private readonly Label errorLabel = new Label();

        internal PowerWireSettings Settings { get; private set; }

        internal PowerWireSettingsForm()
        {
            PowerWireSettings last = PowerWireSettingsStore.Load();

            Text = "HVACPOWERWIRE: draw the indoor-unit power wire";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(560, 600);
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

            AddHeader(main, "Base angle (horizontal is along it, vertical across it)");
            main.Controls.Add(ucsRadio);
            main.SetColumnSpan(ucsRadio, 2);
            FlowLayoutPanel custom = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            custom.Controls.Add(customRadio);
            custom.Controls.Add(baseAngleBox);
            main.Controls.Add(custom);
            main.SetColumnSpan(custom, 2);

            AddHeader(main, "Hook at each unit");
            AddRow(main, "Slant size (mm): 45 degrees, this far along and across", hookBox);
            AddRow(main, "A trunk end past the last branch is a unit from (mm)", tailBox);

            AddHeader(main, "Wire layer");
            main.Controls.Add(supplyRadio);
            main.SetColumnSpan(supplyRadio, 2);
            main.Controls.Add(signalRadio);
            main.SetColumnSpan(signalRadio, 2);

            AddHeader(main, "Cable text");
            main.Controls.Add(textCheck);
            main.SetColumnSpan(textCheck, 2);
            AddRow(main, "Text", cableBox);
            AddRow(main, "Text height (mm); the text sits this far off the leg", heightBox);
            textCheck.CheckedChanged += (sender, e) => UpdateTextControls();

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
            baseAngleBox.Text = Format(last.BaseAngleDegrees);
            ucsRadio.Checked = last.UseUcsBaseAngle;
            customRadio.Checked = !last.UseUcsBaseAngle;
            hookBox.Text = Format(last.HookSize);
            tailBox.Text = Format(last.EndUnitTail);
            supplyRadio.Checked = last.Layer == PowerWireLayerKind.Supply;
            signalRadio.Checked = last.Layer == PowerWireLayerKind.Signal;
            textCheck.Checked = last.WriteText;
            cableBox.Text = last.CableText;
            heightBox.Text = Format(last.TextHeight);
            UpdateTextControls();
        }

        private void UpdateTextControls()
        {
            cableBox.Enabled = textCheck.Checked;
            heightBox.Enabled = textCheck.Checked;
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
            PowerWireSettings settings = new PowerWireSettings();
            if (!TryNumber(weldBox.Text, out settings.WeldTolerance)
                || !TryNumber(angleBox.Text, out settings.AngleToleranceDegrees)
                || !TryNumber(hookBox.Text, out settings.HookSize)
                || !TryNumber(tailBox.Text, out settings.EndUnitTail))
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

            settings.Layer = signalRadio.Checked ? PowerWireLayerKind.Signal : PowerWireLayerKind.Supply;
            settings.WriteText = textCheck.Checked;
            settings.CableText = cableBox.Text.Trim();
            if (!settings.WriteText && settings.CableText.Length == 0)
                settings.CableText = new PowerWireSettings().CableText;

            // The text height is only read when the text is written, so a blank box is fine when it is off.
            if (!TryNumber(heightBox.Text, out settings.TextHeight))
            {
                if (settings.WriteText)
                {
                    errorLabel.Text = "The text height must be a number.";
                    return;
                }

                settings.TextHeight = new PowerWireSettings().TextHeight;
            }

            string error;
            if (!settings.IsValid(out error))
            {
                errorLabel.Text = error;
                return;
            }

            PowerWireSettingsStore.Save(settings);
            Settings = settings;
            DialogResult = DialogResult.OK;
        }
    }
}
