using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using HvacAiCadAddin.Pipes;

namespace HvacAiCadAddin.Vrv
{
    /// <summary>Reads and writes the HVACPLACEODU values in %APPDATA%\HvacAiCadAddin\HVACPLACEODU.settings.</summary>
    internal static class OduSettingsStore
    {
        private static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HvacAiCadAddin",
                    "HVACPLACEODU.settings");
            }
        }

        internal static OduSettings Load()
        {
            if (TestMode.Enabled)
                return TestMode.LoadSettings("HVACPLACEODU.settings", OduSettings.Deserialize, new OduSettings());

            try
            {
                if (File.Exists(SettingsPath))
                    return OduSettings.Deserialize(File.ReadAllText(SettingsPath));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return new OduSettings();
        }

        internal static void Save(OduSettings settings)
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
    /// HVACPLACEODU dialog: the selected indoor units (block, index, kW), their total, the outdoor unit HP
    /// (the smallest that covers the total by default, editable), the connection ratio with its warnings,
    /// the block that will be inserted, the model text and the label text height.
    /// </summary>
    internal sealed class OduForm : Form
    {
        private readonly IList<OduIndoorUnit> units;
        private readonly Func<int, OduBlockChoice> resolve;
        private readonly ListView list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };

        private readonly ComboBox hpBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 80 };
        private readonly TextBox modelBox = new TextBox { Width = 160 };
        private readonly TextBox heightBox = new TextBox { Width = 70 };
        private readonly CheckBox labelBox = new CheckBox { Text = "Write the label", AutoSize = true };
        private readonly Label totalLabel = new Label { AutoSize = true };
        private readonly Label ratioLabel = new Label { AutoSize = true };
        private readonly Label blockLabel = new Label { AutoSize = true };
        private readonly TextBox messages = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Control
        };

        private readonly Label errorLabel = new Label { AutoSize = true, ForeColor = Color.Firebrick };
        private readonly int total;
        private bool updating;

        internal OduSettings Settings { get; private set; }
        internal OduPlan Plan { get; private set; }
        internal OduBlockChoice Block { get; private set; }

        internal OduForm(IList<OduIndoorUnit> units, Func<int, OduBlockChoice> resolve)
        {
            this.units = units;
            this.resolve = resolve;
            total = OduPlanner.Sum(units);
            OduSettings last = OduSettingsStore.Load();

            Text = "HVACPLACEODU: outdoor unit";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(640, 560);
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(560, 480);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            list.Columns.Add("Block", 230);
            list.Columns.Add("Index", 60);
            list.Columns.Add("kW", 60);
            list.Columns.Add("Source", 120);
            foreach (OduIndoorUnit unit in units)
            {
                ListViewItem item = new ListViewItem(unit.BlockName ?? "");
                item.SubItems.Add(unit.Index.ToString(CultureInfo.InvariantCulture));
                item.SubItems.Add(unit.Kw.ToString("0.0#", CultureInfo.InvariantCulture));
                item.SubItems.Add(unit.Source);
                list.Items.Add(item);
            }

            foreach (int hp in VrvTables.HpChoices)
                hpBox.Items.Add(hp.ToString(CultureInfo.InvariantCulture));
            hpBox.Text = VrvTables.DefaultHp(total).ToString(CultureInfo.InvariantCulture);
            modelBox.Text = last.ModelText;
            heightBox.Text = last.TextHeight.ToString("0.##", CultureInfo.InvariantCulture);
            labelBox.Checked = last.WriteLabel;

            FlowLayoutPanel options = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 70,
                Padding = new Padding(6, 6, 6, 0)
            };
            AddOption(options, "Outdoor unit (HP)", hpBox);
            AddOption(options, "Model text", modelBox);
            AddOption(options, "Text height (mm)", heightBox);
            options.Controls.Add(labelBox);

            FlowLayoutPanel summary = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 74,
                FlowDirection = FlowDirection.TopDown,
                Padding = new Padding(8, 4, 6, 0)
            };
            summary.Controls.Add(totalLabel);
            summary.Controls.Add(ratioLabel);
            summary.Controls.Add(blockLabel);

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(6)
            };
            Button ok = new Button { Text = "OK", DialogResult = DialogResult.None, Width = 90 };
            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
            ok.Click += OnOkClick;
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            buttons.Controls.Add(errorLabel);
            AcceptButton = ok;
            CancelButton = cancel;

            Panel messagePanel = new Panel { Dock = DockStyle.Bottom, Height = 150, Padding = new Padding(6) };
            messagePanel.Controls.Add(messages);

            Controls.Add(list);
            Controls.Add(messagePanel);
            Controls.Add(buttons);
            Controls.Add(summary);
            Controls.Add(options);

            hpBox.TextChanged += (sender, e) => Recompute();
            hpBox.SelectedIndexChanged += (sender, e) => Recompute();
            Recompute();
        }

        private static void AddOption(FlowLayoutPanel panel, string caption, Control control)
        {
            FlowLayoutPanel group = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };
            group.Controls.Add(new Label { Text = caption, AutoSize = true });
            group.Controls.Add(control);
            panel.Controls.Add(group);
        }

        // The HP typed or chosen, 0 when the text is not an HP of the table.
        private int CurrentHp()
        {
            int hp;
            return int.TryParse(hpBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out hp)
                && VrvTables.IsHp(hp) ? hp : 0;
        }

        private void Recompute()
        {
            if (updating)
                return;
            updating = true;
            try
            {
                int hp = CurrentHp();
                OduPlan plan = OduPlanner.Plan(units.Count, total, hp);
                OduPlanner.AddUnitNotes(plan, units);
                OduBlockChoice block = resolve(plan.Hp);

                totalLabel.Text = units.Count.ToString(CultureInfo.InvariantCulture) + " indoor units, total index "
                    + total.ToString(CultureInfo.InvariantCulture);
                ratioLabel.Text = "Outdoor " + plan.Hp.ToString(CultureInfo.InvariantCulture) + " HP (index "
                    + plan.OduIndex.ToString(CultureInfo.InvariantCulture) + "), connection ratio "
                    + plan.Ratio.ToString("0", CultureInfo.InvariantCulture) + " %"
                    + (plan.RatioOutside ? "  (outside 50-130 %)" : "");
                ratioLabel.ForeColor = plan.RatioOutside ? Color.Firebrick : SystemColors.ControlText;
                blockLabel.Text = "Block: " + block.BlockName + (block.Substitute ? "  (substitute)" : "");

                List<string> lines = new List<string>();
                foreach (string warning in plan.Warnings)
                    lines.Add("Warning: " + warning);
                if (block.Warning != null)
                    lines.Add("Warning: " + block.Warning);
                foreach (string note in plan.Notes)
                    lines.Add("Note: " + note);
                messages.Text = string.Join(Environment.NewLine, lines);
                errorLabel.Text = hpBox.Text.Trim().Length > 0 && hp == 0 && !string.IsNullOrEmpty(hpBox.Text)
                    ? "Not an outdoor unit size: the smallest covering HP is used."
                    : "";
            }
            finally
            {
                updating = false;
            }
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            double height;
            if (!double.TryParse(heightBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out height)
                && !double.TryParse(heightBox.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out height))
            {
                errorLabel.Text = "The text height must be a number.";
                return;
            }

            OduSettings settings = new OduSettings
            {
                TextHeight = height,
                ModelText = modelBox.Text.Trim(),
                WriteLabel = labelBox.Checked
            };
            string error;
            if (!settings.IsValid(out error))
            {
                errorLabel.Text = error;
                return;
            }

            OduPlan plan = OduPlanner.Plan(units.Count, total, CurrentHp());
            OduPlanner.AddUnitNotes(plan, units);
            if (plan.IndoorTotal <= 0)
            {
                errorLabel.Text = "There is no indoor capacity to serve.";
                return;
            }

            OduSettingsStore.Save(settings);
            Settings = settings;
            Plan = plan;
            Block = resolve(plan.Hp);
            DialogResult = DialogResult.OK;
        }
    }
}
