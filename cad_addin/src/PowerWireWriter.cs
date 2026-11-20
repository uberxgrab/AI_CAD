using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Pipes
{
    // Non-interactive AutoCAD side of HVACPOWERWIRE: writes the planned wire in one transaction. Each run
    // becomes one LWPOLYLINE on the chosen wire layer (colour, linetype and lineweight ByLayer, elevation
    // kept), the selected curves are erased, lines of other networks are written back as cleaned, and the
    // cable text goes beside each unit's leg, on the wire layer, tagged with HVACAI_POWERWIRE xdata so that a
    // second run replaces the texts of the first. The layer must already exist (the command imports it
    // first). It needs only AcDbMgd, and reuses the old-label cleaner of RefrigSizeWriter.
    // See AGENTS.md, "HVACPOWERWIRE: draw the indoor-unit power wire".

    internal sealed class PowerWireWriteRequest
    {
        public IList<PipeSource> Sources;
        public PowerWireResult Result;
        public PowerWireSettings Settings;

        /// <summary>The wire layer, which must exist.</summary>
        public string LayerName;

        /// <summary>The cable text style; <see cref="ObjectId.Null"/> uses the current style.</summary>
        public ObjectId TextStyle = ObjectId.Null;
    }

    internal sealed class PowerWireWriteSummary
    {
        public int Wires;
        public int Spurs;
        public int Texts;
        public int TextsRemoved;
        /// <summary>Old cable texts left in place because their layer is locked.</summary>
        public int TextsLocked;
        public int Erased;
        public int OtherNetworks;
    }

    internal static class PowerWireWriter
    {
        /// <summary>The registered application name that tags the cable texts this command writes.</summary>
        internal const string TextApp = "HVACAI_POWERWIRE";

        /// <summary>
        /// Writes the wire in one transaction. On any failure nothing is changed.
        /// </summary>
        internal static bool Write(
            Database db, PowerWireWriteRequest request, out PowerWireWriteSummary summary, out string error)
        {
            summary = new PowerWireWriteSummary();
            error = null;

            PowerWireResult result = request.Result;
            PowerWireSettings settings = request.Settings;

            try
            {
                using (Transaction transaction = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = (BlockTableRecord)transaction.GetObject(
                        db.CurrentSpaceId, OpenMode.ForWrite);

                    if (settings.WriteText)
                        RefrigSizeWriter.EnsureRegApp(transaction, db, TextApp);

                    Dictionary<int, PipeSource> byCurveId = new Dictionary<int, PipeSource>();
                    foreach (PipeSource source in request.Sources)
                        byCurveId[source.Curve.Id] = source;

                    // Where the old and the new wire are, for finding the cable texts of an earlier run.
                    List<IList<Vec2>> geometry = new List<IList<Vec2>>();
                    foreach (PipeSource source in request.Sources)
                    {
                        List<Vec2> old = new List<Vec2>();
                        foreach (OutlineVertex vertex in source.Curve.Vertices)
                            old.Add(new Vec2(vertex.X, vertex.Y));
                        geometry.Add(old);
                    }

                    foreach (PowerWireRun run in result.Runs)
                    {
                        Polyline polyline = NewPolyline(run.Points, result.Elevation);
                        polyline.Layer = request.LayerName;
                        polyline.ColorIndex = 256;
                        polyline.LinetypeId = db.ByLayerLinetype;
                        polyline.LineWeight = LineWeight.ByLayer;
                        space.AppendEntity(polyline);
                        transaction.AddNewlyCreatedDBObject(polyline, true);
                        if (run.Kind == PowerWireRunKind.Wire)
                            summary.Wires++;
                        else
                            summary.Spurs++;
                        geometry.Add(run.Points);
                    }

                    // Lines of another network are not part of the wire: they stay as the cleaner left them.
                    foreach (RefrigPath path in result.Unreached)
                    {
                        Polyline polyline = NewPolyline(path.Points, path.Elevation);
                        PipeSource origin;
                        if (byCurveId.TryGetValue(path.SourceCurve, out origin))
                        {
                            Entity original = (Entity)transaction.GetObject(origin.Id, OpenMode.ForRead);
                            polyline.SetPropertiesFrom(original);
                        }

                        polyline.Layer = path.Layer;
                        space.AppendEntity(polyline);
                        transaction.AddNewlyCreatedDBObject(polyline, true);
                        summary.OtherNetworks++;
                    }

                    foreach (PipeSource source in request.Sources)
                    {
                        Entity original = transaction.GetObject(source.Id, OpenMode.ForWrite) as Entity;
                        if (original != null && !original.IsErased)
                        {
                            original.Erase();
                            summary.Erased++;
                        }
                    }

                    if (settings.WriteText)
                    {
                        WriteSummary removed = new WriteSummary();
                        RefrigSizeWriter.RemoveOldLabels(
                            transaction,
                            space,
                            geometry,
                            settings.HookSize + 3 * settings.TextHeight,
                            removed,
                            TextApp);
                        summary.TextsRemoved = removed.LabelsRemoved;
                        summary.TextsLocked = removed.LabelsLocked;

                        foreach (PowerWireUnit unit in result.Units)
                        {
                            if (unit.Text == null)
                                continue;
                            AddText(transaction, db, space, unit.Text, result.Elevation, request);
                            summary.Texts++;
                        }
                    }

                    transaction.Commit();
                }

                return true;
            }
            catch (System.Exception ex)
            {
                summary = new PowerWireWriteSummary();
                error = "Could not write the power wire: " + ex.Message;
                return false;
            }
        }

        private static Polyline NewPolyline(IList<Vec2> points, double elevation)
        {
            Polyline polyline = new Polyline();
            for (int i = 0; i < points.Count; i++)
                polyline.AddVertexAt(i, new Point2d(points[i].X, points[i].Y), 0, 0, 0);
            polyline.Elevation = elevation;
            return polyline;
        }

        // The text starts at its anchor, or ends there when it is right justified. The anchor is on the
        // baseline. Order matters: AppendEntity and AddNewlyCreatedDBObject first, then AdjustAlignment.
        private static void AddText(
            Transaction transaction,
            Database db,
            BlockTableRecord space,
            PowerWireText placement,
            double elevation,
            PowerWireWriteRequest request)
        {
            PowerWireSettings settings = request.Settings;
            Point3d at = new Point3d(placement.Anchor.X, placement.Anchor.Y, elevation);

            DBText text = new DBText();
            text.SetDatabaseDefaults(db);
            text.Layer = request.LayerName;
            if (!request.TextStyle.IsNull)
                text.TextStyleId = request.TextStyle;
            text.Height = settings.TextHeight;
            text.TextString = settings.CableText;
            text.Rotation = placement.Rotation;
            text.Position = at;
            if (placement.RightJustified)
            {
                text.HorizontalMode = TextHorizontalMode.TextRight;
                text.VerticalMode = TextVerticalMode.TextBase;
                text.AlignmentPoint = at;
            }

            using (ResultBuffer buffer = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, TextApp),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "CABLETEXT")))
            {
                text.XData = buffer;
            }

            space.AppendEntity(text);
            transaction.AddNewlyCreatedDBObject(text, true);
            if (placement.RightJustified)
            {
                try
                {
                    // Settles the base point of right-justified text; the text is placed correctly without it.
                    text.AdjustAlignment(db);
                }
                catch (System.Exception)
                {
                }
            }
        }
    }
}
