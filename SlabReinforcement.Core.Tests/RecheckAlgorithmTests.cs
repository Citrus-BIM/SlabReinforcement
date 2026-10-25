using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;
using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class RecheckAlgorithmTests
{
    [Fact]
    public void EverySelectedOpeningMustSurviveTheCoverInsetOrBlockCreation()
    {
        var request = Perimeter();
        request.Outer = false;
        request.LegLengthMm = 150;
        request.Slabs[0].Holes.Add(Rect(40, 1000, 1000, 2000));
        request.Slabs[0].Holes.Add(Rect(3000, 1000, 4000, 2000));
        var plan = PerimeterPlanner.Build(request);

        // The 40 mm ligament beside the first opening cannot hold two 29 mm
        // centreline setbacks. It must not silently erase an explicitly selected
        // opening while the second opening alone makes the plan creatable.
        Assert.False(plan.CanCreate, "An explicitly selected opening disappeared after inset: " + Messages(plan));
        Assert.Contains(plan.Diagnostics, d => d.Code == "PERIMETER_OPENING_TOPOLOGY" && d.IsBlocking);
        Assert.Empty(plan.Bars);
    }

    [Fact]
    public void TwoSelectedOpeningsCannotSilentlyMergeAtTheirCoverInsets()
    {
        var request = Perimeter();
        request.Outer = false;
        request.LegLengthMm = 150;
        request.Slabs[0].Holes.Add(Rect(1500, 1000, 2500, 2000));
        request.Slabs[0].Holes.Add(Rect(2540, 1000, 3540, 2000));
        var plan = PerimeterPlanner.Build(request);
        Assert.False(plan.CanCreate, "Two requested openings became one permitted contour: " + Messages(plan));
        Assert.Contains(plan.Diagnostics, d => d.Code == "PERIMETER_OPENING_TOPOLOGY" && d.IsBlocking);
        Assert.Empty(plan.Bars);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EverySelectedOuterContourMustHaveExactlyOneSurvivingInsetComponent(int scenario)
    {
        var request = Perimeter();
        request.Openings = false;
        request.LegLengthMm = 150;
        var narrow = new PolygonInput { Shell = Rect(7000, 0, 7050, 2000) };
        var dumbbell = new PolygonInput
        {
            Shell = new()
            {
                new(0, 0), new(2000, 0), new(2000, 980), new(3000, 980), new(3000, 0), new(5000, 0),
                new(5000, 2000), new(3000, 2000), new(3000, 1020), new(2000, 1020), new(2000, 2000), new(0, 2000)
            }
        };
        if (scenario == 0) request.Slabs.Add(narrow);
        else request.Slabs = scenario == 1 ? new() { dumbbell } : new() { dumbbell, narrow };

        // Scenario 2 intentionally preserves the global contour count: one island
        // is lost while another splits into two. A count-only guard cannot see it.
        if (scenario == 2)
        {
            var host = UnaryUnionOp.Union(request.Slabs.Select(Polygon).Cast<Geometry>().ToList());
            var inset = host.Buffer(-29, new NetTopologySuite.Operation.Buffer.BufferParameters
                { JoinStyle = NetTopologySuite.Operation.Buffer.JoinStyle.Mitre, MitreLimit = 5, SimplifyFactor = 0 });
            Assert.Equal(2, host.NumGeometries);
            Assert.Equal(host.NumGeometries, inset.NumGeometries);
        }
        var plan = PerimeterPlanner.Build(request);
        Assert.False(plan.CanCreate, $"Scenario {scenario}: selected outer contour disappeared or split: {Messages(plan)}");
        Assert.Contains(plan.Diagnostics, d => d.Code == "PERIMETER_OUTER_TOPOLOGY" && d.IsBlocking);
        Assert.Empty(plan.Bars);
    }

    [Fact]
    public void ConnectedColorsWithoutTypeIdentityUseStableOriginalSpecification()
    {
        // Without type identity the stable original key breaks the diameter/pitch tie.
        // The selected specification still owns all placement and anchorage data.
        var request = new PlanRequest
        {
            Slabs = new() { new() { Shell = Rect(0, 0, 3000, 2000) } },
            Faces = new()
            {
                new() { Id = "left", ColorKey = "red", Vertices = Rect(300, 300, 1500, 1700) },
                new() { Id = "right", ColorKey = "blue", Vertices = Rect(1500, 300, 2700, 1700) }
            },
            Specifications = new()
            {
                new() { Key = "left", ColorKey = "red", MaterialKey = "500", DiameterMm = 12, SpacingMm = 200, AnchorageMm = 100 },
                new() { Key = "right", ColorKey = "blue", MaterialKey = "500", DiameterMm = 12, SpacingMm = 200, AnchorageMm = 100 }
            },
            Options = new() { EdgeCoverMm = 25, MergeToleranceMm = 0, RoundIncrementMm = 1 }
        };
        var split = ReinforcementPlanner.Build(request);
        Assert.True(split.CanCreate, Messages(split));
        Assert.DoesNotContain(split.Diagnostics, d => d.Code == "FINAL_BAR_COLLISION");
        Assert.Equal(7, split.Bars.Count);
        Assert.All(split.Bars, bar => {
            Assert.Equal("left", bar.SpecKey);
            Assert.Equal(200, bar.Start.X);
            Assert.Equal(2800, bar.End.X);
        });
        request.Faces[1].ColorKey = "red";
        request.Specifications.RemoveAt(1);
        var merged = ReinforcementPlanner.Build(request);
        Assert.True(merged.CanCreate, Messages(merged));
        Assert.Equal(7, merged.Bars.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OuterOnlySelectionBlocksAHoleJoiningOutsideButAllowsInternalHoleMerging(bool joinsOutside)
    {
        var request = Perimeter();
        request.Openings = false;
        request.LegLengthMm = 150;
        request.Slabs[0].Holes = joinsOutside
            ? new() { Rect(40, 1000, 1000, 2000) }
            : new() { Rect(1500, 1000, 2500, 2000), Rect(2540, 1000, 3540, 2000) };
        var plan = PerimeterPlanner.Build(request);
        Assert.Equal(!joinsOutside, plan.CanCreate);
        if (joinsOutside)
        {
            Assert.Contains(plan.Diagnostics, d => d.Code == "PERIMETER_OUTER_TOPOLOGY" && d.IsBlocking);
            Assert.Empty(plan.Bars);
        }
        else Assert.DoesNotContain(plan.Diagnostics, d => d.IsBlocking);
    }

    [Fact]
    public void SeparateUnchangedOuterContoursRemainCreatable()
    {
        var request = Perimeter();
        request.Openings = false;
        request.LegLengthMm = 150;
        request.Slabs.Add(new() { Shell = Rect(7000, 0, 11000, 3000) });
        var plan = PerimeterPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Contains(plan.Bars, b => b.Start.X < 6000);
        Assert.Contains(plan.Bars, b => b.Start.X > 7000);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BackgroundGroupsPreserveEveryMemberAndActualClearanceOnObliqueHosts(bool alongX)
    {
        for (int seed = 0; seed < 24; seed++)
        {
            var slab = new PolygonInput
            {
                Shell = new() { new(0, 0), new(4200 + seed * 13, 170), new(4500, 2900), new(2100, 3300), new(-200, 2700) },
                Holes = new() { Rect(1600 + seed, 1000, 2300, 1800 + seed) }
            };
            var request = new BackgroundRequest
            {
                Slabs = new() { slab }, ThicknessMm = 250, TopCoverMm = 25, BottomCoverMm = 25, SideCoverMm = 25,
                Layers = new() { new() { Key = "one", TypeKey = "12", AlongX = alongX, DiameterMm = 12, ModelDiameterMm = 16, SpacingMm = 137 + seed } }
            };
            var plan = BackgroundPlanner.Build(request);
            Assert.True(plan.CanCreate, Messages(plan));
            AssertSameGroups(plan, alongX);
            var concrete = Polygon(slab);
            foreach (var bar in plan.Bars)
            {
                var axis = Axis(bar);
                Assert.True(concrete.Covers(axis), $"Seed {seed}: outside concrete");
                Assert.True(concrete.Boundary.Distance(axis) >= 33 - 1e-6, $"Seed {seed}: lost physical side cover");
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ZoneGroupingPreservesAnchorageAndHalfPitchCoverageAcrossSlopedDemand(bool alongX)
    {
        for (int seed = 0; seed < 16; seed++)
        {
            var demand = new List<Point2> { new(900, 1000), new(2600 + seed * 7, 1000), new(3200, 2300), new(700, 2300) };
            if (!alongX) demand = demand.Select(p => new Point2(p.Y, p.X)).ToList();
            var spec = new ReinforcementSpec { Key = "one", ColorKey = "red", MaterialKey = "500", DiameterMm = 12, ModelDiameterMm = 16, SpacingMm = 150, AnchorageMm = 137 };
            var request = new PlanRequest
            {
                Slabs = new() { new() { Shell = Rect(0, 0, 5000, 5000) } },
                Faces = new() { new() { Id = "demand", ColorKey = "red", Vertices = demand } },
                Specifications = new() { spec },
                Options = new() { AlongX = alongX, EdgeCoverMm = 25, MergeToleranceMm = 150, RoundIncrementMm = 25, CoverageHalfSpacing = true }
            };
            var plan = ReinforcementPlanner.Build(request);
            Assert.True(plan.CanCreate, $"Seed {seed}: {Messages(plan)}");
            AssertSameGroups(plan, alongX);
            var demandGeometry = Polygon(new() { Shell = demand });
            var patches = plan.Bars.Select(bar => Patch(bar, 75, alongX)).Cast<Geometry>().ToList();
            Assert.True(demandGeometry.Difference(UnaryUnionOp.Union(patches)).Area <= 1e-5, $"Seed {seed}: demand escaped physical half-pitch strips");
            foreach (var bar in plan.Bars)
            {
                var intersection = demandGeometry.Intersection(Axis(bar));
                if (intersection.IsEmpty || intersection.Length < 1e-6) continue;
                var box = intersection.EnvelopeInternal;
                double start = alongX ? bar.Start.X : bar.Start.Y;
                double end = alongX ? bar.End.X : bar.End.Y;
                Assert.True(start <= (alongX ? box.MinX : box.MinY) - spec.AnchorageMm + 1e-6);
                Assert.True(end >= (alongX ? box.MaxX : box.MaxY) + spec.AnchorageMm - 1e-6);
            }
        }
    }

    [Fact]
    public void CurvedUBarProjectionRetainsHoleCoverForBothInputWindingDirections()
    {
        var request = Perimeter();
        request.LegLengthMm = 150;
        request.Slabs[0].Holes.Add(Rect(2000, 1000, 3000, 2000));
        foreach (bool reverse in new[] { false, true })
        {
            if (reverse) { request.Slabs[0].Shell.Reverse(); request.Slabs[0].Holes[0].Reverse(); }
            var plan = PerimeterPlanner.Build(request);
            Assert.True(plan.CanCreate, Messages(plan));
            var concrete = Polygon(request.Slabs[0]);
            foreach (var bar in plan.Bars)
            {
                var points = PerimeterPlanner.Sample(bar.Centerline, 0.002).ToList();
                foreach (var p in points)
                {
                    var xy = new Point(p.X, p.Y);
                    Assert.True(concrete.Covers(xy));
                    Assert.True(concrete.Boundary.Distance(xy) >= 29 - 1e-6);
                    Assert.InRange(p.Z, -246, -29);
                }
            }
            Assert.Empty(BarContainmentValidator.Validate(plan.Bars, request.Slabs, 275, 25, 25, 25));
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfinitePerimeterAndBackgroundDimensionsCannotYieldCreatablePlans(double invalid)
    {
        var perimeter = Perimeter(); perimeter.SideCoverMm = invalid;
        Assert.False(PerimeterPlanner.Build(perimeter).CanCreate);
        var background = new BackgroundRequest
        {
            Slabs = new() { new() { Shell = Rect(0, 0, 5000, 4000) } }, ThicknessMm = 250,
            Layers = new() { new() { Key = "one", TypeKey = "12", AlongX = true, DiameterMm = 12, ModelDiameterMm = 12, SpacingMm = invalid } }
        };
        Assert.False(BackgroundPlanner.Build(background).CanCreate);
    }

    private static PerimeterRequest Perimeter() => new()
    {
        Slabs = new() { new() { Shell = Rect(0, 0, 6000, 4000) } }, TypeKey = "8", DiameterMm = 8, ModelDiameterMm = 8,
        BendDiameterMm = 32, ThicknessMm = 275, TopCoverMm = 25, BottomCoverMm = 25, SideCoverMm = 25,
        SpacingMm = 200, LegLengthMm = 500, Outer = true, Openings = true, UseUbars = true
    };
    private static void AssertSameGroups(ReinforcementPlan plan, bool alongX)
    {
        var expanded = plan.Groups.SelectMany(g => Enumerable.Range(0, g.Count).Select(i => new PlannedBar
        {
            Start = new(g.Start.X + (alongX ? 0 : i * g.SpacingMm), g.Start.Y + (alongX ? i * g.SpacingMm : 0)),
            End = new(g.End.X + (alongX ? 0 : i * g.SpacingMm), g.End.Y + (alongX ? i * g.SpacingMm : 0)), SpecKey = g.SpecKey
        })).Select(Key).OrderBy(s => s).ToList();
        Assert.Equal(plan.Bars.Select(Key).OrderBy(s => s), expanded);
        Assert.Equal(expanded.Count, expanded.Distinct().Count());
        Assert.All(plan.Groups, g => Assert.InRange(g.Count, 1, 1002));
    }
    private static string Key(PlannedBar b) => $"{b.SpecKey}:{b.Start.X:F6},{b.Start.Y:F6}:{b.End.X:F6},{b.End.Y:F6}";
    private static string Messages(ReinforcementPlan p) => string.Join("; ", p.Diagnostics.Select(d => d.Code + ": " + d.Message));
    private static List<Point2> Rect(double a, double b, double c, double d) => new() { new(a, b), new(c, b), new(c, d), new(a, d) };
    private static LinearRing Ring(IEnumerable<Point2> source)
    {
        var coords = source.Select(p => new Coordinate(p.X, p.Y)).ToList(); coords.Add(coords[0].Copy());
        return new(coords.ToArray());
    }
    private static Polygon Polygon(PolygonInput p) => new(Ring(p.Shell), p.Holes.Select(Ring).ToArray());
    private static LineString Axis(PlannedBar b) => new(new[] { new Coordinate(b.Start.X, b.Start.Y), new Coordinate(b.End.X, b.End.Y) });
    private static Polygon Patch(PlannedBar b, double reach, bool alongX) => Polygon(new() { Shell = alongX
        ? Rect(b.Start.X, b.Start.Y - reach, b.End.X, b.End.Y + reach)
        : Rect(b.Start.X - reach, b.Start.Y, b.End.X + reach, b.End.Y) });
}
