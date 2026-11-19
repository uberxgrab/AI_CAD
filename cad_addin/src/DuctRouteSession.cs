using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using HvacAiCadAddin.Commands;

namespace HvacAiCadAddin.Ducts
{
    /// <summary>Outlines a pending point would add, for the transient preview.</summary>
    internal sealed class PreviewShapes
    {
        public readonly List<Outline> Ducts = new List<Outline>();
        public readonly List<Outline> Fittings = new List<Outline>();
        /// <summary>Why the point cannot be used; null when it can.</summary>
        public string Error;
    }

    // The state machine behind HVACDUCT, without any prompting or graphics: it holds the run
    // drawn so far, solves each new point with RouteSolver and writes the result. One point is one
    // step; a step creates the new duct block and its fittings and shortens the blocks before it,
    // and Undo reverses exactly that. Everything one step writes happens in one transaction, so a
    // failure leaves the drawing untouched. The caller supplies a document that holds no open
    // transaction.
    internal sealed class DuctRouteSession
    {
        private sealed class ModRecord
        {
            public ObjectId Id;
            public Point3d Position;
            public double Distance3;
            public bool WasErased;
        }

        private sealed class Step
        {
            public readonly List<ObjectId> Created = new List<ObjectId>();
            public readonly List<ModRecord> Modified = new List<ModRecord>();
            public bool[] CornerReducedBefore;
        }

        private sealed class WallTarget
        {
            public DuctInfo Main;
            public DuctHit Hit;
        }

        private readonly Database _db;
        private TemplateSource _template;
        private readonly IList<DuctSize> _templateSizes;
        private readonly FittingSettings _settings;
        private readonly string _layer;
        private readonly List<Vec2> _points = new List<Vec2>();
        private readonly List<DuctSize> _sizes = new List<DuctSize>();
        private readonly List<ObjectId> _segmentBlocks = new List<ObjectId>();
        private readonly Stack<Step> _steps = new Stack<Step>();
        private readonly HashSet<ObjectId> _own = new HashSet<ObjectId>();
        private bool[] _cornerReduced = new bool[1];
        private DuctSize _startSize;
        private double _elevation;

        private DuctInfo _previous;
        private bool _previousAtEnd;
        private Vec2 _previousDirection;
        private double _previousLength;
        private WallTarget _tap;

        public DuctRouteSession(
            Database db,
            TemplateSource template,
            IList<DuctSize> templateSizes,
            FittingSettings settings,
            string layer,
            DuctSize size)
        {
            _db = db;
            _template = template;
            _templateSizes = templateSizes;
            _settings = settings;
            _layer = layer;
            _startSize = size;
            CurrentSize = size;
        }

        /// <summary>
        /// The template, used to import layers and blocks. The command opens it only when something is
        /// missing from the drawing, and sets it here before the point that needs it.
        /// </summary>
        public TemplateSource Template
        {
            get { return _template; }
            set { _template = value; }
        }

        /// <summary>The size the next segment gets; the Size keyword changes it.</summary>
        public DuctSize CurrentSize { get; set; }

        public int SegmentCount { get { return _sizes.Count; } }
        public bool HasStarted { get { return _points.Count > 0; } }
        public bool CanUndo { get { return _steps.Count > 0; } }
        public Vec2 CurrentPoint { get { return _points[_points.Count - 1]; } }
        public double Elevation { get { return _elevation; } }

        /// <summary>References this session created, so the end-of-run wall search can ignore them.</summary>
        public ICollection<ObjectId> OwnIds { get { return _own; } }

        /// <summary>The previous segment's direction, or the direction the run left its start in.</summary>
        public Vec2? LastDirection
        {
            get
            {
                if (_points.Count >= 2)
                    return (_points[_points.Count - 1] - _points[_points.Count - 2]).Normalized();
                if (_previous != null)
                    return _previousDirection;
                if (_tap != null)
                    return _tap.Hit.Outward;
                return null;
            }
        }

        /// <summary>A branch's first segment must leave the main at right angles.</summary>
        public Vec2? ForcedDirection
        {
            get { return _tap != null && _points.Count == 1 ? (Vec2?)_tap.Hit.Outward : null; }
        }

        // ------------------------------------------------------------------ starting

        public void StartFree(Vec2 point, double elevation)
        {
            Reset(point, elevation);
        }

        /// <summary>Continues a run from the face of an existing duct.</summary>
        public bool StartContinue(DuctInfo duct, bool atEnd, out string error)
        {
            error = null;
            Reset(atEnd ? duct.Geometry.End : duct.Geometry.Start, duct.Elevation);
            _previous = duct;
            _previousAtEnd = atEnd;
            _previousDirection = atEnd ? duct.Geometry.Axis : -duct.Geometry.Axis;
            _previousLength = duct.Geometry.Length;
            return true;
        }

        /// <summary>Starts a branch with a tap on the side wall of a main.</summary>
        public bool StartTap(DuctInfo main, DuctHit hit, out string error)
        {
            if (hit.Kind != DuctHitKind.LeftWall && hit.Kind != DuctHitKind.RightWall)
            {
                error = "A branch must start on a side wall.";
                return false;
            }

            if (!CheckTapFits(main, hit, CurrentSize, out error))
                return false;

            Reset(hit.Point, main.Elevation);
            _tap = new WallTarget { Main = main, Hit = hit };
            return true;
        }

        private void Reset(Vec2 point, double elevation)
        {
            _points.Clear();
            _sizes.Clear();
            _segmentBlocks.Clear();
            _steps.Clear();
            _points.Add(point);
            _cornerReduced = new bool[1];
            _elevation = elevation;
            _previous = null;
            _tap = null;
            _startSize = CurrentSize;
        }

        // The tap's opening must lie on the main: the 45 degree lead-in is on the upstream side.
        private bool CheckTapFits(DuctInfo main, DuctHit hit, DuctSize branch, out string error)
        {
            error = null;
            double upstream;
            double downstream;
            if (!DuctFittings.TapFits(
                main.Geometry.Length, hit.Along, branch, _settings, out upstream, out downstream))
            {
                error = "The tap needs " + Num(upstream) + " mm of the main upstream of the point and "
                    + Num(downstream) + " mm downstream; the main is too short there.";
                return false;
            }

            return true;
        }

        // ------------------------------------------------------------------ geometry for the prompt

        /// <summary>Applies the first-segment rule of a branch to a cursor point.</summary>
        public Vec2 Constrain(Vec2 cursor)
        {
            Vec2? forced = ForcedDirection;
            if (!forced.HasValue)
                return cursor;
            double distance = (cursor - _points[0]).Dot(forced.Value);
            return _points[0] + forced.Value * Math.Max(distance, 1.0);
        }

        private RouteInput BuildInput(Vec2 next, DuctSize size, double endLead)
        {
            RouteInput input = new RouteInput { Settings = _settings };
            input.Points.AddRange(_points);
            input.Points.Add(next);
            input.Sizes.AddRange(_sizes);
            input.Sizes.Add(size);
            if (_previous != null)
            {
                input.PreviousSize = _previous.Size;
                input.PreviousDirection = _previousDirection;
                input.PreviousLength = _previousLength;
            }

            input.StartLead = _tap != null ? DuctFittings.TapLead(input.Sizes[0], _settings) : 0;
            input.EndLead = endLead;
            input.CornerReduced = _cornerReduced;
            input.FrozenCorners = Math.Max(0, _points.Count - 1);
            return input;
        }

        /// <summary>The outlines a point would add: the pending duct and the fittings at the previous vertex.</summary>
        public PreviewShapes Preview(Vec2 next)
        {
            PreviewShapes shapes = new PreviewShapes();
            RouteResult result = RouteSolver.Solve(BuildInput(next, CurrentSize, 0));
            if (!result.Ok)
            {
                shapes.Error = result.Error;
                return shapes;
            }

            int index = _points.Count - 1;
            RouteStraight straight = result.Straights[index];
            if (straight.Length > 1e-6)
            {
                shapes.Ducts.Add(FittingShapes.Straight(
                    straight.Start, straight.Direction, straight.Length, straight.Size.Width));
            }

            foreach (RouteFitting fitting in result.Fittings)
            {
                if (fitting.Vertex != index)
                    continue;
                shapes.Fittings.Add(fitting.Shape);
                if (fitting.Centreline != null)
                    shapes.Fittings.Add(fitting.Centreline);
            }

            if (index == 0 && _tap != null)
                shapes.Fittings.Add(TapOutline(_tap, CurrentSize));
            return shapes;
        }

        private Outline TapOutline(WallTarget target, DuctSize branch)
        {
            return FittingShapes.Tap(
                target.Hit.Point, target.Main.Geometry.Axis, target.Hit.Outward, branch, _settings);
        }

        // ------------------------------------------------------------------ committing

        /// <summary>Adds the next vertex: writes the new duct and fittings and shortens what came before.</summary>
        public bool TryAddPoint(Vec2 next, out string error, out List<string> notes)
        {
            return Commit(next, 0, null, out error, out notes);
        }

        /// <summary>
        /// Ends the run on the side wall of another duct with a tap. The last segment must meet the
        /// wall at right angles (within 5 degrees).
        /// </summary>
        public bool TryFinishOnWall(DuctInfo main, DuctHit hit, out string error, out List<string> notes)
        {
            notes = new List<string>();
            if (hit.Kind != DuctHitKind.LeftWall && hit.Kind != DuctHitKind.RightWall)
            {
                error = "The run must end on a side wall.";
                return false;
            }

            Vec2 direction = (hit.Point - CurrentPoint).Normalized();
            if (direction.Dot(-hit.Outward) < Math.Cos(5.0 * Math.PI / 180.0))
            {
                error = "The run does not meet the wall at right angles, so no tap is drawn.";
                return false;
            }

            if (!CheckTapFits(main, hit, CurrentSize, out error))
                return false;

            WallTarget target = new WallTarget { Main = main, Hit = hit };
            return Commit(
                hit.Point,
                DuctFittings.TapLead(CurrentSize, _settings),
                target,
                out error,
                out notes);
        }

        private bool Commit(
            Vec2 next,
            double endLead,
            WallTarget endTap,
            out string error,
            out List<string> notes)
        {
            error = null;
            notes = new List<string>();
            DuctSize size = CurrentSize;

            if (_tap != null && _points.Count == 1)
            {
                if (!CheckTapFits(_tap.Main, _tap.Hit, size, out error))
                    return false;
            }

            RouteResult result = RouteSolver.Solve(BuildInput(next, size, endLead));
            if (!result.Ok)
            {
                error = result.Error;
                return false;
            }

            string layerError;
            if (!DuctBlockLibrary.EnsureLayer(_db, _layer, _template, out layerError))
            {
                error = layerError;
                return false;
            }

            string note;
            string blockError;
            ObjectId definition = DuctBlockLibrary.EnsureBlock(
                _db, size, _template, _templateSizes, out note, out blockError);
            if (definition.IsNull)
            {
                error = blockError;
                return false;
            }

            if (note != null)
                notes.Add(note);
            notes.AddRange(result.Notes);

            int index = _points.Count - 1;
            Step step = new Step { CornerReducedBefore = _cornerReduced };
            ObjectId segmentBlock = ObjectId.Null;

            try
            {
                using (Transaction transaction = _db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = (BlockTableRecord)transaction.GetObject(
                        _db.CurrentSpaceId, OpenMode.ForWrite);

                    if (index == 0 && _tap != null)
                    {
                        step.Created.Add(DuctBlockLibrary.AddFitting(
                            _db, transaction, space, TapOutline(_tap, size), _elevation, _layer,
                            RouteFitting.TapKind, size.Label));
                    }

                    if (index == 0 && _previous != null && result.PreviousShorten > 1e-9)
                    {
                        Shrink(transaction, step, _previous.Id, !_previousAtEnd, result.PreviousShorten);
                    }

                    if (index >= 1 && !_segmentBlocks[index - 1].IsNull)
                    {
                        ShrinkTo(transaction, step, _segmentBlocks[index - 1],
                            result.Straights[index - 1].Length);
                    }

                    foreach (RouteFitting fitting in result.Fittings)
                    {
                        if (fitting.Vertex != index)
                            continue;
                        step.Created.Add(DuctBlockLibrary.AddFitting(
                            _db, transaction, space, fitting.Shape, _elevation, _layer,
                            fitting.Kind, fitting.SizeText));
                        if (fitting.Centreline != null)
                        {
                            step.Created.Add(DuctBlockLibrary.AddFitting(
                                _db, transaction, space, fitting.Centreline, _elevation, _layer,
                                fitting.Kind, fitting.SizeText));
                        }
                    }

                    RouteStraight straight = result.Straights[index];
                    if (straight.Length > 1e-6)
                    {
                        segmentBlock = DuctBlockLibrary.InsertDuct(
                            transaction, space, definition, straight.Start, _elevation,
                            straight.Direction, _layer, straight.Length);
                        step.Created.Add(segmentBlock);
                    }

                    if (endTap != null)
                    {
                        step.Created.Add(DuctBlockLibrary.AddFitting(
                            _db, transaction, space, TapOutline(endTap, size), _elevation, _layer,
                            RouteFitting.TapKind, size.Label));
                    }

                    transaction.Commit();
                }
            }
            catch (System.Exception ex)
            {
                error = "Could not draw the duct: " + ex.Message;
                return false;
            }

            _points.Add(next);
            _sizes.Add(size);
            _segmentBlocks.Add(segmentBlock);
            _cornerReduced = result.CornerReduced;
            _steps.Push(step);
            foreach (ObjectId id in step.Created)
                _own.Add(id);
            return true;
        }

        private static void Shrink(
            Transaction transaction, Step step, ObjectId id, bool atStart, double amount)
        {
            BlockReference block = (BlockReference)transaction.GetObject(id, OpenMode.ForWrite, true);
            ModRecord record = new ModRecord { Id = id, Position = block.Position };
            double distance;
            DynamicBlockHelpers.TryGetDistancePropertyValue(
                block, DuctBlockLibrary.DistanceProperty, out distance);
            record.Distance3 = distance;
            step.Modified.Add(record);

            if (distance - amount <= 1e-6)
            {
                block.Erase();
                record.WasErased = true;
                return;
            }

            string error;
            if (!DuctBlockLibrary.AdjustDuctEnd(transaction, id, atStart, amount, out error))
                throw new InvalidOperationException(error);
        }

        private static void ShrinkTo(Transaction transaction, Step step, ObjectId id, double newLength)
        {
            BlockReference block = (BlockReference)transaction.GetObject(id, OpenMode.ForRead, true);
            double distance;
            DynamicBlockHelpers.TryGetDistancePropertyValue(
                block, DuctBlockLibrary.DistanceProperty, out distance);
            double current = distance;
            double amount = current - newLength;
            if (Math.Abs(amount) < 1e-9)
                return;
            Shrink(transaction, step, id, false, amount);
        }

        // ------------------------------------------------------------------ undo

        /// <summary>Removes the last segment and its fittings and restores the blocks it shortened.</summary>
        public bool Undo(out string message)
        {
            message = null;
            if (_steps.Count == 0)
            {
                message = "Nothing to undo.";
                return false;
            }

            Step step = _steps.Peek();
            try
            {
                using (Transaction transaction = _db.TransactionManager.StartTransaction())
                {
                    for (int i = step.Modified.Count - 1; i >= 0; i--)
                    {
                        ModRecord record = step.Modified[i];
                        DBObject target = transaction.GetObject(record.Id, OpenMode.ForWrite, true);
                        if (record.WasErased && target.IsErased)
                            target.Erase(false);
                        DuctBlockLibrary.SetDuctState(
                            transaction, record.Id, record.Position, record.Distance3);
                    }

                    foreach (ObjectId id in step.Created)
                    {
                        DBObject created = transaction.GetObject(id, OpenMode.ForWrite, true);
                        if (!created.IsErased)
                            created.Erase();
                    }

                    transaction.Commit();
                }
            }
            catch (System.Exception ex)
            {
                message = "Could not undo: " + ex.Message;
                return false;
            }

            _steps.Pop();
            _points.RemoveAt(_points.Count - 1);
            _sizes.RemoveAt(_sizes.Count - 1);
            _segmentBlocks.RemoveAt(_segmentBlocks.Count - 1);
            _cornerReduced = step.CornerReducedBefore;
            CurrentSize = _sizes.Count > 0 ? _sizes[_sizes.Count - 1] : _startSize;
            return true;
        }

        private static string Num(double value)
        {
            return value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
