using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using HvacAiCadAddin.Commands;

namespace ScriptedTests
{
    // The scenario drawings of tests\Scripted. Each one is built from a blank Database (rough networks as LINEs and
    // LWPOLYLINEs, duct, unit and label blocks imported from a COPY of the template through TemplateSource) and saved
    // as a DWG; the script of the same name (tests\Scripted\scenarios\<name>.scr) then runs the command on it.
    // Everything is plain millimetres. The code is AutoCAD-first and mapped to ZWCAD like the other test suites.
    internal sealed class Scenario
    {
        public string Name;
        public Action<ScenarioBuilder> Build;
    }

    internal sealed class ScenarioBuilder
    {
        internal const string SupplyLayer = "EIA_1 SUPPLY AIR";
        internal const string EquipmentLayer = "EIA_1 MECHANICAL EQUIPMENT";
        internal const string RoughLayer = "ROUGH";

        private readonly TemplateSource _template;

        internal ScenarioBuilder(Database db, TemplateSource template)
        {
            Db = db;
            _template = template;
        }

        internal Database Db { get; private set; }

        // ---------------------------------------------------------------- template

        internal void ImportBlocks(params string[] names)
        {
            List<string> missing;
            _template.ImportBlocks(Db, names, out missing);
            if (missing.Count > 0)
                throw new InvalidOperationException("template lacks blocks: " + string.Join(", ", missing));
        }

        internal void ImportLayers(params string[] names)
        {
            List<string> missing;
            _template.ImportLayers(Db, names, out missing);
            if (missing.Count > 0)
                throw new InvalidOperationException("template lacks layers: " + string.Join(", ", missing));
        }

        internal void PlainLayer(string name)
        {
            using (Transaction transaction = Db.TransactionManager.StartTransaction())
            {
                LayerTable layers = (LayerTable)transaction.GetObject(Db.LayerTableId, OpenMode.ForWrite);
                if (!layers.Has(name))
                {
                    LayerTableRecord layer = new LayerTableRecord { Name = name };
                    layers.Add(layer);
                    transaction.AddNewlyCreatedDBObject(layer, true);
                }

                transaction.Commit();
            }
        }

        // ---------------------------------------------------------------- entities

        // By id: a layer name resolves against the working database, not against a side database.
        private void SetLayer(Transaction transaction, Entity entity, string layer)
        {
            LayerTable layers = (LayerTable)transaction.GetObject(Db.LayerTableId, OpenMode.ForRead);
            if (!layers.Has(layer))
                throw new InvalidOperationException("layer " + layer + " is not in the drawing");
            entity.LayerId = layers[layer];
        }

        private void Append(Func<Transaction, Entity> make)
        {
            using (Transaction transaction = Db.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)transaction.GetObject(Db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord model = (BlockTableRecord)transaction.GetObject(
                    table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                Entity entity = make(transaction);
                model.AppendEntity(entity);
                transaction.AddNewlyCreatedDBObject(entity, true);
                transaction.Commit();
            }
        }

        internal void Line(string layer, double x1, double y1, double x2, double y2)
        {
            Append(t =>
            {
                Line line = new Line(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0));
                line.SetDatabaseDefaults(Db);
                SetLayer(t, line, layer);
                return line;
            });
        }

        internal void Poly(string layer, params double[] xy)
        {
            Append(t =>
            {
                Autodesk.AutoCAD.DatabaseServices.Polyline polyline = new Autodesk.AutoCAD.DatabaseServices.Polyline();
                polyline.SetDatabaseDefaults(Db);
                for (int i = 0; i + 1 < xy.Length; i += 2)
                    polyline.AddVertexAt(i / 2, new Point2d(xy[i], xy[i + 1]), 0, 0, 0);
                SetLayer(t, polyline, layer);
                return polyline;
            });
        }

        // A block reference; distance3 > 0 sets the dynamic property of a duct block. Attributes (tag -> text)
        // are filled from the definition's attribute definitions.
        internal void Insert(
            string block, string layer, double x, double y, double rotationDegrees = 0, double distance3 = 0,
            IDictionary<string, string> attributes = null)
        {
            using (Transaction transaction = Db.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)transaction.GetObject(Db.BlockTableId, OpenMode.ForRead);
                if (!table.Has(block))
                    throw new InvalidOperationException("block " + block + " is not in the drawing");
                BlockTableRecord model = (BlockTableRecord)transaction.GetObject(
                    table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                BlockReference reference = new BlockReference(new Point3d(x, y, 0), table[block]);
                reference.SetDatabaseDefaults(Db);
                SetLayer(transaction, reference, layer);
                reference.Rotation = rotationDegrees * Math.PI / 180.0;
                model.AppendEntity(reference);
                transaction.AddNewlyCreatedDBObject(reference, true);

                BlockTableRecord definition = (BlockTableRecord)transaction.GetObject(table[block], OpenMode.ForRead);
                if (definition.HasAttributeDefinitions)
                {
                    foreach (ObjectId id in definition)
                    {
                        AttributeDefinition attributeDefinition = transaction.GetObject(id, OpenMode.ForRead) as AttributeDefinition;
                        if (attributeDefinition == null || attributeDefinition.Constant)
                            continue;
                        AttributeReference attribute = new AttributeReference();
                        attribute.SetAttributeFromBlock(attributeDefinition, reference.BlockTransform);
                        string text;
                        if (attributes != null && attributes.TryGetValue(attributeDefinition.Tag, out text))
                            attribute.TextString = text;
                        reference.AttributeCollection.AppendAttribute(attribute);
                        transaction.AddNewlyCreatedDBObject(attribute, true);
                    }
                }

                if (distance3 > 0
                    && !DynamicBlockHelpers.TrySetDistancePropertyValue(reference, "Distance3", distance3, null, false))
                    throw new InvalidOperationException("Distance3 of " + block + " could not be set");
                transaction.Commit();
            }
        }
    }

    internal static class Scenarios
    {
        private const string Duct = "REC_600x300";

        internal static readonly List<Scenario> All = new List<Scenario>
        {
            // HVACSTRETCH (a): one crossing over the end of a LINE and the end of a duct block.
            new Scenario { Name = "stretch_end", Build = b =>
            {
                b.ImportBlocks(Duct);
                b.ImportLayers(ScenarioBuilder.SupplyLayer);
                b.Line("0", 0, 0, 5000, 0);
                b.Insert(Duct, ScenarioBuilder.SupplyLayer, 0, 2000, 0, 3000);
            }},

            // HVACSTRETCH (b): a crossing that fully contains a block (and a line) moves them whole.
            new Scenario { Name = "stretch_whole", Build = b =>
            {
                b.ImportBlocks(Duct);
                b.ImportLayers(ScenarioBuilder.SupplyLayer);
                b.Insert(Duct, ScenarioBuilder.SupplyLayer, 0, 0, 0, 1000);
                b.Line("0", 100, -800, 900, -800);
            }},

            // HVACSTRETCH (c): R and a pick remove one object from the selection; it stays.
            new Scenario { Name = "stretch_remove", Build = b =>
            {
                b.Line("0", 0, 0, 5000, 0);
                b.Line("0", 0, 1000, 5000, 1000);
            }},

            // HVACSTRETCH (d): two crossings in one selection.
            new Scenario { Name = "stretch_two", Build = b =>
            {
                b.Line("0", 0, 0, 5000, 0);
                b.Line("0", 0, 3000, 5000, 3000);
                b.Line("0", 0, 6000, 5000, 6000);
            }},

            // HVACDUCT: a two-leg run with an elbow on a blank drawing (the frame line only gives the view an extent).
            new Scenario { Name = "duct_elbow", Build = b =>
            {
                b.PlainLayer(ScenarioBuilder.RoughLayer);
                b.Line(ScenarioBuilder.RoughLayer, -1000, -1000, 6000, 4000);
            }},

            // HVACDUCTALIGN: align a parallel duct beside another one (not coaxial, so no join).
            new Scenario { Name = "align_edge", Build = b =>
            {
                b.ImportBlocks(Duct);
                b.ImportLayers(ScenarioBuilder.SupplyLayer);
                b.Insert(Duct, ScenarioBuilder.SupplyLayer, 0, 0, 0, 2000);
                b.Insert(Duct, ScenarioBuilder.SupplyLayer, 500, 1200, 0, 2000);
            }},

            // HVACDUCTALIGN: two ducts of one size, nearly coaxial with facing ends: after the align they are merged.
            new Scenario { Name = "align_merge", Build = b =>
            {
                b.ImportBlocks(Duct);
                b.ImportLayers(ScenarioBuilder.SupplyLayer);
                b.Insert(Duct, ScenarioBuilder.SupplyLayer, 0, 0, 0, 2000);
                b.Insert(Duct, ScenarioBuilder.SupplyLayer, 3000, 100, 0, 2000);
            }},

            // HVACPIPESIZE: a T of rough pipe lines; the pick is on the left arm of the run.
            new Scenario { Name = "pipesize", Build = b =>
            {
                b.PlainLayer(ScenarioBuilder.RoughLayer);
                b.Line(ScenarioBuilder.RoughLayer, 0, 0, 4000, 0);
                b.Line(ScenarioBuilder.RoughLayer, 4000, 0, 4000, 3000);
                b.Line(ScenarioBuilder.RoughLayer, 2000, 0, 2000, -1500);
            }},

            // HVACREFRIG: a rough tree (a few mm off the axes) with three indoor units whose capacity is in the block name.
            new Scenario { Name = "refrig", Build = b =>
            {
                b.ImportBlocks("FCU-CAS-2.8 kW", "FCU-CAS-5.6 kW", "FCU-CAS-7.1 kW");
                b.ImportLayers(ScenarioBuilder.EquipmentLayer);
                b.PlainLayer(ScenarioBuilder.RoughLayer);
                b.Line(ScenarioBuilder.RoughLayer, 0, 0, 3000, 20);
                b.Line(ScenarioBuilder.RoughLayer, 3000, 20, 6000, -15);
                b.Line(ScenarioBuilder.RoughLayer, 6000, -15, 9000, 0);
                b.Line(ScenarioBuilder.RoughLayer, 3000, 20, 3010, -2600);
                b.Line(ScenarioBuilder.RoughLayer, 6000, -15, 5990, -2600);
                b.Insert("FCU-CAS-2.8 kW", ScenarioBuilder.EquipmentLayer, 3010, -2300);
                b.Insert("FCU-CAS-5.6 kW", ScenarioBuilder.EquipmentLayer, 5990, -2300);
                b.Insert("FCU-CAS-7.1 kW", ScenarioBuilder.EquipmentLayer, 9000, 300);
            }},

            // HVACDRAIN: the same tree as a rough condensate network; the drain point is its left end.
            new Scenario { Name = "drain", Build = b =>
            {
                b.ImportBlocks("FCU-CAS-2.8 kW", "FCU-CAS-5.6 kW", "FCU-CAS-7.1 kW");
                b.ImportLayers(ScenarioBuilder.EquipmentLayer);
                b.PlainLayer(ScenarioBuilder.RoughLayer);
                b.Line(ScenarioBuilder.RoughLayer, 0, 0, 3000, 20);
                b.Line(ScenarioBuilder.RoughLayer, 3000, 20, 6000, -15);
                b.Line(ScenarioBuilder.RoughLayer, 6000, -15, 9000, 0);
                b.Line(ScenarioBuilder.RoughLayer, 3000, 20, 3010, -2600);
                b.Line(ScenarioBuilder.RoughLayer, 6000, -15, 5990, -2600);
                b.Insert("FCU-CAS-2.8 kW", ScenarioBuilder.EquipmentLayer, 3010, -2300);
                b.Insert("FCU-CAS-5.6 kW", ScenarioBuilder.EquipmentLayer, 5990, -2300);
                b.Insert("FCU-CAS-7.1 kW", ScenarioBuilder.EquipmentLayer, 9000, 300);
            }},

            // HVACPOWERWIRE: a rough trunk with two side branches and a unit at the trunk end.
            new Scenario { Name = "powerwire", Build = b =>
            {
                b.ImportLayers(PowerSupplyLayer);
                b.PlainLayer(ScenarioBuilder.RoughLayer);
                b.Line(ScenarioBuilder.RoughLayer, 0, 0, 3000, 20);
                b.Line(ScenarioBuilder.RoughLayer, 3000, 20, 6000, -15);
                b.Line(ScenarioBuilder.RoughLayer, 6000, -15, 9000, 0);
                b.Line(ScenarioBuilder.RoughLayer, 3000, 20, 3010, -2600);
                b.Line(ScenarioBuilder.RoughLayer, 6000, -15, 5990, -2600);
            }},

            // HVACCTRLWIRE: three cassettes in a row, a template route from the thermostat to the last one.
            new Scenario { Name = "ctrlwire", Build = b =>
            {
                b.ImportBlocks("FCU-CAS-2.8 kW");
                b.ImportLayers(ScenarioBuilder.EquipmentLayer, "EIA_5 CONTROL WIRE");
                b.PlainLayer(ScenarioBuilder.RoughLayer);
                b.Insert("FCU-CAS-2.8 kW", ScenarioBuilder.EquipmentLayer, 1500, 0);
                b.Insert("FCU-CAS-2.8 kW", ScenarioBuilder.EquipmentLayer, 4500, 0);
                b.Insert("FCU-CAS-2.8 kW", ScenarioBuilder.EquipmentLayer, 7500, 0);
                b.Poly(ScenarioBuilder.RoughLayer, 0, -3000, 7500, -3000, 7500, -300);
            }},

            // HVACQTO: ducts, equipment and pipe curves on takeoff layers.
            new Scenario { Name = "qto", Build = b =>
            {
                b.ImportBlocks(Duct, "REC_400x300", "FCU-CAS-2.8 kW", "FCU-CAS-5.6 kW");
                b.ImportLayers(ScenarioBuilder.SupplyLayer, ScenarioBuilder.EquipmentLayer,
                    "EIA_1 CHWS DN100", "EIA_1 Pipe condensated DN27", "EIA_1 RP 12.7-6.4");
                b.Insert(Duct, ScenarioBuilder.SupplyLayer, 0, 0, 0, 2000);
                b.Insert(Duct, ScenarioBuilder.SupplyLayer, 0, 1000, 0, 3000);
                b.Insert("REC_400x300", ScenarioBuilder.SupplyLayer, 0, 2000, 0, 1500);
                b.Insert("FCU-CAS-2.8 kW", ScenarioBuilder.EquipmentLayer, 6000, 0);
                b.Insert("FCU-CAS-2.8 kW", ScenarioBuilder.EquipmentLayer, 8000, 0);
                b.Insert("FCU-CAS-5.6 kW", ScenarioBuilder.EquipmentLayer, 10000, 0);
                b.Line("EIA_1 CHWS DN100", 0, -2000, 10000, -2000);
                b.Line("EIA_1 Pipe condensated DN27", 0, -3000, 4000, -3000);
                b.Poly("EIA_1 RP 12.7-6.4", 0, -4000, 3000, -4000, 3000, -6000);
            }},
        };

        private const string PowerSupplyLayer = "EIA_5 SUPPLY WIRE";
    }
}
