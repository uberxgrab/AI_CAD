using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace HvacAiCadAddin.Commands
{
    public class HvacStretchCommand
    {
        private const double PointTolerance = 1e-8;
        private static readonly Dictionary<Document, NativeStretchSession>
            ActiveStretchSessions = new Dictionary<Document, NativeStretchSession>();

        [CommandMethod("HVACSTRETCH")]
        public void HvacStretch()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            if (ActiveStretchSessions.ContainsKey(document))
            {
                document.Editor.WriteMessage(
                    "\nHVACSTRETCH is already waiting for STRETCH to finish.");
                return;
            }

            Database database = document.Database;
            Editor editor = document.Editor;

            // Capture the Crossing rectangle once. The same two points are
            // subsequently passed to AutoCAD's native STRETCH command.
            PromptPointResult firstCornerResult = editor.GetPoint(
                "\nSpecify first corner of Crossing selection: ");
            if (firstCornerResult.Status != PromptStatus.OK)
                return;

            PromptCornerOptions oppositeCornerOptions = new PromptCornerOptions(
                "\nSpecify opposite corner: ",
                firstCornerResult.Value);
            PromptPointResult oppositeCornerResult = editor.GetCorner(
                oppositeCornerOptions);
            if (oppositeCornerResult.Status != PromptStatus.OK)
                return;

            Point3d firstCorner = firstCornerResult.Value;
            Point3d oppositeCorner = oppositeCornerResult.Value;

            PromptSelectionResult selectionResult = editor.SelectCrossingWindow(
                firstCorner,
                oppositeCorner);
            if (selectionResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\nNo objects were found in the Crossing selection.");
                return;
            }

            List<DynamicBlockCandidate> dynamicBlocks =
                CaptureDynamicBlockCandidates(database, selectionResult.Value);

            PromptPointResult basePointResult = editor.GetPoint(
                "\nSpecify base point: ");
            if (basePointResult.Status != PromptStatus.OK)
                return;

            NativeStretchSession session = new NativeStretchSession(
                document,
                database,
                editor,
                dynamicBlocks,
                firstCorner,
                oppositeCorner,
                basePointResult.Value);

            ActiveStretchSessions.Add(document, session);
            try
            {
                session.Start();
            }
            catch (System.Exception ex)
            {
                session.Dispose();
                ActiveStretchSessions.Remove(document);
                editor.WriteMessage("\nCould not start native STRETCH: {0}", ex.Message);
            }
        }

        private static List<DynamicBlockCandidate> CaptureDynamicBlockCandidates(
            Database database,
            SelectionSet selectionSet)
        {
            List<DynamicBlockCandidate> candidates =
                new List<DynamicBlockCandidate>();

            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId objectId in selectionSet.GetObjectIds())
                {
                    BlockReference block = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead) as BlockReference;

                    DynamicBlockCandidate candidate;
                    if (block != null &&
                        block.IsDynamicBlock &&
                        TryCreateCandidate(block, out candidate))
                    {
                        candidates.Add(candidate);
                    }
                }
            }

            return candidates;
        }

        // Creates world-space end points for both local block axes. The axis
        // is selected later from the user's drag direction, which supports
        // X/Y-oriented block definitions and rotated block references.
        private static bool TryCreateCandidate(
            BlockReference block,
            out DynamicBlockCandidate candidate)
        {
            candidate = null;

            Extents3d extents;
            try
            {
                // Use the reference's actual world-space extents. Dynamic
                // block definition bounds are not reliable after an action
                // has created an anonymous block definition.
                extents = block.GeometricExtents;
            }
            catch
            {
                return false;
            }

            CoordinateSystem3d coordinateSystem =
                block.BlockTransform.CoordinateSystem3d;

            AxisInfo xAxis = CreateAxis(
                extents,
                coordinateSystem.Xaxis);

            AxisInfo yAxis = CreateAxis(
                extents,
                coordinateSystem.Yaxis);

            if (xAxis == null && yAxis == null)
                return false;

            string propertyName;
            double originalPropertyValue;
            if (!TryGetDistanceProperty(
                block,
                out propertyName,
                out originalPropertyValue))
            {
                return false;
            }

            candidate = new DynamicBlockCandidate(
                block.ObjectId,
                propertyName,
                originalPropertyValue,
                block.Position,
                xAxis,
                yAxis);
            return true;
        }

        private static AxisInfo CreateAxis(
            Extents3d extents,
            Vector3d transformedAxis)
        {
            if (transformedAxis.Length <= PointTolerance)
                return null;

            Vector3d direction = transformedAxis.GetNormal();
            double minimumProjection;
            double maximumProjection;
            GetProjectionRange(
                extents,
                direction,
                out minimumProjection,
                out maximumProjection);

            AxisInfo axis = new AxisInfo(
                direction,
                transformedAxis.Length);
            axis.StartProjection = minimumProjection;
            axis.EndProjection = maximumProjection;
            return axis;
        }

        // Queues native STRETCH only through the base-point prompt. AutoCAD
        // owns the second-point prompt and therefore supplies its normal
        // rubber-band preview for all ordinary entities. The preview object
        // below updates dynamic blocks from that same live cursor position.
        private sealed class NativeStretchSession : IDisposable
        {
            private readonly Document _document;
            private readonly Database _database;
            private readonly Editor _editor;
            private readonly IList<DynamicBlockCandidate> _dynamicBlocks;
            private readonly Point3d _firstCorner;
            private readonly Point3d _oppositeCorner;
            private readonly Point3d _basePoint;
            private readonly DynamicBlockPreview _preview;
            private bool _nativeStretchStarted;
            private bool _isDisposed;

            public NativeStretchSession(
                Document document,
                Database database,
                Editor editor,
                IList<DynamicBlockCandidate> dynamicBlocks,
                Point3d firstCorner,
                Point3d oppositeCorner,
                Point3d basePoint)
            {
                _document = document;
                _database = database;
                _editor = editor;
                _dynamicBlocks = dynamicBlocks;
                _firstCorner = firstCorner;
                _oppositeCorner = oppositeCorner;
                _basePoint = basePoint;
                _preview = new DynamicBlockPreview(
                    database,
                    editor,
                    dynamicBlocks,
                    firstCorner,
                    oppositeCorner,
                    basePoint);
            }

            public void Start()
            {
                _document.CommandWillStart += OnCommandWillStart;
                _document.CommandEnded += OnCommandEnded;
                _document.CommandCancelled += OnCommandCancelled;
                _document.CommandFailed += OnCommandFailed;
                _preview.Start();

                _editor.WriteMessage(
                    "\nNative STRETCH is active. Specify the second point.");

                // Do not append a second point. The final newline submits the
                // base point, leaving STRETCH interactively at its second-
                // point prompt, where AutoCAD renders the native preview.
                _document.SendStringToExecute(
                    "_.STRETCH\n_C\n" +
                    ToCommandPoint(_firstCorner) + "\n" +
                    ToCommandPoint(_oppositeCorner) + "\n\n" +
                    ToCommandPoint(_basePoint) + "\n",
                    true,
                    false,
                    false);
            }

            private void OnCommandWillStart(object sender, CommandEventArgs e)
            {
                if (IsNativeStretch(e.GlobalCommandName))
                    _nativeStretchStarted = true;
            }

            private void OnCommandEnded(object sender, CommandEventArgs e)
            {
                if (!_nativeStretchStarted || !IsNativeStretch(e.GlobalCommandName))
                    return;

                try
                {
                    // Undo temporary PointMonitor changes first; this leaves
                    // normal STRETCH results intact and restores dynamic
                    // references to their pre-stretch state.
                    _preview.Restore();

                    Point3d secondPoint;
                    if (_preview.TryGetLastCursorPoint(out secondPoint))
                    {
                        ApplyDynamicBlockStretch(
                            _database,
                            _editor,
                            _dynamicBlocks,
                            _firstCorner,
                            _oppositeCorner,
                            secondPoint - _basePoint);
                    }
                }
                finally
                {
                    Dispose();
                    ActiveStretchSessions.Remove(_document);
                }
            }

            private void OnCommandCancelled(object sender, CommandEventArgs e)
            {
                EndWithoutCommit(e);
            }

            private void OnCommandFailed(object sender, CommandEventArgs e)
            {
                EndWithoutCommit(e);
            }

            private void EndWithoutCommit(CommandEventArgs e)
            {
                if (!_nativeStretchStarted || !IsNativeStretch(e.GlobalCommandName))
                    return;

                Dispose();
                ActiveStretchSessions.Remove(_document);
            }

            public void Dispose()
            {
                if (_isDisposed)
                    return;

                _isDisposed = true;
                _preview.Dispose();
                _document.CommandWillStart -= OnCommandWillStart;
                _document.CommandEnded -= OnCommandEnded;
                _document.CommandCancelled -= OnCommandCancelled;
                _document.CommandFailed -= OnCommandFailed;
            }
        }

        private static bool IsNativeStretch(string globalCommandName)
        {
            return string.Equals(
                globalCommandName,
                "STRETCH",
                StringComparison.OrdinalIgnoreCase);
        }

        private static string ToCommandPoint(Point3d point)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:R},{1:R},{2:R}",
                point.X,
                point.Y,
                point.Z);
        }

        private static void ApplyDynamicBlockStretch(
            Database database,
            Editor editor,
            IEnumerable<DynamicBlockCandidate> dynamicBlocks,
            Point3d firstCorner,
            Point3d oppositeCorner,
            Vector3d requestedDisplacement)
        {
            int changedCount = 0;

            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (DynamicBlockCandidate candidate in dynamicBlocks)
                {
                    BlockReference block = transaction.GetObject(
                        candidate.ObjectId,
                        OpenMode.ForWrite,
                        false) as BlockReference;

                    if (block == null || block.IsErased || !block.IsDynamicBlock)
                        continue;

                    AxisInfo axis = candidate.GetAxisFor(requestedDisplacement);
                    if (axis == null)
                        continue;

                    bool startSelected = axis.IsStartInsideCrossing(
                        firstCorner,
                        oppositeCorner);
                    bool endSelected = axis.IsEndInsideCrossing(
                        firstCorner,
                        oppositeCorner);

                    // No end, or both ends: preserve native STRETCH's result.
                    // A complete block crossing is a normal move operation.
                    if (startSelected == endSelected)
                        continue;

                    double worldAxisDisplacement = requestedDisplacement.DotProduct(
                        axis.Direction);
                    if (Math.Abs(worldAxisDisplacement) <= PointTolerance)
                        continue;

                    double propertyDisplacement =
                        worldAxisDisplacement / axis.Scale;
                    double desiredPropertyChange = endSelected
                        ? propertyDisplacement
                        : -propertyDisplacement;

                    if (!TryChangeDistanceProperty(
                        block,
                        candidate.PropertyName,
                        desiredPropertyChange,
                        editor))
                    {
                        // The property cannot be changed. Keep AutoCAD's
                        // original STRETCH result instead of losing the move.
                        continue;
                    }

                    // Native STRETCH has moved the reference as a whole.
                    // Align the end opposite to the selected end with its
                    // original projection, so that the selected end is the
                    // only end which moves.
                    AlignOppositeEnd(block, axis, startSelected);
                    changedCount++;
                }

                transaction.Commit();
            }

            if (changedCount > 0)
            {
                editor.WriteMessage(
                    "\nHVACSTRETCH: Updated {0} Dynamic Block(s).",
                    changedCount);
            }
        }

        private static bool TryChangeDistanceProperty(
            BlockReference block,
            string propertyName,
            double propertyDisplacement,
            Editor editor)
        {
            double currentValue;
            if (!TryGetDistancePropertyValue(
                block,
                propertyName,
                out currentValue))
            {
                return false;
            }

            return TrySetDistancePropertyValue(
                block,
                propertyName,
                currentValue + propertyDisplacement,
                editor,
                true);
        }

        private static bool TryGetDistanceProperty(
            BlockReference block,
            out string propertyName,
            out double propertyValue)
        {
            propertyName = null;
            propertyValue = 0.0;

            foreach (DynamicBlockReferenceProperty property
                in block.DynamicBlockReferencePropertyCollection)
            {
                if (property.ReadOnly ||
                    property.Value == null ||
                    !IsNumeric(property.Value) ||
                    !IsDistanceProperty(property.PropertyName))
                {
                    continue;
                }

                try
                {
                    propertyValue = Convert.ToDouble(property.Value);
                }
                catch
                {
                    continue;
                }

                propertyName = property.PropertyName;
                return true;
            }

            return false;
        }

        private static bool TryGetDistancePropertyValue(
            BlockReference block,
            string propertyName,
            out double propertyValue)
        {
            propertyValue = 0.0;

            foreach (DynamicBlockReferenceProperty property
                in block.DynamicBlockReferencePropertyCollection)
            {
                if (!property.PropertyName.Equals(
                        propertyName,
                        StringComparison.OrdinalIgnoreCase) ||
                    property.Value == null ||
                    !IsNumeric(property.Value))
                {
                    continue;
                }

                try
                {
                    propertyValue = Convert.ToDouble(property.Value);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static bool TrySetDistancePropertyValue(
            BlockReference block,
            string propertyName,
            double newValue,
            Editor editor,
            bool reportChange)
        {
            foreach (DynamicBlockReferenceProperty property
                in block.DynamicBlockReferencePropertyCollection)
            {
                if (!property.PropertyName.Equals(
                        propertyName,
                        StringComparison.OrdinalIgnoreCase) ||
                    property.ReadOnly)
                {
                    continue;
                }

                try
                {
                    double currentValue = Convert.ToDouble(property.Value);
                    property.Value = newValue;

                    if (reportChange)
                    {
                        editor.WriteMessage(
                            "\nProperty '{0}': {1:F2} -> {2:F2}",
                            property.PropertyName,
                            currentValue,
                            newValue);
                    }

                    return true;
                }
                catch (System.Exception ex)
                {
                    if (reportChange)
                    {
                        editor.WriteMessage(
                            "\nFailed to set property '{0}': {1}",
                            property.PropertyName,
                            ex.Message);
                    }

                    return false;
                }
            }

            return false;
        }

        private static void AlignOppositeEnd(
            BlockReference block,
            AxisInfo axis,
            bool startSelected)
        {
            Extents3d currentExtents = block.GeometricExtents;
            double currentStart;
            double currentEnd;
            GetProjectionRange(
                currentExtents,
                axis.Direction,
                out currentStart,
                out currentEnd);

            double requiredTranslation = startSelected
                ? axis.EndProjection - currentEnd
                : axis.StartProjection - currentStart;

            if (Math.Abs(requiredTranslation) > PointTolerance)
            {
                block.TransformBy(Matrix3d.Displacement(
                    axis.Direction * requiredTranslation));
            }
        }

        private static void GetProjectionRange(
            Extents3d extents,
            Vector3d direction,
            out double minimumProjection,
            out double maximumProjection)
        {
            Point3d min = extents.MinPoint;
            Point3d max = extents.MaxPoint;
            minimumProjection = double.MaxValue;
            maximumProjection = double.MinValue;

            for (int x = 0; x <= 1; x++)
            {
                for (int y = 0; y <= 1; y++)
                {
                    for (int z = 0; z <= 1; z++)
                    {
                        Point3d corner = new Point3d(
                            x == 0 ? min.X : max.X,
                            y == 0 ? min.Y : max.Y,
                            z == 0 ? min.Z : max.Z);
                        double projection = corner.GetAsVector().DotProduct(direction);
                        minimumProjection = Math.Min(minimumProjection, projection);
                        maximumProjection = Math.Max(maximumProjection, projection);
                    }
                }
            }
        }

        private static bool IsDistanceProperty(string propertyName)
        {
            return propertyName.Equals(
                       "Distance3",
                       StringComparison.OrdinalIgnoreCase)
                || propertyName.Equals(
                    "Distance",
                    StringComparison.OrdinalIgnoreCase)
                || propertyName.Equals(
                    "Length",
                    StringComparison.OrdinalIgnoreCase)
                || propertyName.Equals(
                    "Width",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNumeric(object value)
        {
            return value is byte ||
                   value is short ||
                   value is int ||
                   value is long ||
                   value is float ||
                   value is double ||
                   value is decimal;
        }

        // Provides a temporary dynamic-block preview while AutoCAD is asking
        // for the second point. The original values are restored before the
        // native STRETCH command receives its final points.
        private sealed class DynamicBlockPreview : IDisposable
        {
            private readonly Database _database;
            private readonly Editor _editor;
            private readonly IList<DynamicBlockCandidate> _dynamicBlocks;
            private readonly Point3d _firstCorner;
            private readonly Point3d _oppositeCorner;
            private readonly Point3d _basePoint;
            private Point3d _lastCursorPoint;
            private bool _hasCursorPoint;
            private bool _isRestored;
            private bool _isUpdating;
            private bool _isDisposed;

            public DynamicBlockPreview(
                Database database,
                Editor editor,
                IList<DynamicBlockCandidate> dynamicBlocks,
                Point3d firstCorner,
                Point3d oppositeCorner,
                Point3d basePoint)
            {
                _database = database;
                _editor = editor;
                _dynamicBlocks = dynamicBlocks;
                _firstCorner = firstCorner;
                _oppositeCorner = oppositeCorner;
                _basePoint = basePoint;
            }

            public void Start()
            {
                if (_dynamicBlocks.Count > 0)
                    _editor.PointMonitor += OnPointMonitor;
            }

            private void OnPointMonitor(object sender, PointMonitorEventArgs e)
            {
                if (_isDisposed || _isUpdating || !e.Context.PointComputed)
                    return;

                _isUpdating = true;
                try
                {
                    UpdatePreview(e.Context.ComputedPoint);
                }
                catch
                {
                    // Preview failure must not prevent the user from choosing
                    // a second point. The final stretch still uses its normal
                    // validation and error reporting.
                }
                finally
                {
                    _isUpdating = false;
                }
            }

            private void UpdatePreview(Point3d cursorPoint)
            {
                Vector3d displacement = cursorPoint - _basePoint;

                _lastCursorPoint = cursorPoint;
                _hasCursorPoint = true;
                _isRestored = false;

                using (Transaction transaction =
                    _database.TransactionManager.StartTransaction())
                {
                    foreach (DynamicBlockCandidate candidate in _dynamicBlocks)
                    {
                        BlockReference block = transaction.GetObject(
                            candidate.ObjectId,
                            OpenMode.ForWrite,
                            false) as BlockReference;

                        if (block == null || block.IsErased || !block.IsDynamicBlock)
                            continue;

                        RestoreBlockState(block, candidate);

                        AxisInfo axis = candidate.GetAxisFor(displacement);
                        if (axis == null)
                            continue;

                        bool startSelected = axis.IsStartInsideCrossing(
                            _firstCorner,
                            _oppositeCorner);
                        bool endSelected = axis.IsEndInsideCrossing(
                            _firstCorner,
                            _oppositeCorner);
                        if (startSelected == endSelected)
                            continue;

                        double worldAxisDisplacement = displacement.DotProduct(
                            axis.Direction);
                        double propertyDisplacement =
                            worldAxisDisplacement / axis.Scale;
                        double desiredPropertyChange = endSelected
                            ? propertyDisplacement
                            : -propertyDisplacement;

                        if (TrySetDistancePropertyValue(
                            block,
                            candidate.PropertyName,
                            candidate.OriginalPropertyValue +
                                desiredPropertyChange,
                            _editor,
                            false))
                        {
                            AlignOppositeEnd(block, axis, startSelected);
                        }
                    }

                    transaction.Commit();
                }

                _editor.UpdateScreen();
            }

            public bool TryGetLastCursorPoint(out Point3d cursorPoint)
            {
                cursorPoint = _lastCursorPoint;
                return _hasCursorPoint;
            }

            public void Restore()
            {
                if (_isDisposed || _isRestored || _dynamicBlocks.Count == 0)
                    return;

                using (Transaction transaction =
                    _database.TransactionManager.StartTransaction())
                {
                    foreach (DynamicBlockCandidate candidate in _dynamicBlocks)
                    {
                        BlockReference block = transaction.GetObject(
                            candidate.ObjectId,
                            OpenMode.ForWrite,
                            false) as BlockReference;

                        if (block == null || block.IsErased || !block.IsDynamicBlock)
                            continue;

                        RestoreBlockState(block, candidate);
                    }

                    transaction.Commit();
                }

                _editor.UpdateScreen();
                _isRestored = true;
            }

            public void Dispose()
            {
                if (_isDisposed)
                    return;

                Restore();
                _isDisposed = true;
                _editor.PointMonitor -= OnPointMonitor;
            }

            private void RestoreBlockState(
                BlockReference block,
                DynamicBlockCandidate candidate)
            {
                TrySetDistancePropertyValue(
                    block,
                    candidate.PropertyName,
                    candidate.OriginalPropertyValue,
                    _editor,
                    false);
                block.Position = candidate.OriginalPosition;
            }
        }

        private sealed class DynamicBlockCandidate
        {
            public DynamicBlockCandidate(
                ObjectId objectId,
                string propertyName,
                double originalPropertyValue,
                Point3d originalPosition,
                AxisInfo xAxis,
                AxisInfo yAxis)
            {
                ObjectId = objectId;
                PropertyName = propertyName;
                OriginalPropertyValue = originalPropertyValue;
                OriginalPosition = originalPosition;
                XAxis = xAxis;
                YAxis = yAxis;
            }

            public ObjectId ObjectId { get; private set; }
            public string PropertyName { get; private set; }
            public double OriginalPropertyValue { get; private set; }
            public Point3d OriginalPosition { get; private set; }
            public AxisInfo XAxis { get; private set; }
            public AxisInfo YAxis { get; private set; }

            public AxisInfo GetAxisFor(Vector3d displacement)
            {
                if (XAxis == null)
                    return YAxis;
                if (YAxis == null)
                    return XAxis;

                double xProjection = Math.Abs(
                    displacement.DotProduct(XAxis.Direction));
                double yProjection = Math.Abs(
                    displacement.DotProduct(YAxis.Direction));

                return xProjection >= yProjection ? XAxis : YAxis;
            }
        }

        private sealed class AxisInfo
        {
            public AxisInfo(
                Vector3d direction,
                double scale)
            {
                Direction = direction;
                Scale = scale;
            }

            public Vector3d Direction { get; private set; }
            public double Scale { get; private set; }

            public double StartProjection { get; set; }
            public double EndProjection { get; set; }

            public bool IsStartInsideCrossing(
                Point3d firstCorner,
                Point3d oppositeCorner)
            {
                double minimumProjection;
                double maximumProjection;
                GetCrossingProjectionRange(
                    firstCorner,
                    oppositeCorner,
                    Direction,
                    out minimumProjection,
                    out maximumProjection);
                return StartProjection >= minimumProjection - PointTolerance &&
                       StartProjection <= maximumProjection + PointTolerance;
            }

            public bool IsEndInsideCrossing(
                Point3d firstCorner,
                Point3d oppositeCorner)
            {
                double minimumProjection;
                double maximumProjection;
                GetCrossingProjectionRange(
                    firstCorner,
                    oppositeCorner,
                    Direction,
                    out minimumProjection,
                    out maximumProjection);
                return EndProjection >= minimumProjection - PointTolerance &&
                       EndProjection <= maximumProjection + PointTolerance;
            }

            private static void GetCrossingProjectionRange(
                Point3d firstCorner,
                Point3d oppositeCorner,
                Vector3d direction,
                out double minimumProjection,
                out double maximumProjection)
            {
                minimumProjection = double.MaxValue;
                maximumProjection = double.MinValue;

                for (int x = 0; x <= 1; x++)
                {
                    for (int y = 0; y <= 1; y++)
                    {
                        Point3d corner = new Point3d(
                            x == 0 ? firstCorner.X : oppositeCorner.X,
                            y == 0 ? firstCorner.Y : oppositeCorner.Y,
                            firstCorner.Z);
                        double projection = corner.GetAsVector().DotProduct(direction);
                        minimumProjection = Math.Min(minimumProjection, projection);
                        maximumProjection = Math.Max(maximumProjection, projection);
                    }
                }
            }
        }
    }
}
