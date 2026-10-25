using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace SlabReinforcement.UnderlayUiSmoke
{
    public sealed partial class UnderlayApplication
    {
        private void RunNativePathProbe(Autodesk.Revit.ApplicationServices.Application app,ref Document doc,List<RebarBarType> types)
        {
            _report["scope"]="Bounded native PathReinforcement POC on one 2000 mm straight edge; ordinary template U shape and ø12; copy only, no production or shared-type edits; no anchorage claim.";
            Require(new FilteredElementCollector(doc).OfClass(typeof(Rebar)).GetElementCount()==0&&new FilteredElementCollector(doc).OfClass(typeof(RebarInSystem)).GetElementCount()==0,"Clean input required.");
            var floor=new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().Single();
            var type=types.Single(t=>t.Name=="ø12"&&Math.Abs(t.BarNominalDiameter*304.8-12)<0.01&&Numeric(t.LookupParameter("Рзм.ПогМетрыВкл"))==0);
            var shapes=new FilteredElementCollector(doc).OfClass(typeof(RebarShape)).Cast<RebarShape>().ToList();
            var candidates=new List<RebarShape>();var inventory=new List<object>();
            foreach(var shape in shapes)
            {
                var def=shape.GetRebarShapeDefinition() as RebarShapeDefinitionBySegments;
                if(def==null||def.NumberOfSegments!=3)continue;
                var curves=shape.GetCurvesForBrowser();var lines=curves.OfType<Line>().ToList();
                bool isU=lines.Count==3&&Math.Abs(lines[0].Direction.DotProduct(lines[2].Direction)+1)<1e-6&&Math.Abs(lines[0].Direction.DotProduct(lines[1].Direction))<1e-6;
                inventory.Add(new{id=shape.Id.IntegerValue,shape.Name,segments=def.NumberOfSegments,majorSegment=def.MajorSegmentIndex,isU,
                    style=shape.RebarStyle.ToString(),startHookAngle=shape.GetDefaultHookAngle(0),endHookAngle=shape.GetDefaultHookAngle(1),allowedForSelectedType=shape.GetAllowed(type),browser=curves.Select(PathCurve).ToList()});
                if(isU&&def.MajorSegmentIndex==1)candidates.Add(shape);
            }
            _report["threeSegmentTemplateShapes"]=inventory;
            Require(candidates.Count>0,"No existing ordinary three-segment U shape with central major segment in template.");
            var chosen=candidates.OrderBy(s=>s.Name,StringComparer.Ordinal).First();
            var hooks=new FilteredElementCollector(doc).OfClass(typeof(RebarHookType)).Cast<RebarHookType>().ToList();
            _report["templateHooks"]=hooks.Select(h=>new{id=h.Id.IntegerValue,h.Name,style=h.Style.ToString(),angleDegrees=h.HookAngle*180/Math.PI}).ToList();
            ElementId startHook=ExistingHook(chosen,0,hooks),endHook=ExistingHook(chosen,1,hooks);
            var pathType=new FilteredElementCollector(doc).OfClass(typeof(PathReinforcementType)).Cast<PathReinforcementType>().FirstOrDefault();
            Require(pathType!=null,"Template has no PathReinforcementType.");
            var top=floor.get_Geometry(new Options{ComputeReferences=true,DetailLevel=ViewDetailLevel.Fine}).OfType<Solid>().Where(s=>s.Volume>0)
                .SelectMany(s=>s.Faces.Cast<Face>()).OfType<PlanarFace>().Where(f=>f.FaceNormal.Z>0.99).OrderByDescending(f=>f.Origin.Z).First();
            var edge=top.EdgeLoops.Cast<EdgeArray>().SelectMany(a=>a.Cast<Edge>()).Select(e=>e.AsCurve()).OfType<Line>()
                .Where(l=>Math.Abs(l.Direction.Y)<1e-8).OrderByDescending(l=>l.Length).First();
            // Native Path sketch bounds the outside of the shape; FarSide adds bar radius to the web axis.
            var start=edge.Evaluate(0.5,true)-XYZ.BasisX*(1000/304.8)+XYZ.BasisY*(35/304.8);
            var end=start+XYZ.BasisX*(2000/304.8);double topZ=top.Origin.Z*304.8,edgeY=edge.GetEndPoint(0).Y*304.8;
            Require(Math.Abs(floor.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM).AsDouble()*304.8-200)<0.01,"Host thickness changed.");
            foreach(var p in new[]{BuiltInParameter.CLEAR_COVER_TOP,BuiltInParameter.CLEAR_COVER_BOTTOM,BuiltInParameter.CLEAR_COVER_OTHER})
                Require(Math.Abs(((RebarCoverType)doc.GetElement(floor.get_Parameter(p).AsElementId())).CoverDistance*304.8-35)<0.01,"Expected unchanged template cover 35.");
            _report["input"]=new{hostId=floor.Id.IntegerValue,hostType=floor.Name,topElevationMm=topZ,thicknessMm=200,coverMm=35,
                barTypeId=type.Id.IntegerValue,barType=type.Name,parameters=SelectedParameters(type),modelDiameterMm=type.BarModelDiameter*304.8,
                shapeId=chosen.Id.IntegerValue,shapeName=chosen.Name,pathTypeId=pathType.Id.IntegerValue,pathTypeName=pathType.Name,
                sketch=new[]{Mm(start),Mm(end)},edgeYmm=edgeY,additionalOffsetMm=0,requestedLengthMm=500,maximumSpacingMm=200};
            Require(ReinforcementSettings.GetReinforcementSettings(doc).HostStructuralRebar,"Template HostStructuralRebar is disabled.");
            ElementId accepted=null;var attempts=new List<object>();PathEvidence evidence=null;
            foreach(bool flip in new[]{true})
            {
                using(var tx=new Transaction(doc,"POC one native U path"))
                {
                    tx.Start();
                    try
                    {
                        PathReinforcement path;
                        try{path=PathReinforcement.Create(doc,floor,new List<Curve>{Line.CreateBound(start,end)},flip,pathType.Id,type.Id,startHook,endHook,chosen.Id);}
                        catch(Autodesk.Revit.Exceptions.ArgumentsInconsistentException error)
                        {
                            attempts.Add(new{flip,route="Create with shape",error=error.ToString()});
                            Require(chosen.GetDefaultHookAngle(0)==0&&chosen.GetDefaultHookAngle(1)==0,"Shape requires hooks; no no-hook fallback permitted.");
                            path=PathReinforcement.Create(doc,floor,new List<Curve>{Line.CreateBound(start,end)},flip,pathType.Id,type.Id,ElementId.InvalidElementId,ElementId.InvalidElementId);
                            path.PrimaryBarShapeId=chosen.Id;
                            attempts.Add(new{flip,route="Create without shape, assign PrimaryBarShapeId",created=true});
                        }
                        var valid=Enum.GetValues(typeof(ReinforcementBarOrientation)).Cast<ReinforcementBarOrientation>().Where(path.IsValidPrimaryBarOrientation).Select(x=>x.ToString()).ToList();
                        Require(path.IsValidPrimaryBarOrientation(ReinforcementBarOrientation.FarSide),"FarSide is invalid for the template U shape.");
                        path.PrimaryBarOrientation=ReinforcementBarOrientation.FarSide;path.AdditionalOffset=0;
                        path.get_Parameter(BuiltInParameter.PATH_REIN_ALTERNATING)?.Set(0);
                        path.get_Parameter(BuiltInParameter.PATH_REIN_SPACING).Set(200/304.8);
                        var length=path.get_Parameter(BuiltInParameter.PATH_REIN_LENGTH_1);Require(length!=null&&!length.IsReadOnly,"Primary instance length is not writable.");length.Set(500/304.8);
                        doc.Regenerate();evidence=MeasurePath(doc,path,topZ,edgeY);
                        bool inside=Math.Abs(evidence.MinLocalZ+159)<0.01&&Math.Abs(evidence.MaxLocalZ+41)<0.01&&evidence.MinYFromEdge>=41-0.01&&evidence.MaxYFromEdge<2000
                            &&Math.Abs(evidence.WebAxisInsetMm-41)<0.01&&evidence.TailsPointInward;
                        attempts.Add(new{flip,validOrientations=valid,insideCoverEnvelope=inside,measurement=evidence});
                        if(!inside){tx.RollBack();continue;}
                        Require(evidence.Sets.All(s=>s.Layout=="MaximumSpacing"||s.Layout=="Single"),"Actual child layout is not MaximumSpacing.");
                        Require(tx.Commit()==TransactionStatus.Committed,"Native path transaction did not commit.");accepted=path.Id;break;
                    }
                    catch(Exception error){attempts.Add(new{flip,error=error.ToString()});if(tx.GetStatus()==TransactionStatus.Started)tx.RollBack();}
                }
            }
            _report["attempts"]=attempts;Require(accepted!=null,"Template native U path did not produce a valid in-cover result; see attempt evidence. No output model saved.");
            var native=(PathReinforcement)doc.GetElement(accepted);evidence=MeasurePath(doc,native,topZ,edgeY);_report["createdPath"]=evidence;
            var view=PathView(doc,floor,native,start,end);var file=Path.Combine(_run,"native-one-u-path.rvt");doc.SaveAs(file,new SaveAsOptions{MaximumBackups=1});
            var export=new ImageExportOptions{FilePath=Path.Combine(_run,"native-path-detail"),ExportRange=ExportRange.SetOfViews,ZoomType=ZoomFitType.FitToPage,PixelSize=2000,
                ImageResolution=ImageResolution.DPI_150,HLRandWFViewsFileType=ImageFileType.PNG,ShadowViewsFileType=ImageFileType.PNG};export.SetViewsAndSheets(new List<ElementId>{view});doc.ExportImage(export);
            doc.Close(false);doc=null;doc=app.OpenDocumentFile(file);var reopened=MeasurePath(doc,(PathReinforcement)doc.GetElement(accepted),topZ,edgeY);
            Require(reopened.Signature==evidence.Signature&&reopened.PhysicalBars==evidence.PhysicalBars,"Path geometry changed after save/reopen.");
            _report["reopenedPath"]=reopened;doc.Close(false);doc=null;
            Check("native-template-u-path-created-and-reopened",new{model=file,sha256=Hash(file),systems=1,evidence.PhysicalBars,sets=evidence.Sets.Count,unchangedCentreline=true,sharedTypesEdited=false,hostCoverEdited=false});
        }
        private static ElementId ExistingHook(RebarShape shape,int end,List<RebarHookType> hooks)
        {
            int angle=shape.GetDefaultHookAngle(end);if(angle==0)return ElementId.InvalidElementId;
            var hook=hooks.FirstOrDefault(h=>h.Style==shape.RebarStyle&&Math.Abs(h.HookAngle*180/Math.PI-angle)<0.001);
            Require(hook!=null,"No existing template hook matches shape default angle/style.");return hook.Id;
        }
        private static object PathCurve(Curve c)=>new{kind=c.GetType().Name,start=Mm(c.GetEndPoint(0)),end=Mm(c.GetEndPoint(1)),midpoint=Mm(c.Evaluate(0.5,true)),lengthMm=c.Length*304.8,radiusMm=c is Arc a?(double?)(a.Radius*304.8):null};
        private static PathEvidence MeasurePath(Document doc,PathReinforcement path,double topZ,double edgeY)
        {
            var e=new PathEvidence{Id=path.Id.IntegerValue,Orientation=path.PrimaryBarOrientation.ToString(),AdditionalOffsetMm=path.AdditionalOffset*304.8};
            e.Parameters=path.Parameters.Cast<Parameter>().Where(p=>p.Definition!=null&&(p.Id.IntegerValue<0)).Select(p=>new{name=p.Definition.Name,id=p.Id.IntegerValue,storage=p.StorageType.ToString(),readOnly=p.IsReadOnly,value=p.AsValueString(),raw=p.StorageType==StorageType.Double?(object)p.AsDouble():p.StorageType==StorageType.Integer?p.AsInteger():p.StorageType==StorageType.ElementId?p.AsElementId().IntegerValue:(object)p.AsString()}).ToList();
            var points=new List<XYZ>();var signatures=new List<string>();var webInsets=new List<double>();bool tailsInward=true;
            foreach(var id in path.GetRebarInSystemIds())
            {
                var set=(RebarInSystem)doc.GetElement(id);var s=new PathSet{Id=id.IntegerValue,Layout=set.LayoutRule.ToString(),Quantity=set.Quantity,NumberOfPositions=set.NumberOfBarPositions};
                int physical=0;for(int i=0;i<set.NumberOfBarPositions;i++)
                {
                    if(!set.DoesBarExistAtPosition(i))continue;var curves=set.GetTransformedCenterlineCurves(true,false,false,i);
                    Require(curves.OfType<Line>().Count()==3&&curves.OfType<Arc>().Count()==2,"Expected exactly three lines and two arcs per U bar.");
                    var web=curves.OfType<Line>().Single(c=>Math.Abs(c.Direction.Z)>0.999999);
                    Require(Math.Abs(web.Direction.X)<1e-6&&Math.Abs(web.Direction.Y)<1e-6,"U web is not vertical.");
                    webInsets.Add(web.GetEndPoint(0).Y*304.8-edgeY);
                    tailsInward&=curves.First().GetEndPoint(0).Y>web.GetEndPoint(0).Y&&curves.Last().GetEndPoint(1).Y>web.GetEndPoint(0).Y;
                    physical++;e.PhysicalBars++;s.Positions.Add(new{position=i,curves=curves.Select(PathCurve).ToList()});
                    foreach(var c in curves){points.AddRange(c.Tessellate());signatures.Add(c.GetType().Name+":"+string.Join(";",new[]{c.GetEndPoint(0),c.Evaluate(0.5,true),c.GetEndPoint(1)}.SelectMany(Mm).Select(v=>v.ToString("F6",CultureInfo.InvariantCulture))));}
                }
                Require(physical==set.Quantity,"Physical position count differs from native Quantity.");e.Sets.Add(s);
            }
            Require(points.Count>0,"Native path generated no physical RIS curves.");e.MinLocalZ=points.Min(p=>p.Z*304.8-topZ);e.MaxLocalZ=points.Max(p=>p.Z*304.8-topZ);e.MinYFromEdge=points.Min(p=>p.Y*304.8-edgeY);e.MaxYFromEdge=points.Max(p=>p.Y*304.8-edgeY);
            Require(webInsets.Max()-webInsets.Min()<0.01,"U webs do not share the selected parallel edge offset.");e.WebAxisInsetMm=webInsets[0];e.TailsPointInward=tailsInward;
            e.Signature=string.Join("|",signatures.OrderBy(s=>s,StringComparer.Ordinal));return e;
        }
        private static ElementId PathView(Document doc,Floor floor,PathReinforcement path,XYZ start,XYZ end)
        {
            using(var tx=new Transaction(doc,"Native path POC detail view"))
            {
                tx.Start();var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t=>t.ViewFamily==ViewFamily.ThreeDimensional);
                var view=View3D.CreateIsometric(doc,type.Id);view.Name="П-обрамление — native Path POC";view.DetailLevel=ViewDetailLevel.Fine;view.DisplayStyle=DisplayStyle.HLR;
                var ids=path.GetRebarInSystemIds().ToList();foreach(var id in ids){((RebarInSystem)doc.GetElement(id)).SetUnobscuredInView(view,true);view.SetElementOverrides(id,new OverrideGraphicSettings().SetProjectionLineColor(new Autodesk.Revit.DB.Color(190,40,30)));}
                ids.Add(path.Id);ids.Add(floor.Id);view.IsolateElementsTemporary(ids);view.ConvertTemporaryHideIsolateToPermanent();view.SetElementOverrides(floor.Id,new OverrideGraphicSettings().SetSurfaceTransparency(85));
                var min=start-new XYZ(100/304.8,100/304.8,300/304.8);var max=end+new XYZ(100/304.8,800/304.8,100/304.8);view.SetSectionBox(new BoundingBoxXYZ{Min=min,Max=max});view.IsSectionBoxActive=true;
                var centre=(min+max)/2;var eye=centre+new XYZ(-4,-5,4);var forward=(centre-eye).Normalize();var right=forward.CrossProduct(XYZ.BasisZ).Normalize();view.SetOrientation(new ViewOrientation3D(eye,right.CrossProduct(forward).Normalize(),forward));
                var settings=StartingViewSettings.GetStartingViewSettings(doc);if(settings.IsAcceptableStartingView(view.Id))settings.ViewId=view.Id;tx.Commit();return view.Id;
            }
        }
        private sealed class PathEvidence{public int Id{get;set;}public string Orientation{get;set;}public double AdditionalOffsetMm{get;set;}public int PhysicalBars{get;set;}public double MinLocalZ{get;set;}public double MaxLocalZ{get;set;}public double MinYFromEdge{get;set;}public double MaxYFromEdge{get;set;}public double WebAxisInsetMm{get;set;}public bool TailsPointInward{get;set;}public object Parameters{get;set;}public List<PathSet> Sets{get;set;}=new List<PathSet>();public string Signature{get;set;}}
        private sealed class PathSet{public int Id{get;set;}public string Layout{get;set;}public int Quantity{get;set;}public int NumberOfPositions{get;set;}public List<object> Positions{get;set;}=new List<object>();}
    }
}
