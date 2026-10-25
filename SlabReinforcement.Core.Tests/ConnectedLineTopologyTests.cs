using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class ConnectedLineTopologyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectedTwoDimensionalUShapeDoesNotUpgradeItsDisconnectedLowerSlices(bool alongX)
    {
        var request = Request(alongX);
        request.Faces = new()
        {
            Face("left", "red", Rect(1000, 400, 2000, 1600), alongX),
            Face("right", "blue", Rect(4000, 400, 5000, 1600), alongX),
            Face("bridge", "red", Rect(2000, 1200, 4000, 1600), alongX)
        };
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        var lowerLeft = plan.Bars.Where(b => Row(b, alongX) < 1200 && Start(b, alongX) < 2000).ToList();
        Assert.NotEmpty(lowerLeft);
        Assert.All(lowerLeft, b => { Assert.Equal("d12", b.SpecKey); Assert.True(End(b, alongX) <= 2000 + 1e-6); });
        var upper = plan.Bars.Where(b => Row(b, alongX) > 1200).ToList();
        Assert.NotEmpty(upper);
        Assert.All(upper, b => { Assert.Equal("d20", b.SpecKey); Assert.Equal(1000, Start(b, alongX), 6); Assert.Equal(5000, End(b, alongX), 6); });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SharedBoundaryDoesNotRetainLoserWhenWinnerPitchMissesThatStation(bool alongX)
    {
        var request = Request(alongX);
        request.Specifications[1].SpacingMm = 300;
        request.Faces = new()
        {
            Face("lower", "red", Rect(1000, 400, 4500, 1000), alongX),
            Face("upper", "blue", Rect(1000, 1000, 4500, 1600), alongX)
        };
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.DoesNotContain(plan.Bars, b => b.SpecKey == "d12" && Math.Abs(Row(b, alongX) - 1000) < 1e-6);
        Assert.Equal(new[] { 400d, 600d, 800d }, plan.Bars.Where(b => b.SpecKey == "d12").Select(b => Row(b, alongX)).OrderBy(v => v));
        Assert.Equal(new[] { 1200d, 1500d }, plan.Bars.Where(b => b.SpecKey == "d20").Select(b => Row(b, alongX)).OrderBy(v => v));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SlopingSharedColorEdgeStillAssignsWholeConnectedLongitudinalSlice(bool alongX)
    {
        var request = Request(alongX);
        request.Faces = new()
        {
            Face("left", "red", new[] { (1000d, 400d), (2000d, 400d), (3500d, 1600d), (1000d, 1600d) }, alongX),
            Face("right", "blue", new[] { (2000d, 400d), (4500d, 400d), (4500d, 1600d), (3500d, 1600d) }, alongX)
        };
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(7, plan.Bars.Count);
        Assert.All(plan.Bars, b => { Assert.Equal("d20", b.SpecKey); Assert.Equal(1000, Start(b, alongX), 6); Assert.Equal(4500, End(b, alongX), 6); });
    }

    [Theory]
    [InlineData(true, 0.000001)]
    [InlineData(false, 0.000001)]
    [InlineData(true, 0.00001)]
    [InlineData(false, 0.00001)]
    public void PositiveGapOnCanonicalCoordinateGridDoesNotEraseTheLowerDiameterStrip(bool alongX, double gap)
    {
        var request = Request(alongX);
        request.Faces = new()
        {
            Face("left", "red", Rect(0, 400, 1000, 1600), alongX),
            Face("right", "blue", Rect(1000 + gap, 400, 2500, 1600), alongX)
        };
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(7, plan.Bars.Count(b => b.SpecKey == "d12"));
        Assert.Equal(7, plan.Bars.Count(b => b.SpecKey == "d20"));
        Assert.All(plan.Bars.Where(b => b.SpecKey == "d12"), b => Assert.Equal(1000, End(b, alongX), 6));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CriticalPointContactUsesWholeOwnedIntervalAtTheContactStation(bool alongX)
    {
        var request = Request(alongX);
        request.Options.MergeToleranceMm = 2000;
        request.Faces = new()
        {
            Face("lower-left", "red", Rect(1000, 400, 2500, 1000), alongX),
            Face("upper-right", "blue", Rect(2500, 1000, 4500, 1600), alongX)
        };
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        var contact = Assert.Single(plan.Bars, b => Math.Abs(Row(b, alongX) - 1000) < 1e-6);
        Assert.Equal("d20", contact.SpecKey);
        Assert.Equal(1000, Start(contact, alongX), 6);
        Assert.Equal(4500, End(contact, alongX), 6);
        Assert.Contains(plan.Bars, b => b.SpecKey == "d12" && Row(b, alongX) < 1000);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnchorageOverlapDoesNotChangeTheOwnerOfDisconnectedSelectedIntervals(bool alongX)
    {
        var request = Request(alongX);
        request.Specifications[0].AnchorageMm = 1000;
        request.Specifications[1].AnchorageMm = 200;
        request.Faces = new()
        {
            Face("left", "red", Rect(1000, 400, 2000, 1600), alongX),
            Face("right", "blue", Rect(2500, 400, 3500, 1600), alongX)
        };
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(7, plan.Bars.Count(b => b.SpecKey == "d12"));
        Assert.Equal(7, plan.Bars.Count(b => b.SpecKey == "d20"));
        Assert.All(plan.Bars.Where(b => b.SpecKey == "d12"), b => { Assert.Equal(0, Start(b, alongX), 6); Assert.Equal(3000, End(b, alongX), 6); });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NumericBoundaryCannotClaimCoverageFromSupplementalRowsExcludedAsSingletons(bool alongX)
    {
        var request = Request(alongX);
        request.Options.CoverageHalfSpacing = true;
        request.Specifications[1].SpacingMm = 300;
        foreach (var spec in request.Specifications) spec.RequiredAreaMm2PerM = 500;
        request.Faces = new()
        {
            Face("lower", "red", Rect(1000, 400, 4500, 1000), alongX),
            Face("upper", "blue", Rect(1000, 1000, 4500, 1600), alongX)
        };
        var plan = ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "NUMERIC_FINAL_COVERAGE_GAP" && d.IsBlocking);
        var intervals = plan.Bars.Select(b =>
        {
            var spacing = request.Specifications.Single(s => s.Key == b.SpecKey).SpacingMm;
            return (Low: Row(b, alongX) - spacing / 2, High: Row(b, alongX) + spacing / 2);
        }).OrderBy(i => i.Low);
        double coveredThrough = 400;
        foreach (var interval in intervals)
        {
            if (interval.Low > coveredThrough + 1e-6) break;
            coveredThrough = Math.Max(coveredThrough, interval.High);
        }
        Assert.Equal(900, coveredThrough, 6); // The actual missing strip is the reason creation is blocked.
    }

    private static PlanRequest Request(bool alongX) => new()
    {
        Slabs = new() { new() { Shell = Points(Rect(-2000, -2000, 7000, 4000), alongX) } },
        Specifications = new()
        {
            Spec("d12", "red", 12), Spec("d20", "blue", 20)
        },
        Options = new() { AlongX = alongX, EdgeCoverMm = 0, MergeToleranceMm = 0, RoundIncrementMm = 1, AllowBarIntersections = true }
    };
    private static ReinforcementSpec Spec(string key, string color, double diameter) => new()
    {
        Key = key, ColorKey = color, TypeKey = key, MaterialKey = "500", DiameterMm = diameter,
        ModelDiameterMm = diameter, SpacingMm = 200, AnchorageMm = 0, ElevationMm = -165 + diameter / 2
    };
    private static FaceInput Face(string id, string color, IEnumerable<(double X, double Y)> vertices, bool alongX) => new()
    { Id = id, ColorKey = color, Vertices = Points(vertices, alongX) };
    private static (double X, double Y)[] Rect(double left, double bottom, double right, double top) => new[]
    { (left, bottom), (right, bottom), (right, top), (left, top) };
    private static List<Point2> Points(IEnumerable<(double X, double Y)> points, bool alongX) => points
        .Select(p => alongX ? new Point2(p.X, p.Y) : new Point2(p.Y, p.X)).ToList();
    private static double Row(PlannedBar bar, bool alongX) => alongX ? bar.Start.Y : bar.Start.X;
    private static double Start(PlannedBar bar, bool alongX) => alongX ? bar.Start.X : bar.Start.Y;
    private static double End(PlannedBar bar, bool alongX) => alongX ? bar.End.X : bar.End.Y;
    private static string Messages(ReinforcementPlan plan) => string.Join("; ", plan.Diagnostics.Select(d => d.Code + ": " + d.Message));
}
