using NetTopologySuite.Geometries;
using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class ZoneUnificationTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ToleranceIsMeasuredFromFirstOriginalRowForGrowingAndShrinkingDemand(bool alongX, bool grows)
    {
        var request = SlopedRequest(alongX, grows, 2000);
        var plan = ReinforcementPlanner.Build(request);

        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(new[] { 4, 4, 3 }, plan.Groups.Select(g => g.Count));
        Assert.Equal(11, plan.Bars.Count);
        AssertGeometryAndGroups(request, plan);
        foreach (var bar in plan.Bars)
        {
            double row = alongX ? bar.Start.Y : bar.Start.X;
            double demandEnd = grows ? 3000 + row - 400 : 5000 - row + 400;
            Assert.True((alongX ? bar.Start.X : bar.Start.Y) <= 900 + 1e-6);
            Assert.True((alongX ? bar.End.X : bar.End.Y) >= demandEnd + 100 - 1e-6);
        }
    }

    [Fact]
    public void RoundingUsesOriginalRunEnvelopeWithoutRepeatedLengthGrowth()
    {
        var request = SlopedRequest(true, true, 200);
        request.Options.RoundIncrementMm = 100;
        request.Options.MergeToleranceMm = 600;
        var unified = ReinforcementPlanner.Build(request);

        Assert.True(unified.CanCreate, Messages(unified));
        var group = Assert.Single(unified.Groups);
        Assert.Equal(11, group.Count);
        // Individually rounded original runs span x=860..3300. Fit their
        // envelope once to 2500 mm; repeated merges must not inflate it again.
        Assert.Equal(2500, group.End.X - group.Start.X, 6);
        Assert.Equal(830, group.Start.X, 6);
        Assert.Equal(3330, group.End.X, 6);
        AssertGeometryAndGroups(request, unified);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(199, 0)]
    [InlineData(200, 5)]
    [InlineData(600, 3)]
    public void SelectedToleranceControlsEndpointUnification(double tolerance, int expectedGroups)
    {
        var request = SlopedRequest(true, true, 2000);
        request.Options.MergeToleranceMm = tolerance;
        var plan = ReinforcementPlanner.Build(request);
        Assert.Equal(expectedGroups > 0, plan.CanCreate);
        Assert.Equal(expectedGroups, plan.Groups.Count);
        Assert.All(plan.Groups, g => Assert.True(g.Count >= 2));
        AssertGeometryAndGroups(request, plan);
    }

    [Theory]
    [InlineData(true, 1000, 1)]
    [InlineData(true, 2950, 2)]
    [InlineData(false, 1000, 1)]
    [InlineData(false, 2950, 2)]
    public void CompatibleUConnectionsGroupWhileEachActualRowKeepsItsOwnEvidence(bool alongX, double right, int connections)
    {
        var request = PerimeterZoneEndsTests.Request(50, right);
        if (!alongX)
        {
            Point2 Swap(Point2 p) => new(p.Y, p.X);
            request.Slabs[0].Shell = request.Slabs[0].Shell.Select(Swap).ToList();
            request.Faces[0].Vertices = request.Faces[0].Vertices.Select(Swap).ToList();
            var perimeter = PerimeterZoneEndsTests.Perimeter();
            perimeter.Slabs = request.Slabs;
            request.PerimeterPlan = PerimeterPlanner.Build(perimeter);
            request.Options.AlongX = false;
        }
        request.Options.RoundIncrementMm = 100;
        var plan = ReinforcementPlanner.Build(request);

        Assert.True(plan.CanCreate, Messages(plan));
        var group = Assert.Single(plan.Groups);
        Assert.Equal(plan.Bars.Count, group.Count);
        Assert.True(group.Count > 1);
        AssertGeometryAndGroups(request, plan);
        Assert.All(plan.Bars, bar =>
        {
            Assert.Equal(connections, bar.EndConnections.Count);
            foreach (var connection in bar.EndConnections)
            {
                var edge = request.PerimeterPlan.PerimeterUEdges.Single(e => e.Id == connection.EdgeId);
                var point = connection.AtStart ? bar.Start : bar.End;
                double dx = edge.End.X - edge.Start.X, dy = edge.End.Y - edge.Start.Y;
                double station = ((point.X - edge.Start.X) * dx + (point.Y - edge.Start.Y) * dy) / Math.Sqrt(dx * dx + dy * dy);
                Assert.Equal(edge.StationsMm.Min(s => Math.Abs(s - station)), connection.StationOffsetMm, 6);
                Assert.True(connection.OverlapMm > 0);
            }
        });
        Assert.True(plan.Bars.Select(b => b.EndConnections[0].StationOffsetMm).Distinct().Count() > 1);
    }

    [Fact]
    public void CoverageRowsWithEqualFinalEndpointsRejoinWithoutMovingOrDuplicatingBars()
    {
        var request = SlopedRequest(true, true, 0);
        request.Faces[0].Vertices = new()
        {
            new(300, 200), new(1000, 200), new(1000, 201), new(2300, 201),
            new(2300, 209), new(1000, 209), new(1000, 401), new(2300, 401),
            new(2300, 409), new(1000, 409), new(1000, 1200), new(300, 1200)
        };
        request.Specifications[0].AnchorageMm = 0;
        var plan = ReinforcementPlanner.Build(request);

        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Contains(plan.Diagnostics, d => d.Code == "ROWS_EXTENDED_FOR_COVERAGE");
        Assert.Equal(6, plan.Bars.Count);
        Assert.Equal(new[] { 2, 4 }, plan.Groups.Select(g => g.Count));
        Assert.Equal(new[] { 200d, 400, 600, 800, 1000, 1200 }, plan.Bars.Select(b => b.Start.Y));
        Assert.All(plan.Bars.Take(2), b => Assert.Equal(2300, b.End.X, 6));
        Assert.All(plan.Bars.Skip(2), b => Assert.Equal(1000, b.End.X, 6));
        AssertGeometryAndGroups(request, plan);
    }

    [Fact]
    public void EqualEndpointsOnDifferentUEdgesKeepSeparateGroups()
    {
        var request = PerimeterZoneEndsTests.Request(50, 1000);
        var perimeter = PerimeterZoneEndsTests.Perimeter();
        perimeter.Slabs = new()
        {
            new() { Shell = PerimeterZoneEndsTests.Rect(0, 0, 3000, 900) },
            new() { Shell = PerimeterZoneEndsTests.Rect(0, 920, 3000, 2000) }
        };
        request.Slabs = perimeter.Slabs;
        request.PerimeterPlan = PerimeterPlanner.Build(perimeter);
        request.Options.MergeToleranceMm = 600;
        var plan = ReinforcementPlanner.Build(request);

        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(new[] { 3, 4 }, plan.Groups.Select(g => g.Count));
        Assert.Equal(2, plan.Bars.SelectMany(b => b.EndConnections).Select(c => c.EdgeId).Distinct().Count());
        Assert.Equal(7, plan.Bars.Count);
        Assert.All(plan.Bars, b => Assert.Single(b.EndConnections));
        foreach (var group in plan.Groups)
            Assert.All(plan.Bars.Where(b => b.Start.Y >= group.Start.Y && b.Start.Y <= group.Start.Y + (group.Count - 1) * group.SpacingMm),
                b => Assert.Equal(group.EndConnections[0].EdgeId, b.EndConnections[0].EdgeId));
    }

    [Fact]
    public void ObliqueUEdgeCannotTurnDifferentPinnedEndpointsIntoArtificialZones()
    {
        var request = PerimeterZoneEndsTests.Request(50, 3350);
        var perimeter = PerimeterZoneEndsTests.Perimeter();
        perimeter.Slabs[0].Shell = new() { new(0, 0), new(3000, 0), new(3400, 2000), new(0, 2000) };
        request.Slabs = perimeter.Slabs;
        request.PerimeterPlan = PerimeterPlanner.Build(perimeter);
        request.Options.MergeToleranceMm = 600;
        var plan = ReinforcementPlanner.Build(request);

        Assert.False(plan.CanCreate);
        Assert.Empty(plan.Bars);
        Assert.Empty(plan.Groups);
        Assert.Contains(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LargeToleranceCannotUnifyThroughAnObliqueOpening(bool alongX)
    {
        var request = SlopedRequest(alongX, true, 2000);
        var hole = new List<Point2> { new(2000, 300), new(2400, 300), new(3400, 2500), new(3000, 2500) };
        request.Slabs[0].Holes.Add(alongX ? hole : hole.Select(p => new Point2(p.Y, p.X)).ToList());
        request.Specifications[0].AnchorageMm = 0;
        request.Options.MergeToleranceMm = 600;
        var plan = ReinforcementPlanner.Build(request);

        Assert.False(plan.CanCreate);
        Assert.Empty(plan.Bars);
        Assert.Empty(plan.Groups);
        Assert.Contains(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED");
    }

    private static PlanRequest SlopedRequest(bool alongX, bool grows, double growth)
    {
        var vertices = new List<Point2>
        {
            new(1000, 400), new(grows ? 3000 : 3000 + growth, 400),
            new(grows ? 3000 + growth : 3000, 2400), new(1000, 2400)
        };
        return new PlanRequest
        {
            Slabs = new() { new() { Shell = PerimeterZoneEndsTests.Rect(0, 0, 7000, 7000) } },
            Faces = new() { new() { Id = "demand", ColorKey = "red", Vertices = alongX ? vertices : vertices.Select(p => new Point2(p.Y, p.X)).ToList() } },
            Specifications = new() { new() { Key = "zone", ColorKey = "red", MaterialKey = "500", DiameterMm = 12, SpacingMm = 200, AnchorageMm = 100 } },
            Options = new() { AlongX = alongX, EdgeCoverMm = 25, MergeToleranceMm = 600, RoundIncrementMm = 1 }
        };
    }

    private static void AssertGeometryAndGroups(PlanRequest request, ReinforcementPlan plan)
    {
        var factory = new GeometryFactory();
        LinearRing Ring(List<Point2> points) => factory.CreateLinearRing(points.Concat(new[] { points[0] }).Select(p => new Coordinate(p.X, p.Y)).ToArray());
        var slab = request.Slabs[0];
        var host = factory.CreatePolygon(Ring(slab.Shell), slab.Holes.Select(Ring).ToArray());
        foreach (var bar in plan.Bars)
        {
            var axis = factory.CreateLineString(new[] { new Coordinate(bar.Start.X, bar.Start.Y), new Coordinate(bar.End.X, bar.End.Y) });
            Assert.True(host.Covers(axis));
            Assert.True(host.Boundary.Distance(axis) >= request.Options.EdgeCoverMm + request.Specifications[0].EffectiveModelDiameterMm / 2 - 1e-6);
        }
        string Key(Point2 a, Point2 b) => $"{a.X:F6},{a.Y:F6}:{b.X:F6},{b.Y:F6}";
        var expanded = plan.Groups.SelectMany(g => Enumerable.Range(0, g.Count).Select(i =>
        {
            double x = request.Options.AlongX ? 0 : i * g.SpacingMm;
            double y = request.Options.AlongX ? i * g.SpacingMm : 0;
            return Key(new(g.Start.X + x, g.Start.Y + y), new(g.End.X + x, g.End.Y + y));
        })).OrderBy(x => x).ToArray();
        var actual = plan.Bars.Select(b => Key(b.Start, b.End)).OrderBy(x => x).ToArray();
        Assert.Equal(actual, expanded);
        Assert.Equal(actual.Length, actual.Distinct().Count());
        Assert.All(plan.Groups, g => Assert.Equal(request.Specifications[0].SpacingMm, g.SpacingMm));
    }

    private static string Messages(ReinforcementPlan plan) => PerimeterZoneEndsTests.Messages(plan);
}
