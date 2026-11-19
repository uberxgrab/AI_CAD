using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Pipes
{
    // Temporary graphics for the pipe commands: a highlight of the run HVACPIPESIZE found, and the
    // preview and problem markers of HVACREFRIG. Transient graphics never touch the drawing.
    internal sealed class PipeTransients : IDisposable
    {
        private readonly List<Entity> _shown = new List<Entity>();

        /// <summary>About one percent of the view height: a size in drawing units for markers.</summary>
        internal static double ViewSize()
        {
            try
            {
                double view = Convert.ToDouble(
                    Application.GetSystemVariable("VIEWSIZE"), CultureInfo.InvariantCulture);
                return view > 1e-9 ? view : 1000.0;
            }
            catch (System.Exception)
            {
                return 1000.0;
            }
        }

        public void AddOutline(Outline outline, double elevation, short colorIndex, double width)
        {
            Polyline polyline = new Polyline();
            for (int i = 0; i < outline.Vertices.Count; i++)
            {
                OutlineVertex vertex = outline.Vertices[i];
                polyline.AddVertexAt(i, new Point2d(vertex.X, vertex.Y), vertex.Bulge, 0, 0);
            }

            polyline.Closed = outline.Closed;
            polyline.Elevation = elevation;
            polyline.ColorIndex = colorIndex;
            if (width > 0)
                polyline.ConstantWidth = width;
            Show(polyline);
        }

        public void AddPolyline(IList<Vec2> points, double elevation, short colorIndex, double width)
        {
            Outline outline = new Outline { Closed = false };
            foreach (Vec2 point in points)
                outline.Add(point, 0);
            AddOutline(outline, elevation, colorIndex, width);
        }

        public void AddCircle(Vec2 centre, double radius, double elevation, short colorIndex)
        {
            Circle circle = new Circle(new Point3d(centre.X, centre.Y, elevation), Vector3d.ZAxis, radius);
            circle.ColorIndex = colorIndex;
            Show(circle);
        }

        private void Show(Entity entity)
        {
            Autodesk.AutoCAD.GraphicsInterface.TransientManager.CurrentTransientManager.AddTransient(
                entity, Autodesk.AutoCAD.GraphicsInterface.TransientDrawingMode.DirectShortTerm, 128, new IntegerCollection());
            _shown.Add(entity);
        }

        public void Clear()
        {
            foreach (Entity entity in _shown)
            {
                try
                {
                    Autodesk.AutoCAD.GraphicsInterface.TransientManager.CurrentTransientManager.EraseTransient(entity, new IntegerCollection());
                }
                catch (System.Exception)
                {
                }

                entity.Dispose();
            }

            _shown.Clear();
        }

        public void Dispose()
        {
            Clear();
        }
    }
}
