using System.Globalization;
using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class HybridPerimeterPlannerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoLowerBackgroundUsesTheIndependentContourAndManualStep(bool keepUpper)
    {
        var request = Request();
        request.BackgroundPlan.Bars.RemoveAll(b => !keepUpper || b.SpecKey.StartsWith("B"));
        var plan = PerimeterPlanner.Build(request);
        request.BackgroundPlan = null;
        var independent = PerimeterPlanner.Build(request);

        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(Geometry(independent.Bars), Geometry(plan.Bars));
        Assert.Equal(4, plan.PerimeterUEdges.Count);
        Assert.All(plan.PerimeterUEdges, e => Assert.False(e.FollowsBackgroundRows));
        AssertValid(request, plan);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OneLowerDirectionKeepsItsRowsAndCompletesOnlyTheOtherEdges(bool alongX)
    {
        var request = Request();
        KeepDirection(request, alongX);
        request.CompleteMissingBackgroundEdges = false;
        var aligned = PerimeterPlanner.Build(request);
        request.CompleteMissingBackgroundEdges = true;
        var plan = PerimeterPlanner.Build(request);

        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(2, plan.PerimeterUEdges.Count(e => e.FollowsBackgroundRows));
        Assert.Equal(2, plan.PerimeterUEdges.Count(e => !e.FollowsBackgroundRows));
        Assert.Equal(Geometry(aligned.Bars), Geometry(AutomaticBars(plan)));
        Assert.Equal(aligned.PerimeterUEdges.Select(EdgeKey).OrderBy(x => x),
            plan.PerimeterUEdges.Where(e => e.FollowsBackgroundRows).Select(EdgeKey).OrderBy(x => x));
        Assert.DoesNotContain(plan.PerimeterUEdges.Where(e => !e.FollowsBackgroundRows),
            e => aligned.PerimeterUEdges.Any(a => EdgeKey(a) == EdgeKey(e)));
        AssertValid(request, plan);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EditingManualSpacingChangesOnlyPreviouslyUnframedEdges(bool alongX)
    {
        var request = Request(); KeepDirection(request, alongX);
        request.SpacingMm = 100;
        var dense = PerimeterPlanner.Build(request);
        request.SpacingMm = 350;
        var sparse = PerimeterPlanner.Build(request);

        Assert.True(dense.CanCreate, Messages(dense));
        Assert.True(sparse.CanCreate, Messages(sparse));
        Assert.True(dense.Bars.Count > sparse.Bars.Count);
        Assert.Equal(Geometry(AutomaticBars(dense)), Geometry(AutomaticBars(sparse)));
        AssertValid(request, sparse);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    [InlineData(50)]
    [InlineData(900)]
    public void TwoLowerDirectionsKeepTheirDifferentGridsAndIgnoreManualStep(double manualStep)
    {
        var request = Request();
        request.CompleteMissingBackgroundEdges = false;
        var aligned = PerimeterPlanner.Build(request);
        request.CompleteMissingBackgroundEdges = true;
        request.SpacingMm = manualStep;
        var plan = PerimeterPlanner.Build(request);

        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(Geometry(aligned.Bars), Geometry(plan.Bars));
        Assert.All(plan.PerimeterUEdges, e => Assert.True(e.FollowsBackgroundRows));
        AssertValid(request, plan);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExistingLowerReferenceRowsAlsoSelectTheHybridMode(bool alongX)
    {
        var request = Request(); KeepDirection(request, alongX);
        var expected = PerimeterPlanner.Build(request);
        request.BackgroundPlan.ReferenceBars.AddRange(request.BackgroundPlan.Bars);
        request.BackgroundPlan.Bars.Clear();
        var actual = PerimeterPlanner.Build(request);

        Assert.True(actual.CanCreate, Messages(actual));
        Assert.Equal(2, actual.PerimeterUEdges.Count(e => !e.FollowsBackgroundRows));
        Assert.Equal(Geometry(expected.Bars), Geometry(actual.Bars));
        AssertValid(request, actual);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectedHoleEdgesAreCompletedWithoutDuplicatingAutomaticEdges(bool outer)
    {
        var slab = new PolygonInput { Shell = Rect(0, 0, 6000, 4000), Holes = new() { Rect(2200, 1300, 3800, 2700) } };
        var request = Request(slab); KeepDirection(request, true);
        request.Outer = outer;
        var plan = PerimeterPlanner.Build(request);

        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(outer ? 8 : 4, plan.PerimeterUEdges.Count);
        Assert.Equal(outer ? 4 : 2, plan.PerimeterUEdges.Count(e => !e.FollowsBackgroundRows));
        AssertValid(request, plan);
    }

    [Fact]
    public void SelectedOpeningBetweenLowerRowsUsesManualFramingOnAllItsEdges()
    {
        var slab = new PolygonInput { Shell = Rect(0, 0, 6000, 4000),
            Holes = new() { Rect(2200, 1900, 3800, 1920) } };
        var request = Request(slab); KeepDirection(request, true);
        request.Outer = false;
        request.CompleteMissingBackgroundEdges = false;
        var aligned = PerimeterPlanner.Build(request);
        Assert.False(aligned.CanCreate);
        Assert.Contains(aligned.Diagnostics, d => d.Code == "PERIMETER_BACKGROUND_REQUIRED");
        request.CompleteMissingBackgroundEdges = true;
        var plan = PerimeterPlanner.Build(request);

        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(4, plan.PerimeterUEdges.Count);
        Assert.All(plan.PerimeterUEdges, e => Assert.False(e.FollowsBackgroundRows));
        AssertValid(request, plan);
    }

    [Fact]
    public void InvalidExistingLowerRowsDoNotFallBackToIndependentFraming()
    {
        var request = Request(); KeepDirection(request, true);
        var row = request.BackgroundPlan.Bars.First(b => b.SpecKey == "BX");
        row.Centerline = new() { new() { Start = new(row.Start.X, row.Start.Y, row.Placement.ElevationMm + 10),
            End = new(row.End.X, row.End.Y, row.Placement.ElevationMm + 10) } };
        var plan = PerimeterPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Empty(plan.Bars);
        Assert.Contains(plan.Diagnostics, d => d.Code == "PERIMETER_BACKGROUND_INVALID" && d.IsBlocking);
    }

    [Fact]
    public void ObliqueEdgesWithAutomaticRowsDoNotReceiveASecondIndependentLayout()
    {
        var slab = new PolygonInput { Shell = new() { new(3000, 0), new(6000, 2000), new(3000, 4000), new(0, 2000) } };
        var request = Request(slab); KeepDirection(request, true);
        request.LegLengthMm = 100;
        request.SpacingMm = double.NaN;
        var plan = PerimeterPlanner.Build(request);

        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(4, plan.PerimeterUEdges.Count);
        Assert.All(plan.PerimeterUEdges, e => Assert.True(e.FollowsBackgroundRows));
        AssertValid(request, plan);
    }

    [Fact]
    public void AShortUnframedEdgeRetainsItsSinglePerimeterBar()
    {
        var slab = new PolygonInput { Shell = new() { new(0, 0), new(6000, 0), new(6000, 4000),
            new(3000, 4000), new(3000, 3990), new(2900, 3990), new(2900, 4000), new(0, 4000) } };
        var request = Request(slab, onlyAlongX: false);
        var plan = PerimeterPlanner.Build(request);

        Assert.True(plan.CanCreate, Messages(plan));
        var shortEdges = plan.PerimeterUEdges.Where(e => !e.FollowsBackgroundRows && e.StationsMm.Count == 1).ToList();
        Assert.Equal(2, shortEdges.Count);
        foreach (var edge in shortEdges)
            Assert.Contains(plan.Bars, b => b.SpecKey == Assert.Single(edge.BarKeys));
        AssertValid(request, plan);
    }

    [Fact]
    public void AnAlreadyAlignedEdgeDoesNotRunIndependentHeightValidation()
    {
        var slab = new PolygonInput { Shell = new() { new(0, 0), new(6000, 0), new(6000, 3000),
            new(3000, 4000), new(0, 3000) } };
        var request = Request(slab); KeepDirection(request, true);
        request.BackgroundLayers.RemoveAll(l => !l.AlongX);
        request.BackgroundPlan.Bars.RemoveAll(b => b.SpecKey.EndsWith("Y"));
        request.ThicknessMm = 90; request.ModelDiameterMm = request.DiameterMm = 4;
        request.BendDiameterMm = 16; request.LegLengthMm = 100;
        foreach (var bar in request.BackgroundPlan.Bars)
            bar.Placement.ElevationMm = bar.SpecKey == "BX" ? -59 : -31;
        var plan = PerimeterPlanner.Build(request);
        request.BackgroundPlan = null;
        var independent = PerimeterPlanner.Build(request);

        Assert.False(independent.CanCreate);
        Assert.Contains(independent.Diagnostics, d => d.IsBlocking && d.Code == "PERIMETER_GEOMETRY"
            && d.Message.Contains("Загиб П-стержня не помещается"));
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Single(plan.PerimeterUEdges, e => !e.FollowsBackgroundRows);
        AssertValid(request, plan);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void InvalidManualSpacingBlocksARequiredCompletion(double step)
    {
        var request = Request(); KeepDirection(request, true); request.SpacingMm = step;
        var plan = PerimeterPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Empty(plan.Bars);
        Assert.Empty(plan.Groups);
        Assert.Empty(plan.PerimeterUEdges);
        Assert.Contains(plan.Diagnostics, d => d.IsBlocking);
    }

    private static PerimeterRequest Request(PolygonInput slab = null, bool? onlyAlongX = null)
    {
        var background = new BackgroundRequest { Slabs = new() { slab ?? new() { Shell = Rect(0, 0, 6000, 4000) } },
            ThicknessMm = 220, TopCoverMm = 25, BottomCoverMm = 25, SideCoverMm = 25,
            Layers = new[] { ("BX", false, true, 12d, 200d), ("BY", false, false, 16d, 150d),
                ("TX", true, true, 12d, 200d), ("TY", true, false, 16d, 150d) }
                .Select(v => new BackgroundLayer { Key = v.Item1, Top = v.Item2, AlongX = v.Item3,
                    TypeKey = v.Item4.ToString(CultureInfo.InvariantCulture), DiameterMm = v.Item4,
                    ModelDiameterMm = v.Item4, SpacingMm = v.Item5 }).ToList() };
        var configuredLayers = background.Layers;
        if (onlyAlongX.HasValue)
        {
            background.PlacementLayers = configuredLayers;
            background.Layers = configuredLayers.Where(l => l.AlongX == onlyAlongX.Value).ToList();
        }
        var backgroundPlan = BackgroundPlanner.Build(background);
        Assert.True(backgroundPlan.CanCreate, Messages(backgroundPlan));
        return new PerimeterRequest { Slabs = background.Slabs, TypeKey = "8", DiameterMm = 8, ModelDiameterMm = 8,
            BendDiameterMm = 32, ThicknessMm = 220, TopCoverMm = 25, BottomCoverMm = 25, SideCoverMm = 25,
            LegLengthMm = 500, SpacingMm = 175, Outer = true, Openings = true, UseUbars = true,
            BackgroundLayers = configuredLayers, BackgroundPlan = backgroundPlan, CompleteMissingBackgroundEdges = true };
    }

    private static void KeepDirection(PerimeterRequest request, bool alongX) =>
        request.BackgroundPlan.Bars.RemoveAll(b => b.SpecKey == (alongX ? "BY" : "BX"));

    private static IEnumerable<PlannedBar> AutomaticBars(ReinforcementPlan plan)
    {
        var keys = plan.PerimeterUEdges.Where(e => e.FollowsBackgroundRows).SelectMany(e => e.BarKeys).ToHashSet();
        return plan.Bars.Where(b => keys.Contains(b.SpecKey));
    }

    private static void AssertValid(PerimeterRequest request, ReinforcementPlan plan)
    {
        Assert.Equal(plan.Bars.Count, plan.Groups.Sum(g => g.Count));
        Assert.Equal(plan.Bars.Count, plan.Bars.Select(b => b.SpecKey).Distinct().Count());
        Assert.Equal(plan.PerimeterUEdges.Count, plan.PerimeterUEdges.Select(e => e.Id).Distinct().Count());
        Assert.Equal(plan.PerimeterUEdges.Count, plan.PerimeterUEdges.Select(EdgeKey).Distinct().Count());
        var keys = plan.Bars.Select(b => b.SpecKey).ToHashSet();
        foreach (var edge in plan.PerimeterUEdges)
        {
            Assert.All(edge.BarKeys, key => Assert.Contains(key, keys));
            Assert.Equal(edge.StationsMm.Count, edge.BarKeys.Count);
            if (!edge.FollowsBackgroundRows)
            {
                Assert.Equal(request.SpacingMm, edge.SpacingMm);
                for (int i = 1; i < edge.StationsMm.Count; i++)
                    Assert.InRange(edge.StationsMm[i] - edge.StationsMm[i - 1], request.ModelDiameterMm - 1e-6, request.SpacingMm + 1e-6);
            }
        }
        Assert.DoesNotContain(BarContainmentValidator.Validate(plan.Bars, request.Slabs, request.ThicknessMm,
            request.TopCoverMm, request.BottomCoverMm, request.SideCoverMm), d => d.IsBlocking);
        Assert.DoesNotContain(CompositePlanValidator.Validate(plan.Bars, allowBarIntersections: true), d => d.IsBlocking);
    }

    private static string[] Geometry(IEnumerable<PlannedBar> bars) => bars.Select(b => string.Join("|", b.Centerline.Select(s =>
        Point(s.Start) + ":" + Point(s.End) + ":" + (s.Midpoint.HasValue ? Point(s.Midpoint.Value) : "")))).OrderBy(x => x).ToArray();
    private static string Point(Point3 p) => FormattableString.Invariant($"{p.X:R},{p.Y:R},{p.Z:R}");
    private static string EdgeKey(PerimeterUEdge edge) => string.Join("|", new[] {
        FormattableString.Invariant($"{Math.Round(edge.Start.X, 6):R},{Math.Round(edge.Start.Y, 6):R}"),
        FormattableString.Invariant($"{Math.Round(edge.End.X, 6):R},{Math.Round(edge.End.Y, 6):R}") }.OrderBy(x => x));
    private static string Messages(ReinforcementPlan plan) => string.Join("; ", plan.Diagnostics.Select(d => d.Code + ": " + d.Message));
    private static List<Point2> Rect(double a, double b, double c, double d) => new() { new(a, b), new(c, b), new(c, d), new(a, d) };
}
