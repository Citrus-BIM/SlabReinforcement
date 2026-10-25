using NetTopologySuite.Geometries;
using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class PlannerTests
{
    [Fact]
    public void AnchorsRoundsAndGroupsRectangularDemand()
    {
        var request = Request(Rect(0, 0, 3000, 2000), Rect(300, 300, 2700, 1700));
        request.Specifications[0].AnchorageMm = 125;
        request.Options.RoundIncrementMm = 100;
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.NotEmpty(plan.Groups);
        Assert.All(plan.Bars, b => {
            Assert.True(b.Start.X <= 175.000001);
            Assert.True(b.End.X >= 2824.999999);
            Assert.Equal(2700, b.End.X - b.Start.X, 6);
        });
        Assert.Equal(plan.Bars.Count, plan.Groups.Sum(g => g.Count));
        AssertGroupsMatchBars(plan, true);
    }

    [Fact]
    public void RectangularHoleSplitsBarsAndRemainsEmptyAfterGrouping()
    {
        var request = Request(Rect(0, 0, 3000, 2000), Rect(0, 0, 3000, 2000));
        request.Slabs[0].Holes.Add(Rect(1100, 500, 1900, 1500));
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        AssertSafe(request, plan);
        Assert.Contains(plan.Bars, b => b.Start.Y > 500 && b.Start.Y < 1500 && b.End.X < 1100);
        Assert.Contains(plan.Bars, b => b.Start.Y > 500 && b.Start.Y < 1500 && b.Start.X > 1900);
        AssertGroupsMatchBars(plan, true);
    }

    [Fact]
    public void ConcaveLShapeNeverBridgesTheMissingCorner()
    {
        var shell = new List<Point2> { new(0, 0), new(3000, 0), new(3000, 800), new(1000, 800), new(1000, 2000), new(0, 2000) };
        var request = Request(shell, Rect(100, 100, 2900, 1900));
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        AssertSafe(request, plan);
        Assert.Contains(plan.Bars, b => b.Start.Y > 1000 && b.End.X < 1000);
    }

    [Fact]
    public void DiagonalHoleCannotBeCrossedByExpandingSuccessiveGroupRows()
    {
        var request = Request(Rect(0, 0, 3000, 1400), new List<Point2> { new(100, 200), new(925, 200), new(1125, 1200), new(100, 1200) });
        request.Faces.Add(new FaceInput { Id = "right", ColorKey = "red", Vertices = new List<Point2> { new(1475, 200), new(2900, 200), new(2900, 1200), new(1675, 1200) } });
        request.Slabs[0].Holes.Add(new List<Point2> { new(1000, 200), new(1400, 200), new(1600, 1200), new(1200, 1200) });
        request.Options.MergeToleranceMm = 50;
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        AssertSafe(request, plan);
        AssertGroupsMatchBars(plan, true);
    }

    [Fact]
    public void SlotNarrowerThanMergeToleranceDoesNotGetFilled()
    {
        var request = Request(Rect(0, 0, 3000, 2000), Rect(0, 0, 3000, 2000));
        request.Slabs[0].Holes.Add(Rect(1450, 300, 1470, 1700));
        request.Options.MergeToleranceMm = 100;
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        AssertSafe(request, plan);
    }

    [Fact]
    public void NarrowDisconnectedDemandDoesNotCreateIsolatedMidpointRows()
    {
        var request = Request(Rect(0, 0, 3000, 2000), Rect(300, 301, 800, 309));
        request.Faces.Add(new FaceInput { Id = "narrow2", ColorKey = "red", Vertices = Rect(1800, 1101, 2300, 1109) });
        var plan = ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Empty(plan.Bars);
        Assert.Empty(plan.Groups);
        Assert.Contains(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED");
    }

    [Fact]
    public void InsufficientAnchorageIsBlockingAndDoesNotCreateShortenedBar()
    {
        var request = Request(Rect(0, 0, 1000, 1000), Rect(100, 200, 900, 800));
        request.Specifications[0].AnchorageMm = 150;
        var plan = ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.IsBlocking && d.Code == "ANCHORAGE_DOES_NOT_FIT");
        Assert.Empty(plan.Bars);
    }

    [Fact]
    public void AnchorageDoesNotJumpAcrossAHoleToAnotherHostInterval()
    {
        var request = Request(Rect(0, 0, 3000, 1000), Rect(300, 300, 1100, 700));
        request.Slabs[0].Holes.Add(Rect(1200, 100, 1600, 900));
        request.Specifications[0].AnchorageMm = 150;
        var plan = ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "ANCHORAGE_DOES_NOT_FIT");
    }

    [Fact]
    public void UsesLegacyCutLengthThenIncrementForLongerBars()
    {
        var request = Request(Rect(0, 0, 13000, 1000), Rect(500, 200, 1600, 800));
        request.Options.UseCutLengths = true;
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.All(plan.Bars, b => Assert.Equal(1170, b.End.X - b.Start.X, 6));
        request.Faces[0].Vertices = Rect(500, 200, 11003, 800);
        request.Options.RoundIncrementMm = 100;
        plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.All(plan.Bars, b => Assert.Equal(10600, b.End.X - b.Start.X, 6));
    }

    [Fact]
    public void ShiftsRoundingSurplusInsideSameHostIntervalWithoutLosingAnchorage()
    {
        var request = Request(Rect(0, 0, 1600, 1000), Rect(100, 200, 1100, 800));
        request.Specifications[0].AnchorageMm = 50;
        request.Options.RoundIncrementMm = 200;
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.All(plan.Bars, b => {
            Assert.True(b.Start.X <= 50.000001);
            Assert.True(b.End.X >= 1149.999999);
            Assert.Equal(1200, b.End.X - b.Start.X, 6);
        });
        AssertSafe(request, plan);
    }

    [Fact]
    public void HigherAreaPerMetreWinsOverlappingSameMaterialDemand()
    {
        var request = Request(Rect(0, 0, 3000, 2000), Rect(300, 300, 1800, 1700));
        request.Faces.Add(new FaceInput { Id = "blue-face", ColorKey = "blue", Vertices = Rect(1200, 300, 2700, 1700) });
        request.Specifications.Add(new ReinforcementSpec { Key = "strong", ColorKey = "blue", MaterialKey = "A500", DiameterMm = 16, SpacingMm = 200 });
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.Contains(plan.Bars, b => b.SpecKey == "strong");
        Assert.All(plan.Bars.Where(b => b.SpecKey == "base"), b => Assert.True(b.End.X <= 1200.000001));
        Assert.Equal(2400 * 1400, plan.DemandRegions.Sum(Area), 4);
    }

    [Fact]
    public void OverlappingDifferentMaterialsAreBlocking()
    {
        var request = Request(Rect(0, 0, 3000, 2000), Rect(300, 300, 1800, 1700));
        request.Faces.Add(new FaceInput { Id = "blue-face", ColorKey = "blue", Vertices = Rect(1200, 300, 2700, 1700) });
        request.Specifications.Add(new ReinforcementSpec { Key = "other", ColorKey = "blue", MaterialKey = "A400", DiameterMm = 16, SpacingMm = 200 });
        var plan = ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "INCOMPATIBLE_MATERIAL_OVERLAP" && d.IsBlocking);
    }

    [Fact]
    public void DuplicateFacesAndInputOrderingDoNotChangeBarsOrDemandArea()
    {
        var request = Request(Rect(0, 0, 3000, 2000), Rect(300, 300, 1200, 1700));
        request.Faces.Add(new FaceInput { Id = "second", ColorKey = "red", Vertices = Rect(900, 300, 2400, 1700) });
        var original = ReinforcementPlanner.Build(request);
        request.Faces.Add(request.Faces[0]);
        request.Faces.Reverse();
        var duplicate = ReinforcementPlanner.Build(request);
        Assert.True(original.CanCreate, Diagnostics(original));
        Assert.True(duplicate.CanCreate, Diagnostics(duplicate));
        Assert.Equal(original.Bars.Select(Serialize), duplicate.Bars.Select(Serialize));
        Assert.Equal(2100 * 1400, duplicate.DemandRegions.Sum(Area), 4);
    }

    [Fact]
    public void AlongYProducesVerticalBarsAndGroupsAdvanceAlongX()
    {
        var request = Request(Rect(0, 0, 3000, 2000), Rect(300, 300, 2700, 1700));
        request.Options.AlongX = false;
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.All(plan.Bars, b => {
            Assert.Equal(b.Start.X, b.End.X, 6);
            Assert.True(b.End.Y > b.Start.Y);
        });
        AssertSafe(request, plan);
        AssertGroupsMatchBars(plan, false);
    }

    [Theory]
    [InlineData(0, 200, 0)]
    [InlineData(12, 0, 0)]
    [InlineData(double.NaN, 200, 0)]
    [InlineData(12, double.PositiveInfinity, 0)]
    [InlineData(12, 200, -1)]
    public void InvalidSpecificationIsBlocking(double diameter, double spacing, double anchorage)
    {
        var request = Request(Rect(0, 0, 1000, 1000), Rect(200, 200, 800, 800));
        request.Specifications[0].DiameterMm = diameter;
        request.Specifications[0].SpacingMm = spacing;
        request.Specifications[0].AnchorageMm = anchorage;
        var plan = ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "INVALID_SPECIFICATION" && d.IsBlocking);
    }

    [Fact]
    public void UnmappedFaceColorIsBlocking()
    {
        var request = Request(Rect(0, 0, 1000, 1000), Rect(200, 200, 800, 800));
        request.Specifications.Clear();
        var plan = ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "UNMAPPED_COLOR" && d.IsBlocking);
    }

    [Fact]
    public void InvalidFaceAndHostReturnDiagnosticWithoutRepairingGeometry()
    {
        var crossed = new List<Point2> { new(0, 0), new(1000, 1000), new(0, 1000), new(1000, 0) };
        var request = Request(Rect(0, 0, 1000, 1000), crossed);
        var plan = ReinforcementPlanner.Build(request);
        Assert.Contains(plan.Diagnostics, d => d.Code == "INVALID_FACE" && d.SourceId == "face");
        request.Slabs[0].Shell = crossed;
        request.Faces[0].Vertices = Rect(200, 200, 800, 800);
        plan = ReinforcementPlanner.Build(request);
        Assert.Contains(plan.Diagnostics, d => d.Code == "INVALID_SLAB");
    }

    [Fact]
    public void CancellationIsObservedBeforeGeometryProcessing()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => ReinforcementPlanner.Build(new PlanRequest(), cancel.Token));
    }

    [Fact]
    public void ExcessiveRowCountIsBlockedBeforeAllocatingBars()
    {
        var request = Request(Rect(0, 0, 1000, 1000000), Rect(100, 100, 900, 999900));
        request.Specifications[0].SpacingMm = 0.001;
        request.Specifications[0].DiameterMm = 0.0005;
        var plan = ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "PLAN_TOO_LARGE" && d.IsBlocking);
    }

    [Fact]
    public void RoundedSeparateDemandIntervalsCannotGenerateDuplicateBars()
    {
        var request = Request(Rect(0, 0, 1232, 1000), Rect(200, 200, 400, 800));
        request.Faces.Add(new FaceInput { Id = "second", ColorKey = "red", Vertices = Rect(700, 200, 900, 800) });
        request.Options.UseCutLengths = true;
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.Equal(plan.Bars.Count, plan.Bars.Select(Serialize).Distinct().Count());
        AssertGroupsMatchBars(plan, true);
    }

    [Fact]
    public void OutsideFacesTouchingTheHostDoNotPolluteAreaOverlayWithPointsOrLines()
    {
        var request = Request(Rect(0, 0, 3000, 2000), Rect(300, 300, 1200, 1700));
        request.Faces.Add(new FaceInput { Id = "outside-edge", ColorKey = "red", Vertices = Rect(3000, 200, 3500, 800) });
        request.Faces.Add(new FaceInput { Id = "outside-point", ColorKey = "red", Vertices = Rect(-500, -500, 0, 0) });
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.Equal(900 * 1400, plan.DemandRegions.Sum(Area), 4);
        AssertSafe(request, plan);
    }

    [Fact]
    public void NonintegralHostIntervalCannotSilentlyShortenRoundedLength()
    {
        var request = Request(Rect(0, 0, 1000.5, 1000), Rect(0, 100, 1000.5, 900));
        var plan = ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Empty(plan.Bars);
        Assert.Contains(plan.Diagnostics, d => d.Code == "ROUNDING_DOES_NOT_FIT");
    }

    [Fact]
    public void NullRequestAndInvalidOptionsReturnBlockingDiagnostic()
    {
        Assert.Contains(ReinforcementPlanner.Build(null).Diagnostics, d => d.Code == "INVALID_REQUEST");
        var request = Request(Rect(0, 0, 1000, 1000), Rect(200, 200, 800, 800));
        request.Options.EdgeCoverMm = double.NaN;
        Assert.Contains(ReinforcementPlanner.Build(request).Diagnostics, d => d.Code == "INVALID_OPTIONS");
    }

    [Fact]
    public void TriangleMeshFacesUnionIntoOneUnfragmentedDemandRegion()
    {
        var request = Request(Rect(0, 0, 3000, 2000), new List<Point2> { new(300, 300), new(2700, 300), new(2700, 1700) });
        request.Faces.Add(new FaceInput { Id = "tri2", ColorKey = "red", Vertices = new List<Point2> { new(300, 300), new(2700, 1700), new(300, 1700) } });
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.Single(plan.DemandRegions);
        Assert.Single(plan.Groups);
        Assert.All(plan.Bars, b => Assert.Equal(2400, b.End.X - b.Start.X, 6));
    }

    [Fact]
    public void EntireDemandRemovedByCoverIsBlocking()
    {
        var request = Request(Rect(0, 0, 1000, 1000), Rect(1, 200, 20, 800));
        var plan = ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "DEMAND_OUTSIDE_COVER");
    }

    [Fact]
    public void DenseRegularRowsSplitAtRevitArrayLimitWithoutChangingBars()
    {
        var request = Request(Rect(0, 0, 1000, 30000), Rect(100, 100, 900, 29900));
        request.Specifications[0].SpacingMm = 10;
        request.Specifications[0].DiameterMm = 6;
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.Equal(3, plan.Groups.Count);
        Assert.All(plan.Groups, g => Assert.InRange(g.Count, 1, 1002));
        Assert.Equal(2981, plan.Bars.Count);
        AssertGroupsMatchBars(plan, true);
    }

    [Fact]
    public void ModelDiameterDeterminesPhysicalEdgeAndHoleClearance()
    {
        var request = Request(Rect(0, 0, 3000, 2000), Rect(0, 100, 3000, 1900));
        request.Slabs[0].Holes.Add(Rect(1200, 500, 1800, 1500));
        request.Specifications[0].DiameterMm = 16;
        request.Specifications[0].ModelDiameterMm = 18;
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.Contains(plan.Bars, b => Math.Abs(b.Start.X - 34) < 1e-6);
        Assert.Contains(plan.Bars, b => b.Start.Y > 500 && b.Start.Y < 1500 && Math.Abs(b.End.X - 1166) < 1e-6);
        AssertSafe(request, plan);
    }

    [Fact]
    public void MissingModelDiameterFallsBackToNominalDiameter()
    {
        var request = Request(Rect(0, 0, 3000, 2000), Rect(0, 100, 3000, 1900));
        request.Specifications[0].DiameterMm = 16;
        var implicitNominal = ReinforcementPlanner.Build(request);
        request.Specifications[0].ModelDiameterMm = 16;
        var explicitNominal = ReinforcementPlanner.Build(request);
        Assert.True(implicitNominal.CanCreate, Diagnostics(implicitNominal));
        Assert.Equal(implicitNominal.Bars.Select(Serialize), explicitNominal.Bars.Select(Serialize));
        Assert.Contains(implicitNominal.Bars, b => Math.Abs(b.Start.X - 33) < 1e-6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidModelDiameterIsBlocking(double diameter)
    {
        var request = Request(Rect(0, 0, 1000, 1000), Rect(200, 200, 800, 800));
        request.Specifications[0].ModelDiameterMm = diameter;
        var plan = ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "INVALID_SPECIFICATION");
    }

    [Fact]
    public void ModelDiameterDoesNotChangeNominalAreaPriority()
    {
        var request = Request(Rect(0, 0, 3000, 2000), Rect(300, 300, 2700, 1700));
        request.Specifications[0].DiameterMm = 16;
        request.Specifications[0].ModelDiameterMm = 18;
        request.Specifications.Add(new ReinforcementSpec
        {
            Key = "nominal17", ColorKey = "blue", MaterialKey = "A500", DiameterMm = 17, ModelDiameterMm = 17, SpacingMm = 200
        });
        request.Faces.Add(new FaceInput { Id = "blue", ColorKey = "blue", Vertices = Rect(300, 300, 2700, 1700) });
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.All(plan.Bars, b => Assert.Equal("nominal17", b.SpecKey));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AdjacentNarrowLobesExtendTwoNearbyRowsIntoOneZoneWithoutAddingBars(bool alongX)
    {
        var face = new List<Point2>
        {
            new(300, 200), new(1000, 200), new(1000, 201), new(2300, 201),
            new(2300, 209), new(1000, 209), new(1000, 401), new(2300, 401),
            new(2300, 409), new(1000, 409), new(1000, 1200), new(300, 1200)
        };
        if (!alongX) face = face.Select(p => new Point2(p.Y, p.X)).ToList();
        var request = Request(alongX ? Rect(0, 0, 3000, 2000) : Rect(0, 0, 2000, 3000), face);
        request.Options.AlongX = alongX;
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.Contains(plan.Bars, b => (alongX ? b.End.X : b.End.Y) >= 2300);
        Assert.Equal(6, plan.Bars.Count);
        Assert.Equal(2, plan.Bars.Count(b => (alongX ? b.End.X : b.End.Y) >= 2300));
        Assert.Contains(plan.Diagnostics, d => d.Code == "ROWS_EXTENDED_FOR_COVERAGE" && !d.IsBlocking);
        AssertSafe(request, plan);
        AssertGroupsMatchBars(plan, alongX);
    }

    [Fact]
    public void SingleSupplementalSegmentsBesideAnOpeningAreSkipped()
    {
        var request = FingerRequest(301, 309, 2300);
        request.Slabs[0].Holes.Add(Rect(1300, 290, 1500, 320));
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.Contains(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED");
        Assert.DoesNotContain(plan.Bars, b => b.Start.Y > 301 && b.Start.Y < 309);
        AssertSafe(request, plan);
    }

    [Fact]
    public void SupplementalRowsDoNotTruncateRequiredAnchorage()
    {
        var request = FingerRequest(301, 309, 2900);
        request.Specifications[0].AnchorageMm = 100;
        var plan = ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "ANCHORAGE_DOES_NOT_FIT");
        Assert.DoesNotContain(plan.Bars, b => b.End.X >= 2900);
        AssertSafe(request, plan);
    }

    [Fact]
    public void IsolatedExtendedGridRowIsSkippedWithoutAddingAnOverlappingBar()
    {
        var request = FingerRequest(201, 209, 2300);
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.Equal(5, plan.Bars.Count);
        Assert.Contains(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED");
        Assert.DoesNotContain(plan.Bars, b => Math.Abs(b.Start.Y - 200) < 1e-6);
        Assert.DoesNotContain(plan.Bars, b => b.Start.Y > 200 && b.Start.Y < 212);
    }

    [Fact]
    public void SkippedSupplementalRowsCannotCausePhantomCollisionsWithAnotherColor()
    {
        var request = FingerRequest(301, 309, 2300);
        request.Slabs[0].Holes.Add(Rect(1100, 170, 2400, 230));
        request.Slabs[0].Holes.Add(Rect(1100, 370, 2400, 430));
        request.Faces.Add(new FaceInput { Id = "blue-face", ColorKey = "blue", Vertices = Rect(1100, 310, 2400, 312) });
        request.Specifications.Add(new ReinforcementSpec { Key = "blue", ColorKey = "blue", MaterialKey = "A500", DiameterMm = 12, SpacingMm = 200 });
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.Contains(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED");
        Assert.DoesNotContain(plan.Diagnostics, d => d.Code == "FINAL_BAR_COLLISION");
    }

    [Fact]
    public void ExistingRowsAreNeverExtendedAcrossAnOpening()
    {
        var request = FingerRequest(301, 309, 2300);
        request.Slabs[0].Holes.Add(Rect(1100, 170, 2400, 230));
        request.Slabs[0].Holes.Add(Rect(1100, 370, 2400, 430));
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.Contains(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED");
        Assert.DoesNotContain(plan.Bars, b => b.Start.Y > 301 && b.Start.Y < 309);
        Assert.DoesNotContain(plan.Bars, b => (Math.Abs(b.Start.Y - 200) < 1e-6 || Math.Abs(b.Start.Y - 400) < 1e-6) && b.End.X > 1100);
        AssertSafe(request, plan);
    }

    [Fact]
    public void ExtendingAdjacentRowsPreservesAnchorageRoundingAndOtherArrayMembers()
    {
        var request = DoubleFingerRequest(2303);
        request.Specifications[0].AnchorageMm = 125;
        request.Options.RoundIncrementMm = 100;
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        var extended = plan.Bars.Where(b => b.End.X > 2000).ToList();
        Assert.Equal(2, extended.Count);
        Assert.All(extended, b => {
            Assert.True(b.End.X >= 2428 - 1e-6);
            Assert.True(b.Start.X <= 175 + 1e-6);
            Assert.Equal(0, (b.End.X - b.Start.X) % 100, 6);
        });
        Assert.Equal(6, plan.Bars.Count);
        Assert.All(plan.Bars.Where(b => !extended.Contains(b)), b => Assert.True(b.End.X < 1300));
        AssertGroupsMatchBars(plan, true);
    }

    [Fact]
    public void SkippedExtendedRowsCannotCausePhantomCollisionsWithAnotherColor()
    {
        var request = FingerRequest(301, 309, 2300);
        request.Faces.Add(new() { Id = "blue-face", ColorKey = "blue", Vertices = Rect(1100, 395, 2400, 405) });
        request.Specifications.Add(new() { Key = "blue", ColorKey = "blue", MaterialKey = "A500", DiameterMm = 12, SpacingMm = 200 });
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.Contains(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED");
        Assert.DoesNotContain(plan.Diagnostics, d => d.Code == "FINAL_BAR_COLLISION");
    }

    [Fact]
    public void ExtensionNeverClaimsDemandFartherAwayThanOnePitch()
    {
        var request = Request(Rect(0, 0, 3000, 2200), new()
        {
            new(300, 200), new(1000, 200), new(1000, 1592), new(2300, 1592),
            new(2300, 1600), new(999.5, 1600), new(999.5, 1200), new(300, 1200)
        });
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.DoesNotContain(plan.Bars, b => b.Start.Y <= 1200 && b.End.X > 1001);
        Assert.Contains(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED");
        Assert.DoesNotContain(plan.Bars, b => b.End.X >= 2300);
        AssertSafe(request, plan);
    }

    [Fact]
    public void SpacingSmallerThanPhysicalDiameterIsRejected()
    {
        var request = FingerRequest(301, 309, 2300);
        request.Specifications[0].SpacingMm = 10;
        request.Specifications[0].DiameterMm = 8;
        request.Specifications[0].ModelDiameterMm = 12;
        var plan = ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "INVALID_SPECIFICATION");
        Assert.Empty(plan.Bars);
    }

    [Fact]
    public void InitialRowsOfDifferentColorsCannotOverlapThroughAnchorage()
    {
        var request = Request(Rect(0, 0, 3000, 2000), Rect(300, 300, 1200, 1700));
        request.Specifications[0].AnchorageMm = 100;
        request.Faces.Add(new() { Id = "blue-face", ColorKey = "blue", Vertices = Rect(1300, 300, 2700, 1700) });
        request.Specifications.Add(new() { Key = "blue", ColorKey = "blue", MaterialKey = "A500", DiameterMm = 12, SpacingMm = 200, AnchorageMm = 100 });
        var plan = ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "FINAL_BAR_COLLISION" && d.IsBlocking);
    }

    [Fact]
    public void FlatBarEndsMayTouchWithoutOverlappingVolume()
    {
        var request = Request(Rect(0, 0, 3000, 2000), Rect(300, 300, 1200, 1700));
        request.Faces.Add(new() { Id = "blue-face", ColorKey = "blue", Vertices = Rect(1200, 300, 2700, 1700) });
        request.Specifications.Add(new() { Key = "blue", ColorKey = "blue", MaterialKey = "A500", DiameterMm = 12, SpacingMm = 200 });
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.DoesNotContain(plan.Diagnostics, d => d.Code == "FINAL_BAR_COLLISION");
    }

    [Fact]
    public void SkippingAnIsolatedExtensionPreservesAllOtherLongArrayMembers()
    {
        var request = Request(Rect(0, 0, 3000, 30000), new()
        {
            new(300, 100), new(1000, 100), new(1000, 15001), new(2300, 15001),
            new(2300, 15004), new(1000, 15004), new(1000, 29900), new(300, 29900)
        });
        request.Specifications[0].DiameterMm = 6;
        request.Specifications[0].SpacingMm = 10;
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Diagnostics(plan));
        Assert.Equal(2980, plan.Bars.Count);
        Assert.Equal(2980, plan.Bars.Select(b => b.Start.Y).Distinct().Count());
        Assert.DoesNotContain(plan.Bars, b => b.End.X >= 2300);
        Assert.Contains(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED");
        Assert.Equal(4, plan.Groups.Count);
        Assert.All(plan.Groups, g => Assert.InRange(g.Count, 2, 1002));
        AssertGroupsMatchBars(plan, true);
    }

    [Fact]
    public void CurvedTailProjectionAndRowSelectionAreDeterministic()
    {
        var points = new List<Point2> { new(300, 200), new(1000, 200), new(1000, 301) };
        points.AddRange(Enumerable.Range(0, 25).Select(i => new Point2(1000 + 1300 * Math.Sin(Math.PI * i / 24), 301 + i / 3.0)));
        points.AddRange(new[] { new Point2(1000, 1200), new Point2(300, 1200) });
        var request = Request(Rect(0, 0, 3000, 2000), points);
        var first = ReinforcementPlanner.Build(request);
        request.Faces[0].Vertices.Reverse();
        var second = ReinforcementPlanner.Build(request);
        Assert.True(first.CanCreate, Diagnostics(first));
        Assert.True(second.CanCreate, Diagnostics(second));
        Assert.Contains(first.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED");
        Assert.DoesNotContain(first.Bars, b => b.End.X >= 2300);
        Assert.Equal(first.Bars.Select(Serialize), second.Bars.Select(Serialize));
        AssertSafe(request, second);
    }

    private static PlanRequest DoubleFingerRequest(double reach) => Request(Rect(0, 0, 3000, 2000), new()
    {
        new(300, 200), new(1000, 200), new(1000, 201), new(reach, 201),
        new(reach, 209), new(1000, 209), new(1000, 401), new(reach, 401),
        new(reach, 409), new(1000, 409), new(1000, 1200), new(300, 1200)
    });

    private static PlanRequest FingerRequest(double low, double high, double reach) => Request(Rect(0, 0, 3000, 2000), new()
    {
        new(300, 200), new(1000, 200), new(1000, low), new(reach, low),
        new(reach, high), new(1000, high), new(1000, 1200), new(300, 1200)
    });

    private static PlanRequest Request(List<Point2> slab, List<Point2> face) => new()
    {
        Slabs = new() { new PolygonInput { Shell = slab } },
        Faces = new() { new FaceInput { Id = "face", ColorKey = "red", Vertices = face } },
        Specifications = new() { new ReinforcementSpec { Key = "base", ColorKey = "red", MaterialKey = "A500", DiameterMm = 12, SpacingMm = 200 } },
        Options = new PlanOptions { RoundIncrementMm = 1 }
    };

    private static List<Point2> Rect(double x0, double y0, double x1, double y1) => new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };
    private static string Diagnostics(ReinforcementPlan plan) => string.Join("; ", plan.Diagnostics.Select(d => d.Code + ": " + d.Message));
    private static string Serialize(PlannedBar b) => $"{b.SpecKey}:{b.Start.X:F6},{b.Start.Y:F6}:{b.End.X:F6},{b.End.Y:F6}";
    private static Polygon Polygon(PolygonInput input)
    {
        var factory = new GeometryFactory();
        LinearRing Ring(List<Point2> pts) => factory.CreateLinearRing(pts.Concat(new[] { pts[0] }).Select(p => new Coordinate(p.X, p.Y)).ToArray());
        return factory.CreatePolygon(Ring(input.Shell), input.Holes.Select(Ring).ToArray());
    }
    private static double Area(PolygonInput polygon) => Polygon(polygon).Area;
    private static void AssertSafe(PlanRequest request, ReinforcementPlan plan)
    {
        var host = Polygon(request.Slabs[0]);
        foreach (var bar in plan.Bars)
        {
            var spec = request.Specifications.Single(s => s.Key == bar.SpecKey);
            var line = host.Factory.CreateLineString(new[] { new Coordinate(bar.Start.X, bar.Start.Y), new Coordinate(bar.End.X, bar.End.Y) });
            Assert.True(host.Covers(line), Serialize(bar));
            // Independent distance check catches both exterior and hole clearance violations.
            Assert.True(host.Boundary.Distance(line) >= request.Options.EdgeCoverMm + (spec.ModelDiameterMm ?? spec.DiameterMm) / 2 - 0.001, Serialize(bar));
        }
    }
    private static void AssertGroupsMatchBars(ReinforcementPlan plan, bool alongX)
    {
        var expanded = plan.Groups.SelectMany(g => Enumerable.Range(0, g.Count).Select(i => new PlannedBar
        {
            Start = new Point2(g.Start.X + (alongX ? 0 : i * g.SpacingMm), g.Start.Y + (alongX ? i * g.SpacingMm : 0)),
            End = new Point2(g.End.X + (alongX ? 0 : i * g.SpacingMm), g.End.Y + (alongX ? i * g.SpacingMm : 0)),
            SpecKey = g.SpecKey, ColorKey = g.ColorKey
        })).Select(Serialize).OrderBy(x => x).ToArray();
        Assert.Equal(expanded, plan.Bars.Select(Serialize).OrderBy(x => x));
    }
}
