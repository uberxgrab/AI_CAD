using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using HvacAiCadAddin.Ducts;

namespace HvacAiCadAddin.Pipes
{
    // Non-interactive AutoCAD side of HVACREFRIG sizing: where an indoor unit's capacity comes from.
    // For each pipe end, in order: (a) the HVACAI_UNIT xdata a previous run stored on the pipe curve that
    // touches the end; (b) a block whose extents contain the end and whose name has "<number> kW"
    // (FCU-CAS-2.8 kW, IU-5.6 kW); (c) the nearest HVAC-NAME OF EQUIPMENTS block within the search radius
    // whose PROPERTIES attribute has "<number> kW" (its TITLE attribute names the unit); (d) nothing. It
    // needs only AcDbMgd. See AGENTS.md, "HVACREFRIG", "Pipe sizing".

    internal sealed class UnitReading
    {
        public double Kw;
        /// <summary>The capacity index nearest <see cref="Kw"/>.</summary>
        public int Index;
        /// <summary>True when <see cref="Kw"/> is within 6 percent of a nominal capacity.</summary>
        public bool Nominal;
        /// <summary>"xdata", "block" or "label".</summary>
        public string Kind;
        /// <summary>The block name, or the label's TITLE.</summary>
        public string Name;

        /// <summary>The dialog text: where the capacity came from.</summary>
        public string SourceText
        {
            get
            {
                string kw = Kw.ToString("0.0#", CultureInfo.InvariantCulture) + " kW";
                string note = Nominal ? "" : ", nearest index";
                switch (Kind)
                {
                    case "xdata":
                        return "Saved by a previous run (" + kw + ")";
                    case "block":
                        return "Block " + Name + note;
                    default:
                        return "Label " + (string.IsNullOrEmpty(Name) ? "" : Name + " ") + "(" + kw + note + ")";
                }
            }
        }
    }

    internal static class RefrigCapacityReader
    {
        /// <summary>The registered application name of the capacity xdata on a unit's pipe.</summary>
        internal const string UnitApp = "HVACAI_UNIT";

        /// <summary>The block that holds an equipment label.</summary>
        internal const string LabelBlockName = "HVAC-NAME OF EQUIPMENTS";

        private sealed class Candidate
        {
            public double Kw;
            public string Name;
            public Extents3d Extents;
            public bool HasExtents;
            public Vec2 Centre;
        }

        /// <summary>
        /// Reads a capacity for each pipe end. The result has one entry per end, null where nothing was found.
        /// </summary>
        /// <param name="sources">The curves the user selected (their xdata is read).</param>
        /// <param name="ends">The unit positions (pipe ends other than the root).</param>
        /// <param name="tolerance">How near an end must be to a curve end or a block (the weld tolerance).</param>
        /// <param name="radius">How far from an end an equipment label may be.</param>
        internal static UnitReading[] Read(
            Database db, IList<PipeSource> sources, IList<Vec2> ends, double tolerance, double radius)
        {
            UnitReading[] readings = new UnitReading[ends.Count];
            if (ends.Count == 0)
                return readings;

            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                ReadXData(transaction, sources, ends, tolerance, readings);

                List<Candidate> named = new List<Candidate>();
                List<Candidate> labels = new List<Candidate>();
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(
                    db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    if (id.ObjectClass.DxfName != "INSERT")
                        continue;

                    BlockReference block = transaction.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (block == null || block is Table)
                        continue;

                    string name = EffectiveName(transaction, block);
                    if (string.IsNullOrEmpty(name))
                        continue;

                    Candidate candidate = new Candidate { Name = name };
                    try
                    {
                        candidate.Extents = block.GeometricExtents;
                        candidate.HasExtents = true;
                        candidate.Centre = new Vec2(
                            (candidate.Extents.MinPoint.X + candidate.Extents.MaxPoint.X) / 2,
                            (candidate.Extents.MinPoint.Y + candidate.Extents.MaxPoint.Y) / 2);
                    }
                    catch (System.Exception)
                    {
                        candidate.Centre = new Vec2(block.Position.X, block.Position.Y);
                    }

                    if (string.Equals(name, LabelBlockName, StringComparison.OrdinalIgnoreCase))
                    {
                        string title = null;
                        string properties = null;
                        foreach (ObjectId attributeId in block.AttributeCollection)
                        {
                            AttributeReference attribute =
                                transaction.GetObject(attributeId, OpenMode.ForRead) as AttributeReference;
                            if (attribute == null)
                                continue;
                            if (string.Equals(attribute.Tag, "TITLE", StringComparison.OrdinalIgnoreCase))
                                title = attribute.TextString;
                            else if (string.Equals(attribute.Tag, "PROPERTIES", StringComparison.OrdinalIgnoreCase))
                                properties = attribute.TextString;
                        }

                        double kw;
                        if (!CapacityText.TryParseKw(properties, out kw))
                            continue;
                        candidate.Kw = kw;
                        candidate.Name = (title ?? "").Trim();
                        labels.Add(candidate);
                    }
                    else
                    {
                        double kw;
                        if (!CapacityText.TryParseKw(name, out kw))
                            continue;
                        candidate.Kw = kw;
                        named.Add(candidate);
                    }
                }

                MatchNamedBlocks(named, ends, tolerance, readings);
                MatchLabels(labels, ends, radius, readings);
                transaction.Commit();
            }

            return readings;
        }

        private static string EffectiveName(Transaction transaction, BlockReference block)
        {
            try
            {
                ObjectId definitionId = block.IsDynamicBlock ? block.DynamicBlockTableRecord : block.BlockTableRecord;
                BlockTableRecord definition = transaction.GetObject(definitionId, OpenMode.ForRead) as BlockTableRecord;
                return definition == null ? null : definition.Name;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        private static UnitReading MakeReading(string kind, string name, double kw)
        {
            return new UnitReading
            {
                Kind = kind,
                Name = name,
                Kw = kw,
                Index = VrvTables.NearestIndex(kw),
                Nominal = VrvTables.IsNominalKw(kw)
            };
        }

        // (a) xdata a previous run stored on the pipe that touches the end.
        private static void ReadXData(
            Transaction transaction, IList<PipeSource> sources, IList<Vec2> ends, double tolerance, UnitReading[] readings)
        {
            foreach (PipeSource source in sources)
            {
                IList<OutlineVertex> vertices = source.Curve.Vertices;
                if (vertices.Count < 2)
                    continue;

                Vec2 first = new Vec2(vertices[0].X, vertices[0].Y);
                Vec2 last = new Vec2(vertices[vertices.Count - 1].X, vertices[vertices.Count - 1].Y);
                int touching = -1;
                for (int i = 0; i < ends.Count; i++)
                {
                    if (readings[i] == null
                        && (ends[i].DistanceTo(first) <= tolerance || ends[i].DistanceTo(last) <= tolerance))
                    {
                        touching = i;
                        break;
                    }
                }

                if (touching < 0)
                    continue;

                double kw;
                int index;
                Entity entity = transaction.GetObject(source.Id, OpenMode.ForRead) as Entity;
                if (entity == null || !TryReadUnitXData(entity, out index, out kw))
                    continue;

                readings[touching] = MakeReading("xdata", null, kw);
                if (index > 0)
                    readings[touching].Index = index;
            }
        }

        internal static bool TryReadUnitXData(Entity entity, out int index, out double kw)
        {
            index = 0;
            kw = 0;
            using (ResultBuffer buffer = entity.GetXDataForApplication(UnitApp))
            {
                if (buffer == null)
                    return false;

                bool haveIndex = false;
                bool haveKw = false;
                foreach (TypedValue value in buffer)
                {
                    if (value.TypeCode == (short)DxfCode.ExtendedDataInteger32 && !haveIndex)
                    {
                        index = Convert.ToInt32(value.Value, CultureInfo.InvariantCulture);
                        haveIndex = true;
                    }
                    else if (value.TypeCode == (short)DxfCode.ExtendedDataReal && !haveKw)
                    {
                        kw = Convert.ToDouble(value.Value, CultureInfo.InvariantCulture);
                        haveKw = true;
                    }
                }

                return haveIndex && haveKw && index > 0;
            }
        }

        private static double DistanceToBox(Vec2 point, Extents3d extents)
        {
            double dx = Math.Max(Math.Max(extents.MinPoint.X - point.X, 0), point.X - extents.MaxPoint.X);
            double dy = Math.Max(Math.Max(extents.MinPoint.Y - point.Y, 0), point.Y - extents.MaxPoint.Y);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // (b) a block the end touches whose name has a kW value: the extents grown by the tolerance.
        private static void MatchNamedBlocks(
            List<Candidate> named, IList<Vec2> ends, double tolerance, UnitReading[] readings)
        {
            for (int i = 0; i < ends.Count; i++)
            {
                if (readings[i] != null)
                    continue;

                Candidate best = null;
                double bestCentre = double.MaxValue;
                foreach (Candidate candidate in named)
                {
                    if (!candidate.HasExtents || DistanceToBox(ends[i], candidate.Extents) > tolerance)
                        continue;
                    double centre = candidate.Centre.DistanceTo(ends[i]);
                    if (centre < bestCentre)
                    {
                        bestCentre = centre;
                        best = candidate;
                    }
                }

                if (best != null)
                    readings[i] = MakeReading("block", best.Name, best.Kw);
            }
        }

        // (c) the nearest equipment label within the radius. A label serves one end: the pairs are taken
        // nearest first.
        private static void MatchLabels(List<Candidate> labels, IList<Vec2> ends, double radius, UnitReading[] readings)
        {
            List<KeyValuePair<double, int[]>> pairs = new List<KeyValuePair<double, int[]>>();
            for (int i = 0; i < ends.Count; i++)
            {
                if (readings[i] != null)
                    continue;
                for (int j = 0; j < labels.Count; j++)
                {
                    double distance = labels[j].HasExtents
                        ? DistanceToBox(ends[i], labels[j].Extents)
                        : labels[j].Centre.DistanceTo(ends[i]);
                    if (distance <= radius)
                        pairs.Add(new KeyValuePair<double, int[]>(distance, new[] { i, j }));
                }
            }

            pairs.Sort((p, q) => p.Key.CompareTo(q.Key));
            HashSet<int> usedLabels = new HashSet<int>();
            foreach (KeyValuePair<double, int[]> pair in pairs)
            {
                int end = pair.Value[0];
                int label = pair.Value[1];
                if (readings[end] != null || usedLabels.Contains(label))
                    continue;
                usedLabels.Add(label);
                readings[end] = MakeReading("label", labels[label].Name, labels[label].Kw);
            }
        }
    }
}
