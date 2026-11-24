using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HvacAiCadAddin.Norm;

namespace HvacAiCadAddin.Commands
{
    /// <summary>Reads and writes the HVACNORM values in %APPDATA%\HvacAiCadAddin\HVACNORM.settings.</summary>
    internal static class NormSettingsStore
    {
        private static string SettingsPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HvacAiCadAddin", "HVACNORM.settings");
            }
        }

        internal static NormSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return NormSettings.Deserialize(File.ReadAllText(SettingsPath));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return new NormSettings();
        }

        internal static void Save(NormSettings settings)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath, settings.Serialize());
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        internal static void Resolve(NormSettings settings, string addinFolder, out string script, out string python)
        {
            script = (settings.ScriptPath ?? "").Trim();
            if (script.Length == 0)
                script = NormExchangeCore.FindScript(addinFolder);
            python = (settings.PythonExe ?? "").Trim();
            if (python.Length == 0)
                python = NormExchangeCore.DefaultPython(script);
        }
    }

    /// <summary>
    /// HVACNORM dialog: which system each layer of the selection carries (pre-filled from the saved project
    /// map or from the Python proposal), the project map name to save, the lowest confidence whose size is
    /// applied, and the fixes to make.
    /// </summary>
    public sealed class NormForm : Form
    {
        private const string NoSystem = "";

        private readonly DataGridView grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.CellSelect
        };
        private readonly ComboBox mapBox = new ComboBox { Width = 260, DropDownStyle = ComboBoxStyle.DropDown };
        private readonly Button loadButton = new Button { Text = "Load", AutoSize = true };
        private readonly CheckBox saveBox = new CheckBox { Text = "Save this map under the name", AutoSize = true, Checked = true };
        private readonly ComboBox confidenceBox = new ComboBox { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckBox moveBox = new CheckBox { AutoSize = true, Text = "Move pipes of the BOQ material to their existing EIA_1 layer" };
        private readonly CheckBox overridesBox = new CheckBox { AutoSize = true, Text = "Reset colour / linetype / lineweight overrides to ByLayer" };
        private readonly CheckBox duplicatesBox = new CheckBox { AutoSize = true, Text = "Erase pipes drawn twice" };
        private readonly TextBox pythonBox = new TextBox { Width = 380 };
        private readonly TextBox scriptBox = new TextBox { Width = 380 };
        private readonly Label infoLabel = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(700, 0) };
        private readonly Label errorLabel = new Label();
        private readonly string mapsFolder;
        private readonly string addinFolder;

        internal NormSettings Settings { get; private set; }
        internal Dictionary<string, string> Systems { get; private set; }
        /// <summary>The map name to save (null = do not save).</summary>
        internal string SaveName { get; private set; }
        internal string ResolvedScript { get; private set; }
        internal string ResolvedPython { get; private set; }

        internal NormForm(IDictionary<string, int> layerCounts, NormResult first, string mapsFolder, string addinFolder,
            NormSettings last)
        {
            this.mapsFolder = mapsFolder;
            this.addinFolder = addinFolder;
            Text = "HVACNORM: normalise the pipes and single-line ducts of another office";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(760, 700);
            MinimumSize = new Size(640, 560);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            Dictionary<string, string> systems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> hits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (first != null)
            {
                foreach (KeyValuePair<string, NormMapEntry> pair in first.Map)
                {
                    if (pair.Value != null && !string.IsNullOrEmpty(pair.Value.System))
                        systems[pair.Key] = pair.Value.System.ToUpperInvariant();
                }

                foreach (NormProposalRow row in first.Proposal ?? new NormProposalRow[0])
                    hits[row.Layer] = row.LabelHits;
            }

            // Layer grid.
            DataGridViewTextBoxColumn layerColumn = new DataGridViewTextBoxColumn { HeaderText = "Layer", ReadOnly = true, FillWeight = 50 };
            DataGridViewTextBoxColumn countColumn = new DataGridViewTextBoxColumn { HeaderText = "Objects", ReadOnly = true, FillWeight = 12 };
            DataGridViewTextBoxColumn hitColumn = new DataGridViewTextBoxColumn { HeaderText = "Size labels", ReadOnly = true, FillWeight = 12 };
            DataGridViewComboBoxColumn systemColumn = new DataGridViewComboBoxColumn
            {
                HeaderText = "System",
                FillWeight = 40,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
                FlatStyle = FlatStyle.Flat
            };
            systemColumn.Items.Add(NoSystem);
            foreach (string[] code in NormSystems.Codes)
                systemColumn.Items.Add(code[0] + " - " + code[1]);
            grid.Columns.AddRange(layerColumn, countColumn, hitColumn, systemColumn);
            IEnumerable<KeyValuePair<string, int>> ordered = layerCounts
                .Where(p => !p.Key.StartsWith("EIA_", StringComparison.OrdinalIgnoreCase) || systems.ContainsKey(p.Key))
                .OrderBy(p => systems.ContainsKey(p.Key) ? 0 : 1)
                .ThenByDescending(p => hits.ContainsKey(p.Key) ? hits[p.Key] : 0)
                .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, int> pair in ordered)
            {
                string code;
                systems.TryGetValue(pair.Key, out code);
                int hit;
                hits.TryGetValue(pair.Key, out hit);
                grid.Rows.Add(pair.Key, pair.Value.ToString(CultureInfo.InvariantCulture),
                    hit > 0 ? hit.ToString(CultureInfo.InvariantCulture) : "", ItemFor(code));
            }

            GroupBox layerGroup = new GroupBox
            {
                Text = "System of each layer (blank = not a pipe or duct layer). Catalogue layers (EIA_1 ... DN50) are read by themselves.",
                Dock = DockStyle.Fill,
                Padding = new Padding(8)
            };
            layerGroup.Controls.Add(grid);

            // Options.
            TableLayoutPanel options = new TableLayoutPanel { Dock = DockStyle.Bottom, ColumnCount = 2, Height = 300, Padding = new Padding(8, 4, 8, 0) };
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            FlowLayoutPanel mapRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            mapRow.Controls.Add(mapBox);
            mapRow.Controls.Add(loadButton);
            AddRow(options, "Project map", mapRow);
            AddRow(options, "", saveBox);
            AddRow(options, "Apply sizes guessed with at least", confidenceBox);
            AddRow(options, "", moveBox);
            AddRow(options, "", overridesBox);
            AddRow(options, "", duplicatesBox);
            AddRow(options, "python.exe (blank = repository venv)", pythonBox);
            AddRow(options, NormExchangeCore.ScriptName + " (blank = found automatically)", scriptBox);
            options.Controls.Add(infoLabel);
            options.SetColumnSpan(infoLabel, 2);

            foreach (string name in NormMapStore.List(mapsFolder))
                mapBox.Items.Add(name);
            mapBox.Text = last.MapName;
            loadButton.Click += (sender, e) => LoadMap();
            confidenceBox.Items.AddRange(new object[]
            {
                "high: a label points at the pipe",
                "medium: also sizes carried along a straight run",
                "low: also sizes carried across branches (check the preview)"
            });
            confidenceBox.SelectedIndex = NormPlanner.Rank(last.MinConfidence) - 1;
            moveBox.Checked = last.MoveToStandard;
            overridesBox.Checked = last.FixOverrides;
            duplicatesBox.Checked = last.EraseDuplicates;
            pythonBox.Text = last.PythonExe;
            scriptBox.Text = last.ScriptPath;
            infoLabel.Text = Describe(first);

            errorLabel.Dock = DockStyle.Bottom;
            errorLabel.Height = 36;
            errorLabel.Padding = new Padding(10, 4, 10, 0);
            errorLabel.ForeColor = Color.Firebrick;

            Button ok = new Button { Text = "OK" };
            ok.Click += OnOkClick;
            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);

            Controls.Add(layerGroup);
            Controls.Add(options);
            Controls.Add(errorLabel);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private static string Describe(NormResult first)
        {
            if (first == null)
                return "No preview yet: Python did not run (check the paths below).";
            string source = first.MapProposed
                ? "The systems below were PROPOSED from the size labels near each layer; check them."
                : "The systems below come from the saved project map.";
            if (first.Stats == null)
                return source;
            double sized = 0;
            if (first.Stats.ByConfidencePct != null)
            {
                foreach (KeyValuePair<string, double> pair in first.Stats.ByConfidencePct)
                {
                    if (pair.Key != "none")
                        sized += pair.Value;
                }
            }

            return source + string.Format(CultureInfo.InvariantCulture,
                " With this map: {0:0.#} m of pipe/duct, {1:0}% sized, {2}/{3} size labels used.",
                first.Stats.LengthM, sized, first.Stats.LabelsUsed, first.Stats.Labels);
        }

        private static string ItemFor(string code)
        {
            if (string.IsNullOrEmpty(code))
                return NoSystem;
            string[] known = NormSystems.Codes.FirstOrDefault(c => string.Equals(c[0], code, StringComparison.OrdinalIgnoreCase));
            return known == null ? NoSystem : known[0] + " - " + known[1];
        }

        private static string CodeOf(object item)
        {
            string text = Convert.ToString(item, CultureInfo.InvariantCulture) ?? "";
            int dash = text.IndexOf(" - ", StringComparison.Ordinal);
            return (dash > 0 ? text.Substring(0, dash) : text).Trim().ToUpperInvariant();
        }

        private void LoadMap()
        {
            string name = mapBox.Text.Trim();
            NormMap map;
            try
            {
                map = NormMapStore.Load(mapsFolder, name);
            }
            catch (Exception ex)
            {
                errorLabel.Text = "Could not load the map " + name + ": " + ex.Message;
                return;
            }

            Dictionary<string, string> systems = map.Systems();
            foreach (DataGridViewRow row in grid.Rows)
            {
                string code;
                systems.TryGetValue(Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture), out code);
                row.Cells[3].Value = ItemFor(code);
            }

            errorLabel.Text = "";
            infoLabel.Text = "Loaded map " + name + " (" + systems.Count + " layers). The preview is recomputed after OK.";
        }

        private static void AddRow(TableLayoutPanel table, string label, Control control)
        {
            table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
            table.Controls.Add(control);
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            grid.EndEdit();
            Dictionary<string, string> systems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataGridViewRow row in grid.Rows)
            {
                string code = CodeOf(row.Cells[3].Value);
                if (code.Length > 0)
                    systems[Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture)] = code;
            }

            if (systems.Count == 0)
            {
                errorLabel.Text = "Give at least one layer a system.";
                return;
            }

            string name = mapBox.Text.Trim();
            if (saveBox.Checked && !NormMapStore.IsValidName(name))
            {
                errorLabel.Text = "Enter a project map name (letters, digits, spaces, '-', '_', '.'), or untick Save.";
                return;
            }

            NormSettings settings = new NormSettings
            {
                MapName = saveBox.Checked ? name : "",
                MinConfidence = new[] { "high", "medium", "low" }[Math.Max(0, confidenceBox.SelectedIndex)],
                MoveToStandard = moveBox.Checked,
                FixOverrides = overridesBox.Checked,
                EraseDuplicates = duplicatesBox.Checked,
                PythonExe = pythonBox.Text.Trim(),
                ScriptPath = scriptBox.Text.Trim()
            };
            string script;
            string python;
            NormSettingsStore.Resolve(settings, addinFolder, out script, out python);
            if (script == null || !File.Exists(script))
            {
                errorLabel.Text = NormExchangeCore.ScriptName + " was not found; enter its path.";
                return;
            }

            if (python == null || !File.Exists(python))
            {
                errorLabel.Text = "python.exe was not found at " + (python ?? "(none)") + "; enter its path.";
                return;
            }

            NormSettingsStore.Save(settings);
            Settings = settings;
            Systems = systems;
            SaveName = saveBox.Checked ? name : null;
            ResolvedScript = script;
            ResolvedPython = python;
            DialogResult = DialogResult.OK;
        }
    }
}
