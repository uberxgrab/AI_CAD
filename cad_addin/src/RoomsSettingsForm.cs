using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using HvacAiCadAddin.Rooms;

namespace HvacAiCadAddin.Commands
{
    /// <summary>Reads and writes the HVACROOMS values in %APPDATA%\HvacAiCadAddin\HVACROOMS.settings.</summary>
    internal static class RoomsSettingsStore
    {
        private static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HvacAiCadAddin",
                    "HVACROOMS.settings");
            }
        }

        internal static RoomsSettings Load()
        {
            if (TestMode.Enabled)
                return TestMode.LoadSettings("HVACROOMS.settings", RoomsSettings.Deserialize, new RoomsSettings());

            try
            {
                if (File.Exists(SettingsPath))
                    return RoomsSettings.Deserialize(File.ReadAllText(SettingsPath));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return new RoomsSettings();
        }

        internal static void Save(RoomsSettings settings)
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
    /// HVACROOMS dialog: which of the layers in the selection are walls (walls, columns, glass, doors,
    /// facade...), the opening and room-size thresholds, the label height and where Python is.
    /// Layers ticked last time stay ticked; otherwise layers whose names suggest walls are ticked.
    /// </summary>
    public sealed class RoomsSettingsForm : Form
    {
        private readonly CheckedListBox layerList = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            IntegralHeight = false
        };
        private readonly List<string> layerNames = new List<string>();
        private readonly TextBox openingBox = new TextBox { Width = 90 };
        private readonly TextBox areaBox = new TextBox { Width = 90 };
        private readonly TextBox widthBox = new TextBox { Width = 90 };
        private readonly TextBox heightBox = new TextBox { Width = 90 };
        private readonly TextBox pythonBox = new TextBox { Width = 330 };
        private readonly TextBox scriptBox = new TextBox { Width = 330 };
        private readonly Label resolvedLabel = new Label { AutoSize = true, ForeColor = Color.DimGray };
        private readonly Label errorLabel = new Label();
        private readonly string addinFolder;

        internal RoomsSettings Settings { get; private set; }

        /// <summary>The script and interpreter the run will use (after OK).</summary>
        internal string ResolvedScript { get; private set; }
        internal string ResolvedPython { get; private set; }

        internal RoomsSettingsForm(IDictionary<string, int> layerCounts, string addinFolder)
        {
            this.addinFolder = addinFolder;
            RoomsSettings last = RoomsSettingsStore.Load();
            HashSet<string> remembered = new HashSet<string>(last.WallLayers, StringComparer.OrdinalIgnoreCase);

            Text = "HVACROOMS: find rooms in the selected plan";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(640, 640);
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(560, 520);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            // Layer list with its buttons.
            GroupBox layerGroup = new GroupBox
            {
                Text = "Wall layers: walls, columns, glass, doors, windows, facade (count = objects in the selection)",
                Dock = DockStyle.Fill,
                Padding = new Padding(8)
            };
            FlowLayoutPanel layerButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34 };
            Button suggested = new Button { Text = "Tick suggested", AutoSize = true };
            Button none = new Button { Text = "Untick all", AutoSize = true };
            suggested.Click += (sender, e) => TickSuggested();
            none.Click += (sender, e) =>
            {
                for (int i = 0; i < layerList.Items.Count; i++)
                    layerList.SetItemChecked(i, false);
            };
            layerButtons.Controls.Add(suggested);
            layerButtons.Controls.Add(none);
            layerButtons.Controls.Add(new Label
            {
                Text = "Lines on " + RoomsSettings.SplitLayer + " always count as walls.",
                AutoSize = true,
                Margin = new Padding(8, 8, 3, 3)
            });
            layerGroup.Controls.Add(layerList);
            layerGroup.Controls.Add(layerButtons);

            bool anyRemembered = false;
            foreach (KeyValuePair<string, int> pair in layerCounts)
            {
                if (RoomsExchangeCore.IsOwnLayer(pair.Key))
                    continue;
                layerNames.Add(pair.Key);
                layerList.Items.Add(pair.Key + "   (" + pair.Value.ToString(CultureInfo.InvariantCulture) + ")");
                if (remembered.Contains(pair.Key))
                    anyRemembered = true;
            }

            for (int i = 0; i < layerNames.Count; i++)
            {
                bool tick = anyRemembered ? remembered.Contains(layerNames[i]) : RoomsExchangeCore.SuggestWall(layerNames[i]);
                layerList.SetItemChecked(i, tick);
            }

            // Thresholds and Python.
            TableLayoutPanel options = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                ColumnCount = 2,
                Height = 230,
                Padding = new Padding(8, 4, 8, 0)
            };
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(options, "Widest opening closed (mm)", openingBox);
            AddRow(options, "Smallest room (m2)", areaBox);
            AddRow(options, "Narrowest room (mm); thinner = wall cavity", widthBox);
            AddRow(options, "Label text height", heightBox);
            AddRow(options, "python.exe (blank = repository venv)", pythonBox);
            AddRow(options, "arch_rooms.py (blank = found automatically)", scriptBox);
            options.Controls.Add(resolvedLabel);
            options.SetColumnSpan(resolvedLabel, 2);
            pythonBox.TextChanged += (sender, e) => ShowResolved();
            scriptBox.TextChanged += (sender, e) => ShowResolved();

            errorLabel.Dock = DockStyle.Bottom;
            errorLabel.Height = 40;
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

            Controls.Add(layerGroup);
            Controls.Add(options);
            Controls.Add(errorLabel);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;

            openingBox.Text = Format(last.MaxOpening);
            areaBox.Text = Format(last.MinRoomAreaM2);
            widthBox.Text = Format(last.MinRoomWidth);
            heightBox.Text = Format(last.TextHeight);
            pythonBox.Text = last.PythonExe;
            scriptBox.Text = last.ScriptPath;
            ShowResolved();
        }

        private void TickSuggested()
        {
            for (int i = 0; i < layerNames.Count; i++)
                layerList.SetItemChecked(i, RoomsExchangeCore.SuggestWall(layerNames[i]));
        }

        private void Resolve(out string script, out string python)
        {
            script = scriptBox.Text.Trim();
            if (script.Length == 0)
                script = RoomsExchangeCore.FindScript(addinFolder);
            python = pythonBox.Text.Trim();
            if (python.Length == 0)
                python = RoomsExchangeCore.DefaultPython(script);
        }

        private void ShowResolved()
        {
            string script;
            string python;
            Resolve(out script, out python);
            resolvedLabel.Text = "Runs: " + (python ?? "(python not found)") + "  " + (script ?? "(" + RoomsExchangeCore.ScriptName + " not found)");
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static void AddRow(TableLayoutPanel table, string label, Control control)
        {
            table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
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
            RoomsSettings settings = new RoomsSettings();
            for (int i = 0; i < layerNames.Count; i++)
            {
                if (layerList.GetItemChecked(i))
                    settings.WallLayers.Add(layerNames[i]);
            }

            if (!TryNumber(openingBox.Text, out settings.MaxOpening)
                || !TryNumber(areaBox.Text, out settings.MinRoomAreaM2)
                || !TryNumber(widthBox.Text, out settings.MinRoomWidth)
                || !TryNumber(heightBox.Text, out settings.TextHeight))
            {
                errorLabel.Text = "The thresholds and the text height must be numbers.";
                return;
            }

            settings.PythonExe = pythonBox.Text.Trim();
            settings.ScriptPath = scriptBox.Text.Trim();
            string error;
            if (!settings.IsValid(out error))
            {
                errorLabel.Text = error;
                return;
            }

            string script;
            string python;
            Resolve(out script, out python);
            if (script == null || !File.Exists(script))
            {
                errorLabel.Text = RoomsExchangeCore.ScriptName + " was not found; enter its path.";
                return;
            }

            if (python == null || !File.Exists(python))
            {
                errorLabel.Text = "python.exe was not found at " + (python ?? "(none)") + "; enter its path.";
                return;
            }

            // Remember the ticks together with layers ticked in other drawings that are not listed now.
            RoomsSettings last = RoomsSettingsStore.Load();
            HashSet<string> listed = new HashSet<string>(layerNames, StringComparer.OrdinalIgnoreCase);
            RoomsSettings stored = RoomsSettings.Deserialize(settings.Serialize());
            foreach (string layer in last.WallLayers)
            {
                if (!listed.Contains(layer) && !stored.WallLayers.Contains(layer))
                    stored.WallLayers.Add(layer);
            }

            RoomsSettingsStore.Save(stored);
            Settings = settings;
            ResolvedScript = script;
            ResolvedPython = python;
            DialogResult = DialogResult.OK;
        }
    }
}
