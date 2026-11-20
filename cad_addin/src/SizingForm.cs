using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace HvacAiCadAddin.Commands
{
    // The sizing dialog shared by the pipe commands. HVACREFRIG uses it for refrigerant pipes today and a
    // condensate command can reuse it: the caller passes the unit rows, the choices for the capacity and
    // size cells, and a callback that works out the sizes. The form holds no sizing rules of its own. It
    // recomputes on every change, keeps the sizes the user set by hand, and reports the selection so the
    // caller can highlight the segment or unit in the drawing. See AGENTS.md, "HVACREFRIG", "Pipe sizing".

    /// <summary>A header choice, such as an outdoor unit size. Value 0 is the automatic choice.</summary>
    internal sealed class SizingChoice
    {
        public int Value;
        public string Text;
    }

    /// <summary>A capacity the unit cells offer; Value 0 is "none".</summary>
    internal sealed class SizingCapacityChoice
    {
        public double Value;
        public string Text;
    }

    internal sealed class SizingUnitRow
    {
        /// <summary>E1, E2, ...</summary>
        public string Name;
        /// <summary>Where the capacity came from: a block, a label, saved xdata, or empty.</summary>
        public string Source;
        /// <summary>0 means none yet.</summary>
        public double Capacity;
    }

    /// <summary>What the form asks the caller to work out.</summary>
    internal sealed class SizingRequestView
    {
        public double[] Capacities;
        public int HeaderValue;
        public bool Upsize;
        /// <summary>Sizes set by hand: segment index to size text.</summary>
        public Dictionary<int, string> Overrides;
    }

    internal sealed class SizingSegmentView
    {
        /// <summary>S1, S2, ...</summary>
        public string Name;
        public string Kind;
        public string Total;
        public string Units;
        /// <summary>The automatic size text, or empty.</summary>
        public string AutoText;
        /// <summary>The size in use, or empty when there is none.</summary>
        public string SizeText;
        public bool Manual;
        public string Warning;
    }

    internal sealed class SizingState
    {
        /// <summary>The line beside the header choice: totals, ratio, the choice in use.</summary>
        public string HeaderText;
        /// <summary>A second line of information, such as the longest pipe.</summary>
        public string InfoText;
        public readonly List<SizingSegmentView> Segments = new List<SizingSegmentView>();
        /// <summary>The refnet list line: kit names.</summary>
        public string RefnetText;
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public bool CanApply;
    }

    /// <summary>The state and the selection, for the caller to redraw its highlight.</summary>
    internal sealed class SizingView
    {
        public SizingState State;
        public IList<int> SelectedUnits;
        public IList<int> SelectedSegments;
    }

    internal sealed class SizingSetup
    {
        public string Title = "Size pipes";
        /// <summary>The header combo (for example "Outdoor unit"); null leaves it out.</summary>
        public string HeaderLabel;
        public IList<SizingChoice> HeaderChoices;
        public int HeaderValue;
        /// <summary>The check box next to the header (for example the upsize option); null leaves it out.</summary>
        public string UpsizeText;
        public bool Upsize;
        /// <summary>A line above the buttons: what Apply writes.</summary>
        public string ApplyNote;
        public IList<SizingUnitRow> Units = new List<SizingUnitRow>();
        public string UnitsTitle = "Indoor units";
        public string CapacityTitle = "Capacity";
        /// <summary>The capacities the unit cells offer; null makes the cell a number box.</summary>
        public IList<SizingCapacityChoice> CapacityChoices;
        public string SegmentsTitle = "Pipe segments";
        /// <summary>The sizes the segment cells offer.</summary>
        public IList<string> SizeChoices = new List<string>();
        public Func<SizingRequestView, SizingState> Recompute;
        /// <summary>Called after every recompute and every selection change.</summary>
        public Action<SizingView> ViewChanged;
    }

    internal sealed class SizingForm : Form
    {
        private const string ManualMark = "✎";
        private const string NoneText = "(none)";

        private readonly SizingSetup _setup;
        private readonly double[] _capacities;
        private readonly Dictionary<int, string> _overrides = new Dictionary<int, string>();
        private readonly Dictionary<string, double> _capacityByText = new Dictionary<string, double>();
        private readonly Dictionary<double, string> _textByCapacity = new Dictionary<double, string>();

        private readonly ComboBox _headerBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        private readonly Label _headerText = new Label { AutoSize = true, Margin = new Padding(8, 6, 3, 3) };
        private readonly Label _infoText = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
        private readonly CheckBox _upsizeBox = new CheckBox { AutoSize = true, Margin = new Padding(16, 4, 3, 3) };
        private readonly DataGridView _unitGrid = NewGrid();
        private readonly DataGridView _segmentGrid = NewGrid();
        private readonly ComboBox _bulkCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        private readonly TextBox _bulkNumber = new TextBox { Width = 90 };
        private readonly Label _refnetText = new Label { Dock = DockStyle.Fill, AutoEllipsis = false };
        private readonly TextBox _warningBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Window
        };
        private readonly Button _applyButton = new Button { Text = "Apply", Width = 90 };
        private readonly List<int> _headerValues = new List<int>();
        private readonly SplitContainer _split = new SplitContainer();

        private SizingState _state;
        private bool _busy;

        /// <summary>The request behind the state the user accepted.</summary>
        internal SizingRequestView FinalRequest { get; private set; }

        internal SizingState FinalState
        {
            get { return _state; }
        }

        internal SizingForm(SizingSetup setup)
        {
            _setup = setup;
            _capacities = new double[setup.Units.Count];
            for (int i = 0; i < _capacities.Length; i++)
                _capacities[i] = setup.Units[i].Capacity;

            Text = setup.Title;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1120, 660);
            MinimumSize = new Size(900, 520);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            BuildCapacityMaps();
            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(10)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildGrids(), 0, 1);

            GroupBox warnings = new GroupBox { Text = "Warnings", Dock = DockStyle.Fill };
            warnings.Controls.Add(_warningBox);
            root.Controls.Add(warnings, 0, 2);

            Label note = new Label
            {
                Text = setup.ApplyNote ?? "",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(3, 6, 3, 3)
            };
            root.Controls.Add(note, 0, 3);
            root.Controls.Add(BuildButtons(), 0, 4);
            Controls.Add(root);

            SizingFormEvents();
            Shown += (sender, args) =>
            {
                try
                {
                    _split.SplitterDistance = Math.Max(_split.Panel1MinSize, Math.Min(430, _split.Width / 3));
                }
                catch (InvalidOperationException)
                {
                    // Keep the default split.
                }

                _unitGrid.ClearSelection();
                _segmentGrid.ClearSelection();
                Recalculate();
            };
        }

        // ------------------------------------------------------------------ construction

        private static DataGridView NewGrid()
        {
            return new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                EditMode = DataGridViewEditMode.EditOnEnter,
                BackgroundColor = SystemColors.Window,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None
            };
        }

        private void BuildCapacityMaps()
        {
            if (_setup.CapacityChoices == null)
                return;

            _capacityByText[NoneText] = 0;
            _textByCapacity[0] = NoneText;
            foreach (SizingCapacityChoice choice in _setup.CapacityChoices)
            {
                _capacityByText[choice.Text] = choice.Value;
                _textByCapacity[choice.Value] = choice.Text;
            }
        }

        private Control BuildHeader()
        {
            FlowLayoutPanel header = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true
            };

            if (_setup.HeaderChoices != null)
            {
                header.Controls.Add(new Label
                {
                    Text = _setup.HeaderLabel ?? "",
                    AutoSize = true,
                    Margin = new Padding(3, 7, 3, 3)
                });
                foreach (SizingChoice choice in _setup.HeaderChoices)
                {
                    _headerBox.Items.Add(choice.Text);
                    _headerValues.Add(choice.Value);
                }

                int at = _headerValues.IndexOf(_setup.HeaderValue);
                _headerBox.SelectedIndex = at >= 0 ? at : 0;
                header.Controls.Add(_headerBox);
            }

            header.Controls.Add(_headerText);

            if (_setup.UpsizeText != null)
            {
                _upsizeBox.Text = _setup.UpsizeText;
                _upsizeBox.Checked = _setup.Upsize;
                header.Controls.Add(_upsizeBox);
            }

            FlowLayoutPanel column = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };
            column.Controls.Add(header);
            column.Controls.Add(_infoText);
            return column;
        }

        private Control BuildGrids()
        {
            _split.Dock = DockStyle.Fill;
            _split.Orientation = Orientation.Vertical;
            SplitContainer split = _split;

            // Left: the units.
            GroupBox units = new GroupBox { Text = _setup.UnitsTitle, Dock = DockStyle.Fill };
            TableLayoutPanel left = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2
            };
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _unitGrid.Columns.Add(TextColumn("#", 50, true));
            DataGridViewColumn source = TextColumn("Source", 160, true);
            source.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _unitGrid.Columns.Add(source);
            if (_setup.CapacityChoices != null)
            {
                DataGridViewComboBoxColumn capacity = new DataGridViewComboBoxColumn
                {
                    HeaderText = _setup.CapacityTitle,
                    Width = 150,
                    FlatStyle = FlatStyle.Flat,
                    DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox
                };
                capacity.Items.Add(NoneText);
                foreach (SizingCapacityChoice choice in _setup.CapacityChoices)
                    capacity.Items.Add(choice.Text);
                _unitGrid.Columns.Add(capacity);
            }
            else
            {
                _unitGrid.Columns.Add(TextColumn(_setup.CapacityTitle, 110, false));
            }

            for (int i = 0; i < _setup.Units.Count; i++)
            {
                SizingUnitRow unit = _setup.Units[i];
                int row = _unitGrid.Rows.Add();
                _unitGrid.Rows[row].Cells[0].Value = unit.Name;
                _unitGrid.Rows[row].Cells[1].Value = string.IsNullOrEmpty(unit.Source) ? "—" : unit.Source;
                SetCapacityCell(row, unit.Capacity);
            }

            left.Controls.Add(_unitGrid, 0, 0);

            FlowLayoutPanel bulk = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight
            };
            bulk.Controls.Add(new Label { Text = "Set selected rows to", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
            if (_setup.CapacityChoices != null)
            {
                _bulkCombo.Items.Add(NoneText);
                foreach (SizingCapacityChoice choice in _setup.CapacityChoices)
                    _bulkCombo.Items.Add(choice.Text);
                _bulkCombo.SelectedIndex = 0;
                bulk.Controls.Add(_bulkCombo);
            }
            else
            {
                bulk.Controls.Add(_bulkNumber);
            }

            Button applyToSelected = new Button { Text = "Apply to selected rows", AutoSize = true };
            applyToSelected.Click += OnApplyToSelected;
            bulk.Controls.Add(applyToSelected);
            left.Controls.Add(bulk, 0, 1);
            units.Controls.Add(left);
            split.Panel1.Controls.Add(units);

            // Right: the segments.
            GroupBox segments = new GroupBox { Text = _setup.SegmentsTitle, Dock = DockStyle.Fill };
            TableLayoutPanel right = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3
            };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _segmentGrid.Columns.Add(TextColumn("#", 46, true));
            _segmentGrid.Columns.Add(TextColumn("Kind", 110, true));
            _segmentGrid.Columns.Add(TextColumn("Total", 54, true));
            _segmentGrid.Columns.Add(TextColumn("Units", 46, true));
            _segmentGrid.Columns.Add(TextColumn("Auto size", 150, true));
            DataGridViewComboBoxColumn size = new DataGridViewComboBoxColumn
            {
                HeaderText = "Size",
                Width = 170,
                FlatStyle = FlatStyle.Flat,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox
            };
            foreach (string text in _setup.SizeChoices)
                size.Items.Add(text);
            _segmentGrid.Columns.Add(size);
            DataGridViewColumn mark = TextColumn("", 28, true);
            mark.DefaultCellStyle.Font = new Font("Segoe UI Symbol", 9F);
            mark.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            mark.ToolTipText = "Set by hand";
            _segmentGrid.Columns.Add(mark);
            DataGridViewColumn warning = TextColumn("Warning", 200, true);
            warning.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _segmentGrid.Columns.Add(warning);
            right.Controls.Add(_segmentGrid, 0, 0);
            right.Controls.Add(_refnetText, 0, 1);

            Button reset = new Button { Text = "Clear sizes set by hand", AutoSize = true };
            reset.Click += (sender, args) =>
            {
                _overrides.Clear();
                Recalculate();
            };
            right.Controls.Add(reset, 0, 2);
            segments.Controls.Add(right);
            split.Panel2.Controls.Add(segments);
            return split;
        }

        private static DataGridViewColumn TextColumn(string header, int width, bool readOnly)
        {
            return new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                Width = width,
                ReadOnly = readOnly,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
        }

        private Control BuildButtons()
        {
            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft
            };
            Button cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
            _applyButton.Click += OnApply;
            Button recalculate = new Button { Text = "Recalculate", Width = 100 };
            recalculate.Click += (sender, args) => Recalculate();
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(_applyButton);
            buttons.Controls.Add(recalculate);
            CancelButton = cancel;
            return buttons;
        }

        private void SizingFormEvents()
        {
            _headerBox.SelectedIndexChanged += (sender, args) => Recalculate();
            _upsizeBox.CheckedChanged += (sender, args) => Recalculate();

            _unitGrid.CurrentCellDirtyStateChanged += (sender, args) =>
            {
                if (_unitGrid.IsCurrentCellDirty)
                    _unitGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _unitGrid.CellValueChanged += OnUnitValueChanged;
            _unitGrid.DataError += (sender, args) => args.ThrowException = false;
            _unitGrid.SelectionChanged += (sender, args) => NotifyView();

            _segmentGrid.CurrentCellDirtyStateChanged += (sender, args) =>
            {
                if (_segmentGrid.IsCurrentCellDirty)
                    _segmentGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _segmentGrid.CellValueChanged += OnSegmentValueChanged;
            _segmentGrid.DataError += (sender, args) => args.ThrowException = false;
            _segmentGrid.SelectionChanged += (sender, args) => NotifyView();
        }

        // ------------------------------------------------------------------ capacity cells

        private int CapacityColumn
        {
            get { return 2; }
        }

        private void SetCapacityCell(int row, double capacity)
        {
            DataGridViewCell cell = _unitGrid.Rows[row].Cells[CapacityColumn];
            if (_setup.CapacityChoices != null)
            {
                string text;
                cell.Value = _textByCapacity.TryGetValue(capacity, out text) ? text : NoneText;
            }
            else
            {
                cell.Value = capacity > 0 ? capacity.ToString("0.###", CultureInfo.InvariantCulture) : "";
            }

            cell.Style.BackColor = capacity > 0 ? SystemColors.Window : Color.MistyRose;
        }

        private bool TryReadCapacity(object value, out double capacity)
        {
            capacity = 0;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (_setup.CapacityChoices != null)
                return !string.IsNullOrEmpty(text) && _capacityByText.TryGetValue(text, out capacity);

            if (string.IsNullOrWhiteSpace(text))
                return true;
            return double.TryParse(
                text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out capacity)
                && capacity >= 0 && !double.IsInfinity(capacity);
        }

        private void OnUnitValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_busy || e.RowIndex < 0 || e.ColumnIndex != CapacityColumn || e.RowIndex >= _capacities.Length)
                return;

            double capacity;
            if (!TryReadCapacity(_unitGrid.Rows[e.RowIndex].Cells[CapacityColumn].Value, out capacity))
                capacity = 0;
            _capacities[e.RowIndex] = capacity;

            _busy = true;
            try
            {
                SetCapacityCell(e.RowIndex, capacity);
            }
            finally
            {
                _busy = false;
            }

            Recalculate();
        }

        private void OnApplyToSelected(object sender, EventArgs e)
        {
            double capacity;
            if (_setup.CapacityChoices != null)
            {
                string text = Convert.ToString(_bulkCombo.SelectedItem, CultureInfo.InvariantCulture);
                if (!_capacityByText.TryGetValue(text ?? "", out capacity))
                    capacity = 0;
            }
            else if (!TryReadCapacity(_bulkNumber.Text, out capacity))
            {
                return;
            }

            _busy = true;
            try
            {
                foreach (DataGridViewRow row in _unitGrid.SelectedRows)
                {
                    _capacities[row.Index] = capacity;
                    SetCapacityCell(row.Index, capacity);
                }
            }
            finally
            {
                _busy = false;
            }

            Recalculate();
        }

        // ------------------------------------------------------------------ segment cells

        private void OnSegmentValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_busy || e.RowIndex < 0 || e.ColumnIndex != 5 || _state == null || e.RowIndex >= _state.Segments.Count)
                return;

            string text = Convert.ToString(_segmentGrid.Rows[e.RowIndex].Cells[5].Value, CultureInfo.InvariantCulture);
            SizingSegmentView view = _state.Segments[e.RowIndex];
            if (string.IsNullOrEmpty(text) || string.Equals(text, view.AutoText, StringComparison.Ordinal))
                _overrides.Remove(e.RowIndex);
            else
                _overrides[e.RowIndex] = text;
            Recalculate();
        }

        // ------------------------------------------------------------------ recompute

        private SizingRequestView BuildRequest()
        {
            int header = 0;
            if (_headerBox.SelectedIndex >= 0 && _headerBox.SelectedIndex < _headerValues.Count)
                header = _headerValues[_headerBox.SelectedIndex];

            return new SizingRequestView
            {
                Capacities = (double[])_capacities.Clone(),
                HeaderValue = header,
                Upsize = _upsizeBox.Checked,
                Overrides = new Dictionary<int, string>(_overrides)
            };
        }

        private void Recalculate()
        {
            if (_busy || _setup.Recompute == null)
                return;

            SizingRequestView request = BuildRequest();
            SizingState state = _setup.Recompute(request);
            _busy = true;
            try
            {
                _state = state;
                FinalRequest = request;
                ShowState(state);
            }
            finally
            {
                _busy = false;
            }

            NotifyView();
        }

        private void ShowState(SizingState state)
        {
            _headerText.Text = state.HeaderText ?? "";
            _infoText.Text = state.InfoText ?? "";
            _refnetText.Text = state.RefnetText ?? "";

            if (_segmentGrid.Rows.Count != state.Segments.Count)
            {
                _segmentGrid.Rows.Clear();
                for (int i = 0; i < state.Segments.Count; i++)
                    _segmentGrid.Rows.Add();
            }

            for (int i = 0; i < state.Segments.Count; i++)
            {
                SizingSegmentView view = state.Segments[i];
                DataGridViewRow row = _segmentGrid.Rows[i];
                row.Cells[0].Value = view.Name;
                row.Cells[1].Value = view.Kind;
                row.Cells[2].Value = view.Total;
                row.Cells[3].Value = view.Units;
                row.Cells[4].Value = string.IsNullOrEmpty(view.AutoText) ? "—" : view.AutoText;
                row.Cells[5].Value = string.IsNullOrEmpty(view.SizeText) ? null : view.SizeText;
                row.Cells[6].Value = view.Manual ? ManualMark : "";
                row.Cells[7].Value = view.Warning ?? "";
                row.Cells[7].Style.ForeColor = string.IsNullOrEmpty(view.Warning) ? SystemColors.WindowText : Color.Firebrick;
                row.DefaultCellStyle.BackColor = view.Manual ? Color.LightYellow : SystemColors.Window;
            }

            List<string> lines = new List<string>();
            foreach (string warning in state.Warnings)
                lines.Add(warning);
            foreach (string note in state.Notes)
                lines.Add("Note: " + note);
            _warningBox.Text = string.Join(Environment.NewLine, lines);
            _applyButton.Enabled = state.CanApply;
        }

        private void NotifyView()
        {
            if (_busy || _state == null || _setup.ViewChanged == null)
                return;

            List<int> units = new List<int>();
            foreach (DataGridViewRow row in _unitGrid.SelectedRows)
                units.Add(row.Index);
            List<int> segments = new List<int>();
            foreach (DataGridViewRow row in _segmentGrid.SelectedRows)
                segments.Add(row.Index);

            try
            {
                _setup.ViewChanged(new SizingView { State = _state, SelectedUnits = units, SelectedSegments = segments });
            }
            catch (Exception)
            {
                // The highlight is a convenience: a failure to draw it must not stop the dialog.
            }
        }

        private void OnApply(object sender, EventArgs e)
        {
            if (_state == null || !_state.CanApply)
                return;
            DialogResult = DialogResult.OK;
        }
    }
}
