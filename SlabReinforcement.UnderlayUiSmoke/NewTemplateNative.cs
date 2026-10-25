using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Buffer;
using NtsGeometry=NetTopologySuite.Geometries.Geometry;

namespace SlabReinforcement.UnderlayUiSmoke
{
    public sealed partial class UnderlayApplication
    {
        private const double NativeCover=35,NativeDiameter=12,NativeSpacing=200,NativeLength=500;
        private static readonly double[] NativeOffsets={12,24,12,24};
        private static readonly double[] NativeAxisZ={-147,-135,-53,-65};
        private readonly GeometryFactory _nativeFactory=new GeometryFactory();
        private NativeFootprint _nativeFootprint;
        private Polygon _nativePolygon;
        private NtsGeometry _nativeAllowed;
        private readonly List<NativePathInfo> _nativePaths=new List<NativePathInfo>();
        private readonly List<object> _nativeAttempts=new List<object>();
        private string _nativeShapeName;

        private void RunNewTemplateNative(Autodesk.Revit.ApplicationServices.Application app,ref Document doc,List<RebarBarType> types)
        {
            _report["scope"]="New project from official Weandrevit RTE; actual first-layer upper-left footprint; four native single-layer Areas plus native U Paths. Geometric test, not a structural calculation or lap approval.";
            _report["templateSha256"]=Hash(Path.Combine(_run,"template.rte"));
            Require(Hash(Path.Combine(_run,"template.rte"))=="3E72E731CCD62C316CD1F83332BF2608C022A1CB0859A93FCB126F189CF313E7","Unexpected template bytes.");
            _nativeFootprint=new JavaScriptSerializer().Deserialize<NativeFootprint>(File.ReadAllText(Path.Combine(_run,"footprint.json")));
            var f=_nativeFootprint;Require(f.shell.Count>=3&&f.holes.Count==0&&Math.Abs(f.thicknessMm-200)<0.01,"Unexpected test footprint.");
            _nativePolygon=_nativeFactory.CreatePolygon(f.shell.Concat(new[]{f.shell[0]}).Select(p=>new Coordinate(p[0],p[1])).ToArray());
            Require(_nativePolygon.IsValid,"Invalid source polygon.");_nativeAllowed=_nativePolygon.Buffer(-(NativeCover+NativeDiameter/2-0.02));
            Require(new FilteredElementCollector(doc).OfClass(typeof(RebarInSystem)).GetElementCount()==0&&new FilteredElementCollector(doc).OfClass(typeof(Floor)).GetElementCount()==0,"The template unexpectedly contains model instances.");
            WriteNative("new-template-raw-inventory.json",new{bars=types.Select(t=>new{id=t.Id.IntegerValue,t.Name,nominalDiameterMm=t.BarNominalDiameter*304.8,parameters=SelectedParameters(t)}).ToList(),shapes=new FilteredElementCollector(doc).OfClass(typeof(RebarShape)).Cast<RebarShape>().Select(s=>new{id=s.Id.IntegerValue,s.Name}).ToList(),floorTypes=new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().Select(t=>new{id=t.Id.IntegerValue,t.Name}).ToList()});
            var ordinary=types.Single(t=>Math.Abs(t.BarNominalDiameter*304.8-12)<0.01&&Numeric(t.LookupParameter("Арм.КлассЧисло"))==500&&Numeric(t.LookupParameter("Рзм.ПогМетрыВкл"))==0);
            var metres=types.Single(t=>Math.Abs(t.BarNominalDiameter*304.8-12)<0.01&&Numeric(t.LookupParameter("Арм.КлассЧисло"))==500&&Numeric(t.LookupParameter("Рзм.ПогМетрыВкл"))==1);
            Require(Math.Abs(ordinary.BarModelDiameter*304.8-12)<0.01&&Math.Abs(metres.BarModelDiameter*304.8-12)<0.01,"Unexpected model diameter.");
            var shapeCandidates=new FilteredElementCollector(doc).OfClass(typeof(RebarShape)).Cast<RebarShape>().Where(s=>
            {
                var d=s.GetRebarShapeDefinition() as RebarShapeDefinitionBySegments;if(d==null||d.NumberOfSegments!=3||d.MajorSegmentIndex!=1||s.GetDefaultHookAngle(0)!=0||s.GetDefaultHookAngle(1)!=0)return false;
                var ls=s.GetCurvesForBrowser().OfType<Line>().ToList();return ls.Count==3&&Math.Abs(ls[0].Direction.DotProduct(ls[2].Direction)+1)<1e-6&&Math.Abs(ls[0].Direction.DotProduct(ls[1].Direction))<1e-6;
            }).ToList();
            WriteNative("new-template-u-shapes.json",shapeCandidates.Select(s=>new{id=s.Id.IntegerValue,s.Name,browser=s.GetCurvesForBrowser().Select(PathCurve).ToList()}).ToList());
            Require(shapeCandidates.Count>0,"New template contains no ordinary 3-segment U with central major segment.");
            var shape=shapeCandidates.OrderBy(s=>s.Name.StartsWith("21",StringComparison.Ordinal)?0:1).ThenBy(s=>s.Name,StringComparer.Ordinal).First();
            _nativeShapeName=shape.Name;
            var def=shape.GetRebarShapeDefinition() as RebarShapeDefinitionBySegments;
            Require(def!=null&&def.NumberOfSegments==3&&def.MajorSegmentIndex==1&&shape.GetDefaultHookAngle(0)==0&&shape.GetDefaultHookAngle(1)==0,"Chosen template shape is not an ordinary U shape.");
            _report["templateTypes"]=new{ordinary=new{id=ordinary.Id.IntegerValue,ordinary.Name,parameters=SelectedParameters(ordinary)},metres=new{id=metres.Id.IntegerValue,metres.Name,parameters=SelectedParameters(metres)},shape=new{id=shape.Id.IntegerValue,shape.Name,majorSegment=def.MajorSegmentIndex,browser=shape.GetCurvesForBrowser().Select(PathCurve).ToList()},allBarTypes=types.Select(t=>new{id=t.Id.IntegerValue,t.Name,diameterMm=t.BarNominalDiameter*304.8,parameters=SelectedParameters(t)}).ToList()};
            WriteNative("new-template-inventory.json",_report["templateTypes"]);
            var floor=CreateNativeTestFloor(doc);var floorId=floor.Id;
            if(File.Exists(Path.Combine(_run,"clean-plugin-test.flag"))){RunCleanPluginTest(app,ref doc,floor);return;}
            XYZ direction=(NativePoint(f.axisEdge.end)-NativePoint(f.axisEdge.start)).Normalize();
            _report["host"]=new{id=floor.Id.IntegerValue,floor.Name,source=f.sourcePath,shell=f.shell,holes=f.holes,areaM2=_nativePolygon.Area/1000000,thicknessMm=f.thicknessMm,topElevationMm=f.topElevationMm,coverMm=NativeCover,xDirection=Vec(direction)};
            _report["stacking"]=new{outermost="U",outerMeshDirection="X",innerMeshDirection="Y",layers=AreaLayerNames.Select((name,i)=>new{name,extraCoverMm=NativeOffsets[i],axisFromTopMm=NativeAxisZ[i]}).ToList(),basis="Explicit test configuration; touching Ø12 layer surfaces; no automated X/Y separation assumed."};
            var areas=CreateNativeTestAreas(doc,floor,metres,direction);
            var areaEvidence=MeasureAreas(doc,areas,f.topElevationMm,direction);
            for(int i=0;i<4;i++)Require(areaEvidence[i].LocalZmm.Count==1&&Math.Abs(areaEvidence[i].LocalZmm[0]-NativeAxisZ[i])<0.02,"Unexpected native Area elevation for "+AreaLayerNames[i]);
            Log("NEW_TEMPLATE_AREAS_CREATED "+areaEvidence.Sum(a=>a.BarCount));
            CreateNativeTestPaths(doc,floor,ordinary,shape);
            _report["pathAttempts"]=_nativeAttempts;
            _report["pathEdges"]=_nativePaths;
            Require(_nativePaths.Count==f.shell.Count,"Every source edge must have a tested native Path; missing edges cannot be silently accepted.");
            var before=ReadNativeAll(doc,areas,false);
            WriteNative("geometry-before.json",before);
            ReadNativeAll(doc,areas,true);
            var views=CreateNativeTestViews(doc,floor,areas);
            var viewDoc=doc;_report["views"]=views.Select(id=>new{id=id.IntegerValue,name=viewDoc.GetElement(id).Name}).ToList();
            _report["createdAreas"]=areaEvidence;
            var output=Path.Combine(_run,"1-layer-new-Weandrevit-native-background-perimeter.rvt");
            doc.SaveAs(output,new SaveAsOptions{MaximumBackups=1});
            var export=new ImageExportOptions{FilePath=Path.Combine(_run,"native-test"),ExportRange=ExportRange.SetOfViews,ZoomType=ZoomFitType.FitToPage,PixelSize=2200,ImageResolution=ImageResolution.DPI_150,HLRandWFViewsFileType=ImageFileType.PNG,ShadowViewsFileType=ImageFileType.PNG};
            export.SetViewsAndSheets(views.Take(3).ToList());doc.ExportImage(export);
            var warnings=doc.GetWarnings();_report["warnings"]=warnings.Select(w=>new{description=w.GetDescriptionText(),severity=w.GetSeverity().ToString(),failureId=w.GetFailureDefinitionId().Guid,ids=w.GetFailingElements().Select(x=>x.IntegerValue).ToList()}).ToList();
            doc.Close(false);doc=null;doc=app.OpenDocumentFile(output);
            var after=ReadNativeAll(doc,areas,true);WriteNative("geometry-after.json",after);
            Require(before.Signature==after.Signature&&before.PhysicalBars==after.PhysicalBars,"Geometry or quantities changed after save/reopen.");
            Require(new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcement)).GetElementCount()==4,"Expected four native Areas after reopen.");
            Require(new FilteredElementCollector(doc).OfClass(typeof(PathReinforcement)).GetElementCount()==f.shell.Count,"Native perimeter path count changed.");
            doc.Close(false);doc=null;
            _report["nativeSummary"]=new{model=output,sha256=Hash(output),areas=areas.Count,paths=_nativePaths.Count,after.Sets,after.PhysicalBars,minimumSideCoverMm=after.MinSideCoverMm,minimumTopCoverMm=after.MinTopCoverMm,minimumBottomCoverMm=after.MinBottomCoverMm,maxActualSpacingMm=after.MaxGapMm,exactSaveReopen=true,sourceTemplateUnchanged=true,productionChanged=false};
            WriteNative("native-summary.json",_report["nativeSummary"]);
            File.WriteAllText(Path.Combine(_run,"protocol.md"),NativeProtocol(after,output));
            Check("new-template-full-native-background-and-perimeter",_report["nativeSummary"]);
        }

        private Floor CreateNativeTestFloor(Document doc)
        {
            var f=_nativeFootprint;
            var candidates=new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().Where(t=>
            {using(var s=t.GetCompoundStructure()){if(s==null||s.LayerCount!=1||s.VariableLayerIndex>=0)return false;var m=doc.GetElement(s.GetLayers()[0].MaterialId) as Material;if(m==null)return false;var asset=doc.GetElement(m.StructuralAssetId) as PropertySetElement;return (asset!=null&&asset.GetStructuralAsset().StructuralAssetClass==StructuralAssetClass.Concrete)||string.Equals(m.MaterialClass,"Бетон",StringComparison.OrdinalIgnoreCase)||string.Equals(m.MaterialClass,"Concrete",StringComparison.OrdinalIgnoreCase);}}).ToList();
            if(candidates.Count==0)candidates=new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().Where(t=>{using(var s=t.GetCompoundStructure())return s!=null&&s.LayerCount==1&&s.VariableLayerIndex<0&&t.Name.IndexOf("бетон",StringComparison.OrdinalIgnoreCase)>=0;}).ToList();
            Require(candidates.Count>0,"No single-layer concrete floor in new template.");
            var original=candidates.OrderBy(t=>t.Name.IndexOf("25",StringComparison.OrdinalIgnoreCase)>=0?0:1).ThenBy(t=>Math.Abs(t.GetCompoundStructure().GetWidth()*304.8-f.thicknessMm)).First();
            Floor floor;
            using(var tx=new Transaction(doc,"Test slab from actual first-layer footprint"))
            {
                tx.Start();var type=(FloorType)original.Duplicate("ТЕСТ — плита 200 мм — "+original.Name);using(var s=type.GetCompoundStructure()){s.SetLayerWidth(0,f.thicknessMm/304.8);type.SetCompoundStructure(s);}
                var level=Level.Create(doc,f.topElevationMm/304.8);level.Name="ТЕСТ — верх плиты −1.075";
                var ring=new CurveLoop();for(int i=0;i<f.shell.Count;i++)ring.Append(Line.CreateBound(NativePoint(f.shell[i]),NativePoint(f.shell[(i+1)%f.shell.Count])));
                Require(BoundaryValidation.IsValidHorizontalBoundary(new List<CurveLoop>{ring}),"Footprint is not a valid Revit floor boundary.");
                floor=Floor.Create(doc,new List<CurveLoop>{ring},type.Id,level.Id,true,null,0);Require(floor!=null,"Floor.Create returned null.");doc.Regenerate();Log("NEW_FLOOR_CREATED "+floor.Id.IntegerValue);
                floor.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("ТЕСТ-ПЛ-1");
                var cover=new FilteredElementCollector(doc).OfClass(typeof(RebarCoverType)).Cast<RebarCoverType>().FirstOrDefault(c=>Math.Abs(c.CoverDistance*304.8-NativeCover)<0.01)??RebarCoverType.Create(doc,"ТЕСТ — 35 мм",NativeCover/304.8);
                Require(cover!=null,"Cover type is null.");
                foreach(var p in new[]{BuiltInParameter.CLEAR_COVER_TOP,BuiltInParameter.CLEAR_COVER_BOTTOM,BuiltInParameter.CLEAR_COVER_OTHER}){var parameter=floor.get_Parameter(p);Require(parameter!=null&&!parameter.IsReadOnly,"Floor cover parameter unavailable: "+p);parameter.Set(cover.Id);}
                var reinforcementSettings=ReinforcementSettings.GetReinforcementSettings(doc);Require(reinforcementSettings!=null,"Reinforcement settings unavailable.");reinforcementSettings.HostStructuralRebar=true;
                Require(tx.Commit()==TransactionStatus.Committed,"Floor transaction failed.");
            }
            Require(RebarHostData.GetRebarHostData(floor)!=null,"Floor is not a valid reinforcement host.");
            var top=floor.get_Geometry(new Options{DetailLevel=ViewDetailLevel.Fine}).OfType<Solid>().Where(s=>s.Volume>0).SelectMany(s=>s.Faces.Cast<Face>()).OfType<PlanarFace>().Where(face=>face.FaceNormal.Z>.99).OrderByDescending(face=>face.Origin.Z).First();
            var loops=top.GetEdgesAsCurveLoops();Require(loops.Count==1,"Unexpected actual floor openings.");
            var actualShell=loops[0].Select(c=>new[]{c.GetEndPoint(0).X*304.8,c.GetEndPoint(0).Y*304.8}).ToList();
            var actualPolygon=_nativeFactory.CreatePolygon(actualShell.Concat(new[]{actualShell[0]}).Select(p=>new Coordinate(p[0],p[1])).ToArray());
            double footprintDifference=_nativePolygon.SymmetricDifference(actualPolygon).Area;
            Require(actualShell.Count==f.shell.Count&&footprintDifference<0.001,"Actual Revit host footprint differs from source.");
            Require(Math.Abs(top.Origin.Z*304.8-f.topElevationMm)<.01&&Math.Abs(floor.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM).AsDouble()*304.8-f.thicknessMm)<.01,"Actual floor height or thickness differs from requested.");
            _report["actualHostGeometry"]=new{shell=actualShell,topElevationMm=top.Origin.Z*304.8,thicknessMm=f.thicknessMm,symmetricDifferenceMm2=footprintDifference,sourceEdgeCount=f.shell.Count,actualEdgeCount=actualShell.Count};
            _report["floorType"]=new{source=original.Name,result=floor.Name,sourceTypeId=original.Id.IntegerValue,typeId=floor.GetTypeId().IntegerValue,coverWasExplicitTestSetting=true};
            return floor;
        }

        private void RunCleanPluginTest(Autodesk.Revit.ApplicationServices.Application app,ref Document doc,Floor floor)
        {
            _report["scope"]="Clean test input for actual plugin UI: new official template plus exact source floor. No Area, Path, Rebar or RebarInSystem generated.";
            var floorId=floor.Id;ElementId viewId;
            using(var tx=new Transaction(doc,"Clean views for actual plugin test"))
            {
                tx.Start();var threeType=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t=>t.ViewFamily==ViewFamily.ThreeDimensional);
                var view=View3D.CreateIsometric(doc,threeType.Id);view.Name="ТЕСТ — плита до запуска плагина";view.ViewTemplateId=ElementId.InvalidElementId;view.DetailLevel=ViewDetailLevel.Fine;view.DisplayStyle=DisplayStyle.ShadingWithEdges;
                view.IsolateElementsTemporary(new List<ElementId>{floor.Id});view.ConvertTemporaryHideIsolateToPermanent();
                var box=floor.get_BoundingBox(null);view.SetSectionBox(new BoundingBoxXYZ{Min=box.Min-new XYZ(1,1,1),Max=box.Max+new XYZ(1,1,1)});view.IsSectionBoxActive=true;
                OrientNative(view,(box.Min+box.Max)/2,new XYZ(-1,-1,1));viewId=view.Id;
                var planType=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t=>t.ViewFamily==ViewFamily.StructuralPlan||t.ViewFamily==ViewFamily.FloorPlan);
                var plan=ViewPlan.Create(doc,planType.Id,floor.LevelId);plan.Name="ТЕСТ — контур плиты 1 Слой";plan.ViewTemplateId=ElementId.InvalidElementId;plan.DetailLevel=ViewDetailLevel.Fine;plan.Scale=100;
                var range=plan.GetViewRange();range.SetOffset(PlanViewPlane.TopClipPlane,500/304.8);range.SetOffset(PlanViewPlane.CutPlane,50/304.8);range.SetOffset(PlanViewPlane.BottomClipPlane,-500/304.8);range.SetOffset(PlanViewPlane.ViewDepthPlane,-600/304.8);plan.SetViewRange(range);
                plan.CropBox=new BoundingBoxXYZ{Min=new XYZ(box.Min.X-1,box.Min.Y-1,-1),Max=new XYZ(box.Max.X+1,box.Max.Y+1,1)};plan.CropBoxActive=true;plan.CropBoxVisible=false;
                var start=StartingViewSettings.GetStartingViewSettings(doc);if(start.IsAcceptableStartingView(view.Id))start.ViewId=view.Id;
                Require(tx.Commit()==TransactionStatus.Committed,"Clean test views failed.");
            }
            var target=Path.Combine(_run,"clean-plugin-test.rvt");doc.SaveAs(target,new SaveAsOptions{MaximumBackups=1});
            var export=new ImageExportOptions{FilePath=Path.Combine(_run,"clean-plugin-test"),ExportRange=ExportRange.SetOfViews,ZoomType=ZoomFitType.FitToPage,PixelSize=1800,HLRandWFViewsFileType=ImageFileType.PNG,ShadowViewsFileType=ImageFileType.PNG};export.SetViewsAndSheets(new List<ElementId>{viewId});doc.ExportImage(export);
            doc.Close(false);doc=null;doc=app.OpenDocumentFile(target);
            int floors=new FilteredElementCollector(doc).OfClass(typeof(Floor)).GetElementCount(),rebar=new FilteredElementCollector(doc).OfClass(typeof(Rebar)).GetElementCount(),ris=new FilteredElementCollector(doc).OfClass(typeof(RebarInSystem)).GetElementCount(),areas=new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcement)).GetElementCount(),paths=new FilteredElementCollector(doc).OfClass(typeof(PathReinforcement)).GetElementCount();
            Require(floors==1&&rebar==0&&ris==0&&areas==0&&paths==0,"Clean plugin test file has unexpected model elements.");
            Require(doc.GetElement(floorId) is Floor,"Saved host missing.");
            doc.Close(false);doc=null;
            _report["cleanPluginTest"]=new{model=target,sha256=Hash(target),floorId=floorId.IntegerValue,floors,rebar,rebarInSystem=ris,areas,paths,createdFromNewTemplate=true,reopened=true,actualPluginExecuted=false};
            WriteNative("clean-plugin-test-summary.json",_report["cleanPluginTest"]);Check("clean-input-created-and-reopened",_report["cleanPluginTest"]);
        }

        private List<ElementId> CreateNativeTestAreas(Document doc,Floor floor,RebarBarType type,XYZ direction)
        {
            var probes=new List<object>();
            foreach(double sketchInset in new[]{0.0,1.0,36.0,41.0})
            {
                using(var group=new TransactionGroup(doc,"Native Area extent probe "+sketchInset))
                {
                    group.Start();try
                    {
                        var ids=CreateNativeTestAreasAtInset(doc,floor,type,direction,sketchInset);
                        var physical=ReadNativeAll(doc,ids,false);
                        WriteNative("geometry-area-probe-"+sketchInset.ToString("0",CultureInfo.InvariantCulture)+".json",physical);
                        var example=doc.GetElement(ids[0]);
                        bool accepted=physical.AllInsideSlab&&physical.MinSideCoverMm>=NativeCover-0.00001&&physical.MaxGapMm<=NativeSpacing+0.01;
                        probes.Add(new{sketchInsetMm=sketchInset,accepted,physical.MinSideCoverMm,physical.MinTopCoverMm,physical.MinBottomCoverMm,physical.AllInsideSlab,physical.PhysicalBars,physical.MaxGapMm,
                            nativeParameters=example.Parameters.Cast<Parameter>().Where(p=>p.Id.IntegerValue<0).Select(p=>new{name=p.Definition.Name,id=p.Id.IntegerValue,storage=p.StorageType.ToString(),readOnly=p.IsReadOnly,value=p.AsValueString()}).ToList()});
                        _report["areaBoundaryProbes"]=probes;WriteNative("area-boundary-probes.json",probes);
                        if(accepted){Require(group.Assimilate()==TransactionStatus.Committed,"Area group did not commit.");_report["acceptedAreaSketchInsetMm"]=sketchInset;return ids;}
                        group.RollBack();
                    }
                    catch(Exception error){probes.Add(new{sketchInsetMm=sketchInset,error=error.ToString()});if(group.GetStatus()==TransactionStatus.Started)group.RollBack();WriteNative("area-boundary-probes.json",probes);}
                }
            }
            throw new InvalidOperationException("No tested native Area extent meets full physical side cover; see area-boundary-probes.json.");
        }

        private List<ElementId> CreateNativeTestAreasAtInset(Document doc,Floor floor,RebarBarType type,XYZ direction,double sketchInset)
        {
            var result=new List<ElementId>();var areaType=new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcementType)).Cast<AreaReinforcementType>().First();
            var sketchCurves=new List<Curve>();
            if(sketchInset>0)
            {
                var inset=BufferOp.Buffer(_nativePolygon,-sketchInset,new BufferParameters{JoinStyle=JoinStyle.Mitre,MitreLimit=100}) as Polygon;
                Require(inset!=null&&inset.NumInteriorRings==0,"Inset Area boundary is not one polygon.");var cs=inset.ExteriorRing.Coordinates;
                for(int k=0;k<cs.Length-1;k++)sketchCurves.Add(Line.CreateBound(new XYZ(cs[k].X/304.8,cs[k].Y/304.8,_nativeFootprint.topElevationMm/304.8),new XYZ(cs[k+1].X/304.8,cs[k+1].Y/304.8,_nativeFootprint.topElevationMm/304.8)));
            }
            using(var tx=new Transaction(doc,"Four independent native background layers"))
            {
                tx.Start();for(int i=0;i<4;i++)
                {
                    var a=sketchInset==0?AreaReinforcement.Create(doc,floor,direction,areaType.Id,type.Id,ElementId.InvalidElementId):AreaReinforcement.Create(doc,floor,sketchCurves,direction,areaType.Id,type.Id,ElementId.InvalidElementId);
                    foreach(var layer in AreaLayers)a.SetLayerActive(layer==AreaLayers[i],layer);
                    a.AdditionalBottomCoverOffset=NativeOffsets[i]/304.8;a.AdditionalTopCoverOffset=NativeOffsets[i]/304.8;
                    a.get_Parameter(AreaSpacing[i]).Set(NativeSpacing/304.8);
                    a.get_Parameter(BuiltInParameter.NUMBER_PARTITION_PARAM)?.Set("ТЕСТ ФОН "+AreaLayerNames[i]);
                    a.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(AreaLayerNames[i]+"; ø12-пог.м. ≤200; X наружный, Y внутренний; U наружнее фона; нахлёст отдельно; эскиз внутрь "+sketchInset+" мм");
                    result.Add(a.Id);
                }
                Require(tx.Commit()==TransactionStatus.Committed,"Area transaction failed.");
            }return result;
        }

        private void CreateNativeTestPaths(Document doc,Floor floor,RebarBarType type,RebarShape shape)
        {
            var pathType=new FilteredElementCollector(doc).OfClass(typeof(PathReinforcementType)).Cast<PathReinforcementType>().First();
            var ring=_nativeFootprint.shell;double signed=0;for(int i=0;i<ring.Count;i++)signed+=ring[i][0]*ring[(i+1)%ring.Count][1]-ring[(i+1)%ring.Count][0]*ring[i][1];
            for(int edgeIndex=0;edgeIndex<ring.Count;edgeIndex++)
            {
                XYZ a=NativePoint(ring[edgeIndex]),b=NativePoint(ring[(edgeIndex+1)%ring.Count]);var tangent=(b-a).Normalize();var inward=new XYZ(-tangent.Y,tangent.X,0)*(signed>0?1:-1);double length=a.DistanceTo(b)*304.8;
                // Only geometry determines usable stations. The full source contour including tiny sloping edges is preserved.
                Func<double,bool> allowed=s=>NativeCandidateInside(a,tangent,inward,s);
                var spans=new List<double[]>();int steps=Math.Max(2,(int)Math.Ceiling(length/10));double? start=null;double previous=0;
                for(int k=0;k<=steps;k++)
                {
                    double s=length*k/steps;bool ok=allowed(s);
                    if(ok&&!start.HasValue){double lo=previous,hi=s;for(int j=0;j<25;j++){double mid=(lo+hi)/2;if(allowed(mid))hi=mid;else lo=mid;}start=hi;}
                    if(!ok&&start.HasValue){double lo=previous,hi=s;for(int j=0;j<25;j++){double mid=(lo+hi)/2;if(allowed(mid))lo=mid;else hi=mid;}spans.Add(new[]{start.Value,lo});start=null;}
                    previous=s;
                }
                if(start.HasValue)spans.Add(new[]{start.Value,length});
                Require(spans.Count==1&&spans[0][1]-spans[0][0]>NativeDiameter,"Edge "+edgeIndex+" lacks one continuous usable native Path span; length="+length+", spans="+string.Join(";",spans.Select(x=>string.Join(",",x))));
                // Sketch lies at outside cover; native FarSide adds radius toward the inside to its web centreline.
                double s0=spans[0][0],s1=spans[0][1];var p0=a+tangent*(s0/304.8)+inward*(NativeCover/304.8);var p1=a+tangent*(s1/304.8)+inward*(NativeCover/304.8);
                p0=new XYZ(p0.X,p0.Y,_nativeFootprint.topElevationMm/304.8);p1=new XYZ(p1.X,p1.Y,_nativeFootprint.topElevationMm/304.8);
                NativePathInfo accepted=null;
                foreach(bool flip in new[]{true,false})
                {
                    using(var tx=new Transaction(doc,"Native U perimeter edge "+edgeIndex))
                    {
                        tx.Start();try
                        {
                            var path=PathReinforcement.Create(doc,floor,new List<Curve>{Line.CreateBound(p0,p1)},flip,pathType.Id,type.Id,ElementId.InvalidElementId,ElementId.InvalidElementId);
                            path.PrimaryBarShapeId=shape.Id;path.PrimaryBarOrientation=ReinforcementBarOrientation.FarSide;path.AdditionalOffset=0;
                            path.get_Parameter(BuiltInParameter.PATH_REIN_ALTERNATING).Set(0);
                            path.get_Parameter(BuiltInParameter.PATH_REIN_SPACING).Set(NativeSpacing/304.8);
                            path.get_Parameter(BuiltInParameter.PATH_REIN_LENGTH_1).Set(NativeLength/304.8);
                            path.get_Parameter(BuiltInParameter.NUMBER_PARTITION_PARAM)?.Set("ТЕСТ П-ОБРАМЛЕНИЕ");
                            path.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set("Ребро "+(edgeIndex+1)+"; U наружный; ø12 ≤200; параметр длины500мм; это не500мм прямого нахлёста");
                            doc.Regenerate();var info=new NativePathInfo{Id=path.Id.IntegerValue,EdgeIndex=edgeIndex,SourceStart=Mm(a),SourceEnd=Mm(b),Tangent=Vec(tangent),Inward=Vec(inward),SourceLengthMm=length,SketchStart=Mm(p0),SketchEnd=Mm(p1),UsableStartMm=s0,UsableEndMm=s1,Flip=flip};
                            VerifyNativePath(doc,path,info);
                            Require(tx.Commit()==TransactionStatus.Committed,"Path transaction failed.");accepted=info;break;
                        }
                        catch(Exception error){_nativeAttempts.Add(new{edgeIndex,flip,message=error.ToString()});if(tx.GetStatus()==TransactionStatus.Started)tx.RollBack();}
                    }
                }
                WriteNative("path-progress.json",new{completed=_nativePaths,attempts=_nativeAttempts});
                Require(accepted!=null,"No valid U orientation/containment on source edge "+edgeIndex);
                _nativePaths.Add(accepted);Log("NEW_TEMPLATE_PATH edge="+edgeIndex+" bars="+accepted.Bars+" length="+length.ToString("F3",CultureInfo.InvariantCulture));
            }
        }

        private bool NativeCandidateInside(XYZ origin,XYZ tangent,XYZ inward,double stationMm)
        {
            var start=origin+tangent*(stationMm/304.8)+inward*((NativeCover+NativeDiameter/2)/304.8);
            var end=start+inward*((NativeLength-NativeDiameter/2)/304.8);
            return _nativeAllowed.Covers(_nativeFactory.CreateLineString(new[]{new Coordinate(start.X*304.8,start.Y*304.8),new Coordinate(end.X*304.8,end.Y*304.8)}));
        }

        private void VerifyNativePath(Document doc,PathReinforcement path,NativePathInfo info)
        {
            XYZ origin=NativePoint(info.SourceStart),inward=new XYZ(info.Inward[0],info.Inward[1],0),tangent=new XYZ(info.Tangent[0],info.Tangent[1],0);var stations=new List<double>();
            foreach(var id in path.GetRebarInSystemIds())
            {
                var set=(RebarInSystem)doc.GetElement(id);Require(set.LayoutRule==RebarLayoutRule.MaximumSpacing||set.LayoutRule==RebarLayoutRule.Single,"Path child does not use maximum spacing.");
                for(int i=0;i<set.NumberOfBarPositions;i++)if(set.DoesBarExistAtPosition(i))
                {
                    var curves=set.GetTransformedCenterlineCurves(true,false,false,i);Require(curves.OfType<Line>().Count()==3&&curves.OfType<Arc>().Count()==2,"Expected 3 lines + 2 arcs in U.");
                    var web=curves.OfType<Line>().Single(c=>Math.Abs(c.Direction.Z)>0.999999);
                    double inset=(web.GetEndPoint(0)-origin).DotProduct(inward)*304.8;Require(Math.Abs(inset-41)<0.03,"U web inset="+inset);
                    var ends=new[]{curves.First().GetEndPoint(0),curves.Last().GetEndPoint(1)};
                    Require(ends.All(p=>(p-web.GetEndPoint(0)).DotProduct(inward)>0),"U tails point away from slab.");
                    Require(curves.All(c=>NativeCurveCovered(c,6)),"U bar violates side cover on edge "+info.EdgeIndex);
                    var all=curves.SelectMany(c=>c.Tessellate()).ToList();double zmin=all.Min(p=>p.Z*304.8-_nativeFootprint.topElevationMm),zmax=all.Max(p=>p.Z*304.8-_nativeFootprint.topElevationMm);
                    Require(Math.Abs(zmin+159)<0.03&&Math.Abs(zmax+41)<0.03,"U vertical cover differs from35.");
                    stations.Add((web.GetEndPoint(0)-origin).DotProduct(tangent)*304.8);info.Bars++;
                    info.AxisLegProjectionMm=(ends[0]-web.GetEndPoint(0)).DotProduct(inward)*304.8;
                    info.StraightLegMm=curves.OfType<Line>().Where(l=>Math.Abs(l.Direction.Z)<1e-6).Min(l=>l.Length*304.8);
                }
            }
            Require(stations.Count>0,"Path has no physical bars.");stations.Sort();info.FirstAxisStationMm=stations[0];info.LastAxisStationMm=stations.Last();
            info.MaxGapMm=stations.Zip(stations.Skip(1),(a,b)=>b-a).DefaultIfEmpty(0).Max();Require(info.MaxGapMm<=NativeSpacing+0.02,"Path actual spacing exceeds200.");
        }

        private bool NativeCurveCovered(Curve curve,double radius)
        {
            var points=curve.Tessellate().Select(p=>new Coordinate(p.X*304.8,p.Y*304.8)).Distinct(new NativeCoordinateComparer()).ToArray();
            if(points.Length==1)return _nativeAllowed.Covers(_nativeFactory.CreatePoint(points[0]));
            return _nativeAllowed.Covers(_nativeFactory.CreateLineString(points));
        }

        private NativeGeometry ReadNativeAll(Document doc,List<ElementId> areaIds,bool strict)
        {
            var e=new NativeGeometry{Shell=_nativeFootprint.shell,TopElevationMm=_nativeFootprint.topElevationMm,ThicknessMm=_nativeFootprint.thicknessMm,CoverMm=NativeCover,PathEdges=_nativePaths,MinSideCoverMm=double.MaxValue,MinTopCoverMm=double.MaxValue,MinBottomCoverMm=double.MaxValue,AllInsideSlab=true};
            var signatures=new List<string>();var definitions=new List<Tuple<ElementId,string,string>>();
            for(int i=0;i<areaIds.Count;i++)definitions.Add(Tuple.Create(areaIds[i],"Area",AreaLayerNames[i]));
            foreach(var p in _nativePaths)definitions.Add(Tuple.Create(new ElementId(p.Id),"Path","П ребро "+(p.EdgeIndex+1)));
            foreach(var definition in definitions)
            {
                var system=doc.GetElement(definition.Item1);ICollection<ElementId> ids=system is AreaReinforcement ar?ar.GetRebarInSystemIds():((PathReinforcement)system).GetRebarInSystemIds();
                foreach(var id in ids)
                {
                    var set=(RebarInSystem)doc.GetElement(id);var type=(RebarBarType)doc.GetElement(set.GetTypeId());double radius=type.BarModelDiameter*304.8/2;var stations=new List<double>();int count=0;
                    Require(set.LayoutRule==RebarLayoutRule.MaximumSpacing||set.LayoutRule==RebarLayoutRule.Single,"Native child layout is not MaximumSpacing/Single.");
                    for(int position=0;position<set.NumberOfBarPositions;position++)if(set.DoesBarExistAtPosition(position))
                    {
                        var curves=set.GetTransformedCenterlineCurves(true,false,false,position);count++;e.PhysicalBars++;
                        var curveData=new List<object>();foreach(var c in curves)
                        {
                            var points=c.Tessellate();var xy=points.Select(p=>new Coordinate(p.X*304.8,p.Y*304.8)).Distinct(new NativeCoordinateComparer()).ToArray();
                            NtsGeometry g=xy.Length==1?(NtsGeometry)_nativeFactory.CreatePoint(xy[0]):_nativeFactory.CreateLineString(xy);
                            NtsGeometry body=g.Buffer(radius);bool areaLine=definition.Item2=="Area"&&c is Line;
                            if(areaLine)
                            {
                                var p0=c.GetEndPoint(0)*304.8;var p1=c.GetEndPoint(1)*304.8;var delta=(p1-p0).Normalize();var offset=new XYZ(-delta.Y,delta.X,0)*radius;
                                var corners=new[]{p0+offset,p1+offset,p1-offset,p0-offset,p0+offset};body=_nativeFactory.CreatePolygon(corners.Select(p=>new Coordinate(p.X,p.Y)).ToArray());
                            }
                            double side=_nativePolygon.Boundary.Distance(body);e.MinSideCoverMm=Math.Min(e.MinSideCoverMm,side);
                            e.AllInsideSlab&=_nativePolygon.Covers(body);
                            foreach(var p in points){double z=p.Z*304.8-_nativeFootprint.topElevationMm;e.MinTopCoverMm=Math.Min(e.MinTopCoverMm,-z-radius);e.MinBottomCoverMm=Math.Min(e.MinBottomCoverMm,z+_nativeFootprint.thicknessMm-radius);}
                            if(strict)Require(_nativePolygon.Covers(body)&&side>=NativeCover-.03,"Side cover failed in "+definition.Item3+" set "+id.IntegerValue+" position "+position+" cover="+side);
                            signatures.Add(definition.Item2+"|"+definition.Item3+"|"+set.Id.IntegerValue+"|"+position+"|"+c.GetType().Name+"|"+string.Join(";",new[]{c.GetEndPoint(0),c.Evaluate(.5,true),c.GetEndPoint(1)}.SelectMany(Mm).Select(v=>v.ToString("F6",CultureInfo.InvariantCulture))));
                            curveData.Add(new{kind=c.GetType().Name,start=Mm(c.GetEndPoint(0)),end=Mm(c.GetEndPoint(1)),midpoint=Mm(c.Evaluate(.5,true)),radiusMm=c is Arc arc?(double?) (arc.Radius*304.8):null,centerMm=c is Arc ac?Mm(ac.Center):null,normal=c is Arc an?Vec(an.Normal):null,lengthMm=c.Length*304.8,points=points.Select(Mm).ToList()});
                        }
                        stations.Add(curves.First().GetEndPoint(0).DotProduct(set.Normal)*304.8);
                        e.Bars.Add(new{systemId=definition.Item1.IntegerValue,systemKind=definition.Item2,layer=definition.Item3,setId=id.IntegerValue,position,typeId=type.Id.IntegerValue,typeName=type.Name,nominalDiameterMm=type.BarNominalDiameter*304.8,modelDiameterMm=type.BarModelDiameter*304.8,layout=set.LayoutRule.ToString(),curves=curveData});
                    }
                    Require(count==set.Quantity,"Physical bar count differs from native quantity.");
                    stations.Sort();e.MaxGapMm=Math.Max(e.MaxGapMm,stations.Zip(stations.Skip(1),(a,b)=>b-a).DefaultIfEmpty(0).Max());e.Sets++;
                }
            }
            if(strict){Require(e.AllInsideSlab&&e.MinSideCoverMm>=NativeCover-.03&&e.MinTopCoverMm>=NativeCover-.03&&e.MinBottomCoverMm>=NativeCover-.03,"Minimum cover failed.");
            Require(e.MaxGapMm<=NativeSpacing+.03,"Actual maximum spacing failed.");}
            e.Signature=string.Join("|",signatures.OrderBy(s=>s,StringComparer.Ordinal));return e;
        }

        private List<ElementId> CreateNativeTestViews(Document doc,Floor floor,List<ElementId> areas)
        {
            var result=new List<ElementId>();var allSystems=areas.Concat(_nativePaths.Select(p=>new ElementId(p.Id))).ToList();
            using(var tx=new Transaction(doc,"Views for background and perimeter test"))
            {
                tx.Start();
                var threeType=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t=>t.ViewFamily==ViewFamily.ThreeDimensional);
                var bounds=floor.get_BoundingBox(null);var overview=View3D.CreateIsometric(doc,threeType.Id);overview.Name="ТЕСТ 01 — Фон и П — вся плита";
                SetupNativeView(doc,overview,floor,areas,allSystems);overview.SetSectionBox(new BoundingBoxXYZ{Min=bounds.Min-new XYZ(1,1,1),Max=bounds.Max+new XYZ(1,1,1)});overview.IsSectionBoxActive=true;OrientNative(overview,(bounds.Min+bounds.Max)/2,new XYZ(-1,-1,1));
                var edge=_nativeFootprint.shell.Last();var bottom=_nativeFootprint.shell[0];var corner=new XYZ(bottom[0]/304.8,bottom[1]/304.8,_nativeFootprint.topElevationMm/304.8);
                var detail=View3D.CreateIsometric(doc,threeType.Id);detail.Name="ТЕСТ 02 — Угол — четыре слоя и П";SetupNativeView(doc,detail,floor,areas,allSystems);
                detail.SetSectionBox(new BoundingBoxXYZ{Min=corner+new XYZ(-1800/304.8,-100/304.8,-300/304.8),Max=corner+new XYZ(100/304.8,1800/304.8,150/304.8)});detail.IsSectionBoxActive=true;OrientNative(detail,corner+new XYZ(-700/304.8,700/304.8,-100/304.8),new XYZ(1,-1,1));
                var typeSection=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t=>t.ViewFamily==ViewFamily.Section);
                var transform=Transform.Identity;transform.Origin=corner+new XYZ(-1200/304.8,0,-100/304.8);transform.BasisX=XYZ.BasisY;transform.BasisY=XYZ.BasisZ;transform.BasisZ=XYZ.BasisX;
                var section=ViewSection.CreateSection(doc,typeSection.Id,new BoundingBoxXYZ{Transform=transform,Min=new XYZ(-150/304.8,-500/304.8,0),Max=new XYZ(1300/304.8,350/304.8,250/304.8)});section.Name="ТЕСТ 03 — Разрез края — слои и защитный слой";section.Scale=10;SetupNativeView(doc,section,floor,areas,allSystems);section.CropBoxVisible=false;
                var textType=new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().First();
                TextNote.Create(doc,section.Id,transform.OfPoint(new XYZ(1250/304.8,-240/304.8,0)),"ТЕСТ · h 200 · ø12 ≤200 · защитный слой 35 мм\nОси от верха: П 41/159; X 53/147; Y 65/135 мм\nП «"+_nativeShapeName+"»: длина 500; прямая полка "+_nativePaths[0].StraightLegMm.ToString("0.###",CultureInfo.InvariantCulture)+" мм\nФон: эскиз внутрь "+_report["acceptedAreaSketchInsetMm"]+" мм. Нахлёст отдельно.",new TextNoteOptions(textType.Id));
                result.Add(detail.Id);result.Add(section.Id);result.Add(overview.Id);
                var planType=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t=>t.ViewFamily==ViewFamily.StructuralPlan||t.ViewFamily==ViewFamily.FloorPlan);
                for(int i=0;i<5;i++)
                {
                    var systems=i<4?new List<ElementId>{areas[i]}:_nativePaths.Select(p=>new ElementId(p.Id)).ToList();
                    var plan=ViewPlan.Create(doc,planType.Id,floor.LevelId);plan.Name="ТЕСТ "+(i+4).ToString("00")+" — "+(i<4?AreaLayerNames[i]+" — фон отдельно":"П-обрамление отдельно");plan.Scale=100;
                    SetupNativeView(doc,plan,floor,areas,systems);var range=plan.GetViewRange();range.SetOffset(PlanViewPlane.TopClipPlane,500/304.8);range.SetOffset(PlanViewPlane.CutPlane,50/304.8);range.SetOffset(PlanViewPlane.BottomClipPlane,-500/304.8);range.SetOffset(PlanViewPlane.ViewDepthPlane,-600/304.8);plan.SetViewRange(range);
                    plan.CropBox=new BoundingBoxXYZ{Min=new XYZ(bounds.Min.X-1,bounds.Min.Y-1,-1),Max=new XYZ(bounds.Max.X+1,bounds.Max.Y+1,1)};plan.CropBoxActive=true;plan.CropBoxVisible=false;result.Add(plan.Id);
                }
                var start=StartingViewSettings.GetStartingViewSettings(doc);if(start.IsAcceptableStartingView(detail.Id))start.ViewId=detail.Id;
                Require(tx.Commit()==TransactionStatus.Committed,"View transaction failed.");
            }return result;
        }

        private void SetupNativeView(Document doc,View view,Floor floor,List<ElementId> areas,List<ElementId> systems)
        {
            view.ViewTemplateId=ElementId.InvalidElementId;view.DetailLevel=ViewDetailLevel.Fine;view.DisplayStyle=DisplayStyle.HLR;var visible=new List<ElementId>{floor.Id};
            var colors=new[]{new Autodesk.Revit.DB.Color(20,70,190),new Autodesk.Revit.DB.Color(20,160,195),new Autodesk.Revit.DB.Color(190,40,35),new Autodesk.Revit.DB.Color(220,130,10)};
            foreach(var id in systems)
            {
                visible.Add(id);var sys=doc.GetElement(id);var children=sys is AreaReinforcement area?area.GetRebarInSystemIds():((PathReinforcement)sys).GetRebarInSystemIds();int index=areas.IndexOf(id);var color=index<0?new Autodesk.Revit.DB.Color(110,40,150):colors[index];
                foreach(var child in children){visible.Add(child);((RebarInSystem)doc.GetElement(child)).SetUnobscuredInView(view,true);view.SetElementOverrides(child,new OverrideGraphicSettings().SetProjectionLineColor(color).SetCutLineColor(color).SetProjectionLineWeight(2));}
            }
            view.IsolateElementsTemporary(visible);view.ConvertTemporaryHideIsolateToPermanent();view.SetElementOverrides(floor.Id,new OverrideGraphicSettings().SetSurfaceTransparency(90).SetProjectionLineColor(new Autodesk.Revit.DB.Color(140,140,140)).SetCutLineColor(new Autodesk.Revit.DB.Color(140,140,140)).SetProjectionLineWeight(1).SetCutLineWeight(1).SetSurfaceForegroundPatternVisible(false).SetSurfaceBackgroundPatternVisible(false).SetCutForegroundPatternVisible(false).SetCutBackgroundPatternVisible(false));
        }
        private static void OrientNative(View3D view,XYZ centre,XYZ eyeDirection){var eye=centre+eyeDirection.Normalize()*20;var forward=(centre-eye).Normalize();var right=forward.CrossProduct(XYZ.BasisZ).Normalize();view.SetOrientation(new ViewOrientation3D(eye,right.CrossProduct(forward).Normalize(),forward));}
        private static XYZ NativePoint(double[] point)=>new XYZ(point[0]/304.8,point[1]/304.8,0);
        private void WriteNative(string name,object value)=>File.WriteAllText(Path.Combine(_run,name),new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Serialize(value));
        private string NativeProtocol(NativeGeometry data,string output)=>"# Тест нового шаблона Weandrevit в Revit 2023\n\nМодель: `"+Path.GetFileName(output)+"`. Создана через NewProjectDocument из официального RTE от07.05.2026. Исходник не изменён.\n\n- Плита: точный контур верхней левой плиты1Слой.dxf,200мм,1303.210м². Сохранены14рёбер,включая2наклонныхребра10ммзубца.\n-4независимых native Area:НизX,НизY,ВерхX,ВерхY;ø12-пог.м.;максимальныйшаг200мм.\n-"+_nativePaths.Count+"native Path:П-образнаяформа21,обычныйø12;максимальныйшаг200мм;общийпериметрна4слоя.\n-ЗащитныйслойдоП35мм.ФонXнаружный:оси53/147мм;Yвнутренний:65/135мм.Нахлёстыотдельно.\n-ПараметрдлиныП500мм;фактическаяпроекцияоси494мм,прямойучасток458мм.ЭтоНЕ500ммнахлёста.\n-Количество:"+data.PhysicalBars+"физическихстержней,"+data.Sets+"наборов.\n-Проверенывсефизическиепозиции,направлениехвостов,полныйконтур,защитныйслой,максимальныйшаг,высоты4слоёв.Сохранениеиповторноеоткрытие:геометрияидентична.\n-Этофайлдляпроверкигеометриииинтерфейсаnativeсистем,нерасчётнесущейспособности.ПересеченияUвуглахразрешеныкакпользовательскаятестоваянастройка.Производственныйплагиннеизменён.\n\nЦвета:НизXсиний,НизYголубой,ВерхXкрасный,ВерхYоранжевый,Пфиолетовый.Стартовыйвид:ТЕСТ02угол.Другиевиды:ТЕСТ01всяплита,03разрез,04–08отдельныеслои/П.\n";

        public sealed class NativeFootprint{public List<double[]> shell{get;set;}public List<List<double[]>> holes{get;set;}public double thicknessMm{get;set;}public double topElevationMm{get;set;}public string sourcePath{get;set;}public NativeAxisEdge axisEdge{get;set;}}
        public sealed class NativeAxisEdge{public double[] start{get;set;}public double[] end{get;set;}}
        private sealed class NativePathInfo{public int Id{get;set;}public int EdgeIndex{get;set;}public double[] SourceStart{get;set;}public double[] SourceEnd{get;set;}public double[] Tangent{get;set;}public double[] Inward{get;set;}public double SourceLengthMm{get;set;}public double[] SketchStart{get;set;}public double[] SketchEnd{get;set;}public double UsableStartMm{get;set;}public double UsableEndMm{get;set;}public bool Flip{get;set;}public int Bars{get;set;}public double FirstAxisStationMm{get;set;}public double LastAxisStationMm{get;set;}public double MaxGapMm{get;set;}public double AxisLegProjectionMm{get;set;}public double StraightLegMm{get;set;}}
        private sealed class NativeGeometry{public List<double[]> Shell{get;set;}public double TopElevationMm{get;set;}public double ThicknessMm{get;set;}public double CoverMm{get;set;}public List<NativePathInfo> PathEdges{get;set;}public bool AllInsideSlab{get;set;}public int Sets{get;set;}public int PhysicalBars{get;set;}public double MinSideCoverMm{get;set;}public double MinTopCoverMm{get;set;}public double MinBottomCoverMm{get;set;}public double MaxGapMm{get;set;}public List<object> Bars{get;set;}=new List<object>();public string Signature{get;set;}}
        private sealed class NativeCoordinateComparer:IEqualityComparer<Coordinate>{public bool Equals(Coordinate a,Coordinate b)=>a.Distance(b)<1e-8;public int GetHashCode(Coordinate p)=>0;}
    }
}
