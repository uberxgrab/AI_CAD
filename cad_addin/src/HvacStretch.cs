using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace HvacAiCadAddin.Commands
{
    // HVACSTRETCH: native STRETCH for ordinary entities plus a dynamic-block
    // distance change when exactly one end of a block is crossed.
    //
    // Objects are selected with AutoCAD's own selection prompt (GetSelection),
    // so crossing/window by dragging, polygons, several selections, single
    // picks and Shift-removal behave as in STRETCH. The areas the user made,
    // the objects they removed and the objects they picked are read back and
    // replayed into native STRETCH, which then owns the second-point prompt.
    // Each dynamic block is judged against the area that selected it; a picked
    // block counts as fully crossed (moved whole). The behaviour before this
    // selection change (one Crossing rectangle) is tagged
    // hvacstretch-before-selection.
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

            // Select once, as native STRETCH does. The selection areas, removals
            // and picks are replayed into native STRETCH afterwards.
            PromptSelectionOptions selectionOptions = new PromptSelectionOptions();
            selectionOptions.MessageForAdding =
                "\nSelect objects to stretch by crossing-window or crossing-polygon...";
            PromptSelectionResult selectionResult = editor.GetSelection(
                selectionOptions);
            if (selectionResult.Status == PromptStatus.Cancel)
                return;
            if (selectionResult.Status != PromptStatus.OK ||
                selectionResult.Value.Count == 0)
            {
                editor.WriteMessage("\nNo objects were selected.");
                return;
            }

            StretchSelection selection = StretchSelection.Read(
                editor,
                selectionResult.Value);

            List<DynamicBlockCandidate> dynamicBlocks =
                CaptureDynamicBlockCandidates(database, selection);

            PromptPointResult basePointResult = editor.GetPoint(
                "\nSpecify base point: ");
            if (basePointResult.Status != PromptStatus.OK)
                return;

            NativeStretchSession session = new NativeStretchSession(
                document,
                database,
                editor,
                dynamicBlocks,
                selection,
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
            StretchSelection selection)
        {
            List<DynamicBlockCandidate> candidates =
                new List<DynamicBlockCandidate>();

            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId objectId in selection.ObjectIds)
                {
                    BlockReference block = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead) as BlockReference;

                    DynamicBlockCandidate candidate;
                    if (block != null &&
                        block.IsDynamicBlock &&
                        TryCreateCandidate(
                            block,
                            selection.GetArea(objectId),
                            out candidate))
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
            SelectionArea area,
            out DynamicBlockCandidate candidate)
        {
            candidate = null;

            // Bounds come from the reference's displayed geometry (its current
            // anonymous representation), not the dynamic definition, and not
            // GeometricExtents, which includes invisible entities.
            CoordinateSystem3d coordinateSystem =
                block.BlockTransform.CoordinateSystem3d;

            AxisInfo xAxis = CreateAxis(
                block,
                coordinateSystem.Xaxis);

            AxisInfo yAxis = CreateAxis(
                block,
                coordinateSystem.Yaxis);

            if (xAxis == null && yAxis == null)
                return false;

            string propertyName;
            double originalPropertyValue;
            if (!DynamicBlockHelpers.TryGetDistanceProperty(
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
                yAxis,
                area);
            return true;
        }

        private static AxisInfo CreateAxis(
            BlockReference block,
            Vector3d transformedAxis)
        {
            if (transformedAxis.Length <= PointTolerance)
                return null;

            Vector3d direction = transformedAxis.GetNormal();
            double minimumProjection;
            double maximumProjection;
            if (!DynamicBlockHelpers.TryGetDisplayedProjectionRange(
                block,
                direction,
                out minimumProjection,
                out maximumProjection))
            {
                return null;
            }

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
        // The user's selection (areas, removals, picks) is replayed into
        // STRETCH's own "Select objects:" prompt first.
        private sealed class NativeStretchSession : IDisposable
        {
            private readonly Document _document;
            private readonly Database _database;
            private readonly Editor _editor;
            private readonly IList<DynamicBlockCandidate> _dynamicBlocks;
            private readonly StretchSelection _selection;
            private readonly Point3d _basePoint;
            private readonly DynamicBlockPreview _preview;
            private bool _nativeStretchStarted;
            private bool _isDisposed;

            public NativeStretchSession(
                Document document,
                Database database,
                Editor editor,
                IList<DynamicBlockCandidate> dynamicBlocks,
                StretchSelection selection,
                Point3d basePoint)
            {
                _document = document;
                _database = database;
                _editor = editor;
                _dynamicBlocks = dynamicBlocks;
                _selection = selection;
                _basePoint = basePoint;
                _preview = new DynamicBlockPreview(
                    database,
                    editor,
                    dynamicBlocks,
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
                // The empty line before the base point ends object selection.
                _document.SendStringToExecute(
                    "_.STRETCH\n" +
                    _selection.ToCommandText() + "\n" +
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

                    bool startSelected;
                    bool endSelected;
                    candidate.GetEndsSelected(
                        axis,
                        out startSelected,
                        out endSelected);

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

                    if (!DynamicBlockHelpers.TryChangeDistanceProperty(
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

        private static void AlignOppositeEnd(
            BlockReference block,
            AxisInfo axis,
            bool startSelected)
        {
            double currentStart;
            double currentEnd;
            if (!DynamicBlockHelpers.TryGetDisplayedProjectionRange(
                block,
                axis.Direction,
                out currentStart,
                out currentEnd))
            {
                return;
            }

            double requiredTranslation = startSelected
                ? axis.EndProjection - currentEnd
                : axis.StartProjection - currentStart;

            if (Math.Abs(requiredTranslation) > PointTolerance)
            {
                block.TransformBy(Matrix3d.Displacement(
                    axis.Direction * requiredTranslation));
            }
        }

        // Provides a temporary dynamic-block preview while AutoCAD is asking
        // for the second point. The original values are restored before the
        // native STRETCH command receives its final points.
        private sealed class DynamicBlockPreview : IDisposable
        {
            private readonly Database _database;
            private readonly Editor _editor;
            private readonly IList<DynamicBlockCandidate> _dynamicBlocks;
            private readonly Point3d _basePoint;
            // References the preview has changed and must put back. A fully
            // crossed reference is never changed here, so native STRETCH's
            // move of it survives Restore().
            private readonly HashSet<ObjectId> _previewedBlocks = new HashSet<ObjectId>();
            private Point3d _lastCursorPoint;
            private bool _hasCursorPoint;
            private bool _isRestored;
            private bool _isUpdating;
            private bool _isDisposed;

            public DynamicBlockPreview(
                Database database,
                Editor editor,
                IList<DynamicBlockCandidate> dynamicBlocks,
                Point3d basePoint)
            {
                _database = database;
                _editor = editor;
                _dynamicBlocks = dynamicBlocks;
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

                        if (_previewedBlocks.Remove(candidate.ObjectId))
                            RestoreBlockState(block, candidate);

                        AxisInfo axis = candidate.GetAxisFor(displacement);
                        if (axis == null)
                            continue;

                        bool startSelected;
                        bool endSelected;
                        candidate.GetEndsSelected(
                            axis,
                            out startSelected,
                            out endSelected);
                        if (startSelected == endSelected)
                            continue;

                        double worldAxisDisplacement = displacement.DotProduct(
                            axis.Direction);
                        double propertyDisplacement =
                            worldAxisDisplacement / axis.Scale;
                        double desiredPropertyChange = endSelected
                            ? propertyDisplacement
                            : -propertyDisplacement;

                        _previewedBlocks.Add(candidate.ObjectId);
                        if (DynamicBlockHelpers.TrySetDistancePropertyValue(
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
                        if (!_previewedBlocks.Contains(candidate.ObjectId))
                            continue;

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

                _previewedBlocks.Clear();
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
                DynamicBlockHelpers.TrySetDistancePropertyValue(
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
                AxisInfo yAxis,
                SelectionArea area)
            {
                ObjectId = objectId;
                PropertyName = propertyName;
                OriginalPropertyValue = originalPropertyValue;
                OriginalPosition = originalPosition;
                XAxis = xAxis;
                YAxis = yAxis;
                Area = area;
            }

            public ObjectId ObjectId { get; private set; }
            public string PropertyName { get; private set; }
            public double OriginalPropertyValue { get; private set; }
            public Point3d OriginalPosition { get; private set; }
            public AxisInfo XAxis { get; private set; }
            public AxisInfo YAxis { get; private set; }
            // The area that selected this block; null when it was picked.
            public SelectionArea Area { get; private set; }

            // Which ends of the axis lie inside the area that selected the
            // block. A picked block has no area and counts as fully crossed,
            // so native STRETCH moves it whole and its size is kept.
            public void GetEndsSelected(
                AxisInfo axis,
                out bool startSelected,
                out bool endSelected)
            {
                if (Area == null)
                {
                    startSelected = true;
                    endSelected = true;
                    return;
                }

                startSelected = axis.IsStartInsideCrossing(
                    Area.FirstCorner,
                    Area.OppositeCorner);
                endSelected = axis.IsEndInsideCrossing(
                    Area.FirstCorner,
                    Area.OppositeCorner);
            }

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

        // One crossing/window rectangle or polygon the user selected with, in
        // UCS like the corners GetPoint used to return.
        private sealed class SelectionArea
        {
            public SelectionArea(bool isWindow, List<Point3d> points)
            {
                IsWindow = isWindow;
                Points = points;

                // The rectangle the dynamic-block end test uses: the two
                // corners themselves, or a polygon's bounding rectangle.
                double minimumX = double.MaxValue;
                double minimumY = double.MaxValue;
                double maximumX = double.MinValue;
                double maximumY = double.MinValue;
                foreach (Point3d point in points)
                {
                    minimumX = Math.Min(minimumX, point.X);
                    minimumY = Math.Min(minimumY, point.Y);
                    maximumX = Math.Max(maximumX, point.X);
                    maximumY = Math.Max(maximumY, point.Y);
                }

                FirstCorner = new Point3d(minimumX, minimumY, points[0].Z);
                OppositeCorner = new Point3d(maximumX, maximumY, points[0].Z);
                ReplayIds = new HashSet<ObjectId>();
            }

            public bool IsWindow { get; private set; }
            // Two opposite corners for a rectangle, three or more vertices
            // for a polygon.
            public List<Point3d> Points { get; private set; }
            public Point3d FirstCorner { get; private set; }
            public Point3d OppositeCorner { get; private set; }
            // What replaying this area into native STRETCH will select.
            public HashSet<ObjectId> ReplayIds { get; set; }

            private bool IsPolygon
            {
                get { return Points.Count > 2; }
            }

            // Reads the area from the pick points AutoCAD reports for an
            // object selected by crossing or window. A rectangle comes back as
            // its four corners, a polygon as its vertices (see ssnamex).
            // Returns null when the points cannot describe an area.
            public static SelectionArea TryCreate(
                Editor editor,
                bool isWindow,
                PickPointDescriptor[] pickPoints)
            {
                if (pickPoints == null || pickPoints.Length < 2)
                    return null;

                // The descriptors are lines of sight in WCS. Take each one
                // onto the UCS XY plane, where GetPoint used to return it.
                Matrix3d wcsToUcs = editor.CurrentUserCoordinateSystem.Inverse();
                List<Point3d> vertices = new List<Point3d>();
                foreach (PickPointDescriptor pickPoint in pickPoints)
                    vertices.Add(ToUcsPlanePoint(pickPoint, wcsToUcs));

                // Replay a plain crossing/window as "_C p1 p2" / "_W p1 p2".
                if (vertices.Count == 4 && IsAxisAlignedRectangle(vertices))
                    vertices = new List<Point3d> { vertices[0], vertices[2] };

                return new SelectionArea(isWindow, vertices);
            }

            private static Point3d ToUcsPlanePoint(
                PickPointDescriptor pickPoint,
                Matrix3d wcsToUcs)
            {
                Point3d point = pickPoint.PointOnLine.TransformBy(wcsToUcs);
                Vector3d direction = pickPoint.Direction.TransformBy(wcsToUcs);
                if (Math.Abs(direction.Z) <= PointTolerance)
                    return point;

                double distance = -point.Z / direction.Z;
                return new Point3d(
                    point.X + direction.X * distance,
                    point.Y + direction.Y * distance,
                    0.0);
            }

            private static bool IsAxisAlignedRectangle(List<Point3d> corners)
            {
                bool horizontalFirst =
                    Near(corners[0].Y, corners[1].Y) &&
                    Near(corners[1].X, corners[2].X) &&
                    Near(corners[2].Y, corners[3].Y) &&
                    Near(corners[3].X, corners[0].X);
                bool verticalFirst =
                    Near(corners[0].X, corners[1].X) &&
                    Near(corners[1].Y, corners[2].Y) &&
                    Near(corners[2].X, corners[3].X) &&
                    Near(corners[3].Y, corners[0].Y);
                return horizontalFirst || verticalFirst;
            }

            private static bool Near(double first, double second)
            {
                return Math.Abs(first - second) <= 1e-6;
            }

            public bool IsSameAs(SelectionArea other)
            {
                if (IsWindow != other.IsWindow ||
                    Points.Count != other.Points.Count)
                {
                    return false;
                }

                for (int i = 0; i < Points.Count; i++)
                {
                    if (!Near(Points[i].X, other.Points[i].X) ||
                        !Near(Points[i].Y, other.Points[i].Y))
                    {
                        return false;
                    }
                }

                return true;
            }

            // The objects AutoCAD selects from this area right now. The view
            // is unchanged since the user selected, so native STRETCH selects
            // the same objects when the area is replayed.
            public HashSet<ObjectId> Select(Editor editor)
            {
                HashSet<ObjectId> objectIds = new HashSet<ObjectId>();
                try
                {
                    PromptSelectionResult result;
                    if (IsPolygon)
                    {
                        Point3dCollection polygon =
                            new Point3dCollection(Points.ToArray());
                        result = IsWindow
                            ? editor.SelectWindowPolygon(polygon)
                            : editor.SelectCrossingPolygon(polygon);
                    }
                    else
                    {
                        result = IsWindow
                            ? editor.SelectWindow(Points[0], Points[1])
                            : editor.SelectCrossingWindow(Points[0], Points[1]);
                    }

                    if (result.Status == PromptStatus.OK)
                    {
                        foreach (ObjectId objectId in result.Value.GetObjectIds())
                            objectIds.Add(objectId);
                    }
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    // An area AutoCAD rejects selects nothing; its objects
                    // are then replayed as picks.
                }

                return objectIds;
            }

            // STRETCH "Select objects:" input that re-makes this selection.
            public string ToCommandText()
            {
                string method = IsPolygon
                    ? (IsWindow ? "_WP" : "_CP")
                    : (IsWindow ? "_W" : "_C");

                StringBuilder text = new StringBuilder();
                text.Append(method).Append('\n');
                foreach (Point3d point in Points)
                    text.Append(ToCommandPoint(point)).Append('\n');
                if (IsPolygon)
                    text.Append('\n'); // an empty line closes the polygon
                return text.ToString();
            }
        }

        // What the user selected, in the terms native STRETCH can replay: the
        // areas they made, objects those areas would select but the user
        // removed, and objects they picked.
        private sealed class StretchSelection
        {
            private readonly List<SelectionArea> _areas = new List<SelectionArea>();
            // The final selection, in selection order.
            private readonly List<ObjectId> _objectIds = new List<ObjectId>();
            // The area that selected each object; null when it was picked.
            private readonly Dictionary<ObjectId, SelectionArea> _areaByObject =
                new Dictionary<ObjectId, SelectionArea>();
            private readonly List<ObjectId> _removed = new List<ObjectId>();
            private readonly List<ObjectId> _picked = new List<ObjectId>();

            public IList<ObjectId> ObjectIds
            {
                get { return _objectIds; }
            }

            public SelectionArea GetArea(ObjectId objectId)
            {
                return _areaByObject[objectId];
            }

            public static StretchSelection Read(
                Editor editor,
                SelectionSet selectionSet)
            {
                StretchSelection selection = new StretchSelection();
                foreach (SelectedObject selected in selectionSet)
                {
                    ObjectId objectId = selected.ObjectId;
                    if (selection._areaByObject.ContainsKey(objectId))
                        continue;

                    selection._objectIds.Add(objectId);
                    selection._areaByObject.Add(
                        objectId,
                        selection.FindArea(editor, selected));
                }

                selection.PlanReplay(editor);
                return selection;
            }

            // The area that selected the object, shared with every other
            // object it selected, or null for a pick, fence or other method.
            private SelectionArea FindArea(Editor editor, SelectedObject selected)
            {
                CrossingOrWindowSelectedObject byArea =
                    selected as CrossingOrWindowSelectedObject;
                if (byArea == null ||
                    (selected.SelectionMethod != SelectionMethod.Crossing &&
                     selected.SelectionMethod != SelectionMethod.Window))
                {
                    return null;
                }

                SelectionArea area = SelectionArea.TryCreate(
                    editor,
                    selected.SelectionMethod == SelectionMethod.Window,
                    byArea.GetPickPoints());
                if (area == null)
                    return null;

                foreach (SelectionArea known in _areas)
                {
                    if (known.IsSameAs(area))
                        return known;
                }

                _areas.Add(area);
                return area;
            }

            // Works out what replaying the areas selects, so the replay can
            // remove what the user removed and add what they picked.
            private void PlanReplay(Editor editor)
            {
                HashSet<ObjectId> final = new HashSet<ObjectId>(_objectIds);
                HashSet<ObjectId> replayed = new HashSet<ObjectId>();
                foreach (SelectionArea area in _areas)
                {
                    area.ReplayIds = area.Select(editor);
                    foreach (ObjectId objectId in area.ReplayIds)
                    {
                        if (replayed.Add(objectId) && !final.Contains(objectId))
                            _removed.Add(objectId);
                    }
                }

                foreach (ObjectId objectId in _objectIds)
                {
                    SelectionArea area = _areaByObject[objectId];
                    if (area != null && area.ReplayIds.Contains(objectId))
                        continue;

                    // Picked, or its area would not select it again: it is
                    // picked in the replay and counts as fully crossed. An
                    // area replayed first would select it as a crossing, so
                    // remove it there and add it back as a pick.
                    _areaByObject[objectId] = null;
                    _picked.Add(objectId);
                    if (replayed.Contains(objectId))
                        _removed.Add(objectId);
                }
            }

            // STRETCH "Select objects:" input: each area, then the removals,
            // then the picks. The caller ends the selection.
            public string ToCommandText()
            {
                StringBuilder text = new StringBuilder();
                foreach (SelectionArea area in _areas)
                    text.Append(area.ToCommandText());

                if (_removed.Count > 0)
                {
                    text.Append("_R\n");
                    foreach (ObjectId objectId in _removed)
                        text.Append(ToHandleText(objectId));
                    if (_picked.Count > 0)
                        text.Append("_A\n");
                }

                foreach (ObjectId objectId in _picked)
                    text.Append(ToHandleText(objectId));
                return text.ToString();
            }

            // An AutoLISP expression that returns the entity, which a
            // selection prompt accepts like a click on it.
            private static string ToHandleText(ObjectId objectId)
            {
                return "(handent \"" + objectId.Handle.ToString() + "\")\n";
            }
        }
    }
}
