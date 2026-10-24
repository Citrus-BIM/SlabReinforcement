using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using SlabReinforcement.Core;
using SlabReinforcement.Integration;

namespace SlabReinforcement.RevitSmoke
{
    // Opt-in, temporary test application. Opens only the configured workspace copy.
    // Test mutations roll back; a successful demonstration is saved only as a separate copy.
    public sealed class SmokeApplication : IExternalApplication
    {
        private static readonly string AssemblyDirectory = Path.GetDirectoryName(typeof(SmokeApplication).Assembly.Location);
        private static readonly string RunDirectory = Path.GetFullPath(Path.Combine(AssemblyDirectory, "..", "..", "..", "..", "artifacts", "revit-smoke"));
        private readonly List<object> _cases = new List<object>();
        private readonly Dictionary<string, object> _report = new Dictionary<string, object>();
        private readonly Dictionary<string, Tuple<int, int>> _baselineMvpCounts = new Dictionary<string, Tuple<int, int>>();

        public Result OnStartup(UIControlledApplication application)
        {
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            Directory.CreateDirectory(RunDirectory);
            Log("STARTUP " + application.ControlledApplication.VersionNumber + " " + application.ControlledApplication.VersionBuild);
            _report["revitVersion"] = application.ControlledApplication.VersionNumber;
            _report["revitBuild"] = application.ControlledApplication.VersionBuild;
            _report["startedUtc"] = DateTime.UtcNow.ToString("O");
            application.DialogBoxShowing += OnDialog;
            application.Idling += OnIdling;
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.Idling -= OnIdling;
            application.DialogBoxShowing -= OnDialog;
            Log("SHUTDOWN");
            return Result.Succeeded;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            if (name != "SlabReinforcement.Core" && name != "SlabReinforcement.Dxf" && name != "System.Text.Encoding.CodePages" && name != "NetTopologySuite" && name != "System.Memory" && name != "System.Buffers" && name != "System.Numerics.Vectors" && name != "System.Runtime.CompilerServices.Unsafe") return null;
            string path = Path.Combine(AssemblyDirectory, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }

        private void OnDialog(object sender, DialogBoxShowingEventArgs args)
        {
            string message = args is TaskDialogShowingEventArgs task ? task.Message : "";
            Log("DIALOG " + args.DialogId + " " + message);
            // No blanket acceptance of unexpected dialogs. Their exact identifiers are logged.
        }

        private void OnIdling(object sender, IdlingEventArgs args)
        {
            var app = (UIApplication)sender;
            app.Idling -= OnIdling;
            Document document = null;
            bool renderOnly = File.Exists(Path.Combine(RunDirectory, "render-only.flag"));
            try
            {
                string input = Path.Combine(RunDirectory, renderOnly ? "MVP-demo.rvt" : "input.rvt");
                if (!File.Exists(input)) throw new FileNotFoundException("Safe test copy is missing.", input);
                Log("OPEN " + input);
                document = app.Application.OpenDocumentFile(input);
                _report["document"] = document.PathName;
                if (renderOnly) RenderDemo(document);
                else Run(document);
                _report["status"] = "COMPLETED";
            }
            catch (Exception error)
            {
                _report["status"] = "FAILED";
                _report["exception"] = error.ToString();
                Log("FAILED " + error);
            }
            finally
            {
                if (document != null && document.IsValidObject)
                {
                    try { document.Close(false); Log("CLOSED_WITHOUT_SAVE"); }
                    catch (Exception error) { Log("CLOSE_ERROR " + error); }
                }
                _report["cases"] = _cases;
                _report["allRequiredChecksPassed"] = !_cases.Any(c => new[] { "FAILED", "BLOCKED", "SKIPPED" }.Contains(c.GetType().GetProperty("status")?.GetValue(c) as string)) && _report["status"].ToString() == "COMPLETED";
                _report["finishedUtc"] = DateTime.UtcNow.ToString("O");
                File.WriteAllText(Path.Combine(RunDirectory, renderOnly ? "render-results.json" : "results.json"), new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(_report));
                File.WriteAllText(Path.Combine(RunDirectory, renderOnly ? "render-completed.flag" : "completed.flag"), _report["status"].ToString());
                if (renderOnly) File.Delete(Path.Combine(RunDirectory, "render-only.flag"));
                Log("DONE " + _report["status"]);
                app.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit));
            }
        }

        private void Run(Document doc)
        {
            var floors = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().ToList();
            var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
            var types = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToList();
            _report["inventory"] = new
            {
                floorCount = floors.Count, importCount = imports.Count, existingRebarSets = CountRebar(doc),
                floors = floors.Select(f => new { id = f.Id.IntegerValue, f.Name, areaM2 = (f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0) * 0.09290304 }).ToList(),
                imports = imports.Select(i => new { id = i.Id.IntegerValue, i.Name, linked = i.IsLinked }).ToList(),
                types = types.Select(t => new { id = t.Id.IntegerValue, t.Name, nominalMm = t.BarNominalDiameter * 304.8, modelMm = t.BarModelDiameter * 304.8, grade = ReadGrade(t) }).ToList()
            };
            WriteProgress();
            Log("INVENTORY floors=" + floors.Count + " imports=" + imports.Count + " types=" + types.Count);
            if (floors.Count == 0 || types.Count == 0) throw new InvalidOperationException("No floor or rebar types in copied model.");
            SlabGeometrySnapshot slab = null;
            foreach (var floor in floors.OrderByDescending(f => f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0))
            {
                try { slab = SlabGeometryReader.Read(doc, floor, FindEdge(floor)); break; }
                catch (Exception error) { _cases.Add(new { name = "read-floor", floor = floor.Id.IntegerValue, status = "REJECTED", message = error.Message }); }
            }
            if (slab == null) throw new InvalidOperationException("No supported slab geometry in model.");
            _report["slab"] = new { id = slab.Host.Id.IntegerValue, slab.ThicknessMm, slab.TopCoverMm, slab.BottomCoverMm, slab.SideCoverMm, slab.AxisDescription, shellCount = slab.Slabs.Count, holeCount = slab.Slabs.Sum(p => p.Holes.Count) };
            _report["baselineFrame"] = FrameInfo(slab);
            var type = types.OrderBy(t => Math.Abs(t.BarNominalDiameter * 304.8 - 12)).ThenBy(t => ReadGrade(t) == "500" ? 0 : 1).First();
            try
            {
                var runtime = ProductRuntimeProbe.Run(RunDirectory, slab, types);
                _cases.Add(new { name = "product-dll-wpf-runtime", status = "PASS", runtime });
                Log("PRODUCT_DLL_WPF_PASS");
            }
            catch (Exception error)
            {
                _cases.Add(new { name = "product-dll-wpf-runtime", status = "FAILED", message = error.ToString() });
                Log("PRODUCT_DLL_WPF_FAILED " + error);
            }
            WriteProgress();
            foreach (var import in imports)
            {
                try
                {
                    var mesh = ImportedMeshReader.Read(doc, import, slab.Frame);
                    _cases.Add(new { name = "read-cad", id = import.Id.IntegerValue, status = "PASS", groups = mesh.Groups.Select(g => new { g.Key, g.Layer, g.ElevationMm, faces = g.Faces.Count, colors = g.Faces.Select(f => f.ColorKey).Distinct().Count() }).ToList(), mesh.Warnings });
                }
                catch (Exception error) { _cases.Add(new { name = "read-cad", id = import.Id.IntegerValue, status = "REJECTED", message = error.ToString(), geometry = InspectImport(doc, import) }); }
                try
                {
                    string source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Армирование плит", "2025-064 М8 ПК1 верхняя вдоль оси Y_Очищена.dxf");
                    var mesh = DxfMeshReader.Read(doc, import, slab.Frame, source, 1);
                    _cases.Add(new { name = "dxf-fallback-cleaned", status = "PASS", scaleBasis = "Confirmed by stored import type: millimetres; geometric match is mandatory", groups = mesh.Groups.Select(g=>new {g.Key,g.Layer,g.ElevationMm,faces=g.Faces.Count}).ToList(),mesh.Warnings});
                    RunRealDxfZone(doc, slab, mesh, types, type);
                }
                catch(Exception error) { _cases.Add(new { name = "dxf-fallback-cleaned", status = "FAILED", message=error.ToString() }); }
                WriteProgress();
            }
            foreach (var direction in new[] { "НизX", "НизY", "ВерхX", "ВерхY" })
                RunStraightCase(doc, slab, types, type, direction);
            RunMvpCases(doc, slab, types, type, "");
            RunVariation(doc, slab, types, type, "rotate37", floor =>
                ElementTransformUtils.RotateElement(doc, floor.Id, Line.CreateUnbound(XYZ.Zero, XYZ.BasisZ), 37 * Math.PI / 180));
            RunVariation(doc, slab, types, type, "hole2000", floor =>
            {
                var points = slab.Slabs.SelectMany(p => p.Shell).ToList();
                double x = (points.Min(p => p.X) + points.Max(p => p.X)) / 2;
                double y = (points.Min(p => p.Y) + points.Max(p => p.Y)) / 2;
                var corners = new[] { new Point3(x - 1000, y - 1000, 0), new Point3(x + 1000, y - 1000, 0), new Point3(x + 1000, y + 1000, 0), new Point3(x - 1000, y + 1000, 0) }.Select(p => SlabGeometryReader.ToXyz(slab.Frame.ToWorld(p))).ToArray();
                var opening = new CurveArray();
                for (int i = 0; i < 4; i++) opening.Append(Line.CreateBound(corners[i], corners[(i + 1) % 4]));
                doc.Create.NewOpening(floor, opening, true);
            });
            RunDxfImport(doc, slab);
            RunSessionSemantics(doc, slab, types, type);
            RunAdjacentEquivalentZones(doc, slab, types, type);
            try
            {
                _cases.Add(new { name = "existing-adjusted-stirrup", status = "PASS", result = ExistingStirrupProbe.Run(doc, slab, type) });
            }
            catch (Exception error) { _cases.Add(new { name = "existing-adjusted-stirrup", status = "FAILED", message = error.ToString() }); }
            WriteProgress();
            if (!_cases.Any(c => new[] { "FAILED", "BLOCKED" }.Contains(c.GetType().GetProperty("status")?.GetValue(c) as string)))
                SaveDemo(doc, slab, types, type);
        }

        private void RunAdjacentEquivalentZones(Document doc, SlabGeometrySnapshot slab, List<RebarBarType> types, RebarBarType type)
        {
            try
            {
                var shell = slab.Slabs.SelectMany(p => p.Shell).ToList();
                double x = (shell.Min(p => p.X) + shell.Max(p => p.X)) / 2;
                double y = (shell.Min(p => p.Y) + shell.Max(p => p.Y)) / 2;
                Func<string, double, double, FaceInput> face = (color, left, right) => new FaceInput
                {
                    Id = color, ColorKey = color,
                    Vertices = new List<Point2> { new Point2(left, y - 1500), new Point2(right, y - 1500), new Point2(right, y + 1500), new Point2(left, y + 1500) }
                };
                var comparison = new List<ReinforcementPlan>();
                var created = new List<int>();
                foreach (bool split in new[] { false, true })
                {
                    var source = new ImportedMeshSnapshot { Groups = new List<CadPlaneGroup>
                    { new CadPlaneGroup { Key = "ADJACENT_EQUIVALENT", Layer = "SYNTHETIC_TEST_ONLY", Faces = split ? new List<FaceInput> { face("RED", x - 3000, x), face("BLUE", x, x + 3000) } : new List<FaceInput> { face("RED", x - 3000, x + 3000) } } } };
                    var p = new PreviewParameters
                    {
                        IncludeZones = true, CalculationAxesConfirmed = true, DemandMeaning = "Manual", Direction = "Низ X", ConcreteClass = "B25",
                        SourceGroupKey = "ADJACENT_EQUIVALENT", EdgeCoverMm = slab.SideCoverMm, RoundIncrementMm = 1,
                        Selections = (split ? new[] { "RED", "BLUE" } : new[] { "RED" }).Select(color => new PreviewSelection { ColorKey = color, SpecKey = type.UniqueId, SpacingMm = 200 }).ToList()
                    };
                    var session = new ReinforcementSession(slab, source, types);
                    var plan = session.CreateContext().Calculate(p, System.Threading.CancellationToken.None);
                    if (!plan.CanCreate) throw new InvalidOperationException("Equivalent-color fixture was blocked: " + string.Join("; ", plan.Diagnostics.Select(d => d.Code + ": " + d.Message)));
                    comparison.Add(plan);
                    int before = CountRebar(doc);
                    using (var scope = new TransactionGroup(doc, "Temporary equivalent-color zones"))
                    {
                        scope.Start();
                        try { created.Add(RebarPlanWriter.Create(doc, slab, session, plan, p)); }
                        finally { scope.RollBack(); }
                    }
                    if (CountRebar(doc) != before) throw new InvalidOperationException("Equivalent-color fixture rollback changed the rebar count.");
                }
                if (comparison[0].Bars.Count != comparison[1].Bars.Count || comparison[0].Groups.Count != comparison[1].Groups.Count || created[0] != created[1])
                    throw new InvalidOperationException("Adjacent equivalent colors produced a different bar/group count.");
                Func<ReinforcementPlan, List<PlannedBar>> order = plan => plan.Bars.OrderBy(b => b.Start.X).ThenBy(b => b.Start.Y).ThenBy(b => b.End.X).ThenBy(b => b.End.Y).ToList();
                var a = order(comparison[0]); var b = order(comparison[1]);
                for (int i = 0; i < a.Count; i++)
                    if (new[] { a[i].Start.X - b[i].Start.X, a[i].Start.Y - b[i].Start.Y, a[i].End.X - b[i].End.X, a[i].End.Y - b[i].End.Y, a[i].Placement.ElevationMm - b[i].Placement.ElevationMm }.Any(delta => Math.Abs(delta) > 1e-6))
                        throw new InvalidOperationException("Adjacent equivalent colors changed bar geometry.");
                _cases.Add(new { name = "adjacent-equivalent-zone-colors", status = "PASS", singleColorBars = a.Count, splitColorBars = b.Count, sets = created[1], geometryMatches = true, actualWrites = 2, purpose = "SYNTHETIC MANUAL SAME TYPE/PITCH; REAL SESSION AND REVIT WRITER" });
            }
            catch (Exception error) { _cases.Add(new { name = "adjacent-equivalent-zone-colors", status = "FAILED", message = error.ToString() }); }
            WriteProgress();
        }

        private void RunRealDxfZone(Document doc, SlabGeometrySnapshot slab, ImportedMeshSnapshot mesh, List<RebarBarType> types, RebarBarType type)
        {
            var group = mesh.Groups.OrderByDescending(g => g.Faces.Count).First();
            var colors = group.Faces.GroupBy(f => f.ColorKey).ToList();
            string color = colors.Any(g => g.Key == "RGB(0, 255, 0)") ? "RGB(0, 255, 0)" : colors.OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
            var parameters = new PreviewParameters
            {
                IncludeZones = true, CalculationAxesConfirmed = true, DemandMeaning = "Manual", Direction = "Верх Y", ConcreteClass = "B25",
                SourceGroupKey = group.Key, EdgeCoverMm = slab.SideCoverMm, RoundIncrementMm = 1,
                Selections = new List<PreviewSelection> { new PreviewSelection { ColorKey = color, SpecKey = type.UniqueId, SpacingMm = 200 } }
            };
            var session = new ReinforcementSession(slab, mesh, types);
            var plan = session.CreateContext().Calculate(parameters, System.Threading.CancellationToken.None);
            if (!plan.CanCreate)
            {
                _cases.Add(new { name = "real-dxf-manual-zone", status = "REJECTED", purpose = "FIXED TEST PARAMETERS — NOT LEGEND INTERPRETATION", group = group.Key, color, sourceFaces = group.Faces.Count(f => f.ColorKey == color), plan.Diagnostics });
                WriteProgress();
                return;
            }
            int before = CountRebar(doc);
            using (var scope = new TransactionGroup(doc, "Temporary real DXF zone test"))
            {
                scope.Start();
                try
                {
                    int sets = RebarPlanWriter.Create(doc, slab, session, plan, parameters);
                    _cases.Add(new { name = "real-dxf-manual-zone", status = "PASS", purpose = "FIXED TEST PARAMETERS — NOT LEGEND INTERPRETATION", group = group.Key, color, sourceFaces = group.Faces.Count(f => f.ColorKey == color), bars = plan.Bars.Count, sets, plan.Diagnostics });
                }
                catch (Exception error) { _cases.Add(new { name = "real-dxf-manual-zone", status = "FAILED", message = error.ToString(), plan.Diagnostics }); }
                finally { scope.RollBack(); }
            }
            if (CountRebar(doc) != before) throw new InvalidOperationException("Real DXF test rollback failed.");
            WriteProgress();
        }

        private void RunSessionSemantics(Document doc, SlabGeometrySnapshot slab, List<RebarBarType> types, RebarBarType type)
        {
            var mesh = new ImportedMeshSnapshot { Groups = new List<CadPlaneGroup> { new CadPlaneGroup { Key = "SYNTHETIC", Layer = "SYNTHETIC_TEST_ONLY", Faces = InteriorDemand(slab.Slabs) } } };
            Func<PreviewParameters> parameters = () => new PreviewParameters
            {
                IncludeZones = true, CalculationAxesConfirmed = true, DemandMeaning = "TotalArea", Direction = "Низ X", ConcreteClass = "B25", SourceGroupKey = "SYNTHETIC", EdgeCoverMm = slab.SideCoverMm, LayerOffsetMm = 0, RoundIncrementMm = 1,
                Selections = new List<PreviewSelection> { new PreviewSelection { ColorKey = "SMOKE", SpecKey = type.UniqueId, SpacingMm = 200, RequiredAreaMm2PerM = 700 } },
                BackgroundLayers = new List<PreviewBackgroundLayer> { new PreviewBackgroundLayer { Enabled = true, Direction = "Низ X", SpecKey = type.UniqueId, SpacingMm = 200 } }
            };
            foreach (var name in new[] { "total-as", "zero-total-as", "insufficient-as", "axes-unconfirmed", "offset-total-as" })
            {
                try
                {
                    var p = parameters();
                    if (name == "zero-total-as") p.Selections[0].RequiredAreaMm2PerM = 0;
                    if (name == "insufficient-as") p.Selections[0].RequiredAreaMm2PerM = 2000;
                    if (name == "axes-unconfirmed") p.CalculationAxesConfirmed = false;
                    if (name == "offset-total-as") p.LayerOffsetMm = 12;
                    var session = new ReinforcementSession(slab, mesh, types);
                    var plan = session.CreateContext().Calculate(p, System.Threading.CancellationToken.None);
                    bool expected = name == "total-as" || name == "zero-total-as" ? plan.CanCreate : !plan.CanCreate && plan.Diagnostics.Any(d=>d.IsBlocking && d.Code == (name == "axes-unconfirmed" ? "CALCULATION_AXES" : "SPECIFICATION"));
                    if (!expected) throw new InvalidOperationException("Unexpected semantic result: " + string.Join("; ", plan.Diagnostics.Select(d=>d.Code+": "+d.Message)));
                    int sets = 0;
                    if (plan.CanCreate)
                        using (var scope = new TransactionGroup(doc, "Temporary full As test"))
                        { scope.Start();try {sets=RebarPlanWriter.Create(doc,slab,session,plan,p);}finally{scope.RollBack();} }
                    _cases.Add(new { name="semantics-"+name,status="PASS",canCreate=plan.CanCreate,sets,plan.Diagnostics });
                }
                catch(Exception error){_cases.Add(new{name="semantics-"+name,status="FAILED",message=error.ToString()});}
                WriteProgress();
            }
            using (var scope = new TransactionGroup(doc, "Temporary existing-rebar guard test"))
            {
                scope.Start();
                try
                {
                    var p=parameters();p.IncludeZones=false;
                    var session=new ReinforcementSession(slab,mesh,types);
                    var plan=session.CreateContext().Calculate(p,System.Threading.CancellationToken.None);
                    RebarPlanWriter.Create(doc,slab,session,plan,p);
                    var renewed=new ReinforcementSession(slab,mesh,types);
                    var second=renewed.CreateContext().Calculate(p,System.Threading.CancellationToken.None);
                    if(second.CanCreate || !second.Diagnostics.Any(d=>d.IsBlocking&&d.Code=="COMPOSITE_BAR_COLLISION"))throw new InvalidOperationException("Existing reinforcement was not detected.");
                    _cases.Add(new{name="existing-model-repeat-guard",status="PASS",existingSets=CountRebar(doc),second.Diagnostics});
                }
                catch(Exception error){_cases.Add(new{name="existing-model-repeat-guard",status="FAILED",message=error.ToString()});}
                finally{scope.RollBack();}
            }
            WriteProgress();
        }

        private void SaveDemo(Document doc, SlabGeometrySnapshot slab, List<RebarBarType> types, RebarBarType type)
        {
            var p = new PreviewParameters
            {
                IncludeZones=false,EdgeCoverMm=slab.SideCoverMm,Direction="Низ X",ConcreteClass="B25",
                BackgroundLayers=new[]{"Низ X","Низ Y","Верх X","Верх Y"}.Select(d=>new PreviewBackgroundLayer{Enabled=true,Direction=d,SpecKey=type.UniqueId,SpacingMm=200}).ToList(),
                Perimeter=new PreviewPerimeterOptions{Enabled=true,Outer=true,Openings=true,UseUbars=true,UseLongitudinalBars=false,SpecKey=type.UniqueId,SpacingMm=200,LegLengthMm=500}
            };
            var session=new ReinforcementSession(slab,new ImportedMeshSnapshot(),types);
            var plan=session.CreateContext().Calculate(p,System.Threading.CancellationToken.None);
            if(!plan.CanCreate)throw new InvalidOperationException("Final demonstration plan is not valid.");
            AssertUOnlyPerimeter(plan, true);
            int sets=RebarPlanWriter.Create(doc,slab,session,plan,p);
            View3D view;
            using(var transaction=new Transaction(doc,"MVP demonstration view — test parameters"))
            {
                transaction.Start();
                var type3d=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t=>t.ViewFamily==ViewFamily.ThreeDimensional);
                view=View3D.CreateIsometric(doc,type3d.Id);view.Name="MVP — ТЕСТОВЫЕ параметры — не рабочее армирование";view.DetailLevel=ViewDetailLevel.Fine;view.DisplayStyle=DisplayStyle.Shading;
                var bars=new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().ToList();
                var visible=bars.Select(b=>b.Id).Concat(new[]{slab.Host.Id}).ToList();view.IsolateElementsTemporary(visible);view.ConvertTemporaryHideIsolateToPermanent();
                foreach(var bar in bars)
                {
                    bar.SetUnobscuredInView(view,true);
                    view.SetElementOverrides(bar.Id,new OverrideGraphicSettings().SetProjectionLineColor(new Color(219,100,48)).SetProjectionLineWeight(2));
                }
                view.SetElementOverrides(slab.Host.Id,new OverrideGraphicSettings().SetSurfaceTransparency(85));
                var box=slab.Host.get_BoundingBox(null);var centre=(box.Min+box.Max)/2;double length=box.Max.DistanceTo(box.Min);var eye=centre+new XYZ(length,-length,length*0.75);var forward=(centre-eye).Normalize();var right=forward.CrossProduct(XYZ.BasisZ).Normalize();var up=right.CrossProduct(forward).Normalize();
                view.SetOrientation(new ViewOrientation3D(eye,up,forward));
                var section=new BoundingBoxXYZ{Min=box.Min-new XYZ(1,1,1),Max=box.Max+new XYZ(1,1,1)};view.SetSectionBox(section);view.IsSectionBoxActive=true;
                transaction.Commit();
            }
            string model=Path.Combine(RunDirectory,"MVP-demo.rvt");
            doc.SaveAs(model,new SaveAsOptions{OverwriteExistingFile=true,MaximumBackups=1});
            RenderDemo(doc);
            _cases.Add(new{name="saved-demo",status="PASS",model,purpose="TEST PARAMETERS ONLY — NOT A STRUCTURAL DESIGN",sets,bars=plan.Bars.Count,viewId=view.Id.IntegerValue});
            WriteProgress();
        }

        private void RenderDemo(Document doc)
        {
            var floor = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().Single();
            var bars = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().ToList();
            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
            var box = floor.get_BoundingBox(null);
            var views = new List<ElementId>();
            foreach (bool detail in new[] { false, true })
            {
                using (var transaction = new Transaction(doc, "MVP test model export"))
                {
                    transaction.Start();
                    string name = detail ? "MVP угол — тестовые параметры" : "MVP общий — тестовые параметры";
                    var view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v => v.Name == name) ?? View3D.CreateIsometric(doc, type.Id);
                    view.Name = name;
                    view.DetailLevel = ViewDetailLevel.Fine;
                    view.DisplayStyle = detail ? DisplayStyle.Shading : DisplayStyle.HLR;
                    view.IsolateElementsTemporary(bars.Select(b => b.Id).Concat(new[] { floor.Id }).ToList());
                    view.ConvertTemporaryHideIsolateToPermanent();
                    foreach (var bar in bars)
                    {
                        bar.SetUnobscuredInView(view, true);
                        view.SetElementOverrides(bar.Id, new OverrideGraphicSettings().SetProjectionLineColor(new Color(190, 80, 35)).SetProjectionLineWeight(1));
                    }
                    view.SetElementOverrides(floor.Id, new OverrideGraphicSettings().SetSurfaceTransparency(95)
                        .SetSurfaceForegroundPatternVisible(false).SetSurfaceBackgroundPatternVisible(false)
                        .SetCutForegroundPatternVisible(false).SetCutBackgroundPatternVisible(false));
                    var min = box.Min - new XYZ(0.1, 0.1, 0.5);
                    var max = detail ? new XYZ(box.Min.X + 3000 / 304.8, box.Min.Y + 3000 / 304.8, box.Max.Z + 0.5) : box.Max + new XYZ(0.1, 0.1, 0.5);
                    view.SetSectionBox(new BoundingBoxXYZ { Min = min, Max = max });
                    view.IsSectionBoxActive = true;
                    var centre = (min + max) / 2;
                    double length = max.DistanceTo(min);
                    var eye = centre + new XYZ(-length, -length, length * 0.8);
                    var forward = (centre - eye).Normalize();
                    var right = forward.CrossProduct(XYZ.BasisZ).Normalize();
                    view.SetOrientation(new ViewOrientation3D(eye, right.CrossProduct(forward).Normalize(), forward));
                    views.Add(view.Id);
                    transaction.Commit();
                }
            }
            doc.Save();
            foreach (var id in views)
            {
                string name = id.Equals(views[0]) ? "MVP-overview" : "MVP-corner";
                var export = new ImageExportOptions { FilePath = Path.Combine(RunDirectory, name), ExportRange = ExportRange.SetOfViews, ZoomType = ZoomFitType.FitToPage, PixelSize = 2400, ImageResolution = ImageResolution.DPI_150, HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG };
                export.SetViewsAndSheets(new List<ElementId> { id });
                doc.ExportImage(export);
            }
            _report["model"] = doc.PathName;
            _report["rebarSets"] = bars.Count;
            _report["purpose"] = "TEST PARAMETERS ONLY — NOT A STRUCTURAL DESIGN";
            _report["viewIds"] = views.Select(id => id.IntegerValue).ToList();
        }

        private void RunVariation(Document doc, SlabGeometrySnapshot original, List<RebarBarType> types, RebarBarType type, string name, Action<Floor> mutation)
        {
            using (var scope = new TransactionGroup(doc, "Temporary smoke geometry: " + name))
            {
                var stableEdge = FindEdge(original.Host).ConvertToStableRepresentation(doc);
                scope.Start();
                try
                {
                    using (var transaction = new Transaction(doc, "Temporary slab variation"))
                    {
                        transaction.Start(); mutation(original.Host);
                        if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Could not create temporary slab variation.");
                    }
                    var varied = SlabGeometryReader.Read(doc, original.Host, Reference.ParseFromStableRepresentation(doc, stableEdge));
                    _cases.Add(new { name = name + "-geometry", status = "PASS", varied.AxisDescription, holes = varied.Slabs.Sum(p => p.Holes.Count), frame = FrameInfo(varied), stableEdge });
                    foreach (var direction in new[] { "НизX", "НизY", "ВерхX", "ВерхY" }) RunStraightCase(doc, varied, types, type, direction, name + "-");
                    RunMvpCases(doc, varied, types, type, name + "-");
                }
                catch (Exception error) { _cases.Add(new { name = name + "-geometry", status = "FAILED", message = error.ToString() }); }
                finally { scope.RollBack(); }
            }
            WriteProgress();
        }

        private void RunMvpCases(Document doc, SlabGeometrySnapshot slab, List<RebarBarType> types, RebarBarType type, string prefix)
        {
            foreach (var variant in new[] { "background", "perimeter-u", "background-perimeter" })
            {
                var parameters = new PreviewParameters
                {
                    IncludeZones = false, Direction = "НизX", ConcreteClass = "B25", EdgeCoverMm = slab.SideCoverMm,
                    Perimeter = new PreviewPerimeterOptions { Enabled = variant != "background", UseUbars = true, UseLongitudinalBars = false, Outer = true, Openings = true, SpecKey = type.UniqueId, SpacingMm = 200, LegLengthMm = 500 },
                    BackgroundLayers = variant.StartsWith("background") ? new[] { "Низ X", "Низ Y", "Верх X", "Верх Y" }.Select(direction => new PreviewBackgroundLayer { Enabled = true, Direction = direction, SpecKey = type.UniqueId, SpacingMm = 200 }).ToList() : new List<PreviewBackgroundLayer>()
                };
                if (variant == "background-perimeter")
                {
                    // Previously persisted flags must not re-enable the retired MVP option.
                    parameters.Perimeter.UseUbars = false;
                    parameters.Perimeter.UseLongitudinalBars = true;
                }
                var session = new ReinforcementSession(slab, new ImportedMeshSnapshot(), types);
                var plan = session.CreateContext().Calculate(parameters, System.Threading.CancellationToken.None);
                string name = prefix + "mvp-" + variant;
                Log("MVP_PLAN " + name + " bars=" + plan.Bars.Count + " groups=" + plan.Groups.Count + " valid=" + plan.CanCreate);
                if (prefix.Length == 0) _baselineMvpCounts[variant] = Tuple.Create(plan.Bars.Count, plan.Groups.Count);
                if (prefix == "rotate37-" && _baselineMvpCounts.TryGetValue(variant, out var expectedCounts) &&
                    (plan.Bars.Count != expectedCounts.Item1 || plan.Groups.Count != expectedCounts.Item2))
                {
                    _cases.Add(new { name = name + "-rotation-invariance", status = "FAILED", expectedBars = expectedCounts.Item1, expectedGroups = expectedCounts.Item2, actualBars = plan.Bars.Count, actualGroups = plan.Groups.Count });
                }
                if (!plan.CanCreate)
                {
                    _cases.Add(new { name, status = "BLOCKED", plan.Diagnostics }); WriteProgress(); continue;
                }
                int before = CountRebar(doc);
                using (var scope = new TransactionGroup(doc, "Temporary MVP smoke: rollback"))
                {
                    scope.Start();
                    try
                    {
                        AssertUOnlyPerimeter(plan, variant != "background");
                        int sets = RebarPlanWriter.Create(doc, slab, session, plan, parameters);
                        if (CountRebar(doc) - before != sets) throw new InvalidOperationException("Created MVP set count mismatch.");
                        _cases.Add(new { name, status = "PASS", bars = plan.Bars.Count, sets, legacyPerimeterFlagsIgnored = variant == "background-perimeter", components = plan.Bars.GroupBy(b => b.Placement.Component).Select(g => new { component = g.Key, bars = g.Count() }).ToList(), plan.Diagnostics });
                        Log("MVP_PASS " + name + " sets=" + sets);
                    }
                    catch (Exception error)
                    {
                        _cases.Add(new { name, status = "FAILED", message = error.ToString(), plan.Diagnostics }); Log("MVP_FAILED " + name + " " + error);
                    }
                    finally { scope.RollBack(); }
                }
                if (CountRebar(doc) != before) throw new InvalidOperationException("MVP rollback count mismatch.");
                WriteProgress();
            }
        }

        private static void AssertUOnlyPerimeter(ReinforcementPlan plan, bool requireUbars)
        {
            if (plan.Bars.Any(b => b.Placement?.Component == "PerimeterLongitudinal") ||
                plan.Groups.Any(g => g.Placement?.Component == "PerimeterLongitudinal"))
                throw new InvalidOperationException("The current MVP must not create longitudinal perimeter bars.");
            if (requireUbars && !plan.Bars.Any(b => b.Placement?.Component == "PerimeterU"))
                throw new InvalidOperationException("The current MVP requires U-shaped perimeter bars.");
        }

        private void RunDxfImport(Document doc, SlabGeometrySnapshot slab)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Армирование плит", "1 Слой.dxf");
            if (!File.Exists(path)) { _cases.Add(new { name = "import-metre-dxf", status = "SKIPPED", reason = "File not found" }); return; }
            using (var scope = new TransactionGroup(doc, "Temporary source import: rollback"))
            {
                scope.Start();
                try
                {
                    ElementId id;
                    using (var transaction = new Transaction(doc, "Import DXF in confirmed metres"))
                    using (var options = new DWGImportOptions { Unit = ImportUnit.Meter, ColorMode = ImportColorMode.Preserved, Placement = ImportPlacement.Origin, ThisViewOnly = false })
                    {
                        transaction.Start();
                        var view = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v => !v.IsTemplate && (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.EngineeringPlan));
                        Log("DXF_IMPORT_BEGIN " + path);
                        if (!doc.Import(path, options, view, out id)) throw new InvalidOperationException("Revit returned false for DXF import.");
                        if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("DXF import did not commit.");
                    }
                    var import = (ImportInstance)doc.GetElement(id);
                    var metadata = InspectImport(doc, import);
                    try
                    {
                        var mesh = ImportedMeshReader.Read(doc, import, slab.Frame);
                        _cases.Add(new { name = "import-metre-dxf", status = "PASS", unit = "metres", groups = mesh.Groups.Select(g => new { g.Key, g.Layer, g.ElevationMm, faces = g.Faces.Count }).ToList(), metadata });
                    }
                    catch (Exception error) { _cases.Add(new { name = "import-metre-dxf", status = "REJECTED", message = error.ToString(), metadata }); }
                    try
                    {
                        var mesh=DxfMeshReader.Read(doc,import,slab.Frame,path,1000);
                        _cases.Add(new {name="dxf-fallback-metres",status="PASS",groups=mesh.Groups.Select(g=>new{g.Key,g.Layer,g.ElevationMm,faces=g.Faces.Count}).ToList(),mesh.Warnings});
                        using(var transaction=new Transaction(doc,"Temporary CAD transform"))
                        {transaction.Start();transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(new SmokeFailures()).SetClearAfterRollback(true));import.Pinned=false;ElementTransformUtils.RotateElement(doc,import.Id,Line.CreateUnbound(XYZ.Zero,XYZ.BasisZ),37*Math.PI/180);ElementTransformUtils.MoveElement(doc,import.Id,new XYZ(3,5,2));if(transaction.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("Temporary CAD transform failed.");}
                        var rotated=DxfMeshReader.Read(doc,import,slab.Frame,path,1000);
                        _cases.Add(new{name="dxf-fallback-metres-rotated37-translated",status="PASS",groups=rotated.Groups.Count,rotated.Warnings,metadata=InspectImport(doc,import)});
                        try{DxfMeshReader.Read(doc,import,slab.Frame,path,1);throw new InvalidOperationException("Wrong units were accepted.");}
                        catch(ArgumentException){_cases.Add(new{name="dxf-fallback-wrong-units",status="PASS",result="Blocked before planning"});}
                    }
                    catch(Exception error){_cases.Add(new{name="dxf-fallback-metres",status="FAILED",message=error.ToString()});}
                }
                catch (Exception error) { _cases.Add(new { name = "import-metre-dxf", status = "FAILED", message = error.ToString() }); }
                finally { scope.RollBack(); }
            }
            WriteProgress();
        }

        private static object FrameInfo(SlabGeometrySnapshot slab) => new { slab.Frame.Origin, slab.Frame.XAxis, slab.Frame.YAxis, slab.Frame.Normal, shells = slab.Slabs.Select(p => p.Shell).ToList() };

        private void RunStraightCase(Document doc, SlabGeometrySnapshot slab, List<RebarBarType> types, RebarBarType type, string direction, string prefix = "")
        {
            int before = CountRebar(doc);
            var selection = new PreviewSelection { ColorKey = "SMOKE", SpecKey = type.UniqueId, SpacingMm = 200 };
            var parameters = new PreviewParameters { Direction = direction, ConcreteClass = "B25", EdgeCoverMm = slab.SideCoverMm, LayerOffsetMm = 0, RoundIncrementMm = 1, Selections = new List<PreviewSelection> { selection } };
            var request = new PlanRequest
            {
                Slabs = slab.Slabs,
                Faces = InteriorDemand(slab.Slabs),
                Options = new PlanOptions { AlongX = direction.EndsWith("X"), EdgeCoverMm = slab.SideCoverMm + 0.05, RoundIncrementMm = 1, MergeToleranceMm = 0 },
                Specifications = new List<ReinforcementSpec> { new ReinforcementSpec { Key = ReinforcementSession.SpecKey(selection), ColorKey = "SMOKE", MaterialKey = "SMOKE", DiameterMm = type.BarNominalDiameter * 304.8, ModelDiameterMm = type.BarModelDiameter * 304.8, SpacingMm = 200, AnchorageMm = 0 } }
            };
            var plan = ReinforcementPlanner.Build(request);
            Log("PLAN " + direction + " bars=" + plan.Bars.Count + " groups=" + plan.Groups.Count + " valid=" + plan.CanCreate);
            if (!plan.CanCreate)
            {
                _cases.Add(new { name = prefix + "straight-" + direction, status = "BLOCKED", purpose = "GEOMETRY_ONLY_ZERO_ANCHORAGE", plan.Diagnostics });
                WriteProgress(); return;
            }
            using (var outer = new TransactionGroup(doc, "Temporary smoke: rollback"))
            {
                outer.Start();
                try
                {
                    var session = new ReinforcementSession(slab, new ImportedMeshSnapshot(), types);
                    int created = RebarPlanWriter.Create(doc, slab, session, plan, parameters);
                    int after = CountRebar(doc);
                    if (after - before != created) throw new InvalidOperationException("Unexpected created set count.");
                    _cases.Add(new { name = prefix + "straight-" + direction, status = "PASS", purpose = "GEOMETRY_ONLY_ZERO_ANCHORAGE", bars = plan.Bars.Count, sets = created, countBefore = before, countAfter = after });
                    Log("PASS " + direction + " sets=" + created + " bars=" + plan.Bars.Count);
                }
                catch (Exception error)
                {
                    _cases.Add(new { name = prefix + "straight-" + direction, status = "FAILED", message = error.ToString() });
                    Log("CASE_FAILED " + direction + " " + error);
                }
                finally { outer.RollBack(); }
            }
            if (CountRebar(doc) != before) throw new InvalidOperationException("Rollback failed to restore rebar set count.");
            Log("ROLLBACK_VERIFIED " + direction);
            WriteProgress();
        }

        private static Reference FindEdge(Floor floor)
        {
            using (var options = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine })
            {
                var candidates = new List<Edge>();
                foreach (var obj in floor.get_Geometry(options))
                    if (obj is Solid solid)
                        foreach (Edge edge in solid.Edges)
                            if (edge.AsCurve() is Line line && Math.Abs(line.Direction.Z) < 0.5 && edge.Reference != null) candidates.Add(edge);
                return candidates.OrderByDescending(e => e.AsCurve().Length).First().Reference;
            }
        }

        private static List<FaceInput> InteriorDemand(List<PolygonInput> polygons)
        {
            var factory = new NetTopologySuite.Geometries.GeometryFactory();
            var faces = new List<FaceInput>();
            foreach (var source in polygons)
            {
                Func<List<Point2>, NetTopologySuite.Geometries.LinearRing> ring = points => factory.CreateLinearRing(points.Concat(new[] { points[0] }).Select(p => new NetTopologySuite.Geometries.Coordinate(p.X, p.Y)).ToArray());
                var interior = factory.CreatePolygon(ring(source.Shell), source.Holes.Select(ring).ToArray()).Buffer(-1000);
                for (int i = 0; i < interior.NumGeometries; i++)
                {
                    var polygon = interior.GetGeometryN(i) as NetTopologySuite.Geometries.Polygon;
                    if (polygon == null || polygon.IsEmpty) continue;
                    faces.Add(new FaceInput { Id = "synthetic-interior-" + faces.Count, ColorKey = "SMOKE", Vertices = polygon.ExteriorRing.Coordinates.Select(c => new Point2(c.X, c.Y)).ToList() });
                }
            }
            return faces;
        }

        private static object InspectImport(Document doc, ImportInstance import)
        {
            var counts = new Dictionary<string, int>();
            Action<GeometryElement, int, ElementId, bool> visit = null;
            visit = (geometry, depth, inherited, symbol) =>
            {
                foreach (GeometryObject item in geometry)
                {
                    var styleId = item.GraphicsStyleId;
                    var style = doc.GetElement(styleId) as GraphicsStyle;
                    var category = style?.GraphicsStyleCategory;
                    var materialId = item is Mesh mesh ? mesh.MaterialElementId : ElementId.InvalidElementId;
                    var material = doc.GetElement(materialId);
                    string key = "symbol=" + symbol + " depth=" + depth + " kind=" + item.GetType().Name + " styleId=" + styleId.IntegerValue + " style=" + style?.Name + " category=" + category?.Name + " categoryId=" + category?.Id.IntegerValue + " parent=" + category?.Parent?.Name + " inherited=" + inherited.IntegerValue + " material=" + material?.Name + (item is Mesh m ? " triangles=" + m.NumTriangles : "");
                    counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
                    if (item is GeometryInstance instance)
                        using (var placed = symbol ? instance.GetSymbolGeometry() : instance.GetInstanceGeometry()) visit(placed, depth + 1, styleId.Equals(ElementId.InvalidElementId) ? inherited : styleId, symbol);
                }
            };
            using (var options = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = true })
            {
                visit(import.get_Geometry(options), 0, ElementId.InvalidElementId, false);
                visit(import.get_Geometry(options), 0, ElementId.InvalidElementId, true);
            }
            var importType = doc.GetElement(import.GetTypeId());
            Func<BuiltInParameter, object> parameter = builtIn => { var p = importType.get_Parameter(builtIn); return p == null ? null : (object)new { storage = p.StorageType.ToString(), value = p.StorageType == StorageType.Integer ? p.AsInteger().ToString() : p.StorageType == StorageType.Double ? p.AsDouble().ToString(System.Globalization.CultureInfo.InvariantCulture) : p.AsString(), display = p.AsValueString() }; };
            Func<XYZ, object> xyz = p => new { p.X, p.Y, p.Z };
            var transform = import.GetTransform();
            string externalPath = null;
            if (import.IsLinked) externalPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(ExternalFileUtils.GetExternalFileReference(doc, importType.Id).GetAbsolutePath());
            return new { importCategory = import.Category?.Name, externalPath, importUnits = parameter(BuiltInParameter.IMPORT_DISPLAY_UNITS), importScale = parameter(BuiltInParameter.IMPORT_SCALE), transform = new { origin = xyz(transform.Origin), x = xyz(transform.BasisX), y = xyz(transform.BasisY), z = xyz(transform.BasisZ) }, layers = import.Category.SubCategories.Cast<Category>().Select(c => new { c.Name, id = c.Id.IntegerValue, color = c.LineColor.Red + "," + c.LineColor.Green + "," + c.LineColor.Blue }).ToList(), styles = counts };
        }

        private static int CountRebar(Document document) => new FilteredElementCollector(document).OfClass(typeof(Rebar)).GetElementCount();
        private sealed class SmokeFailures : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                var messages=accessor.GetFailureMessages();
                foreach(var message in messages)Log("TRANSACTION_FAILURE "+message.GetSeverity()+" "+message.GetDescriptionText());
                if(messages.Any(m=>m.GetSeverity()==FailureSeverity.Error))return FailureProcessingResult.ProceedWithRollBack;
                foreach(var message in messages)if(message.GetSeverity()==FailureSeverity.Warning)accessor.DeleteWarning(message);
                return FailureProcessingResult.Continue;
            }
        }
        private static string ReadGrade(RebarBarType type)
        {
            var parameter = type.get_Parameter(new Guid("32a47c7f-e91d-4a8e-bf24-927cb679b4d1"));
            return parameter == null ? null : parameter.StorageType == StorageType.Double ? parameter.AsDouble().ToString(System.Globalization.CultureInfo.InvariantCulture) : parameter.StorageType == StorageType.Integer ? parameter.AsInteger().ToString() : parameter.AsString();
        }
        private void WriteProgress()
        {
            _report["cases"] = _cases;
            File.WriteAllText(Path.Combine(RunDirectory, "progress.json"), new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(_report));
        }
        private static void Log(string value) => File.AppendAllText(Path.Combine(RunDirectory, "run.log"), DateTime.UtcNow.ToString("O") + " " + value + Environment.NewLine);
    }
}
