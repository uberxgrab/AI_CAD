using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using HvacAiCadAddin.ControlWires;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Pipes;

namespace HvacAiCadAddin.Commands
{
    // HVACCTRLWIRE: draws the control wires of a thermostat to many indoor units from one template route.
    // The user selects the indoor-unit blocks, picks a template LINE/LWPOLYLINE that already runs from the
    // thermostat to one of the units, and confirms the thermostat end. Each other unit gets its own open
    // polyline on EIA_5 CONTROL WIRE, parallel to the template at k * d (k = 1, 2, 3... on the side the
    // unit lies on; the wire that leaves the route first is the outermost, so the wires do not cross),
    // starting at its own offset position at the thermostat end (no common start). Only the last part
    // leaves the parallel run, perpendicular to the route, and goes to the unit's terminal point: the
    // template unit's terminal point carried into each unit of the same block, or the insertion point of
    // a different block. The cable text (default 2x0.75 MM2, T_unicode, 275 mm) goes beside each exit
    // leg. This class only prompts and previews; ControlWirePlanner (AutoCAD-free) lays the wires out and
    // ControlWireWriter writes them in one transaction (one undo group). The template is never modified.
    // Straight segments only: a template with an arc is refused.
    // See AGENTS.md, "HVACCTRLWIRE: draw control wires from a template route".
    public class HvacCtrlWireCommand
    {
        /// <summary>A block within this distance of a template end counts as touching it (mm).</summary>
        private const double TouchTolerance = 500;

        [CommandMethod("HVACCTRLWIRE", CommandFlags.UsePickSet)]
        public void HvacCtrlWire()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            try
            {
                Run(document);
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage("\nHVACCTRLWIRE stopped: " + ex.Message);
            }
        }

        private static void Run(Document document)
        {
            Database db = document.Database;
            Editor editor = document.Editor;

            // 1. The indoor units: block references from the pickfirst set, or a new selection.
            List<ObjectId> ids = new List<ObjectId>();
            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status == PromptStatus.OK && selection.Value != null)
            {
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    if (id.ObjectClass.DxfName == "INSERT")
                        ids.Add(id);
                }
            }

            if (ids.Count == 0)
            {
                PromptSelectionOptions selectionOptions = new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect the indoor units (block references) to wire: "
                };
                SelectionFilter filter = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "INSERT")
                });
                selection = editor.GetSelection(selectionOptions, filter);
                if (selection.Status != PromptStatus.OK)
                    return;
                ids.AddRange(selection.Value.GetObjectIds());
            }

            List<ControlWireBlock> selected = ControlWireWriter.ReadBlocks(db, ids);
            if (selected.Count == 0)
            {
                editor.WriteMessage("\nNo block references were selected.");
                return;
            }

            // 2. The template route.
            PromptEntityOptions templateOptions = new PromptEntityOptions(
                "\nPick the template control wire (the route from the thermostat to one indoor unit): ");
            templateOptions.SetRejectMessage("\nPick a LINE or a polyline.");
            templateOptions.AddAllowedClass(typeof(Line), true);
            templateOptions.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline), true);
            PromptEntityResult templatePick = editor.GetEntity(templateOptions);
            if (templatePick.Status != PromptStatus.OK)
                return;

            int skipped;
            List<PipeSource> templates = PipeDrawingLibrary.ReadCurves(
                db, new[] { templatePick.ObjectId }, null, out skipped);
            if (templates.Count == 0)
            {
                editor.WriteMessage("\nThe template must be a LINE or a polyline in the XY plane.");
                return;
            }

            PipeCurve curve = templates[0].Curve;
            string routeError;
            if (!ControlWirePlanner.CheckRoute(curve.Vertices, curve.Closed, out routeError))
            {
                editor.WriteMessage("\n" + routeError);
                return;
            }

            double elevation = curve.Elevation;
            Vec2 first = new Vec2(curve.Vertices[0].X, curve.Vertices[0].Y);
            Vec2 last = new Vec2(
                curve.Vertices[curve.Vertices.Count - 1].X, curve.Vertices[curve.Vertices.Count - 1].Y);
            if (first.DistanceTo(last) < 1e-6 && curve.Vertices.Count <= 2)
            {
                editor.WriteMessage("\nThe template route has no length.");
                return;
            }

            // 3. The thermostat end. By default it is the template end that touches no unit.
            List<ControlWireBlock> everything = null;
            double distanceFirst;
            double distanceLast;
            NearestBlock(first, selected, out distanceFirst);
            NearestBlock(last, selected, out distanceLast);
            if (Math.Min(distanceFirst, distanceLast) > TouchTolerance)
            {
                // The template's own unit may not be among the selected blocks: look at every block.
                everything = ControlWireWriter.ReadBlocks(db, ControlWireWriter.BlockIdsInCurrentSpace(db));
                NearestBlock(first, everything, out distanceFirst);
                NearestBlock(last, everything, out distanceLast);
            }

            bool thermostatIsFirst = distanceFirst >= distanceLast;
            Matrix3d ucs = editor.CurrentUserCoordinateSystem;
            double view = PipeTransients.ViewSize();
            using (PipeTransients marker = new PipeTransients())
            {
                while (true)
                {
                    marker.Clear();
                    Vec2 shown = thermostatIsFirst ? first : last;
                    marker.AddCircle(shown, view / 60.0, elevation, 1);
                    marker.AddText(shown + new Vec2(view / 50.0, view / 50.0), "Thermostat", view / 70.0, 1, elevation);

                    PromptPointOptions pointOptions = new PromptPointOptions(
                        "\nPick the thermostat point or [Other end] <circled end>: ");
                    pointOptions.Keywords.Add("Other");
                    pointOptions.AllowNone = true;
                    PromptPointResult pointResult = editor.GetPoint(pointOptions);
                    if (pointResult.Status == PromptStatus.Keyword)
                    {
                        thermostatIsFirst = !thermostatIsFirst;
                        continue;
                    }

                    if (pointResult.Status == PromptStatus.None)
                        break;
                    if (pointResult.Status != PromptStatus.OK)
                        return;

                    // A picked point chooses the template end nearest to it.
                    Point3d picked = pointResult.Value.TransformBy(ucs);
                    Vec2 at = new Vec2(picked.X, picked.Y);
                    thermostatIsFirst = at.DistanceTo(first) <= at.DistanceTo(last);
                    break;
                }
            }

            Vec2 thermostat = thermostatIsFirst ? first : last;
            Vec2 unitEnd = thermostatIsFirst ? last : first;

            // The template's own unit: the block at the other end.
            double unused;
            ControlWireBlock templateUnit = NearestBlock(unitEnd, selected, out unused);
            if (templateUnit == null || unused > TouchTolerance)
            {
                if (everything == null)
                    everything = ControlWireWriter.ReadBlocks(db, ControlWireWriter.BlockIdsInCurrentSpace(db));
                templateUnit = NearestBlock(unitEnd, everything, out unused);
                if (unused > TouchTolerance)
                    templateUnit = null;
            }

            // 4. Settings.
            ControlWireSettings settings;
            using (ControlWireSettingsForm form = new ControlWireSettingsForm())
            {
                if (DialogHost.ShowModal(form) != System.Windows.Forms.DialogResult.OK)
                    return;
                settings = form.Settings;
            }

            // 5. The units to wire and the point each wire ends on.
            List<ControlWireUnit> units = new List<ControlWireUnit>();
            List<string> handles = new List<string>();
            List<string> insertionPoint = new List<string>();
            foreach (ControlWireBlock block in selected)
            {
                if (templateUnit != null && block.Id == templateUnit.Id)
                    continue;

                ControlWireUnit unit = new ControlWireUnit { Name = block.DisplayName, Terminal = block.Position };
                Vec2 mapped;
                if (templateUnit != null
                    && string.Equals(block.Name, templateUnit.Name, StringComparison.OrdinalIgnoreCase)
                    && ControlWireAffine.TryMapTerminal(unitEnd, templateUnit.Transform, block.Transform, out mapped))
                {
                    unit.Terminal = mapped;
                }
                else
                {
                    insertionPoint.Add(block.DisplayName);
                }

                units.Add(unit);
                handles.Add(block.Id.Handle.ToString());
            }

            if (templateUnit == null)
            {
                editor.WriteMessage(
                    "\nNo block touches the unit end of the template: every wire ends on its unit's insertion point.");
            }
            else
            {
                editor.WriteMessage(
                    "\nThe template serves {0}; units of the same block get the same terminal point.",
                    templateUnit.DisplayName);
                if (insertionPoint.Count > 0)
                {
                    editor.WriteMessage(
                        "\nA different block, so its insertion point is the terminal: {0}.",
                        string.Join(", ", insertionPoint));
                }
            }

            if (units.Count == 0)
            {
                editor.WriteMessage("\nEvery selected block is the template's own unit: nothing to wire.");
                return;
            }

            // 6. Plan.
            List<OutlineVertex> route = new List<OutlineVertex>();
            if (thermostatIsFirst)
            {
                foreach (OutlineVertex vertex in curve.Vertices)
                    route.Add(new OutlineVertex(vertex.X, vertex.Y, 0));
            }
            else
            {
                for (int i = curve.Vertices.Count - 1; i >= 0; i--)
                    route.Add(new OutlineVertex(curve.Vertices[i].X, curve.Vertices[i].Y, 0));
            }

            ControlWirePlan plan = ControlWirePlanner.Plan(new ControlWireInput
            {
                Route = route,
                Units = units,
                Spacing = settings.Spacing,
                TextHeight = settings.TextHeight
            });
            if (!plan.Ok)
            {
                editor.WriteMessage("\n" + plan.Error);
                return;
            }

            ReportPlan(editor, plan, settings);

            // 7. Preview and confirm.
            if (!Confirm(editor, plan, thermostat, elevation))
            {
                editor.WriteMessage("\nCancelled: nothing was changed.");
                return;
            }

            // 8. Write.
            using (PipeTemplateHolder template = new PipeTemplateHolder(editor))
            {
                string note;
                string error;
                if (!PipeDrawingLibrary.EnsureNamedLayer(
                    db, ControlWireSettings.LayerName, template.Open, out note, out error))
                {
                    editor.WriteMessage("\n" + error + " Nothing was changed.");
                    return;
                }

                if (note != null)
                    editor.WriteMessage("\n" + note);

                ControlWireWriteRequest request = new ControlWireWriteRequest
                {
                    Plan = plan,
                    Settings = settings,
                    Elevation = elevation,
                    LayerName = ControlWireSettings.LayerName,
                    UnitHandles = handles
                };
                if (settings.WriteText)
                {
                    request.TextStyle = PipeDrawingLibrary.FindTextStyle(db, ControlWireSettings.TextStyleName);
                    if (request.TextStyle.IsNull)
                    {
                        editor.WriteMessage(
                            "\nText style {0} is not in the drawing: the cable text uses the current style.",
                            ControlWireSettings.TextStyleName);
                    }
                }

                int earlier = ControlWireWriter.CountTaggedWires(db);

                ControlWireWriteSummary written;
                if (!ControlWireWriter.Write(db, request, out written, out error))
                {
                    editor.WriteMessage("\n" + error + " Nothing was changed.");
                    return;
                }

                editor.WriteMessage(
                    "\nWrote {0} control wire(s) on {1}. The template route was not changed.",
                    written.Wires, ControlWireSettings.LayerName);
                if (settings.WriteText)
                {
                    editor.WriteMessage(
                        "\nCable text '{0}': {1} written (height {2} mm).",
                        settings.TextString,
                        written.Texts,
                        settings.TextHeight.ToString("0.###", CultureInfo.InvariantCulture));
                }

                if (earlier > 0)
                {
                    editor.WriteMessage(
                        "\nThe drawing already had {0} control wire(s) from an earlier HVACCTRLWIRE run; "
                        + "they were left as they are.",
                        earlier);
                }

                if (plan.Warnings.Count > 0)
                {
                    editor.WriteMessage("\n{0} warning(s); check these:", plan.Warnings.Count);
                    foreach (ControlWireWarning warning in plan.Warnings)
                        editor.WriteMessage("\n  " + warning.Message);
                }
            }
        }

        // The block nearest to a point (distance to its extents), or null when there are none.
        private static ControlWireBlock NearestBlock(
            Vec2 point, IList<ControlWireBlock> blocks, out double distance)
        {
            distance = double.MaxValue;
            ControlWireBlock nearest = null;
            foreach (ControlWireBlock block in blocks)
            {
                double d = block.DistanceTo(point);
                if (d < distance)
                {
                    distance = d;
                    nearest = block;
                }
            }

            return nearest;
        }

        private static void ReportPlan(Editor editor, ControlWirePlan plan, ControlWireSettings settings)
        {
            editor.WriteMessage(
                "\nControl wires: {0} wire(s) at {1} mm spacing, from the thermostat end of the template.",
                plan.Wires.Count, settings.Spacing.ToString("0.###", CultureInfo.InvariantCulture));
            foreach (ControlWire wire in plan.Wires)
            {
                editor.WriteMessage(
                    "\n  {0}: {1}, {2} side, {3} x d = {4} mm off the route, leaves it {5} m from the thermostat, {6} m long.",
                    wire.Label,
                    wire.UnitName,
                    wire.Side > 0 ? "left" : "right",
                    wire.K,
                    Math.Abs(wire.Offset).ToString("0.###", CultureInfo.InvariantCulture),
                    Metres(wire.ExitDistance),
                    Metres(wire.Length));
            }

            foreach (ControlWireWarning warning in plan.Warnings)
                editor.WriteMessage("\nWarning: " + warning.Message);
        }

        private static string Metres(double millimetres)
        {
            return (millimetres / 1000.0).ToString("0.00", CultureInfo.InvariantCulture);
        }

        // Shows the new wires (green), the template (cyan), a label W1, W2... with the unit each wire
        // serves, the thermostat (red circle) and the warnings (yellow circles), then asks. Yes is the
        // default.
        private static bool Confirm(Editor editor, ControlWirePlan plan, Vec2 thermostat, double elevation)
        {
            double view = PipeTransients.ViewSize();
            using (PipeTransients preview = new PipeTransients())
            {
                preview.AddPolyline(plan.Route, elevation, 4, view / 250.0);
                foreach (ControlWire wire in plan.Wires)
                {
                    preview.AddPolyline(wire.Points, elevation, 3, view / 500.0);
                    Vec2 end = wire.Points[wire.Points.Count - 1];
                    preview.AddText(
                        end + new Vec2(view / 80.0, view / 80.0), wire.Label + ": " + wire.UnitName,
                        view / 70.0, 7, elevation);
                }

                preview.AddCircle(thermostat, view / 60.0, elevation, 1);
                foreach (ControlWireWarning warning in plan.Warnings)
                    preview.AddCircle(warning.Point, view / 40.0, elevation, 2);

                editor.WriteMessage(
                    "\nPreview: {0} new wire(s) in green, the template in cyan, the thermostat circled in red, "
                    + "{1} warning(s) circled in yellow.",
                    plan.Wires.Count, plan.Warnings.Count);

                PromptKeywordOptions options = new PromptKeywordOptions(
                    "\nDraw the control wires? [Yes/No] <Yes>: ", "Yes No");
                options.Keywords.Default = "Yes";
                options.AllowNone = true;
                PromptResult answer = editor.GetKeywords(options);
                if (answer.Status == PromptStatus.None)
                    return true;
                return answer.Status == PromptStatus.OK
                    && string.Equals(answer.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
