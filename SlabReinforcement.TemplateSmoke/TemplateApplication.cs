using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using SlabReinforcement.Core;
using SlabReinforcement.Integration;

namespace SlabReinforcement.TemplateSmoke
{
    public sealed class TemplateApplication : IExternalApplication
    {
        private static readonly string Bin = Path.GetDirectoryName(typeof(TemplateApplication).Assembly.Location);
        private static readonly Guid SteelGuid = new Guid("32a47c7f-e91d-4a8e-bf24-927cb679b4d1");
        private string _run;
        private readonly Dictionary<string, object> _report = new Dictionary<string, object>();
        private readonly List<object> _cases = new List<object>();
        private readonly Dictionary<string, Tuple<int,int>> _counts = new Dictionary<string, Tuple<int,int>>();
        private readonly List<DemoRequest> _demos = new List<DemoRequest>();

        public Result OnStartup(UIControlledApplication app)
        {
            _run = File.ReadAllText(Path.Combine(Bin, "run-directory.txt")).Trim();
            if (!Directory.Exists(_run)) return Result.Failed;
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            _report["startedUtc"] = DateTime.UtcNow.ToString("O");
            _report["revitVersion"] = app.ControlledApplication.VersionNumber;
            _report["revitBuild"] = app.ControlledApplication.VersionBuild;
            app.DialogBoxShowing += OnDialog;
            app.Idling += Run;
            Log("STARTUP");
            return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication app) { Log("SHUTDOWN"); return Result.Succeeded; }
        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string path = Path.Combine(Bin, new AssemblyName(args.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
        private void OnDialog(object sender, DialogBoxShowingEventArgs args) => Log("DIALOG " + args.DialogId + " " + (args is TaskDialogShowingEventArgs task ? task.Message : ""));
        private void Run(object sender, IdlingEventArgs args)
        {
            var ui = (UIApplication)sender;
            ui.Idling -= Run;
            Document source = null, doc = null;
            try
            {
                VerifyDependencies();
                Log("NEW_PROJECT_FROM_TEMPLATE");
                doc = ui.Application.NewProjectDocument(Path.Combine(_run, "template.rte"));
                if (doc == null) throw new InvalidOperationException("NewProjectDocument returned null.");
                Inventory(doc);
                doc.SaveAs(Path.Combine(_run, "new-template-input.rvt"), new SaveAsOptions { MaximumBackups = 1 });
                Log("OPEN_READ_ONLY_REFERENCE_COPY");
                source = ui.Application.OpenDocumentFile(Path.Combine(_run, "reference.rvt"));
                var oldFloor = new FilteredElementCollector(source).OfClass(typeof(Floor)).Cast<Floor>().OrderByDescending(f => f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0).First();
                var oldSlab = SlabGeometryReader.Read(source, oldFloor, FindEdge(oldFloor, null));
                var oldImport = new FilteredElementCollector(source).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Single();
                var verifiedSource = DxfMeshReader.Read(source, oldImport, oldSlab.Frame, Path.Combine(_run, "source.dxf"), 1);
                var sourceTransform = oldImport.GetTotalTransform();
                _report["source"] = new { floor = oldFloor.Name, oldSlab.ThicknessMm, frame = Frame(oldSlab), verifiedSource.Warnings, importTransform = TransformInfo(sourceTransform), importUnits = Param(source.GetElement(oldImport.GetTypeId()).get_Parameter(BuiltInParameter.IMPORT_DISPLAY_UNITS)), importScale = Param(source.GetElement(oldImport.GetTypeId()).get_Parameter(BuiltInParameter.IMPORT_SCALE)), existingRebarSets = RebarCount(source) };
                var slab = CreateSlab(doc, oldSlab);
                source.Close(false); source = null;
                var import = ImportDxf(doc, slab, sourceTransform);
                var mesh = DxfMeshReader.Read(doc, import, slab.Frame, Path.Combine(_run, "source.dxf"), 1);
                _report["newSlab"] = new { slab.Host.Name, slab.ThicknessMm, slab.TopCoverMm, slab.BottomCoverMm, slab.SideCoverMm, frame = Frame(slab) };
                _report["dxf"] = new { millimetresPerUnit = 1, groups = mesh.Groups.Select(g => new { g.Key, g.Layer, g.ElevationMm, faces = g.Faces.Count, colors = g.Faces.GroupBy(f => f.ColorKey).Select(c => new { color = c.Key, faces = c.Count() }).ToList() }).ToList(), mesh.Warnings, transform = TransformInfo(import.GetTotalTransform()) };
                bool axesMatch = Math.Abs(new XYZ(slab.Frame.YAxis.X, slab.Frame.YAxis.Y, slab.Frame.YAxis.Z).Normalize().DotProduct(import.GetTotalTransform().BasisY.Normalize())) > 1 - 1e-8;
                _report["topYAxesConfirmed"] = axesMatch;
                if (!axesMatch) throw new InvalidOperationException("Selected slab Y axis is not parallel to placed DXF Y.");
                var baselineView = CreateBaselineView(doc, slab.Host, import);
                doc.Save();
                ExportView(doc, baselineView, "baseline-dxf");
                Add("new-template-baseline", "PASS", new { model = doc.PathName, rebarSets = RebarCount(doc), templateCoversPreserved = true });
                var types = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToList();
                var type = types.Where(t => Math.Abs(t.BarNominalDiameter * 304.8 - 12) < 0.01).OrderBy(t => Grade(t) == 500 ? 0 : 1).ThenBy(t => t.Name, StringComparer.Ordinal).FirstOrDefault();
                if (type == null) throw new InvalidOperationException("Template contains no nominal diameter 12 mm bar type. No artificial type was created.");
                _report["selectedBarType"] = TypeInfo(type);
                try { Add("product-runtime-template-types","PASS",TemplateRuntimeProbe.Run(_run,slab,types)); }
                catch(Exception error) { Add("product-runtime-template-types","FAILED",new {message=error.ToString()}); }
                var main = mesh.Groups.OrderByDescending(g => g.Faces.Count).First();
                foreach (var group in mesh.Groups.OrderBy(g => g.ElevationMm))
                {
                    var p = ZoneParameters(slab, type, group, group.Faces.Select(f => f.ColorKey).Distinct());
                    TestPlan(doc, slab, mesh, types, p, "all-colors-z" + Math.Round(group.ElevationMm, 3).ToString(CultureInfo.InvariantCulture), group == main ? "success-demo.rvt" : null);
                }
                var green = main.Faces.Where(f => f.ColorKey == "RGB(0, 255, 0)").ToList();
                if (green.Count == 0) Add("green-zone", "BLOCKED", new { message = "No green source faces in main map." });
                else TestPlan(doc, slab, mesh, types, ZoneParameters(slab, type, main, new[] { "RGB(0, 255, 0)" }), "green-zone", "green-zone-demo.rvt");
                foreach (var variant in new[] { "background", "perimeter-u", "background-u" })
                    TestPlan(doc, slab, new ImportedMeshSnapshot(), types, BackgroundParameters(slab, type, variant), variant, variant == "background-u" ? "background-u-demo.rvt" : null);
                var combined = ZoneParameters(slab,type,main,main.Faces.Select(f=>f.ColorKey).Distinct());
                var components = BackgroundParameters(slab,type,"background-u");
                combined.BackgroundLayers = components.BackgroundLayers;
                combined.Perimeter = components.Perimeter;
                TestPlan(doc,slab,mesh,types,combined,"combined-main-zone-background-u","combined-demo.rvt");
                Variation(doc, slab, types, type, "rotate37", true);
                Variation(doc, slab, types, type, "opening2000", false);
                string stableEdge = FindEdge(slab.Host, new XYZ(slab.Frame.XAxis.X,slab.Frame.XAxis.Y,slab.Frame.XAxis.Z)).ConvertToStableRepresentation(doc);
                int hostId = slab.Host.Id.IntegerValue;
                doc.Close(false); doc = null;
                SaveDemos(ui.Application, hostId, stableEdge);
                _report["status"] = "COMPLETED";
            }
            catch (Exception error) { _report["status"] = "FAILED"; _report["exception"] = error.ToString(); Log("FAILED " + error); }
            finally
            {
                if (source != null && source.IsValidObject) source.Close(false);
                if (doc != null && doc.IsValidObject) doc.Close(false);
                _report["finishedUtc"] = DateTime.UtcNow.ToString("O");
                _report["allRequestedCasesPassed"] = _report.ContainsKey("status") && (string)_report["status"] == "COMPLETED" && !_cases.Any(c => (string)c.GetType().GetProperty("status").GetValue(c) != "PASS");
                Write("results.json");
                File.WriteAllText(Path.Combine(_run, "completed.flag"), (string)_report["status"]);
                Log("CLOSED_WITHOUT_FURTHER_SAVE " + _report["status"]);
                ui.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit));
            }
        }
        private void VerifyDependencies()
        {
            string workspace = Path.GetFullPath(Path.Combine(Bin, "..", "..", "..", ".."));
            string product = Path.Combine(workspace, "SlabReinforcement", "bin", "R2023");
            foreach (var assembly in new[] { typeof(PlannedBar).Assembly, typeof(SlabReinforcement.Dxf.DxfFaceSource).Assembly, typeof(NetTopologySuite.Geometries.Geometry).Assembly })
                if (Hash(assembly.Location) != Hash(Path.Combine(product, Path.GetFileName(assembly.Location)))) throw new InvalidOperationException("Product dependency mismatch: " + assembly.GetName().Name);
            _report["exactProductDependencies"] = true;
        }
        private void Inventory(Document doc)
        {
            var types = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToList();
            var floors = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().ToList();
            _report["templateInventory"] = new
            {
                isFamily = doc.IsFamilyDocument, isWorkshared = doc.IsWorkshared, rebarSets = RebarCount(doc),
                barTypes = types.Select(TypeInfo).ToList(), missingSteelGradeParameterCount = types.Count(t => !Grade(t).HasValue),
                floorTypes = floors.Select(t => new { t.Name, id = t.Id.IntegerValue, structure = StructureInfo(doc, t) }).ToList(),
                covers = new FilteredElementCollector(doc).OfClass(typeof(RebarCoverType)).Cast<RebarCoverType>().Select(t => new { t.Name, mm = t.CoverDistance * 304.8 }).ToList(),
                levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Select(l => new { l.Name, elevationMm = l.Elevation * 304.8 }).ToList(),
                sharedParameters = new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)).Cast<SharedParameterElement>().Select(p => new { p.Name, guid = p.GuidValue.ToString() }).ToList()
            };
            Write("inventory.json");
            Log("INVENTORY bars=" + types.Count + " floors=" + floors.Count + " missingGrade=" + types.Count(t => !Grade(t).HasValue));
        }
        private SlabGeometrySnapshot CreateSlab(Document doc, SlabGeometrySnapshot source)
        {
            if (Math.Abs(source.Frame.Normal.Z - 1) > 1e-8) throw new InvalidOperationException("Template fixture requires a horizontal source slab.");
            var candidates = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().Where(t =>
            { using (var s = t.GetCompoundStructure()) return s != null && s.LayerCount == 1 && s.VariableLayerIndex < 0 && IsConcrete(doc.GetElement(s.GetLayers()[0].MaterialId) as Material); }).ToList();
            var originalType = candidates.OrderBy(t => t.Name.IndexOf("25", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                .ThenBy(t => Math.Abs(t.GetCompoundStructure().GetWidth() * 304.8 - source.ThicknessMm)).FirstOrDefault();
            if (originalType == null) throw new InvalidOperationException("No single-layer constant-thickness concrete floor type in template; automatic material substitution is forbidden.");
            Floor floor;
            using (var t = new Transaction(doc, "Create slab from source geometry using template type"))
            {
                t.Start();
                var type = originalType;
                using (var structure = originalType.GetCompoundStructure())
                    if (Math.Abs(structure.GetWidth() * 304.8 - source.ThicknessMm) > 0.01)
                    {
                        type = (FloorType)originalType.Duplicate(originalType.Name + " — test " + source.ThicknessMm.ToString("0.###", CultureInfo.InvariantCulture) + "mm");
                        structure.SetLayerWidth(0, source.ThicknessMm / 304.8);
                        type.SetCompoundStructure(structure);
                    }
                double top = source.Frame.Origin.Z / 304.8;
                var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => Math.Abs(l.Elevation - top)).FirstOrDefault() ?? Level.Create(doc, top);
                var loops = new List<CurveLoop>();
                foreach (var polygon in source.Slabs)
                    foreach (var ring in new[] { polygon.Shell }.Concat(polygon.Holes))
                    {
                        var loop = new CurveLoop();
                        for (int i = 0; i < ring.Count; i++)
                        {
                            var a = SlabGeometryReader.ToXyz(source.Frame.ToWorld(new Point3(ring[i].X, ring[i].Y, 0)));
                            var b = SlabGeometryReader.ToXyz(source.Frame.ToWorld(new Point3(ring[(i + 1) % ring.Count].X, ring[(i + 1) % ring.Count].Y, 0)));
                            loop.Append(Line.CreateBound(new XYZ(a.X, a.Y, 0), new XYZ(b.X, b.Y, 0)));
                        }
                        loops.Add(loop);
                    }
                if (!BoundaryValidation.IsValidHorizontalBoundary(loops)) throw new InvalidOperationException("Source footprint is not a valid horizontal Floor boundary.");
                floor = Floor.Create(doc, loops, type.Id, level.Id, true, null, 0);
                floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(top - level.Elevation);
                if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Floor creation failed.");
            }
            var preferred = new XYZ(source.Frame.XAxis.X, source.Frame.XAxis.Y, source.Frame.XAxis.Z);
            var result = SlabGeometryReader.Read(doc, floor, FindEdge(floor, preferred));
            if(RebarHostData.GetRebarHostData(floor)==null)throw new InvalidOperationException("New template floor is not a valid reinforcement host.");
            if (Math.Abs(result.ThicknessMm - source.ThicknessMm) > 0.01 || Math.Abs(result.Frame.Origin.Z - source.Frame.Origin.Z) > 0.01)
                throw new InvalidOperationException("Created floor thickness or top elevation differs from source.");
            _report["floorTypeSelection"] = new { templateType = originalType.Name, resultingType = floor.FloorType.Name, structure = StructureInfo(doc, floor.FloorType), copiedGeometryOnly = true, defaultTemplateCoversKept = true };
            return result;
        }
        private ImportInstance ImportDxf(Document doc, SlabGeometrySnapshot slab, Transform desired)
        {
            if (Math.Abs(desired.BasisX.GetLength() - 1) > 1e-8 || Math.Abs(desired.BasisY.GetLength() - 1) > 1e-8 || desired.HasReflection || Math.Abs(desired.BasisZ.Z - 1) > 1e-8)
                throw new InvalidOperationException("Source DXF transform is not a rigid horizontal transform.");
            ElementId id;
            using (var t = new Transaction(doc, "Import original DXF in proven millimetres"))
            using (var options = new DWGImportOptions { Unit = ImportUnit.Millimeter, Placement = ImportPlacement.Origin, ColorMode = ImportColorMode.Preserved, ThisViewOnly = false, OrientToView = false, AutoCorrectAlmostVHLines = false })
            {
                t.Start();
                var view = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(v => !v.IsTemplate && (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.EngineeringPlan));
                if (view == null)
                {
                    var family = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.FloorPlan || v.ViewFamily == ViewFamily.StructuralPlan);
                    view = ViewPlan.Create(doc, family.Id, slab.Host.LevelId);
                }
                if (!doc.Import(Path.Combine(_run, "source.dxf"), options, view, out id)) throw new InvalidOperationException("DXF import returned false.");
                doc.Regenerate();
                var import = (ImportInstance)doc.GetElement(id);
                import.Pinned = false;
                var current = import.GetTotalTransform();
                double angle = Math.Atan2(desired.BasisX.Y, desired.BasisX.X) - Math.Atan2(current.BasisX.Y, current.BasisX.X);
                if (Math.Abs(angle) > 1e-12) ElementTransformUtils.RotateElement(doc, id, Line.CreateUnbound(current.Origin, XYZ.BasisZ), angle);
                doc.Regenerate();
                ElementTransformUtils.MoveElement(doc, id, desired.Origin - import.GetTotalTransform().Origin);
                if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("DXF import commit failed.");
            }
            var placed = (ImportInstance)doc.GetElement(id);
            var actual = placed.GetTotalTransform();
            if (actual.Origin.DistanceTo(desired.Origin) * 304.8 > 0.001 || actual.BasisX.DistanceTo(desired.BasisX) > 1e-8 || actual.BasisY.DistanceTo(desired.BasisY) > 1e-8)
                throw new InvalidOperationException("New DXF total transform differs from source.");
            return placed;
        }
        private bool TestPlan(Document doc, SlabGeometrySnapshot slab, ImportedMeshSnapshot mesh, List<RebarBarType> types, PreviewParameters p, string name, string demoName = null)
        {
            try
            {
                var session = new ReinforcementSession(slab, mesh, types);
                var plan = session.CreateContext().Calculate(p, CancellationToken.None);
                if (!plan.CanCreate) { Add(name, "BLOCKED", new { plan.Diagnostics, bars = plan.Bars.Count, parameters = p }); return false; }
                if (plan.Bars.Any(b => b.Placement?.Component == "PerimeterLongitudinal")) throw new InvalidOperationException("Unexpected longitudinal perimeter.");
                if (name == "background-u") _counts[name] = Tuple.Create(plan.Bars.Count, plan.Groups.Count);
                if (name == "rotate37-background-u" && _counts.TryGetValue("background-u", out var count) && (count.Item1 != plan.Bars.Count || count.Item2 != plan.Groups.Count)) throw new InvalidOperationException("Rotation changed bar/group count.");
                int before = RebarCount(doc);
                using (var scope = new TransactionGroup(doc, "Temporary test plan " + name))
                {
                    scope.Start();
                    try
                    {
                        int sets = RebarPlanWriter.Create(doc, slab, session, plan, p);
                        if (RebarCount(doc) - before != sets) throw new InvalidOperationException("Created set count mismatch.");
                        if (demoName != null) _demos.Add(new DemoRequest { Name=name, Filename=demoName, Parameters=p });
                        Add(name, "PASS", new { bars = plan.Bars.Count, sets, plan.Diagnostics, parameters = p, demo = demoName });
                    }
                    finally { scope.RollBack(); }
                }
                if (RebarCount(doc) != before) throw new InvalidOperationException("Test rollback did not restore rebar count.");
                return true;
            }
            catch (Exception error) { Add(name, "FAILED", new { message = error.ToString() }); return false; }
        }
        private void SaveDemos(Autodesk.Revit.ApplicationServices.Application application,int hostId,string stableEdge)
        {
            foreach(var demo in _demos)
            {
                Document doc=null;
                try
                {
                    doc=application.OpenDocumentFile(Path.Combine(_run,"new-template-input.rvt"));
                    var floor=(Floor)doc.GetElement(new ElementId(hostId));
                    var slab=SlabGeometryReader.Read(doc,floor,Reference.ParseFromStableRepresentation(doc,stableEdge));
                    var types=new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToList();
                    var mesh=demo.Parameters.IncludeZones?DxfMeshReader.Read(doc,new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Single(),slab.Frame,Path.Combine(_run,"source.dxf"),1):new ImportedMeshSnapshot();
                    var session=new ReinforcementSession(slab,mesh,types);
                    var plan=session.CreateContext().Calculate(demo.Parameters,CancellationToken.None);
                    if(!plan.CanCreate)throw new InvalidOperationException("Saved-demo recalculation is blocked: "+string.Join(";",plan.Diagnostics.Select(d=>d.Code+":"+d.Message)));
                    int sets=RebarPlanWriter.Create(doc,slab,session,plan,demo.Parameters);
                    var views=CreateRebarViews(doc,floor,demo.Name);
                    string target=Path.Combine(_run,demo.Filename);
                    doc.SaveAs(target,new SaveAsOptions{MaximumBackups=1});
                    foreach(var view in views)
                    {
                        var export=new ImageExportOptions{FilePath=Path.Combine(_run,Path.GetFileNameWithoutExtension(demo.Filename)+(view.Key?"-corner":"-overview")),ExportRange=ExportRange.SetOfViews,ZoomType=ZoomFitType.FitToPage,PixelSize=2400,ImageResolution=ImageResolution.DPI_150,HLRandWFViewsFileType=ImageFileType.PNG,ShadowViewsFileType=ImageFileType.PNG};
                        export.SetViewsAndSheets(new List<ElementId>{view.Value});doc.ExportImage(export);
                    }
                    doc.Close(false);doc=null;
                    doc=application.OpenDocumentFile(target);
                    var reopened=new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().ToList();
                    if(reopened.Count!=sets||reopened.Sum(b=>b.Quantity)!=plan.Bars.Count)throw new InvalidOperationException("Saved RVT re-open bar/set count mismatch.");
                    doc.Close(false);doc=null;
                    Add("saved-"+demo.Name,"PASS",new{model=target,sha256=Hash(target),bars=plan.Bars.Count,sets,reopenedAndVerified=true,purpose="FIXED MANUAL TEST PARAMETERS — NOT A STRUCTURAL DESIGN"});
                }
                catch(Exception error){Add("saved-"+demo.Name,"FAILED",new{message=error.ToString()});}
                finally{if(doc!=null&&doc.IsValidObject)doc.Close(false);}
            }
        }
        private static List<KeyValuePair<bool,ElementId>> CreateRebarViews(Document doc,Floor floor,string name)
        {
            var result=new List<KeyValuePair<bool,ElementId>>();
            var bars=new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().ToList();
            var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t=>t.ViewFamily==ViewFamily.ThreeDimensional);
            var box=floor.get_BoundingBox(null);
            using(var transaction=new Transaction(doc,"Views of test reinforcement"))
            {
                transaction.Start();
                foreach(bool detail in new[]{false,true})
                {
                    var view=View3D.CreateIsometric(doc,type.Id);view.Name="TEST — "+name+(detail?" — detail":" — overall");view.DetailLevel=ViewDetailLevel.Fine;view.DisplayStyle=detail?DisplayStyle.Shading:DisplayStyle.HLR;
                    view.IsolateElementsTemporary(bars.Select(b=>b.Id).Concat(new[]{floor.Id}).ToList());view.ConvertTemporaryHideIsolateToPermanent();
                    foreach(var bar in bars){bar.SetUnobscuredInView(view,true);view.SetElementOverrides(bar.Id,new OverrideGraphicSettings().SetProjectionLineColor(new Color(190,80,35)).SetProjectionLineWeight(1));}
                    view.SetElementOverrides(floor.Id,new OverrideGraphicSettings().SetSurfaceTransparency(95).SetSurfaceForegroundPatternVisible(false).SetSurfaceBackgroundPatternVisible(false).SetCutForegroundPatternVisible(false).SetCutBackgroundPatternVisible(false));
                    var min=box.Min-new XYZ(0.1,0.1,0.5);var max=detail?new XYZ(box.Min.X+3000/304.8,box.Min.Y+3000/304.8,box.Max.Z+0.5):box.Max+new XYZ(0.1,0.1,0.5);
                    if(detail&&(name.StartsWith("all-colors")||name=="green-zone"))
                    {var barBox=bars.First().get_BoundingBox(null);var centreBar=(barBox.Min+barBox.Max)/2;min=new XYZ(centreBar.X-1500/304.8,centreBar.Y-1500/304.8,box.Min.Z-0.5);max=new XYZ(centreBar.X+1500/304.8,centreBar.Y+1500/304.8,box.Max.Z+0.5);}
                    view.SetSectionBox(new BoundingBoxXYZ{Min=min,Max=max});view.IsSectionBoxActive=true;
                    var centre=(min+max)/2;double length=max.DistanceTo(min);var eye=centre+new XYZ(-length,-length,length*0.8);var forward=(centre-eye).Normalize();var right=forward.CrossProduct(XYZ.BasisZ).Normalize();view.SetOrientation(new ViewOrientation3D(eye,right.CrossProduct(forward).Normalize(),forward));
                    result.Add(new KeyValuePair<bool,ElementId>(detail,view.Id));
                    if(!detail)SetStartingView(doc,view.Id);
                }
                transaction.Commit();
            }
            return result;
        }
        private sealed class DemoRequest { public string Name,Filename; public PreviewParameters Parameters; }
        private void Variation(Document doc, SlabGeometrySnapshot slab, List<RebarBarType> types, RebarBarType type, string name, bool rotate)
        {
            string reference = FindEdge(slab.Host, new XYZ(slab.Frame.XAxis.X, slab.Frame.XAxis.Y, slab.Frame.XAxis.Z)).ConvertToStableRepresentation(doc);
            using (var scope = new TransactionGroup(doc, "Temporary template slab variation"))
            {
                scope.Start();
                try
                {
                    using (var transaction = new Transaction(doc, name))
                    {
                        transaction.Start();
                        if (rotate) ElementTransformUtils.RotateElement(doc, slab.Host.Id, Line.CreateUnbound(XYZ.Zero, XYZ.BasisZ), 37 * Math.PI / 180);
                        else
                        {
                            var box = slab.Host.get_BoundingBox(null); var c = (box.Min + box.Max) / 2; double half = 1000 / 304.8;
                            var pts = new[] { new XYZ(c.X-half,c.Y-half,c.Z),new XYZ(c.X+half,c.Y-half,c.Z),new XYZ(c.X+half,c.Y+half,c.Z),new XYZ(c.X-half,c.Y+half,c.Z) };
                            var curves = new CurveArray(); for (int i=0;i<4;i++) curves.Append(Line.CreateBound(pts[i],pts[(i+1)%4]));
                            doc.Create.NewOpening(slab.Host,curves,true);
                        }
                        if(transaction.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("Variation failed.");
                    }
                    var changed = SlabGeometryReader.Read(doc,slab.Host,Reference.ParseFromStableRepresentation(doc,reference));
                    Add(name+"-geometry","PASS",new {frame=Frame(changed),holes=changed.Slabs.Sum(p=>p.Holes.Count)});
                    TestPlan(doc,changed,new ImportedMeshSnapshot(),types,BackgroundParameters(changed,type,"background-u"),name+"-background-u");
                }
                catch(Exception error){Add(name,"FAILED",new{message=error.ToString()});}
                finally{scope.RollBack();}
            }
        }
        private static PreviewParameters ZoneParameters(SlabGeometrySnapshot slab,RebarBarType type,CadPlaneGroup group,IEnumerable<string> colors)=>new PreviewParameters
        {IncludeZones=true,CalculationAxesConfirmed=true,DemandMeaning="Manual",Direction="Верх Y",ConcreteClass="B25",SourceGroupKey=group.Key,EdgeCoverMm=slab.SideCoverMm,RoundIncrementMm=1,Selections=colors.Select(c=>new PreviewSelection{ColorKey=c,SpecKey=type.UniqueId,SpacingMm=200}).ToList()};
        private static PreviewParameters BackgroundParameters(SlabGeometrySnapshot slab,RebarBarType type,string variant)=>new PreviewParameters
        {IncludeZones=false,Direction="Низ X",ConcreteClass="B25",EdgeCoverMm=slab.SideCoverMm,
            BackgroundLayers=variant.StartsWith("background")?new[]{"Низ X","Низ Y","Верх X","Верх Y"}.Select(d=>new PreviewBackgroundLayer{Enabled=true,Direction=d,SpecKey=type.UniqueId,SpacingMm=200}).ToList():new List<PreviewBackgroundLayer>(),
            Perimeter=new PreviewPerimeterOptions{Enabled=variant!="background",Outer=true,Openings=true,UseUbars=true,SpecKey=type.UniqueId,SpacingMm=200,LegLengthMm=500}};
        private ElementId CreateBaselineView(Document doc,Floor floor,ImportInstance import)
        {
            using(var t=new Transaction(doc,"Template test baseline view"))
            {t.Start();var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v=>v.ViewFamily==ViewFamily.ThreeDimensional);var view=View3D.CreateIsometric(doc,type.Id);view.Name="TEST — новая плита и исходный DXF";view.DetailLevel=ViewDetailLevel.Fine;view.DisplayStyle=DisplayStyle.Shading;view.IsolateElementsTemporary(new List<ElementId>{floor.Id,import.Id});view.ConvertTemporaryHideIsolateToPermanent();SetStartingView(doc,view.Id);t.Commit();return view.Id;}
        }
        private static void SetStartingView(Document doc,ElementId view){var settings=StartingViewSettings.GetStartingViewSettings(doc);if(!settings.IsAcceptableStartingView(view))throw new InvalidOperationException("Test overview cannot be a starting view.");settings.ViewId=view;}
        private void ExportView(Document doc,ElementId view,string name){var options=new ImageExportOptions{FilePath=Path.Combine(_run,name),ExportRange=ExportRange.SetOfViews,ZoomType=ZoomFitType.FitToPage,PixelSize=2400,ImageResolution=ImageResolution.DPI_150,HLRandWFViewsFileType=ImageFileType.PNG,ShadowViewsFileType=ImageFileType.PNG};options.SetViewsAndSheets(new List<ElementId>{view});doc.ExportImage(options);}
        private static object StructureInfo(Document doc,FloorType type)
        {
            using(var s=type.GetCompoundStructure())return s==null?null:new {thicknessMm=s.GetWidth()*304.8,structuralMaterialIndex=s.StructuralMaterialIndex,layers=s.GetLayers().Select(l=>new{function=l.Function.ToString(),widthMm=l.Width*304.8,material=doc.GetElement(l.MaterialId)?.Name,materialId=l.MaterialId.IntegerValue,materialClass=(doc.GetElement(l.MaterialId) as Material)?.MaterialClass,concrete=IsConcrete(doc.GetElement(l.MaterialId) as Material)}).ToList()};
        }
        private static bool IsConcrete(Material material)
        {if(material==null)return false;var asset=material.Document.GetElement(material.StructuralAssetId) as PropertySetElement;if(asset!=null&&asset.GetStructuralAsset().StructuralAssetClass==StructuralAssetClass.Concrete)return true;return string.Equals(material.MaterialClass,"Бетон",StringComparison.OrdinalIgnoreCase)||string.Equals(material.MaterialClass,"Concrete",StringComparison.OrdinalIgnoreCase);}
        private static object TypeInfo(RebarBarType t)=>new {t.Name,key=t.UniqueId,nominalMm=t.BarNominalDiameter*304.8,modelMm=t.BarModelDiameter*304.8,bendMm=t.StandardBendDiameter*304.8,steelGrade=Grade(t),steelParameter=Param(t.get_Parameter(SteelGuid))};
        private static double? Grade(RebarBarType type){var p=type.get_Parameter(SteelGuid);if(p==null||!p.HasValue)return null;if(p.StorageType==StorageType.Double)return p.AsDouble();if(p.StorageType==StorageType.Integer)return p.AsInteger();if(p.StorageType==StorageType.String&&double.TryParse(p.AsString(),NumberStyles.Float,CultureInfo.InvariantCulture,out double g))return g;return null;}
        private static object Param(Parameter p)=>p==null?null:new {name=p.Definition.Name,storage=p.StorageType.ToString(),hasValue=p.HasValue,value=p.StorageType==StorageType.Double?p.AsDouble().ToString("R",CultureInfo.InvariantCulture):p.StorageType==StorageType.Integer?p.AsInteger().ToString():p.StorageType==StorageType.String?p.AsString():p.AsElementId().IntegerValue.ToString(),display=p.AsValueString()};
        private static Reference FindEdge(Floor floor,XYZ preferred)
        {using(var options=new Options{ComputeReferences=true,DetailLevel=ViewDetailLevel.Fine}){var edges=new List<Edge>();foreach(var item in floor.get_Geometry(options))if(item is Solid solid)foreach(Edge e in solid.Edges)if(e.AsCurve() is Line line&&Math.Abs(line.Direction.Z)<1e-6&&e.Reference!=null)edges.Add(e);return edges.OrderBy(e=>preferred!=null&&((Line)e.AsCurve()).Direction.DotProduct(preferred)>0.999999?0:1).ThenByDescending(e=>e.AsCurve().Length).First().Reference;}}
        private static object Frame(SlabGeometrySnapshot s)=>new{s.Frame.Origin,s.Frame.XAxis,s.Frame.YAxis,s.Frame.Normal,shells=s.Slabs.Select(p=>p.Shell).ToList()};
        private static object TransformInfo(Transform t)=>new {originMm=SlabGeometryReader.ToPoint(t.Origin),x=new Point3(t.BasisX.X,t.BasisX.Y,t.BasisX.Z),y=new Point3(t.BasisY.X,t.BasisY.Y,t.BasisY.Z),z=new Point3(t.BasisZ.X,t.BasisZ.Y,t.BasisZ.Z)};
        private static int RebarCount(Document d)=>new FilteredElementCollector(d).OfClass(typeof(Rebar)).GetElementCount();
        private static string Hash(string path){using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");}
        private void Add(string name,string status,object result){_cases.Add(new{name,status,result});Log(name+" "+status);Write("progress.json");}
        private void Write(string filename){_report["cases"]=_cases;File.WriteAllText(Path.Combine(_run,filename),new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Serialize(_report));}
        private void Log(string message)=>File.AppendAllText(Path.Combine(_run,"run.log"),DateTime.UtcNow.ToString("O")+" "+message+Environment.NewLine);
    }
}
