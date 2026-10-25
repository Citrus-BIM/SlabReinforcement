using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class AutomaticZoneEndLapSpanTests
{
    [Theory]
    [InlineData(true,true)]
    [InlineData(true,false)]
    [InlineData(false,true)]
    [InlineData(false,false)]
    public void SmallZoneExtendsOnlyItsFreeEndForFullRequiredLap(bool alongX,bool atStart)
    {
        var request=Request(3000);
        Point2 Transform(Point2 p)
        {
            var oriented=new Point2(atStart?p.X:3000-p.X,p.Y);
            return alongX?oriented:new(oriented.Y,oriented.X);
        }
        request.Options.AlongX=alongX;
        request.Slabs[0].Shell=request.Slabs[0].Shell.Select(Transform).ToList();
        request.Faces[0].Vertices=request.Faces[0].Vertices.Select(Transform).ToList();
        var plan=ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.Equal(2,Assert.Single(plan.Groups).Count);
        Assert.All(plan.Bars,b=>
        {
            Assert.Equal(1030,alongX?b.End.X-b.Start.X:b.End.Y-b.Start.Y,6);
            var connection=Assert.Single(b.EndConnections);Assert.Equal(atStart,connection.AtStart);
            Assert.True(connection.OverlapMm>=1000-1e-6);
            Assert.Equal(atStart?41.05:2958.95,alongX?(atStart?b.Start.X:b.End.X):(atStart?b.Start.Y:b.End.Y),6);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShortHostOrOpeningBlocksRequiredSpanWithoutShorteningLap(bool opening)
    {
        var request=Request(opening?3000:1000);
        if(opening)request.Slabs[0].Holes.Add(new(){new(900,200),new(1200,200),new(1200,1500),new(900,1500)});
        var plan=ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        var diagnostic=Assert.Single(plan.Diagnostics,d=>d.Code=="ZONE_U_DOES_NOT_FIT");
        Assert.Contains("1030",diagnostic.Message);
        Assert.Contains("X=",diagnostic.Message);Assert.Contains("Y=",diagnostic.Message);
        Assert.Empty(plan.Bars);
        Assert.True(plan.AutomaticZoneEndPlan==null||plan.AutomaticZoneEndPlan.Bars.Count==0);
    }

    [Fact]
    public void BothPinnedEndsCannotReleaseTheSpanByMovingEitherEnd()
    {
        var request=Request(1000);
        request.Faces[0].Vertices=new(){new(50,600),new(950,600),new(950,1000),new(50,1000)};
        var plan=ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics,d=>d.Code=="ZONE_U_DOES_NOT_FIT"&&d.Message.Contains("1030"));
        Assert.Empty(plan.Bars);
    }

    [Fact]
    public void LegacyGeometryModeDoesNotExtendAFreeEndToTheFullULeg()
    {
        var request=Request(3000);request.AutomaticZoneEnds.RequiredLapMm=0;
        var plan=ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.All(plan.Bars,b=>{Assert.Equal(530,b.End.X-b.Start.X,6);Assert.True(Assert.Single(b.EndConnections).OverlapMm<1000);});
    }

    [Fact]
    public void PerTypeRequiredSpanOverridesTheFallbackOptions()
    {
        var request=Request(3000);
        var options=request.AutomaticZoneEnds;
        request.AutomaticZoneEnds=new(){TypeKey="unused",DiameterMm=12,ModelDiameterMm=12,LegLengthMm=500};
        request.AutomaticZoneEndTypes=new(){["12"]=options};
        var plan=ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.All(plan.Bars,b=>Assert.Equal(1030,b.End.X-b.Start.X,6));
    }

    private static PlanRequest Request(double width)=>new()
    {
        Slabs=new(){new(){Shell=new(){new(0,0),new(width,0),new(width,2000),new(0,2000)}}},
        Faces=new(){new(){Id="small",ColorKey="navy",Vertices=new(){new(50,600),new(70,600),new(70,1000),new(50,1000)}}},
        Specifications=new(){new(){Key="zone",TypeKey="12",ColorKey="navy",MaterialKey="A500C",DiameterMm=12,
            ModelDiameterMm=12,SpacingMm=200,AnchorageMm=500,ElevationMm=-159}},
        Options=new(){AlongX=true,EdgeCoverMm=35.05,MergeToleranceMm=600,RoundIncrementMm=10,GridOffsetMm=100,
            UsePerimeterUForZoneEnds=true,AllowBarIntersections=true},
        AutomaticZoneEnds=new(){TypeKey="12",DiameterMm=12,ModelDiameterMm=12,BendDiameterMm=48,LegLengthMm=1030,RequiredLapMm=1000,
            ThicknessMm=200,TopCoverMm=35,BottomCoverMm=35,SideCoverMm=35.05}
    };
    private static string Messages(ReinforcementPlan plan)=>string.Join(";",plan.Diagnostics.Select(d=>d.Code+":"+d.Message));
}
