using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;
using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class NumericCoverageTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NumericCoverageCannotAssignOneBarToNearlyTwoPitches(bool alongX)
    {
        var request = Request(1, 399, alongX);
        var manual = ReinforcementPlanner.Build(request);
        Assert.True(manual.CanCreate); Assert.Single(manual.Bars);
        request.Options.CoverageHalfSpacing = true;
        var numeric = ReinforcementPlanner.Build(request);
        Assert.True(numeric.CanCreate, string.Join(";", numeric.Diagnostics.Select(d => d.Message)));
        Assert.True(numeric.Bars.Count >= 2);
        var patches = numeric.Bars.Select(b => Patch(b, 100, alongX)).Cast<Geometry>().ToList();
        var demand = Polygon(request.Faces[0].Vertices);
        Assert.True(demand.Difference(UnaryUnionOp.Union(patches)).Area < 1e-5);
    }

    [Fact]
    public void HalfSpacingChangesOuterReachNotThePitchInsideAnArray()
    {
        var request = Request(100, 1100, true); request.Options.CoverageHalfSpacing = true;
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate); Assert.Equal(5, plan.Bars.Count);
        var group = Assert.Single(plan.Groups);
        Assert.Equal(5, group.Count); Assert.Equal(200, group.SpacingMm);
        Assert.DoesNotContain(plan.Diagnostics, d => d.Code == "SUPPLEMENTAL_ROWS_CREATED");
    }

    [Fact]
    public void BackgroundCreditRequiresPhysicalHalfPitchCoverage()
    {
        var request = Request(1, 399, true);
        var one = new[] { Bar(300, 200, 2700, 200) };
        Assert.Contains(BackgroundCoverageValidator.Validate(request.Faces, request.Slabs, one, 200, true), d => d.Code == "BACKGROUND_COVERAGE_GAP" && d.IsBlocking);
        var two = new[] { Bar(300, 100, 2700, 100), Bar(300, 300, 2700, 300) };
        Assert.Empty(BackgroundCoverageValidator.Validate(request.Faces, request.Slabs, two, 200, true));
    }

    [Fact]
    public void UnreinforcedNarrowSlabLobeCannotReceiveGlobalBackgroundCredit()
    {
        var slab = new PolygonInput { Shell = new() { new(0, 0), new(3000, 0), new(3000, 1000), new(3500, 1000), new(3500, 1050), new(3000, 1050), new(3000, 2000), new(0, 2000) } };
        var background = BackgroundPlanner.Build(new BackgroundRequest
        {
            Slabs = new() { slab }, ThicknessMm = 220, TopCoverMm = 25, BottomCoverMm = 25, SideCoverMm = 25,
            Layers = new() { new() { Key = "BX", TypeKey = "12", AlongX = true, DiameterMm = 12, ModelDiameterMm = 12, SpacingMm = 200 } }
        });
        Assert.True(background.CanCreate);
        var faces = new[] { new FaceInput { Id = "finger", Vertices = Rect(3200, 1005, 3450, 1045) } };
        Assert.Contains(BackgroundCoverageValidator.Validate(faces, new[] { slab }, background.Bars, 200, true), d => d.Code == "BACKGROUND_COVERAGE_GAP");
    }

    [Fact]
    public void HalfPitchCoverageNeverFillsAnOpeningWithAnUnsafeBar()
    {
        var request = Request(100, 1100, true); request.Options.CoverageHalfSpacing = true;
        request.Slabs[0].Holes.Add(Rect(1300, 500, 1700, 900));
        var plan = ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "UNCOVERED_DEMAND");
        Assert.DoesNotContain(plan.Bars, b => b.Start.Y >= 500 && b.Start.Y <= 900 && b.Start.X < 1300 && b.End.X > 1700);
        var withAnchor = Request(100, 1100, true); withAnchor.Options.CoverageHalfSpacing = true;
        withAnchor.Slabs[0].Holes.Add(Rect(1300, 500, 1700, 900));
        withAnchor.Specifications[0].AnchorageMm = 100;
        Assert.False(ReinforcementPlanner.Build(withAnchor).CanCreate);
    }

    [Fact]
    public void NumericDemandCannotDisappearWhenCoverErasesAConnectedNarrowTail()
    {
        var request = Request(300, 1700, true);
        request.Options.EdgeCoverMm = 25;
        request.Slabs[0].Shell = new() { new(0, 0), new(3000, 0), new(3000, 1000), new(3500, 1000), new(3500, 1050), new(3000, 1050), new(3000, 2000), new(0, 2000) };
        request.Faces[0].Vertices = new() { new(300, 300), new(2700, 300), new(2700, 1005), new(3450, 1005), new(3450, 1045), new(2700, 1045), new(2700, 1700), new(300, 1700) };
        request.Options.CoverageHalfSpacing = true;
        var plan = ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "UNCOVERED_DEMAND" && d.IsBlocking);
    }

    private static PlanRequest Request(double low, double high, bool alongX)
    {
        var demand = Rect(300, low, 2700, high);
        if (!alongX) demand = demand.Select(p => new Point2(p.Y, p.X)).ToList();
        return new PlanRequest
        {
            Slabs = new() { new() { Shell = Rect(-1000, -1000, 4000, 4000) } },
            Faces = new() { new() { Id = "demand", ColorKey = "red", Vertices = demand } },
            Specifications = new() { new() { Key = "d12", ColorKey = "red", MaterialKey = "A500", DiameterMm = 12, SpacingMm = 200 } },
            Options = new() { AlongX = alongX, EdgeCoverMm = 0, MergeToleranceMm = 0, RoundIncrementMm = 1 }
        };
    }
    private static PlannedBar Bar(double x1, double y1, double x2, double y2) => new() { Start = new(x1, y1), End = new(x2, y2) };
    private static List<Point2> Rect(double x1, double y1, double x2, double y2) => new() { new(x1, y1), new(x2, y1), new(x2, y2), new(x1, y2) };
    private static Polygon Polygon(IEnumerable<Point2> points)
    {
        var coordinates = points.Select(p => new Coordinate(p.X, p.Y)).ToList(); coordinates.Add(coordinates[0].Copy());
        return new GeometryFactory().CreatePolygon(coordinates.ToArray());
    }
    private static Polygon Patch(PlannedBar bar, double reach, bool alongX) => Polygon(alongX
        ? Rect(bar.Start.X, bar.Start.Y - reach, bar.End.X, bar.End.Y + reach)
        : Rect(bar.Start.X - reach, bar.Start.Y, bar.End.X + reach, bar.End.Y));
}
