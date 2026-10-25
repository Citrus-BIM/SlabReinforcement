using System.Text.Json;
using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class ZonePathBandPlannerTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void ContinuousArraysMeetAtMidpointWithoutChangingPhysicalPlan(bool alongX, bool top)
    {
        var request = Request(alongX, top);
        var plan = Plan(alongX, Group(300), Group(700));
        var physical = Physical(plan);
        ZonePathBandPlanner.Apply(plan, request);
        Assert.Equal(200, plan.Groups[0].PathBandStartMm);
        Assert.Equal(600, plan.Groups[0].PathBandEndMm);
        Assert.Equal(600, plan.Groups[1].PathBandStartMm);
        Assert.Equal(1000, plan.Groups[1].PathBandEndMm);
        Assert.Equal(physical, Physical(plan));
    }

    [Fact]
    public void DifferentTypesAndPitchesUseCommonMidpointWithoutChangingEitherPitch()
    {
        var request = Request();
        request.Specifications.Add(new() { Key="b",ColorKey="blue",TypeKey="16",MaterialKey="steel",DiameterMm=16,SpacingMm=250 });
        var second = Group(700); second.SpecKey="b"; second.SpacingMm=250;
        var plan=Plan(true,Group(300),second);
        ZonePathBandPlanner.Apply(plan,request);
        Assert.Equal(600,plan.Groups[0].PathBandEndMm);
        Assert.Equal(600,plan.Groups[1].PathBandStartMm);
        Assert.Equal(1075,plan.Groups[1].PathBandEndMm);
        Assert.Equal(200,plan.Groups[0].SpacingMm);
        Assert.Equal(250,plan.Groups[1].SpacingMm);
    }

    [Theory]
    [InlineData(8,false)]
    [InlineData(24,false)]
    [InlineData(40,true)]
    public void SharedMidpointMustEncloseBothPhysicalBarFaces(double gap,bool fits)
    {
        var request=Request();
        request.Specifications.Add(new(){Key="b",ColorKey="blue",TypeKey="32",MaterialKey="steel",DiameterMm=32,SpacingMm=250});
        var second=Group(500+gap);second.SpecKey="b";second.SpacingMm=250;
        var plan=Plan(true,Group(300),second);
        var physical=Physical(plan);
        ZonePathBandPlanner.Apply(plan,request);
        if(fits)
        {
            Assert.Equal(500+gap/2,plan.Groups[0].PathBandEndMm);
            Assert.Equal(500+gap/2,plan.Groups[1].PathBandStartMm);
        }
        else
        {
            Assert.All(plan.Groups,g=>{Assert.Null(g.PathBandStartMm);Assert.Null(g.PathBandEndMm);});
            Assert.Contains(plan.Diagnostics,d=>d.Code=="ZONE_PATH_BAND_UNAVAILABLE"&&!d.IsBlocking);
        }
        Assert.Equal(physical,Physical(plan));
    }

    [Fact]
    public void MissingSingletonOrWhiteGapDoesNotBecomeAnAdjacentBand()
    {
        var plan=Plan(true,Group(300),Group(900));
        ZonePathBandPlanner.Apply(plan,Request());
        Assert.Equal(600,plan.Groups[0].PathBandEndMm);
        Assert.Equal(800,plan.Groups[1].PathBandStartMm);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HoleAwayFromNativeAnchorLimitsWholeBarSpan(bool alongX)
    {
        var request=Request(alongX);
        request.Slabs[0].Holes.Add(Points(alongX,(2000,600),(2500,600),(2500,900),(2000,900)));
        var plan=Plan(alongX,Group(300));
        ZonePathBandPlanner.Apply(plan,request);
        Assert.Equal(565,plan.Groups[0].PathBandEndMm.GetValueOrDefault(double.NaN),6);
        Assert.Equal(200,plan.Groups[0].PathBandStartMm);
    }

    [Fact]
    public void ConcaveEdgeAwayFromAnchorLimitsWholeBarSpan()
    {
        var request=Request();
        request.Slabs[0].Shell=Points(true,(0,0),(4000,0),(4000,600),(2000,600),(2000,1200),(4000,1200),(4000,3000),(0,3000));
        var plan=Plan(true,Group(300));
        ZonePathBandPlanner.Apply(plan,request);
        Assert.Equal(565,plan.Groups[0].PathBandEndMm.GetValueOrDefault(double.NaN),6);
    }

    [Theory]
    [InlineData(true,35)]
    [InlineData(false,105)]
    public void TerminalBandReachesCoverOnlyWhenConnectedSelectedDemandReachesIt(bool reachesEdge,double expected)
    {
        var request=Request();
        request.Faces.Add(new() {Id="demand",ColorKey="red",Vertices=Points(true,(250,reachesEdge?0:150),(2900,reachesEdge?0:150),(2900,600),(250,600))});
        var plan=Plan(true,Group(205));
        ZonePathBandPlanner.Apply(plan,request);
        Assert.Equal(expected,plan.Groups[0].PathBandStartMm);
    }

    [Fact]
    public void DisconnectedDemandNearEdgeDoesNotExtendTerminalBandAcrossWhiteGap()
    {
        var request=Request();
        request.Faces.Add(new(){Id="edge",ColorKey="red",Vertices=Points(true,(250,0),(2900,0),(2900,80),(250,80))});
        request.Faces.Add(new(){Id="rows",ColorKey="red",Vertices=Points(true,(250,150),(2900,150),(2900,600),(250,600))});
        var plan=Plan(true,Group(205));
        ZonePathBandPlanner.Apply(plan,request);
        Assert.Equal(105,plan.Groups[0].PathBandStartMm);
    }

    [Fact]
    public void InteriorSearchWindowIsNotMistakenForAHostEdge()
    {
        var request=Request();
        request.Faces.Add(new(){Id="interior",ColorKey="red",Vertices=Points(true,(250,0),(2900,0),(2900,2000),(250,2000))});
        var plan=Plan(true,Group(500));
        ZonePathBandPlanner.Apply(plan,request);
        Assert.Equal(400,plan.Groups[0].PathBandStartMm);
        Assert.Equal(800,plan.Groups[0].PathBandEndMm);
    }

    [Fact]
    public void HoleBetweenExistingRowsLeavesMetadataUnavailableAndKeepsPhysicalPlan()
    {
        var request=Request();
        request.Slabs[0].Holes.Add(Points(true,(2000,370),(2500,370),(2500,400),(2000,400)));
        var plan=Plan(true,Group(300));
        var physical=Physical(plan);
        ZonePathBandPlanner.Apply(plan,request);
        Assert.Null(plan.Groups[0].PathBandStartMm);
        Assert.Null(plan.Groups[0].PathBandEndMm);
        Assert.Contains(plan.Diagnostics,d=>d.Code=="ZONE_PATH_BAND_UNAVAILABLE"&&!d.IsBlocking);
        Assert.Equal(physical,Physical(plan));
    }

    [Fact]
    public void BranchedNeighbourhoodDoesNotPretendOneScalarBoundaryMeetsDifferentMidpoints()
    {
        var left=Group(650);left.End=new(1400,650);
        var right=Group(700);right.Start=new(1600,700);
        var plan=Plan(true,Group(300),left,right);
        ZonePathBandPlanner.Apply(plan,Request());
        Assert.Equal(575,plan.Groups[0].PathBandEndMm);
        Assert.Equal(575,left.PathBandStartMm);
        Assert.Equal(600,right.PathBandStartMm);
    }

    [Fact]
    public void InvalidOutlineGeometryDoesNotInvalidateAnAlreadyCalculatedPhysicalPlan()
    {
        var request=Request();
        request.Slabs[0].Shell=Points(true,(0,0),(4000,3000),(4000,0),(0,3000));
        var plan=Plan(true,Group(300));
        var physical=Physical(plan);
        ZonePathBandPlanner.Apply(plan,request);
        Assert.True(plan.CanCreate);
        Assert.Equal(physical,Physical(plan));
        Assert.Null(plan.Groups[0].PathBandStartMm);
        Assert.Contains(plan.Diagnostics,d=>d.Code=="ZONE_PATH_BAND_UNAVAILABLE"&&!d.IsBlocking);
    }

    [Fact]
    public void CancellationIsNotConvertedToAnOutlineFallback()
    {
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        var plan=Plan(true,Group(300));
        Assert.Throws<OperationCanceledException>(()=>ZonePathBandPlanner.Apply(plan,Request(),cancellation.Token));
        Assert.Empty(plan.Diagnostics);
    }

    [Fact]
    public void PlannerAttachesBandsToFinalGroupsWithoutChangingRowCount()
    {
        var request=Request();
        request.Faces.Add(new(){Id="rows",ColorKey="red",Vertices=Points(true,(500,300),(2500,300),(2500,900),(500,900))});
        request.Options.GridOffsetMm=100;
        var plan=ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate,string.Join(";",plan.Diagnostics.Select(d=>d.Message)));
        var group=Assert.Single(plan.Groups);
        Assert.Equal(4,group.Count);
        Assert.Equal(4,plan.Bars.Count);
        Assert.Equal(200,group.PathBandStartMm);
        Assert.Equal(1000,group.PathBandEndMm);
    }

    private static PlanRequest Request(bool alongX=true,bool top=false)=>new()
    {
        Slabs=new(){new(){Shell=Points(alongX,(0,0),(4000,0),(4000,3000),(0,3000))}},
        Options=new(){AlongX=alongX,EdgeCoverMm=35,MergeToleranceMm=600,RoundIncrementMm=10},
        Specifications=new(){new(){Key="a",ColorKey="red",TypeKey="12",MaterialKey="steel",DiameterMm=12,SpacingMm=200,ElevationMm=top?-41:-159}}
    };
    private static BarGroup Group(double row)=>new(){Start=new(100,row),End=new(3000,row),Count=2,SpacingMm=200,SpecKey="a",ColorKey="red"};
    private static ReinforcementPlan Plan(bool alongX,params BarGroup[] groups)
    {
        var plan=new ReinforcementPlan {Groups=groups.ToList()};
        foreach(var g in groups)
        {
            if(!alongX){g.Start=new(g.Start.Y,g.Start.X);g.End=new(g.End.Y,g.End.X);}
            for(int i=0;i<g.Count;i++)plan.Bars.Add(new(){Start=new(g.Start.X+(alongX?0:i*g.SpacingMm),g.Start.Y+(alongX?i*g.SpacingMm:0)),
                End=new(g.End.X+(alongX?0:i*g.SpacingMm),g.End.Y+(alongX?i*g.SpacingMm:0)),SpecKey=g.SpecKey});
        }
        return plan;
    }
    private static List<Point2> Points(bool alongX,params (double x,double y)[] values)=>values.Select(p=>alongX?new Point2(p.x,p.y):new Point2(p.y,p.x)).ToList();
    private static string Physical(ReinforcementPlan plan)=>JsonSerializer.Serialize(new {plan.Bars,Groups=plan.Groups.Select(g=>new{g.Start,g.End,g.Count,g.SpacingMm,g.SpecKey,g.EndConnections})});
}
