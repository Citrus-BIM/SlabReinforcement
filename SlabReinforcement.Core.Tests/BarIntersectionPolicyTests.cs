using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class BarIntersectionPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AllowedCrossingsProduceNoDiagnosticsForNewOrExistingBars(int existingCount)
    {
        var crossing = Straight(); crossing.Start = new(50, -50); crossing.End = new(50, 50);
        var bars = new[] { Straight(), crossing };
        var strict = Assert.Single(CompositePlanValidator.Validate(bars, existingBarCount: existingCount));
        Assert.Equal("COMPOSITE_BAR_COLLISION", strict.Code);
        Assert.True(strict.IsBlocking);
        Assert.Empty(CompositePlanValidator.Validate(bars, existingBarCount: existingCount, allowBarIntersections: true));
        // Permission belongs to one request, never to validator static state.
        Assert.True(Assert.Single(CompositePlanValidator.Validate(bars, existingBarCount: existingCount)).IsBlocking);
    }

    [Fact]
    public void ExplicitPermissionDoesNotRelaxInvalidGeometryOrLimits()
    {
        var invalid = Straight(); invalid.Placement.ModelDiameterMm = double.NaN;
        Assert.Contains(CompositePlanValidator.Validate(new[] { invalid }, allowBarIntersections: true),
            d => d.Code == "INVALID_COMPOSITE_BAR" && d.IsBlocking);
        Assert.Contains(CompositePlanValidator.Validate(Enumerable.Repeat(Straight(), 200001), allowBarIntersections: true),
            d => d.Code == "COMPOSITE_PLAN_TOO_LARGE" && d.IsBlocking);
        Assert.Throws<OperationCanceledException>(() => CompositePlanValidator.Validate(new[] { Straight() }, new CancellationToken(true), allowBarIntersections: true));
    }

    [Fact]
    public void AllowedCrossingsDoNotCheckUncertainCurveClearance()
    {
        var arc = Straight(); arc.Centerline = new() { new() { Start = new(-50, 0, 0), Midpoint = new(0, 50, 0), End = new(50, 0, 0) } };
        var crossing = Straight(); crossing.Centerline = new() { new() { Start = new(0, 61.99, -20), End = new(0, 61.99, 20) } };
        Assert.Contains(CompositePlanValidator.Validate(new[] { arc, crossing }),
            d => d.Code == "COMPOSITE_CURVE_CLEARANCE_UNCERTAIN" && d.IsBlocking);
        Assert.Empty(CompositePlanValidator.Validate(new[] { arc, crossing }, allowBarIntersections: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AllowedCrossingsStillRejectCompleteDuplicatesInEitherDirection(int existingCount)
    {
        var duplicate = Straight(); duplicate.Start = new(100, 0); duplicate.End = new(0, 0);
        duplicate.Placement.TypeKey = "another-type";
        var diagnostic = Assert.Single(CompositePlanValidator.Validate(new[] { Straight(), duplicate },
            existingBarCount: existingCount, allowBarIntersections: true));
        Assert.Equal("DUPLICATE_BAR", diagnostic.Code);
        Assert.True(diagnostic.IsBlocking);
    }

    [Fact]
    public void AllowedCrossingsStillRejectReversedCurvedDuplicates()
    {
        var a = Straight(); a.Centerline = new() { new() { Start = new(-50, 0, 0), Midpoint = new(0, 50, 0), End = new(50, 0, 0) } };
        var b = Straight(); b.Centerline = new() { new() { Start = new(50, 0, 0), Midpoint = new(0, 50, 0), End = new(-50, 0, 0) } };
        Assert.Contains(CompositePlanValidator.Validate(new[] { a, b }, allowBarIntersections: true),
            d => d.Code == "DUPLICATE_BAR" && d.IsBlocking);
    }

    [Fact]
    public void ReversedUPathsAreDuplicatesButPartialOverlapsAreAllowed()
    {
        var a = Straight(); a.Centerline = new() {
            new() { Start = new(100, 0, 0), End = new(0, 0, 0) },
            new() { Start = new(0, 0, 0), End = new(0, 0, 100) },
            new() { Start = new(0, 0, 100), End = new(100, 0, 100) } };
        var b = Straight(); b.Centerline = a.Centerline.AsEnumerable().Reverse().Select(s => new CenterlineSegment { Start = s.End, End = s.Start }).ToList();
        Assert.Equal("DUPLICATE_BAR", Assert.Single(CompositePlanValidator.Validate(new[] { a, b }, allowBarIntersections: true)).Code);
        b.Centerline[0].Start = new(150, 0, 100);
        Assert.Empty(CompositePlanValidator.Validate(new[] { a, b }, allowBarIntersections: true));
    }

    [Fact]
    public void ImplicitAndExplicitStraightAxesAreRecognizedAsDuplicates()
    {
        var b = Straight(); b.Centerline = new() { new() { Start = new(100, 0, -40), End = new(0, 0, -40) } };
        Assert.Equal("DUPLICATE_BAR", Assert.Single(CompositePlanValidator.Validate(new[] { Straight(), b }, allowBarIntersections: true)).Code);
    }

    [Fact]
    public void ExistingDuplicatesAloneDoNotBlockAnUnrelatedNewBar()
    {
        var other = Straight(); other.Placement.ElevationMm -= 50;
        Assert.Empty(CompositePlanValidator.Validate(new[] { Straight(), Straight(), other },
            existingBarCount: 2, allowBarIntersections: true));
    }

    [Fact]
    public void AllowedCrossingsSkipPairLimitsForDenseButDistinctBars()
    {
        // More than two million close pairs: pair scanning used to hit its work limit
        // even after the user allowed intersections.
        var bars = Enumerable.Range(0, 2002).Select(i =>
        {
            var bar = Straight(); bar.Start = new(0, i * .001); bar.End = new(100, i * .001); return bar;
        });
        Assert.Empty(CompositePlanValidator.Validate(bars, allowBarIntersections: true));
    }

    private static PlannedBar Straight() => new()
    {
        Start = new(0, 0), End = new(100, 0),
        Placement = new() { TypeKey = "12", Component = "test", ElevationMm = -40, ModelDiameterMm = 12 }
    };
}
