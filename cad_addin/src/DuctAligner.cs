using System;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using HvacAiCadAddin.Commands;

namespace HvacAiCadAddin.Ducts
{
    /// <summary>What aligning duct B to a reference edge would do.</summary>
    internal sealed class AlignPlan
    {
        /// <summary>Why the pick was refused; null when the plan is usable.</summary>
        public string Error;
        public DuctInfo A;
        public DuctInfo B;
        /// <summary>World translation that makes B's edge collinear with the reference.</summary>
        public Vec2 Translation;
        /// <summary>The join A-B would get; Kind is None when there is nothing to offer.</summary>
        public JoinPlan Join = new JoinPlan();
        /// <summary>Extra information for the user, such as why no join is offered.</summary>
        public string Note;
    }

    // Non-interactive part of HVACDUCTALIGN: edge extraction from picked entities, the plan, and the
    // edit. The command class only prompts (nested picks, Multiple, Join?).
    internal static class DuctAligner
    {
        /// <summary>
        /// Maps a picked LINE or LWPOLYLINE (possibly inside a block, with the block's transform) to the
        /// world segment nearest the pick. Arcs are refused.
        /// </summary>
        internal static bool TryGetEdge(
            Transaction transaction,
            ObjectId entityId,
            Matrix3d toWorld,
            Point3d pickedWorld,
            out Vec2 from,
            out Vec2 to,
            out string error)
        {
            from = new Vec2();
            to = new Vec2();
            error = null;

            Entity entity = transaction.GetObject(entityId, OpenMode.ForRead) as Entity;
            Line line = entity as Line;
            if (line != null)
            {
                Point3d start = line.StartPoint.TransformBy(toWorld);
                Point3d end = line.EndPoint.TransformBy(toWorld);
                from = new Vec2(start.X, start.Y);
                to = new Vec2(end.X, end.Y);
                return Check(from, to, out error);
            }

            Polyline polyline = entity as Polyline;
            if (polyline == null)
            {
                error = "Select a line or a polyline segment.";
                return false;
            }

            Point3d local = pickedWorld.TransformBy(toWorld.Inverse());
            int segments = polyline.Closed ? polyline.NumberOfVertices : polyline.NumberOfVertices - 1;
            int best = -1;
            double bestDistance = double.MaxValue;
            bool sawArc = false;
            for (int i = 0; i < segments; i++)
            {
                if (polyline.GetSegmentType(i) != SegmentType.Line)
                {
                    sawArc = true;
                    continue;
                }

                LineSegment3d segment = polyline.GetLineSegmentAt(i);
                double distance = segment.GetClosestPointTo(local).Point.DistanceTo(local);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            if (best < 0)
            {
                error = sawArc
                    ? "That polyline has only arc segments; pick a straight edge."
                    : "That polyline has no segments.";
                return false;
            }

            LineSegment3d chosen = polyline.GetLineSegmentAt(best);
            Point3d chosenStart = chosen.StartPoint.TransformBy(toWorld);
            Point3d chosenEnd = chosen.EndPoint.TransformBy(toWorld);
            from = new Vec2(chosenStart.X, chosenStart.Y);
            to = new Vec2(chosenEnd.X, chosenEnd.Y);
            return Check(from, to, out error);
        }

        private static bool Check(Vec2 from, Vec2 to, out string error)
        {
            error = null;
            if (from.DistanceTo(to) < 1e-6)
            {
                error = "That segment has no length.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Plans the alignment. <paramref name="a"/> is the duct the reference edge belongs to (null for a
        /// plain line). B's edge must be parallel to the reference within 1 degree and must run along B.
        /// </summary>
        internal static AlignPlan Plan(
            Vec2 referenceFrom,
            Vec2 referenceTo,
            DuctInfo a,
            DuctInfo b,
            Vec2 edgeFrom,
            Vec2 edgeTo,
            FittingSettings settings)
        {
            AlignPlan plan = new AlignPlan { A = a, B = b };
            Vec2 referenceDirection = referenceTo - referenceFrom;
            Vec2 edgeDirection = edgeTo - edgeFrom;

            if (!DuctAlignMath.IsParallel(
                referenceDirection, edgeDirection, DuctAlignMath.ParallelToleranceDegrees))
            {
                plan.Error = "The edge is "
                    + DuctAlignMath.AngleBetweenLines(referenceDirection, edgeDirection)
                        .ToString("0.0", CultureInfo.InvariantCulture)
                    + " degrees off the reference; it must be parallel within "
                    + DuctAlignMath.ParallelToleranceDegrees.ToString("0", CultureInfo.InvariantCulture)
                    + " degree.";
                return plan;
            }

            if (!DuctAlignMath.IsParallel(edgeDirection, b.Geometry.Axis, DuctAlignMath.ParallelToleranceDegrees))
            {
                // An end face of B against a side wall of A: B is a branch, joined with a tap.
                if (a != null && a.Id != b.Id)
                {
                    plan.Join = DuctAlignMath.PlanTap(a.Geometry, b.Geometry, b.Size, referenceFrom, settings);
                    if (plan.Join.Kind == JoinKind.Tap)
                        return plan;
                    plan.Error = "No tap can be added: " + plan.Join.Reason + ".";
                    return plan;
                }

                plan.Error = "That edge is an end face of the duct. To add a branch tap, pick a side wall "
                    + "of the main duct as the reference; otherwise pick a side edge of the duct.";
                return plan;
            }

            plan.Translation = DuctAlignMath.AlignTranslation(referenceFrom, referenceTo, edgeFrom);

            if (a == null)
                return plan;

            if (a.Id == b.Id)
            {
                plan.Note = "Both edges belong to the same duct.";
                return plan;
            }

            DuctGeometry moved = new DuctGeometry(
                b.Geometry.Start + plan.Translation, b.Geometry.Axis, b.Geometry.Length, b.Geometry.Width);

            if (!DuctAlignMath.IsParallel(a.Geometry.Axis, b.Geometry.Axis, DuctAlignMath.ParallelToleranceDegrees))
            {
                plan.Note = "No join is offered: the ducts are not parallel. For a branch tap, pick a side "
                    + "wall of the main duct, then the end face of the branch.";
                return plan;
            }

            plan.Join = DuctAlignMath.PlanJoin(a.Geometry, moved, referenceFrom, settings);
            if (plan.Join.Kind == JoinKind.None)
                plan.Note = "No join is offered: " + plan.Join.Reason + ".";
            return plan;
        }

        /// <summary>
        /// Moves B (one transaction). With <paramref name="join"/> and a join plan, B is also resized to
        /// meet A, and an eccentric transition is drawn when the widths differ.
        /// </summary>
        internal static bool Apply(Database db, AlignPlan plan, bool join, out string message)
        {
            message = null;
            try
            {
                using (Transaction transaction = db.TransactionManager.StartTransaction())
                {
                    BlockReference block = (BlockReference)transaction.GetObject(
                        plan.B.Id, OpenMode.ForWrite);
                    Point3d position = block.Position
                        + new Vector3d(plan.Translation.X, plan.Translation.Y, 0);
                    double distance = plan.B.Distance3;
                    bool tap = plan.Join.Kind == JoinKind.Tap;
                    string text = tap
                        ? "Branch joined to the side wall of the " + plan.A.Size.Label + " duct"
                        : "Aligned: duct moved "
                            + plan.Translation.Length.ToString("0.##", CultureInfo.InvariantCulture) + " mm";

                    // A tap has no alternative: the branch is always joined.
                    bool joined = (join || tap) && plan.Join.Kind != JoinKind.None;
                    if (joined)
                    {
                        if (plan.Join.PositionMoves)
                        {
                            position = new Point3d(plan.Join.NewStart.X, plan.Join.NewStart.Y, position.Z);
                        }

                        distance = plan.Join.NewLength;
                        text += (tap ? "; new length " : "; joined, new length ")
                            + plan.Join.NewLength.ToString("0.##", CultureInfo.InvariantCulture) + " mm";
                    }

                    if (!DuctBlockLibrary.SetDuctState(transaction, plan.B.Id, position, distance))
                    {
                        message = "Could not set Distance3 on the duct.";
                        return false;
                    }

                    if (joined && plan.Join.Kind == JoinKind.Eccentric)
                    {
                        BlockTableRecord space = (BlockTableRecord)transaction.GetObject(
                            block.OwnerId, OpenMode.ForWrite);
                        DuctBlockLibrary.AddFitting(
                            db,
                            transaction,
                            space,
                            plan.Join.Transition,
                            plan.A.Elevation,
                            plan.A.Layer,
                            RouteFitting.TransitionKind,
                            plan.A.Size.Label + ">" + plan.B.Size.Label);
                        text += "; eccentric transition "
                            + plan.Join.TransitionLength.ToString("0.#", CultureInfo.InvariantCulture)
                            + " mm long added";
                    }

                    if (joined && tap)
                    {
                        BlockTableRecord space = (BlockTableRecord)transaction.GetObject(
                            block.OwnerId, OpenMode.ForWrite);
                        DuctBlockLibrary.AddFitting(
                            db,
                            transaction,
                            space,
                            plan.Join.Tap,
                            plan.A.Elevation,
                            plan.A.Layer,
                            RouteFitting.TapKind,
                            plan.B.Size.Label);
                        text += "; tap added ("
                            + plan.Join.TapLead.ToString("0.#", CultureInfo.InvariantCulture) + " mm lead)";
                    }

                    transaction.Commit();
                    message = text + ".";
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                message = "Could not align: " + ex.Message;
                return false;
            }
        }
    }
}
