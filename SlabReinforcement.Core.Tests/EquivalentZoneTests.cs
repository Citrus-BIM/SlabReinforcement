using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;
using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class EquivalentZoneTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void AdjacentColorsWithOneExplicitTypeMatchASingleColor(bool alongX, bool numeric)
    {
        var split = Request(alongX, numeric);
        var combined = SingleColorRequest(split);
        var reference = ReinforcementPlanner.Build(combined);
        var actual = ReinforcementPlanner.Build(split);
        Assert.True(reference.CanCreate, Messages(reference));
        Assert.True(actual.CanCreate, Messages(actual));
        Assert.Equal(7, actual.Bars.Count);
        Assert.Equal(GeometryKeys(reference), GeometryKeys(actual));
        Assert.All(actual.Bars, bar => Assert.Equal("a-blue", bar.SpecKey));
        Assert.Equal(2400 * 1400, DemandGeometry(actual).Area, 5);
        if (numeric) AssertHalfPitchCoverage(actual, alongX);

        split.Specifications.Reverse();
        split.Faces.Reverse();
        var reversed = ReinforcementPlanner.Build(split);
        Assert.True(reversed.CanCreate, Messages(reversed));
        Assert.Equal(GeometryKeys(actual), GeometryKeys(reversed));
        Assert.All(reversed.Bars, bar => Assert.Equal("a-blue", bar.SpecKey));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EquivalentColorsPreserveAnOpeningAndRotatedDistribution(bool alongX)
    {
        var split = Request(alongX, false);
        split.Slabs[0].Holes.Add(Orient(Rect(1300, 600, 1700, 1400), alongX));
        foreach (var spec in split.Specifications) spec.AnchorageMm = 0;
        var reference = ReinforcementPlanner.Build(SingleColorRequest(split));
        var actual = ReinforcementPlanner.Build(split);
        Assert.True(reference.CanCreate, Messages(reference));
        Assert.True(actual.CanCreate, Messages(actual));
        Assert.Equal(GeometryKeys(reference), GeometryKeys(actual));
        Assert.Equal(2400 * 1400 - 400 * 800, DemandGeometry(actual).Area, 5);
        var concrete = Polygon(split.Slabs[0]);
        foreach (var bar in actual.Bars)
        {
            var axis = Axis(bar);
            Assert.True(concrete.Covers(axis));
            Assert.True(axis.Distance(concrete.Boundary) >= 31 - 1e-6);
        }
    }

    [Theory]
    [InlineData("type")]
    [InlineData("type-case")]
    [InlineData("material")]
    [InlineData("nominal")]
    [InlineData("physical")]
    [InlineData("spacing")]
    [InlineData("anchorage")]
    [InlineData("spacing-near")]
    [InlineData("anchorage-near")]
    [InlineData("no-type")]
    [InlineData("empty-type")]
    [InlineData("blank-type")]
    public void ConnectedManualAssignmentsChooseOneOriginalWinner(string difference)
    {
        var request = Request(true, false);
        var changed = request.Specifications.Single(spec => spec.ColorKey == "red");
        switch (difference)
        {
            case "type": changed.TypeKey = "other-type"; break;
            case "type-case": changed.TypeKey = "TYPE:12"; break;
            case "material": changed.MaterialKey = "400"; break;
            case "nominal": changed.ModelDiameterMm = 12; changed.DiameterMm = 10; break;
            case "physical": changed.ModelDiameterMm = 14; break;
            case "spacing": changed.SpacingMm = 250; break;
            case "anchorage": changed.AnchorageMm = 125; break;
            case "spacing-near": changed.SpacingMm += 0.0000001; break;
            case "anchorage-near": changed.AnchorageMm += 0.0000001; break;
            case "no-type": foreach (var spec in request.Specifications) spec.TypeKey = null; break;
            case "empty-type": foreach (var spec in request.Specifications) spec.TypeKey = ""; break;
            case "blank-type": foreach (var spec in request.Specifications) spec.TypeKey = " "; break;
        }
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        var winner = request.Specifications.OrderByDescending(s => s.DiameterMm).ThenBy(s => s.SpacingMm)
            .ThenBy(s => s.TypeKey ?? "", StringComparer.Ordinal).ThenBy(s => s.Key, StringComparer.Ordinal).First();
        Assert.Equal(7, plan.Bars.Count);
        Assert.All(plan.Bars, bar => {
            Assert.Equal(winner.Key, bar.SpecKey);
            Assert.Equal(300 - winner.AnchorageMm, bar.Start.X, 6);
            Assert.Equal(2700 + winner.AnchorageMm, bar.End.X, 6);
        });
    }

    [Fact]
    public void ExplicitAndImplicitEqualPhysicalDiametersCanShareOneType()
    {
        var request = Request(true, false);
        request.Specifications[0].ModelDiameterMm = request.Specifications[0].DiameterMm;
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(7, plan.Bars.Count);
    }

    [Fact]
    public void OverlappingLargerDiameterPromotesTheWholeConnectedLine()
    {
        var request = Request(true, true);
        foreach (var spec in request.Specifications) spec.AnchorageMm = 0;
        var referenceRequest = SingleColorRequest(request);
        foreach (var target in new[] { request, referenceRequest })
        {
            target.Faces.Add(new FaceInput { Id = "strong-face", ColorKey = "green", Vertices = Rect(1000, 300, 2000, 1700) });
            target.Specifications.Add(new ReinforcementSpec
            {
                Key = "strong", ColorKey = "green", TypeKey = "type:16", MaterialKey = "500",
                DiameterMm = 16, SpacingMm = 200
            });
        }
        var actual = ReinforcementPlanner.Build(request);
        var reference = ReinforcementPlanner.Build(referenceRequest);
        Assert.True(reference.CanCreate, Messages(reference));
        Assert.True(actual.CanCreate, Messages(actual));
        Assert.Equal(GeometryKeys(reference), GeometryKeys(actual));
        Assert.Contains(actual.Bars, bar => bar.SpecKey == "strong");
        Assert.All(actual.Bars.Where(bar => bar.SpecKey == "strong"), bar =>
        {
            Assert.Equal(300, bar.Start.X, 6);
            Assert.Equal(2700, bar.End.X, 6);
        });
        Assert.DoesNotContain(actual.Bars, bar => bar.SpecKey != "strong" && bar.Start.X < 2000 && bar.End.X > 1000);
        Assert.Equal(2400 * 1400, DemandGeometry(actual).Area, 5);
        AssertHalfPitchCoverage(actual, true);
    }

    [Fact]
    public void EquivalentDisconnectedColorsPreserveBothDemandComponents()
    {
        var request = Request(true, true);
        request.Faces[0].Vertices = Rect(300, 300, 1000, 1700);
        request.Faces[1].Vertices = Rect(2000, 300, 2700, 1700);
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(14, plan.Bars.Count);
        Assert.All(plan.Bars, bar => Assert.Equal("a-blue", bar.SpecKey));
        Assert.Equal(2, plan.DemandRegions.Count);
        AssertHalfPitchCoverage(plan, true);
    }

    [Fact]
    public void OverlappingEquivalentColorsAreUnitedBeforeAnchorageIsAdded()
    {
        var request = Request(true, true);
        request.Faces[0].Vertices = Rect(300, 300, 1700, 1700);
        request.Faces[1].Vertices = Rect(1300, 300, 2700, 1700);
        var actual = ReinforcementPlanner.Build(request);
        var reference = ReinforcementPlanner.Build(SingleColorRequest(request));
        Assert.True(actual.CanCreate, Messages(actual));
        Assert.Equal(7, actual.Bars.Count);
        Assert.Equal(GeometryKeys(reference), GeometryKeys(actual));
        Assert.Equal(2400 * 1400, DemandGeometry(actual).Area, 5);
        AssertHalfPitchCoverage(actual, true);
    }

    private static PlanRequest Request(bool alongX, bool numeric) => new()
    {
        Slabs = new() { new() { Shell = Orient(Rect(0, 0, 3000, 2000), alongX) } },
        Faces = new()
        {
            new() { Id = "left", ColorKey = "red", Vertices = Orient(Rect(300, 300, 1500, 1700), alongX) },
            new() { Id = "right", ColorKey = "blue", Vertices = Orient(Rect(1500, 300, 2700, 1700), alongX) }
        },
        Specifications = new()
        {
            Spec("z-red", "red"), Spec("a-blue", "blue")
        },
        Options = new() { AlongX = alongX, EdgeCoverMm = 25, MergeToleranceMm = 0, RoundIncrementMm = 1, CoverageHalfSpacing = numeric }
    };

    private static PlanRequest SingleColorRequest(PlanRequest split) => new()
    {
        Slabs = split.Slabs,
        Faces = new() { new() { Id = "combined", ColorKey = "blue", Vertices = Orient(Rect(300, 300, 2700, 1700), split.Options.AlongX) } },
        Specifications = new() { Spec("a-blue", "blue", split.Specifications[0].AnchorageMm) },
        Options = split.Options
    };

    private static ReinforcementSpec Spec(string key, string color, double anchor = 100) => new()
    {
        Key = key, ColorKey = color, TypeKey = "type:12", MaterialKey = "500", DiameterMm = 12, SpacingMm = 200, AnchorageMm = anchor
    };

    private static void AssertHalfPitchCoverage(ReinforcementPlan plan, bool alongX)
    {
        var patches = plan.Bars.Select(bar => (Geometry)Polygon(new PolygonInput { Shell = alongX
            ? Rect(bar.Start.X, bar.Start.Y - 100, bar.End.X, bar.End.Y + 100)
            : Rect(bar.Start.X - 100, bar.Start.Y, bar.End.X + 100, bar.End.Y) })).ToList();
        Assert.True(DemandGeometry(plan).Difference(UnaryUnionOp.Union(patches)).Area <= 1e-5);
    }

    private static string[] GeometryKeys(ReinforcementPlan plan) => plan.Bars
        .Select(bar => FormattableString.Invariant($"{bar.SpecKey}:{bar.Start.X:F6},{bar.Start.Y:F6}:{bar.End.X:F6},{bar.End.Y:F6}"))
        .OrderBy(key => key, StringComparer.Ordinal).ToArray();
    private static Geometry DemandGeometry(ReinforcementPlan plan) => UnaryUnionOp.Union(plan.DemandRegions.Select(p => (Geometry)Polygon(p)).ToList());
    private static Polygon Polygon(PolygonInput p) => new(Ring(p.Shell), p.Holes.Select(Ring).ToArray());
    private static LinearRing Ring(IEnumerable<Point2> source)
    {
        var points = source.Select(p => new Coordinate(p.X, p.Y)).ToList(); points.Add(points[0].Copy());
        return new(points.ToArray());
    }
    private static LineString Axis(PlannedBar bar) => new(new[] { new Coordinate(bar.Start.X, bar.Start.Y), new Coordinate(bar.End.X, bar.End.Y) });
    private static List<Point2> Orient(List<Point2> points, bool alongX) => alongX ? points : points.Select(p => new Point2(-p.Y, p.X)).ToList();
    private static List<Point2> Rect(double x1, double y1, double x2, double y2) => new() { new(x1, y1), new(x2, y1), new(x2, y2), new(x1, y2) };
    private static string Messages(ReinforcementPlan plan) => string.Join("; ", plan.Diagnostics.Select(d => d.Code + ": " + d.Message));
}
