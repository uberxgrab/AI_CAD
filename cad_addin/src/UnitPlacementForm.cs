using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using HvacAiCadAddin.Units;

namespace HvacAiCadAddin.Commands
{
    /// <summary>A room read from the drawing: HVACAI_ROOM polyline plus the optional HVACAI_LOAD load.</summary>
    internal sealed class RoomUnitInput
    {
        public string Id;
        public string Name;
        public double AreaM2;

        /// <summary>The load in kW, or NaN when the room has no HVACAI_LOAD tag.</summary>
        public double LoadKw = double.NaN;

        public double[][] Polygon;
    }

    /// <summary>A room the user chose to fill, with the units the dialog settled on.</summary>
    internal sealed class RoomUnitPlan
    {
        public RoomUnitInput Room;
        public UnitChoice Choice;
    }

    /// <summary>Reads and writes the HVACPLACEUNITS values in %APPDATA%\HvacAiCadAddin\HVACPLACEUNITS.settings.</summary>
    internal static class UnitSettingsStore
    {
        private static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HvacAiCadAddin",
                    "HVACPLACEUNITS.settings");
            }
        }

        internal static UnitSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return UnitSettings.Deserialize(File.ReadAllText(SettingsPath));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return new UnitSettings();
        }

        internal static void Save(UnitSettings settings)
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
    /// HVACPLACEUNITS dialog: one row per room with its load (typed when the room has none), the unit
    /// chosen for it and the number of units (editable), plus the unit family, the largest unit index, the
    /// tolerance and the wall clearance. Rows without a load stay unticked until a load is typed.
    /// </summary>
    internal sealed class UnitPlacementForm : Form
    {
        private const int ColUse = 0, ColId = 1, ColName = 2, ColArea = 3, ColLoad = 4, ColUnits = 5, ColChosen = 6, ColNote = 7;

        private readonly DataGridView grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None
        };

        private readonly ComboBox familyBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        private readonly ComboBox maxIndexBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        private readonly TextBox toleranceBox = new TextBox { Width = 70 };
        private readonly TextBox maxUnitsBox = new TextBox { Width = 70 };
        private readonly TextBox clearanceBox = new TextBox { Width = 70 };
        private readonly Label errorLabel = new Label();
        private readonly Label summaryLabel = new Label { AutoSize = true, ForeColor = Color.DimGray };
        private readonly IList<RoomUnitInput> rooms;
        private readonly UnitChoice[] choices;
        private bool updating;

        internal UnitSettings Settings { get; private set; }

        /// <summary>The ticked rooms that have a unit choice (after OK).</summary>
        internal List<RoomUnitPlan> Plans { get; private set; }

        internal UnitPlacementForm(IList<RoomUnitInput> rooms)
        {
            this.rooms = rooms;
            choices = new UnitChoice[rooms.Count];
            UnitSettings last = UnitSettingsStore.Load();

            Text = "HVACPLACEUNITS: indoor units in rooms";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(900, 560);
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(760, 420);
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            // Options on top.
            FlowLayoutPanel options = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 66, Padding = new Padding(6, 6, 6, 0) };
            foreach (UnitFamily family in UnitCatalog.Families)
                familyBox.Items.Add(family.Name);
            familyBox.SelectedItem = UnitCatalog.Find(last.Family).Name;
            if (familyBox.SelectedIndex < 0)
                familyBox.SelectedIndex = 0;
            FillMaxIndex(last.MaxIndex);
            toleranceBox.Text = (last.Tolerance * 100).ToString("0.##", CultureInfo.InvariantCulture);
            maxUnitsBox.Text = last.MaxUnitsPerRoom.ToString(CultureInfo.InvariantCulture);
            clearanceBox.Text = last.Clearance.ToString("0.##", CultureInfo.InvariantCulture);
            AddOption(options, "Unit block", familyBox);
            AddOption(options, "Largest unit", maxIndexBox);
            AddOption(options, "May be under load (%)", toleranceBox);
            AddOption(options, "Most units per room", maxUnitsBox);
            AddOption(options, "Wall clearance (mm)", clearanceBox);
            familyBox.SelectedIndexChanged += (sender, e) =>
            {
                FillMaxIndex(SelectedMaxIndex());
                RecomputeAll();
            };
            maxIndexBox.SelectedIndexChanged += (sender, e) => RecomputeAll();
            toleranceBox.TextChanged += (sender, e) => RecomputeAll();
            maxUnitsBox.TextChanged += (sender, e) => RecomputeAll();

            // Room grid.
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Use", Width = 40 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Room", Width = 70, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", Width = 170, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Area m2", Width = 70, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Load kW", Width = 70 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Units", Width = 50 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Chosen", Width = 220, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Note", Width = 200, ReadOnly = true });
            grid.CellValueChanged += OnCellValueChanged;
            grid.CurrentCellDirtyStateChanged += (sender, e) =>
            {
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell)
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };

            updating = true;
            for (int i = 0; i < rooms.Count; i++)
            {
                RoomUnitInput room = rooms[i];
                bool hasLoad = !double.IsNaN(room.LoadKw) && room.LoadKw > 0;
                grid.Rows.Add(
                    hasLoad,
                    room.Id,
                    room.Name,
                    room.AreaM2.ToString("0.0", CultureInfo.InvariantCulture),
                    hasLoad ? room.LoadKw.ToString("0.##", CultureInfo.InvariantCulture) : "",
                    "",
                    "",
                    "");
            }

            updating = false;
            RecomputeAll();

            errorLabel.Dock = DockStyle.Bottom;
            errorLabel.Height = 36;
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
            buttons.Controls.Add(summaryLabel);
            summaryLabel.Margin = new Padding(12, 8, 3, 3);

            Controls.Add(grid);
            Controls.Add(options);
            Controls.Add(errorLabel);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private static void AddOption(FlowLayoutPanel panel, string label, Control control)
        {
            FlowLayoutPanel cell = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true };
            cell.Controls.Add(new Label { Text = label, AutoSize = true });
            cell.Controls.Add(control);
            panel.Controls.Add(cell);
        }

        private UnitFamily SelectedFamily()
        {
            return UnitCatalog.Find(familyBox.SelectedItem as string);
        }

        private void FillMaxIndex(int wanted)
        {
            maxIndexBox.Items.Clear();
            UnitFamily family = SelectedFamily();
            int pick = family.Models.Length - 1;
            for (int i = 0; i < family.Models.Length; i++)
            {
                UnitModel model = family.Models[i];
                maxIndexBox.Items.Add(model.Index.ToString(CultureInfo.InvariantCulture) + "  -  "
                    + model.Kw.ToString("0.0", CultureInfo.InvariantCulture) + " kW");
                if (model.Index <= wanted)
                    pick = i;
            }

            maxIndexBox.SelectedIndex = Math.Max(0, Math.Min(pick, maxIndexBox.Items.Count - 1));
        }

        private int SelectedMaxIndex()
        {
            UnitFamily family = SelectedFamily();
            int at = maxIndexBox.SelectedIndex;
            if (at < 0 || at >= family.Models.Length)
                return family.Models[family.Models.Length - 1].Index;
            return family.Models[at].Index;
        }

        private static bool TryNumber(string text, out double value)
        {
            return double.TryParse(
                (text ?? "").Trim().Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value) && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        // The settings the selection logic needs; defaults for a field that does not parse yet.
        private UnitSettings CurrentSelectionSettings()
        {
            UnitSettings settings = new UnitSettings { Family = SelectedFamily().Name, MaxIndex = SelectedMaxIndex() };
            double tolerance;
            if (TryNumber(toleranceBox.Text, out tolerance) && tolerance >= 0 && tolerance <= 50)
                settings.Tolerance = tolerance / 100;
            int units;
            if (int.TryParse(maxUnitsBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out units)
                && units >= 1 && units <= 50)
                settings.MaxUnitsPerRoom = units;
            return settings;
        }

        private void OnCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (updating || e.RowIndex < 0)
                return;
            if (e.ColumnIndex == ColLoad)
            {
                // Typing a load ticks the room.
                double load;
                string text = Convert.ToString(grid.Rows[e.RowIndex].Cells[ColLoad].Value, CultureInfo.InvariantCulture);
                if (TryNumber(text, out load) && load > 0)
                {
                    updating = true;
                    grid.Rows[e.RowIndex].Cells[ColUse].Value = true;
                    updating = false;
                }
            }

            if (e.ColumnIndex == ColLoad || e.ColumnIndex == ColUnits || e.ColumnIndex == ColUse)
                RecomputeRow(e.RowIndex, CurrentSelectionSettings());
            UpdateSummary();
        }

        private void RecomputeAll()
        {
            if (updating)
                return;
            UnitSettings settings = CurrentSelectionSettings();
            for (int i = 0; i < grid.Rows.Count; i++)
                RecomputeRow(i, settings);
            UpdateSummary();
        }

        private void RecomputeRow(int index, UnitSettings settings)
        {
            DataGridViewRow row = grid.Rows[index];
            double load;
            string loadText = Convert.ToString(row.Cells[ColLoad].Value, CultureInfo.InvariantCulture);
            int units = 0;
            string unitsText = (Convert.ToString(row.Cells[ColUnits].Value, CultureInfo.InvariantCulture) ?? "").Trim();
            string note = "";
            if (unitsText.Length > 0 && (!int.TryParse(unitsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out units)
                || units < 1 || units > 50))
            {
                units = 0;
                note = "Units must be 1 to 50.";
            }

            UnitChoice choice = null;
            if (!TryNumber(loadText, out load) || load <= 0)
            {
                if (note.Length == 0)
                    note = string.IsNullOrWhiteSpace(loadText) ? "No load: type kW or leave unticked." : "Load must be a number above 0.";
            }
            else
            {
                choice = UnitSelector.Choose(load, SelectedFamily(), settings, units);
            }

            choices[index] = choice;
            updating = true;
            if (choice == null)
                row.Cells[ColChosen].Value = "";
            else
            {
                row.Cells[ColChosen].Value = choice.Units.ToString(CultureInfo.InvariantCulture) + " x "
                    + choice.Model.BlockName + " (index " + choice.Model.Index.ToString(CultureInfo.InvariantCulture) + ")";
                if (choice.Undersized)
                    note = "Units too small for the load: raise the units or the largest unit.";
                else if (units == 0 && choice.Units > 1)
                    note = "Load split into " + choice.Units.ToString(CultureInfo.InvariantCulture) + " units.";
            }

            row.Cells[ColNote].Value = note;
            updating = false;
        }

        private void UpdateSummary()
        {
            int rowsUsed = 0, unitsTotal = 0;
            for (int i = 0; i < grid.Rows.Count; i++)
            {
                if (IsUsed(i))
                {
                    rowsUsed++;
                    unitsTotal += choices[i].Units;
                }
            }

            summaryLabel.Text = rowsUsed.ToString(CultureInfo.InvariantCulture) + " rooms, "
                + unitsTotal.ToString(CultureInfo.InvariantCulture) + " units";
        }

        private bool IsUsed(int index)
        {
            object used = grid.Rows[index].Cells[ColUse].Value;
            return used is bool && (bool)used && choices[index] != null;
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            grid.EndEdit();
            UnitSettings settings = new UnitSettings { Family = SelectedFamily().Name, MaxIndex = SelectedMaxIndex() };
            double tolerance, clearance;
            int units;
            if (!TryNumber(toleranceBox.Text, out tolerance) || !TryNumber(clearanceBox.Text, out clearance)
                || !int.TryParse(maxUnitsBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out units))
            {
                errorLabel.Text = "The tolerance, the most units and the clearance must be numbers.";
                return;
            }

            settings.Tolerance = tolerance / 100;
            settings.Clearance = clearance;
            settings.MaxUnitsPerRoom = units;
            string error;
            if (!settings.IsValid(out error))
            {
                errorLabel.Text = error;
                return;
            }

            RecomputeAll();
            List<RoomUnitPlan> plans = new List<RoomUnitPlan>();
            for (int i = 0; i < rooms.Count; i++)
            {
                if (IsUsed(i))
                    plans.Add(new RoomUnitPlan { Room = rooms[i], Choice = choices[i] });
            }

            if (plans.Count == 0)
            {
                errorLabel.Text = "Tick at least one room with a load.";
                return;
            }

            UnitSettingsStore.Save(settings);
            Settings = settings;
            Plans = plans;
            DialogResult = DialogResult.OK;
        }
    }
}
