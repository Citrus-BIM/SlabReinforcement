using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class PolygonTopologyTests
{
    [Fact]
    public void ClassifiesHoleAndIslandRegardlessOfLoopOrderAndWinding()
    {
        var outer = Rect(0, 0, 1000, 1000);
        var hole = Rect(100, 100, 900, 900);
        var island = Rect(200, 200, 800, 800);
        hole.Reverse();
        var result = PolygonTopology.FromLoops(new[] { island, hole, outer });
        Assert.Equal(2, result.Count);
        Assert.Single(result[0].Holes);
        Assert.Empty(result[1].Holes);
        Assert.Equal(720000, result.Sum(p => Area(p.Shell) - p.Holes.Sum(Area)), 6);
    }

    [Fact]
    public void AcceptsExplicitClosureAndConsecutiveRepeatedVertices()
    {
        var points = Rect(0, 0, 100, 100);
        points.Insert(1, points[0]);
        points.Add(points[0]);
        var result = Assert.Single(PolygonTopology.FromLoops(new[] { points }));
        Assert.Equal(4, result.Shell.Count);
    }

    [Fact]
    public void RejectsSelfCrossingRing()
    {
        var crossed = new[] { new Point2(0, 0), new Point2(100, 100), new Point2(0, 100), new Point2(100, 0) };
        Assert.Throws<ArgumentException>(() => PolygonTopology.FromLoops(new[] { crossed }));
    }

    [Fact]
    public void RejectsCrossingAndTouchingLoopsInsteadOfGuessingHoleTopology()
    {
        Assert.Throws<ArgumentException>(() => PolygonTopology.FromLoops(new[] { Rect(0, 0, 100, 100), Rect(50, 50, 150, 150) }));
        Assert.Throws<ArgumentException>(() => PolygonTopology.FromLoops(new[] { Rect(0, 0, 100, 100), Rect(0, 20, 30, 30) }));
    }

    [Fact]
    public void RejectsNonfiniteCoordinates()
    {
        Assert.Throws<ArgumentException>(() => PolygonTopology.FromLoops(new[] { new[] { new Point2(0, 0), new Point2(double.NaN, 100), new Point2(100, 0) } }));
    }

    [Fact]
    public void ProducesDeterministicGeometryForReversedInputOrder()
    {
        var a = Rect(0, 0, 100, 100);
        var b = Rect(200, 0, 300, 100);
        var first = PolygonTopology.FromLoops(new[] { a, b });
        a.Reverse();
        b.Reverse();
        var second = PolygonTopology.FromLoops(new[] { b, a });
        Assert.Equal(first.Select(Serialize), second.Select(Serialize));
    }

    private static string Serialize(PolygonInput polygon) => string.Join(";", polygon.Shell.Select(p => $"{p.X:R},{p.Y:R}"));
    private static List<Point2> Rect(double x0, double y0, double x1, double y1) => new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };
    private static double Area(List<Point2> points) => Math.Abs(points.Select((p, i) => p.X * points[(i + 1) % points.Count].Y - points[(i + 1) % points.Count].X * p.Y).Sum()) / 2;
}
