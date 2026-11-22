using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using HvacAiCadAddin.Load;

namespace HvacAiCadAddin.Commands
{
    /// <summary>Reads and writes the HVACLOAD values in %APPDATA%\HvacAiCadAddin\HVACLOAD.settings.</summary>
    internal static class LoadSettingsStore
    {
        private static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HvacAiCadAddin",
                    "HVACLOAD.settings");
            }
        }

        internal static LoadSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return LoadSettings.Deserialize(File.ReadAllText(SettingsPath));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return new LoadSettings();
        }

        internal static void Save(LoadSettings settings)
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
    /// HVACLOAD dialog: one row per selected room with the room type Python guessed (changeable), the
    /// W/m2 per type (editable, placeholder values until the user's own table is in
    /// heat_load_table.json) and the safety factor. The kW column is recomputed live with the same
    /// formula as heat_load.py; the final numbers written to the drawing come from Python.
    /// </summary>
    public sealed class LoadSettingsForm : Form
    {
        private readonly List<LoadRoomRef> rooms;
        private readonly LoadTable table;
        private readonly DataGridView roomGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.CellSelect
        };
        private readonly DataGridView typeGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.CellSelect
        };
        private readonly TextBox safetyBox = new TextBox { Width = 70 };
        private readonly CheckBox saveTableBox = new CheckBox
        {
            Text = "Save these W/m2 values to " + LoadCore.TableName,
            AutoSize = true
        };
        private readonly Label totalLabel = new Label { AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        private readonly Label noteLabel = new Label { AutoSize = true, ForeColor = Color.DarkOrange };
        private readonly Label errorLabel = new Label();
        private bool loading;

        /// <summary>The table with the values as edited in the dialog (after OK).</summary>
        internal LoadTable Table { get; private set; }
        internal double SafetyFactor { get; private set; }
        internal bool SaveTable { get; private set; }

        /// <summary>The room type chosen for each room, in the order of the rooms passed in (after OK).</summary>
        internal string[] Types { get; private set; }

        internal LoadSettingsForm(
            IList<LoadRoomRef> rooms, LoadTable table, IList<string> guessedTypes,
            LoadSettings last, string tableNote)
        {
            this.rooms = new List<LoadRoomRef>(rooms);
            this.table = table.Clone();
            loading = true;

            Text = "HVACLOAD: area-based cooling load";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(820, 640);
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(700, 520);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            // Rooms.
            List<string> keys = this.table.Keys();
            roomGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "id", HeaderText = "Id", ReadOnly = true, FillWeight = 40 });
            roomGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Name", ReadOnly = true, FillWeight = 110 });
            roomGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "area", HeaderText = "Area m2", ReadOnly = true, FillWeight = 55 });
            DataGridViewComboBoxColumn typeColumn = new DataGridViewComboBoxColumn
            {
                Name = "type",
                HeaderText = "Room type",
                FlatStyle = FlatStyle.Flat,
                FillWeight = 80
            };
            foreach (string key in keys)
                typeColumn.Items.Add(key);
            roomGrid.Columns.Add(typeColumn);
            roomGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "w", HeaderText = "W/m2", ReadOnly = true, FillWeight = 45 });
            roomGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "kw", HeaderText = "kW", ReadOnly = true, FillWeight = 45 });
            foreach (LoadRoomRef room in this.rooms)
            {
                string type = guessedTypes[this.rooms.IndexOf(room)];
                if (!this.table.Types.ContainsKey(type))
                    type = this.table.Types.ContainsKey("other") ? "other" : keys[0];
                int row = roomGrid.Rows.Add(room.Id, DisplayName(room), room.AreaM2.ToString("0.00", CultureInfo.InvariantCulture), type, "", "");
                if (IsShaft(room))
                    roomGrid.Rows[row].Cells["type"].ReadOnly = true;
            }

            GroupBox roomGroup = new GroupBox { Text = "Rooms (change the type if the name was misread)", Dock = DockStyle.Fill, Padding = new Padding(8) };
            roomGroup.Controls.Add(roomGrid);

            // Types.
            typeGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "key", HeaderText = "Type", ReadOnly = true, FillWeight = 50 });
            typeGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "label", HeaderText = "Description", ReadOnly = true, FillWeight = 120 });
            typeGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "w", HeaderText = "W/m2", FillWeight = 40 });
            typeGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "cooled", HeaderText = "Cooled", FillWeight = 30 });
            foreach (string key in keys)
            {
                LoadTypeDef def = this.table.Types[key];
                typeGrid.Rows.Add(key, def.LabelVi, def.WPerM2.ToString("0.##", CultureInfo.InvariantCulture), def.Cooled);
            }

            GroupBox typeGroup = new GroupBox { Text = "W/m2 per room type (from " + LoadCore.TableName + ")", Dock = DockStyle.Fill, Padding = new Padding(8) };
            typeGroup.Controls.Add(typeGrid);

            SplitContainer split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 280
            };
            split.Panel1.Controls.Add(roomGroup);
            split.Panel2.Controls.Add(typeGroup);

            // Bottom: safety factor, save, total, note.
            FlowLayoutPanel options = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 66, Padding = new Padding(8, 4, 8, 0), WrapContents = true };
            options.Controls.Add(new Label { Text = "Safety factor", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
            options.Controls.Add(safetyBox);
            options.Controls.Add(saveTableBox);
            options.Controls.Add(totalLabel);
            noteLabel.Text = string.IsNullOrEmpty(tableNote) ? "" : tableNote;
            noteLabel.MaximumSize = new Size(780, 0);
            options.Controls.Add(noteLabel);
            options.SetFlowBreak(saveTableBox, true);

            errorLabel.Dock = DockStyle.Bottom;
            errorLabel.Height = 28;
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

            Controls.Add(split);
            Controls.Add(options);
            Controls.Add(errorLabel);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;

            safetyBox.Text = last.SafetyFactor.ToString("0.###", CultureInfo.InvariantCulture);
            saveTableBox.Checked = last.SaveTable;

            safetyBox.TextChanged += (sender, e) => Recompute();
            typeGrid.CellValueChanged += (sender, e) => Recompute();
            roomGrid.CellValueChanged += (sender, e) => Recompute();
            roomGrid.CurrentCellDirtyStateChanged += (sender, e) =>
            {
                if (roomGrid.IsCurrentCellDirty)
                    roomGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            typeGrid.CurrentCellDirtyStateChanged += (sender, e) =>
            {
                if (typeGrid.IsCurrentCellDirty)
                    typeGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            roomGrid.DataError += (sender, e) => e.ThrowException = false;

            loading = false;
            Recompute();
        }

        private static string DisplayName(LoadRoomRef room)
        {
            if (!string.IsNullOrWhiteSpace(room.Name))
                return room.Name;
            return IsShaft(room) ? "HKT" : "";
        }

        private static bool IsShaft(LoadRoomRef room)
        {
            return string.Equals(room.Kind, "shaft", StringComparison.Ordinal);
        }

        private static bool TryNumber(string text, out double value)
        {
            return double.TryParse(
                (text ?? "").Trim().Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value) && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        // Copies the grid values into the working table; false when a W/m2 cell is not a number.
        private bool ReadTypeGrid(LoadTable target)
        {
            bool good = true;
            foreach (DataGridViewRow row in typeGrid.Rows)
            {
                string key = (string)row.Cells["key"].Value;
                double w;
                if (TryNumber(Convert.ToString(row.Cells["w"].Value, CultureInfo.InvariantCulture), out w) && w >= 0 && w <= 2000)
                    target.Types[key].WPerM2 = w;
                else
                    good = false;
                target.Types[key].Cooled = row.Cells["cooled"].Value is bool && (bool)row.Cells["cooled"].Value;
            }

            return good;
        }

        private void Recompute()
        {
            if (loading)
                return;
            double safety;
            bool safetyOk = TryNumber(safetyBox.Text, out safety) && safety >= 0.5 && safety <= 3;
            if (!safetyOk)
                safety = 1.0;
            bool typesOk = ReadTypeGrid(table);

            double total = 0;
            foreach (DataGridViewRow row in roomGrid.Rows)
            {
                LoadRoomRef room = rooms[row.Index];
                string key = row.Cells["type"].Value as string;
                LoadTypeDef def;
                double w = 0, kw = 0;
                if (!IsShaft(room) && key != null && table.Types.TryGetValue(key, out def) && def.Cooled)
                {
                    w = def.WPerM2;
                    kw = LoadCore.Compute(room.AreaM2, w, true, safety);
                }

                total += kw;
                row.Cells["w"].Value = w.ToString("0.##", CultureInfo.InvariantCulture);
                row.Cells["kw"].Value = kw.ToString("0.00", CultureInfo.InvariantCulture);
            }

            totalLabel.Text = "Total: " + total.ToString("0.0", CultureInfo.InvariantCulture) + " kW";
            errorLabel.Text = safetyOk && typesOk ? "" : "Check the safety factor (0.5 to 3) and the W/m2 values.";
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            roomGrid.EndEdit();
            typeGrid.EndEdit();
            double safety;
            if (!TryNumber(safetyBox.Text, out safety) || safety < 0.5 || safety > 3)
            {
                errorLabel.Text = "The safety factor must be a number between 0.5 and 3.";
                return;
            }

            LoadTable edited = table.Clone();
            if (!ReadTypeGrid(edited))
            {
                errorLabel.Text = "The W/m2 values must be numbers between 0 and 2000.";
                return;
            }

            edited.SafetyFactor = safety;
            string[] types = new string[rooms.Count];
            for (int i = 0; i < rooms.Count; i++)
            {
                string key = roomGrid.Rows[i].Cells["type"].Value as string;
                if (key == null || !edited.Types.ContainsKey(key))
                {
                    errorLabel.Text = "Choose a room type for " + rooms[i].Id + ".";
                    return;
                }

                types[i] = key;
            }

            Table = edited;
            SafetyFactor = safety;
            SaveTable = saveTableBox.Checked;
            Types = types;
            LoadSettings stored = LoadSettingsStore.Load();
            stored.SafetyFactor = safety;
            stored.SaveTable = SaveTable;
            LoadSettingsStore.Save(stored);
            DialogResult = DialogResult.OK;
        }
    }
}
