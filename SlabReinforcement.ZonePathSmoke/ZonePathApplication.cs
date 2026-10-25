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

namespace SlabReinforcement.ZonePathSmoke
{
    public sealed partial class ZonePathApplication : IExternalApplication
    {
        private static readonly string Bin = Path.GetDirectoryName(typeof(ZonePathApplication).Assembly.Location);
        private string _run;
        private readonly List<object> _cases = new List<object>();
        private readonly Dictionary<string, object> _report = new Dictionary<string, object>();
        public Result OnStartup(UIControlledApplication app)
        {
            _run = File.ReadAllText(Path.Combine(Bin, "run-directory.txt")).Trim();
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var file = Path.Combine(Bin, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
            app.Idling += Run;
            app.DialogBoxShowing += (s, e) => Log("DIALOG " + e.DialogId + " " + (e is TaskDialogShowingEventArgs t ? t.Message : ""));
            return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication app) { Log("SHUTDOWN"); return Result.Succeeded; }
        private void Run(object sender, IdlingEventArgs args)
        {
            var ui = (UIApplication)sender; ui.Idling -= Run;
            Document doc = null;
            try
            {
                doc = ui.Application.OpenDocumentFile(Path.Combine(_run, "input.rvt"));
                var original = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().First();
                Floor floor;
                using (var tx = new Transaction(doc, "Create isolated rectangular test slab"))
                {
                    tx.Start();
                    var pts = new[] { new XYZ(0,0,0), new XYZ(8000/304.8,0,0), new XYZ(8000/304.8,6000/304.8,0), new XYZ(0,6000/304.8,0) };
                    var loop = new CurveLoop();
                    for (int i=0;i<4;i++) loop.Append(Line.CreateBound(pts[i], pts[(i+1)%4]));
                    floor = Floor.Create(doc, new List<CurveLoop>{loop}, original.GetTypeId(), original.LevelId, true, null, 0);
                    floor.get_Parameter(BuiltInParameter.ALL_MODEL_MARK).Set("ППм.Тест-33.33");
                    ReinforcementSettings.GetReinforcementSettings(doc).HostStructuralRebar = true;
                    tx.Commit();
                }
                var slab = SlabGeometryReader.Read(doc, floor, FindEdge(floor));
                var type = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>()
                    .Where(t => Math.Abs(t.BarNominalDiameter*304.8-12)<0.001)
                    .OrderBy(t => t.Name.Contains("240") ? 1 : 0).ThenBy(t => t.Name.Contains("пог") ? 1 : 0).First();
                _report["slab"] = new { slab.ThicknessMm, slab.TopCoverMm, slab.BottomCoverMm, slab.SideCoverMm, type=type.Name };
                if (File.Exists(Path.Combine(_run, "production-path-bands.flag")))
                {
                    RunProductionPathBands(ui.Application, ref doc, slab, type);
                    _report["status"] = "COMPLETED";
                    return;
                }
                if (File.Exists(Path.Combine(_run, "path-boundary-probe.flag")))
                {
                    RunPathBoundaryProbe(ui.Application, ref doc, slab, type);
                    _report["status"] = "COMPLETED";
                    return;
                }
                var nameValidation = typeof(NumberingSchema).GetMethod("IsValidPartitionName");
                _report["partitionNames"] = new[] { "низ X; верх X; П-обрамление", "низ X верх X П-обрамление", "низ X фон" }.Select(name =>
                {
                    var values = new object[] { name, null };
                    bool valid = (bool)nameValidation.Invoke(null, values);
                    return new { name, valid, message = values[1] };
                }).ToList();
                Case("native-reader-and-parameters", () => ProbeNative(doc, slab, type));
                foreach (string direction in new[]{"Низ X","Низ Y","Верх X","Верх Y"})
                {
                    string selected = direction;
                    Case("writer-"+selected, () => TestWriter(doc, slab, type, selected, 7));
                    Case("writer-two-bars-"+selected, () => TestWriter(doc, slab, type, selected, 2));
                    Case("automatic-zone-ends-"+selected, () => TestAutomaticZoneEnds(doc, slab, type, selected));
                }
                Case("writer-rejects-singleton", () => TestSingletonRejected(doc, slab, type));
                Case("rotated-frame", () => TestRotated(doc, slab, type));
                Case("opening-preserved", () => TestOpening(doc, slab, type));
                Case("background-and-sequential-native-zones", () => TestSession(doc, slab, type));
                RunCreationScopeCases(doc, slab, type);
                RunProductionPathBandRegression(doc, slab, type);
                RunLineMaximumCases(doc, slab, type);
                RunPerimeterAfterZonesCases(doc, slab, type);
                foreach (string mode in new[] { "none", "Низ X", "Низ Y", "both" })
                {
                    string selected = mode;
                    Case("perimeter-and-drawing-filters-" + selected, () => TestPerimeterAndFilters(doc, slab, type, selected));
                }
                _report["status"] = "COMPLETED";
            }
            catch (Exception e) { _report["status"]="FAILED"; _report["exception"]=e.ToString(); Log(e.ToString()); }
            finally
            {
                if(doc!=null && doc.IsValidObject) doc.Close(false);
                _report["cases"]=_cases;
                File.WriteAllText(Path.Combine(_run,"results.json"),new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Serialize(_report));
                File.WriteAllText(Path.Combine(_run,"completed.flag"),_report["status"].ToString());
                ui.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit));
            }
        }
        private object ProbeNative(Document doc, SlabGeometrySnapshot slab, RebarBarType type)
        {
            using(var group=new TransactionGroup(doc,"Native Path probe rollback"))
            {
                group.Start(); PathReinforcement path;
                using(var tx=new Transaction(doc,"Native Path parameter probe"))
                {
                    tx.Start();
                    var pt=new FilteredElementCollector(doc).OfClass(typeof(PathReinforcementType)).FirstElement();
                    double x=slab.Slabs.SelectMany(p=>p.Shell).Min(p=>p.X)+1000,y=slab.Slabs.SelectMany(p=>p.Shell).Min(p=>p.Y)+1000;
                    path=PathReinforcement.Create(doc,slab.Host,new List<Curve>{Line.CreateBound(World(slab,x,y,0),World(slab,x,y+1200,0))},false,pt.Id,type.Id,ElementId.InvalidElementId,ElementId.InvalidElementId);
                    path.get_Parameter(BuiltInParameter.PATH_REIN_FACE_SLAB).Set(1);
                    path.get_Parameter(BuiltInParameter.PATH_REIN_ALTERNATING).Set(0);
                    path.get_Parameter(BuiltInParameter.PATH_REIN_LENGTH_1).Set(2000/304.8);
                    path.get_Parameter(BuiltInParameter.PATH_REIN_SPACING).Set(200/304.8);
                    tx.Commit();
                }
                _report["nativeParameters"] = path.Parameters.Cast<Parameter>().Select(p=>new {name=p.Definition.Name,id=p.Id.IntegerValue,kind=p.StorageType.ToString(),readOnly=p.IsReadOnly,value=p.AsValueString(),number=p.StorageType==StorageType.Integer?(double?)p.AsInteger():p.StorageType==StorageType.Double?p.AsDouble():null}).ToList();
                _report["nativePositions"] = RawBars(doc,slab,path);
                WriteProgress();
                var ids=new HashSet<ElementId>(path.GetRebarInSystemIds());
                var actual=ExistingRebarReader.Read(slab,ids.Contains);
                Require(actual.Count>1,"Native system positions were not read.");
                Require(ExistingRebarReader.Read(slab,id=>false).Count==0,"Physical include filter was not honored.");
                var result=new {children=ids.Count,bars=actual.Count};
                group.RollBack(); return result;
            }
        }
        private object TestWriter(Document doc, SlabGeometrySnapshot slab, RebarBarType type, string direction, int count)
        {
            using(var operation=new TransactionGroup(doc,"Writer fixture rollback"))
            {
                operation.Start();
                bool x=direction.EndsWith("X"),top=direction.StartsWith("Верх");
                double z=BarElevation.Calculate(top,slab.ThicknessMm,top?slab.TopCoverMm:slab.BottomCoverMm,top?slab.BottomCoverMm:slab.TopCoverMm,type.BarModelDiameter*304.8,x?0:type.BarModelDiameter*304.8);
                var placement=new BarPlacement{TypeKey=type.UniqueId,Component="Zones",ElevationMm=z,ModelDiameterMm=type.BarModelDiameter*304.8,Distribution=x?new Point3(0,1,0):new Point3(1,0,0)};
                double ax=slab.Slabs.SelectMany(p=>p.Shell).Min(p=>p.X)+1000,ay=slab.Slabs.SelectMany(p=>p.Shell).Min(p=>p.Y)+1000;
                var bars=new List<PlannedBar>();
                for(int i=0;i<count;i++) bars.Add(new PlannedBar{Start=x?new Point2(ax,ay+i*200):new Point2(ax+i*200,ay),End=x?new Point2(ax+2000,ay+i*200):new Point2(ax+i*200,ay+2000),SpecKey="zone",ColorKey="red",Placement=placement});
                var plan=new ReinforcementPlan{Bars=bars,Groups=new List<BarGroup>{new BarGroup{Start=bars[0].Start,End=bars[0].End,Count=count,SpacingMm=200,SpecKey="zone",ColorKey="red",Placement=placement}}};
                var parameters=new PreviewParameters{Direction=direction,IncludeZones=true,DemandMeaning="Manual",TopCoverMm=slab.TopCoverMm,BottomCoverMm=slab.BottomCoverMm,EdgeCoverMm=slab.SideCoverMm};
                var session=new ReinforcementSession(slab,new ImportedMeshSnapshot(),new List<RebarBarType>{type});
                var beforeIds=new HashSet<ElementId>(new FilteredElementCollector(doc).OfClass(typeof(PathReinforcement)).ToElementIds());
                RebarPlanWriter.Create(doc,slab,session,plan,parameters);
                var paths=new FilteredElementCollector(doc).OfClass(typeof(PathReinforcement)).Cast<PathReinforcement>().Where(p=>!beforeIds.Contains(p.Id)).ToList();
                Require(paths.Count==1,"Expected one native PathReinforcement, actual "+paths.Count);
                var ids=new HashSet<ElementId>(paths.SelectMany(p=>p.GetRebarInSystemIds()));
                var actual=ExistingRebarReader.Read(slab,ids.Contains);
                Require(actual.Count==count,"Physical position count mismatch: "+actual.Count+" versus "+count);
                Require(ExistingReinforcementMatcher.Match(bars,actual).IsComplete,"Actual Path geometry does not match the plan.");
                string partition = (top ? "верх " : "низ ") + (x ? "X" : "Y") + " доп";
                Require(paths[0].get_Parameter(BuiltInParameter.NUMBER_PARTITION_PARAM).AsString() == partition, "Path partition mismatch.");
                AssertConstructionMark(paths[0], slab.Host);
                foreach (var id in ids)
                {
                    var child = doc.GetElement(id);
                    Require(child.get_Parameter(BuiltInParameter.NUMBER_PARTITION_PARAM).AsString() == partition, "Native child did not inherit partition.");
                    AssertConstructionMark(child, slab.Host);
                }
                var hiddenDirect = TemplateFilter(doc, "Арм стержни прямые доп не IFC");
                if (hiddenDirect != null)
                {
                    Require(!Matches(doc, hiddenDirect, paths[0]), "Direct-bar hiding filter must not hide Path category.");
                    Require(ids.All(id => Matches(doc, hiddenDirect, doc.GetElement(id))), "Zone physical bars must follow template direct-bar hiding rule.");
                    Require(!Matches(doc, TemplateFilter(doc, "Арм Плита Не " + (top ? "верх " : "низ ") + (x ? "X" : "Y")), paths[0]), "Zone hidden on its own directional plan.");
                }
                new ReinforcementSession(slab,new ImportedMeshSnapshot(),new List<RebarBarType>{type});
                var result=new{direction,count,z,paths=paths.Count,children=ids.Count,actual=actual.Count};
                operation.RollBack(); return result;
            }
        }
        private object TestSingletonRejected(Document doc, SlabGeometrySnapshot slab, RebarBarType type)
        {
            int before = new FilteredElementCollector(doc).OfClass(typeof(PathReinforcement)).GetElementCount();
            bool rejected = false;
            try { TestWriter(doc, slab, type, "Низ X", 1); }
            catch (ArgumentException e) when (e.Message.Contains("не меньше двух")) { rejected = true; }
            Require(rejected, "Writer must reject a single-row zone.");
            Require(new FilteredElementCollector(doc).OfClass(typeof(PathReinforcement)).GetElementCount() == before,
                "Rejected single row left a native system behind.");
            return new { rejected, createdPaths = 0 };
        }
        private static object RawBars(Document doc,SlabGeometrySnapshot slab,PathReinforcement path)
        {
            return path.GetRebarInSystemIds().Select(id=>(RebarInSystem)doc.GetElement(id)).Select(r=>new{ id=r.Id.IntegerValue,rule=r.LayoutRule.ToString(),count=r.NumberOfBarPositions,
                bars=Enumerable.Range(0,r.NumberOfBarPositions).Where(r.DoesBarExistAtPosition).Select(i=>r.GetTransformedCenterlineCurves(true,false,false,i).Select(c=>new[]{Local(slab,c.GetEndPoint(0)),Local(slab,c.GetEndPoint(1))}).ToList()).ToList()}).ToList();
        }
        private object TestRotated(Document doc,SlabGeometrySnapshot slab,RebarBarType type)
        {
            using(var operation=new TransactionGroup(doc,"Rotated fixture rollback"))
            {
                operation.Start();
                using(var tx=new Transaction(doc,"Rotate fixture"))
                {tx.Start();ElementTransformUtils.RotateElement(doc,slab.Host.Id,Line.CreateBound(XYZ.Zero,XYZ.BasisZ),37*Math.PI/180);tx.Commit();}
                var rotated=SlabGeometryReader.Read(doc,slab.Host,FindEdge(slab.Host));
                var results=new[]{"Низ X","Низ Y","Верх X","Верх Y"}.Select(d=>TestWriter(doc,rotated,type,d,7)).ToList();
                operation.RollBack();return results;
            }
        }
        private object TestOpening(Document doc,SlabGeometrySnapshot slab,RebarBarType type)
        {
            using(var operation=new TransactionGroup(doc,"Opening fixture rollback"))
            {
                operation.Start();
                double ax=slab.Slabs.SelectMany(p=>p.Shell).Min(p=>p.X),ay=slab.Slabs.SelectMany(p=>p.Shell).Min(p=>p.Y);
                using(var tx=new Transaction(doc,"Add fixture opening"))
                {
                    tx.Start();var loop=new CurveArray();
                    var pts=new[]{World(slab,ax+2500,ay+2000,0),World(slab,ax+3500,ay+2000,0),World(slab,ax+3500,ay+3000,0),World(slab,ax+2500,ay+3000,0)};
                    for(int i=0;i<4;i++)loop.Append(Line.CreateBound(pts[i],pts[(i+1)%4]));
                    doc.Create.NewOpening(slab.Host,loop,true);tx.Commit();
                }
                var opened=SlabGeometryReader.Read(doc,slab.Host,FindEdge(slab.Host));
                Require(opened.Slabs.Sum(p=>p.Holes.Count)>0,"Opening was not read.");
                var plan=ReinforcementPlanner.Build(new PlanRequest{Slabs=opened.Slabs,
                    Faces=new List<FaceInput>{new FaceInput{Id="d",ColorKey="r",Vertices=Rect(ax+1000,ay+1000,ax+5000,ay+4000)}},
                    Specifications=new List<ReinforcementSpec>{new ReinforcementSpec{Key="zone",ColorKey="r",TypeKey=type.UniqueId,MaterialKey="500",DiameterMm=12,ModelDiameterMm=type.BarModelDiameter*304.8,SpacingMm=200}},
                    Options=new PlanOptions{EdgeCoverMm=slab.SideCoverMm+0.05,MergeToleranceMm=600,RoundIncrementMm=1}});
                Require(plan.CanCreate,string.Join(";",plan.Diagnostics.Select(d=>d.Message)));
                var placement=new BarPlacement{TypeKey=type.UniqueId,Component="Zones",ElevationMm=BarElevation.Calculate(false,slab.ThicknessMm,slab.BottomCoverMm,slab.TopCoverMm,type.BarModelDiameter*304.8,0),ModelDiameterMm=type.BarModelDiameter*304.8,Distribution=new Point3(0,1,0)};
                foreach(var bar in plan.Bars)bar.Placement=placement;foreach(var g in plan.Groups)g.Placement=placement;
                var parameters=ZoneParameters(opened,type,"Низ X");
                var session=new ReinforcementSession(opened,new ImportedMeshSnapshot(),new List<RebarBarType>{type});
                RebarPlanWriter.Create(doc,opened,session,plan,parameters);
                var physical=ExistingRebarReader.Read(opened);
                Require(ExistingReinforcementMatcher.Match(plan.Bars,physical).IsComplete,"Opening fixture readback mismatch.");
                var result=new{bars=plan.Bars.Count,groups=plan.Groups.Count,holes=opened.Slabs.Sum(p=>p.Holes.Count)};
                operation.RollBack();return result;
            }
        }
        private object TestSession(Document doc,SlabGeometrySnapshot slab,RebarBarType type)
        {
            using(var operation=new TransactionGroup(doc,"Sequential native zones rollback"))
            {
                operation.Start();
                double ax=slab.Slabs.SelectMany(p=>p.Shell).Min(p=>p.X)+1500,ay=slab.Slabs.SelectMany(p=>p.Shell).Min(p=>p.Y)+1500;
                var mesh=new ImportedMeshSnapshot();
                mesh.Groups.Add(new CadPlaneGroup{Key="map",Faces=new List<FaceInput>{new FaceInput{Id="d",ColorKey="r",Vertices=Rect(ax,ay,ax+2500,ay+2000)}}});
                var parameters=ZoneParameters(slab,type,"Низ X");
                parameters.BackgroundLayers=new[]{"Низ X","Низ Y","Верх X","Верх Y"}.Select(d=>new PreviewBackgroundLayer{Direction=d,Enabled=true,SpecKey=type.UniqueId,SpacingMm=200}).ToList();
                var session=new ReinforcementSession(slab,mesh,new List<RebarBarType>{type});
                var first=session.CreateContext().Calculate(parameters,default);
                Require(first.CanCreate,string.Join(";",first.Diagnostics.Select(d=>d.Message)));
                RebarPlanWriter.Create(doc,slab,session,first,parameters);
                foreach(var layer in parameters.BackgroundLayers)layer.Enabled=false;
                parameters.Direction="Верх Y";
                var nextSession=new ReinforcementSession(slab,mesh,new List<RebarBarType>{type});
                var second=nextSession.CreateContext().Calculate(parameters,default);
                Require(second.CanCreate,string.Join(";",second.Diagnostics.Select(d=>d.Message)));
                Require(second.ReferenceBars.Count>0,"Existing background was not reused.");
                RebarPlanWriter.Create(doc,slab,nextSession,second,parameters);
                var result=new{first=first.Bars.Count,second=second.Bars.Count,reused=second.ReferenceBars.Count,paths=new FilteredElementCollector(doc).OfClass(typeof(PathReinforcement)).Cast<PathReinforcement>().Count(p=>p.GetHostId()==slab.Host.Id)};
                operation.RollBack();return result;
            }
        }
        private static PreviewParameters ZoneParameters(SlabGeometrySnapshot slab,RebarBarType type,string direction)=>new PreviewParameters{
            IncludeZones=true,Direction=direction,ConcreteClass="B25",SourceGroupKey="map",CalculationAxesConfirmed=true,DemandMeaning="Manual",RoundIncrementMm=10,MergeToleranceMm=600,
            TopCoverMm=slab.TopCoverMm,BottomCoverMm=slab.BottomCoverMm,EdgeCoverMm=slab.SideCoverMm,
            Selections=new List<PreviewSelection>{new PreviewSelection{ColorKey="r",SpecKey=type.UniqueId,SpacingMm=200}}};

        private object TestAutomaticZoneEnds(Document doc, SlabGeometrySnapshot slab, RebarBarType type, string direction)
        {
            using (var operation = new TransactionGroup(doc, "Automatic local zone ends rollback"))
            {
                operation.Start();
                bool alongX = direction.EndsWith("X", StringComparison.Ordinal);
                var points = slab.Slabs.SelectMany(s => s.Shell).ToList();
                double minX = points.Min(p=>p.X), maxX=points.Max(p=>p.X), minY=points.Min(p=>p.Y), maxY=points.Max(p=>p.Y);
                var mesh = new ImportedMeshSnapshot();
                mesh.Groups.Add(new CadPlaneGroup { Key="map", Faces=new List<FaceInput> { new FaceInput { Id="zone-at-edge",ColorKey="r",
                    Vertices=alongX ? Rect(maxX-900,minY+1000,maxX-5,minY+2400) : Rect(minX+1000,maxY-900,minX+2400,maxY-5) } } });
                var p=ZoneParameters(slab,type,direction);
                p.BackgroundLayers=new[]{"Низ X","Низ Y","Верх X","Верх Y"}.Select(d=>new PreviewBackgroundLayer {Direction=d,Enabled=false,SpecKey=type.UniqueId,SpacingMm=200}).ToList();
                p.UsePerimeterUForZoneEnds=true; p.AutomaticULegLength=true; p.ZoneEndLegLengthMm=double.NaN;
                p.Perimeter.Enabled=false; p.Perimeter.SpecKey=null;
                var session=new ReinforcementSession(slab,mesh,new List<RebarBarType>{type});
                var plan=session.CreateContext().Calculate(p,default);
                Require(plan.CanCreate,string.Join("; ",plan.Diagnostics.Select(d=>d.Message)));
                var zones=plan.Bars.Where(b=>b.Placement.Component=="Zones").ToList();
                var endings=plan.Bars.Where(b=>b.Placement.Component=="PerimeterU").ToList();
                Require(zones.Count>=2 && endings.Count==zones.Sum(b=>b.EndConnections.Count),"Only final zone endpoints may create local U bars.");
                Require(plan.Bars.All(b=>b.Placement.Component!="Background"),"Automatic zone ends created main background.");
                int created=RebarPlanWriter.Create(doc,slab,session,plan,p);
                var actual=ExistingRebarReader.Read(slab);
                Require(actual.Count==plan.Bars.Count,"Automatic zone ends physical readback count differs.");
                var result=new {direction,zoneBars=zones.Count,localU= endings.Count,created,physical=actual.Count,
                    paths=new FilteredElementCollector(doc).OfClass(typeof(PathReinforcement)).Cast<PathReinforcement>().Count(s=>s.GetHostId()==slab.Host.Id)};
                operation.RollBack(); return result;
            }
        }

        private object TestPerimeterAndFilters(Document doc, SlabGeometrySnapshot slab, RebarBarType type, string mode)
        {
            using (var operation = new TransactionGroup(doc, "Perimeter and template parameters rollback"))
            {
                operation.Start();
                var parameters = ZoneParameters(slab, type, "Низ X");
                parameters.IncludeZones = false;
                parameters.AutomaticULegLength = true;
                parameters.BackgroundLayers = new[] { "Низ X", "Низ Y", "Верх X", "Верх Y" }
                    .Select(d => new PreviewBackgroundLayer { Direction = d, Enabled = mode == "both" || mode == d,
                        SpecKey = type.UniqueId, SpacingMm = d.EndsWith("X") ? 200 : 250 }).ToList();
                parameters.Perimeter = new PreviewPerimeterOptions { Enabled = true, Outer = true, Openings = false,
                    SpecKey = type.UniqueId, SpacingMm = mode == "both" ? double.NaN : 330, LegLengthMm = 500 };
                var session = new ReinforcementSession(slab, new ImportedMeshSnapshot(), new List<RebarBarType> { type });
                var plan = session.CreateContext().Calculate(parameters, default);
                Require(plan.CanCreate, string.Join("; ", plan.Diagnostics.Select(d => d.Message)));
                int manual = plan.PerimeterUEdges.Count(e => !e.FollowsBackgroundRows);
                Require(manual == (mode == "none" ? 4 : mode == "both" ? 0 : 2), "Unexpected independent edge count.");
                RebarPlanWriter.Create(doc, slab, session, plan, parameters);
                var actual = ExistingRebarReader.Read(slab);
                Require(actual.Count == plan.Bars.Count && ExistingReinforcementMatcher.Match(plan.Bars, actual).IsComplete,
                    "Perimeter physical geometry differs from preview.");
                var sets = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().Where(r => r.GetHostId() == slab.Host.Id).ToList();
                var uSets = sets.Where(r => (r.get_Parameter(BuiltInParameter.NUMBER_PARTITION_PARAM)?.AsString() ?? "").Contains("П-обрамление")).ToList();
                Require(uSets.Count > 0, "No U sets have a drawing partition.");
                var oppositePlans = new[] { "низ X", "верх X", "низ Y", "верх Y" };
                int checkedFilters = 0;
                foreach (var set in sets) AssertConstructionMark(set, slab.Host);
                foreach (var set in uSets)
                {
                    string partition = set.get_Parameter(BuiltInParameter.NUMBER_PARTITION_PARAM).AsString();
                    Require(!partition.Contains("доп"), "U set would be hidden as a direct zone bar.");
                    foreach (string direction in oppositePlans)
                    {
                        var filter = TemplateFilter(doc, "Арм Плита Не " + direction);
                        if (filter == null) continue;
                        Require(Matches(doc, filter, set) == !partition.Contains(direction), "U directional filter membership mismatch.");
                        checkedFilters++;
                    }
                    var direct = TemplateFilter(doc, "Арм стержни прямые доп не IFC");
                    if (direct != null) Require(!Matches(doc, direct, set), "Template direct zone filter hides a U set.");
                }
                foreach (var set in sets.Except(uSets))
                    Require((set.get_Parameter(BuiltInParameter.NUMBER_PARTITION_PARAM)?.AsString() ?? "").EndsWith(" фон"), "Background partition missing.");
                var result = new { mode, physicalBars = actual.Count, uSets = uSets.Count, manualEdges = manual,
                    alignedEdges = plan.PerimeterUEdges.Count - manual, checkedDirectionalFilters = checkedFilters,
                    hostMark = slab.Host.get_Parameter(BuiltInParameter.ALL_MODEL_MARK).AsString() };
                operation.RollBack();
                return result;
            }
        }

        private static void AssertConstructionMark(Element element, Element host)
        {
            var parameter = element.get_Parameter(new Guid("5d369dfb-17a2-4ae2-a1a1-bdfc33ba7405"));
            if (parameter != null && parameter.StorageType == StorageType.String && !parameter.IsReadOnly)
                Require(parameter.AsString() == host.get_Parameter(BuiltInParameter.ALL_MODEL_MARK).AsString(), "Optional construction mark does not match the host.");
        }
        private static ParameterFilterElement TemplateFilter(Document doc, string name) =>
            new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>().FirstOrDefault(f => f.Name == name);
        private static bool Matches(Document doc, ParameterFilterElement filter, Element element) =>
            filter != null && element.Category != null && filter.GetCategories().Contains(element.Category.Id)
            && filter.GetElementFilter().PassesFilter(doc, element.Id);
        private static List<Point2> Rect(double a,double b,double c,double d)=>new List<Point2>{new Point2(a,b),new Point2(c,b),new Point2(c,d),new Point2(a,d)};
        private static double[] Local(SlabGeometrySnapshot slab,XYZ p){var v=slab.Frame.ToLocal(new Point3(p.X*304.8,p.Y*304.8,p.Z*304.8));return new[]{v.X,v.Y,v.Z};}
        private static XYZ World(SlabGeometrySnapshot slab,double x,double y,double z)=>SlabGeometryReader.ToXyz(slab.Frame.ToWorld(new Point3(x,y,z)));
        private static Reference FindEdge(Floor floor)
        {
            using(var options=new Options{ComputeReferences=true,DetailLevel=ViewDetailLevel.Fine})
                return floor.get_Geometry(options).OfType<Solid>().SelectMany(s=>s.Edges.Cast<Edge>()).Where(e=>e.Reference!=null && e.AsCurve() is Line l && Math.Abs(l.Direction.Z)<0.01).OrderByDescending(e=>e.AsCurve().Length).First().Reference;
        }
        private void Case(string name,Func<object> action)
        {
            try{var result=action();_cases.Add(new{name,status="PASS",result});Log("PASS "+name);}
            catch(Exception e){_cases.Add(new{name,status="FAILED",error=e.ToString(),details=e.Data.Cast<System.Collections.DictionaryEntry>().ToDictionary(p=>p.Key.ToString(),p=>p.Value?.ToString())});Log("FAILED "+name+" "+e);}
            WriteProgress();
        }
        private void WriteProgress(){_report["cases"]=_cases;File.WriteAllText(Path.Combine(_run,"progress.json"),new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Serialize(_report));}
        private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
        private void Log(string text)=>File.AppendAllText(Path.Combine(_run,"run.log"),DateTime.UtcNow.ToString("O")+" "+text+Environment.NewLine);
    }
}
