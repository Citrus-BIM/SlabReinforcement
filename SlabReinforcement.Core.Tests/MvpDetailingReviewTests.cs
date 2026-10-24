using NetTopologySuite.Geometries;
using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class MvpDetailingReviewTests
{
    [Fact]
    public void PhysicalLayerStackIsIndependentOfLayerOrderAndNominalDiameter()
    {
        BackgroundRequest request = Background();
        request.Layers[0].DiameterMm = 10;
        request.Layers[0].ModelDiameterMm = 14;
        request.OuterLayerOffsetMm = 24;
        ReinforcementPlan first = BackgroundPlanner.Build(request);
        Assert.True(first.CanCreate, Messages(first));
        request.Layers.Reverse();
        ReinforcementPlan reverse = BackgroundPlanner.Build(request);
        Assert.Equal(first.Bars.Select(Key).OrderBy(key => key), reverse.Bars.Select(Key).OrderBy(key => key));
        var bottomX = first.Bars.First(bar => bar.SpecKey == "BX").Placement;
        var bottomY = first.Bars.First(bar => bar.SpecKey == "BY").Placement;
        Assert.Equal(14, bottomX.ModelDiameterMm);
        Assert.Equal(14, bottomY.ElevationMm - bottomX.ElevationMm, 8);
        Assert.Equal(49, bottomX.ElevationMm - bottomX.ModelDiameterMm / 2 + request.ThicknessMm, 8);
    }

    [Fact]
    public void SingleYLayerUsesOuterCoverWithoutReservingMissingXLayer()
    {
        BackgroundRequest request = Background();
        request.Layers = request.Layers.Where(layer => layer.Key == "TY").ToList();
        ReinforcementPlan plan = BackgroundPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.All(plan.Bars, bar => Assert.Equal(-32, bar.Placement.ElevationMm, 8));
    }

    [Fact]
    public void BackgroundClearanceIsMeasuredToEveryHoleAndOuterEdge()
    {
        BackgroundRequest request = Background();
        request.Slabs[0].Holes.Add(Rectangle(1300, 900, 1600, 1000).Shell);
        ReinforcementPlan plan = BackgroundPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        Polygon concrete = Polygon(request.Slabs[0]);
        foreach (PlannedBar bar in plan.Bars)
        {
            var axis = new LineString(new[] { new Coordinate(bar.Start.X, bar.Start.Y), new Coordinate(bar.End.X, bar.End.Y) });
            Assert.True(concrete.Covers(axis));
            Assert.True(axis.Distance(concrete.Boundary) >= request.SideCoverMm + bar.Placement.ModelDiameterMm / 2 - 1e-6);
        }
    }

    [Fact]
    public void UBendsHaveThePhysicalCentrelineRadiusAndContinuousPaths()
    {
        PerimeterRequest request = Perimeter();
        ReinforcementPlan plan = PerimeterPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        foreach (PlannedBar bar in plan.Bars)
        {
            Assert.Equal(5, bar.Centerline.Count);
            for (int index = 0; index < bar.Centerline.Count - 1; index++)
                Assert.Equal(bar.Centerline[index].End, bar.Centerline[index + 1].Start);
            foreach (CenterlineSegment arc in bar.Centerline.Where(segment => segment.Midpoint.HasValue))
            {
                double a = Distance(arc.Start, arc.Midpoint.Value);
                double b = Distance(arc.Midpoint.Value, arc.End);
                double c = Distance(arc.End, arc.Start);
                double semi = (a + b + c) / 2;
                double triangleArea = Math.Sqrt(semi * (semi - a) * (semi - b) * (semi - c));
                double radius = a * b * c / (4 * triangleArea);
                Assert.Equal((request.BendDiameterMm + request.ModelDiameterMm) / 2, radius, 8);
                Assert.Equal(Math.Sqrt(2) * radius, c, 8);
            }
        }
    }

    [Fact]
    public void ClosedLongitudinalShellAndHoleCurvesRespectConcreteCover()
    {
        PerimeterRequest request = Perimeter();
        request.UseUbars = false;
        request.UseLongitudinalBars = true;
        request.Slabs[0].Holes.Add(Rectangle(1500, 1000, 1000, 1000).Shell);
        ReinforcementPlan plan = PerimeterPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(4, plan.Bars.Count);
        Polygon concrete = Polygon(request.Slabs[0]);
        foreach (PlannedBar bar in plan.Bars)
        {
            Assert.Equal(bar.Centerline[0].Start, bar.Centerline.Last().End);
            for (int index = 0; index < bar.Centerline.Count - 1; index++)
                Assert.Equal(bar.Centerline[index].End, bar.Centerline[index + 1].Start);
            foreach (Point3 point in PerimeterPlanner.Sample(bar.Centerline, 0.01))
            {
                var sample = new Point(point.X, point.Y);
                Assert.True(concrete.Covers(sample));
                Assert.True(sample.Distance(concrete.Boundary) >= request.SideCoverMm + request.ModelDiameterMm / 2 - 1e-6);
            }
        }
    }

    [Fact]
    public void ReservedDepthCoversBandsAfterCollinearVerticesAreRemoved()
    {
        PerimeterRequest request = Perimeter();
        request.Slabs[0].Shell = new List<Point2>
        {
            new(0, 0), new(2000, 0), new(4000, 0), new(5000, 2000), new(2500, 4000), new(0, 2000)
        };
        ReinforcementPlan plan = PerimeterPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        double actualOccupiedDepth = plan.Bars.Max(bar => bar.Centerline[0].Start.Z + request.ModelDiameterMm / 2)
            + request.ThicknessMm - request.BottomCoverMm;
        Assert.True(PerimeterPlanner.ReservedDepth(request) >= actualOccupiedDepth - 1e-6,
            $"Reserved {PerimeterPlanner.ReservedDepth(request)} mm, actual U-band depth {actualOccupiedDepth} mm.");
    }

    [Fact]
    public void ThinLongitudinalTopAndBottomRingsMustNotInterpenetrate()
    {
        PerimeterRequest request = Perimeter();
        request.UseUbars = false;
        request.UseLongitudinalBars = true;
        request.ThicknessMm = 60;
        ReinforcementPlan plan = PerimeterPlanner.Build(request);
        Assert.False(plan.CanCreate,
            "Upper and lower longitudinal bars at z=-29/-31 with diameter 8 mm physically overlap.");
    }

    [Fact]
    public void UVerticalWebCanMeetBackgroundEndsDespiteSeparateElevationBands()
    {
        // Characterises why the combined validator is mandatory: vertical clearance alone is insufficient.
        PerimeterRequest perimeterRequest = Perimeter();
        perimeterRequest.Slabs = new List<PolygonInput> { Rectangle(0, 0, 4100, 3000) };
        BackgroundRequest backgroundRequest = Background();
        backgroundRequest.Slabs = perimeterRequest.Slabs;
        backgroundRequest.OuterLayerOffsetMm = PerimeterPlanner.ReservedDepth(perimeterRequest);
        ReinforcementPlan perimeter = PerimeterPlanner.Build(perimeterRequest);
        ReinforcementPlan background = BackgroundPlanner.Build(backgroundRequest);
        Assert.True(perimeter.CanCreate, Messages(perimeter));
        Assert.True(background.CanCreate, Messages(background));
        bool overlap = perimeter.Bars.Any(u => background.Bars.Any(bar =>
        {
            CenterlineSegment web = u.Centerline[2];
            if (bar.Placement.ElevationMm < Math.Min(web.Start.Z, web.End.Z) ||
                bar.Placement.ElevationMm > Math.Max(web.Start.Z, web.End.Z)) return false;
            var backgroundAxis = new LineString(new[] { new Coordinate(bar.Start.X, bar.Start.Y), new Coordinate(bar.End.X, bar.End.Y) });
            return backgroundAxis.Distance(new Point(web.Start.X, web.Start.Y)) <
                (bar.Placement.ModelDiameterMm + u.Placement.ModelDiameterMm) / 2 - 1e-6;
        }));
        Assert.True(overlap, "This adversarial dimension should demonstrate a collision that the combined validator must reject.");
        Assert.Contains(CompositePlanValidator.Validate(perimeter.Bars.Concat(background.Bars)), diagnostic => diagnostic.IsBlocking);
    }

    private static BackgroundRequest Background() => new()
    {
        Slabs = new List<PolygonInput> { Rectangle(0, 0, 4000, 3000) }, ThicknessMm = 240,
        TopCoverMm = 25, BottomCoverMm = 25, SideCoverMm = 25,
        Layers = new List<BackgroundLayer>
        {
            new() { Key = "BX", TypeKey = "12", AlongX = true, DiameterMm = 12, ModelDiameterMm = 12, SpacingMm = 200 },
            new() { Key = "BY", TypeKey = "14", AlongX = false, DiameterMm = 14, ModelDiameterMm = 14, SpacingMm = 200 },
            new() { Key = "TX", TypeKey = "12", Top = true, AlongX = true, DiameterMm = 12, ModelDiameterMm = 12, SpacingMm = 200 },
            new() { Key = "TY", TypeKey = "14", Top = true, AlongX = false, DiameterMm = 14, ModelDiameterMm = 14, SpacingMm = 200 }
        }
    };
    private static PerimeterRequest Perimeter() => new()
    {
        Slabs = new List<PolygonInput> { Rectangle(0, 0, 4000, 3000) }, TypeKey = "8", DiameterMm = 8,
        ModelDiameterMm = 8, BendDiameterMm = 32, ThicknessMm = 240, TopCoverMm = 25,
        BottomCoverMm = 25, SideCoverMm = 25, SpacingMm = 200, LegLengthMm = 500,
        Outer = true, Openings = true, UseUbars = true
    };
    private static PolygonInput Rectangle(double x, double y, double width, double height) => new()
    {
        Shell = new List<Point2> { new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height) }
    };
    private static Polygon Polygon(PolygonInput input) => new(Ring(input.Shell), input.Holes.Select(Ring).ToArray());
    private static LinearRing Ring(IEnumerable<Point2> points)
    {
        var coordinates = points.Select(point => new Coordinate(point.X, point.Y)).ToList();
        coordinates.Add(coordinates[0].Copy());
        return new LinearRing(coordinates.ToArray());
    }
    private static double Distance(Point3 a, Point3 b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));
    private static string Key(PlannedBar bar) => $"{bar.SpecKey}|{bar.Start.X:R},{bar.Start.Y:R}|{bar.End.X:R},{bar.End.Y:R}|{bar.Placement.ElevationMm:R}";
    private static string Messages(ReinforcementPlan plan) => string.Join("; ", plan.Diagnostics.Select(diagnostic => diagnostic.Message));
}
