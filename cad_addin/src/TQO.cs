using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

namespace HvacAiCadAddin.Commands
{
    public class LayerLengthCommand
    {
        [CommandMethod("HVACCOUNT")]
        public void CalculateLayerLengths()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            var totals = new Dictionary<string, double>(
                StringComparer.OrdinalIgnoreCase);

            var counts = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt =
                    (BlockTable)tr.GetObject(
                        db.BlockTableId,
                        OpenMode.ForRead);

                BlockTableRecord modelSpace =
                    (BlockTableRecord)tr.GetObject(
                        bt[BlockTableRecord.ModelSpace],
                        OpenMode.ForRead);

                foreach (ObjectId id in modelSpace)
                {
                    Entity entity =
                        tr.GetObject(id, OpenMode.ForRead) as Entity;

                    if (entity == null)
                        continue;

                    string layerName = entity.Layer;

                    if (!IsTargetLayer(layerName))
                        continue;

                    double length = 0.0;

                    if (entity is Line line)
                    {
                        length = line.Length;
                    }
                    else if (entity is Polyline polyline)
                    {
                        length = polyline.Length;
                    }
                    else
                    {
                        continue;
                    }

                    if (!totals.ContainsKey(layerName))
                    {
                        totals[layerName] = 0.0;
                        counts[layerName] = 0;
                    }

                    totals[layerName] += length;
                    counts[layerName]++;
                }

                tr.Commit();
            }

            ed.WriteMessage("\n\n===== HVAC LAYER LENGTH =====");

            foreach (var item in totals)
            {
                ed.WriteMessage(
                    "\nLayer: {0} | Count: {1} | Length: {2:F2}",
                    item.Key,
                    counts[item.Key],
                    item.Value);
            }

            ed.WriteMessage(
                "\n===== TOTAL LAYERS: {0} =====\n",
                totals.Count);
        }

        private bool IsTargetLayer(string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
                return false;

            // Ví dụ: !ABC-x-01
            // Phải bắt đầu bằng "!"
            // và có "x" trong tên layer.
            return layerName.StartsWith("!")
                && layerName.IndexOf(
                    "x",
                    StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}