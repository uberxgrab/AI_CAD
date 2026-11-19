using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Commands
{
    /// <summary>Reads and writes the HVACDUCT dialog values in %APPDATA%\HvacAiCadAddin\HVACDUCT.settings.</summary>
    internal static class DuctSettingsStore
    {
        private static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HvacAiCadAddin",
                    "HVACDUCT.settings");
            }
        }

        internal static DuctDrawSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return DuctDrawSettings.Deserialize(File.ReadAllText(SettingsPath));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return new DuctDrawSettings();
        }

        internal static void Save(DuctDrawSettings settings)
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
    /// HVACDUCT dialog: shape, size, system layer, default segment length and the fitting factors.
    /// Sizes come from the drawing and the template; free entry is accepted. The last accepted values
    /// are remembered between runs.
    /// </summary>
    public sealed class DuctDrawForm : Form
    {
        private readonly RadioButton rectangular = new RadioButton { Text = "Rectangular", AutoSize = true };
        private readonly RadioButton round = new RadioButton { Text = "Round", AutoSize = true };
        private readonly ComboBox sizeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 180 };
        private readonly ComboBox layerBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        private readonly TextBox lengthBox = new TextBox { Width = 80 };
        private readonly Label errorLabel = new Label();
        private readonly List<DuctSize> knownSizes;
        private readonly List<KeyValuePair<string, TextBox>> factorBoxes = new List<KeyValuePair<string, TextBox>>();
        private bool updatingShape;

        internal DuctSize ChosenSize { get; private set; }
        internal DuctDrawSettings Settings { get; private set; }

        internal DuctDrawForm(IEnumerable<DuctSize> sizes, IEnumerable<string> layers)
        {
            knownSizes = new List<DuctSize>(sizes);
            DuctDrawSettings last = DuctSettingsStore.Load();

            Text = "HVACDUCT: draw duct run";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(560, 560);
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
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            FlowLayoutPanel shape = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            shape.Controls.Add(rectangular);
            shape.Controls.Add(round);
            AddRow(main, "Shape", shape);
            AddRow(main, "Size (600x300, D350)", sizeBox);

            layerBox.Items.AddRange(new List<string>(layers).ToArray());
            AddRow(main, "System layer", layerBox);
            AddRow(main, "Default segment length (mm)", lengthBox);

            Label header = new Label
            {
                Text = "Fitting factors (defaults from the approved fitting table)",
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(3, 14, 3, 4)
            };
            main.Controls.Add(header);
            main.SetColumnSpan(header, 2);

            FittingSettings f = last.Fittings;
            AddFactor(main, "Rectangular elbow R / W", "rectElbow", f.RectElbowRadiusFactor);
            AddFactor(main, "Round elbow R / d", "roundElbow", f.RoundElbowRadiusFactor);
            AddFactor(main, "Concentric transition L / delta", "concentricSlope", f.ConcentricSlope);
            AddFactor(main, "Transition minimum L / W_large", "transitionMin", f.TransitionMinLengthFactor);
            AddFactor(main, "Eccentric transition L / delta W", "eccentricSlope", f.EccentricSlope);
            AddFactor(main, "Shoe tap lead / Wb", "tapFactor", f.TapLeadFactor);
            AddFactor(main, "Shoe tap minimum lead (mm)", "tapMin", f.TapMinLead);
            AddFactor(main, "Round tap base extra (mm)", "roundTapExtra", f.RoundTapBaseExtra);
            AddFactor(main, "Round tap cone length (mm)", "roundTapCone", f.RoundTapConeLength);
            AddFactor(main, "Round tap collar (mm)", "roundTapCollar", f.RoundTapCollar);

            errorLabel.Dock = DockStyle.Bottom;
            errorLabel.Height = 38;
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

            // Initial values.
            updatingShape = true;
            rectangular.Checked = !last.IsRound;
            round.Checked = last.IsRound;
            updatingShape = false;
            rectangular.CheckedChanged += OnShapeChanged;
            round.CheckedChanged += OnShapeChanged;
            FillSizes(last.IsRound);
            sizeBox.Text = last.SizeText;
            lengthBox.Text = DuctSize.FormatNumber(last.SegmentLength);

            int layerIndex = layerBox.Items.IndexOf(last.Layer);
            if (layerIndex < 0)
                layerIndex = layerBox.Items.IndexOf("EIA_1 SUPPLY AIR");
            if (layerIndex < 0 && layerBox.Items.Count > 0)
                layerIndex = 0;
            layerBox.SelectedIndex = layerIndex;
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

        private void AddFactor(TableLayoutPanel table, string label, string key, double value)
        {
            TextBox box = new TextBox { Width = 80, Text = value.ToString("0.###", CultureInfo.InvariantCulture) };
            factorBoxes.Add(new KeyValuePair<string, TextBox>(key, box));
            AddRow(table, label, box);
        }

        private void FillSizes(bool roundShape)
        {
            sizeBox.Items.Clear();
            foreach (DuctSize size in knownSizes)
            {
                if (size.IsRound == roundShape)
                    sizeBox.Items.Add(size.Label);
            }
        }

        private void OnShapeChanged(object sender, EventArgs e)
        {
            if (updatingShape)
                return;

            bool roundShape = round.Checked;
            FillSizes(roundShape);
            DuctSize current;
            if (!DuctSize.TryParseText(sizeBox.Text, roundShape, out current))
                sizeBox.Text = sizeBox.Items.Count > 0 ? Convert.ToString(sizeBox.Items[0]) : "";
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
            DuctSize size;
            if (!DuctSize.TryParseText(sizeBox.Text, round.Checked, out size))
            {
                errorLabel.Text = round.Checked
                    ? "Size must be a diameter such as D350 (10 to 10000 mm)."
                    : "Size must be width x height such as 600x300 (10 to 10000 mm).";
                return;
            }

            if (layerBox.SelectedItem == null)
            {
                errorLabel.Text = "Choose a system layer.";
                return;
            }

            double length;
            if (!TryNumber(lengthBox.Text, out length) || length <= 0)
            {
                errorLabel.Text = "The default segment length must be a positive number.";
                return;
            }

            // Serialise the factors through the settings text so the parse and its validation are shared.
            DuctDrawSettings settings = new DuctDrawSettings
            {
                IsRound = round.Checked,
                SizeText = size.Label,
                Layer = Convert.ToString(layerBox.SelectedItem),
                SegmentLength = length
            };

            foreach (KeyValuePair<string, TextBox> pair in factorBoxes)
            {
                double value;
                if (!TryNumber(pair.Value.Text, out value))
                {
                    errorLabel.Text = "A fitting factor is not a number: " + pair.Value.Text;
                    return;
                }

                Apply(settings.Fittings, pair.Key, value);
            }

            string error;
            if (!settings.Fittings.IsValid(out error))
            {
                errorLabel.Text = error;
                return;
            }

            DuctSettingsStore.Save(settings);
            Settings = settings;
            ChosenSize = size;
            DialogResult = DialogResult.OK;
        }

        private static void Apply(FittingSettings f, string key, double value)
        {
            switch (key)
            {
                case "rectElbow": f.RectElbowRadiusFactor = value; break;
                case "roundElbow": f.RoundElbowRadiusFactor = value; break;
                case "concentricSlope": f.ConcentricSlope = value; break;
                case "transitionMin": f.TransitionMinLengthFactor = value; break;
                case "eccentricSlope": f.EccentricSlope = value; break;
                case "tapFactor": f.TapLeadFactor = value; break;
                case "tapMin": f.TapMinLead = value; break;
                case "roundTapExtra": f.RoundTapBaseExtra = value; break;
                case "roundTapCone": f.RoundTapConeLength = value; break;
                case "roundTapCollar": f.RoundTapCollar = value; break;
            }
        }
    }
}
