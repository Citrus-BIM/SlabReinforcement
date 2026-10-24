using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class BarContainmentValidatorTests
{
    [Fact]
    public void StraightBarCanTouchPhysicalSideAndTopCover()
    {
        Assert.Empty(Validate(Straight(31, 31, 3969, 31, -31)));
        Assert.NotEmpty(Validate(Straight(30.95, 31, 3969, 31, -31)));
        Assert.NotEmpty(Validate(Straight(31, 31, 3969, 31, -30.95)));
    }

    [Fact]
    public void BottomCoverUsesFullPhysicalDiameter()
    {
        Assert.Empty(Validate(Straight(31, 31, 3969, 31, -189)));
        Assert.NotEmpty(Validate(Straight(31, 31, 3969, 31, -189.05)));
    }

    [Fact]
    public void LineCannotCrossAHoleEvenWhenBothEndsAreInsideConcrete()
    {
        var slab = Slab(); slab.Holes.Add(new() { new(1500, 1000), new(2500, 1000), new(2500, 2000), new(1500, 2000) });
        Assert.NotEmpty(BarContainmentValidator.Validate(new[] { Straight(300, 1500, 3700, 1500, -31) }, new[] { slab }, 220, 25, 25, 25));
    }

    [Fact]
    public void ArcCanLeaveTheSlabEvenWhenItsChordStaysInside()
    {
        var bar = Straight(100, 100, 200, 100, -31);
        bar.Centerline = new() { new() { Start = new(100, 100, -31), Midpoint = new(150, -100, -31), End = new(200, 100, -31) } };
        Assert.NotEmpty(Validate(bar));
    }

    [Fact]
    public void ArcClearanceUncertaintyCannotConsumeTheCoverReserve()
    {
        var bar = Straight(0, 0, 0, 0, -31);
        // Semicircle touching the y=31 axis boundary; its chord lies farther inside.
        bar.Centerline = new() { new() { Start = new(100, 81, -31), Midpoint = new(150, 31, -31), End = new(200, 81, -31) } };
        Assert.Contains(Validate(bar), d => d.IsBlocking);
        bar.Centerline[0] = new() { Start = new(100, 81.05, -31), Midpoint = new(150, 31.05, -31), End = new(200, 81.05, -31) };
        Assert.Empty(Validate(bar));
    }

    [Fact]
    public void UCurvesUseExactVerticalExtremaAndTheSideCoverReserve()
    {
        var plan = PerimeterPlanner.Build(new PerimeterRequest
        {
            Slabs = new() { Slab() }, TypeKey = "8", DiameterMm = 8, ModelDiameterMm = 8, BendDiameterMm = 32,
            ThicknessMm = 220, TopCoverMm = 25, BottomCoverMm = 25, SideCoverMm = 25.05,
            SpacingMm = 200, LegLengthMm = 500, Outer = true, UseUbars = true
        });
        Assert.True(plan.CanCreate);
        Assert.Empty(BarContainmentValidator.Validate(plan.Bars, new[] { Slab() }, 220, 25, 25, 25));
    }

    [Fact]
    public void InvalidInputAndCancellationAreNotAccepted()
    {
        var bar = Straight(31, 31, 3969, 31, double.NaN);
        Assert.NotEmpty(Validate(bar));
        Assert.Throws<OperationCanceledException>(() => BarContainmentValidator.Validate(new[] { bar }, new[] { Slab() }, 220, 25, 25, 25, new CancellationToken(true)));
    }

    [Fact]
    public void VerticalUBendHasAnExactLinearProjectionAtTheReservedSideCover()
    {
        var plan = PerimeterPlanner.Build(new PerimeterRequest
        {
            Slabs = new() { Slab() }, TypeKey = "8", DiameterMm = 8, ModelDiameterMm = 8, BendDiameterMm = 32,
            ThicknessMm = 220, TopCoverMm = 25, BottomCoverMm = 25, SideCoverMm = 25.05,
            SpacingMm = 200, LegLengthMm = 500, Outer = true, UseUbars = true
        });
        Assert.True(plan.CanCreate);
        Assert.Empty(BarContainmentValidator.Validate(plan.Bars, new[] { Slab() }, 220, 25, 25, 25.05));
        Assert.NotEmpty(BarContainmentValidator.Validate(plan.Bars, new[] { Slab() }, 220, 25, 25, 25.051));
    }

    private static List<PlanDiagnostic> Validate(PlannedBar bar) => BarContainmentValidator.Validate(new[] { bar }, new[] { Slab() }, 220, 25, 25, 25);
    private static PlannedBar Straight(double x1, double y1, double x2, double y2, double z) => new()
    {
        Start = new(x1, y1), End = new(x2, y2), Placement = new() { ElevationMm = z, ModelDiameterMm = 12 }
    };
    private static PolygonInput Slab() => new() { Shell = new() { new(0, 0), new(4000, 0), new(4000, 3000), new(0, 3000) } };
}
