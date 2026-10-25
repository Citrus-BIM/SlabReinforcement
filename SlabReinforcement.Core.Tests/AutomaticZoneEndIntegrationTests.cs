using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class AutomaticZoneEndIntegrationTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void MissingStraightAnchorageCreatesExactLocalUWithoutBackground(bool top, bool alongX)
    {
        var request = Request(top, alongX);
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.All(plan.Groups, g => Assert.True(g.Count >= 2));
        Assert.Equal(6, plan.Bars.Count);
        var automatic = Assert.IsType<ReinforcementPlan>(plan.AutomaticZoneEndPlan);
        Assert.True(automatic.CanCreate, Messages(automatic));
        Assert.Equal(plan.Bars.Count, automatic.Bars.Count);
        Assert.All(plan.Bars, b =>
        {
            var connection = Assert.Single(b.EndConnections);
            Assert.False(connection.AtStart);
            Assert.StartsWith("AutoZoneU|", connection.EdgeId);
            Assert.Equal(0, connection.StationOffsetMm);
            var u = Assert.Single(automatic.Bars, u => Math.Abs((alongX ? u.Centerline[2].Start.Y : u.Centerline[2].Start.X)
                - (alongX ? b.End.Y : b.End.X)) < 1e-6);
            Assert.Equal(alongX ? b.End.X : b.End.Y, alongX ? u.Centerline[2].Start.X : u.Centerline[2].Start.Y, 6);
            Assert.Equal(request.Specifications[0].ElevationMm, top ? u.Centerline[4].End.Z : u.Centerline[0].Start.Z);
        });
        Assert.Empty(BarContainmentValidator.Validate(automatic.Bars, request.Slabs, 200, 35, 35, 35.05));
    }

    [Fact]
    public void SteppedEdgeKeepsEveryValidZoneRowWithoutWideningBackgroundStationRanges()
    {
        var request = Request();
        request.Slabs[0].Shell = new() { new(0, 0), new(4000, 0), new(4000, 950), new(5000, 950), new(5000, 2000), new(0, 2000) };
        request.Faces[0].Vertices = Rect(3400, 600, 4900, 1400);
        request.Options.GridOffsetMm = 100;
        var background = new BackgroundRequest
        {
            Slabs = request.Slabs, ThicknessMm = 200, TopCoverMm = 35, BottomCoverMm = 35, SideCoverMm = 35.05,
            Layers = new[] { ("BX", false, true), ("BY", false, false), ("TX", true, true), ("TY", true, false) }
                .Select(x => new BackgroundLayer { Key = x.Item1, Top = x.Item2, AlongX = x.Item3, TypeKey = "12",
                    DiameterMm = 12, ModelDiameterMm = 12, SpacingMm = 200 }).ToList()
        };
        var backgroundPlan = BackgroundPlanner.Build(background);
        Assert.True(backgroundPlan.CanCreate, Messages(backgroundPlan));
        request.PerimeterPlan = PerimeterPlanner.Build(new PerimeterRequest
        {
            Slabs = request.Slabs, ThicknessMm = 200, TopCoverMm = 35, BottomCoverMm = 35, SideCoverMm = 35.05,
            TypeKey = "12", DiameterMm = 12, ModelDiameterMm = 12, BendDiameterMm = 48, LegLengthMm = 500,
            SpacingMm = 200, Outer = true, UseUbars = true, BackgroundPlan = backgroundPlan, BackgroundLayers = background.Layers
        });
        Assert.True(request.PerimeterPlan.CanCreate, Messages(request.PerimeterPlan));
        var originalRanges = request.PerimeterPlan.PerimeterUEdges.Select(e => (e.Id, e.StationRangeStartMm, e.StationRangeEndMm, Stations: string.Join(",", e.StationsMm))).ToArray();
        var shortEdge = Assert.Single(request.PerimeterPlan.PerimeterUEdges, e => Math.Abs(e.Start.X - 3958.95) < 1e-6 && Math.Abs(e.End.X - 3958.95) < 1e-6);
        Assert.True(shortEdge.Start.Y + shortEdge.StationRangeEndMm < 900);
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(new[] { 700d, 900, 1100, 1300 }, plan.Bars.Select(b => b.Start.Y));
        Assert.Equal(2, plan.Groups.Count);
        Assert.All(plan.Groups, g => Assert.Equal(2, g.Count));
        var automatic = Assert.IsType<ReinforcementPlan>(plan.AutomaticZoneEndPlan);
        Assert.Equal(4, automatic.Bars.Count);
        Assert.Contains(automatic.Bars, b => Math.Abs(b.Centerline[2].Start.X - 3958.95) < 1e-6 && Math.Abs(b.Centerline[2].Start.Y - 900) < 1e-6);
        Assert.Equal(originalRanges, request.PerimeterPlan.PerimeterUEdges.Select(e => (e.Id, e.StationRangeStartMm, e.StationRangeEndMm, Stations: string.Join(",", e.StationsMm))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InteriorOrExcludedSingleRowCreatesNoAutomaticU(bool singleRow)
    {
        var request = Request();
        request.Faces[0].Vertices = singleRow ? Rect(2400, 700, 2900, 710) : Rect(800, 400, 1800, 1400);
        var plan = ReinforcementPlanner.Build(request);
        if (singleRow)
        {
            Assert.Empty(plan.Bars);
            Assert.Contains(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED");
        }
        else
        {
            Assert.True(plan.CanCreate, Messages(plan));
            Assert.All(plan.Bars, b => Assert.Empty(b.EndConnections));
        }
        Assert.True(plan.AutomaticZoneEndPlan == null || plan.AutomaticZoneEndPlan.Bars.Count == 0);
    }

    [Fact]
    public void ImpossibleLocalUReportsTheActualFailedEndInsteadOfStraightAnchorage()
    {
        var request = Request(); request.AutomaticZoneEnds.LegLengthMm = 4000;
        var plan = ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        var failure = Assert.Single(plan.Diagnostics, d => d.Code == "ZONE_U_DOES_NOT_FIT");
        Assert.True(failure.IsBlocking);
        Assert.Contains("Полка П-окончания не помещается", failure.Message);
        Assert.Contains("X=", failure.Message); Assert.Contains("Y=", failure.Message);
        Assert.DoesNotContain(plan.Diagnostics, d => d.Code == "ANCHORAGE_DOES_NOT_FIT");
        Assert.True(plan.AutomaticZoneEndPlan == null || plan.AutomaticZoneEndPlan.Bars.Count == 0);
    }

    [Fact]
    public void AutomaticOptionStillRequiresExplicitManualZoneEndMode()
    {
        var request = Request(); request.Options.UsePerimeterUForZoneEnds = false;
        var disabled = ReinforcementPlanner.Build(request);
        Assert.Contains(disabled.Diagnostics, d => d.Code == "ANCHORAGE_DOES_NOT_FIT");
        Assert.Null(disabled.AutomaticZoneEndPlan);
        request.Options.UsePerimeterUForZoneEnds = true; request.Options.CoverageHalfSpacing = true;
        Assert.Contains(ReinforcementPlanner.Build(request).Diagnostics, d => d.Code == "ZONE_U_REQUIRES_MANUAL");
    }

    private static PlanRequest Request(bool top = false, bool alongX = true)
    {
        List<Point2> Orient(List<Point2> points) => alongX ? points : points.Select(p => new Point2(p.Y, p.X)).ToList();
        return new()
        {
            Slabs = new() { new() { Shell = Orient(Rect(0, 0, 3000, 2000)) } },
            Faces = new() { new() { Id = "zone", ColorKey = "navy", Vertices = Orient(Rect(2400, 400, 2900, 1400)) } },
            Specifications = new() { new() { Key = "zone", ColorKey = "navy", TypeKey = "12", MaterialKey = "A500C", DiameterMm = 12,
                ModelDiameterMm = 12, SpacingMm = 200, AnchorageMm = 500, ElevationMm = top ? -41 : -159 } },
            Options = new() { AlongX = alongX, EdgeCoverMm = 35.05, MergeToleranceMm = 600, RoundIncrementMm = 1,
                UsePerimeterUForZoneEnds = true, AllowBarIntersections = true },
            AutomaticZoneEnds = new() { TypeKey = "12", DiameterMm = 12, ModelDiameterMm = 12, BendDiameterMm = 48, LegLengthMm = 500,
                ThicknessMm = 200, TopCoverMm = 35, BottomCoverMm = 35, SideCoverMm = 35.05, Top = top }
        };
    }
    private static List<Point2> Rect(double x1, double y1, double x2, double y2) => new() { new(x1, y1), new(x2, y1), new(x2, y2), new(x1, y2) };
    private static string Messages(ReinforcementPlan plan) => string.Join("; ", plan.Diagnostics.Select(d => d.Code + ":" + d.Message));
}
