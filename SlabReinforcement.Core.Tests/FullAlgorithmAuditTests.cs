using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

/// <summary>
/// Characterisation of the current single-layer contract, not approval of a structural design.
/// These tests deliberately prove the gaps a background/zone assembly contract must close.
/// </summary>
public class FullAlgorithmAuditTests
{
    [Theory]
    [InlineData(600)]
    [InlineData(500.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AnchorageTableDoesNotDowngradeAnUnsupportedSteelGrade(double steelGrade)
    {
        Assert.Throws<ArgumentException>(() => LegacyAnchorageTable.GetLength(12, "B20", steelGrade));
    }

    [Fact]
    public void SeparateBackgroundAndZoneCallsCanReturnIdenticalPhysicalRows()
    {
        var background = ReinforcementPlanner.Build(Request(Rect(300, 300, 2700, 1700)));
        var zone = ReinforcementPlanner.Build(Request(Rect(300, 300, 2700, 1700)));
        Assert.True(background.CanCreate);
        Assert.True(zone.CanCreate);
        Assert.Equal(background.Bars.Select(Key), zone.Bars.Select(Key));
        Assert.NotEmpty(background.Bars);
        // No prior bars or layer identity can be supplied in the current PlanRequest.
        // Creating both successful plans would duplicate every physical bar.
    }

    [Fact]
    public void IndependentOrthogonalPlansUseTheSameElevationAtOneLayerOffset()
    {
        var requestX = Request(Rect(300, 300, 2700, 1700));
        var requestY = Request(Rect(300, 300, 2700, 1700));
        requestY.Options.AlongX = false;
        var x = ReinforcementPlanner.Build(requestX);
        var y = ReinforcementPlanner.Build(requestY);
        Assert.True(x.CanCreate);
        Assert.True(y.CanCreate);
        var zX = BarElevation.Calculate(true, 220, 25, 25, 12, 0);
        var zY = BarElevation.Calculate(true, 220, 25, 25, 12, 0);
        Assert.Equal(zX, zY);
        Assert.Contains(x.Bars, a => y.Bars.Any(b => b.Start.X > a.Start.X && b.Start.X < a.End.X
            && a.Start.Y > b.Start.Y && a.Start.Y < b.End.Y));
        // The independent commands are valid, but their combined geometry collides.
    }

    [Fact]
    public void WholeSlabBackgroundCannotUseTheCurrentZoneExtensionAnchoragePolicy()
    {
        var request = Request(Rect(0, 0, 3000, 2000));
        request.Specifications[0].AnchorageMm = LegacyAnchorageTable.GetLength(12, "B20", 500);
        var plan = ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "ANCHORAGE_DOES_NOT_FIT" && d.IsBlocking);
    }

    [Fact]
    public void GeometricCoverageIsNotNumericalAreaPerMetreVerification()
    {
        var request = Request(Rect(300, 1, 2700, 599));
        request.Options.EdgeCoverMm = 0;
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate);
        Assert.Equal(2, plan.Bars.Count);
        Assert.Equal(2, Assert.Single(plan.Groups).Count);
        // Allowed transverse range is 6..599 after the physical radius clearance.
        // Two bars form a real zone, but their +/- one-pitch geometric envelope
        // still covers a strip wider than the numerical tributary width.
        var nominalAs = Math.PI * 12 * 12 / 4 * 1000 / 200;
        var stripAverageAs = 2 * Math.PI * 12 * 12 / 4 * 1000 / (599 - 6);
        Assert.True(stripAverageAs < 0.68 * nominalAs);
        // This is a demonstration of the metric mismatch, not a code-based section check.
    }

    [Fact]
    public void CurrentSpacingValidationPermitsZeroPhysicalClearGap()
    {
        var request = Request(Rect(300, 300, 2700, 1700));
        request.Specifications[0].SpacingMm = 12;
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate);
        var rows = plan.Bars.Select(b => b.Start.Y).Distinct().OrderBy(v => v).ToList();
        Assert.True(rows.Count > 1);
        Assert.Equal(12, rows[1] - rows[0], 8);
        // Diameter-only collision prevention is not a minimum clear-spacing policy.
    }

    [Fact]
    public void TwoDimensionalCollisionCanRejectBarsSeparatedInActualElevation()
    {
        var request = Request(Rect(300, 199, 2700, 201));
        request.Specifications[0].DiameterMm = 6;
        request.Faces.Add(new() { Id = "small-second-row", ColorKey = "red", Vertices = Rect(300, 399, 2700, 401) });
        request.Faces.Add(new() { Id = "large", ColorKey = "blue", Vertices = Rect(300, 219, 2700, 221) });
        request.Faces.Add(new() { Id = "large-second-row", ColorKey = "blue", Vertices = Rect(300, 419, 2700, 421) });
        request.Specifications.Add(new() { Key = "large", ColorKey = "blue", MaterialKey = "A500", DiameterMm = 40, SpacingMm = 200 });
        var plan = ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "FINAL_BAR_COLLISION");
        Assert.All(plan.Groups, g => Assert.Equal(2, g.Count));
        Assert.Equal(2, plan.Bars.Count(b => b.SpecKey == "base"));
        Assert.Equal(2, plan.Bars.Count(b => b.SpecKey == "large"));
        var small = plan.Bars.First(b => b.SpecKey == "base");
        var large = plan.Bars.First(b => b.SpecKey == "large");
        var smallZ = BarElevation.Calculate(true, 300, 25, 25, 6, 0);
        var largeZ = BarElevation.Calculate(true, 300, 25, 25, 40, 0);
        var dy = small.Start.Y - large.Start.Y;
        var dz = smallZ - largeZ;
        Assert.True(Math.Sqrt(dy * dy + dz * dz) > (6 + 40) / 2.0);
        // The adapter uses diameter-dependent Z; the current 2D core does not know it.
    }

    [Fact]
    public void PartialDemandOutsideTheSelectedHostIsClippedWithoutAnAreaDiagnostic()
    {
        var request = Request(Rect(-1000, 300, 1000, 1700));
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate);
        Assert.DoesNotContain(plan.Diagnostics, d => d.Code == "DEMAND_OUTSIDE_SLAB");
        Assert.All(plan.DemandRegions.SelectMany(p => p.Shell), p => Assert.True(p.X >= 0));
        // This may be intentional for a multi-slab source. The UI still needs an explicit
        // clipped-area metric so an import placement/unit error is not silently accepted.
    }

    [Fact]
    public void CurrentWholeSlabGeometrySplitsAZeroExtensionBackgroundAtAHole()
    {
        var request = Request(Rect(0, 0, 3000, 2000));
        request.Slabs[0].Holes.Add(Rect(1100, 500, 1900, 1500));
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate);
        Assert.Contains(plan.Bars, b => b.Start.Y > 500 && b.Start.Y < 1500 && b.End.X <= 1069 + 1e-6);
        Assert.Contains(plan.Bars, b => b.Start.Y > 500 && b.Start.Y < 1500 && b.Start.X >= 1931 - 1e-6);
        // Geometry splitting works; this does not create opening-trimming or anchorage details.
    }

    private static PlanRequest Request(List<Point2> demand) => new()
    {
        Slabs = new() { new() { Shell = Rect(0, 0, 3000, 2000) } },
        Faces = new() { new() { Id = "demand", ColorKey = "red", Vertices = demand } },
        Specifications = new() { new() { Key = "base", ColorKey = "red", MaterialKey = "A500", DiameterMm = 12, SpacingMm = 200 } },
        Options = new() { EdgeCoverMm = 25, MergeToleranceMm = 0, RoundIncrementMm = 1 }
    };
    private static List<Point2> Rect(double a, double b, double c, double d) => new() { new(a, b), new(c, b), new(c, d), new(a, d) };
    private static string Key(PlannedBar b) => $"{b.Start.X:R},{b.Start.Y:R}/{b.End.X:R},{b.End.Y:R}";
}
