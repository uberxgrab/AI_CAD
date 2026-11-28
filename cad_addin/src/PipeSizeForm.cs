using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using HvacAiCadAddin.Pipes;

namespace HvacAiCadAddin.Commands
{
    /// <summary>Reads and writes the HVACPIPESIZE values in %APPDATA%\HvacAiCadAddin\HVACPIPESIZE.settings.</summary>
    internal static class PipeSizeSettingsStore
    {
        private static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HvacAiCadAddin",
                    "HVACPIPESIZE.settings");
            }
        }

        internal static PipeSizeSettings Load()
        {
            if (TestMode.Enabled)
                return TestMode.LoadSettings("HVACPIPESIZE.settings", PipeSizeSettings.Deserialize, new PipeSizeSettings());

            try
            {
                if (File.Exists(SettingsPath))
                    return PipeSizeSettings.Deserialize(File.ReadAllText(SettingsPath));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return new PipeSizeSettings();
        }

        internal static void Save(PipeSizeSettings settings)
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
    /// HVACPIPESIZE dialog: the pipe type and size for the highlighted run. The last accepted values
    /// are preselected.
    /// </summary>
    public sealed class PipeSizeForm : Form
    {
        private readonly ComboBox typeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly ComboBox sizeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly Label layerLabel = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
        private readonly Label errorLabel = new Label();
        private readonly PipeSizeSettings last;
        private bool filling;

        internal PipeKind ChosenKind { get; private set; }
        internal string ChosenSize { get; private set; }

        internal PipeSizeForm(PipeSizeSettings remembered, string runSummary, string currentLayer)
        {
            last = remembered;

            Text = "HVACPIPESIZE: size a pipe run";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(460, 250);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            TableLayoutPanel main = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(10)
            };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            Label summary = new Label
            {
                Text = runSummary + "\nNow on layer: " + currentLayer,
                AutoSize = true,
                Margin = new Padding(3, 3, 3, 10)
            };
            main.Controls.Add(summary);
            main.SetColumnSpan(summary, 2);

            foreach (PipeKind kind in PipeCatalog.Kinds)
                typeBox.Items.Add(PipeCatalog.Label(kind));
            AddRow(main, "Pipe type", typeBox);
            AddRow(main, "Size", sizeBox);
            AddRow(main, "New layer", layerLabel);

            errorLabel.Dock = DockStyle.Bottom;
            errorLabel.Height = 34;
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

            filling = true;
            typeBox.SelectedIndex = Math.Max(0, new List<PipeKind>(PipeCatalog.Kinds).IndexOf(last.Kind));
            FillSizes(SelectedKind(), last.SizeText);
            filling = false;
            typeBox.SelectedIndexChanged += OnTypeChanged;
            sizeBox.SelectedIndexChanged += OnSizeChanged;
            ShowLayerName();
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

        private PipeKind SelectedKind()
        {
            int index = Math.Max(0, typeBox.SelectedIndex);
            return PipeCatalog.Kinds[index];
        }

        private void FillSizes(PipeKind kind, string preferred)
        {
            sizeBox.Items.Clear();
            foreach (string text in PipeCatalog.SizeTexts(kind))
                sizeBox.Items.Add(text);

            int index = sizeBox.Items.IndexOf(preferred ?? "");
            sizeBox.SelectedIndex = index >= 0 ? index : 0;
        }

        private void OnTypeChanged(object sender, EventArgs e)
        {
            if (filling)
                return;
            FillSizes(SelectedKind(), Convert.ToString(sizeBox.SelectedItem));
            ShowLayerName();
        }

        private void OnSizeChanged(object sender, EventArgs e)
        {
            if (!filling)
                ShowLayerName();
        }

        private void ShowLayerName()
        {
            string name = PipeCatalog.LayerName(SelectedKind(), Convert.ToString(sizeBox.SelectedItem));
            layerLabel.Text = name ?? "";
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            PipeKind kind = SelectedKind();
            string size = Convert.ToString(sizeBox.SelectedItem);
            if (PipeCatalog.LayerName(kind, size) == null)
            {
                errorLabel.Text = "Choose a size.";
                return;
            }

            last.Kind = kind;
            last.SizeText = size;
            PipeSizeSettingsStore.Save(last);
            ChosenKind = kind;
            ChosenSize = size;
            DialogResult = DialogResult.OK;
        }
    }
}
