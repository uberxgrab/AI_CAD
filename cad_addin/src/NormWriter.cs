using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using HvacAiCadAddin.Takeoff;

namespace HvacAiCadAddin.Norm
{
    internal sealed class NormWriteSummary
    {
        public int Erased;
        public int Moved;
        public int Tagged;
        public int Reset;
        public int Split;
        public int Missing;
        public int Locked;
        public int SplitFailed;
    }

    // HVACNORM writer: applies the planned actions in one transaction. Duplicates are erased, overrides
    // reset to ByLayer, a curve whose size changes inside it is cut with GetSplitCurves (the pieces keep
    // layer, colour, linetype and xdata; they are appended and the original erased), each piece is moved
    // to its template layer (which must already exist: the command imports it from the template first) or
    // gets the HVACAI_PIPE / HVACAI_DUCT size tag. Objects on locked layers are left alone. Needs only
    // AcDbMgd, so the headless tests run it in accoreconsole.
    internal static class NormWriter
    {
        private const double ParamTolerance = 1e-6;

        internal static NormWriteSummary Write(Database db, IList<EntityPlan> plans, IDictionary<string, ObjectId> handles)
        {
            NormWriteSummary summary = new NormWriteSummary();
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                EnsureRegApp(transaction, db, SizeTag.PipeApp);
                EnsureRegApp(transaction, db, SizeTag.DuctApp);
                LayerTable layers = (LayerTable)transaction.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (EntityPlan plan in plans)
                {
                    ObjectId id;
                    if (!handles.TryGetValue(plan.Handle, out id) || id.IsNull || id.IsErased)
                    {
                        summary.Missing++;
                        continue;
                    }

                    Curve curve = transaction.GetObject(id, OpenMode.ForRead) as Curve;
                    if (curve == null)
                    {
                        summary.Missing++;
                        continue;
                    }

                    if (IsLocked(transaction, layers, curve.Layer))
                    {
                        summary.Locked++;
                        continue;
                    }

                    curve.UpgradeOpen();
                    if (plan.EraseWhole)
                    {
                        curve.Erase();
                        summary.Erased++;
                        continue;
                    }

                    if (plan.ResetOverrides)
                    {
                        curve.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                        curve.Linetype = "ByLayer";
                        curve.LineWeight = LineWeight.ByLayer;
                        summary.Reset++;
                    }

                    if (plan.Pieces.Count == 1)
                    {
                        Apply(curve, plan.Pieces[0], layers, summary);
                        continue;
                    }

                    List<Curve> parts = Split(transaction, curve, plan);
                    if (parts == null)
                    {
                        summary.SplitFailed++;
                        continue;
                    }

                    summary.Split++;
                    BlockTableRecord owner = (BlockTableRecord)transaction.GetObject(curve.OwnerId, OpenMode.ForWrite);
                    for (int i = 0; i < parts.Count; i++)
                    {
                        if (plan.Pieces[i].Erase)
                        {
                            parts[i].Dispose();
                            summary.Erased++;
                            continue;
                        }

                        owner.AppendEntity(parts[i]);
                        transaction.AddNewlyCreatedDBObject(parts[i], true);
                        Apply(parts[i], plan.Pieces[i], layers, summary);
                    }

                    curve.Erase();
                }

                transaction.Commit();
            }

            return summary;
        }

        // The pieces of the curve, cut where each planned piece after the first starts; null (and nothing
        // changed) when the cut points do not give exactly one part per planned piece.
        private static List<Curve> Split(Transaction transaction, Curve curve, EntityPlan plan)
        {
            if (curve.Closed)
                return null;
            List<double> parameters = new List<double>();
            double start = curve.StartParam;
            double end = curve.EndParam;
            for (int i = 1; i < plan.Pieces.Count; i++)
            {
                double[] at = plan.Pieces[i].SplitAt;
                if (at == null)
                    return null;
                try
                {
                    Point3d near = curve.GetClosestPointTo(new Point3d(at[0], at[1], 0), Vector3d.ZAxis, false);
                    double parameter = curve.GetParameterAtPoint(near);
                    if (parameter <= start + ParamTolerance || parameter >= end - ParamTolerance)
                        return null;
                    if (parameters.Count > 0 && parameter <= parameters[parameters.Count - 1] + ParamTolerance)
                        return null;
                    parameters.Add(parameter);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    return null;
                }
            }

            DBObjectCollection pieces;
            try
            {
                pieces = curve.GetSplitCurves(new DoubleCollection(parameters.ToArray()));
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return null;
            }

            List<Curve> parts = pieces.Cast<DBObject>().OfType<Curve>().ToList();
            if (parts.Count != plan.Pieces.Count || parts.Count != pieces.Count)
            {
                foreach (DBObject piece in pieces)
                    piece.Dispose();
                return null;
            }

            return parts;
        }

        private static void Apply(Entity entity, PiecePlan piece, LayerTable layers, NormWriteSummary summary)
        {
            if (piece.Erase)
            {
                entity.Erase();
                summary.Erased++;
                return;
            }

            if (piece.TargetLayer != null && layers.Has(piece.TargetLayer))
            {
                entity.Layer = piece.TargetLayer;
                ClearTags(entity); // the layer now says the size
                summary.Moved++;
            }

            if (piece.Tag != null)
            {
                SetTag(entity, piece.Tag);
                summary.Tagged++;
            }
        }

        internal static void SetTag(Entity entity, SizeTag tag)
        {
            ClearTags(entity);
            ResultBuffer data = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, tag.App));
            foreach (KeyValuePair<short, object> value in tag.ToValues())
                data.Add(new TypedValue(value.Key, value.Value));
            entity.XData = data;
            data.Dispose();
        }

        /// <summary>Removes both size tags (an xdata buffer with only the application name deletes it).</summary>
        internal static void ClearTags(Entity entity)
        {
            foreach (string app in new[] { SizeTag.PipeApp, SizeTag.DuctApp })
            {
                using (ResultBuffer existing = entity.GetXDataForApplication(app))
                {
                    if (existing == null)
                        continue;
                }

                using (ResultBuffer clear = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, app)))
                    entity.XData = clear;
            }
        }

        /// <summary>The size tag of an entity, or null.</summary>
        internal static SizeTag ReadTag(Entity entity)
        {
            foreach (string app in new[] { SizeTag.PipeApp, SizeTag.DuctApp })
            {
                using (ResultBuffer data = entity.GetXDataForApplication(app))
                {
                    if (data == null)
                        continue;
                    TypedValue[] values = data.AsArray();
                    List<KeyValuePair<short, object>> pairs = new List<KeyValuePair<short, object>>();
                    for (int i = 1; i < values.Length; i++)
                        pairs.Add(new KeyValuePair<short, object>(values[i].TypeCode, values[i].Value));
                    SizeTag tag;
                    if (SizeTag.TryParse(app, pairs, out tag))
                        return tag;
                }
            }

            return null;
        }

        private static bool IsLocked(Transaction transaction, LayerTable layers, string layer)
        {
            if (!layers.Has(layer))
                return false;
            LayerTableRecord record = (LayerTableRecord)transaction.GetObject(layers[layer], OpenMode.ForRead);
            return record.IsLocked;
        }

        internal static void EnsureRegApp(Transaction transaction, Database db, string app)
        {
            RegAppTable table = (RegAppTable)transaction.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (table.Has(app))
                return;
            table.UpgradeOpen();
            RegAppTableRecord record = new RegAppTableRecord { Name = app };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }
    }
}
