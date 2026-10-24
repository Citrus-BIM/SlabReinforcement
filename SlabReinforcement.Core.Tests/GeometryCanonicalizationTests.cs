using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Buffer;
using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class GeometryCanonicalizationTests
{
    [Theory]
    [InlineData(true, 136)]
    [InlineData(false, 296)]
    public void RevitFrameRoundoffDoesNotDropBoundaryRows(bool alongX, int expectedCount)
    {
        var exact = new List<Point2> { new(0, -29000), new(61000, -29000), new(61000, 0), new(0, 0) };
        var rotated = new List<Point2> { new(0, 0), new(5.46e-12, -28999.999999999993), new(61000, -28999.999999999993), new(60999.999999999985, 7.28e-12) };
        var first = ReinforcementPlanner.Build(Request(exact, alongX));
        var second = ReinforcementPlanner.Build(Request(rotated, alongX));
        Assert.True(first.CanCreate); Assert.True(second.CanCreate);
        Assert.Equal(expectedCount, first.Bars.Count);
        Assert.Equal(first.Bars.Count, second.Bars.Count);
        Assert.Equal(first.Bars.Select(Key).OrderBy(k => k), second.Bars.Select(Key).OrderBy(k => k));
    }

    [Fact]
    public void SubToleranceNoiseOnHoleEdgesCannotCreateThroughHoleBars()
    {
        var exact = Request(new() { new(0, 0), new(5000, 0), new(5000, 4000), new(0, 4000) }, true);
        exact.Slabs[0].Holes.Add(new() { new(2100, 1700), new(2900, 1700), new(2900, 2300), new(2100, 2300) });
        var noisy = Request(exact.Slabs[0].Shell, true);
        noisy.Slabs[0].Holes.Add(exact.Slabs[0].Holes[0].Select((p, i) => new Point2(p.X + (i % 2 == 0 ? 1e-10 : -1e-10), p.Y - 1e-10)).ToList());
        var a = ReinforcementPlanner.Build(exact); var b = ReinforcementPlanner.Build(noisy);
        Assert.True(a.CanCreate); Assert.True(b.CanCreate);
        Assert.Equal(a.Bars.Select(Key).OrderBy(k => k), b.Bars.Select(Key).OrderBy(k => k));
        Assert.DoesNotContain(b.Bars, bar => bar.Start.Y > 1700 && bar.Start.Y < 2300 && bar.Start.X < 2100 && bar.End.X > 2900);
    }

    private static PlanRequest Request(List<Point2> shell, bool alongX)
    {
        var coordinates = shell.Select(p => new Coordinate(p.X, p.Y)).Append(new Coordinate(shell[0].X, shell[0].Y)).ToArray();
        var demand = (Polygon)new GeometryFactory().CreatePolygon(coordinates).Buffer(-1000, new BufferParameters { JoinStyle = JoinStyle.Mitre });
        return new PlanRequest
        {
            Slabs = new() { new() { Shell = shell } },
            Faces = new() { new() { Id = "synthetic-inner-strip", ColorKey = "red", Vertices = demand.ExteriorRing.Coordinates.Take(demand.ExteriorRing.NumPoints - 1).Select(p => new Point2(p.X, p.Y)).ToList() } },
            Specifications = new() { new() { Key = "d12", ColorKey = "red", MaterialKey = "A500", DiameterMm = 12, SpacingMm = 200, AnchorageMm = 0 } },
            Options = new() { AlongX = alongX, EdgeCoverMm = 25, MergeToleranceMm = 0, RoundIncrementMm = 1 }
        };
    }
    private static string Key(PlannedBar bar) => $"{bar.Start.X:R},{bar.Start.Y:R}/{bar.End.X:R},{bar.End.Y:R}";
}
