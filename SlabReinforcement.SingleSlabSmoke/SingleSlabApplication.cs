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

namespace SlabReinforcement.SingleSlabSmoke
{
    public sealed partial class SingleSlabApplication : IExternalApplication
    {
        private static readonly string Bin = Path.GetDirectoryName(typeof(SingleSlabApplication).Assembly.Location);
        private static readonly Guid SteelGuid = new Guid("32a47c7f-e91d-4a8e-bf24-927cb679b4d1");
        private string _run;
        private readonly Dictionary<string, object> _report = new Dictionary<string, object>();
        private readonly List<object> _cases = new List<object>();
        private readonly List<DemoRequest> _demos = new List<DemoRequest>();
        private FootprintInput _footprint;

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
            Document doc = null;
            try
            {
                VerifyDependencies();
                _footprint = new JavaScriptSerializer().Deserialize<FootprintInput>(File.ReadAllText(Path.Combine(_run,"footprint.json")));
                _footprint.Validate();
                _report["inputFootprint"] = _footprint;
                bool resume=File.Exists(Path.Combine(_run,"resume-input.rvt"));
                bool backgroundOnly=File.Exists(Path.Combine(_run,"background-only.flag"));
                _report["testScope"]=backgroundOnly?"BACKGROUND_ONLY_DIAGNOSTIC":"MANUAL_BOTTOM_TOP_X_WITH_REUSED_PERIMETER_AND_MAXIMUM_SPACING";
                _report["directionAssumption"]=new {direction="Низ X",basis="User described 1 Слой as probably bottom X; adopted for this Manual geometry test only",verifiedCalculationLegend=false,requiredAreaInferred=false};
                _report["resumedExistingBaseline"]=resume;
                Log(resume?"OPEN_BASELINE_COPY":"NEW_PROJECT_FROM_TEMPLATE");
                doc = resume?ui.Application.OpenDocumentFile(Path.Combine(_run,"resume-input.rvt")):ui.Application.NewProjectDocument(Path.Combine(_run, "template.rte"));
                if (doc == null) throw new InvalidOperationException("NewProjectDocument returned null.");
                Inventory(doc);
                if(RebarCount(doc)!=0)throw new InvalidOperationException("Single-slab input must contain no reinforcement; existing background/U must never be duplicated.");
                var existingFloor=resume?new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().Single():null;
                var slab = resume?SlabGeometryReader.Read(doc,existingFloor,FindInputEdge(existingFloor)):CreateInputSlab(doc);
                ValidateFootprint(slab,"created-floor");
                var import = resume?new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Single():ImportDxf(doc, slab, Transform.Identity);
                var mesh = DxfMeshReader.Read(doc, import, slab.Frame, Path.Combine(_run, "source.dxf"), _footprint.cleanedDxfMmPerUnit);
                _report["newSlab"] = new { slab.Host.Name, slab.ThicknessMm, slab.TopCoverMm, slab.BottomCoverMm, slab.SideCoverMm, frame = Frame(slab) };
                _report["dxf"] = new { millimetresPerUnit = _footprint.cleanedDxfMmPerUnit, groups = mesh.Groups.Select(g => new { g.Key, g.Layer, g.ElevationMm, faces = g.Faces.Count, colors = g.Faces.GroupBy(f => f.ColorKey).Select(c => new { color = c.Key, faces = c.Count() }).ToList() }).ToList(), mesh.Warnings, transform = TransformInfo(import.GetTotalTransform()) };
                bool axesMatch = Math.Abs(new XYZ(slab.Frame.XAxis.X, slab.Frame.XAxis.Y, slab.Frame.XAxis.Z).Normalize().DotProduct(import.GetTotalTransform().BasisX.Normalize())) > 1 - 1e-8;
                _report["bottomXGeometricAxesConfirmed"] = axesMatch;
                if (!axesMatch) throw new InvalidOperationException("Selected slab X axis is not parallel to placed DXF X.");
                var baselineView = resume?new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Single(v=>v.Name=="Плита — план").Id:CreateBaselineView(doc, slab.Host, import);
                doc.SaveAs(Path.Combine(_run,"single-slab-input.rvt"),new SaveAsOptions{MaximumBackups=1});
                ExportView(doc, baselineView, "baseline-dxf");
                Add("single-slab-baseline", "PASS", new { model = doc.PathName, rebarSets = RebarCount(doc), templateCoversPreserved = true });
                var types = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToList();
                var type = types.Where(t => Math.Abs(t.BarNominalDiameter * 304.8 - 12) < 0.01).OrderBy(t => Grade(t) == 500 ? 0 : 1).ThenBy(t => t.Name, StringComparer.Ordinal).FirstOrDefault();
                if (type == null) throw new InvalidOperationException("Template contains no nominal diameter 12 mm bar type. No artificial type was created.");
                _report["selectedBarType"] = TypeInfo(type);
                try { Add("product-runtime-template-types","PASS",TemplateRuntimeProbe.Run(_run,slab,types)); }
                catch(Exception error) { Add("product-runtime-template-types","FAILED",new {message=error.ToString()}); }
                var main = mesh.Groups.OrderByDescending(g => g.Faces.Count).First();
                if(mesh.Groups.Count!=1)throw new InvalidOperationException("Cleaned single-slab DXF must contain exactly one explicitly selected layer/plane; received "+mesh.Groups.Count);
                if(backgroundOnly)TestPlan(doc,slab,new ImportedMeshSnapshot(),types,BackgroundParameters(slab,type,"background"),"background","background-only.rvt");
                else
                {
                foreach (var group in mesh.Groups.OrderBy(g => g.ElevationMm))
                {
                    var p = ZoneParameters(slab, type, group, group.Faces.Select(f => f.ColorKey).Distinct());
                    TestPlan(doc, slab, mesh, types, p, "all-colors-manual-bottom-x", expectAnchorageBlock:true);
                }
                var nonwhite = main.Faces.Select(f=>f.ColorKey).Distinct().Where(c=>c!="RGB(255, 255, 255)").ToList();
                TestPlan(doc,slab,mesh,types,ZoneParameters(slab,type,main,nonwhite),"nonwhite-zones-manual-bottom-x",expectAnchorageBlock:true);
                foreach (var variant in new[] { "background", "perimeter-u", "background-u" })
                    TestPlan(doc, slab, new ImportedMeshSnapshot(), types, BackgroundParameters(slab, type, variant), variant, variant == "background-u" ? "reinforced-slab.rvt" : variant == "background" ? "background-only.rvt" : null);
                var strictCombined = CombinedParameters(slab,type,main,nonwhite,false);
                TestPlan(doc,slab,mesh,types,strictCombined,"combined-nonwhite-zones-bottom-x-background-u-strict",expectAnchorageBlock:true);
                var acceptedCombined = CombinedParameters(slab,type,main,nonwhite,true);
                TestPlan(doc,slab,mesh,types,acceptedCombined,"combined-nonwhite-zones-bottom-x-background-u","zones-bottom-x-background-u.rvt");
                var noPerimeter = ReuseParameters(slab,type,main,nonwhite,"Низ X",false);
                TestPlan(doc,slab,mesh,types,noPerimeter,"accepted-u-ends-without-perimeter",expectedBlockingCodes:new[]{"ZONE_U_REQUIRES_PERIMETER"});
                var numerical = CombinedParameters(slab,type,main,nonwhite,true);
                numerical.DemandMeaning="AdditionalArea";
                foreach(var selection in numerical.Selections)selection.RequiredAreaMm2PerM=565;
                TestPlan(doc,slab,mesh,types,numerical,"accepted-u-ends-with-numeric-as",expectedBlockingCodes:new[]{"ZONE_U_REQUIRES_MANUAL"});
                }
                string stableEdge = FindInputEdge(slab.Host).ConvertToStableRepresentation(doc);
                int hostId = slab.Host.Id.IntegerValue;
                doc.Close(false); doc = null;
                SaveDemos(ui.Application, hostId, stableEdge);
                if(!backgroundOnly)
                {
                    RunSessionReuseRegressions(ui.Application,hostId,stableEdge);
                    RunSequentialReuse(ui.Application,hostId,stableEdge);
                }
                _report["status"] = "COMPLETED";
            }
            catch (Exception error) { _report["status"] = "FAILED"; _report["exception"] = error.ToString(); Log("FAILED " + error); }
            finally
            {
                if (doc != null && doc.IsValidObject) doc.Close(false);
                _report["finishedUtc"] = DateTime.UtcNow.ToString("O");
                _report["allRequestedCasesPassed"] = _report.ContainsKey("status") && (string)_report["status"] == "COMPLETED" && !_cases.Any(c => { var status=(string)c.GetType().GetProperty("status").GetValue(c);return status != "PASS" && status != "EXPECTED_BLOCKED"; });
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
        private SlabGeometrySnapshot CreateInputSlab(Document doc)
        {
            var source=new SlabGeometrySnapshot
            {
                ThicknessMm=_footprint.thicknessMm,
                Frame=SlabFrame.FromEdge(new Point3(0,0,_footprint.topElevationMm),new Point3(1000,0,_footprint.topElevationMm),new Point3(0,0,1)),
                Slabs=new List<PolygonInput>{new PolygonInput{Shell=_footprint.shell.Select(p=>new Point2(p[0],p[1])).ToList(),Holes=_footprint.holes.Select(h=>h.Select(p=>new Point2(p[0],p[1])).ToList()).ToList()}}
            };
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
            var result = SlabGeometryReader.Read(doc, floor, FindInputEdge(floor));
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
            using (var options = new DWGImportOptions { Unit = _footprint.cleanedDxfMmPerUnit==1000?ImportUnit.Meter:_footprint.cleanedDxfMmPerUnit==1?ImportUnit.Millimeter:throw new InvalidOperationException("Unsupported explicit DXF unit factor."), Placement = ImportPlacement.Origin, ColorMode = ImportColorMode.Preserved, ThisViewOnly = false, OrientToView = false, AutoCorrectAlmostVHLines = false })
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
        private bool TestPlan(Document doc, SlabGeometrySnapshot slab, ImportedMeshSnapshot mesh, List<RebarBarType> types, PreviewParameters p, string name, string demoName = null, bool expectAnchorageBlock=false, string[] expectedBlockingCodes=null)
        {
            try
            {
                int originalCount=RebarCount(doc);
                var session = new ReinforcementSession(slab, mesh, types);
                var plan = session.CreateContext().Calculate(p, CancellationToken.None);
                if(expectAnchorageBlock || expectedBlockingCodes!=null)
                {
                    var codes=plan.Diagnostics.Where(d=>d.IsBlocking).Select(d=>d.Code).Distinct().OrderBy(c=>c).ToList();
                    var expected=(expectedBlockingCodes??new[]{"ANCHORAGE_DOES_NOT_FIT","UNCOVERED_DEMAND"}).Distinct().OrderBy(c=>c).ToArray();
                    if(plan.CanCreate||!codes.SequenceEqual(expected)||RebarCount(doc)!=originalCount)
                        throw new InvalidOperationException("Rejection differs from explicit expectation: "+string.Join(",",codes));
                    bool writerRejected=false;
                    using(var scope=new TransactionGroup(doc,"Verify rejected plan does not write"))
                    {
                        scope.Start();
                        try{RebarPlanWriter.Create(doc,slab,session,plan,p);}
                        catch(ArgumentException){writerRejected=true;}
                        finally{scope.RollBack();}
                    }
                    if(!writerRejected||RebarCount(doc)!=originalCount)throw new InvalidOperationException("Rejected plan wrote model changes.");
                    Add(name,"EXPECTED_BLOCKED",new{plan.Diagnostics,partialPreviewBars=plan.Bars.Count,parameters=p,expectedBlockingCodes=expected,writerRejected,unchangedRebarCount=originalCount});
                    return true;
                }
                if (!plan.CanCreate) { Add(name, "BLOCKED", new { plan.Diagnostics, bars = plan.Bars.Count, parameters = p }); return false; }
                if (plan.Bars.Any(b => b.Placement?.Component == "PerimeterLongitudinal")) throw new InvalidOperationException("Unexpected longitudinal perimeter.");
                AssertAcceptedZonePlan(plan,p);
                int before = RebarCount(doc);
                using (var scope = new TransactionGroup(doc, "Temporary test plan " + name))
                {
                    scope.Start();
                    try
                    {
                        int sets = RebarPlanWriter.Create(doc, slab, session, plan, p);
                        if (RebarCount(doc) - before != sets) throw new InvalidOperationException("Created set count mismatch.");
                        var physicalMapping=new Dictionary<string,PlannedBar>();
                        var centrelineComparison=CompareFullCentrelines(plan.Bars,ExistingRebarReader.Read(slab),physicalMapping);
                        var actualLayouts=AssertMaximumSpacing(doc,physicalMapping);
                        if (demoName != null) _demos.Add(new DemoRequest { Name=name, Filename=demoName, Parameters=p });
                        Add(name, "PASS", new { bars = plan.Bars.Count, sets, components = Components(plan), plan.Diagnostics, parameters = p, demo = demoName,centrelineComparison,actualLayouts,acceptedZoneEnds=ZoneConnections(plan) });
                    }
                    finally { scope.RollBack(); }
                }
                if (RebarCount(doc) != before) throw new InvalidOperationException("Test rollback did not restore rebar count.");
                return true;
            }
            catch (Exception error) { Add(name, "FAILED", new { message = error.ToString(), data = error.Data.Keys.Cast<object>().ToDictionary(k=>k.ToString(),k=>error.Data[k]) }); return false; }
        }
        private void SaveDemos(Autodesk.Revit.ApplicationServices.Application application,int hostId,string stableEdge)
        {
            foreach(var demo in _demos)
            {
                Document doc=null;
                try
                {
                    doc=application.OpenDocumentFile(Path.Combine(_run,"single-slab-input.rvt"));
                    if(RebarCount(doc)!=0)throw new InvalidOperationException("Saved-demo baseline already contains reinforcement.");
                    var floor=(Floor)doc.GetElement(new ElementId(hostId));
                    var slab=SlabGeometryReader.Read(doc,floor,Reference.ParseFromStableRepresentation(doc,stableEdge));
                    ValidateFootprint(slab,"before-"+demo.Name);
                    var types=new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToList();
                    var mesh=demo.Parameters.IncludeZones?DxfMeshReader.Read(doc,new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Single(),slab.Frame,Path.Combine(_run,"source.dxf"),_footprint.cleanedDxfMmPerUnit):new ImportedMeshSnapshot();
                    var session=new ReinforcementSession(slab,mesh,types);
                    var plan=session.CreateContext().Calculate(demo.Parameters,CancellationToken.None);
                    if(!plan.CanCreate)throw new InvalidOperationException("Saved-demo recalculation is blocked: "+string.Join(";",plan.Diagnostics.Select(d=>d.Code+":"+d.Message)));
                    AssertAcceptedZonePlan(plan,demo.Parameters);
                    int sets=RebarPlanWriter.Create(doc,slab,session,plan,demo.Parameters);
                    var visualMapping=new Dictionary<string,PlannedBar>();
                    CompareFullCentrelines(plan.Bars,ExistingRebarReader.Read(slab),visualMapping);
                    var views=CreateRebarViews(doc,floor,demo.Name,demo.Parameters.UsePerimeterUForZoneEnds?visualMapping:null);
                    string target=Path.Combine(_run,demo.Filename);
                    doc.SaveAs(target,new SaveAsOptions{MaximumBackups=1});
                    foreach(var view in views)
                    {
                        var export=new ImageExportOptions{FilePath=Path.Combine(_run,Path.GetFileNameWithoutExtension(demo.Filename)+(view.Key?"-corner":"-overview")),ExportRange=ExportRange.SetOfViews,ZoomType=ZoomFitType.FitToPage,PixelSize=2400,ImageResolution=ImageResolution.DPI_150,HLRandWFViewsFileType=ImageFileType.PNG,ShadowViewsFileType=ImageFileType.PNG};
                        export.SetViewsAndSheets(new List<ElementId>{view.Value});doc.ExportImage(export);
                    }
                    doc.Close(false);doc=null;
                    doc=application.OpenDocumentFile(target);
                    var reopenedSlab=SlabGeometryReader.Read(doc,(Floor)doc.GetElement(new ElementId(hostId)),Reference.ParseFromStableRepresentation(doc,stableEdge));
                    ValidateFootprint(reopenedSlab,"reopened-"+demo.Name);
                    var reopened=new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().ToList();
                    if(reopened.Count!=sets||reopened.Sum(b=>b.Quantity)!=plan.Bars.Count)throw new InvalidOperationException("Saved RVT re-open bar/set count mismatch.");
                    var actual=ExistingRebarReader.Read(reopenedSlab);
                    var reopenedMapping=new Dictionary<string,PlannedBar>();
                    var reopenedCentrelineComparison=CompareFullCentrelines(plan.Bars,actual,reopenedMapping);
                    var reopenedLayouts=AssertMaximumSpacing(doc,reopenedMapping);
                    var reopenedDiagnostics=BarContainmentValidator.Validate(actual,reopenedSlab.Slabs,reopenedSlab.ThicknessMm,reopenedSlab.TopCoverMm,reopenedSlab.BottomCoverMm,demo.Parameters.EdgeCoverMm+0.05);
                    reopenedDiagnostics.AddRange(CompositePlanValidator.Validate(actual,allowBarIntersections:demo.Parameters.AllowBarIntersections));
                    if(actual.Count!=plan.Bars.Count||reopenedDiagnostics.Any(d=>d.IsBlocking))throw new InvalidOperationException("Reopened reinforcement failed actual geometry validation: "+string.Join(";",reopenedDiagnostics.Where(d=>d.IsBlocking).Select(d=>d.Code+":"+d.Message)));
                    doc.Close(false);doc=null;
                    Add("saved-"+demo.Name,"PASS",new{model=target,sha256=Hash(target),bars=plan.Bars.Count,sets,components=Components(plan),acceptedZoneEnds=ZoneConnections(plan),reopenedAndVerified=true,actualTotalBarsVerified=true,reopenedActualGeometryChecked=true,reopenedCentrelineComparison,reopenedLayouts,reopenedDiagnostics,purpose="FIXED MANUAL TEST PARAMETERS — NOT A STRUCTURAL DESIGN"});
                }
                catch(Exception error){Add("saved-"+demo.Name,"FAILED",new{message=error.ToString()});}
                finally{if(doc!=null&&doc.IsValidObject)doc.Close(false);}
            }
        }
        private static List<KeyValuePair<bool,ElementId>> CreateRebarViews(Document doc,Floor floor,string name,IDictionary<string,PlannedBar> physicalMapping=null)
        {
            var result=new List<KeyValuePair<bool,ElementId>>();
            var bars=new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().ToList();
            var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t=>t.ViewFamily==ViewFamily.ThreeDimensional);
            var box=floor.get_BoundingBox(null);
            var styles=new Dictionary<string,PlannedBar>();
            if(physicalMapping!=null)
                foreach(var pair in physicalMapping)
                {
                    var parts=pair.Key.Split('|');
                    if(parts.Length!=3||parts[0]!="Existing")throw new InvalidOperationException("Unexpected physical readback identifier.");
                    if(styles.TryGetValue(parts[1],out var prior)&&prior.Placement.Component!=pair.Value.Placement.Component)
                        throw new InvalidOperationException("One Revit set unexpectedly contains different components.");
                    styles[parts[1]]=pair.Value;
                }
            var zoneBar=bars.FirstOrDefault(b=>styles.TryGetValue(b.UniqueId,out var p)&&p.Placement.Component=="Zones");
            XYZ zoneFocus=zoneBar?.GetCenterlineCurves(true,false,false,MultiplanarOption.IncludeAllMultiplanarCurves,0).First().GetEndPoint(0);
            var solidFill=physicalMapping==null?null:new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().FirstOrDefault(f=>f.GetFillPattern().IsSolidFill);
            using(var transaction=new Transaction(doc,"Views of test reinforcement"))
            {
                transaction.Start();
                foreach(bool detail in new[]{false,true})
                {
                    var view=View3D.CreateIsometric(doc,type.Id);view.Name=detail?"Армирование — фрагмент":"Армирование — общий вид";view.DetailLevel=ViewDetailLevel.Fine;view.DisplayStyle=detail?DisplayStyle.Shading:DisplayStyle.HLR;
                    view.IsolateElementsTemporary(bars.Select(b=>b.Id).Concat(new[]{floor.Id}).ToList());view.ConvertTemporaryHideIsolateToPermanent();
                    foreach(var bar in bars)
                    {
                        bar.SetUnobscuredInView(view,true);
                        var color=styles.TryGetValue(bar.UniqueId,out var planned)?DisplayColor(planned):new Color(190,80,35);
                        var graphics=new OverrideGraphicSettings().SetProjectionLineColor(color).SetProjectionLineWeight(planned?.Placement.Component=="Zones"?3:1);
                        if(solidFill!=null)graphics.SetSurfaceForegroundPatternId(solidFill.Id).SetSurfaceForegroundPatternColor(color).SetSurfaceForegroundPatternVisible(true);
                        view.SetElementOverrides(bar.Id,graphics);
                    }
                    view.SetElementOverrides(floor.Id,new OverrideGraphicSettings().SetSurfaceTransparency(95).SetSurfaceForegroundPatternVisible(false).SetSurfaceBackgroundPatternVisible(false).SetCutForegroundPatternVisible(false).SetCutBackgroundPatternVisible(false));
                    var min=box.Min-new XYZ(0.1,0.1,0.5);var max=detail?new XYZ(box.Min.X+3000/304.8,box.Min.Y+3000/304.8,box.Max.Z+0.5):box.Max+new XYZ(0.1,0.1,0.5);
                    if(detail&&zoneFocus!=null)
                    {min=new XYZ(zoneFocus.X-1500/304.8,zoneFocus.Y-1500/304.8,box.Min.Z-0.5);max=new XYZ(zoneFocus.X+1500/304.8,zoneFocus.Y+1500/304.8,box.Max.Z+0.5);}
                    else if(detail&&(name.StartsWith("all-colors")||name.StartsWith("nonwhite-zones")))
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
        private static Color DisplayColor(PlannedBar bar)
        {
            if(bar.Placement.Component=="Background")return new Color(115,125,135);
            if(bar.Placement.Component=="PerimeterU")return new Color(190,80,35);
            var color=System.Text.RegularExpressions.Regex.Match(bar.ColorKey??"",@"^RGB\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)$");
            return color.Success?new Color(byte.Parse(color.Groups[1].Value),byte.Parse(color.Groups[2].Value),byte.Parse(color.Groups[3].Value)):new Color(0,130,190);
        }
        private static object Components(ReinforcementPlan plan)=>plan.Bars.GroupBy(b=>b.Placement?.Component??"Unknown").Select(g=>new{component=g.Key,bars=g.Count(),sets=plan.Groups.Count(s=>(s.Placement?.Component??"Unknown")==g.Key)}).ToList();
        private static object ZoneConnections(ReinforcementPlan plan)=>plan.Bars.Where(b=>b.Placement?.Component=="Zones"&&b.EndConnections.Count>0)
            .Select(b=>new{b.SpecKey,b.ColorKey,b.Start,b.End,b.EndConnections}).ToList();
        private static void AssertAcceptedZonePlan(ReinforcementPlan plan,PreviewParameters parameters)
        {
            if(!parameters.UsePerimeterUForZoneEnds)return;
            var zones=plan.Bars.Where(b=>b.Placement?.Component=="Zones").ToList();
            if(parameters.DemandMeaning!="Manual"||parameters.Direction!="Низ X"||!parameters.Perimeter.Enabled||zones.Count==0)
                throw new InvalidOperationException("Accepted U-end fixture requires Manual zones and the same operation's perimeter.");
            if(!plan.Bars.Any(b=>b.Placement?.Component=="PerimeterU")
                ||plan.Bars.Where(b=>b.Placement?.Component=="Background").Select(b=>b.SpecKey).Distinct().Count()!=4)
                throw new InvalidOperationException("Accepted U-end fixture must include U bars and all four background layers.");
            if(zones.Any(b=>PathOf(b).Count!=1||PathOf(b)[0].Midpoint.HasValue))
                throw new InvalidOperationException("Accepted shared perimeter must leave zone bars straight.");
            if(zones.Any(b=>Math.Abs(b.Placement.ElevationMm+135)>1e-6||Math.Abs(PathOf(b)[0].Start.Z+135)>1e-6||Math.Abs(PathOf(b)[0].End.Z+135)>1e-6))
                throw new InvalidOperationException("Bottom-X zones must use the actual matching background row at local Z=-135 mm.");
            if(!plan.Diagnostics.Any(d=>d.Code=="ZONE_ENDS_IN_PERIMETER_U"&&!d.IsBlocking))
                throw new InvalidOperationException("Accepted shared perimeter geometry-only diagnostic is missing.");
            if(zones.Sum(b=>b.EndConnections.Count)==0)throw new InvalidOperationException("Accepted U-end fixture did not provide per-end geometric evidence.");
        }
        private static List<CenterlineSegment> PathOf(PlannedBar bar)
        {
            if(bar.Centerline!=null&&bar.Centerline.Count>0)return bar.Centerline;
            if(bar.Placement==null)throw new InvalidOperationException("Expected/actual bar has no 3D placement.");
            return new List<CenterlineSegment>{new CenterlineSegment
            {Start=new Point3(bar.Start.X,bar.Start.Y,bar.Placement.ElevationMm),End=new Point3(bar.End.X,bar.End.Y,bar.Placement.ElevationMm)}};
        }
        // Match whole physical bars and every line/arc one-to-one; neither collector order,
        // array order, chain direction nor segment order is assumed after SaveAs/reopening.
        private static object CompareFullCentrelines(IList<PlannedBar> expected,IList<PlannedBar> actual,IDictionary<string,PlannedBar> physicalMapping=null)
        {
            const double toleranceMm=0.1;
            if(expected.Count!=actual.Count)throw new InvalidOperationException("Centreline comparison: physical bar counts differ.");
            var remaining=actual.Select(b=>new {Bar=b,Path=PathOf(b)}).ToList();
            int totalSegments=0;double maximumErrorMm=0;
            foreach(var bar in expected)
            {
                var path=PathOf(bar);int match=-1;double matchedError=double.PositiveInfinity;
                for(int i=0;i<remaining.Count;i++)
                {
                    var candidate=remaining[i];
                    if(candidate.Bar.Placement?.TypeKey!=bar.Placement?.TypeKey||candidate.Path.Count!=path.Count
                        ||Math.Abs(candidate.Bar.Placement.ModelDiameterMm-bar.Placement.ModelDiameterMm)>1e-6)continue;
                    double error=PathError(path,candidate.Path,toleranceMm);
                    if(error<=toleranceMm&&error<matchedError){match=i;matchedError=error;if(error<1e-8)break;}
                }
                if(match<0)throw new InvalidOperationException("Actual physical centreline does not match expected bar: "+bar.SpecKey+"; component="+bar.Placement?.Component);
                if(physicalMapping!=null)physicalMapping.Add(remaining[match].Bar.SpecKey,bar);
                remaining.RemoveAt(match);totalSegments+=path.Count;maximumErrorMm=Math.Max(maximumErrorMm,matchedError);
            }
            if(remaining.Count!=0)throw new InvalidOperationException("Unmatched actual centrelines remain.");
            return new {matchedBars=expected.Count,matchedSegments=totalSegments,maximumErrorMm,toleranceMm,orderIndependent=true,includesEveryArrayPosition=true};
        }
        private static double PathError(IList<CenterlineSegment> expected,IList<CenterlineSegment> actual,double tolerance)
        {
            var remaining=actual.ToList();double maximum=0;
            foreach(var segment in expected)
            {
                int best=-1;double error=double.PositiveInfinity;
                for(int i=0;i<remaining.Count;i++)
                {
                    var candidate=remaining[i];if(segment.Midpoint.HasValue!=candidate.Midpoint.HasValue)continue;
                    double direct=Math.Max(Distance(segment.Start,candidate.Start),Distance(segment.End,candidate.End));
                    double reversed=Math.Max(Distance(segment.Start,candidate.End),Distance(segment.End,candidate.Start));
                    double candidateError=Math.Min(direct,reversed);
                    if(segment.Midpoint.HasValue)candidateError=Math.Max(candidateError,Distance(segment.Midpoint.Value,candidate.Midpoint.Value));
                    if(candidateError<error){error=candidateError;best=i;}
                }
                if(best<0||error>tolerance)return double.PositiveInfinity;
                remaining.RemoveAt(best);maximum=Math.Max(maximum,error);
            }
            return maximum;
        }
        private static double Distance(Point3 a,Point3 b)
        {double x=a.X-b.X,y=a.Y-b.Y,z=a.Z-b.Z;return Math.Sqrt(x*x+y*y+z*z);}
        private static PreviewParameters CombinedParameters(SlabGeometrySnapshot slab,RebarBarType type,CadPlaneGroup group,IEnumerable<string> colors,bool acceptedU)
        {
            var result=ZoneParameters(slab,type,group,colors);
            var components=BackgroundParameters(slab,type,"background-u");
            result.BackgroundLayers=components.BackgroundLayers;result.Perimeter=components.Perimeter;
            result.UsePerimeterUForZoneEnds=acceptedU;
            return result;
        }
        private void ValidateFootprint(SlabGeometrySnapshot slab,string stage)
        {
            var factory=new NetTopologySuite.Geometries.GeometryFactory();
            Func<IEnumerable<double[]>,NetTopologySuite.Geometries.LinearRing> ring=points=>
            {var list=points.Select(p=>new NetTopologySuite.Geometries.Coordinate(p[0],p[1])).ToList();list.Add(list[0].Copy());return factory.CreateLinearRing(list.ToArray());};
            var expected=factory.CreatePolygon(ring(_footprint.shell),_footprint.holes.Select(h=>ring(h)).ToArray());
            if(!expected.IsValid)throw new InvalidOperationException("Input footprint is not a valid polygon; no repair or approximation is allowed.");
            if(slab.Slabs.Count!=1||slab.Slabs.Sum(p=>p.Holes.Count)!=_footprint.holes.Count)throw new InvalidOperationException("Actual Revit floor shell/hole topology differs from JSON.");
            Func<Point2,double[]> world=p=>{var w=slab.Frame.ToWorld(new Point3(p.X,p.Y,0));return new[]{w.X,w.Y};};
            var actual=factory.CreatePolygon(ring(slab.Slabs[0].Shell.Select(world)),slab.Slabs[0].Holes.Select(h=>ring(h.Select(world))).ToArray());
            double areaError=Math.Abs(actual.Area-_footprint.expectedAreaMm2);
            double sourceAreaError=Math.Abs(expected.Area-_footprint.expectedAreaMm2);
            double symmetricDifferenceMm2=expected.SymmetricDifference(actual).Area;
            double boundaryDistanceMm=NetTopologySuite.Algorithm.Distance.DiscreteHausdorffDistance.Distance(expected.Boundary,actual.Boundary);
            double areaToleranceMm2=Math.Max(1,_footprint.expectedAreaMm2*1e-8);
            if(sourceAreaError>areaToleranceMm2||areaError>areaToleranceMm2||symmetricDifferenceMm2>areaToleranceMm2||boundaryDistanceMm>0.05||Math.Abs(slab.ThicknessMm-_footprint.thicknessMm)>0.01||Math.Abs(slab.Frame.Origin.Z-_footprint.topElevationMm)>0.01)
                throw new InvalidOperationException("Actual floor differs from approved footprint: areaError="+areaError+"mm2, symmetricDifference="+symmetricDifferenceMm2+"mm2, boundaryDistance="+boundaryDistanceMm+"mm.");
            _report["footprintCheck:"+stage]=new {expectedAreaMm2=_footprint.expectedAreaMm2,actualAreaMm2=actual.Area,areaErrorMm2=areaError,symmetricDifferenceMm2,boundaryDistanceMm,holes=slab.Slabs[0].Holes.Count,shellVertices=slab.Slabs[0].Shell.Count,slab.ThicknessMm,topElevationMm=slab.Frame.Origin.Z,passed=true};
        }
        private Reference FindInputEdge(Floor floor)
        {
            var a=new XYZ(_footprint.axisEdge.start[0]/304.8,_footprint.axisEdge.start[1]/304.8,_footprint.topElevationMm/304.8);
            var b=new XYZ(_footprint.axisEdge.end[0]/304.8,_footprint.axisEdge.end[1]/304.8,_footprint.topElevationMm/304.8);
            using(var options=new Options{ComputeReferences=true,DetailLevel=ViewDetailLevel.Fine})
            {
                var matches=new List<Edge>();
                foreach(var item in floor.get_Geometry(options))if(item is Solid solid)foreach(Edge edge in solid.Edges)
                    if(edge.Reference!=null&&edge.AsCurve() is Line line&&Math.Abs(line.Direction.Z)<1e-8)
                    {
                        var origin=line.GetEndPoint(0);var direction=line.Direction;double length=line.Length;
                        Func<XYZ,bool> onLine=p=>{var d=new XYZ(p.X-origin.X,p.Y-origin.Y,0);double projection=d.DotProduct(direction);return d.CrossProduct(direction).GetLength()*304.8<0.01&&projection>=-1e-8&&projection<=length+1e-8;};
                        if(onLine(a)&&onLine(b))matches.Add(edge);
                    }
                if(matches.Count==0)throw new InvalidOperationException("Approved axisEdge was not found on the created Revit floor.");
                return matches.OrderByDescending(e=>e.AsCurve().Length).First().Reference;
            }
        }
        public sealed class AxisEdgeInput { public double[] start {get;set;} public double[] end {get;set;} }
        public sealed class FootprintInput
        {
            public List<double[]> shell {get;set;}
            public List<List<double[]>> holes {get;set;}
            public AxisEdgeInput axisEdge {get;set;}
            public double expectedAreaMm2 {get;set;}
            public double thicknessMm {get;set;}
            public double topElevationMm {get;set;}
            public double sourcePlaneElevationMm {get;set;}
            public double cleanedDxfMmPerUnit {get;set;}
            public double sourceUnitsToMm {get;set;}
            public void Validate()
            {
                if(shell==null||holes==null||axisEdge==null||axisEdge.start==null||axisEdge.end==null)throw new InvalidOperationException("Footprint JSON requires shell, holes, and axisEdge start/end.");
                if(axisEdge.start.Length!=2||axisEdge.end.Length!=2||axisEdge.start.Concat(axisEdge.end).Any(v=>double.IsNaN(v)||double.IsInfinity(v)))throw new InvalidOperationException("Axis edge requires finite [x,y] endpoints in millimetres.");
                foreach(var ring in new[]{shell}.Concat(holes))
                {
                    if(ring.Any(p=>p==null||p.Length!=2||p.Any(v=>double.IsNaN(v)||double.IsInfinity(v))))throw new InvalidOperationException("Footprint coordinates must be finite [x,y] millimetres.");
                    if(ring.Count>1&&ring.First()[0]==ring.Last()[0]&&ring.First()[1]==ring.Last()[1])ring.RemoveAt(ring.Count-1);
                    if(ring.Count<3)throw new InvalidOperationException("Footprint ring needs at least three vertices.");
                }
                if(new[]{expectedAreaMm2,thicknessMm,topElevationMm,sourcePlaneElevationMm,cleanedDxfMmPerUnit,sourceUnitsToMm}.Any(v=>double.IsNaN(v)||double.IsInfinity(v))||expectedAreaMm2<=0||thicknessMm<=0||(cleanedDxfMmPerUnit!=1000&&cleanedDxfMmPerUnit!=1)||sourceUnitsToMm<=0)throw new InvalidOperationException("Explicit finite area, thickness and drawing units are required.");
                if(Math.Abs(axisEdge.start[1]-axisEdge.end[1])>0.01||Math.Abs(axisEdge.start[0]-axisEdge.end[0])<1)throw new InvalidOperationException("Selected X edge must be a real horizontal source edge to preserve source Y.");
            }
        }
        private static PreviewParameters ZoneParameters(SlabGeometrySnapshot slab,RebarBarType type,CadPlaneGroup group,IEnumerable<string> colors)=>new PreviewParameters
          {AllowBarIntersections=true,IncludeZones=true,CalculationAxesConfirmed=true,DemandMeaning="Manual",Direction="Низ X",ConcreteClass="B25",SourceGroupKey=group.Key,EdgeCoverMm=slab.SideCoverMm,RoundIncrementMm=1,Selections=colors.Select(c=>new PreviewSelection{ColorKey=c,SpecKey=type.UniqueId,SpacingMm=200}).ToList()};
        private static PreviewParameters BackgroundParameters(SlabGeometrySnapshot slab,RebarBarType type,string variant)=>new PreviewParameters
        {AllowBarIntersections=true,IncludeZones=false,Direction="Низ X",ConcreteClass="B25",EdgeCoverMm=slab.SideCoverMm,
            BackgroundLayers=variant.StartsWith("background")?new[]{"Низ X","Низ Y","Верх X","Верх Y"}.Select(d=>new PreviewBackgroundLayer{Enabled=true,Direction=d,SpecKey=type.UniqueId,SpacingMm=200}).ToList():new List<PreviewBackgroundLayer>(),
            Perimeter=new PreviewPerimeterOptions{Enabled=variant!="background",Outer=true,Openings=true,UseUbars=true,SpecKey=type.UniqueId,SpacingMm=200,LegLengthMm=500}};
        private ElementId CreateBaselineView(Document doc,Floor floor,ImportInstance import)
        {
            using(var t=new Transaction(doc,"Template test baseline view"))
            {t.Start();var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v=>v.ViewFamily==ViewFamily.ThreeDimensional);var view=View3D.CreateIsometric(doc,type.Id);view.Name="Плита — DXF";view.DetailLevel=ViewDetailLevel.Fine;view.DisplayStyle=DisplayStyle.Shading;view.IsolateElementsTemporary(new List<ElementId>{floor.Id,import.Id});view.ConvertTemporaryHideIsolateToPermanent();
                var plan=View3D.CreateIsometric(doc,type.Id);plan.Name="Плита — план";plan.DetailLevel=ViewDetailLevel.Fine;plan.DisplayStyle=DisplayStyle.Shading;plan.IsolateElementsTemporary(new List<ElementId>{floor.Id,import.Id});plan.ConvertTemporaryHideIsolateToPermanent();var box=floor.get_BoundingBox(null);var centre=(box.Min+box.Max)/2;plan.SetOrientation(new ViewOrientation3D(centre+XYZ.BasisZ*100,XYZ.BasisY,-XYZ.BasisZ));SetStartingView(doc,plan.Id);t.Commit();return plan.Id;}
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
