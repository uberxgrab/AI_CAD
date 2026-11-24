using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Commands
{
    /// <summary>Reads and writes the HVACDUCTLINE values in %APPDATA%\HvacAiCadAddin\HVACDUCTLINE.settings.</summary>
    internal static class DuctLineSettingsStore
    {
        private static string SettingsPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HvacAiCadAddin", "HVACDUCTLINE.settings");
            }
        }

        internal static DuctLineOptions Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return DuctLineOptions.Deserialize(File.ReadAllText(SettingsPath));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return new DuctLineOptions();
        }

        internal static void Save(DuctLineOptions options)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath, options.Serialize());
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// HVACDUCTLINE dialog: the size and layer of lines without an HVACNORM tag, the weld tolerance and
    /// whether the single lines are erased. Elbow, transition and tap rules are those of HVACDUCT.
    /// </summary>
    public sealed class DuctLineForm : Form
    {
        private readonly TextBox sizeBox = new TextBox { Width = 160 };
        private readonly ComboBox layerBox = new ComboBox { Width = 320, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox weldBox = new TextBox { Width = 160 };
        private readonly CheckBox eraseBox = new CheckBox { AutoSize = true, Text = "Erase the single lines after drawing (otherwise kept, without size tag)" };
        private readonly Label errorLabel = new Label { AutoSize = true, ForeColor = Color.Firebrick };

        internal DuctLineOptions Options { get; private set; }

        internal DuctLineForm(IList<string> ductLayers, string summary)
        {
            DuctLineOptions last = DuctLineSettingsStore.Load();
            Text = "HVACDUCTLINE: draw ducts from single lines";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            TableLayoutPanel table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(10) };
            Label info = new Label { Text = summary, AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Color.DimGray };
            table.Controls.Add(info);
            table.SetColumnSpan(info, 2);
            AddRow(table, "Size of lines without a size tag", sizeBox);
            AddRow(table, "Layer of runs with no known system", layerBox);
            AddRow(table, "Join ends closer than (mm)", weldBox);
            table.Controls.Add(new Label());
            table.Controls.Add(eraseBox);
            Label rules = new Label
            {
                Text = "Sizes come from HVACNORM tags (run HVACNORM first); systems SA, RA, OA, EA... go on their EIA layers. "
                    + "Elbows, transitions and taps follow the HVACDUCT settings.",
                AutoSize = true,
                MaximumSize = new Size(560, 0),
                ForeColor = Color.DimGray
            };
            table.Controls.Add(rules);
            table.SetColumnSpan(rules, 2);
            table.Controls.Add(errorLabel);
            table.SetColumnSpan(errorLabel, 2);

            Button ok = new Button { Text = "OK" };
            ok.Click += OnOkClick;
            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            FlowLayoutPanel buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            table.Controls.Add(buttons);
            table.SetColumnSpan(buttons, 2);
            Controls.Add(table);
            AcceptButton = ok;
            CancelButton = cancel;

            foreach (string layer in ductLayers)
                layerBox.Items.Add(layer);
            int index = layerBox.Items.IndexOf(last.Layer);
            if (index < 0)
                index = layerBox.Items.IndexOf("EIA_1 SUPPLY AIR");
            if (layerBox.Items.Count > 0)
                layerBox.SelectedIndex = Math.Max(0, index);
            sizeBox.Text = last.SizeText;
            weldBox.Text = last.WeldTolerance.ToString("0.###", CultureInfo.InvariantCulture);
            eraseBox.Checked = last.EraseLines;
        }

        private static void AddRow(TableLayoutPanel table, string label, Control control)
        {
            table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 12, 3) });
            table.Controls.Add(control);
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            double weld;
            if (!double.TryParse(weldBox.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out weld))
            {
                errorLabel.Text = "The weld tolerance must be a number.";
                return;
            }

            DuctLineOptions options = new DuctLineOptions
            {
                SizeText = sizeBox.Text.Trim(),
                Layer = layerBox.SelectedItem as string ?? "",
                WeldTolerance = weld,
                EraseLines = eraseBox.Checked
            };
            string error;
            if (!options.IsValid(out error))
            {
                errorLabel.Text = error;
                return;
            }

            if (options.Layer.Length == 0)
            {
                errorLabel.Text = "No duct layer is available (the template was not found).";
                return;
            }

            DuctLineSettingsStore.Save(options);
            Options = options;
            DialogResult = DialogResult.OK;
        }
    }
}
