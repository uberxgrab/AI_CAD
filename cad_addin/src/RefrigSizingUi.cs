using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Pipes;

namespace HvacAiCadAddin.Commands
{
    /// <summary>What the sizing dialog accepted.</summary>
    internal sealed class RefrigSizingOutcome
    {
        public SizingResult Result;
        /// <summary>The capacity index of each indoor unit (indexes into <see cref="NetworkTopology.Ends"/>).</summary>
        public int[] UnitIndexes;
        /// <summary>The capacity in kW of each indoor unit, as read or nominal.</summary>
        public double[] UnitKw;
    }

    // The AutoCAD side of the HVACREFRIG sizing dialog: turns the topology and the capacity readings into
    // SizingForm rows, calls the AutoCAD-free sizer on every change, and keeps a transient preview in the
    // drawing: E1, E2, ... at the pipe ends, S1, S2, ... at the segment midpoints, each segment coloured by
    // its size, with the selected row's segment or unit highlighted. Nothing is written here.
    internal sealed class RefrigSizingSession : IDisposable
    {
        // Preview colours by catalogue size (AutoCAD colour index); magenta is kept for refnet bodies.
        private static readonly short[] SizeColours = { 8, 3, 4, 5, 150, 30, 1, 40, 200, 90, 2 };
        private const short NoSizeColour = 1;
        private const short HighlightColour = 7;

        private readonly Editor _editor;
        private readonly NetworkTopology _topology;
        private readonly RefrigResult _clean;
        private readonly UnitReading[] _readings;
        private readonly List<string> _extraWarnings;
        private readonly double _elevation;
        private PipeTransients _preview;
        private SizingResult _last;

        internal RefrigSizingSession(
            Editor editor,
            NetworkTopology topology,
            RefrigResult clean,
            UnitReading[] readings,
            List<string> extraWarnings,
            double elevation)
        {
            _editor = editor;
            _topology = topology;
            _clean = clean;
            _readings = readings;
            _extraWarnings = extraWarnings;
            _elevation = elevation;
        }

        /// <summary>Shows the dialog. Returns null when the user cancels.</summary>
        internal RefrigSizingOutcome Run(string applyNote)
        {
            SizingSetup setup = BuildSetup(applyNote);
            _preview = new PipeTransients();
            try
            {
                using (SizingForm form = new SizingForm(setup))
                {
                    if (Application.ShowModalDialog(form) != System.Windows.Forms.DialogResult.OK || _last == null)
                        return null;

                    SizingRequestView request = form.FinalRequest;
                    RefrigSizingOutcome outcome = new RefrigSizingOutcome
                    {
                        Result = Compute(request),
                        UnitIndexes = ToIndexes(request.Capacities),
                        UnitKw = new double[request.Capacities.Length]
                    };
                    for (int i = 0; i < outcome.UnitKw.Length; i++)
                    {
                        outcome.UnitKw[i] = outcome.UnitIndexes[i] > 0
                            ? KwFor(i, outcome.UnitIndexes[i])
                            : 0;
                    }

                    return outcome;
                }
            }
            finally
            {
                _preview.Dispose();
                _preview = null;
                TryUpdateScreen();
            }
        }

        public void Dispose()
        {
            if (_preview != null)
                _preview.Dispose();
        }

        // A unit read from xdata or a block keeps its own kW when the user did not change the index.
        private double KwFor(int end, int index)
        {
            UnitReading reading = end < _readings.Length ? _readings[end] : null;
            if (reading != null && reading.Index == index)
                return reading.Kw;
            return VrvTables.NominalKw(index);
        }

        // ------------------------------------------------------------------ setup

        private SizingSetup BuildSetup(string applyNote)
        {
            SizingSetup setup = new SizingSetup
            {
                Title = "HVACREFRIG: size refrigerant pipes (Daikin VRV IV)",
                HeaderLabel = "Outdoor unit",
                HeaderValue = 0,
                UpsizeText = "Upsize main pipe (equivalent length ≥ 90 m)",
                Upsize = false,
                ApplyNote = applyNote,
                UnitsTitle = "Indoor units",
                CapacityTitle = "Capacity",
                SegmentsTitle = "Pipe segments",
                Recompute = Recompute,
                ViewChanged = UpdatePreview
            };

            List<SizingChoice> headers = new List<SizingChoice> { new SizingChoice { Value = 0, Text = "Auto" } };
            foreach (int hp in VrvTables.HpChoices)
            {
                headers.Add(new SizingChoice
                {
                    Value = hp,
                    Text = hp.ToString(CultureInfo.InvariantCulture) + " HP"
                });
            }

            setup.HeaderChoices = headers;

            List<SizingCapacityChoice> capacities = new List<SizingCapacityChoice>();
            foreach (int index in VrvTables.UnitIndexes)
                capacities.Add(new SizingCapacityChoice { Value = index, Text = VrvTables.IndexText(index) });
            setup.CapacityChoices = capacities;

            List<string> sizes = new List<string>(PipeCatalog.RefrigerantSizes);
            setup.SizeChoices = sizes;

            List<SizingUnitRow> units = new List<SizingUnitRow>();
            for (int i = 0; i < _topology.Ends.Count; i++)
            {
                UnitReading reading = i < _readings.Length ? _readings[i] : null;
                units.Add(new SizingUnitRow
                {
                    Name = "E" + (i + 1).ToString(CultureInfo.InvariantCulture),
                    Source = reading == null ? "" : reading.SourceText,
                    Capacity = reading == null ? 0 : reading.Index
                });
            }

            setup.Units = units;
            return setup;
        }

        private static int[] ToIndexes(double[] capacities)
        {
            int[] indexes = new int[capacities.Length];
            for (int i = 0; i < indexes.Length; i++)
                indexes[i] = capacities[i] > 0 ? (int)Math.Round(capacities[i]) : 0;
            return indexes;
        }

        private SizingResult Compute(SizingRequestView request)
        {
            return RefrigSizer.Compute(new SizingRequest
            {
                Topology = _topology,
                UnitIndexes = ToIndexes(request.Capacities),
                Hp = request.HeaderValue,
                UpsizeMain = request.Upsize,
                Overrides = request.Overrides
            });
        }

        private static string KindText(SegmentKind kind)
        {
            switch (kind)
            {
                case SegmentKind.Main: return "Main";
                case SegmentKind.BetweenRefnets: return "Between refnets";
                default: return "To unit";
            }
        }

        private static string Number(double value, string format)
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        private SizingState Recompute(SizingRequestView request)
        {
            SizingResult result = Compute(request);
            _last = result;

            SizingState state = new SizingState { CanApply = result.CanApply };
            StringBuilder header = new StringBuilder();
            header.Append("Total index ").Append(result.SumIndex.ToString(CultureInfo.InvariantCulture));
            header.Append(" · ").Append(result.HpIsDefault ? "auto " : "").Append(result.Hp.ToString(CultureInfo.InvariantCulture));
            header.Append(" HP (index ").Append(result.OduIndex.ToString(CultureInfo.InvariantCulture)).Append(")");
            if (result.SumIndex > 0)
                header.Append(" · connection ").Append(Number(result.Ratio, "0")).Append(" %");
            state.HeaderText = header.ToString();

            if (result.LongestUnit >= 0)
            {
                StringBuilder info = new StringBuilder();
                info.Append("Longest pipe from the root: ").Append(Number(result.LongestPipe / 1000.0, "0.0"));
                info.Append(" m (equivalent ").Append(Number(result.LongestEquivalent / 1000.0, "0.0"));
                info.Append(" m, refnets at 0.5 m each).");
                if (result.LongestEquivalent >= VrvTables.UpsizeEquivalentLength && !request.Upsize)
                    info.Append(" Equivalent length is 90 m or more: consider upsizing the main pipe.");
                state.InfoText = info.ToString();
            }

            foreach (SegmentSizing row in result.Segments)
            {
                state.Segments.Add(new SizingSegmentView
                {
                    Name = "S" + (row.Index + 1).ToString(CultureInfo.InvariantCulture),
                    Kind = KindText(row.Kind),
                    Total = row.TotalIndex.ToString(CultureInfo.InvariantCulture),
                    Units = row.UnitCount.ToString(CultureInfo.InvariantCulture),
                    AutoText = row.AutoText ?? "",
                    SizeText = row.ChosenText ?? "",
                    Manual = row.Manual,
                    Warning = row.Warning
                });
            }

            if (result.Kits.Count > 0)
            {
                StringBuilder kits = new StringBuilder("Refnets: ");
                for (int i = 0; i < result.Kits.Count; i++)
                {
                    RefnetKit kit = result.Kits[i];
                    if (i > 0)
                        kits.Append("   ");
                    kits.Append("R").Append((kit.Refnet + 1).ToString(CultureInfo.InvariantCulture)).Append(' ');
                    kits.Append(kit.Kit).Append(" (total ").Append(kit.TotalIndex.ToString(CultureInfo.InvariantCulture)).Append(')');
                }

                state.RefnetText = kits.ToString();
            }
            else
            {
                state.RefnetText = "No refnets.";
            }

            foreach (string warning in _extraWarnings)
                state.Warnings.Add(warning);
            state.Warnings.AddRange(result.Warnings);
            state.Notes.AddRange(result.Notes);
            return state;
        }

        // ------------------------------------------------------------------ preview

        private static short ColourOf(SegmentSizing row)
        {
            if (!row.HasChosen)
                return NoSizeColour;
            for (int i = 0; i < PipeCatalog.RefrigerantSizes.Length; i++)
            {
                if (string.Equals(PipeCatalog.RefrigerantSizes[i], row.ChosenText, StringComparison.Ordinal))
                    return SizeColours[i % SizeColours.Length];
            }

            return NoSizeColour;
        }

        private static Vec2 PointAlong(IList<Vec2> points, double fraction)
        {
            double total = 0;
            for (int i = 1; i < points.Count; i++)
                total += points[i - 1].DistanceTo(points[i]);

            double target = total * fraction;
            double walked = 0;
            for (int i = 1; i < points.Count; i++)
            {
                double length = points[i - 1].DistanceTo(points[i]);
                if (walked + length >= target && length > 1e-9)
                {
                    double t = (target - walked) / length;
                    return points[i - 1] + (points[i] - points[i - 1]) * t;
                }

                walked += length;
            }

            return points[points.Count - 1];
        }

        // Redraws the transients: pipes by size, refnet bodies, E and S labels, the selection and the
        // cases the checks circled. Transient graphics and a modal dialog: if AutoCAD does not repaint
        // while the dialog is open, the static preview drawn at the start still shows.
        private void UpdatePreview(SizingView view)
        {
            if (_preview == null)
                return;

            _preview.Clear();
            double scale = PipeTransients.ViewSize();
            double width = scale / 400.0;
            double text = scale / 90.0;

            HashSet<int> selectedSegments = new HashSet<int>(view.SelectedSegments);
            HashSet<int> selectedUnits = new HashSet<int>(view.SelectedUnits);

            for (int i = 0; i < _topology.Segments.Count; i++)
            {
                TopologySegment segment = _topology.Segments[i];
                if (selectedSegments.Contains(i))
                    _preview.AddPolyline(segment.Points, segment.Elevation, HighlightColour, scale / 120.0);
            }

            for (int i = 0; i < _topology.Segments.Count && i < _last.Segments.Count; i++)
            {
                TopologySegment segment = _topology.Segments[i];
                _preview.AddPolyline(segment.Points, segment.Elevation, ColourOf(_last.Segments[i]), width);
                Vec2 middle = PointAlong(segment.Points, 0.5);
                _preview.AddText(
                    middle + new Vec2(text * 0.4, text * 0.6),
                    "S" + (i + 1).ToString(CultureInfo.InvariantCulture),
                    text,
                    selectedSegments.Contains(i) ? HighlightColour : (short)2,
                    segment.Elevation);
            }

            foreach (TopologyRefnet refnet in _topology.Refnets)
            {
                _preview.AddPolyline(
                    new List<Vec2> { refnet.Inlet, refnet.Main, refnet.Branch }, _elevation, 6, scale / 150.0);
            }

            for (int i = 0; i < _topology.Ends.Count; i++)
            {
                TopologyEnd end = _topology.Ends[i];
                bool selected = selectedUnits.Contains(i);
                _preview.AddCircle(end.Point, selected ? scale / 45.0 : scale / 90.0, _elevation, selected ? HighlightColour : (short)3);
                _preview.AddText(
                    end.Point + new Vec2(text * 0.8, text * 0.8),
                    "E" + (i + 1).ToString(CultureInfo.InvariantCulture),
                    text,
                    selected ? HighlightColour : (short)3,
                    _elevation);
            }

            foreach (Vec2 point in _clean.ReportPoints)
                _preview.AddCircle(point, scale / 40.0, _elevation, 2);
            foreach (Vec2 point in _clean.WarningPoints)
                _preview.AddCircle(point, scale / 40.0, _elevation, 2);
            foreach (Vec2 point in _topology.WarningPoints)
                _preview.AddCircle(point, scale / 40.0, _elevation, 2);

            TryUpdateScreen();
        }

        private void TryUpdateScreen()
        {
            try
            {
                _editor.UpdateScreen();
            }
            catch (System.Exception)
            {
                // A repaint request that is refused only delays the preview.
            }
        }
    }
}
