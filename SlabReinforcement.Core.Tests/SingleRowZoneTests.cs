using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class SingleRowZoneTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void IsolatedRowIsExcludedFromBothCreationAndPreview(bool alongX, bool numericCoverage)
    {
        var request = Request(alongX, 400, 400);
        request.Options.CoverageHalfSpacing = numericCoverage;
        var plan = ReinforcementPlanner.Build(request);

        Assert.Empty(plan.Groups);
        Assert.Empty(plan.Bars);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED" && !d.IsBlocking);
        Assert.Contains(plan.Diagnostics, d => d.Code == "NO_BARS" && d.IsBlocking);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TwoAdjacentRowsRemainOneZone(bool alongX)
    {
        var plan = ReinforcementPlanner.Build(Request(alongX, 400, 600));

        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(2, Assert.Single(plan.Groups).Count);
        Assert.Equal(2, plan.Bars.Count);
        Assert.DoesNotContain(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SkippingDisconnectedSingleRowDoesNotRemoveValidZone(bool alongX)
    {
        var request = Request(alongX, 400, 600);
        var single = Rect(1000, 1395, 3000, 1405);
        request.Faces.Add(new() { Id = "single", ColorKey = "red", Vertices = Orient(single, alongX) });
        var plan = ReinforcementPlanner.Build(request);

        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(2, Assert.Single(plan.Groups).Count);
        Assert.Equal(2, plan.Bars.Count);
        Assert.All(plan.Bars, b => Assert.InRange(alongX ? b.Start.Y : b.Start.X, 400, 600));
        Assert.Contains(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED" && !d.IsBlocking);
        Assert.DoesNotContain(plan.Diagnostics, d => d.Code == "UNCOVERED_DEMAND");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ArrayLimitDoesNotTurnLastRowOfLargeZoneIntoSingleton(bool alongX)
    {
        var plan = ReinforcementPlanner.Build(Request(alongX, 400, 200800));

        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(new[] { 1001, 2 }, plan.Groups.Select(g => g.Count));
        Assert.Equal(1003, plan.Bars.Count);
        Assert.DoesNotContain(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED");
    }

    private static PlanRequest Request(bool alongX, double firstRow, double lastRow) => new()
    {
        Slabs = new() { new() { Shell = Orient(Rect(0, 0, 7000, lastRow + 1000), alongX) } },
        Faces = new() { new() { Id = "zone", ColorKey = "red", Vertices = Orient(Rect(1000, firstRow - 5, 3000, lastRow + 5), alongX) } },
        Specifications = new() { new() { Key = "zone", ColorKey = "red", MaterialKey = "500", DiameterMm = 12, SpacingMm = 200, AnchorageMm = 0 } },
        Options = new() { AlongX = alongX, EdgeCoverMm = 25, MergeToleranceMm = 600, RoundIncrementMm = 1 }
    };

    private static List<Point2> Rect(double x0, double y0, double x1, double y1) => new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };
    private static List<Point2> Orient(List<Point2> points, bool alongX) => alongX ? points : points.Select(p => new Point2(p.Y, p.X)).ToList();
    private static string Messages(ReinforcementPlan plan) => string.Join("; ", plan.Diagnostics.Select(d => d.Code + ": " + d.Message));
}
