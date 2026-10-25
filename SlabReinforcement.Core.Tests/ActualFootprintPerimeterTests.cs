using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class ActualFootprintPerimeterTests
{
    [Fact]
    public void ActualSteppedSlabAllowsPerimeterCrossingsWithoutWarnings()
    {
        var request = Request();
        var plan = PerimeterPlanner.Build(request);
        Assert.True(plan.CanCreate, string.Join(";", plan.Diagnostics.Select(d => d.Message)));
        Assert.Equal(plan.PerimeterUEdges.Sum(e=>e.StationsMm.Count),plan.Bars.Count);
        var strict = Assert.Single(CompositePlanValidator.Validate(plan.Bars));
        Assert.True(strict.IsBlocking);
        Assert.Equal("COMPOSITE_BAR_COLLISION",strict.Code);
        Assert.Empty(CompositePlanValidator.Validate(plan.Bars, allowBarIntersections: true));
        Assert.Empty(BarContainmentValidator.Validate(plan.Bars, request.Slabs, 200, 35, 35, 35));
    }

    [Fact]
    public void ActualBackgroundAndUAllowCrossingsWithoutWarnings()
    {
        var request = Request();
        var perimeter = PerimeterPlanner.Build(request);
        var background = BackgroundPlanner.Build(new BackgroundRequest
        {
            Slabs = request.Slabs, ThicknessMm = 200, TopCoverMm = 35, BottomCoverMm = 35, SideCoverMm = 47.05,
            OuterLayerOffsetMm = PerimeterPlanner.ReservedDepth(request),
            Layers = new[] { (false, true), (false, false), (true, true), (true, false) }.Select((p, i) => new BackgroundLayer
            { Key = "layer" + i, TypeKey = "12", Top = p.Item1, AlongX = p.Item2, DiameterMm = 12, ModelDiameterMm = 12, SpacingMm = 200 }).ToList()
        });
        Assert.True(background.CanCreate, string.Join(";", background.Diagnostics.Select(d => d.Message)));
        var bars = perimeter.Bars.Concat(background.Bars).ToList();
        Assert.True(perimeter.CanCreate,string.Join(";",perimeter.Diagnostics.Select(d=>d.Message)));
        Assert.Equal(perimeter.Bars.Count+background.Bars.Count,bars.Count);
        var strict = Assert.Single(CompositePlanValidator.Validate(bars));
        Assert.True(strict.IsBlocking);
        Assert.Equal("COMPOSITE_BAR_COLLISION",strict.Code);
        Assert.Empty(CompositePlanValidator.Validate(bars, allowBarIntersections: true));
        Assert.Empty(BarContainmentValidator.Validate(bars, request.Slabs, 200, 35, 35, 35));
    }

    internal static PerimeterRequest Request() => new()
    {
        // Exact Revit slab-local frame from the reviewed DXF footprint: source X/Y negated,
        // origin (5256.6,18078.3,-1075). Includes the real 10 mm tooth, without smoothing.
        Slabs = new() { new() { Shell = new()
        {
            new(-1160,-24224.999),new(6680,-24224.999),new(6680,-37704.998),new(35929.999,-37704.998),
            new(35919.999,-37554.997),new(35929.999,-37367.497),new(35929.999,-13425),new(34729.6,-13425),
            new(34729.6,-5624.999),new(40559.998,-5624.999),new(40559.998,0),new(0,0),new(0,-5400),new(-1160,-5400)
        } } },
        TypeKey = "12", DiameterMm = 12, ModelDiameterMm = 12, BendDiameterMm = 60,
        ThicknessMm = 200, TopCoverMm = 35, BottomCoverMm = 35, SideCoverMm = 35.05,
        SpacingMm = 200, LegLengthMm = 500, Outer = true, Openings = true, UseUbars = true
    };
}
