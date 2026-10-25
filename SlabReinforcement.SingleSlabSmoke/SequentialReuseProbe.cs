using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SlabReinforcement.Core;
using SlabReinforcement.Integration;

namespace SlabReinforcement.SingleSlabSmoke
{
    public sealed partial class SingleSlabApplication
    {
        private static PreviewParameters ReuseParameters(SlabGeometrySnapshot slab,RebarBarType type,CadPlaneGroup group,
            IEnumerable<string> colors,string direction,bool createPerimeter)
        {
            var parameters=CombinedParameters(slab,type,group,colors,true);
            parameters.Direction=direction;
            parameters.Perimeter.Enabled=createPerimeter;
            // Retain the exact four layer/type/spacing choices as reference geometry.
            foreach(var layer in parameters.BackgroundLayers)layer.Enabled=false;
            return parameters;
        }

        private void RunSequentialReuse(Autodesk.Revit.ApplicationServices.Application application,int hostId,string stableEdge)
        {
            Document doc=null;
            try
            {
                _report["sequentialDirectionAssumption"]=new {directions=new[]{"Низ X","Верх X"},
                    basis="User-requested Manual bottom/top workflow on the same source map; calculation-layer identity and As legend are not verified"};
                doc=application.OpenDocumentFile(Path.Combine(_run,"single-slab-input.rvt"));
                if(RebarCount(doc)!=0)throw new InvalidOperationException("Sequential test requires a fresh baseline with no reinforcement.");
                var slab=SlabGeometryReader.Read(doc,(Floor)doc.GetElement(new ElementId(hostId)),Reference.ParseFromStableRepresentation(doc,stableEdge));
                ValidateFootprint(slab,"sequential-input");
                var types=new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToList();
                var type=types.Where(t=>Math.Abs(t.BarNominalDiameter*304.8-12)<0.01&&Grade(t)==500).OrderBy(t=>t.Name,StringComparer.Ordinal).First();
                var import=new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Single();
                var mesh=DxfMeshReader.Read(doc,import,slab.Frame,Path.Combine(_run,"source.dxf"),_footprint.cleanedDxfMmPerUnit);
                var group=mesh.Groups.Single();var colors=group.Faces.Select(f=>f.ColorKey).Distinct().Where(c=>c!="RGB(255, 255, 255)").ToList();

                var firstParameters=BackgroundParameters(slab,type,"background-u");
                var firstSession=new ReinforcementSession(slab,new ImportedMeshSnapshot(),types);
                var firstPlan=firstSession.CreateContext().Calculate(firstParameters,CancellationToken.None);
                if(!firstPlan.CanCreate||!firstPlan.Bars.Any(b=>b.Placement?.Component=="PerimeterU")
                    ||firstPlan.Bars.Where(b=>b.Placement?.Component=="Background").Select(b=>b.SpecKey).Distinct().Count()!=4)
                    throw new InvalidOperationException("Initial common fixture must contain U bars and all four background layers.");
                int firstSets=RebarPlanWriter.Create(doc,slab,firstSession,firstPlan,firstParameters);
                var commonMapping=new Dictionary<string,PlannedBar>();
                var firstComparison=CompareFullCentrelines(firstPlan.Bars,ExistingRebarReader.Read(slab),commonMapping);
                var uIds=ElementUniqueIds(commonMapping,"PerimeterU");var backgroundIds=ElementUniqueIds(commonMapping,"Background");
                var commonIds=new HashSet<string>(uIds.Concat(backgroundIds));
                if(uIds.Count!=firstPlan.Groups.Count(g=>g.Placement?.Component=="PerimeterU")
                    ||backgroundIds.Count!=firstPlan.Groups.Count(g=>g.Placement?.Component=="Background")||firstSets!=firstPlan.Groups.Count)
                    throw new InvalidOperationException("Initial common set counts differ from the computed plan.");
                var initialLayouts=AssertMaximumSpacing(doc,commonMapping);
                Add("sequential-create-background-and-u","PASS",new{bars=firstPlan.Bars.Count,sets=firstSets,components=Components(firstPlan),firstComparison,initialLayouts});

                // All destructive negative fixtures are contained in an outer rollback.
                using(var scope=new TransactionGroup(doc,"Temporarily remove one U bar"))
                {
                    scope.Start();
                    try
                    {
                        using(var transaction=new Transaction(doc,"Remove one U for rejection test"))
                        {transaction.Start();doc.Delete(doc.GetElement(uIds.First()).Id);transaction.Commit();}
                        if(!TestPlan(doc,slab,mesh,types,ReuseParameters(slab,type,group,colors,"Низ X",false),
                            "sequential-incomplete-u-rejected",expectedBlockingCodes:new[]{"PERIMETER_PARTIAL_MATCH"}))
                            throw new InvalidOperationException("Incomplete existing U guard did not pass.");
                    }
                    finally{scope.RollBack();}
                }
                AssertCommonUnchanged(slab,firstPlan.Bars,commonIds,uIds,backgroundIds);

                var changed=ReuseParameters(slab,type,group,colors,"Низ X",false);
                changed.Perimeter.LegLengthMm=510;
                if(!TestPlan(doc,slab,mesh,types,changed,"sequential-changed-u-settings-rejected",
                    expectedBlockingCodes:new[]{"ZONE_U_REQUIRES_PERIMETER"}))
                    throw new InvalidOperationException("Changed existing U settings guard did not pass.");
                var changedCreate=ReuseParameters(slab,type,group,colors,"Низ X",true);
                changedCreate.Perimeter.LegLengthMm=510;
                if(!TestPlan(doc,slab,mesh,types,changedCreate,"sequential-changed-u-settings-create-on-rejected",
                    expectedBlockingCodes:new[]{"PERIMETER_EXISTING_MISMATCH"}))
                    throw new InvalidOperationException("Mismatched existing U must not be duplicated with create enabled.");

                using(var scope=new TransactionGroup(doc,"Explicit create-on reuses exact existing U"))
                {
                    scope.Start();
                    try
                    {
                        var createOn=ReuseParameters(slab,type,group,colors,"Низ X",true);
                        WriteReuseStage(doc,slab,mesh,types,createOn,firstPlan.Bars,commonIds,uIds,backgroundIds,
                            "sequential-create-on-reuses-u",ExpectedBackgroundZ(firstPlan,"Низ X"));
                    }
                    finally{scope.RollBack();}
                }
                AssertCommonUnchanged(slab,firstPlan.Bars,commonIds,uIds,backgroundIds);
                if(RebarCount(doc)!=firstSets)throw new InvalidOperationException("Create-on rollback left reinforcement behind.");

                var bottom=WriteReuseStage(doc,slab,mesh,types,ReuseParameters(slab,type,group,colors,"Низ X",false),
                    firstPlan.Bars,commonIds,uIds,backgroundIds,"sequential-bottom-x-existing-u",ExpectedBackgroundZ(firstPlan,"Низ X"));
                var beforeTop=firstPlan.Bars.Concat(bottom.Bars).ToList();
                var top=WriteReuseStage(doc,slab,mesh,types,ReuseParameters(slab,type,group,colors,"Верх X",false),
                    beforeTop,commonIds,uIds,backgroundIds,"sequential-top-x-existing-u",ExpectedBackgroundZ(firstPlan,"Верх X"));
                var complete=new ReinforcementPlan();
                foreach(var part in new[]{firstPlan,bottom,top})
                {complete.Bars.AddRange(part.Bars);complete.Groups.AddRange(part.Groups);}
                var mapping=new Dictionary<string,PlannedBar>();
                CompareFullCentrelines(complete.Bars,ExistingRebarReader.Read(slab),mapping);
                var views=CreateRebarViews(doc,slab.Host,"sequential-bottom-top-x",mapping);
                string target=Path.Combine(_run,"zones-bottom-top-x-existing-u.rvt");
                doc.SaveAs(target,new SaveAsOptions{MaximumBackups=1});
                foreach(var view in views)
                {
                    var export=new ImageExportOptions{FilePath=Path.Combine(_run,"zones-bottom-top-x-existing-u"+(view.Key?"-corner":"-overview")),
                        ExportRange=ExportRange.SetOfViews,ZoomType=ZoomFitType.FitToPage,PixelSize=2400,ImageResolution=ImageResolution.DPI_150,
                        HLRandWFViewsFileType=ImageFileType.PNG,ShadowViewsFileType=ImageFileType.PNG};
                    export.SetViewsAndSheets(new List<ElementId>{view.Value});doc.ExportImage(export);
                }
                doc.Close(false);doc=null;
                doc=application.OpenDocumentFile(target);
                slab=SlabGeometryReader.Read(doc,(Floor)doc.GetElement(new ElementId(hostId)),Reference.ParseFromStableRepresentation(doc,stableEdge));
                ValidateFootprint(slab,"reopened-sequential-bottom-top-x");
                var actual=ExistingRebarReader.Read(slab);
                var reopenedMapping=new Dictionary<string,PlannedBar>();
                var comparison=CompareFullCentrelines(complete.Bars,actual,reopenedMapping);
                var reopenedLayouts=AssertMaximumSpacing(doc,reopenedMapping);
                var commonComparison=AssertCommonUnchanged(slab,firstPlan.Bars,commonIds,uIds,backgroundIds);
                var diagnostics=BarContainmentValidator.Validate(actual,slab.Slabs,slab.ThicknessMm,slab.TopCoverMm,slab.BottomCoverMm,slab.SideCoverMm+0.05);
                diagnostics.AddRange(CompositePlanValidator.Validate(actual,allowBarIntersections:true));
                if(diagnostics.Any(d=>d.IsBlocking)||RebarCount(doc)!=complete.Groups.Count)
                    throw new InvalidOperationException("Saved sequential model failed exact readback/count/containment checks.");
                doc.Close(false);doc=null;
                Add("saved-sequential-bottom-top-x-existing-u","PASS",new{model=target,sha256=Hash(target),bars=complete.Bars.Count,
                    sets=complete.Groups.Count,components=Components(complete),bottomZoneBars=bottom.Bars.Count,topZoneBars=top.Bars.Count,
                    bottomAcceptedEnds=ZoneConnections(bottom),topAcceptedEnds=ZoneConnections(top),comparison,commonComparison,diagnostics,
                    originalUCount=firstPlan.Bars.Count(b=>b.Placement?.Component=="PerimeterU"),
                    originalBackgroundCount=firstPlan.Bars.Count(b=>b.Placement?.Component=="Background"),reopenedLayouts,
                    reopenedAndVerified=true,commonElementIdsUnchanged=true,
                    purpose="MANUAL GEOMETRIC REUSE; NO CALCULATED ANCHORAGE OR AS CREDIT"});
            }
            catch(Exception error){Add("sequential-reuse-workflow","FAILED",new{message=error.ToString(),data=error.Data.Keys.Cast<object>().ToDictionary(k=>k.ToString(),k=>error.Data[k])});}
            finally{if(doc!=null&&doc.IsValidObject)doc.Close(false);}
        }

        private ReinforcementPlan WriteReuseStage(Document doc,SlabGeometrySnapshot slab,ImportedMeshSnapshot mesh,List<RebarBarType> types,
            PreviewParameters parameters,IList<PlannedBar> priorExpected,HashSet<string> commonIds,HashSet<string> uIds,HashSet<string> backgroundIds,
            string name,double expectedZ)
        {
            var beforeIds=RebarUniqueIds(doc);int before=RebarCount(doc);
            var session=new ReinforcementSession(slab,mesh,types);
            var plan=session.CreateContext().Calculate(parameters,CancellationToken.None);
            if(!plan.CanCreate)throw new InvalidOperationException(name+" blocked: "+string.Join(";",plan.Diagnostics.Where(d=>d.IsBlocking).Select(d=>d.Code+":"+d.Message)));
            if(plan.Bars.Count==0||plan.Bars.Any(b=>b.Placement?.Component!="Zones"||PathOf(b).Count!=1||PathOf(b)[0].Midpoint.HasValue
                ||Math.Abs(b.Placement.ElevationMm-expectedZ)>1e-6))throw new InvalidOperationException("Reuse stage must create only straight zones in the existing reference row.");
            foreach(string code in new[]{"PERIMETER_REUSED","BACKGROUND_REUSED","ZONE_ENDS_IN_PERIMETER_U"})
                if(!plan.Diagnostics.Any(d=>d.Code==code&&!d.IsBlocking))throw new InvalidOperationException("Missing explicit reuse diagnostic: "+code);
            var commonExpected=priorExpected.Where(b=>b.Placement?.Component!="Zones").ToList();
            var referenceComparison=CompareFullCentrelines(commonExpected,plan.ReferenceBars);
            int sets=RebarPlanWriter.Create(doc,slab,session,plan,parameters);
            var addedIds=RebarUniqueIds(doc);addedIds.ExceptWith(beforeIds);
            if(RebarCount(doc)-before!=sets||addedIds.Count!=plan.Groups.Count)throw new InvalidOperationException("Reuse-stage created-set count mismatch.");
            var actualNew=ExistingRebarReader.Read(slab,id=>addedIds.Contains(doc.GetElement(id).UniqueId));
            var newComparison=CompareFullCentrelines(plan.Bars,actualNew);
            var priorComparison=CompareFullCentrelines(priorExpected,ExistingRebarReader.Read(slab,id=>beforeIds.Contains(doc.GetElement(id).UniqueId)));
            var commonComparison=AssertCommonUnchanged(slab,commonExpected,commonIds,uIds,backgroundIds);
            Add(name,"PASS",new{newBars=plan.Bars.Count,newSets=sets,totalBars=priorExpected.Count+plan.Bars.Count,
                newComponents=Components(plan),parameters,newComparison,priorComparison,commonComparison,referenceComparison,plan.Diagnostics,
                acceptedEnds=ZoneConnections(plan),unchangedUCount=commonExpected.Count(b=>b.Placement?.Component=="PerimeterU"),
                unchangedBackgroundCount=commonExpected.Count(b=>b.Placement?.Component=="Background")});
            return plan;
        }

        private static HashSet<string> RebarUniqueIds(Document doc)=>new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().Select(b=>b.UniqueId));
        private static HashSet<string> ElementUniqueIds(IDictionary<string,PlannedBar> mapping,string component)=>new HashSet<string>(mapping
            .Where(p=>p.Value.Placement.Component==component).Select(p=>p.Key.Split('|')[1]));
        private static double ExpectedBackgroundZ(ReinforcementPlan common,string direction)=>common.Bars.First(b=>b.SpecKey=="Background|"+direction).Placement.ElevationMm;
        private static object AssertCommonUnchanged(SlabGeometrySnapshot slab,IList<PlannedBar> expected,HashSet<string> commonIds,HashSet<string> uIds,HashSet<string> backgroundIds)
        {
            var doc=slab.Host.Document;
            var u=uIds.Select(id=>doc.GetElement(id) as Rebar).ToList();var background=backgroundIds.Select(id=>doc.GetElement(id) as Rebar).ToList();
            if(u.Any(b=>b==null)||background.Any(b=>b==null)
                ||u.Sum(b=>b.Quantity)!=expected.Count(b=>b.Placement?.Component=="PerimeterU")
                ||background.Sum(b=>b.Quantity)!=expected.Count(b=>b.Placement?.Component=="Background"))
                throw new InvalidOperationException("Original U/background IDs or physical quantities changed.");
            var comparison=CompareFullCentrelines(expected,ExistingRebarReader.Read(slab,id=>commonIds.Contains(doc.GetElement(id).UniqueId)));
            return new {uElements=u.Count,uBars=u.Sum(b=>b.Quantity),backgroundElements=background.Count,backgroundBars=background.Sum(b=>b.Quantity),comparison,elementIdsUnchanged=true};
        }

        private static object AssertMaximumSpacing(Document doc,IDictionary<string,PlannedBar> physicalMapping)
        {
            var records=new List<object>();
            foreach(var component in new[]{"Background","PerimeterU"})
            {
                var ids=ElementUniqueIds(physicalMapping,component);
                var bars=ids.Select(id=>doc.GetElement(id) as Rebar).ToList();
                if(bars.Any(b=>b==null))throw new InvalidOperationException("Layout check lost a physical rebar set.");
                foreach(var bar in bars)
                    if(bar.Quantity>1)
                    {
                        if(bar.LayoutRule!=RebarLayoutRule.MaximumSpacing)
                            throw new InvalidOperationException(component+" multiposition set "+bar.UniqueId+" must use MaximumSpacing, got "+bar.LayoutRule);
                        if(Math.Abs(bar.MaxSpacing*304.8-200)>0.01)
                            throw new InvalidOperationException(component+" maximum spacing differs from the fixed 200 mm test assignment.");
                    }
                records.Add(new{component,sets=bars.Count,multiplePositionSets=bars.Count(b=>b.Quantity>1),
                    maximumSpacingsMm=bars.Where(b=>b.Quantity>1).Select(b=>b.MaxSpacing*304.8).Distinct().ToList(),
                    layoutRules=bars.GroupBy(b=>b.LayoutRule.ToString()).Select(g=>new{rule=g.Key,sets=g.Count(),physicalBars=g.Sum(b=>b.Quantity)}).ToList()});
            }
            return records;
        }
    }
}
