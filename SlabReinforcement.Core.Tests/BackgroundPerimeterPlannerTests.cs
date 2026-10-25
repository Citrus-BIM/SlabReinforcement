using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class BackgroundPerimeterPlannerTests
{
    [Fact]
    public void BothDirectionsFollowLowerRowsAndTheirActualHeight()
    {
        var request = Request();
        var before = Keys(request.BackgroundPlan.Bars);
        var plan = PerimeterPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        var lower = Lower(request);
        Assert.Equal(lower.Count * 2, plan.Bars.Count);
        Assert.All(plan.PerimeterUEdges, e => Assert.True(e.FollowsBackgroundRows));
        foreach (var source in lower)
        {
            bool x = source.SpecKey == "BX";
            var matches = plan.Bars.Where(u => Math.Abs((x ? u.Centerline[2].Start.Y : u.Centerline[2].Start.X)
                - (x ? source.Start.Y : source.Start.X)) < 1e-6
                && Math.Abs(u.Centerline[0].Start.Z - source.Placement.ElevationMm) < 1e-6).ToList();
            Assert.Equal(2, matches.Count);
            Assert.All(matches, u => Assert.Equal(x ? -31 : -45, u.Centerline.Last().End.Z, 6));
        }
        Assert.Equal(before, Keys(request.BackgroundPlan.Bars));
        Assert.Empty(BarContainmentValidator.Validate(plan.Bars, request.Slabs, 220, 25, 25, 25));
    }

    [Fact]
    public void DenserUpperGridDoesNotAddPerimeterBarsAndUpperHeightIsMeasured()
    {
        var request = Request();
        foreach (var bar in request.BackgroundPlan.Bars.Where(b => b.SpecKey == "TX")) bar.Placement.ElevationMm = -37;
        var top = request.BackgroundPlan.Bars.Where(b => b.SpecKey == "TX").ToList();
        request.BackgroundPlan.Bars.AddRange(top.Select(b => new PlannedBar { SpecKey = "TX", Start = new(b.Start.X, b.Start.Y + 20),
            End = new(b.End.X, b.End.Y + 20), Placement = b.Placement }));
        var plan = PerimeterPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(Lower(request).Count * 2, plan.Bars.Count);
        Assert.All(plan.PerimeterUEdges.Where(e => e.AlongX), e => Assert.Equal(-37, e.TopZ));
    }

    [Fact]
    public void DisabledUpperLayersUseConfiguredVirtualHeightsWithoutCreatingUpperBars()
    {
        var request = Request(); request.BackgroundPlan.Bars.RemoveAll(b => b.SpecKey.StartsWith("T"));
        var plan = PerimeterPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.All(plan.PerimeterUEdges, e => Assert.Equal(e.AlongX ? -31 : -45, e.TopZ, 6));
        Assert.All(request.BackgroundPlan.Bars, b => Assert.StartsWith("B", b.SpecKey));
    }

    [Fact]
    public void LargerUBarReportsCoverFailureWithoutMovingTheBackground()
    {
        var request = Request(); request.ModelDiameterMm = request.DiameterMm = 16;
        var before = Keys(request.BackgroundPlan.Bars);
        var plan = PerimeterPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "PERIMETER_BACKGROUND_COVER" && d.IsBlocking);
        Assert.Equal(before, Keys(request.BackgroundPlan.Bars));
    }

    [Fact]
    public void UpperOnlyBackgroundCannotDefineLowerStations()
    {
        var request = Request(); request.BackgroundPlan.Bars.RemoveAll(b => b.SpecKey.StartsWith("B"));
        var plan = PerimeterPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "PERIMETER_BACKGROUND_REQUIRED");
    }

    [Fact]
    public void OpeningsAndOuterEdgesAreSelectedIndependently()
    {
        var request = Request(new() { Shell = Rect(0, 0, 5000, 4000), Holes = new() { Rect(1800, 1300, 3200, 2700) } });
        var all = PerimeterPlanner.Build(request);
        request.Outer = false; var holes = PerimeterPlanner.Build(request);
        request.Outer = true; request.Openings = false; var outer = PerimeterPlanner.Build(request);
        Assert.True(all.CanCreate, Messages(all)); Assert.True(holes.CanCreate, Messages(holes)); Assert.True(outer.CanCreate, Messages(outer));
        Assert.Equal(all.Bars.Count, holes.Bars.Count + outer.Bars.Count);
        Assert.Equal(Lower(request).Count * 2, all.Bars.Count);
        Assert.Empty(BarContainmentValidator.Validate(all.Bars, request.Slabs, 220, 25, 25, 25));
    }

    [Fact]
    public void NativeMaximumSpacingGroupsPreserveEveryActualLowerRow()
    {
        foreach (var request in new[] { Request(), Request(ActualFootprintPerimeterTests.Request().Slabs[0]) })
        {
            var plan = PerimeterPlanner.Build(request);
            Assert.True(plan.CanCreate, Messages(plan));
            Assert.Contains(plan.Groups, g => g.Count > 1);
            Assert.Equal(plan.Bars.Count, plan.Groups.Sum(g => g.Count));
            foreach (var group in plan.Groups)
            {
                Assert.Equal(BarLayoutRule.MaximumSpacing, group.LayoutRule);
                if (group.Count == 1) continue;
                Assert.Equal(group.Count, (int)Math.Ceiling(group.ArrayLengthMm / group.MaximumSpacingMm) + 1);
                Assert.Equal(group.ArrayLengthMm / (group.Count - 1), group.SpacingMm, 6);
            }
            var expected = plan.Bars.Select(b => b.Centerline[2].Start).ToList();
            var actual = plan.Groups.SelectMany(g => Enumerable.Range(0, g.Count).Select(i => new Point3(
                    g.Centerline[2].Start.X + i * g.SpacingMm * g.Placement.Distribution.X,
                    g.Centerline[2].Start.Y + i * g.SpacingMm * g.Placement.Distribution.Y,
                    g.Centerline[2].Start.Z))).ToList();
            Assert.Equal(expected.Count, actual.Count);
            foreach (var point in expected)
            {
                int index = actual.FindIndex(p => Math.Abs(point.X - p.X) <= 1e-6
                    && Math.Abs(point.Y - p.Y) <= 1e-6 && Math.Abs(point.Z - p.Z) <= 1e-6);
                Assert.True(index >= 0, "A physical lower-row endpoint was lost or moved by grouping.");
                actual.RemoveAt(index);
            }
            Assert.Empty(actual);
        }
    }

    [Fact]
    public void IrregularActualLowerStationsArePreservedByGroups()
    {
        var request = Request(); request.BackgroundPlan.Bars.RemoveAll(b => b.SpecKey != "BX");
        var first = request.BackgroundPlan.Bars[0];
        request.BackgroundPlan.Bars = new[] { 400d, 570d, 870d, 1040d }.Select(y => new PlannedBar {
            SpecKey = "BX", Start = new(first.Start.X, y), End = new(first.End.X, y), Placement = first.Placement }).ToList();
        var plan = PerimeterPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan)); Assert.Equal(8, plan.Bars.Count);
        Assert.Equal(plan.Bars.Count, plan.Groups.Sum(g => g.Count));
        Assert.Equal(plan.Bars.Select(b => Key(b.Centerline[2].Start)).OrderBy(s => s),
            plan.Groups.SelectMany(g => Enumerable.Range(0, g.Count).Select(i => Key(new Point3(
                g.Centerline[2].Start.X + i * g.SpacingMm * g.Placement.Distribution.X,
                g.Centerline[2].Start.Y + i * g.SpacingMm * g.Placement.Distribution.Y,
                g.Centerline[2].Start.Z)))).OrderBy(s => s));
    }

    [Fact]
    public void ObliqueBoundaryUsesAxisAlignedUBarsAndSingleSets()
    {
        var request = Request(new() { Shell = new() { new(0, 0), new(4000, 0), new(5000, 3000), new(0, 3000) } });
        request.BackgroundPlan.Bars.RemoveAll(b => b.SpecKey != "BX" || b.Start.Y < 400 || b.Start.Y > 2500);
        var plan = PerimeterPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(Lower(request).Count * 2, plan.Bars.Count);
        Assert.All(plan.Bars, u => Assert.Equal(u.Centerline[0].Start.Y, u.Centerline[2].Start.Y, 6));
        Assert.All(plan.Groups.Where(g => g.Centerline[2].Start.X > 3900), g => Assert.Equal(1, g.Count));
        Assert.Empty(BarContainmentValidator.Validate(plan.Bars, request.Slabs, 220, 25, 25, 25));
    }

    [Fact]
    public void ActualFirstLayerContourKeepsAllLowerEndpointsWithoutParityOffsets()
    {
        var source = ActualFootprintPerimeterTests.Request();
        var request = Request(source.Slabs[0]);
        var plan = PerimeterPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(Lower(request).Count * 2, plan.Bars.Count);
        Assert.All(plan.PerimeterUEdges, e => Assert.Equal(e.AlongX ? -31 : -45, e.TopZ, 6));
        Assert.Empty(BarContainmentValidator.Validate(plan.Bars, request.Slabs, 220, 25, 25, 25));
    }

    [Fact]
    public void ActualFirstLayerAt200Thickness35CoverAnd12BarsKeepsTheAcceptedFourHeights()
    {
        var request = ActualFootprintPerimeterTests.Request();
        var background = new BackgroundRequest { Slabs = request.Slabs, ThicknessMm = 200,
            TopCoverMm = 35, BottomCoverMm = 35, SideCoverMm = request.SideCoverMm,
            Layers = new[] { ("BX", false, true), ("BY", false, false), ("TX", true, true), ("TY", true, false) }
                .Select(v => new BackgroundLayer { Key = v.Item1, Top = v.Item2, AlongX = v.Item3, TypeKey = "12",
                    DiameterMm = 12, ModelDiameterMm = 12, SpacingMm = 200 }).ToList() };
        request.BackgroundLayers = background.Layers; request.BackgroundPlan = BackgroundPlanner.Build(background);
        Assert.True(request.BackgroundPlan.CanCreate, Messages(request.BackgroundPlan));
        var plan = PerimeterPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan)); Assert.Equal(Lower(request).Count * 2, plan.Bars.Count);
        Assert.All(plan.PerimeterUEdges, e => { Assert.Equal(e.AlongX ? -41 : -53, e.TopZ, 6); Assert.Equal(e.AlongX ? -159 : -147, e.BottomZ, 6); });
        Assert.Empty(BarContainmentValidator.Validate(plan.Bars, request.Slabs, 200, 35, 35, request.SideCoverMm));
    }

    [Theory]
    [InlineData(17)]
    [InlineData(89)]
    public void RotatedWorldFrameKeepsTheSameLowerRowsInTheChosenSlabAxes(double degrees)
    {
        var request = Request();
        double angle = degrees * Math.PI / 180;
        var frame = SlabFrame.FromEdge(new(50000, -25000, 3000), new(50000 + 1000 * Math.Cos(angle), -25000 + 1000 * Math.Sin(angle), 3000), new(0, 0, 1));
        request.Slabs[0].Shell = request.Slabs[0].Shell.Select(p => frame.ToLocal(frame.ToWorld(new(p.X, p.Y, 0)))).Select(p => new Point2(p.X, p.Y)).ToList();
        // This is how Revit's measured world geometry arrives back in Core.
        foreach (var bar in request.BackgroundPlan.Bars)
        {
            var a = frame.ToLocal(frame.ToWorld(new(bar.Start.X, bar.Start.Y, bar.Placement.ElevationMm)));
            var b = frame.ToLocal(frame.ToWorld(new(bar.End.X, bar.End.Y, bar.Placement.ElevationMm)));
            bar.Start = new(a.X, a.Y); bar.End = new(b.X, b.Y);
        }
        var plan = PerimeterPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan)); Assert.Equal(Lower(request).Count * 2, plan.Bars.Count);
        Assert.Empty(BarContainmentValidator.Validate(plan.Bars, request.Slabs, 220, 25, 25, 25));
    }

    [Fact]
    public void MeasuredReversedCenterlineOverridesTheDisplayEndpointsAndPlacementHeight()
    {
        var request = Request(); request.BackgroundPlan.Bars.RemoveAll(b => b.SpecKey != "BX");
        request.BackgroundPlan.Bars = new() { new() { SpecKey = "BX", Start = new(0, 0), End = new(1, 0),
            Placement = new() { Component = "Background", TypeKey = "12", ModelDiameterMm = 12, ElevationMm = 999 },
            Centerline = new() { new() { Start = new(3969, 1234, -180), End = new(31, 1234, -180) } } } };
        var plan = PerimeterPlanner.Build(request);
        Assert.True(plan.CanCreate, Messages(plan)); Assert.Equal(2, plan.Bars.Count);
        Assert.All(plan.Bars, b => { Assert.Equal(1234, b.Centerline[2].Start.Y); Assert.Equal(-180, b.Centerline[0].Start.Z); });
    }

    [Fact]
    public void NarrowIntervalFailsWithoutShorteningTheLegOrPublishingAPartialPlan()
    {
        var request = Request(new() { Shell = Rect(0, 0, 600, 4000) });
        request.LegLengthMm = 700;
        var plan = PerimeterPlanner.Build(request);
        Assert.False(plan.CanCreate); Assert.Empty(plan.Bars); Assert.Empty(plan.Groups); Assert.Empty(plan.PerimeterUEdges);
        Assert.Contains(plan.Diagnostics, d => d.Code == "PERIMETER_BACKGROUND_LEG");
    }

    [Fact]
    public void SeparatePerimeterSpacingNoLongerControlsTheSelectedLowerRows()
    {
        var a = Request(); var b = Request(); a.SpacingMm = 50; b.SpacingMm = 900;
        Assert.Equal(PerimeterPlanner.Build(a).Bars.Select(p => Key(p.Centerline[2].Start)),
            PerimeterPlanner.Build(b).Bars.Select(p => Key(p.Centerline[2].Start)));
    }

    [Fact]
    public void CancellationRemainsImmediate()
    {
        Assert.Throws<OperationCanceledException>(() => PerimeterPlanner.Build(Request(), new CancellationToken(true)));
    }

    internal static PerimeterRequest Request(PolygonInput slab = null)
    {
        var background = new BackgroundRequest { Slabs = new() { slab ?? new() { Shell = Rect(0, 0, 4000, 3000) } },
            ThicknessMm = 220, TopCoverMm = 25, BottomCoverMm = 25, SideCoverMm = 25,
            Layers = new[] { ("BX", false, true, 12d), ("BY", false, false, 16d), ("TX", true, true, 12d), ("TY", true, false, 16d) }
                .Select(v => new BackgroundLayer { Key = v.Item1, Top = v.Item2, AlongX = v.Item3, TypeKey = v.Item4.ToString(),
                    DiameterMm = v.Item4, ModelDiameterMm = v.Item4, SpacingMm = 200 }).ToList() };
        var plan = BackgroundPlanner.Build(background);
        Assert.True(plan.CanCreate, Messages(plan));
        return new PerimeterRequest { Slabs = background.Slabs, TypeKey = "8", DiameterMm = 8, ModelDiameterMm = 8, BendDiameterMm = 32,
            ThicknessMm = 220, TopCoverMm = 25, BottomCoverMm = 25, SideCoverMm = 25, LegLengthMm = 500,
            SpacingMm = 50, Outer = true, Openings = true, UseUbars = true, BackgroundLayers = background.Layers, BackgroundPlan = plan };
    }

    internal static List<PlannedBar> Lower(PerimeterRequest request) => request.BackgroundPlan.Bars.Where(b => b.SpecKey.StartsWith("B")).ToList();
    internal static string Messages(ReinforcementPlan p) => string.Join("; ", p.Diagnostics.Select(d => d.Code + ":" + d.Message));
    private static string[] Keys(IEnumerable<PlannedBar> bars) => bars.Select(b => b.SpecKey + ":" + b.Start.X + "," + b.Start.Y + "," + b.End.X + "," + b.End.Y + "," + b.Placement.ElevationMm).ToArray();
    private static string Key(Point3 p) => $"{p.X:0.000000},{p.Y:0.000000},{p.Z:0.000000}";
    internal static List<Point2> Rect(double a, double b, double c, double d) => new() { new(a, b), new(c, b), new(c, d), new(a, d) };
}
