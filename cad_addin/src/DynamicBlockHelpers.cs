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

        internal static void GetProjectionRange(
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
