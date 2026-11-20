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
    /// <summary>What the HVACDRAIN sizing dialog accepted.</summary>
    internal sealed class DrainSizingOutcome
    {
        public DrainSizingResult Result;

        /// <summary>The capacity in kW of each indoor unit (0 means none).</summary>
        public double[] UnitKw;
    }

    // Draws the HVACDRAIN preview as transient graphics: the pipes with their chamfers and slants, coloured
    // by size (green when sizing is off), the segment labels S1, S2, ... at the label spots and the unit
    // labels E1, E2, ... at the pipe ends, the selected rows highlighted in white, and the cases the checks
    // reported circled in yellow. Nothing is written to the drawing.
    internal static class DrainPreviewDrawer
    {
        private const short HighlightColour = 7;
        private const short WarningColour = 2;
        private const short UnsizedColour = 3;

        internal static void Draw(
            PipeTransients preview,
            DrainPlan plan,
            DrainSizingResult sizing,
            ICollection<int> selectedUnits,
            ICollection<int> selectedSegments,
            double elevation)
        {
            double scale = PipeTransients.ViewSize();
            double width = scale / 400.0;
            double text = scale / 90.0;
            DrainTree tree = plan.Tree;

            for (int i = 0; i < tree.Segments.Count; i++)
            {
                if (selectedSegments != null && selectedSegments.Contains(i))
                    preview.AddPolyline(plan.Geometry.Segments[i], tree.Segments[i].Elevation, HighlightColour, scale / 120.0);
            }

            for (int i = 0; i < tree.Segments.Count; i++)
            {
                short colour = sizing != null && i < sizing.Segments.Count
                    ? DrainSizing.PreviewColour(sizing.Segments[i].Size)
                    : UnsizedColour;
                preview.AddPolyline(plan.Geometry.Segments[i], tree.Segments[i].Elevation, colour, width);
                if (sizing == null)
                    continue;

                SizeLabelPlacement spot;
                if (SizeLabels.TryPlace(plan.Geometry.Segments[i], text * 0.6, "S" + (i + 1).ToString(CultureInfo.InvariantCulture), out spot))
                {
                    bool selected = selectedSegments != null && selectedSegments.Contains(i);
                    preview.AddText(
                        spot.Position, spot.Text, text, selected ? HighlightColour : WarningColour, tree.Segments[i].Elevation);
                }
            }

            foreach (RefrigPath path in tree.Unreached)
                preview.AddPolyline(path.Points, path.Elevation, 8, width);

            for (int i = 0; i < tree.Units.Count; i++)
            {
                bool selected = selectedUnits != null && selectedUnits.Contains(i);
                Vec2 at = tree.Units[i].Point;
                preview.AddCircle(at, selected ? scale / 45.0 : scale / 90.0, elevation, selected ? HighlightColour : (short)3);
                if (sizing != null)
                {
                    preview.AddText(
                        at + new Vec2(text * 0.8, text * 0.8),
                        "E" + (i + 1).ToString(CultureInfo.InvariantCulture),
                        text,
                        selected ? HighlightColour : (short)3,
                        elevation);
                }
            }

            // The drain point.
            preview.AddCircle(tree.Nodes[tree.RootNode].Point, scale / 60.0, elevation, 1);

            foreach (Vec2 point in plan.ReportPoints)
                preview.AddCircle(point, scale / 40.0, elevation, WarningColour);
        }
    }

    // The AutoCAD side of the HVACDRAIN sizing dialog: turns the drain tree and the capacity readings into
    // SizingForm rows (kW mode: the capacity cells are numbers), calls the AutoCAD-free sizer on every
    // change, and keeps the transient preview in step with the selection. Nothing is written here.
    internal sealed class DrainSizingSession : IDisposable
    {
        private readonly Editor _editor;
        private readonly DrainPlan _plan;
        private readonly DrainSettings _settings;
        private readonly UnitReading[] _readings;
        private readonly double _elevation;
        private PipeTransients _preview;
        private DrainSizingResult _last;

        internal DrainSizingSession(
            Editor editor, DrainPlan plan, DrainSettings settings, UnitReading[] readings, double elevation)
        {
            _editor = editor;
            _plan = plan;
            _settings = settings;
            _readings = readings;
            _elevation = elevation;
        }

        /// <summary>Shows the dialog. Returns null when the user cancels.</summary>
        internal DrainSizingOutcome Run(string applyNote)
        {
            SizingSetup setup = BuildSetup(applyNote);
            _preview = new PipeTransients();
            try
            {
                using (SizingForm form = new SizingForm(setup))
                {
                    if (Application.ShowModalDialog(form) != System.Windows.Forms.DialogResult.OK)
                        return null;

                    SizingRequestView request = form.FinalRequest;
                    DrainSizingResult result = Compute(request);
                    if (!result.CanApply)
                        return null;
                    return new DrainSizingOutcome { Result = result, UnitKw = (double[])request.Capacities.Clone() };
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

        // ------------------------------------------------------------------ setup

        private SizingSetup BuildSetup(string applyNote)
        {
            SizingSetup setup = new SizingSetup
            {
                Title = "HVACDRAIN: size condensate pipes (Daikin Si39)",
                ApplyNote = applyNote,
                UnitsTitle = "Indoor units",
                CapacityTitle = "Capacity (kW)",
                SegmentsTitle = "Pipe segments (flow runs toward the drain point, S1)",
                KindTitle = "Flow L/h",
                TotalTitle = "Σ kW",
                TotalWidth = 70,
                Recompute = Recompute,
                ViewChanged = UpdatePreview
            };

            List<string> sizes = new List<string>();
            foreach (int size in DrainSizing.Choices)
                sizes.Add(DrainSizing.SizeText(size));
            setup.SizeChoices = sizes;

            List<SizingUnitRow> units = new List<SizingUnitRow>();
            for (int i = 0; i < _plan.Tree.Units.Count; i++)
            {
                UnitReading reading = i < _readings.Length ? _readings[i] : null;
                units.Add(new SizingUnitRow
                {
                    Name = "E" + (i + 1).ToString(CultureInfo.InvariantCulture),
                    Source = reading == null ? "" : SourceText(reading),
                    Capacity = reading == null ? 0 : reading.Kw
                });
            }

            setup.Units = units;
            return setup;
        }

        // Where a capacity came from. The reader's own text mentions capacity indexes, which a drain has no use for.
        private static string SourceText(UnitReading reading)
        {
            string kw = reading.Kw.ToString("0.0#", CultureInfo.InvariantCulture) + " kW";
            switch (reading.Kind)
            {
                case "xdata":
                    return "Saved by a previous run (" + kw + ")";
                case "block":
                    return "Block " + reading.Name;
                default:
                    return "Label " + (string.IsNullOrEmpty(reading.Name) ? "" : reading.Name + " ") + "(" + kw + ")";
            }
        }

        private DrainSizingResult Compute(SizingRequestView request)
        {
            Dictionary<int, int> manual = new Dictionary<int, int>();
            if (request.Overrides != null)
            {
                foreach (KeyValuePair<int, string> pair in request.Overrides)
                {
                    int size;
                    if (DrainSizing.TryParseSize(pair.Value, out size))
                        manual[pair.Key] = size;
                }
            }

            return DrainSizing.Compute(
                _plan.Tree, request.Capacities, _settings.Slope, _settings.CondensateFactor, manual);
        }

        private static string Number(double value, string format)
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        private SizingState Recompute(SizingRequestView request)
        {
            DrainSizingResult result = Compute(request);
            _last = result;

            double totalKw = 0;
            foreach (double kw in request.Capacities)
            {
                if (kw > 0 && !double.IsInfinity(kw))
                    totalKw += kw;
            }

            SizingState state = new SizingState { CanApply = result.CanApply };
            StringBuilder header = new StringBuilder();
            header.Append(_plan.Tree.Units.Count.ToString(CultureInfo.InvariantCulture)).Append(" indoor unit(s)");
            header.Append(" · total ").Append(Number(totalKw, "0.##")).Append(" kW");
            header.Append(" · ").Append(Number(totalKw * _settings.CondensateFactor, "0.#")).Append(" L/h");
            header.Append(" · slope ").Append(_settings.SlopeText);
            state.HeaderText = header.ToString();
            state.InfoText = "Daikin Si39 basis: " + Number(_settings.CondensateFactor, "0.###")
                + " L/h per kW. A pipe never gets smaller toward the drain point.";

            for (int i = 0; i < result.Segments.Count; i++)
            {
                DrainSegmentSizing row = result.Segments[i];
                state.Segments.Add(new SizingSegmentView
                {
                    Name = "S" + (i + 1).ToString(CultureInfo.InvariantCulture),
                    Kind = Number(row.FlowLh, "0.#"),
                    Total = Number(row.TotalKw, "0.##"),
                    Units = row.UnitCount.ToString(CultureInfo.InvariantCulture),
                    AutoText = row.AutoText,
                    SizeText = row.SizeText,
                    Manual = row.Manual,
                    Warning = row.Warning
                });
            }

            DrainGeometryResult geometry = _plan.Geometry;
            state.RefnetText = string.Format(
                CultureInfo.InvariantCulture,
                "Joints: {0} bend(s) chamfered, {1} branch(es) slanted, {2} kept as drawn. "
                + "The slant is {3} mm long (cut {4} mm).",
                geometry.Chamfers,
                geometry.Slants,
                geometry.Kept,
                Number(_settings.SlantLength, "0.###"),
                Number(geometry.Cut, "0.##"));

            state.Warnings.AddRange(_plan.Reports);
            state.Warnings.AddRange(result.Warnings);
            state.Notes.AddRange(result.Notes);
            return state;
        }

        // ------------------------------------------------------------------ preview

        // Redraws the transients with the current sizes and the selected rows highlighted. Transient graphics
        // and a modal dialog: if AutoCAD does not repaint while the dialog is open, the preview drawn when
        // it opened still shows.
        private void UpdatePreview(SizingView view)
        {
            if (_preview == null || _last == null)
                return;

            _preview.Clear();
            DrainPreviewDrawer.Draw(
                _preview,
                _plan,
                _last,
                new HashSet<int>(view.SelectedUnits),
                new HashSet<int>(view.SelectedSegments),
                _elevation);
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
