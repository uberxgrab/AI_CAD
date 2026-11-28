using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace ScriptedTests
{
    // Writes the model space of a drawing as canonical UTF-8 JSON: one object per entity, no handles, numbers rounded
    // to 0.01, entities sorted by their own text. tests\Scripted\compare.py reads it. The code is AutoCAD-first and
    // compiled for ZWCAD through the namespace mapping, so both CADs write the same shape.
    internal static class DrawingDump
    {
        internal static double R(double value)
        {
            double rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
            return rounded == 0 ? 0 : rounded;
        }

        internal static string Write(Database db, string scenario, string cad, string cadVersion, string path)
        {
            List<string> entities = new List<string>();
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)transaction.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord model = (BlockTableRecord)transaction.GetObject(
                    table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in model)
                {
                    Entity entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
                    if (entity == null)
                        continue;
                    SortedDictionary<string, object> item;
                    try
                    {
                        item = Describe(transaction, entity);
                    }
                    catch (System.Exception ex)
                    {
                        item = new SortedDictionary<string, object>
                        {
                            { "type", entity.GetType().Name }, { "layer", entity.Layer }, { "error", ex.Message }
                        };
                    }

                    entities.Add(Json.Write(item));
                }

                transaction.Commit();
            }

            entities.Sort(StringComparer.Ordinal);
            StringBuilder text = new StringBuilder();
            text.Append("{\"scenario\":").Append(Json.Quote(scenario));
            text.Append(",\"cad\":").Append(Json.Quote(cad));
            text.Append(",\"cadVersion\":").Append(Json.Quote(cadVersion));
            text.Append(",\"entities\":[\n");
            text.Append(string.Join(",\n", entities));
            text.Append("\n]}\n");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            return text.ToString();
        }

        // Handles differ from drawing to drawing and between CADs; a handle stored in xdata (hex digits with at least
        // one digit, like "12C") is replaced by a marker so that dumps stay comparable.
        private static string ScrubHandle(string text)
        {
            if (text != null && text.Length >= 2 && text.Length <= 8)
            {
                bool hex = true;
                bool digit = false;
                foreach (char c in text)
                {
                    if (c >= '0' && c <= '9')
                        digit = true;
                    else if (!(c >= 'A' && c <= 'F'))
                        hex = false;
                }

                if (hex && digit)
                    return "<handle>";
            }

            return text;
        }

        private static List<object> Pt(double x, double y)
        {
            return new List<object> { R(x), R(y) };
        }

        private static SortedDictionary<string, object> Describe(Transaction transaction, Entity entity)
        {
            SortedDictionary<string, object> item = new SortedDictionary<string, object>();
            item["type"] = entity.GetType().Name;
            item["layer"] = entity.Layer;
            if (!entity.Color.IsByLayer)
                item["color"] = entity.Color.IsByBlock ? "ByBlock" : entity.Color.ColorIndex.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(entity.Linetype, "ByLayer", StringComparison.OrdinalIgnoreCase))
                item["linetype"] = entity.Linetype;

            Line line = entity as Line;
            Polyline polyline = entity as Polyline;
            BlockReference block = entity as BlockReference;
            DBText text = entity as DBText;
            MText mtext = entity as MText;
            Circle circle = entity as Circle;
            Arc arc = entity as Arc;
            if (line != null)
            {
                item["type"] = "LINE";
                List<List<object>> ends = new List<List<object>>
                {
                    Pt(line.StartPoint.X, line.StartPoint.Y), Pt(line.EndPoint.X, line.EndPoint.Y)
                };
                ends.Sort((a, b) => ((double)a[0] != (double)b[0])
                    ? ((double)a[0]).CompareTo((double)b[0])
                    : ((double)a[1]).CompareTo((double)b[1]));
                item["line"] = new List<object> { ends[0][0], ends[0][1], ends[1][0], ends[1][1] };
            }
            else if (polyline != null)
            {
                item["type"] = "LWPOLYLINE";
                List<object> vertices = new List<object>();
                for (int i = 0; i < polyline.NumberOfVertices; i++)
                {
                    Point2d p = polyline.GetPoint2dAt(i);
                    vertices.Add(new List<object> { R(p.X), R(p.Y), R(polyline.GetBulgeAt(i)) });
                }

                item["vertices"] = vertices;
                item["closed"] = polyline.Closed;
            }
            else if (block != null)
            {
                item["type"] = block is Table ? "ACAD_TABLE" : "INSERT";
                string name = block.Name;
                if (block.IsDynamicBlock)
                {
                    BlockTableRecord definition = (BlockTableRecord)transaction.GetObject(
                        block.DynamicBlockTableRecord, OpenMode.ForRead);
                    name = definition.Name;
                    SortedDictionary<string, object> dyn = new SortedDictionary<string, object>();
                    foreach (DynamicBlockReferenceProperty property in block.DynamicBlockReferencePropertyCollection)
                    {
                        object value = property.Value;
                        if (value is double || value is float || value is int || value is short)
                            dyn[property.PropertyName] = R(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                        else if (value is string)
                            dyn[property.PropertyName] = (string)value;
                    }

                    item["dyn"] = dyn;
                }

                item["block"] = name;
                item["pos"] = new List<object> { R(block.Position.X), R(block.Position.Y), R(block.Position.Z) };
                item["rot"] = R(block.Rotation * 180.0 / Math.PI);
                item["scale"] = new List<object> { R(block.ScaleFactors.X), R(block.ScaleFactors.Y), R(block.ScaleFactors.Z) };
                if (block.AttributeCollection.Count > 0)
                {
                    SortedDictionary<string, object> attributes = new SortedDictionary<string, object>();
                    foreach (ObjectId attributeId in block.AttributeCollection)
                    {
                        AttributeReference attribute = transaction.GetObject(attributeId, OpenMode.ForRead) as AttributeReference;
                        if (attribute != null)
                            attributes[attribute.Tag] = attribute.TextString;
                    }

                    item["attributes"] = attributes;
                }
            }
            else if (text != null)
            {
                item["type"] = "TEXT";
                item["text"] = text.TextString;
                item["height"] = R(text.Height);
                item["pos"] = Pt(text.Position.X, text.Position.Y);
            }
            else if (mtext != null)
            {
                item["type"] = "MTEXT";
                item["text"] = mtext.Contents;
                item["height"] = R(mtext.TextHeight);
                item["pos"] = Pt(mtext.Location.X, mtext.Location.Y);
            }
            else if (circle != null && arc == null)
            {
                item["type"] = "CIRCLE";
                item["center"] = Pt(circle.Center.X, circle.Center.Y);
                item["radius"] = R(circle.Radius);
            }
            else if (arc != null)
            {
                item["type"] = "ARC";
                item["center"] = Pt(arc.Center.X, arc.Center.Y);
                item["radius"] = R(arc.Radius);
                item["angles"] = new List<object> { R(arc.StartAngle * 180 / Math.PI), R(arc.EndAngle * 180 / Math.PI) };
            }

            ResultBuffer xdata = entity.XData;
            if (xdata != null)
            {
                SortedDictionary<string, object> apps = new SortedDictionary<string, object>();
                string current = null;
                List<object> values = null;
                foreach (TypedValue value in xdata)
                {
                    if (value.TypeCode == (int)DxfCode.ExtendedDataRegAppName)
                    {
                        current = Convert.ToString(value.Value, CultureInfo.InvariantCulture);
                        values = new List<object>();
                        apps[current] = values;
                    }
                    else if (current != null && current.StartsWith("HVACAI_", StringComparison.Ordinal))
                    {
                        object v = value.Value;
                        if (v is double)
                            values.Add(R((double)v));
                        else if (v is short || v is int)
                            values.Add(Convert.ToDouble(v, CultureInfo.InvariantCulture));
                        else if (v is Point3d)
                            values.Add(Pt(((Point3d)v).X, ((Point3d)v).Y));
                        else
                            values.Add(ScrubHandle(Convert.ToString(v, CultureInfo.InvariantCulture)));
                    }
                }

                xdata.Dispose();
                item["xdata"] = apps;
            }

            return item;
        }
    }

    internal static class Json
    {
        internal static string Quote(string s)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }

            return sb.Append('"').ToString();
        }

        internal static string Write(object value)
        {
            StringBuilder sb = new StringBuilder();
            Append(sb, value);
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }
            if (value is string) { sb.Append(Quote((string)value)); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            if (value is double)
            {
                double d = (double)value;
                sb.Append(d == Math.Floor(d) && Math.Abs(d) < 1e15
                    ? ((long)d).ToString(CultureInfo.InvariantCulture)
                    : d.ToString("0.##########", CultureInfo.InvariantCulture));
                return;
            }

            SortedDictionary<string, object> map = value as SortedDictionary<string, object>;
            if (map != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> pair in map)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Quote(pair.Key)).Append(':');
                    Append(sb, pair.Value);
                }

                sb.Append('}');
                return;
            }

            System.Collections.IEnumerable list = value as System.Collections.IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object o in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Append(sb, o);
                }

                sb.Append(']');
                return;
            }

            sb.Append(Quote(Convert.ToString(value, CultureInfo.InvariantCulture)));
        }
    }
}
