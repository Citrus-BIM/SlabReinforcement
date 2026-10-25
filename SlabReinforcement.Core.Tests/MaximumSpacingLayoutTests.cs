using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class MaximumSpacingLayoutTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RectangleStartsAndEndsAtCoverAndEveryGapIsAtMostMaximum(bool alongX)
    {
        var request=Background();request.Layers[0].AlongX=alongX;
        var plan=BackgroundPlanner.Build(request);Assert.True(plan.CanCreate,Messages(plan));
        var rows=plan.Bars.Select(b=>alongX?b.Start.Y:b.Start.X).Distinct().OrderBy(x=>x).ToArray();
        Assert.Equal(31,rows[0],6);Assert.Equal(alongX?2969:3969,rows[^1],6);
        Assert.All(rows.Zip(rows.Skip(1),(a,b)=>b-a),gap=>Assert.InRange(gap,12-1e-6,200+1e-6));
        AssertNativeGroupsMatchBars(plan);
    }

    [Fact]
    public void SteppedExteriorAndOpeningParallelEdgesAreMandatoryRows()
    {
        var request=Background();request.Slabs[0].Shell=new(){new(0,0),new(4000,0),new(4000,1300),new(3000,1300),new(3000,3000),new(0,3000)};
        request.Slabs[0].Holes.Add(PerimeterZoneEndsTests.Rect(1000,700,2000,1900));
        var plan=BackgroundPlanner.Build(request);Assert.True(plan.CanCreate,Messages(plan));
        foreach(var row in new[]{31.0,669,1269,1931,2969})Assert.Contains(plan.Bars,b=>Math.Abs(b.Start.Y-row)<1e-6);
        AssertNativeGroupsMatchBars(plan);
        Assert.Empty(BarContainmentValidator.Validate(plan.Bars,request.Slabs,200,25,25,25));
    }

    [Fact]
    public void ThinComponentHasOneCentreButConflictingMandatoryInternalRowsBlock()
    {
        var request=Background();request.Slabs[0].Shell=PerimeterZoneEndsTests.Rect(0,0,4000,74);
        var plan=BackgroundPlanner.Build(request);Assert.True(plan.CanCreate);Assert.Equal(37,Assert.Single(plan.Bars).Start.Y);
        request=Background();request.Slabs[0].Shell=new(){new(0,0),new(4000,0),new(4000,1000),new(3000,1000),new(3000,1010),new(2000,1010),new(2000,3000),new(0,3000)};
        plan=BackgroundPlanner.Build(request);Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics,d=>d.Code=="BACKGROUND_BOUNDARY_CONFLICT"&&d.IsBlocking);
    }

    [Fact]
    public void UStationsReserveDiameterAtBothCornersAndUseNativeMaximumSpacingGroups()
    {
        var request=PerimeterZoneEndsTests.Perimeter();var plan=PerimeterPlanner.Build(request);
        Assert.True(plan.CanCreate,Messages(plan));
        foreach(var edge in plan.PerimeterUEdges)
        {
            double length=Math.Sqrt(Math.Pow(edge.End.X-edge.Start.X,2)+Math.Pow(edge.End.Y-edge.Start.Y,2));
            Assert.Equal(12,edge.StationsMm[0],6);Assert.Equal(length-12,edge.StationsMm[^1],6);
            Assert.All(edge.StationsMm.Zip(edge.StationsMm.Skip(1),(a,b)=>b-a),gap=>Assert.InRange(gap,12-1e-6,200+1e-6));
        }
        Assert.True(plan.Groups.Count<plan.Bars.Count);
        AssertNativeGroupsMatchBars(plan);
        Assert.Equal(plan.Bars.Count,plan.PerimeterUEdges.Sum(e=>e.BarKeys.Count));
        var zones=PerimeterZoneEndsTests.Request(50,1000);zones.PerimeterPlan=plan;
        Assert.True(ReinforcementPlanner.Build(zones).CanCreate);
    }

    [Fact]
    public void ObliqueCornerUsesExactSafeRangeWithoutShorteningAnyLeg()
    {
        var request=ActualFootprintPerimeterTests.Request();var plan=PerimeterPlanner.Build(request);
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.Contains(plan.PerimeterUEdges,e=>e.StationRangeStartMm>e.ModelDiameterMm+1);
        foreach(var bar in plan.Bars)
        {
            var tip=bar.Centerline[0].Start;var web=bar.Centerline[2].Start;
            Assert.Equal(500,Math.Sqrt(Math.Pow(tip.X-web.X,2)+Math.Pow(tip.Y-web.Y,2)),6);
        }
        Assert.Empty(BarContainmentValidator.Validate(plan.Bars,request.Slabs,200,35,35,35.05));
    }

    [Fact]
    public void AnEndOutsideTheTrimmedUStationRangeCannotClaimThatEdge()
    {
        var perimeterRequest=ActualFootprintPerimeterTests.Request();var perimeter=PerimeterPlanner.Build(perimeterRequest);
        var edge=perimeter.PerimeterUEdges.First(e=>e.StationRangeStartMm>e.ModelDiameterMm+1);
        double length=Math.Sqrt(Math.Pow(edge.End.X-edge.Start.X,2)+Math.Pow(edge.End.Y-edge.Start.Y,2));
        double station=(edge.ModelDiameterMm+edge.StationRangeStartMm)/2;
        double x=edge.Start.X+(edge.End.X-edge.Start.X)*station/length,y=edge.Start.Y+(edge.End.Y-edge.Start.Y)*station/length;
        var request=new PlanRequest
        {
            Slabs=perimeterRequest.Slabs,PerimeterPlan=perimeter,
            Faces=new(){new(){Id="trimmed-corner",ColorKey="red",Vertices=PerimeterZoneEndsTests.Rect(x-200,y-1,x-50,y+1)}},
            Specifications=new(){new(){Key="zone",ColorKey="red",TypeKey="12",MaterialKey="500",DiameterMm=12,SpacingMm=200,AnchorageMm=500,ElevationMm=-77}},
            Options=new(){AlongX=true,EdgeCoverMm=35.05,GridOffsetMm=y,RoundIncrementMm=1,MergeToleranceMm=0,UsePerimeterUForZoneEnds=true}
        };
        var plan=ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);Assert.Contains(plan.Diagnostics,d=>d.Code=="ANCHORAGE_DOES_NOT_FIT"&&d.IsBlocking);
    }

    [Fact]
    public void AForbiddenInteriorStripCannotSilentlySplitOneUEdgeIntoPartialArrays()
    {
        var request=PerimeterZoneEndsTests.Perimeter();request.Openings=false;
        request.Slabs[0].Holes.Add(PerimeterZoneEndsTests.Rect(1200,200,1800,700));
        var plan=PerimeterPlanner.Build(request);Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics,d=>d.Code=="PERIMETER_STATION_RANGE"&&d.IsBlocking);Assert.Empty(plan.PerimeterUEdges);
        Assert.Equal(500,request.LegLengthMm);
    }

    private static void AssertNativeGroupsMatchBars(ReinforcementPlan plan)
    {
        Assert.Equal(plan.Bars.Count,plan.Groups.Sum(g=>g.Count));
        foreach(var group in plan.Groups)
        {
            Assert.Equal(BarLayoutRule.MaximumSpacing,group.LayoutRule);
            if(group.Count==1)continue;
            Assert.Equal(group.Count,(int)Math.Ceiling(group.ArrayLengthMm/group.MaximumSpacingMm)+1);
            Assert.Equal(group.ArrayLengthMm/(group.Count-1),group.SpacingMm,6);
            for(int i=0;i<group.Count;i++)
            {
                var p=group.Centerline?.First().Start??new Point3(group.Start.X,group.Start.Y,group.Placement.ElevationMm);
                var q=new Point3(p.X+i*group.SpacingMm*group.Placement.Distribution.X,p.Y+i*group.SpacingMm*group.Placement.Distribution.Y,p.Z);
                Assert.Contains(plan.Bars,b=>{var a=b.Centerline?.First().Start??new Point3(b.Start.X,b.Start.Y,b.Placement.ElevationMm);return Math.Abs(q.X-a.X)<1e-6&&Math.Abs(q.Y-a.Y)<1e-6&&Math.Abs(q.Z-a.Z)<1e-6;});
            }
        }
    }
    private static BackgroundRequest Background()=>new()
    {
        Slabs=new(){new(){Shell=PerimeterZoneEndsTests.Rect(0,0,4000,3000)}},ThicknessMm=200,TopCoverMm=25,BottomCoverMm=25,SideCoverMm=25,
        Layers=new(){new(){Key="BX",TypeKey="12",AlongX=true,ModelDiameterMm=12,DiameterMm=12,SpacingMm=200}}
    };
    private static string Messages(ReinforcementPlan plan)=>string.Join(";",plan.Diagnostics.Select(d=>d.Code+":"+d.Message));
}
