using System.Text.Json;
using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class BackgroundPerimeterReadbackPrecisionTests
{
    [Theory]
    [InlineData(true, false, -1e-10)]
    [InlineData(true, true, 1e-10)]
    [InlineData(false, false, -1e-10)]
    [InlineData(false, true, 1e-10)]
    [InlineData(true, false, 1e-10)]
    [InlineData(true, true, -1e-10)]
    [InlineData(false, false, 1e-10)]
    [InlineData(false, true, -1e-10)]
    public void InsetBoundaryReadbackNoiseKeepsActualStationAndOriginalCover(bool alongX, bool high, double noise)
    {
        var request = Request(alongX, high, noise);
        AssertPreservesRow(request, alongX);
    }

    [Theory]
    [InlineData(true, false, -2e-6)]
    [InlineData(true, true, 2e-6)]
    [InlineData(false, false, -2e-6)]
    [InlineData(false, true, 2e-6)]
    public void ActualCoverViolationStillBlocksInsteadOfMovingTheRow(bool alongX, bool high, double noise)
    {
        var request = Request(alongX, high, noise);
        var before = JsonSerializer.Serialize(request.BackgroundPlan.ReferenceBars);
        var plan = PerimeterPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "PERIMETER_BACKGROUND_COVER" && d.IsBlocking);
        Assert.Empty(plan.Bars);
        Assert.Equal(before, JsonSerializer.Serialize(request.BackgroundPlan.ReferenceBars));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeRepeatSnapshotKeepsTheMeasuredTerminalRow(bool swapAxes)
    {
        // Revit 2023 run-20261025-182356/perimeter-repeat-shared.json.
        // The final lower-X station is 4e-13 mm outside the exact inset edge.
        var request = Request(true, true, 0);
        var row = request.BackgroundPlan.ReferenceBars.Single();
        row.Start = new Point2(16.050000000000182, -16.0499999999996);
        row.End = new Point2(7983.95, -16.0499999999996);
        if (swapAxes)
        {
            request.Slabs[0].Shell = request.Slabs[0].Shell.Select(p => new Point2(p.Y, p.X)).ToList();
            row.Start = new Point2(row.Start.Y, row.Start.X);
            row.End = new Point2(row.End.Y, row.End.X);
            row.SpecKey = "BY";
            row.Placement.ElevationMm = -172;
        }
        SetMeasuredCenterline(row);
        AssertPreservesRow(request, !swapAxes);
    }

    private static void AssertPreservesRow(PerimeterRequest request, bool alongX)
    {
        var before = JsonSerializer.Serialize(request.BackgroundPlan.ReferenceBars);
        var row = request.BackgroundPlan.ReferenceBars.Single();
        double station = alongX ? row.Start.Y : row.Start.X;
        var plan = PerimeterPlanner.Build(request);
        Assert.True(plan.CanCreate, string.Join("; ", plan.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Equal(2, plan.Bars.Count);
        Assert.All(plan.Bars, u => Assert.All(u.Centerline, s =>
        {
            Assert.Equal(station, alongX ? s.Start.Y : s.Start.X);
            Assert.Equal(station, alongX ? s.End.Y : s.End.X);
            if (s.Midpoint.HasValue) Assert.Equal(station, alongX ? s.Midpoint.Value.Y : s.Midpoint.Value.X);
        }));
        var shell = request.Slabs[0].Shell;
        double min = shell.Min(p => alongX ? p.X : p.Y) + 16.05;
        double max = shell.Max(p => alongX ? p.X : p.Y) - 16.05;
        var webs = plan.Bars.Select(u => alongX ? u.Centerline[2].Start.X : u.Centerline[2].Start.Y).OrderBy(v => v).ToList();
        Assert.Equal(min, webs[0]);
        Assert.Equal(max, webs[1]);
        Assert.Empty(BarContainmentValidator.Validate(plan.Bars, request.Slabs, 200, 10, 10, 10.05));
        Assert.Equal(before, JsonSerializer.Serialize(request.BackgroundPlan.ReferenceBars));
        Assert.Equal(2, plan.Groups.Sum(g => g.Count));
    }

    private static PerimeterRequest Request(bool alongX, bool high, double noise)
    {
        var layers = new[] { ("BX", false, true), ("BY", false, false), ("TX", true, true), ("TY", true, false) }
            .Select(v => new BackgroundLayer { Key = v.Item1, Top = v.Item2, AlongX = v.Item3,
                TypeKey = "12", DiameterMm = 12, ModelDiameterMm = 12, SpacingMm = 200 }).ToList();
        double station = (alongX ? (high ? -16.05 : -5983.95) : (high ? 7983.95 : 16.05)) + noise;
        var row = new PlannedBar { SpecKey = alongX ? "BX" : "BY",
            Start = alongX ? new Point2(16.05, station) : new Point2(station, -5983.95),
            End = alongX ? new Point2(7983.95, station) : new Point2(station, -16.05),
            Placement = new BarPlacement { Component = "Background", TypeKey = "12", ModelDiameterMm = 12,
                ElevationMm = alongX ? -184 : -172 } };
        SetMeasuredCenterline(row);
        return new PerimeterRequest { Slabs = new() { new() { Shell = new() { new(0, -6000), new(8000, -6000), new(8000, 0), new(0, 0) } } },
            BackgroundLayers = layers, BackgroundPlan = new() { ReferenceBars = new() { row } },
            TypeKey = "12", DiameterMm = 12, ModelDiameterMm = 12, BendDiameterMm = 60,
            ThicknessMm = 200, TopCoverMm = 10, BottomCoverMm = 10, SideCoverMm = 10.05,
            Outer = true, UseUbars = true, LegLengthMm = 500 };
    }

    private static void SetMeasuredCenterline(PlannedBar row) => row.Centerline = new() { new() {
        Start = new Point3(row.Start.X, row.Start.Y, row.Placement.ElevationMm),
        End = new Point3(row.End.X, row.End.Y, row.Placement.ElevationMm) } };
}
