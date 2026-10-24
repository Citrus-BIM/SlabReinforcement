using SlabReinforcement.Core;
using Xunit;

public class PerimeterPlannerTests
{
    private static PerimeterRequest Request() => new PerimeterRequest
    {
        Slabs=new List<PolygonInput>{new(){Shell=new(){new(0,0),new(4000,0),new(4000,3000),new(0,3000)}}},
        TypeKey="8",DiameterMm=8,ModelDiameterMm=8,BendDiameterMm=32,
        ThicknessMm=220,TopCoverMm=25,BottomCoverMm=25,SideCoverMm=25,SpacingMm=200,LegLengthMm=500,
        Outer=true,Openings=true,UseUbars=true,UseLongitudinalBars=false
    };
    [Fact] public void UBarsIncludeTwoRealQuarterCircleBends()
    {
        var plan=PerimeterPlanner.Build(Request());
        Assert.True(plan.CanCreate,string.Join(";",plan.Diagnostics.Select(d=>d.Message)));
        Assert.All(plan.Groups,g=>{Assert.Equal(5,g.Centerline.Count);Assert.Equal(2,g.Centerline.Count(s=>s.Midpoint.HasValue));});
        Assert.All(plan.Groups,g=>Assert.InRange(g.Centerline.Min(s=>s.Start.Z),-191,-183));
    }
    [Fact] public void NarrowConcreteStripDoesNotShortenTheSpecifiedLeg()
    {
        var request=Request();request.Slabs[0].Holes.Add(new(){new(250,250),new(3750,250),new(3750,2750),new(250,2750)});
        var plan=PerimeterPlanner.Build(request);
        Assert.False(plan.CanCreate);Assert.Contains(plan.Diagnostics,d=>d.Code=="PERIMETER_CONTAINMENT");
    }
    [Fact] public void LongitudinalContoursHaveRoundedCornersAndRemainClosed()
    {
        var request=Request();request.UseUbars=false;request.UseLongitudinalBars=true;
        var plan=PerimeterPlanner.Build(request);Assert.True(plan.CanCreate,string.Join(";",plan.Diagnostics.Select(d=>d.Message)));
        Assert.Equal(2,plan.Groups.Count);
        foreach(var g in plan.Groups){Assert.Equal(4,g.Centerline.Count(s=>s.Midpoint.HasValue));Assert.Equal(g.Centerline[0].Start,g.Centerline.Last().End);}
    }
    [Fact] public void BendThatDoesNotFitThicknessIsRejected()
    {
        var request=Request();request.ThicknessMm=80;
        Assert.False(PerimeterPlanner.Build(request).CanCreate);
    }
    [Fact] public void OptionalContoursAndCancellationAreRespected()
    {
        var request=Request();request.Outer=false;
        Assert.False(PerimeterPlanner.Build(request).CanCreate);
        Assert.Throws<OperationCanceledException>(()=>PerimeterPlanner.Build(Request(),new CancellationToken(true)));
    }
}
