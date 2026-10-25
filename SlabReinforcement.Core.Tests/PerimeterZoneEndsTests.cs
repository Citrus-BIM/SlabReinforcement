using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class PerimeterZoneEndsTests
{
    [Theory]
    [InlineData(50,1000,1)]
    [InlineData(261.55,1000,1)]
    [InlineData(50,2950,2)]
    public void OnlyNonfittingEndsUseActualFullEdgeU(double left,double right,int connections)
    {
        var request=Request(left,right);
        var plan=ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.All(plan.Bars,b=>Assert.Equal(connections,b.EndConnections.Count));
        Assert.Equal(plan.Bars.Count,Assert.Single(plan.Groups).Count);
        Assert.All(plan.Bars,b=>Assert.Equal(41.05,b.Start.X,6));
        Assert.All(plan.Bars,b=>{if(connections==2)Assert.Equal(2958.95,b.End.X,6);else Assert.InRange(b.End.X,right+500,right+501);});
        Assert.All(plan.Bars.SelectMany(b=>b.EndConnections),c=>Assert.True(c.OverlapMm>0));
        Assert.Contains(plan.Diagnostics,d=>d.Code=="ZONE_ENDS_IN_PERIMETER_U"&&!d.IsBlocking);
        Assert.Equal(500,request.Specifications[0].AnchorageMm);
    }

    [Fact]
    public void InteriorAndDisabledModesKeepStraightAnchorage()
    {
        var request=Request(800,1800);var enabled=ReinforcementPlanner.Build(request);
        request.Options.UsePerimeterUForZoneEnds=false;var original=ReinforcementPlanner.Build(request);
        Assert.True(enabled.CanCreate,Messages(enabled));Assert.True(original.CanCreate,Messages(original));
        Assert.Equal(original.Bars.Select(b=>(b.Start,b.End)),enabled.Bars.Select(b=>(b.Start,b.End)));
        Assert.All(enabled.Bars,b=>Assert.Empty(b.EndConnections));
        request=Request(50,1000);request.Options.UsePerimeterUForZoneEnds=false;
        Assert.Contains(ReinforcementPlanner.Build(request).Diagnostics,d=>d.Code=="ANCHORAGE_DOES_NOT_FIT"&&d.IsBlocking);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MissingWrongEdgeWrongHeightOrIncompleteUDoNotReleaseEnds(int scenario)
    {
        var request=Request(50,1000);
        if(scenario==0) request.PerimeterPlan=null;
        if(scenario==1){var p=Perimeter();p.Slabs[0].Shell=Rect(4000,0,7000,2000);request.PerimeterPlan=PerimeterPlanner.Build(p);}
        if(scenario==2)request.Specifications[0].ElevationMm=-35;
        if(scenario==3)request.PerimeterPlan.Bars.Clear();
        Assert.False(ReinforcementPlanner.Build(request).CanCreate);
    }

    [Fact]
    public void TwoPinnedEndsRetainExactLengthAndOriginalDemand()
    {
        var request=Request(50,2950);request.Options.RoundIncrementMm=100;
        var before=request.Faces[0].Vertices.ToArray();var plan=ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.Contains(plan.Diagnostics,d=>d.Code=="ZONE_EDGE_EXACT_LENGTH"&&!d.IsBlocking);
        Assert.Equal(before,request.Faces[0].Vertices);
        Assert.Equal(2900*1200,Area(Assert.Single(plan.DemandRegions)),4);
        Assert.All(plan.Bars,b=>Assert.Equal(2917.9,b.End.X-b.Start.X,5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HoleEndsRequireThatSpecificOpeningToBeFramed(bool openings)
    {
        var p=Perimeter();p.Slabs[0].Shell=Rect(0,0,6000,4000);p.Slabs[0].Holes.Add(Rect(2500,1000,3500,3000));p.Openings=openings;
        var request=Request(1800,2400);request.Slabs=p.Slabs;request.PerimeterPlan=PerimeterPlanner.Build(p);
        request.Faces[0].Vertices=Rect(1800,1400,2400,2600);
        Assert.True(request.PerimeterPlan.CanCreate,Messages(request.PerimeterPlan));
        var plan=ReinforcementPlanner.Build(request);Assert.Equal(openings,plan.CanCreate);
        if(openings)Assert.All(plan.Bars,b=>{Assert.Equal(2458.95,b.End.X,6);Assert.False(Assert.Single(b.EndConnections).AtStart);});
        else Assert.Contains(plan.Diagnostics,d=>d.Code=="ANCHORAGE_DOES_NOT_FIT"&&d.IsBlocking);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TranslatedXYFramesKeepTheSameConnections(bool alongX)
    {
        var request=Request(50,2950);var original=ReinforcementPlanner.Build(request);
        Point2 Transform(Point2 p)=>alongX?new(p.X+123.4,p.Y-567.8):new(-p.Y+123.4,p.X-567.8);
        request.Slabs[0].Shell=request.Slabs[0].Shell.Select(Transform).ToList();request.Faces[0].Vertices=request.Faces[0].Vertices.Select(Transform).ToList();
        var p=Perimeter();p.Slabs=request.Slabs;request.PerimeterPlan=PerimeterPlanner.Build(p);
        request.Options.AlongX=alongX;request.Options.GridOffsetMm=alongX?-567.8:123.4;
        var plan=ReinforcementPlanner.Build(request);Assert.True(plan.CanCreate,Messages(plan));Assert.Equal(original.Bars.Count,plan.Bars.Count);
        Assert.All(plan.Bars,b=>Assert.Equal(2,b.EndConnections.Count));
        Assert.All(plan.Bars,b=>Assert.Equal(2917.9,alongX?b.End.X-b.Start.X:b.End.Y-b.Start.Y,5));
        Assert.All(plan.Bars.SelectMany(b=>b.EndConnections),c=>Assert.Equal(464,c.OverlapMm,5));
    }

    [Fact]
    public void AdjacentCoverageExtensionsRetainLeftConnectionsAndAddRightConnections()
    {
        var request=Request(50,1000);request.Options.RoundIncrementMm=100;
        request.Faces[0].Vertices=new(){new(50,200),new(1000,200),new(1000,201),new(2900,201),new(2900,209),new(1000,209),new(1000,401),new(2900,401),new(2900,409),new(1000,409),new(1000,1200),new(50,1200)};
        var plan=ReinforcementPlanner.Build(request);Assert.True(plan.CanCreate,Messages(plan));
        Assert.Contains(plan.Diagnostics,d=>d.Code=="ROWS_EXTENDED_FOR_COVERAGE");
        Assert.Equal(6,plan.Bars.Count);var extended=plan.Bars.Where(b=>b.End.X>2800).ToList();
        Assert.Equal(2,extended.Count);Assert.All(extended,b=>{Assert.Equal(2,b.EndConnections.Count);Assert.Equal(2958.95,b.End.X,6);});
        Assert.All(plan.Bars.Where(b=>b.End.X<=2800),b=>Assert.Single(b.EndConnections));
        Assert.Contains(plan.Groups,g=>g.Count>1);Assert.Equal(plan.Bars.Count,plan.Groups.Sum(g=>g.Count));
    }

    [Fact]
    public void SupplementalSingleRowIsSkippedWithoutMovingRetainedUConnectedRowsAcrossHoles()
    {
        var request=Request(50,1000);request.Specifications[0].AnchorageMm=50;
        var p=Perimeter();p.Slabs[0].Shell=Rect(0,-2000,3000,2000);p.Openings=false;
        p.Slabs[0].Holes.Add(Rect(1100,170,2400,230));p.Slabs[0].Holes.Add(Rect(1100,370,2400,430));
        request.Slabs=p.Slabs;request.PerimeterPlan=PerimeterPlanner.Build(p);Assert.True(request.PerimeterPlan.CanCreate,Messages(request.PerimeterPlan));
        request.Faces[0].Vertices=new(){new(50,200),new(1000,200),new(1000,301),new(2930,301),new(2930,309),new(1000,309),new(1000,1200),new(50,1200)};
        var plan=ReinforcementPlanner.Build(request);Assert.True(plan.CanCreate,Messages(plan));
        Assert.Contains(plan.Diagnostics,d=>d.Code=="SUPPLEMENTAL_ROWS_CREATED");
        Assert.Contains(plan.Diagnostics,d=>d.Code=="SINGLE_ROW_ZONES_SKIPPED");
        Assert.DoesNotContain(plan.Bars,b=>b.Start.Y>301&&b.Start.Y<309);
        Assert.All(plan.Bars,b=>Assert.Single(b.EndConnections));
        Assert.DoesNotContain(plan.Bars,b=>(b.Start.Y==200||b.Start.Y==400)&&b.End.X>1100);
    }

    [Fact]
    public void RemovingOneActualURejectsItsWholeEdge()
    {
        var request=Request(50,1000);var edge=request.PerimeterPlan.PerimeterUEdges.Single(e=>Math.Abs(e.Start.X-41.05)<1e-6&&Math.Abs(e.End.X-41.05)<1e-6);
        request.PerimeterPlan.Bars.RemoveAll(b=>b.SpecKey==edge.BarKeys[0]);
        Assert.True(request.PerimeterPlan.CanCreate);Assert.False(ReinforcementPlanner.Build(request).CanCreate);
        var p=Perimeter();p.LegLengthMm=4000;var failed=PerimeterPlanner.Build(p);Assert.False(failed.CanCreate);Assert.Empty(failed.PerimeterUEdges);
    }

    [Fact]
    public void ExplicitIntersectionPermissionAlsoAppliesToDifferentZoneTypes()
    {
        var request=Request(800,1500);request.Options.UsePerimeterUForZoneEnds=false;
        request.Faces.Add(new(){Id="blue",ColorKey="blue",Vertices=Rect(1600,400,2200,1600)});
        request.Specifications.Add(new(){Key="blue",ColorKey="blue",TypeKey="different12",MaterialKey="500",DiameterMm=12,ModelDiameterMm=12,SpacingMm=200,AnchorageMm=500,ElevationMm=-77});
        var strict=ReinforcementPlanner.Build(request);Assert.Contains(strict.Diagnostics,d=>d.Code=="FINAL_BAR_COLLISION"&&d.IsBlocking);
        request.Options.AllowBarIntersections=true;var allowed=ReinforcementPlanner.Build(request);
        Assert.True(allowed.CanCreate,Messages(allowed));Assert.DoesNotContain(allowed.Diagnostics,d=>d.Code=="FINAL_BAR_COLLISION");
        Assert.Equal(strict.Bars.Select(b=>(b.Start,b.End)),allowed.Bars.Select(b=>(b.Start,b.End)));
    }

    internal static PlanRequest Request(double left,double right)
    {
        var perimeter=Perimeter();
        return new PlanRequest
        {
            Slabs=perimeter.Slabs,PerimeterPlan=PerimeterPlanner.Build(perimeter),
            Faces=new(){new(){Id="zone",ColorKey="red",Vertices=Rect(left,400,right,1600)}},
            Specifications=new(){new(){Key="zone",ColorKey="red",TypeKey="12",MaterialKey="500",DiameterMm=12,ModelDiameterMm=12,SpacingMm=200,AnchorageMm=500,ElevationMm=-77}},
            Options=new(){AlongX=true,EdgeCoverMm=35.05,MergeToleranceMm=0,RoundIncrementMm=1,UsePerimeterUForZoneEnds=true}
        };
    }
    internal static PerimeterRequest Perimeter()=>new()
    {
        Slabs=new(){new(){Shell=Rect(0,0,3000,2000)}},TypeKey="12",DiameterMm=12,ModelDiameterMm=12,BendDiameterMm=60,
        ThicknessMm=200,TopCoverMm=35,BottomCoverMm=35,SideCoverMm=35.05,SpacingMm=200,LegLengthMm=500,Outer=true,Openings=true,UseUbars=true
    };
    internal static List<Point2> Rect(double x1,double y1,double x2,double y2)=>new(){new(x1,y1),new(x2,y1),new(x2,y2),new(x1,y2)};
    internal static string Messages(ReinforcementPlan p)=>string.Join(";",p.Diagnostics.Select(d=>d.Code+":"+d.Message));
    private static double Area(PolygonInput p)=>Math.Abs(p.Shell.Select((v,i)=>v.X*p.Shell[(i+1)%p.Shell.Count].Y-v.Y*p.Shell[(i+1)%p.Shell.Count].X).Sum())/2;
}
