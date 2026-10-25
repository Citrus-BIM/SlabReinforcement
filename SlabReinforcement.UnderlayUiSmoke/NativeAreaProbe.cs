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
        private static readonly AreaReinforcementLayerType[] AreaLayers={AreaReinforcementLayerType.BottomOrBackMajor,
            AreaReinforcementLayerType.BottomOrBackMinor,AreaReinforcementLayerType.TopOrFrontMajor,AreaReinforcementLayerType.TopOrFrontMinor};
        private static readonly string[] AreaLayerNames={"Низ X","Низ Y","Верх X","Верх Y"};
        private static readonly BuiltInParameter[] AreaSpacing={BuiltInParameter.REBAR_SYSTEM_SPACING_BOTTOM_DIR_1,
            BuiltInParameter.REBAR_SYSTEM_SPACING_BOTTOM_DIR_2,BuiltInParameter.REBAR_SYSTEM_SPACING_TOP_DIR_1,BuiltInParameter.REBAR_SYSTEM_SPACING_TOP_DIR_2};
        private void RunNativeAreaProbe(Autodesk.Revit.ApplicationServices.Application app,ref Document doc,List<RebarBarType> types)
        {
            _report["scope"]="Native AreaReinforcement POC: four separate systems, one layer each, copy-only; no lap design or production changes";
            Require(new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcement)).GetElementCount()==0
                &&new FilteredElementCollector(doc).OfClass(typeof(Rebar)).GetElementCount()==0,"Area POC requires the clean baseline.");
            var floor=new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().Single();
            var type=types.FirstOrDefault(t=>Math.Abs(t.BarNominalDiameter*304.8-12)<0.01&&Numeric(t.get_Parameter(new Guid("32a47c7f-e91d-4a8e-bf24-927cb679b4d1")))==500);
            Require(type!=null,"Template has no nominal 12 mm grade-500 type.");
            _report["templateMetadata"]=AreaTemplateMetadata(doc,type);
            Log("AREA_SELECTED_TYPE "+type.Name);
            var solids=floor.get_Geometry(new Options{ComputeReferences=true,DetailLevel=ViewDetailLevel.Fine}).OfType<Solid>().Where(s=>s.Volume>0).ToList();
            var top=solids.SelectMany(s=>s.Faces.Cast<Face>()).OfType<PlanarFace>().Where(f=>f.FaceNormal.Z>0.99).OrderByDescending(f=>f.Origin.Z).First();
            var edge=top.EdgeLoops.Cast<EdgeArray>().SelectMany(a=>a.Cast<Edge>()).Select(e=>e.AsCurve()).OfType<Line>()
                .Where(l=>Math.Abs(l.Direction.Y)<1e-8&&Math.Abs(l.Direction.Z)<1e-8).OrderByDescending(l=>l.Length).First();
            XYZ direction=edge.Direction.Normalize();double topZ=top.Origin.Z*304.8;
            _report["host"]=new{floorId=floor.Id.IntegerValue,floor.Name,topElevationMm=topZ,majorDirection=Vec(direction),selectedEdge=new[]{Mm(edge.GetEndPoint(0)),Mm(edge.GetEndPoint(1))},
                barType=type.Name,nominalDiameterMm=type.BarNominalDiameter*304.8,modelDiameterMm=type.BarModelDiameter*304.8,
                topCover=Cover(doc,floor,BuiltInParameter.CLEAR_COVER_TOP),bottomCover=Cover(doc,floor,BuiltInParameter.CLEAR_COVER_BOTTOM),sideCover=Cover(doc,floor,BuiltInParameter.CLEAR_COVER_OTHER)};
            var settings=ReinforcementSettings.GetReinforcementSettings(doc);bool hostedBefore=settings.HostStructuralRebar;
            var ids=new List<ElementId>();
            using(var transaction=new Transaction(doc,"POC four separate native areas"))
            {
                transaction.Start();settings.HostStructuralRebar=true;
                var areaType=new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcementType)).Cast<AreaReinforcementType>().FirstOrDefault();
                ElementId areaTypeId=areaType?.Id??AreaReinforcementType.CreateDefaultAreaReinforcementType(doc);
                for(int i=0;i<AreaLayers.Length;i++)
                {
                    var area=AreaReinforcement.Create(doc,floor,direction,areaTypeId,type.Id,ElementId.InvalidElementId);
                    foreach(var layer in AreaLayers)area.SetLayerActive(layer==AreaLayers[i],layer);
                    area.AdditionalBottomCoverOffset=0;area.AdditionalTopCoverOffset=0;
                    var spacing=area.get_Parameter(AreaSpacing[i]);Require(spacing!=null&&!spacing.IsReadOnly,"Layer spacing is not writable.");spacing.Set(200/304.8);
                    area.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set("POC "+AreaLayerNames[i]+" — one native layer, zero extra cover offset");
                    ids.Add(area.Id);
                }
                Require(transaction.Commit()==TransactionStatus.Committed,"Native area creation was not committed.");
            }
            Log("AREA_CREATED count="+ids.Count);
            _report["hostStructuralRebar"]=new{before=hostedBefore,after=settings.HostStructuralRebar};
            var before=MeasureAreas(doc,ids,topZ,direction);
            _report["createdAreas"]=before;
            _report["layerHeights"]=new{bottomXmm=before[0].LocalZmm,bottomYmm=before[1].LocalZmm,topXmm=before[2].LocalZmm,topYmm=before[3].LocalZmm,
                bottomXYSeparationMm=Math.Abs(before[0].LocalZmm.Single()-before[1].LocalZmm.Single()),
                topXYSeparationMm=Math.Abs(before[2].LocalZmm.Single()-before[3].LocalZmm.Single()),extraCoverOffsetsMm=0,
                interpretation="Measured default result for four independently created single-layer areas; no automatic adjustment assumed"};
            var view=AreaView(doc,floor,ids);string target=Path.Combine(_run,"native-four-single-layer-areas.rvt");
            doc.SaveAs(target,new SaveAsOptions{MaximumBackups=1});
            var export=new ImageExportOptions{FilePath=Path.Combine(_run,"native-areas-overview"),ExportRange=ExportRange.SetOfViews,ZoomType=ZoomFitType.FitToPage,PixelSize=2400,
                ImageResolution=ImageResolution.DPI_150,HLRandWFViewsFileType=ImageFileType.PNG,ShadowViewsFileType=ImageFileType.PNG};
            export.SetViewsAndSheets(new List<ElementId>{view});doc.ExportImage(export);
            doc.Close(false);doc=null;doc=app.OpenDocumentFile(target);
            var after=MeasureAreas(doc,ids,topZ,direction);
            Require(before.Count==after.Count&&before.Zip(after,(a,b)=>a.Signature==b.Signature&&a.BarCount==b.BarCount&&a.SetCount==b.SetCount).All(x=>x),"Reopened native systems differ from created geometry.");
            Require(new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcement)).GetElementCount()==4,"Expected exactly four independent native Areas after reopen.");
            _report["reopenedAreas"]=after;doc.Close(false);doc=null;
            Check("four-native-single-layer-areas",new{model=target,sha256=Hash(target),areaSystems=4,rebarInSystemSets=before.Sum(a=>a.SetCount),physicalBars=before.Sum(a=>a.BarCount),reopened=true,exactCentrelineSignaturesUnchanged=true});
        }
        private static List<AreaEvidence> MeasureAreas(Document doc,List<ElementId> ids,double topZ,XYZ major)
        {
            var result=new List<AreaEvidence>();
            for(int index=0;index<ids.Count;index++)
            {
                var area=(AreaReinforcement)doc.GetElement(ids[index]);
                var active=AreaLayers.Where(area.IsLayerActive).ToList();Require(active.Count==1&&active[0]==AreaLayers[index],"Native area has unexpected enabled layers.");
                var sets=area.GetRebarInSystemIds().Select(id=>doc.GetElement(id)).Cast<RebarInSystem>().ToList();Require(sets.Count>0,"Native area has no physical RebarInSystem.");
                var evidence=new AreaEvidence{AreaId=area.Id.IntegerValue,Name=AreaLayerNames[index],Layer=active[0].ToString(),SetCount=sets.Count,
                    Direction=Vec(area.Direction),DistributionDirection=Vec(area.GetLayerDirection(active[0])),TopExtraCoverMm=area.AdditionalTopCoverOffset*304.8,BottomExtraCoverMm=area.AdditionalBottomCoverOffset*304.8,
                    LayoutRule=area.get_Parameter(BuiltInParameter.REBAR_SYSTEM_LAYOUT_RULE)?.AsValueString(),SpacingMm=area.get_Parameter(AreaSpacing[index]).AsDouble()*304.8};
                evidence.InstanceParameters=SelectedParameters(sets[0]);
                var points=new List<double>();var signatures=new List<string>();
                foreach(var set in sets)
                {
                    var positions=new List<object>();int physical=0;
                    for(int position=0;position<set.NumberOfBarPositions;position++)
                    {
                        if(!set.DoesBarExistAtPosition(position))continue;
                        var curves=set.GetTransformedCenterlineCurves(true,false,false,position);Require(curves.Count>0,"Native bar has no centerline.");physical++;
                        foreach(var curve in curves)
                        {
                            XYZ a=curve.GetEndPoint(0),b=curve.GetEndPoint(1);points.Add(a.Z*304.8-topZ);points.Add(b.Z*304.8-topZ);
                            signatures.Add(string.Join(";",new[]{a.X,a.Y,a.Z,b.X,b.Y,b.Z}.Select(v=>(v*304.8).ToString("F6",CultureInfo.InvariantCulture))));
                        }
                        if(position==0||position==set.NumberOfBarPositions-1)
                            positions.Add(new{position,curves=curves.Select(c=>new{kind=c.GetType().Name,start=Mm(c.GetEndPoint(0)),end=Mm(c.GetEndPoint(1)),axisDotMajor=Math.Abs((c.GetEndPoint(1)-c.GetEndPoint(0)).Normalize().DotProduct(major))}).ToList(),
                                positionTransformOriginMm=Mm(set.GetBarPositionTransform(position).Origin)});
                    }
                    Require(physical==set.Quantity,"Native physical-position count differs from Quantity.");evidence.BarCount+=physical;
                    evidence.Sets.Add(new{Id=set.Id.IntegerValue,SystemId=set.SystemId.IntegerValue,set.Quantity,set.NumberOfBarPositions,layout=set.LayoutRule.ToString(),normal=Vec(set.Normal),firstAndLast=positions});
                }
                evidence.LocalZmm=points.Select(v=>Math.Round(v,6)).Distinct().OrderBy(v=>v).ToList();
                evidence.Signature=string.Join("|",signatures.OrderBy(s=>s,StringComparer.Ordinal));result.Add(evidence);
            }
            return result;
        }
        private static ElementId AreaView(Document doc,Floor floor,List<ElementId> areas)
        {
            using(var transaction=new Transaction(doc,"POC area view"))
            {
                transaction.Start();var viewType=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t=>t.ViewFamily==ViewFamily.ThreeDimensional);
                var view=View3D.CreateIsometric(doc,viewType.Id);view.Name="Фон — четыре отдельные области";view.DetailLevel=ViewDetailLevel.Fine;view.DisplayStyle=DisplayStyle.HLR;
                var visible=new List<ElementId>(areas){floor.Id};
                var colors=new[]{new Autodesk.Revit.DB.Color(0,80,190),new Autodesk.Revit.DB.Color(0,160,190),new Autodesk.Revit.DB.Color(190,40,30),new Autodesk.Revit.DB.Color(230,140,20)};
                for(int i=0;i<areas.Count;i++)foreach(var id in ((AreaReinforcement)doc.GetElement(areas[i])).GetRebarInSystemIds())
                {visible.Add(id);var bar=(RebarInSystem)doc.GetElement(id);bar.SetUnobscuredInView(view,true);view.SetElementOverrides(id,new OverrideGraphicSettings().SetProjectionLineColor(colors[i]));}
                view.IsolateElementsTemporary(visible);view.ConvertTemporaryHideIsolateToPermanent();view.SetElementOverrides(floor.Id,new OverrideGraphicSettings().SetSurfaceTransparency(90));
                var box=floor.get_BoundingBox(null);view.SetSectionBox(new BoundingBoxXYZ{Min=box.Min-new XYZ(1,1,1),Max=box.Max+new XYZ(1,1,1)});view.IsSectionBoxActive=true;
                var centre=(box.Min+box.Max)/2;double length=box.Min.DistanceTo(box.Max);var eye=centre+new XYZ(-length,-length,length);var forward=(centre-eye).Normalize();var right=forward.CrossProduct(XYZ.BasisZ).Normalize();view.SetOrientation(new ViewOrientation3D(eye,right.CrossProduct(forward).Normalize(),forward));
                var start=StartingViewSettings.GetStartingViewSettings(doc);if(start.IsAcceptableStartingView(view.Id))start.ViewId=view.Id;
                transaction.Commit();return view.Id;
            }
        }
        private sealed class AreaEvidence
        {
            public int AreaId{get;set;}public string Name{get;set;}public string Layer{get;set;}public int BarCount{get;set;}public int SetCount{get;set;}
            public double[] Direction{get;set;}public double[] DistributionDirection{get;set;}public double TopExtraCoverMm{get;set;}public double BottomExtraCoverMm{get;set;}public double SpacingMm{get;set;}
            public string LayoutRule{get;set;}public List<double> LocalZmm{get;set;}public List<object> Sets{get;set;}=new List<object>();public string Signature{get;set;}public object InstanceParameters{get;set;}
        }
        private static readonly string[] TemplateParameterNames={"Арм.КлассЧисло","Рзм.ПогМетрыВкл","Мрк.МаркаКонструкции","Орг.ЗонаРасположения","Арм.КлассБетона"};
        private void RunNativeAreaTemplateInventory(Document doc,List<RebarBarType> types)
        {
            _report["scope"]="READ-ONLY complete template bar-type inventory and existing native Area/RIS partition metadata; no parameter/geometry changes";
            var areas=new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcement)).Cast<AreaReinforcement>().ToList();
            var bars=new FilteredElementCollector(doc).OfClass(typeof(RebarInSystem)).Cast<RebarInSystem>().ToList();
            _report["allBarTypes"]=types.OrderBy(t=>t.BarNominalDiameter).ThenBy(t=>t.Name,StringComparer.Ordinal)
                .Select(t=>new{id=t.Id.IntegerValue,t.Name,nominalDiameterMm=t.BarNominalDiameter*304.8,modelDiameterMm=t.BarModelDiameter*304.8,
                    parameters=SelectedParameters(t)}).ToList();
            _report["areaPartitions"]=areas.Select(PartitionEvidence).ToList();
            _report["rebarInSystemPartitions"]=bars.Select(PartitionEvidence).ToList();
            _report["nativeInstanceParameters"]=bars.Take(1).Select(b=>new{id=b.Id.IntegerValue,parameters=SelectedParameters(b)}).ToList();
            Check("template-types-and-native-partitions-read",new{types=types.Count,areas=areas.Count,rebarInSystemSets=bars.Count,
                physicalBars=bars.Sum(b=>b.Quantity),writeTransactions=0,parametersModified=false});
        }
        private static object PartitionEvidence(Element element)
        {
            var parameter=element.get_Parameter(BuiltInParameter.NUMBER_PARTITION_PARAM);
            return new{elementId=element.Id.IntegerValue,elementClass=element.GetType().Name,parameterPresent=parameter!=null,name=parameter?.Definition.Name,
                storage=parameter?.StorageType.ToString(),parameter?.HasValue,raw=parameter?.StorageType==StorageType.String?parameter.AsString():parameter?.AsValueString(),
                display=parameter?.AsValueString(),readOnly=parameter?.IsReadOnly};
        }
        private static object AreaTemplateMetadata(Document doc,RebarBarType type)
        {
            var fields=new List<object>();
            foreach(var schedule in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(s=>s.Name.IndexOf("КЖ_Спецификация арматуры",StringComparison.OrdinalIgnoreCase)>=0||s.Name.IndexOf("КЖ_ВРС",StringComparison.OrdinalIgnoreCase)>=0))
            {
                var selected=schedule.Definition.GetFieldOrder().Select(id=>schedule.Definition.GetField(id))
                    .Where(f=>new[]{"Нахлест","Нахлёст","Пог","Rs","Rbt"}.Any(term=>(f.GetName()+" "+f.ColumnHeading).IndexOf(term,StringComparison.OrdinalIgnoreCase)>=0))
                    .Select(f=>new{name=f.GetName(),f.ColumnHeading,f.IsCalculatedField,fieldType=f.FieldType.ToString(),f.IsHidden}).ToList();
                fields.Add(new{schedule=schedule.Name,fields=selected});
            }
            return new{areaTypes=new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcementType)).Cast<AreaReinforcementType>().Select(t=>new{id=t.Id.IntegerValue,t.Name}).ToList(),
                selectedType=type.Name,selectedBarParameters=SelectedParameters(type),
                sharedDefinitions=new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)).Cast<SharedParameterElement>().Where(p=>TemplateParameterNames.Contains(p.Name)).Select(p=>new{p.Name,guid=p.GuidValue.ToString()}).ToList(),
                schedules=fields,formulaReadback="Revit 2023 public ScheduleField API exposes IsCalculatedField/FieldType, not formula text; no formula inferred."};
        }
        private static object SelectedParameters(Element element)=>TemplateParameterNames.Select(name=>
        {
            var p=element.GetParameters(name).FirstOrDefault();return new{name,present=p!=null,guid=p?.IsShared==true?p.GUID.ToString():null,storage=p?.StorageType.ToString(),p?.HasValue,
                raw=p==null?null:p.StorageType==StorageType.String?p.AsString():p.StorageType==StorageType.Double?p.AsDouble().ToString("G17",CultureInfo.InvariantCulture):p.StorageType==StorageType.Integer?p.AsInteger().ToString():p.AsElementId().IntegerValue.ToString(),display=p?.AsValueString(),readOnly=p?.IsReadOnly};
        }).ToList();
        private static double? Numeric(Parameter parameter)
        {if(parameter==null||!parameter.HasValue)return null;if(parameter.StorageType==StorageType.Double)return parameter.AsDouble();if(parameter.StorageType==StorageType.Integer)return parameter.AsInteger();return double.TryParse(parameter.AsString(),NumberStyles.Float,CultureInfo.InvariantCulture,out double value)?value:(double?)null;}
        private static object Cover(Document doc,Floor floor,BuiltInParameter name){var id=floor.get_Parameter(name)?.AsElementId();var cover=id==null?null:doc.GetElement(id) as RebarCoverType;return new{id=id?.IntegerValue,name=cover?.Name,distanceMm=cover?.CoverDistance*304.8};}
        private static double[] Mm(XYZ point)=>new[]{point.X*304.8,point.Y*304.8,point.Z*304.8};
        private static double[] Vec(XYZ vector)=>new[]{vector.X,vector.Y,vector.Z};
    }
}
