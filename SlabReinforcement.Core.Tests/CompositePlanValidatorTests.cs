using System.Diagnostics;
using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class CompositePlanValidatorTests
{
    [Fact]
    public void ExactDuplicateBackgroundAndZoneBarsAreBlocked()
    {
        AssertCollision(Straight(0, 0, 1000, 0, -31), Straight(0, 0, 1000, 0, -31));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(11.99, true)]
    [InlineData(12, false)]
    [InlineData(12.01, false)]
    public void CrossedGridsUseThreeDimensionalDiameterClearance(double dz, bool collision)
    {
        var diagnostics = CompositePlanValidator.Validate(new[] { Straight(-100, 0, 100, 0, 0), Straight(0, -100, 0, 100, dz) });
        Assert.Equal(collision, diagnostics.Any(d => d.IsBlocking));
    }

    [Theory]
    [InlineData(-0.01, true)]
    [InlineData(0, false)]
    [InlineData(0.01, false)]
    public void FlatEndsMayTouchOrHaveASmallGap(double endGap, bool collision)
    {
        var diagnostics = CompositePlanValidator.Validate(new[] { Straight(0, 0, 100, 0, 0), Straight(100 + endGap, 0, 200, 0, 0) });
        Assert.Equal(collision, diagnostics.Any(d => d.IsBlocking));
    }

    [Fact]
    public void DifferentDiameterParallelBarsUseActualHeight()
    {
        Assert.Empty(CompositePlanValidator.Validate(new[] { Straight(0, 0, 1000, 0, -28, 6), Straight(0, 20, 1000, 20, -45, 40) }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(89)]
    public void DiagonalRotationDoesNotChangeCollision(double degrees)
    {
        var a = Spatial(new(0, 0, 0), new(100, 0, 0));
        var b = Spatial(new(90, 5, 0), new(150, 65, 0));
        Rotate(a, degrees); Rotate(b, degrees);
        AssertCollision(a, b);
    }

    [Fact]
    public void NearbyDiagonalFlatEndsDoNotUseSphericalEndCaps()
    {
        // Both cylinders lie in x <= 0 or x >= 1, although their endpoint spheres overlap.
        Assert.Empty(CompositePlanValidator.Validate(new[] {
            Spatial(new(-100, 0, 0), new(0, 0, 0)), Spatial(new(1, 0, 0), new(101, 5, 0), 0.1)
        }));
    }

    [Fact]
    public void EndOfOneBarCanPenetrateTheSideOfAnother()
    {
        AssertCollision(Spatial(new(-100, 0, 0), new(-5, 0, 0)), Spatial(new(0, -100, 0), new(0, 100, 0)));
    }

    [Fact]
    public void UShapeUsesAbsoluteCenterlineHeightOnlyOnce()
    {
        var u = UShape();
        u.Placement.ElevationMm = 9000; // Centerline already contains absolute local Z.
        AssertCollision(u, Spatial(new(0, -20, -50), new(0, 20, -50)));
        Assert.Empty(CompositePlanValidator.Validate(new[] { u, Spatial(new(0, -20, -200), new(0, 20, -200)) }));
    }

    [Fact]
    public void ArcMidpointCollisionCannotBeMissedByItsChord()
    {
        var arc = Arc(new(-50, 0, 0), new(0, 50, 0), new(50, 0, 0));
        AssertCollision(arc, Spatial(new(0, 49, -20), new(0, 49, 20)));
        Assert.Empty(CompositePlanValidator.Validate(new[] { arc, Spatial(new(-10, 0, -20), new(-10, 0, 20)) }));
    }

    [Fact]
    public void ArcClearanceIsConservativeWithinPointZeroTwoMillimetres()
    {
        var arc = Arc(new(-50, 0, 0), new(0, 50, 0), new(50, 0, 0));
        AssertCollision(arc, Spatial(new(0, 61.95, -20), new(0, 61.95, 20)));
        Assert.Empty(CompositePlanValidator.Validate(new[] { arc, Spatial(new(0, 62.1, -20), new(0, 62.1, 20)) }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e300)]
    public void InvalidDiameterIsBlocking(double diameter)
    {
        Assert.Contains(CompositePlanValidator.Validate(new[] { Straight(0, 0, 100, 0, 0, diameter) }), d => d.Code == "INVALID_COMPOSITE_BAR" && d.IsBlocking);
    }

    [Fact]
    public void DegenerateOrDisconnectedCurvesAreBlocking()
    {
        Assert.Contains(CompositePlanValidator.Validate(new[] { Arc(new(0, 0, 0), new(50, 0, 0), new(100, 0, 0)) }), d => d.Code == "INVALID_COMPOSITE_BAR");
        var u = UShape(); u.Centerline[1].Start = new(50, 0, 0);
        Assert.Contains(CompositePlanValidator.Validate(new[] { u }), d => d.Code == "INVALID_COMPOSITE_BAR");
    }

    [Fact]
    public void MissingPlacementAndNonfiniteCoordinatesAreBlocking()
    {
        Assert.Contains(CompositePlanValidator.Validate(new[] { new PlannedBar() }), d => d.Code == "INVALID_COMPOSITE_BAR");
        Assert.Contains(CompositePlanValidator.Validate(new[] { Spatial(new(double.NaN, 0, 0), new(100, 0, 0)) }), d => d.Code == "INVALID_COMPOSITE_BAR");
    }

    [Fact]
    public void MoreThanTwoHundredThousandBarsAreRejectedBeforeUnboundedExpansion()
    {
        var bar = Straight(0, 0, 100, 0, 0);
        Assert.Contains(CompositePlanValidator.Validate(Enumerable.Repeat(bar, 200001)), d => d.Code == "COMPOSITE_PLAN_TOO_LARGE");
    }

    [Fact]
    public void CancellationIsObserved()
    {
        using var source = new CancellationTokenSource(); source.Cancel();
        Assert.Throws<OperationCanceledException>(() => CompositePlanValidator.Validate(new[] { Straight(0, 0, 100, 0, 0) }, source.Token));
    }

    [Fact]
    public void CancellationDuringInputEnumerationIsObserved()
    {
        using var source = new CancellationTokenSource();
        IEnumerable<PlannedBar> Input()
        {
            for (var i = 0; i < 100; i++)
            {
                if (i == 10) source.Cancel();
                yield return Straight(0, i * 100, 1000, i * 100, 0);
            }
        }
        Assert.Throws<OperationCanceledException>(() => CompositePlanValidator.Validate(Input(), source.Token));
    }

    [Fact]
    public void TenThousandParallelBarsRemainDeterministicAndIndexed()
    {
        var bars = Enumerable.Range(0, 10000).Select(i => Straight(0, i * 200, 10000, i * 200, -31)).ToList();
        var watch = Stopwatch.StartNew();
        Assert.Empty(CompositePlanValidator.Validate(bars));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"Indexed validation took {watch.Elapsed}.");
        Assert.Empty(CompositePlanValidator.Validate(bars.AsEnumerable().Reverse()));
    }

    [Fact]
    public void ExistingAgainstExistingIsIgnoredButNewAgainstExistingIsChecked()
    {
        var duplicate = Straight(0, 0, 100, 0, 0);
        var existing = new[] { duplicate, duplicate };
        Assert.Empty(CompositePlanValidator.Validate(existing, existingBarCount: 2));
        Assert.Contains(CompositePlanValidator.Validate(existing.Append(duplicate), existingBarCount: 2), d => d.Code == "COMPOSITE_BAR_COLLISION");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void InvalidExistingCountCannotDisableCollisionChecks(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CompositePlanValidator.Validate(new[] { Straight(0, 0, 100, 0, 0) }, existingBarCount: count));
    }

    [Fact]
    public void NearlyParallelLongBarsCannotHideAnInteriorCrossing()
    {
        AssertCollision(Spatial(new(0, 0, 0), new(100000000, 0, 0), 0.01),
            Spatial(new(0, 1, 0), new(100000000, -1, 0), 0.01));
    }

    [Fact]
    public void FourBackgroundLayersCanTouchWithoutPenetrating()
    {
        var request = BackgroundRequest();
        var plan = BackgroundPlanner.Build(request);
        Assert.True(plan.CanCreate);
        Assert.Empty(CompositePlanValidator.Validate(plan.Bars));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RectanglePerimeterCanCombineUbarsAndRoundedLongitudinalBars(bool longitudinal)
    {
        var request = PerimeterRequest(); request.UseLongitudinalBars = longitudinal;
        var plan = PerimeterPlanner.Build(request);
        Assert.True(plan.CanCreate, string.Join(";", plan.Diagnostics.Select(d => d.Message)));
        Assert.Empty(CompositePlanValidator.Validate(plan.Bars));
    }

    [Fact]
    public void VerticalOffsetAloneDoesNotGuaranteeBackgroundPerimeterCompatibility()
    {
        var perimeterRequest = PerimeterRequest();
        var perimeter = PerimeterPlanner.Build(perimeterRequest);
        var backgroundRequest = BackgroundRequest();
        foreach (var layer in backgroundRequest.Layers) layer.SpacingMm = 100;
        backgroundRequest.OuterLayerOffsetMm = PerimeterPlanner.ReservedDepth(perimeterRequest);
        var background = BackgroundPlanner.Build(backgroundRequest);
        Assert.True(perimeter.CanCreate); Assert.True(background.CanCreate);
        // U stems can collide with straight background ends. Reserving only vertical
        // bands does not reserve the horizontal space occupied by the stems.
        Assert.Contains(CompositePlanValidator.Validate(perimeter.Bars.Concat(background.Bars)), d => d.Code == "COMPOSITE_BAR_COLLISION");
    }

    private static BackgroundRequest BackgroundRequest() => new()
    {
        Slabs = new() { Slab() }, ThicknessMm = 220, TopCoverMm = 25, BottomCoverMm = 25, SideCoverMm = 25,
        Layers = (from top in new[] { false, true } from alongX in new[] { false, true }
            select new BackgroundLayer { Key = $"{top}/{alongX}", TypeKey = "12", Top = top, AlongX = alongX, DiameterMm = 12, ModelDiameterMm = 12, SpacingMm = 200 }).ToList()
    };
    private static PerimeterRequest PerimeterRequest() => new()
    {
        Slabs = new() { Slab() }, TypeKey = "8", DiameterMm = 8, ModelDiameterMm = 8, BendDiameterMm = 32,
        ThicknessMm = 220, TopCoverMm = 25, BottomCoverMm = 25, SideCoverMm = 25, SpacingMm = 200, LegLengthMm = 500,
        Outer = true, Openings = true, UseUbars = true
    };
    private static PolygonInput Slab() => new() { Shell = new() { new(0, 0), new(4000, 0), new(4000, 3000), new(0, 3000) } };

    private static void AssertCollision(params PlannedBar[] bars) => Assert.Contains(CompositePlanValidator.Validate(bars), d => d.Code == "COMPOSITE_BAR_COLLISION" && d.IsBlocking);
    private static PlannedBar Straight(double x1, double y1, double x2, double y2, double z, double diameter = 12) => new()
    {
        Start = new(x1, y1), End = new(x2, y2), SpecKey = "test",
        Placement = new() { TypeKey = "D" + diameter, Component = "test", ElevationMm = z, ModelDiameterMm = diameter }
    };
    private static PlannedBar Spatial(Point3 start, Point3 end, double diameter = 12)
    {
        var b = Straight(0, 0, 0, 0, 0, diameter);
        b.Centerline = new() { new() { Start = start, End = end } };
        return b;
    }
    private static PlannedBar Arc(Point3 start, Point3 midpoint, Point3 end)
    {
        var b = Spatial(start, end); b.Centerline[0].Midpoint = midpoint; return b;
    }
    private static PlannedBar UShape()
    {
        var b = Spatial(new(100, 0, 0), new(0, 0, 0));
        b.Centerline.Add(new() { Start = new(0, 0, 0), End = new(0, 0, -100) });
        b.Centerline.Add(new() { Start = new(0, 0, -100), End = new(100, 0, -100) });
        return b;
    }
    private static void Rotate(PlannedBar bar, double degrees)
    {
        var angle = degrees * Math.PI / 180; var c = Math.Cos(angle); var s = Math.Sin(angle);
        Point3 R(Point3 p) => new(p.X * c - p.Y * s, p.X * s + p.Y * c, p.Z);
        foreach (var segment in bar.Centerline) { segment.Start = R(segment.Start); segment.End = R(segment.End); }
    }
}
