using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using HvacAiCadAddin.Commands;
using HvacAiCadAddin.Norm;

namespace HvacAiCadAddin.Ducts
{
    internal sealed class DuctLineWriteSummary
    {
        public int Runs;
        public int Ducts;
        public int Fittings;
        public int Taps;
        public int LinesErased;
        public int TagsCleared;
        public readonly List<string> Notes = new List<string>();
        public readonly List<string> Errors = new List<string>();
    }

    // Reads a selected single line into the AutoCAD-free form: vertices with bulges, its HVACNORM size
    // tag (system and size), layer and elevation.
    internal static class DuctLineReader
    {
        internal static DuctLineCurve Read(Entity entity, int id)
        {
            DuctLineCurve curve = new DuctLineCurve { Id = id, Layer = entity.Layer };
            Polyline polyline = entity as Polyline;
            if (polyline != null)
            {
                for (int i = 0; i < polyline.NumberOfVertices; i++)
                {
                    Autodesk.AutoCAD.Geometry.Point2d p = polyline.GetPoint2dAt(i);
                    curve.Vertices.Add(new OutlineVertex(p.X, p.Y, polyline.GetBulgeAt(i)));
                }

                if (polyline.Closed && polyline.NumberOfVertices > 1)
                {
                    Autodesk.AutoCAD.Geometry.Point2d p = polyline.GetPoint2dAt(0);
                    curve.Vertices.Add(new OutlineVertex(p.X, p.Y, 0));
                }

                curve.Elevation = polyline.Elevation;
            }
            else if (entity is Line)
            {
                Line line = (Line)entity;
                curve.Vertices.Add(new OutlineVertex(line.StartPoint.X, line.StartPoint.Y, 0));
                curve.Vertices.Add(new OutlineVertex(line.EndPoint.X, line.EndPoint.Y, 0));
                curve.Elevation = line.StartPoint.Z;
            }
            else if (entity is Arc)
            {
                Arc arc = (Arc)entity;
                double sweep = arc.EndAngle - arc.StartAngle;
                if (sweep < 0)
                    sweep += 2 * Math.PI;
                curve.Vertices.Add(new OutlineVertex(arc.StartPoint.X, arc.StartPoint.Y, Math.Tan(sweep / 4)));
                curve.Vertices.Add(new OutlineVertex(arc.EndPoint.X, arc.EndPoint.Y, 0));
                curve.Elevation = arc.Center.Z;
            }
            else if (entity is Curve)
            {
                // Splines and old-style polylines: the ends (splines) or the vertices at whole parameters.
                Curve other = (Curve)entity;
                curve.IsSpline = entity is Spline;
                int last = curve.IsSpline ? 1 : (int)Math.Round(other.EndParam);
                for (int i = 0; i <= last; i++)
                {
                    Autodesk.AutoCAD.Geometry.Point3d p = curve.IsSpline
                        ? (i == 0 ? other.StartPoint : other.EndPoint)
                        : other.GetPointAtParameter(Math.Min(i, other.EndParam));
                    curve.Vertices.Add(new OutlineVertex(p.X, p.Y, 0));
                }

                curve.Elevation = other.StartPoint.Z;
            }
            else
            {
                return null;
            }

            Takeoff.SizeTag tag = NormWriter.ReadTag(entity);
            if (tag != null && tag.Kind == Takeoff.SizeTagKind.Duct)
            {
                curve.System = tag.System ?? "";
                curve.Size = tag.Round || !(tag.Size2Mm > 0) ? DuctSize.Round(tag.SizeMm) : DuctSize.Rect(tag.SizeMm, tag.Size2Mm);
                if (!curve.Size.IsValid)
                    curve.Size = null;
            }

            return curve;
        }
    }

    // HVACDUCTLINE writer. Layers and duct blocks are made available first, with no transaction open (the
    // HVACDUCT library imports them from the template, or clones the nearest size); then, in one
    // transaction, each solved run gets its REC_/Round D blocks (Distance3 = straight length), its elbows
    // and transitions and, for a branch, the tap on the main's side wall, all on the run's system layer.
    // The single lines of the drawn runs are erased, or kept with their HVACNORM size tag removed so that
    // HVACQTO does not count them next to the new blocks. Needs only AcDbMgd and AcCoreMgd.
    internal static class DuctLineWriter
    {
        internal static DuctLineWriteSummary Write(
            Database db,
            DuctLinePlan plan,
            Func<DuctLineRun, string> layerOf,
            TemplateSource template,
            IList<DuctSize> templateSizes,
            FittingSettings fittings,
            IDictionary<int, ObjectId> curveIds,
            bool eraseLines)
        {
            DuctLineWriteSummary summary = new DuctLineWriteSummary();
            Dictionary<string, ObjectId> blocks = new Dictionary<string, ObjectId>(StringComparer.Ordinal);
            Dictionary<DuctLineRun, string> layers = new Dictionary<DuctLineRun, string>();
            HashSet<int> skippedCurves = new HashSet<int>();

            foreach (DuctLineRun run in plan.Runs.Where(r => r.Ok))
            {
                string layer = layerOf(run);
                string error;
                if (!DuctBlockLibrary.EnsureLayer(db, layer, template, out error))
                {
                    summary.Errors.Add(error);
                    skippedCurves.UnionWith(run.Curves);
                    continue;
                }

                bool ok = true;
                foreach (DuctSize size in run.Sizes.Distinct())
                {
                    if (blocks.ContainsKey(size.BlockName))
                        continue;
                    string note;
                    ObjectId id = DuctBlockLibrary.EnsureBlock(db, size, template, templateSizes, out note, out error);
                    if (id.IsNull)
                    {
                        summary.Errors.Add(error);
                        ok = false;
                        break;
                    }

                    if (note != null)
                        summary.Notes.Add(note);
                    blocks[size.BlockName] = id;
                }

                if (ok)
                    layers[run] = layer;
                else
                    skippedCurves.UnionWith(run.Curves);
            }

            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                foreach (KeyValuePair<DuctLineRun, string> pair in layers)
                {
                    DuctLineRun run = pair.Key;
                    string layer = pair.Value;
                    summary.Runs++;
                    if (run.StartsWithTap)
                    {
                        Outline tap = FittingShapes.Tap(run.TapWallPoint, run.TapMainAxis, run.TapOutward, run.Sizes[0], fittings);
                        DuctBlockLibrary.AddFitting(db, transaction, space, tap, run.Elevation, layer,
                            RouteFitting.TapKind, run.Sizes[0].Label);
                        summary.Taps++;
                    }

                    foreach (RouteStraight straight in run.Result.Straights)
                    {
                        if (straight.Length <= 1e-6)
                            continue;
                        DuctBlockLibrary.InsertDuct(transaction, space, blocks[straight.Size.BlockName], straight.Start,
                            run.Elevation, straight.Direction, layer, straight.Length);
                        summary.Ducts++;
                    }

                    foreach (RouteFitting fitting in run.Result.Fittings)
                    {
                        DuctBlockLibrary.AddFitting(db, transaction, space, fitting.Shape, run.Elevation, layer,
                            fitting.Kind, fitting.SizeText);
                        if (fitting.Centreline != null)
                        {
                            DuctBlockLibrary.AddFitting(db, transaction, space, fitting.Centreline, run.Elevation, layer,
                                fitting.Kind, fitting.SizeText);
                        }

                        summary.Fittings++;
                    }

                    summary.Notes.AddRange(run.Result.Notes);
                }

                // The single lines of what was drawn.
                foreach (int curve in plan.DoneCurves)
                {
                    ObjectId id;
                    if (skippedCurves.Contains(curve) || !curveIds.TryGetValue(curve, out id) || id.IsErased)
                        continue;
                    Entity entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                    LayerTableRecord onLayer = (LayerTableRecord)transaction.GetObject(entity.LayerId, OpenMode.ForRead);
                    if (onLayer.IsLocked)
                        continue;
                    entity.UpgradeOpen();
                    if (eraseLines)
                    {
                        entity.Erase();
                        summary.LinesErased++;
                    }
                    else if (NormWriter.ReadTag(entity) != null)
                    {
                        NormWriter.ClearTags(entity);
                        summary.TagsCleared++;
                    }
                }

                transaction.Commit();
            }

            return summary;
        }
    }
}
