using SlabReinforcement.Core;
using Xunit;

public class BackgroundAlignedZoneEndsTests
{
    [Fact]
    public void ActualIrregularUStationsCanReceiveAZoneAtTheCorrespondingLegLevel()
    {
        var request=Request();
        var plan=ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate,string.Join(";",plan.Diagnostics.Select(d=>d.Message)));
        Assert.NotEmpty(plan.Bars.SelectMany(bar=>bar.EndConnections));
    }

    [Fact]
    public void AZoneBetweenTheTwoLegLevelsDoesNotClaimAnAlignedLap()
    {
        var request=Request();request.Specifications[0].ElevationMm=-100;
        Assert.False(ReinforcementPlanner.Build(request).CanCreate);
    }

    [Fact]
    public void MissingReferencedUStillInvalidatesItsEdge()
    {
        var request=Request();
        var edge=request.PerimeterPlan.PerimeterUEdges.Single(e=>Math.Abs(e.Start.X-41.05)<.001&&Math.Abs(e.End.X-41.05)<.001);
        request.PerimeterPlan.Bars.RemoveAll(bar=>bar.SpecKey==edge.BarKeys[0]);
        Assert.False(ReinforcementPlanner.Build(request).CanCreate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObliquePinnedEndpointsRemainExcludedSingletonsWithRealAlignedU(bool reverseRing)
    {
        var slab = new PolygonInput { Shell = new() { new(0, 0), new(4000, 0), new(5000, 3000), new(0, 3000) } };
        if (reverseRing) slab.Shell.Reverse();
        var background = new BackgroundRequest
        {
            Slabs = new() { slab }, ThicknessMm = 200, TopCoverMm = 35, BottomCoverMm = 35, SideCoverMm = 35.05,
            Layers = new[] { ("BX", false, true), ("BY", false, false), ("TX", true, true), ("TY", true, false) }
                .Select(x => new BackgroundLayer { Key = x.Item1, Top = x.Item2, AlongX = x.Item3, TypeKey = "12",
                    DiameterMm = 12, ModelDiameterMm = 12, SpacingMm = 200 }).ToList()
        };
        var backgroundPlan = BackgroundPlanner.Build(background);
        Assert.True(backgroundPlan.CanCreate, Messages(backgroundPlan));
        var perimeter = PerimeterPlanner.Build(new PerimeterRequest
        {
            Slabs = background.Slabs, ThicknessMm = 200, TopCoverMm = 35, BottomCoverMm = 35, SideCoverMm = 35.05,
            TypeKey = "8", DiameterMm = 8, ModelDiameterMm = 8, BendDiameterMm = 32,
            LegLengthMm = 500, Outer = true, UseUbars = true,
            BackgroundLayers = background.Layers, BackgroundPlan = backgroundPlan
        });
        Assert.True(perimeter.CanCreate, Messages(perimeter));
        var edge = Assert.Single(perimeter.PerimeterUEdges, e => e.AlongX && Math.Abs(e.End.X-e.Start.X)>1 && Math.Abs(e.End.Y-e.Start.Y)>1);
        Assert.True(edge.FollowsBackgroundRows);
        var request = new PlanRequest
        {
            Slabs = background.Slabs, PerimeterPlan = perimeter,
            Faces = new() { new() { Id = "edge-zone", ColorKey = "red", Vertices = new() { new(3400,600), new(4900,600), new(4900,2200), new(3400,2200) } } },
            Specifications = new() { new() { Key = "zone", ColorKey = "red", TypeKey = "12", MaterialKey = "A500C",
                DiameterMm = 12, ModelDiameterMm = 12, SpacingMm = 200, AnchorageMm = 500, ElevationMm = edge.BottomZ } },
            Options = new() { AlongX = true, EdgeCoverMm = 35.05, MergeToleranceMm = 0, RoundIncrementMm = 1,
                UsePerimeterUForZoneEnds = true, AllowBarIntersections = true }
        };
        var plan = ReinforcementPlanner.Build(request);
        // Each checked endpoint is pinned to a different coordinate on this
        // oblique edge. Valid U evidence must not turn those separate rows into
        // a native zone by moving their endpoints or retaining singleton output.
        Assert.False(plan.CanCreate);
        Assert.Empty(plan.Groups);
        Assert.Empty(plan.Bars);
        Assert.Contains(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED" && !d.IsBlocking);
        Assert.Contains(plan.Diagnostics, d => d.Code == "NO_BARS");
        Assert.DoesNotContain(plan.Diagnostics, d => d.Code == "ANCHORAGE_DOES_NOT_FIT" || d.Code == "ZONE_U_CONNECTION_LOST");
    }

    private static string Messages(ReinforcementPlan plan) => string.Join("; ", plan.Diagnostics.Select(d => d.Code + ":" + d.Message));

    private static PlanRequest Request()
    {
        var slab=new PolygonInput { Shell=new() {new(0,0),new(3000,0),new(3000,2000),new(0,2000)} };
        var perimeter=PerimeterPlanner.Build(new PerimeterRequest
        {
            Slabs=new(){slab},TypeKey="12",DiameterMm=12,ModelDiameterMm=12,BendDiameterMm=60,
            ThicknessMm=200,TopCoverMm=35,BottomCoverMm=35,SideCoverMm=35.05,
            SpacingMm=200,LegLengthMm=500,Outer=true,UseUbars=true
        });
        foreach(var edge in perimeter.PerimeterUEdges)
        {
            edge.FollowsBackgroundRows=true;edge.AlongX=Math.Abs(edge.Inward.X)>.5;
            // The evidence is complete for its explicitly stored, nonuniform rows.
            var removed=edge.BarKeys[2];edge.BarKeys.RemoveAt(2);edge.StationsMm.RemoveAt(2);
            perimeter.Bars.RemoveAll(bar=>bar.SpecKey==removed);
        }
        var left=perimeter.PerimeterUEdges.Single(e=>Math.Abs(e.Start.X-41.05)<.001&&Math.Abs(e.End.X-41.05)<.001);
        return new PlanRequest
        {
            Slabs=new(){slab},PerimeterPlan=perimeter,
            Faces=new(){new(){Id="edge-zone",ColorKey="red",Vertices=new(){new(50,400),new(1000,400),new(1000,1400),new(50,1400)}}},
            Specifications=new(){new(){Key="zone",ColorKey="red",TypeKey="12",MaterialKey="A500C",DiameterMm=12,ModelDiameterMm=12,SpacingMm=200,AnchorageMm=500,ElevationMm=left.BottomZ}},
            Options=new(){AlongX=true,EdgeCoverMm=35.05,MergeToleranceMm=0,RoundIncrementMm=10,UsePerimeterUForZoneEnds=true,AllowBarIntersections=true}
        };
    }
}
