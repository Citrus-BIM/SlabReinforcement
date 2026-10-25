using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class ExistingReinforcementMatcherTests
{
    [Fact]
    public void StraightArrayPositionsMatchIndividuallyRegardlessOfKeysOrInputOrder()
    {
        var expected=Enumerable.Range(0,4).Select(i=>Straight("background",i*200)).ToList();
        var actual=Enumerable.Range(0,4).Select(i=>Actual(Straight("",i*200),"Existing|array|"+i)).Reverse().ToList();
        var result=ExistingReinforcementMatcher.Match(expected,actual);
        Assert.True(result.IsComplete);Assert.Equal(4,result.MatchedCount);
        Assert.All(result.Matches,m=>Assert.Equal("Existing|array|"+m.ExpectedIndex,m.ActualSpecKey));
        actual.RemoveAt(0);result=ExistingReinforcementMatcher.Match(expected,actual);Assert.False(result.IsComplete);Assert.Equal(3,result.MatchedCount);
    }

    [Fact]
    public void UPathsMatchWithReorderedAndReversedLinesAndArcs()
    {
        var expected=U();var actual=Actual(expected,"Existing|u|0");
        actual.Centerline=actual.Centerline.Select(s=>new CenterlineSegment{Start=s.End,End=s.Start,Midpoint=s.Midpoint}).Reverse().ToList();
        (actual.Centerline[0],actual.Centerline[3])=(actual.Centerline[3],actual.Centerline[0]);
        Assert.True(ExistingReinforcementMatcher.Match(new[]{expected},new[]{actual}).IsComplete);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("diameter")]
    [InlineData("height")]
    [InlineData("bend")]
    [InlineData("missing")]
    [InlineData("nan")]
    [InlineData("empty")]
    public void APhysicalMismatchOrInvalidCurveCannotBeReused(string change)
    {
        var expected=U();var actual=Actual(expected,"actual");
        if(change=="type")actual.Placement.TypeKey="other";
        if(change=="diameter")actual.Placement.ModelDiameterMm+=0.01;
        if(change=="height")Shift(actual,0,0,0.11);
        if(change=="bend"){var s=actual.Centerline[1];var m=s.Midpoint.Value;s.Midpoint=new(m.X+2,m.Y,m.Z);}
        if(change=="missing")actual.Centerline.RemoveAt(2);
        if(change=="nan")actual.Centerline[0].Start=new(double.NaN,0,0);
        if(change=="empty")actual.Centerline.Clear();
        Assert.False(ExistingReinforcementMatcher.Match(new[]{expected},new[]{actual}).IsComplete);
    }

    [Theory]
    [InlineData(0,0,true)]
    [InlineData(0,0.1,true)]
    [InlineData(0,0.100000001,false)]
    [InlineData(0.08,0.08,false)]
    public void ToleranceIsAnExactEuclideanBoundWithoutAnExtraEpsilon(double dy,double dz,bool match)
    {
        var expected=Straight("plan",0);var actual=Actual(expected,"actual");Shift(actual,0,dy,dz);
        Assert.Equal(match,ExistingReinforcementMatcher.Match(new[]{expected},new[]{actual}).IsComplete);
    }

    [Fact]
    public void ActualBarIsNeverReusedTwiceAndRepeatedActualIdentitiesAreRejected()
    {
        var expected=new[]{Straight("p",0),Straight("p",0)};var actual=Actual(expected[0],"same-physical-id");
        var result=ExistingReinforcementMatcher.Match(expected,new[]{actual});Assert.Equal(1,result.MatchedCount);Assert.False(result.IsComplete);
        result=ExistingReinforcementMatcher.Match(expected,new[]{actual,Actual(expected[0],actual.SpecKey)});
        Assert.Equal(0,result.MatchedCount);Assert.False(result.IsComplete);
    }

    [Fact]
    public void MatchingDoesNotGreedilyLoseAnAvailableOneToOneAssignment()
    {
        var expected=new[]{Straight("p",0.09),Straight("p",0)};
        var actual=new[]{Actual(Straight("",0),"a"),Actual(Straight("",0.18),"b")};
        var result=ExistingReinforcementMatcher.Match(expected,actual);
        Assert.True(result.IsComplete);Assert.Equal("b",result.Matches.Single(m=>m.ExpectedIndex==0).ActualSpecKey);
    }

    [Fact]
    public void DisconnectedOrDuplicatedSegmentsAreInvalidEvenWhenBothInputsContainThem()
    {
        var a=Actual(Straight("",0),"a");a.Centerline.Add(new(){Start=new(200,0,0),End=new(300,0,0)});
        Assert.False(ExistingReinforcementMatcher.Match(new[]{a},new[]{Actual(a,"b")}).IsComplete);
        a=U();a.Centerline.Add(a.Centerline[0]);Assert.False(ExistingReinforcementMatcher.Match(new[]{a},new[]{Actual(a,"b")}).IsComplete);
    }

    [Fact]
    public void InvalidArgumentsAndCancellationAreExplicit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(()=>ExistingReinforcementMatcher.Match(Array.Empty<PlannedBar>(),Array.Empty<PlannedBar>(),double.NaN));
        Assert.Throws<OperationCanceledException>(()=>ExistingReinforcementMatcher.Match(new[]{Straight("",0)},Array.Empty<PlannedBar>(),cancellationToken:new CancellationToken(true)));
        Assert.True(ExistingReinforcementMatcher.Match(Array.Empty<PlannedBar>(),Array.Empty<PlannedBar>()).IsComplete);
    }

    [Fact]
    public void FullSteppedSlabPerimeterMatchesEveryPhysicalU()
    {
        var plan=PerimeterPlanner.Build(ActualFootprintPerimeterTests.Request());
        Assert.True(plan.CanCreate,string.Join(";",plan.Diagnostics.Select(d=>d.Code+":"+d.SourceId+":"+d.Message)));
        var actual=plan.Bars.Select((b,i)=>Actual(b,"Existing|slab|"+i)).Reverse().ToList();
        var result=ExistingReinforcementMatcher.Match(plan.Bars,actual);
        Assert.True(result.IsComplete);Assert.Equal(plan.Bars.Count,result.MatchedCount);
        Assert.Equal(result.MatchedCount,result.Matches.Select(m=>m.ActualIndex).Distinct().Count());
    }

    private static PlannedBar Straight(string key,double y)=>new(){SpecKey=key,Start=new(0,y),End=new(100,y),Placement=new(){TypeKey="12",ModelDiameterMm=12,ElevationMm=0}};
    private static PlannedBar U()=>PerimeterPlanner.Build(PerimeterZoneEndsTests.Perimeter()).Bars[0];
    private static PlannedBar Actual(PlannedBar bar,string key)=>new()
    {
        SpecKey=key,Start=bar.Start,End=bar.End,
        Placement=new(){TypeKey=bar.Placement.TypeKey,ModelDiameterMm=bar.Placement.ModelDiameterMm,ElevationMm=bar.Placement.ElevationMm},
        Centerline=(bar.Centerline??new(){new(){Start=new(bar.Start.X,bar.Start.Y,bar.Placement.ElevationMm),End=new(bar.End.X,bar.End.Y,bar.Placement.ElevationMm)}})
            .Select(s=>new CenterlineSegment{Start=s.Start,End=s.End,Midpoint=s.Midpoint}).ToList()
    };
    private static void Shift(PlannedBar bar,double x,double y,double z)
    {
        Point3 Move(Point3 p)=>new(p.X+x,p.Y+y,p.Z+z);
        foreach(var s in bar.Centerline){s.Start=Move(s.Start);s.End=Move(s.End);if(s.Midpoint.HasValue)s.Midpoint=Move(s.Midpoint.Value);}
    }
}
