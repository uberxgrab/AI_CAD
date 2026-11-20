using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using HvacAiCadAddin.ControlWires;

namespace HvacAiCadAddin.Commands
{
    /// <summary>Reads and writes the HVACCTRLWIRE values in %APPDATA%\HvacAiCadAddin\HVACCTRLWIRE.settings.</summary>
    internal static class ControlWireSettingsStore
    {
        private static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HvacAiCadAddin",
                    "HVACCTRLWIRE.settings");
            }
        }

        internal static ControlWireSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return ControlWireSettings.Deserialize(File.ReadAllText(SettingsPath));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return new ControlWireSettings();
        }

        internal static void Save(ControlWireSettings settings)
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
    /// HVACCTRLWIRE dialog: the offset spacing, the (fixed) wire layer and the cable text. The last
    /// accepted values are remembered between runs.
    /// </summary>
    public sealed class ControlWireSettingsForm : Form
    {
        private readonly TextBox spacingBox = new TextBox { Width = 90 };
        private readonly TextBox layerBox = new TextBox { Width = 200, ReadOnly = true };
        private readonly CheckBox textCheck = new CheckBox { Text = "Write cable text", AutoSize = true };
        private readonly TextBox cableBox = new TextBox { Width = 200 };
        private readonly TextBox heightBox = new TextBox { Width = 90 };
        private readonly Label errorLabel = new Label();

        internal ControlWireSettings Settings { get; private set; }

        internal ControlWireSettingsForm()
        {
            ControlWireSettings last = ControlWireSettingsStore.Load();

            Text = "HVACCTRLWIRE: draw control wires from a template route";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(560, 330);
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

            AddHeader(main, "Wires");
            AddRow(main, "Offset spacing d (mm): wires run at d, 2d, 3d... from the template", spacingBox);
            AddRow(main, "Layer", layerBox);

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

            spacingBox.Text = Format(last.Spacing);
            layerBox.Text = ControlWireSettings.LayerName;
            textCheck.Checked = last.WriteText;
            cableBox.Text = last.TextString;
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
            ControlWireSettings defaults = new ControlWireSettings();
            ControlWireSettings settings = new ControlWireSettings();
            if (!TryNumber(spacingBox.Text, out settings.Spacing))
            {
                errorLabel.Text = "The offset spacing must be a number.";
                return;
            }

            settings.WriteText = textCheck.Checked;
            settings.TextString = cableBox.Text.Trim();
            if (!settings.WriteText && settings.TextString.Length == 0)
                settings.TextString = defaults.TextString;

            // The text height is only read when the text is written, so a blank box is fine when it is off.
            if (!TryNumber(heightBox.Text, out settings.TextHeight))
            {
                if (settings.WriteText)
                {
                    errorLabel.Text = "The text height must be a number.";
                    return;
                }

                settings.TextHeight = defaults.TextHeight;
            }

            string error;
            if (!settings.IsValid(out error))
            {
                errorLabel.Text = error;
                return;
            }

            ControlWireSettingsStore.Save(settings);
            Settings = settings;
            DialogResult = DialogResult.OK;
        }
    }
}
