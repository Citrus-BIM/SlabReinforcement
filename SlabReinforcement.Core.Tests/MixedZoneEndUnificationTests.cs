using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class MixedZoneEndUnificationTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public void PinnedAndStraightRowsShareZoneWithoutAddingUToTheStraightRow(bool alongX, bool atStart, bool pinnedFirst)
    {
        var request = Request();
        request.Faces[0].Vertices = new() { new(50,600), new(1000,600), new(1000,1000), new(600,1000), new(600,800), new(50,800) };
        Point2 Transform(Point2 p)
        {
            var oriented = new Point2(atStart ? p.X : 3000-p.X, pinnedFirst ? p.Y : 2000-p.Y);
            return alongX ? oriented : new(oriented.Y, oriented.X);
        }
        request.Slabs[0].Shell = request.Slabs[0].Shell.Select(Transform).ToList();
        request.Faces[0].Vertices = request.Faces[0].Vertices.Select(Transform).ToList();
        request.Options.AlongX = alongX;
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(2, Assert.Single(plan.Groups).Count);
        Assert.Single(plan.Bars.SelectMany(b => b.EndConnections));
        Assert.Single(plan.AutomaticZoneEndPlan.Bars);
        Assert.Single(plan.Bars, b => b.EndConnections.Count == 0);
        Assert.All(plan.Bars, b => Assert.Equal(atStart ? 41.05 : 2958.95,
            alongX ? (atStart ? b.Start.X : b.End.X) : (atStart ? b.Start.Y : b.End.Y), 6));
    }

    [Fact]
    public void PinnedMiddleRowRemainsFixedWhenLaterFreeRowExtendsOtherEnd()
    {
        var request = Request();
        request.Faces[0].Vertices = new() { new(600,600), new(1000,600), new(1000,1000), new(1050,1000), new(1050,1200),
            new(650,1200), new(650,1000), new(50,1000), new(50,800), new(600,800) };
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(3, Assert.Single(plan.Groups).Count);
        Assert.All(plan.Bars, b => { Assert.Equal(41.05, b.Start.X, 6); Assert.True(b.End.X >= 1550); });
        Assert.Equal(900, Assert.Single(plan.Bars, b => b.EndConnections.Count > 0).Start.Y);
        Assert.Single(plan.AutomaticZoneEndPlan.Bars);
    }

    [Fact]
    public void MixedConnectionsCannotExtendThroughOpeningAtAnotherRow()
    {
        var request = Request();
        request.Slabs[0].Holes.Add(new() { new(100,850), new(300,850), new(300,950), new(100,950) });
        request.Faces[0].Vertices = new() { new(50,600), new(1500,600), new(1500,1000), new(1000,1000), new(1000,800), new(50,800) };
        var plan = ReinforcementPlanner.Build(request);
        Assert.Empty(plan.Bars); Assert.Empty(plan.Groups);
        Assert.Contains(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED");
    }

    [Fact]
    public void MiddlePinnedRowDoesNotResetFirstOriginalRowTolerance()
    {
        var request = Request();
        request.Faces[0].Vertices = new() { new(600,600), new(1800,600), new(1800,1200), new(1300,1200),
            new(1300,1000), new(50,1000), new(50,800), new(600,800) };
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(2, Assert.Single(plan.Groups).Count);
        Assert.Equal(new[] { 700d, 900 }, plan.Bars.Select(b => b.Start.Y));
        Assert.Contains(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED");
    }

    [Theory]
    [InlineData(-23300, -1018.1430154637271, 5339.325138181009, -23500, -1310, 5339.83590626676)]
    [InlineData(-11300, -740.7717938930052, 5629.883833666001, -11100, -1359.558349514245, 5929.676454666224)]
    [InlineData(-10700, -1058.1579118963764, 5928.840776219861, -10900, -1359.754812525172, 5929.344339090004)]
    public void ActualB25MissingRowsJoinPinnedNeighbors(double freeRow, double freeStart, double freeEnd,
        double pinnedRow, double pinnedStart, double pinnedEnd)
    {
        var request = Request();
        request.Slabs[0].Shell = new() { new(-1160,-38000), new(35930,-38000), new(35930,0), new(-1160,0) };
        FaceInput Face(double row, double start, double end) => new() { Id = row.ToString(), ColorKey = "navy",
            Vertices = new() { new(start+500,row-10), new(end-500,row-10), new(end-500,row+10), new(start+500,row+10) } };
        request.Faces = new() { Face(freeRow,freeStart,freeEnd), Face(pinnedRow,pinnedStart,pinnedEnd) };
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(2, Assert.Single(plan.Groups).Count);
        Assert.All(plan.Bars, b => Assert.Equal(-1118.95, b.Start.X, 6));
        Assert.Empty(Assert.Single(plan.Bars, b => Math.Abs(b.Start.Y-freeRow)<1e-6).EndConnections);
        Assert.Single(plan.AutomaticZoneEndPlan.Bars);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void InterveningOffGridRowDoesNotSplitCompatibleRegularRows(bool alongX, bool pinned)
    {
        var request = Request();
        request.Faces = new() { Face(700,pinned?50:600,1000), Face(900,pinned?50:600,1010), Face(810,1800,2200) };
        if(!alongX)
        {
            Point2 Swap(Point2 p)=>new(p.Y,p.X);
            request.Slabs[0].Shell=request.Slabs[0].Shell.Select(Swap).ToList();
            foreach(var face in request.Faces)face.Vertices=face.Vertices.Select(Swap).ToList();
            request.Options.AlongX=false;
        }
        var plan=ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.Equal(2,Assert.Single(plan.Groups).Count);
        Assert.Equal(new[]{700d,900},plan.Bars.Select(b=>alongX?b.Start.Y:b.Start.X));
        Assert.Equal(pinned?2:0,plan.AutomaticZoneEndPlan?.Bars.Count??0);
        Assert.Contains(plan.Diagnostics,d=>d.Code=="SINGLE_ROW_ZONES_SKIPPED");
    }

    [Fact]
    public void ActualB25OffGridRowCannotDiscardRegularRowAtMinus21300()
    {
        var request=Request();
        request.Slabs[0].Shell=new(){new(-1160,-38000),new(35930,-38000),new(35930,0),new(-1160,0)};
        request.Faces=new()
        {
            Face(-21300,33864.71539188964,34174.71539188964),
            Face(-21245.2975105,33864.75195150866,34174.75195150866),
            Face(-21100,33864.81160816689,34174.81160816689)
        };
        var plan=ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.Equal(new[]{-21300d,-21100},plan.Bars.Select(b=>b.Start.Y));
        Assert.Equal(2,Assert.Single(plan.Groups).Count);
        Assert.All(plan.Bars,b=>Assert.Empty(b.EndConnections));
    }

    [Fact]
    public void InterleavedGridsRemainTwoContiguousArrays()
    {
        var request=Request();
        request.Options.UsePerimeterUForZoneEnds=false;
        request.Slabs[0].Shell=new(){new(0,0),new(3000,0),new(3000,82000),new(0,82000)};
        request.Faces=Enumerable.Range(0,400).SelectMany(i=>new[]{Face(700+i*200,600,1000+i*.1),Face(810+i*200,1800,2200+i*.1)}).ToList();
        var plan=ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.Equal(800,plan.Bars.Count);
        Assert.Equal(2,plan.Groups.Count);
        Assert.All(plan.Groups,g=>{Assert.Equal(400,g.Count);Assert.Equal(200,g.SpacingMm);});
    }

    private static FaceInput Face(double row,double left,double right)=>new(){Id=row.ToString(),ColorKey="navy",
        Vertices=new(){new(left,row-10),new(right,row-10),new(right,row+10),new(left,row+10)}};

    private static PlanRequest Request() => new()
    {
        Slabs = new() { new() { Shell = new() { new(0,0), new(3000,0), new(3000,2000), new(0,2000) } } },
        Faces = new() { new() { Id = "step", ColorKey = "navy" } },
        Specifications = new() { new() { Key = "12", TypeKey = "12", ColorKey = "navy", MaterialKey = "A500C", DiameterMm = 12,
            ModelDiameterMm = 12, SpacingMm = 200, AnchorageMm = 500, ElevationMm = -159 } },
        Options = new() { AlongX = true, EdgeCoverMm = 35.05, MergeToleranceMm = 600, RoundIncrementMm = 10,
            GridOffsetMm = 100, UsePerimeterUForZoneEnds = true, AllowBarIntersections = true },
        AutomaticZoneEnds = new() { TypeKey = "12", DiameterMm = 12, ModelDiameterMm = 12, BendDiameterMm = 48, LegLengthMm = 500,
            ThicknessMm = 200, TopCoverMm = 35, BottomCoverMm = 35, SideCoverMm = 35.05, Top = false }
    };
    private static string Messages(ReinforcementPlan plan) => string.Join(";", plan.Diagnostics.Select(d => d.Code + ":" + d.Message));
}
