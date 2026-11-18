using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace HvacAiCadAddin.Commands
{
    // Dynamic-block property and bounds helpers shared by HVACSTRETCH and the
    // duct drawing commands. Callers must have a transaction open and the
    // block reference opened through it; dynamic properties only evaluate on
    // references that are already in the database.
    internal static class DynamicBlockHelpers
    {
        // Returns the first writable numeric length-like property.
        internal static bool TryGetDistanceProperty(
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

        internal static bool TryGetDistancePropertyValue(
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

        // editor may be null when reportChange is false.
        internal static bool TrySetDistancePropertyValue(
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

                    if (reportChange && editor != null)
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
                    if (reportChange && editor != null)
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

        internal static bool TryChangeDistanceProperty(
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

        // Projects what the reference actually displays onto a world direction.
        // REC_/Round duct blocks hold 41 invisible polylines fixed at 3000 long
        // and up to 650 wide, and BlockReference.GeometricExtents includes them
        // (checked in accoreconsole on MEP_DWT_3: 236 of 269 duct references
        // had wrong extents). So measure only visible entities of the
        // reference's current block table record, in block coordinates, and
        // transform that box with the block transform. This is also exact for
        // rotated references, where a world-aligned box is too wide. Falls back
        // to GeometricExtents when nothing visible can be measured.
        internal static bool TryGetDisplayedProjectionRange(
            BlockReference block,
            Vector3d direction,
            out double minimumProjection,
            out double maximumProjection)
        {
            Extents3d blockSpaceExtents;
            if (TryGetVisibleBlockSpaceExtents(block, out blockSpaceExtents))
            {
                GetProjectionRange(
                    blockSpaceExtents,
                    block.BlockTransform,
                    direction,
                    out minimumProjection,
                    out maximumProjection);
                return true;
            }

            try
            {
                GetProjectionRange(
                    block.GeometricExtents,
                    direction,
                    out minimumProjection,
                    out maximumProjection);
                return true;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                minimumProjection = 0.0;
                maximumProjection = 0.0;
                return false;
            }
        }

        private static bool TryGetVisibleBlockSpaceExtents(
            BlockReference block,
            out Extents3d extents)
        {
            extents = new Extents3d();
            Database database = block.Database;
            Transaction transaction = database == null
                ? null
                : database.TransactionManager.TopTransaction;
            if (transaction == null)
                return false;

            BlockTableRecord record = transaction.GetObject(
                block.BlockTableRecord,
                OpenMode.ForRead,
                false) as BlockTableRecord;
            if (record == null)
                return false;

            bool found = false;
            foreach (ObjectId entityId in record)
            {
                Entity entity = transaction.GetObject(
                    entityId,
                    OpenMode.ForRead,
                    false) as Entity;
                if (entity == null ||
                    !entity.Visible ||
                    entity is AttributeDefinition)
                {
                    continue;
                }

                Extents3d entityExtents;
                try
                {
                    entityExtents = entity.GeometricExtents;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    continue;
                }

                if (found)
                {
                    extents.AddExtents(entityExtents);
                }
                else
                {
                    extents = entityExtents;
                    found = true;
                }
            }

            return found;
        }

        internal static void GetProjectionRange(
            Extents3d extents,
            Vector3d direction,
            out double minimumProjection,
            out double maximumProjection)
        {
            GetProjectionRange(
                extents,
                Matrix3d.Identity,
                direction,
                out minimumProjection,
                out maximumProjection);
        }

        // Transforms the box corners first, then projects them.
        internal static void GetProjectionRange(
            Extents3d extents,
            Matrix3d transform,
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
                            z == 0 ? min.Z : max.Z).TransformBy(transform);
                        double projection = corner.GetAsVector().DotProduct(direction);
                        minimumProjection = Math.Min(minimumProjection, projection);
                        maximumProjection = Math.Max(maximumProjection, projection);
                    }
                }
            }
        }

        internal static bool IsDistanceProperty(string propertyName)
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

        internal static bool IsNumeric(object value)
        {
            return value is byte ||
                   value is short ||
                   value is int ||
                   value is long ||
                   value is float ||
                   value is double ||
                   value is decimal;
        }
    }
}
