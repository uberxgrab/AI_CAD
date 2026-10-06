using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Exception = Autodesk.AutoCAD.Runtime.Exception;
namespace HvacAiCadAddin
{
    public class NativeStretch
    {
        private class CrossingZone
        {
            public Point3d MinPt { get; set; }
            public Point3d MaxPt { get; set; }
        }

        [CommandMethod("YOURSTRETCH")]
        public void MultiCrossingStretch()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            try
            {
                // ===== Bước 1: Thu thập nhiều vùng crossing =====
                List<CrossingZone> zones = GetCrossingZones(ed);
                if (zones.Count == 0)
                {
                    ed.WriteMessage("\nKhông có vùng crossing nào được chọn. Hủy lệnh.");
                    return;
                }

                // ===== Bước 2: Thu thập điểm gốc / điểm đích =====
                PromptPointResult basePtRes = ed.GetPoint("\nChọn điểm gốc: ");
                if (basePtRes.Status != PromptStatus.OK) return;

                PromptPointOptions targetOpt = new PromptPointOptions("\nChọn điểm đích: ")
                {
                    UseBasePoint = true,
                    BasePoint = basePtRes.Value,
                    UseDashedLine = true
                };
                PromptPointResult targetPtRes = ed.GetPoint(targetOpt);
                if (targetPtRes.Status != PromptStatus.OK) return;

                Vector3d displacement = targetPtRes.Value - basePtRes.Value;

                // ===== Bước 3: Gom (ObjectId, gripIndex) tránh trùng giữa các vùng =====
                // key: ObjectId, value: tập chỉ số grip cần di chuyển
                Dictionary<ObjectId, HashSet<int>> gripMap = new Dictionary<ObjectId, HashSet<int>>();
                HashSet<ObjectId> skippedEntities = new HashSet<ObjectId>(); // entity không hỗ trợ grip

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (var zone in zones)
                    {
                        PromptSelectionResult psr = ed.SelectCrossingWindow(zone.MinPt, zone.MaxPt);
                        if (psr.Status != PromptStatus.OK) continue;

                        foreach (SelectedObject so in psr.Value)
                        {
                            ObjectId id = so.ObjectId;
                            if (skippedEntities.Contains(id)) continue;

                            Entity ent;
                            try
                            {
                                ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            }
                            catch
                            {
                                skippedEntities.Add(id);
                                continue;
                            }
                            if (ent == null)
                            {
                                skippedEntities.Add(id);
                                continue;
                            }

                            GripDataCollection grips = new GripDataCollection();
                            try
                            {
                                ent.GetGripPoints(grips, 0.0, 0, Vector3d.ZAxis, GetGripPointsFlags.GripPointsOnly);

                            }
                            catch
                            {
                                // Entity không hỗ trợ grip -> bỏ qua, giữ nguyên vị trí
                                skippedEntities.Add(id);
                                continue;
                            }

                            if (grips == null || grips.Count == 0)
                            {
                                // Không có grip nào trả về -> giữ nguyên, không đụng vào
                                skippedEntities.Add(id);
                                continue;
                            }

                            if (!gripMap.ContainsKey(id))
                                gripMap[id] = new HashSet<int>();

                            for (int i = 0; i < grips.Count; i++)
                            {
                                Point3d gp = grips[i].GripPoint;
                                if (gp.X >= zone.MinPt.X && gp.X <= zone.MaxPt.X &&
                                    gp.Y >= zone.MinPt.Y && gp.Y <= zone.MaxPt.Y)
                                {
                                    gripMap[id].Add(i);
                                }
                            }
                        }
                    }

                    // ===== Áp dụng MoveGripPointsAt cho từng entity, 1 lần duy nhất =====
                    int movedCount = 0;
                    int skippedCount = 0;

                    foreach (var kvp in gripMap)
                    {
                        if (kvp.Value.Count == 0) continue;

                        Entity entForWrite;
                        try
                        {
                            entForWrite = tr.GetObject(kvp.Key, OpenMode.ForWrite) as Entity;
                        }
                        catch
                        {
                            skippedCount++;
                            continue;
                        }
                        if (entForWrite == null)
                        {
                            skippedCount++;
                            continue;
                        }

                        IntegerCollection indices = new IntegerCollection();
                        foreach (int idx in kvp.Value)
                            indices.Add(idx);

                        try
                        {
                            entForWrite.MoveGripPointsAt(indices, displacement);
                            movedCount++;
                        }
                        catch
                        {
                            // Nếu MoveGripPointsAt lỗi (entity không thực sự hỗ trợ dù
                            // GetGripPoints không throw) -> bỏ qua, không đụng gì vào entity này
                            skippedCount++;
                        }
                    }

                    tr.Commit();

                    ed.WriteMessage($"\nĐã stretch {movedCount} entity. Bỏ qua {skippedCount + skippedEntities.Count} entity không hỗ trợ grip stretch.");
                }
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\nLệnh bị hủy hoặc lỗi: {ex.Message}");
            }
        }

        /// <summary>
        /// Thu thập nhiều vùng crossing liên tiếp. Nhấn Enter (không chọn điểm) để dừng.
        /// </summary>
        private List<CrossingZone> GetCrossingZones(Editor ed)
        {
            List<CrossingZone> zones = new List<CrossingZone>();

            while (true)
            {
                PromptPointOptions ppo1 = new PromptPointOptions(
                    "\nChọn điểm đầu crossing (Enter để kết thúc): ")
                {
                    AllowNone = true
                };
                PromptPointResult ppr1 = ed.GetPoint(ppo1);

                if (ppr1.Status != PromptStatus.OK)
                    break;

                PromptCornerOptions ppo2 = new PromptCornerOptions(
                    "\nChọn điểm đối diện: ", ppr1.Value);
                PromptPointResult ppr2 = ed.GetCorner(ppo2);

                if (ppr2.Status != PromptStatus.OK)
                    break;

                Point3d p1 = ppr1.Value;
                Point3d p2 = ppr2.Value;

                zones.Add(new CrossingZone
                {
                    MinPt = new Point3d(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y), 0),
                    MaxPt = new Point3d(Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y), 0)
                });

                ed.WriteMessage($"\nĐã thêm vùng crossing {zones.Count}.");
            }

            return zones;
        }
    }
}