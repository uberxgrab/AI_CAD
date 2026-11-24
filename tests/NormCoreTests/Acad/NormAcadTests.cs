using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using HvacAiCadAddin.Commands;
using HvacAiCadAddin.Ducts;
using HvacAiCadAddin.Norm;
using HvacAiCadAddin.Rooms;
using HvacAiCadAddin.Takeoff;

[assembly: CommandClass(typeof(NormAcadTests.NormAcadTestCommands))]

namespace NormAcadTests
{
    // Runs in a blank drawing. Needs HVACNORM_LOG, HVACNORM_TEMPLATE (a copy of CAD\MEP_DWT_3.dwg),
    // HVACNORM_PYTHON and HVACNORM_SCRIPT (set by run-tests.ps1). Results go to the log file.
    public class NormAcadTestCommands
    {
        private static int passed, failed;

        private static void Log(string text)
        {
            string path = Environment.GetEnvironmentVariable("HVACNORM_LOG") ?? Path.Combine(Path.GetTempPath(), "hvacnorm.log");
            File.AppendAllText(path, text + Environment.NewLine, new UTF8Encoding(false));
        }

        private static void Check(string name, bool ok, string detail = "")
        {
            if (ok) { passed++; Log("PASS " + name); }
            else { failed++; Log("FAIL " + name + " :: " + detail); }
        }

        [CommandMethod("NORMTEST")]
        public void RunAll()
        {
            try
            {
                Body();
            }
            catch (System.Exception ex)
            {
                failed++;
                Log("FAIL NORMTEST crashed :: " + ex);
            }
            finally
            {
                Log("SUMMARY passed=" + passed + " failed=" + failed);
            }
        }

        // ------------------------------------------------------------------ HVACDUCTLINE

        [CommandMethod("DUCTLINETEST")]
        public void RunDuctLine()
        {
            try
            {
                DuctLineBody();
            }
            catch (System.Exception ex)
            {
                failed++;
                Log("FAIL DUCTLINETEST crashed :: " + ex);
            }
            finally
            {
                Log("SUMMARY passed=" + passed + " failed=" + failed);
            }
        }

        private static void DuctLineBody()
        {
            Database db = HostApplicationServices.WorkingDatabase;
            Dictionary<int, ObjectId> ids = new Dictionary<int, ObjectId>();
            List<DuctLineCurve> curves = new List<DuctLineCurve>();
            HashSet<ObjectId> before = new HashSet<ObjectId>();
            using (Transaction transaction = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForRead))
                    before.Add(id);
            }
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                AddLayer(transaction, db, "M-HVAC-DUCT-SA");
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                ObjectId main = Add(transaction, space, Poly(0, 0, 10000, 0), "M-HVAC-DUCT-SA");
                ObjectId tail = Add(transaction, space, Poly(10000, 0, 14000, 0, 14000, 5000), "M-HVAC-DUCT-SA");
                ObjectId branch = Add(transaction, space, new Line(new Point3d(5000, 0, 0), new Point3d(5000, 4000, 0)), "M-HVAC-DUCT-SA");
                NormWriter.EnsureRegApp(transaction, db, SizeTag.DuctApp);
                Tag(transaction, main, 600, 300);
                Tag(transaction, tail, 400, 300);
                Tag(transaction, branch, 300, 300);
                transaction.Commit();
                foreach (ObjectId id in new[] { main, tail, branch })
                    ids[ids.Count] = id;
            }

            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                foreach (KeyValuePair<int, ObjectId> pair in ids)
                    curves.Add(DuctLineReader.Read((Entity)transaction.GetObject(pair.Value, OpenMode.ForRead), pair.Key));
                transaction.Commit();
            }

            Check("reader takes the tag size", curves[0].Size != null && curves[0].Size.Label == "600x300" && curves[0].System == "SA");
            DuctLineSettings settings = new DuctLineSettings();
            DuctLinePlan plan = DuctLinePlanner.Plan(curves, new Vec2(0, 0), settings);
            Check("plan ok", plan.Ok && plan.Runs.All(r => r.Ok), plan.Error + " " + string.Join("|", plan.Reports));

            string templatePath = Environment.GetEnvironmentVariable("HVACNORM_TEMPLATE");
            string error;
            TemplateCatalog catalog = TemplateCatalog.Read(templatePath, out error);
            using (TemplateSource template = TemplateSource.TryOpen(templatePath, out error))
            {
                DuctLineWriteSummary written = DuctLineWriter.Write(db, plan,
                    run => DuctLinePlanner.LayerForSystem(run.Group) ?? "EIA_1 SUPPLY AIR", template, catalog.Sizes,
                    settings.Fittings, ids, false);
                Log("ductline written: runs=" + written.Runs + " ducts=" + written.Ducts + " fittings=" + written.Fittings
                    + " taps=" + written.Taps + " tagsCleared=" + written.TagsCleared + " errors=" + string.Join("|", written.Errors));
                Check("two runs drawn", written.Runs == 2 && written.Errors.Count == 0, string.Join("|", written.Errors));
                Check("one tap", written.Taps == 1);
                Check("tags of the single lines removed", written.TagsCleared == 3);
            }

            Check("layer imported", HasLayer(db, "EIA_1 SUPPLY AIR"));
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                List<TakeoffBlockInput> blocks = new List<TakeoffBlockInput>();
                Dictionary<string, int> fittings = new Dictionary<string, int>();
                List<TakeoffCurveInput> lines = new List<TakeoffCurveInput>();
                foreach (ObjectId id in space)
                {
                    if (before.Contains(id))
                        continue; // left by NORMTEST in the same drawing
                    Entity entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                    BlockReference block = entity as BlockReference;
                    if (block != null)
                    {
                        BlockTableRecord definition = (BlockTableRecord)transaction.GetObject(
                            block.IsDynamicBlock ? block.DynamicBlockTableRecord : block.BlockTableRecord, OpenMode.ForRead);
                        TakeoffBlockInput input = new TakeoffBlockInput(definition.Name);
                        foreach (DynamicBlockReferenceProperty property in block.DynamicBlockReferencePropertyCollection)
                        {
                            if (property.PropertyName == "Distance3")
                                input.Distance3 = Convert.ToDouble(property.Value);
                        }

                        Check("duct block on the SA layer: " + definition.Name, block.Layer == "EIA_1 SUPPLY AIR", block.Layer);
                        blocks.Add(input);
                        continue;
                    }

                    string kind;
                    string sizeText;
                    if (DuctBlockLibrary.TryReadFittingTag(entity, out kind, out sizeText))
                    {
                        int n;
                        fittings.TryGetValue(kind, out n);
                        fittings[kind] = n + 1;
                    }
                    else if (entity is Curve)
                    {
                        lines.Add(new TakeoffCurveInput(entity.Layer, ((Curve)entity).GetDistanceAtParameter(((Curve)entity).EndParam))
                        {
                            Tag = NormWriter.ReadTag(entity)
                        });
                    }
                }

                Log("fittings: " + string.Join(", ", fittings.Select(p => p.Key + "=" + p.Value)));
                Check("tap, transition and elbow written", fittings.ContainsKey(RouteFitting.TapKind)
                    && fittings.ContainsKey(RouteFitting.TransitionKind) && fittings.ContainsKey(RouteFitting.ElbowKind));
                TakeoffResult takeoff = TakeoffAggregator.Aggregate(blocks, lines, TakeoffOptions.CreateDefault());
                Log("ducts: " + string.Join(" | ", takeoff.DuctSizeRows.Select(r => r.SizeText + " " + r.LengthMeters.ToString("0.00"))));
                Check("three duct sizes in the BOQ", takeoff.DuctSizeRows.Count == 3);
                Check("single lines no longer counted", takeoff.TaggedCurveCount == 0);
                DuctSizeRow big = takeoff.DuctSizeRows.Single(r => r.SizeText == "600 x 300 mm");
                Check("600x300 runs from the root to the transition", big.LengthMeters > 9 && big.LengthMeters <= 10.0001,
                    big.LengthMeters.ToString("0.000"));
                transaction.Commit();
            }
        }

        [CommandMethod("DUCTLINEREAL")]
        public void RunDuctLineReal()
        {
            try
            {
                Real();          // HVACNORM first: tags the single lines
                DuctLineReal();
            }
            catch (System.Exception ex)
            {
                failed++;
                Log("FAIL DUCTLINEREAL crashed :: " + ex);
            }
            finally
            {
                Log("SUMMARY passed=" + passed + " failed=" + failed);
            }
        }

        // Per tagged duct system: the root is the free end of its largest duct; plan, write and log.
        private static void DuctLineReal()
        {
            Database db = HostApplicationServices.WorkingDatabase;
            List<DuctLineCurve> all = new List<DuctLineCurve>();
            Dictionary<int, ObjectId> ids = new Dictionary<int, ObjectId>();
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    Entity entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                    if (!(entity is Curve) || entity is Circle || entity is Ellipse)
                        continue;
                    DuctLineCurve curve = DuctLineReader.Read(entity, all.Count);
                    if (curve == null || curve.Size == null || curve.Vertices.Count < 2)
                        continue;
                    ids[curve.Id] = id;
                    all.Add(curve);
                }

                transaction.Commit();
            }

            string templatePath = Environment.GetEnvironmentVariable("HVACNORM_TEMPLATE");
            string error;
            TemplateCatalog catalog = TemplateCatalog.Read(templatePath, out error);
            int networks = 0, runsOk = 0, runsFailed = 0, ducts = 0, fittings = 0, taps = 0, writeErrors = 0;
            Dictionary<string, int> reasons = new Dictionary<string, int>();
            foreach (IGrouping<string, DuctLineCurve> system in all.GroupBy(c => c.Group))
            {
                // Every separate network of the system: root at the free end of its largest duct.
                List<DuctLineCurve> remaining = system.ToList();
                for (int guard = 0; remaining.Count > 0 && guard < 300; guard++)
                {
                    Vec2 root = FreeEndOfLargest(remaining);
                    DuctLinePlan plan = DuctLinePlanner.Plan(remaining, root, new DuctLineSettings());
                    HashSet<int> reached = new HashSet<int>(plan.Runs.SelectMany(r => r.Curves));
                    if (reached.Count == 0)
                        reached.Add(remaining.OrderByDescending(c => c.Size.Width * c.Size.Height).First().Id);
                    remaining = remaining.Where(c => !reached.Contains(c.Id)).ToList();
                    networks++;
                    foreach (DuctLineRun run in plan.Runs)
                    {
                        if (run.Ok)
                        {
                            runsOk++;
                            continue;
                        }

                        runsFailed++;
                        string key = (run.Error ?? "").Split(':')[0];
                        int n;
                        reasons.TryGetValue(key, out n);
                        reasons[key] = n + 1;
                    }

                    if (!plan.Ok)
                        continue;
                    using (TemplateSource template = TemplateSource.TryOpen(templatePath, out error))
                    {
                        DuctLineWriteSummary written = DuctLineWriter.Write(db, plan,
                            run => DuctLinePlanner.LayerForSystem(run.Group) ?? "EIA_1 SUPPLY AIR", template, catalog.Sizes,
                            new DuctLineSettings().Fittings, ids, false);
                        ducts += written.Ducts;
                        fittings += written.Fittings;
                        taps += written.Taps;
                        writeErrors += written.Errors.Count;
                        foreach (string e in written.Errors.Distinct())
                            Log("  write error: " + e);
                    }
                }
            }

            Log(string.Format("ductline total: {0} networks, runs ok {1}, runs that do not fit {2}, ducts {3}, fittings {4}, taps {5}",
                networks, runsOk, runsFailed, ducts, fittings, taps));
            foreach (KeyValuePair<string, int> reason in reasons)
                Log("  " + reason.Value + "x " + reason.Key);
            Check("ductline: most runs drawn", runsOk >= 4 * runsFailed, runsOk + " ok, " + runsFailed + " failed");
            Check("ductline: no write error", writeErrors == 0, writeErrors.ToString());
        }

        private static Vec2 FreeEndOfLargest(List<DuctLineCurve> curves)
        {
            List<Vec2> ends = curves.SelectMany(c => new[]
            {
                new Vec2(c.Vertices[0].X, c.Vertices[0].Y),
                new Vec2(c.Vertices[c.Vertices.Count - 1].X, c.Vertices[c.Vertices.Count - 1].Y)
            }).ToList();
            foreach (DuctLineCurve c in curves.OrderByDescending(c => c.Size.Width * c.Size.Height))
            {
                foreach (OutlineVertex v in new[] { c.Vertices[0], c.Vertices[c.Vertices.Count - 1] })
                {
                    Vec2 p = new Vec2(v.X, v.Y);
                    if (ends.Count(e => e.DistanceTo(p) < 100) == 1)
                        return p;
                }
            }

            return new Vec2(curves[0].Vertices[0].X, curves[0].Vertices[0].Y);
        }

        private static Polyline Poly(params double[] xy)
        {
            Polyline polyline = new Polyline();
            for (int i = 0; i + 1 < xy.Length; i += 2)
                polyline.AddVertexAt(i / 2, new Point2d(xy[i], xy[i + 1]), 0, 0, 0);
            return polyline;
        }

        private static void Tag(Transaction transaction, ObjectId id, double width, double height)
        {
            Entity entity = (Entity)transaction.GetObject(id, OpenMode.ForWrite);
            NormWriter.SetTag(entity, new SizeTag
            {
                Kind = SizeTagKind.Duct, System = "SA", SizeMm = width, Size2Mm = height,
                SizeText = width + "x" + height, Confidence = "high", SourceLayer = entity.Layer
            });
        }

        // On a COPY of a real drawing (never saved): everything in model space, the map proposed by Python,
        // all fixes applied, then the takeoff. Checks that nothing fails and logs the BOQ lines.
        [CommandMethod("NORMREAL")]
        public void RunReal()
        {
            try
            {
                Real();
            }
            catch (System.Exception ex)
            {
                failed++;
                Log("FAIL NORMREAL crashed :: " + ex);
            }
            finally
            {
                Log("SUMMARY passed=" + passed + " failed=" + failed);
            }
        }

        private static void Real()
        {
            Database db = HostApplicationServices.WorkingDatabase;
            List<ObjectId> all = new List<ObjectId>();
            using (Transaction transaction = db.TransactionManager.StartOpenCloseTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId id in space)
                    all.Add(id);
            }

            string folder = NormExchange.NewWorkFolder();
            NormRequest request = new NormRequest();
            Dictionary<string, ObjectId> handles = NormExchange.WriteSnapshot(db, all, Path.Combine(folder, request.Dxf));
            Log("objects " + all.Count + ", snapshot handles " + handles.Count);
            string requestPath = Path.Combine(folder, "request.json");
            NormExchange.WriteRequest(requestPath, request);
            string output;
            RoomsExchange.RunPython(Environment.GetEnvironmentVariable("HVACNORM_PYTHON"),
                Environment.GetEnvironmentVariable("HVACNORM_SCRIPT"), requestPath, 600000, out output);
            string resultPath = Path.Combine(folder, request.Out);
            Check("real: python wrote a result", File.Exists(resultPath), output);
            if (!File.Exists(resultPath))
                return;
            NormResult result = NormExchangeCore.ParseResult(File.ReadAllText(resultPath));
            Check("real: python ok", result.IsOk, result.Message);
            Log("map: " + string.Join(", ", result.Map.Select(p => p.Key + "=" + p.Value.System)));
            int unmapped = result.Entities.Count(e => !e.InBlock && e.Handle != null && !handles.ContainsKey(e.Handle));
            Check("real: every changeable curve maps back", unmapped == 0, unmapped + " unmapped");

            string error;
            using (TemplateSource template = TemplateSource.TryOpen(Environment.GetEnvironmentVariable("HVACNORM_TEMPLATE"), out error))
            {
                HashSet<string> templateLayers = new HashSet<string>(template.GetLayerNames(), StringComparer.OrdinalIgnoreCase);
                NormSummary summary;
                List<EntityPlan> plans = NormPlanner.Plan(result, new NormSettings(),
                    name => HasLayer(db, name) || templateLayers.Contains(name), out summary);
                Log(string.Format("plan: entities={0} moved={1} tagged={2} reset={3} erased={4} splits={5} unsized={6:0.#}m inblock={7}",
                    summary.Entities, summary.Moved, summary.Tagged, summary.Reset, summary.Erased, summary.Splits,
                    summary.UnsizedM, summary.InBlock));
                List<string> missing;
                template.ImportLayers(db, summary.TargetLayers.Where(n => !HasLayer(db, n)), out missing);
                NormWriteSummary written = NormWriter.Write(db, plans, handles);
                Log("written: moved=" + written.Moved + " tagged=" + written.Tagged + " reset=" + written.Reset + " erased="
                    + written.Erased + " split=" + written.Split + " missing=" + written.Missing + " locked=" + written.Locked
                    + " failed=" + written.SplitFailed);
                Check("real: no object missing", written.Missing == 0, written.Missing.ToString());
                Check("real: every cut worked", written.SplitFailed == 0, written.SplitFailed.ToString());
            }

            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                List<TakeoffCurveInput> curves = new List<TakeoffCurveInput>();
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    Curve curve = transaction.GetObject(id, OpenMode.ForRead) as Curve;
                    if (curve == null || curve is Circle || curve is Ellipse)
                        continue;
                    double length;
                    try
                    {
                        length = curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    curves.Add(new TakeoffCurveInput(curve.Layer, length) { Tag = NormWriter.ReadTag(curve) });
                }

                TakeoffResult takeoff = TakeoffAggregator.Aggregate(new TakeoffBlockInput[0], curves, TakeoffOptions.CreateDefault());
                Log("takeoff: tagged curves " + takeoff.TaggedCurveCount);
                foreach (PipeRow row in takeoff.CopperRows.Concat(takeoff.UpvcRows).Concat(takeoff.SteelRows).Concat(takeoff.OtherRows))
                    Log("  pipe  " + row.Description + " = " + row.LengthMeters.ToString("0.00") + " m");
                foreach (DuctSizeRow row in takeoff.DuctSizeRows)
                    Log("  duct  " + row.SizeText + " = " + row.LengthMeters.ToString("0.00") + " m, " + row.AreaSquareMeters.ToString("0.00") + " m2");
                Check("real: the takeoff has rows", takeoff.TaggedCurveCount > 0);
                transaction.Commit();
            }
        }

        private static void Body()
        {
            Database db = HostApplicationServices.WorkingDatabase;
            Dictionary<string, ObjectId> drawn = Draw(db);

            // Snapshot with the handle map.
            string folder = NormExchange.NewWorkFolder();
            NormRequest request = new NormRequest
            {
                Map = NormMapStore.FromSystems("Test", new Dictionary<string, string>
                {
                    { "M-P-WS", "WS" }, { "M-HVAC-CHWS", "CHWS" }, { "M-HVAC-DUCT-SA", "SA" }
                })
            };
            Dictionary<string, ObjectId> handles = NormExchange.WriteSnapshot(db, drawn.Values.ToList(), Path.Combine(folder, request.Dxf));
            Check("every selected object has a snapshot handle", handles.Count == drawn.Count, handles.Count + " of " + drawn.Count);
            Check("handles map back to the drawing", handles.Values.All(id => drawn.Values.Contains(id)));

            // Python, as the command runs it.
            string requestPath = Path.Combine(folder, "request.json");
            NormExchange.WriteRequest(requestPath, request);
            string output;
            int exit = RoomsExchange.RunPython(Environment.GetEnvironmentVariable("HVACNORM_PYTHON"),
                Environment.GetEnvironmentVariable("HVACNORM_SCRIPT"), requestPath, 300000, out output);
            string resultPath = Path.Combine(folder, request.Out);
            Check("python wrote a result", File.Exists(resultPath), "exit " + exit + ": " + output);
            if (!File.Exists(resultPath))
                return;
            NormResult result = NormExchangeCore.ParseResult(File.ReadAllText(resultPath));
            Check("python ok", result.IsOk, result.Message);
            Check("snapshot handles reach python", result.Entities.All(e => e.Handle != null && handles.ContainsKey(e.Handle)),
                string.Join(",", result.Entities.Select(e => e.Handle)));

            // Plan with the template's layers, import, write.
            string error;
            using (TemplateSource template = TemplateSource.TryOpen(Environment.GetEnvironmentVariable("HVACNORM_TEMPLATE"), out error))
            {
                Check("template opens", template != null, error);
                if (template == null)
                    return;
                HashSet<string> templateLayers = new HashSet<string>(template.GetLayerNames(), StringComparer.OrdinalIgnoreCase);
                Check("no EIA_1 CHWS DN50 layer before", !HasLayer(db, "EIA_1 CHWS DN50"));
                NormSummary summary;
                List<EntityPlan> plans = NormPlanner.Plan(result, new NormSettings(),
                    name => HasLayer(db, name) || templateLayers.Contains(name), out summary);
                Check("plan moves the CHWS pipe", summary.TargetLayers.Contains("EIA_1 CHWS DN50"), string.Join(",", summary.TargetLayers));
                List<string> missing;
                template.ImportLayers(db, summary.TargetLayers.Where(n => !HasLayer(db, n)), out missing);
                Check("layer imported from the template", HasLayer(db, "EIA_1 CHWS DN50") && missing.Count == 0, string.Join(",", missing));

                NormWriteSummary written = NormWriter.Write(db, plans, handles);
                Log("written: moved=" + written.Moved + " tagged=" + written.Tagged + " reset=" + written.Reset + " erased="
                    + written.Erased + " split=" + written.Split + " missing=" + written.Missing + " failed=" + written.SplitFailed);
                Check("nothing missing or failed", written.Missing == 0 && written.SplitFailed == 0 && written.Locked == 0);
                Check("two curves cut (trunk, duct)", written.Split == 2, written.Split.ToString());
            }

            Verify(db, drawn);
        }

        private static void Verify(Database db, Dictionary<string, ObjectId> drawn)
        {
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                Check("duplicate erased", drawn["dup"].IsErased);
                Check("trunk replaced by its pieces", drawn["trunk"].IsErased);
                Entity branch = (Entity)transaction.GetObject(drawn["branch"], OpenMode.ForRead);
                Check("override reset to ByLayer", branch.Color.IsByLayer && branch.Linetype == "ByLayer", branch.Color + " " + branch.Linetype);
                SizeTag branchTag = NormWriter.ReadTag(branch);
                Check("branch tagged D25 PPR", branchTag != null && branchTag.SizeText == "D25" && branchTag.Material == "PPR",
                    branchTag == null ? "no tag" : branchTag.SizeText);
                Entity chws = (Entity)transaction.GetObject(drawn["chws"], OpenMode.ForRead);
                Check("CHWS moved to EIA_1 CHWS DN50", chws.Layer == "EIA_1 CHWS DN50", chws.Layer);
                Check("moved pipe has no tag", NormWriter.ReadTag(chws) == null);

                List<TakeoffCurveInput> curves = new List<TakeoffCurveInput>();
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                List<SizeTag> wsTags = new List<SizeTag>();
                foreach (ObjectId id in space)
                {
                    Curve curve = transaction.GetObject(id, OpenMode.ForRead) as Curve;
                    if (curve == null)
                        continue;
                    double length = curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam);
                    SizeTag tag = NormWriter.ReadTag(curve);
                    curves.Add(new TakeoffCurveInput(curve.Layer, length) { Tag = tag });
                    if (tag != null && curve.Layer == "M-P-WS" && curve is Polyline)
                        wsTags.Add(tag);
                }

                Check("trunk pieces tagged D63 and D50", wsTags.Select(t => t.SizeText).OrderBy(s => s).SequenceEqual(new[] { "D50", "D63" }),
                    string.Join(",", wsTags.Select(t => t.SizeText)));

                TakeoffResult takeoff = TakeoffAggregator.Aggregate(new TakeoffBlockInput[0], curves, TakeoffOptions.CreateDefault());
                PipeRow d63 = takeoff.OtherRows.FirstOrDefault(r => r.Description == "WS - Ống PPR PN10 D63");
                Check("BOQ: WS PPR D63 ~10 m", d63 != null && Math.Abs(d63.LengthMeters - 10) < 0.05,
                    string.Join(" | ", takeoff.OtherRows.Select(r => r.Description + " " + r.LengthMeters.ToString("0.00"))));
                Check("BOQ: CHWS DN50 steel row", takeoff.SteelRows.Any(r => r.Description.StartsWith("Ống thép hàn mạ kẽm DN50")),
                    string.Join(" | ", takeoff.SteelRows.Select(r => r.Description)));
                DuctSizeRow big = takeoff.DuctSizeRows.FirstOrDefault(r => r.SizeText == "600 x 300 mm");
                DuctSizeRow small = takeoff.DuctSizeRows.FirstOrDefault(r => r.SizeText == "400 x 300 mm");
                Check("BOQ: duct cut halfway between its labels", big != null && small != null
                    && Math.Abs(big.LengthMeters - 6) < 0.5 && Math.Abs(small.LengthMeters - 6) < 0.5,
                    string.Join(" | ", takeoff.DuctSizeRows.Select(r => r.SizeText + " " + r.LengthMeters.ToString("0.00"))));
                Check("BOQ: nothing left unexported but the labels' layers",
                    takeoff.NotExportedLayers.All(l => l == "0-Note"), string.Join(",", takeoff.NotExportedLayers));
                transaction.Commit();
            }
        }

        // WS tree (trunk with a tee, a branch with a colour override, a line drawn twice), a CHWS pipe, a
        // single-line duct whose size changes inside one line, and their labels.
        private static Dictionary<string, ObjectId> Draw(Database db)
        {
            Dictionary<string, ObjectId> ids = new Dictionary<string, ObjectId>();
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                foreach (string layer in new[] { "M-P-WS", "M-HVAC-CHWS", "M-HVAC-DUCT-SA", "0-Note" })
                    AddLayer(transaction, db, layer);
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                Polyline trunk = new Polyline();
                trunk.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0);
                trunk.AddVertexAt(1, new Point2d(20000, 0), 0, 0, 0);
                ids["trunk"] = Add(transaction, space, trunk, "M-P-WS");
                Line branch = new Line(new Point3d(10000, 0, 0), new Point3d(10000, 8000, 0));
                ids["branch"] = Add(transaction, space, branch, "M-P-WS");
                branch.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
                ids["dup"] = Add(transaction, space, new Line(new Point3d(2000, 0, 0), new Point3d(4000, 0, 0)), "M-P-WS");
                ids["chws"] = Add(transaction, space, new Line(new Point3d(0, 40000, 0), new Point3d(5000, 40000, 0)), "M-HVAC-CHWS");
                ids["duct"] = Add(transaction, space, new Line(new Point3d(0, 20000, 0), new Point3d(12000, 20000, 0)), "M-HVAC-DUCT-SA");
                ids["t63"] = Text(transaction, space, "WS-PPR-DN63", 3000, 150);
                ids["t50"] = Text(transaction, space, "WS-PPR-DN50", 15000, 150);
                ids["t25"] = Text(transaction, space, "WS-PPR-DN25", 10150, 3000);
                ids["tchws"] = Text(transaction, space, "CHWS DN50", 1000, 40150);
                ids["tsa1"] = Text(transaction, space, "SAD-600x300", 1500, 20150);
                ids["tsa2"] = Text(transaction, space, "SAD-400x300", 9000, 20150);
                transaction.Commit();
            }

            return ids;
        }

        private static ObjectId Add(Transaction transaction, BlockTableRecord space, Entity entity, string layer)
        {
            entity.SetDatabaseDefaults(space.Database);
            entity.Layer = layer;
            ObjectId id = space.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
            return id;
        }

        private static ObjectId Text(Transaction transaction, BlockTableRecord space, string text, double x, double y)
        {
            DBText label = new DBText { TextString = text, Position = new Point3d(x, y, 0), Height = 250 };
            return Add(transaction, space, label, "0-Note");
        }

        private static void AddLayer(Transaction transaction, Database db, string name)
        {
            LayerTable table = (LayerTable)transaction.GetObject(db.LayerTableId, OpenMode.ForWrite);
            if (table.Has(name))
                return;
            LayerTableRecord record = new LayerTableRecord { Name = name };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static bool HasLayer(Database db, string name)
        {
            using (Transaction transaction = db.TransactionManager.StartOpenCloseTransaction())
            {
                LayerTable table = (LayerTable)transaction.GetObject(db.LayerTableId, OpenMode.ForRead);
                return table.Has(name);
            }
        }
    }
}
