using SlabReinforcement.Core;
using System.Text.Json;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class ConnectedLineAssignmentTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectedSelectedLineUsesLargestAssignmentAndItsAnchorageAtBothOuterEnds(bool alongX)
    {
        var request=Request(alongX);
        request.Faces=new()
        {
            Face("left","red",1000,2200,alongX),
            Face("middle","blue",2200,3000,alongX),
            Face("right","green",3000,4500,alongX)
        };
        var plan=ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.Equal(7,plan.Bars.Count);
        var group=Assert.Single(plan.Groups);
        Assert.Equal("d20",group.SpecKey);
        Assert.Equal(200,group.SpacingMm);
        Assert.All(plan.Bars,bar=>
        {
            Assert.Equal("d20",bar.SpecKey);
            Assert.Equal(1000-830,alongX?bar.Start.X:bar.Start.Y,6);
            Assert.Equal(4500+830,alongX?bar.End.X:bar.End.Y,6);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SeparateSelectedFragmentsDoNotUpgradeAcrossAnUnselectedGap(bool alongX)
    {
        var request=Request(alongX);
        foreach(var spec in request.Specifications)spec.AnchorageMm=0;
        request.Faces=new(){Face("left","red",1000,2200,alongX),Face("right","blue",3100,4500,alongX)};
        var plan=ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.Contains(plan.Bars,b=>b.SpecKey=="d12");
        Assert.Contains(plan.Bars,b=>b.SpecKey=="d20");
        Assert.All(plan.Bars,b=>Assert.False((alongX?b.Start.X:b.Start.Y)<2200&&(alongX?b.End.X:b.End.Y)>3100));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HostOpeningSeparatesAssignmentsEvenWhenSourceFacesTouch(bool alongX)
    {
        var request=Request(alongX);
        foreach(var spec in request.Specifications)spec.AnchorageMm=0;
        request.Slabs[0].Holes.Add(Rect(2400,200,2600,1800,alongX));
        request.Faces=new(){Face("left","red",1000,2500,alongX),Face("right","blue",2500,4500,alongX)};
        var plan=ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.Contains(plan.Bars,b=>b.SpecKey=="d12");
        Assert.Contains(plan.Bars,b=>b.SpecKey=="d20");
        Assert.All(plan.Bars,b=>Assert.False((alongX?b.Start.X:b.Start.Y)<2400&&(alongX?b.End.X:b.End.Y)>2600));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LargestDiameterKeepsItsOwnPitchEvenWhenSmallerDiameterHasGreaterArea(bool alongX)
    {
        var request=Request(alongX);
        request.Specifications.Single(s=>s.Key=="d20").SpacingMm=300;
        foreach(var spec in request.Specifications.Where(s=>s.DiameterMm==12))spec.SpacingMm=100;
        request.Faces=new(){Face("left","red",1000,2200,alongX),Face("middle","blue",2200,3000,alongX),Face("right","green",3000,4500,alongX)};
        var plan=ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate,Messages(plan));
        var group=Assert.Single(plan.Groups);
        Assert.Equal("d20",group.SpecKey);
        Assert.Equal(300,group.SpacingMm);
        Assert.Equal(new double[]{600,900,1200,1500},plan.Bars.Select(b=>alongX?b.Start.Y:b.Start.X));
        Assert.All(plan.Bars,b=>Assert.Equal("d20",b.SpecKey));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EqualDiametersChooseTheSmallerAssignedPitch(bool alongX)
    {
        var request=Request(alongX);
        foreach(var spec in request.Specifications)
        {spec.DiameterMm=20;spec.ModelDiameterMm=20;spec.AnchorageMm=830;spec.ElevationMm=-155;spec.SpacingMm=spec.Key=="d20"?200:300;}
        request.Faces=new(){Face("left","red",1000,2200,alongX),Face("middle","blue",2200,3000,alongX),Face("right","green",3000,4500,alongX)};
        var plan=ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate,Messages(plan));
        var group=Assert.Single(plan.Groups);
        Assert.Equal("d20",group.SpecKey);
        Assert.Equal(200,group.SpacingMm);
        Assert.Equal(7,plan.Bars.Count);
    }

    [Fact]
    public void EqualDiameterAndPitchUseStableTypeIdentityWithoutMutatingInput()
    {
        var request=Request(true);
        request.Specifications=new()
        {
            Spec("a-key","red","z-type",20,830),Spec("z-key","blue","a-type",20,830)
        };
        request.Faces=new(){Face("left","red",1000,2500,true),Face("right","blue",2500,4500,true)};
        string before=JsonSerializer.Serialize(request);
        var plan=ReinforcementPlanner.Build(request);
        Assert.Equal(before,JsonSerializer.Serialize(request));
        request.Faces.Reverse();request.Specifications.Reverse();
        before=JsonSerializer.Serialize(request);
        var reversed=ReinforcementPlanner.Build(request);
        Assert.Equal(before,JsonSerializer.Serialize(request));
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.True(reversed.CanCreate,Messages(reversed));
        Assert.Equal(7,plan.Bars.Count);
        Assert.All(plan.Bars,b=>Assert.Equal("z-key",b.SpecKey));
        Assert.Equal(JsonSerializer.Serialize(plan.Bars),JsonSerializer.Serialize(reversed.Bars));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AdjacentTransverseBandsHaveOneOwnerAtTheirSharedPhysicalStation(bool alongX)
    {
        var request=Request(alongX);
        request.Faces=new()
        {
            new(){Id="lower",ColorKey="red",Vertices=Rect(1000,400,4500,1000,alongX)},
            new(){Id="upper",ColorKey="blue",Vertices=Rect(1000,1000,4500,1600,alongX)}
        };
        var plan=ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate,Messages(plan));
        var shared=Assert.Single(plan.Bars,b=>Math.Abs((alongX?b.Start.Y:b.Start.X)-1000)<1e-6);
        Assert.Equal("d20",shared.SpecKey);
        Assert.Equal(7,plan.Bars.Count);
        Assert.Contains(plan.Bars,b=>b.SpecKey=="d12"&&(alongX?b.Start.Y:b.Start.X)==800);
        Assert.Contains(plan.Bars,b=>b.SpecKey=="d20"&&(alongX?b.Start.Y:b.Start.X)==1200);
    }

    [Theory]
    [InlineData(1000, true)]
    [InlineData(1100, false)]
    public void PromotedNumericLineChecksTheOriginalRequiredArea(double required, bool valid)
    {
        var request=Request(true);
        request.Options.CoverageHalfSpacing=true;
        request.Specifications.Single(s=>s.Key=="d12").RequiredAreaMm2PerM=required;
        request.Specifications.Single(s=>s.Key=="d20").SpacingMm=300;
        request.Faces=new()
        {
            new(){Id="left",ColorKey="red",Vertices=Rect(1000,450,2200,1650,true)},
            new(){Id="right",ColorKey="blue",Vertices=Rect(2200,450,4500,1650,true)}
        };
        var plan=ReinforcementPlanner.Build(request);
        Assert.Equal(valid,plan.CanCreate);
        Assert.Equal(!valid,plan.Diagnostics.Any(d=>d.Code=="LINE_ASSIGNMENT_AREA_DEFICIT"&&d.SourceId=="d12"&&d.IsBlocking));
    }

    [Fact]
    public void NumericPromotionCannotTransferRequiredAreaAcrossSteelClasses()
    {
        var request=Request(true);
        var source=request.Specifications.Single(s=>s.Key=="d12");
        source.RequiredAreaMm2PerM=100;source.MaterialKey="400";
        request.Faces=new(){Face("left","red",1000,2200,true),Face("right","blue",2200,4500,true)};
        var plan=ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics,d=>d.Code=="LINE_ASSIGNMENT_MATERIAL_MISMATCH"&&d.IsBlocking);
        Assert.Empty(plan.Bars);
    }

    [Fact]
    public void SourceMaterialOverlapRemainsBlockingBeforeLineNormalization()
    {
        var request=Request(true);
        request.Specifications.Single(s=>s.Key=="d12").MaterialKey="400";
        request.Faces=new(){Face("left","red",1000,2500,true),Face("right","blue",2200,4500,true)};
        var plan=ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics,d=>d.Code=="INCOMPATIBLE_MATERIAL_OVERLAP"&&d.IsBlocking);
        Assert.Empty(plan.Bars);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void InvalidRequiredAreaIsRejected(double value)
    {
        var request=Request(true);
        request.Specifications[0].RequiredAreaMm2PerM=value;
        request.Faces=new(){Face("left","red",1000,4500,true)};
        var plan=ReinforcementPlanner.Build(request);
        Assert.Contains(plan.Diagnostics,d=>d.Code=="INVALID_SPECIFICATION"&&d.IsBlocking);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PublicBandReplayUsesThePromotedDemandAtTerminalEdges(bool alongX)
    {
        var request=Request(alongX);
        foreach(var spec in request.Specifications)spec.AnchorageMm=0;
        request.Options.GridOffsetMm=100;
        request.Faces=new()
        {
            new(){Id="left",ColorKey="red",Vertices=Rect(500,35,2200,1000,alongX)},
            new(){Id="right",ColorKey="blue",Vertices=Rect(2200,35,4500,1000,alongX)}
        };
        var plan=ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate,Messages(plan));
        var expected=plan.Groups.Select(g=>(g.PathBandStartMm,g.PathBandEndMm)).ToArray();
        Assert.All(plan.Groups,g=>Assert.Equal(35,g.PathBandStartMm));
        foreach(var group in plan.Groups){group.PathBandStartMm=null;group.PathBandEndMm=null;}
        ZonePathBandPlanner.Apply(plan,request);
        Assert.Equal(expected,plan.Groups.Select(g=>(g.PathBandStartMm,g.PathBandEndMm)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PositiveMicronWideLargerAssignmentStillOwnsTheConnectedLine(bool alongX)
    {
        var request=Request(alongX);
        foreach(var spec in request.Specifications)spec.AnchorageMm=0;
        request.Faces=new()
        {
            Face("left","red",1000,2200,alongX),
            Face("thin","blue",2200,2200.000001,alongX),
            Face("right","green",2200.000001,4500,alongX)
        };
        var plan=ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.Equal(7,plan.Bars.Count);
        Assert.All(plan.Bars,b=> {
            Assert.Equal("d20",b.SpecKey);
            Assert.Equal(1000,alongX?b.Start.X:b.Start.Y,6);
            Assert.Equal(4500,alongX?b.End.X:b.End.Y,6);
        });
    }

    private static PlanRequest Request(bool alongX)=>new()
    {
        Slabs=new(){new(){Shell=Rect(0,0,6000,2000,alongX)}},
        Specifications=new()
        {
            Spec("d12","red","type12",12,500),Spec("d20","blue","type20",20,830),Spec("d12green","green","type12",12,500)
        },
        Options=new(){AlongX=alongX,EdgeCoverMm=35,MergeToleranceMm=600,RoundIncrementMm=1,AllowBarIntersections=true}
    };
    private static ReinforcementSpec Spec(string key,string color,string type,double diameter,double anchor)=>new()
    {Key=key,ColorKey=color,TypeKey=type,MaterialKey="500",DiameterMm=diameter,ModelDiameterMm=diameter,SpacingMm=200,AnchorageMm=anchor,ElevationMm=-200+35+diameter/2};
    private static FaceInput Face(string id,string color,double left,double right,bool alongX)=>new()
    {Id=id,ColorKey=color,Vertices=Rect(left,400,right,1600,alongX)};
    private static List<Point2> Rect(double left,double bottom,double right,double top,bool alongX)=>new (double x,double y)[]
        {(left,bottom),(right,bottom),(right,top),(left,top)}.Select(p=>alongX?new Point2(p.x,p.y):new Point2(p.y,p.x)).ToList();
    private static string Messages(ReinforcementPlan plan)=>string.Join("; ",plan.Diagnostics.Select(d=>d.Code+": "+d.Message));
}
