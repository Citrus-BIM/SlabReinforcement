using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class BackgroundPerimeterBoundaryPrecisionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundaryRowsKeepBothPhysicalEndpointsOnTheActualSteppedFootprint(bool swapAxes)
    {
        // Exact 14-corner shell from artifacts/single-slab/footprint.json. The inset
        // corner at Y=42262.248999999996 must not move across its horizontal edge.
        var shell = new List<Point2>
        {
            new(5256.599999999999, 18078.3), new(5256.599999999999, 23478.3),
            new(6416.599999999999, 23478.3), new(6416.599999999999, 42303.299),
            new(-1423.4, 42303.299), new(-1423.4, 55783.298),
            new(-30673.399, 55783.298), new(-30663.398999999998, 55633.297),
            new(-30673.399, 55445.797), new(-30673.399, 31503.3),
            new(-29473, 31503.3), new(-29473, 23703.299000000003),
            new(-35303.398, 23703.299000000003), new(-35303.398, 18078.3)
        };
        if (swapAxes) shell = shell.Select(p => new Point2(p.Y, p.X)).ToList();
        var slabs = new List<PolygonInput> { new() { Shell = shell } };
        var layers = new[] { ("BX", false, true), ("BY", false, false), ("TX", true, true), ("TY", true, false) }
            .Select(v => new BackgroundLayer
            {
                Key = v.Item1, Top = v.Item2, AlongX = v.Item3, TypeKey = "12",
                DiameterMm = 12, ModelDiameterMm = 12, SpacingMm = v.Item1 == "TX" ? 150 : 200
            }).ToList();
        var background = BackgroundPlanner.Build(new BackgroundRequest
        {
            Slabs = slabs, Layers = layers, PlacementLayers = layers,
            ThicknessMm = 200, TopCoverMm = 35, BottomCoverMm = 35, SideCoverMm = 35.05
        });
        Assert.True(background.CanCreate, Messages(background));
        var perimeter = PerimeterPlanner.Build(new PerimeterRequest
        {
            Slabs = slabs, BackgroundPlan = background, BackgroundLayers = layers,
            TypeKey = "12", DiameterMm = 12, ModelDiameterMm = 12, BendDiameterMm = 48,
            ThicknessMm = 200, TopCoverMm = 35, BottomCoverMm = 35, SideCoverMm = 35.05,
            Outer = true, UseUbars = true, LegLengthMm = 500
        });
        Assert.True(perimeter.CanCreate, Messages(perimeter));
        var lower = background.Bars.Where(b => b.SpecKey.StartsWith("B", StringComparison.Ordinal)).ToList();
        Assert.Equal(lower.Count * 2, perimeter.Bars.Count);
        foreach (var row in lower)
            foreach (var end in new[] { row.Start, row.End })
            {
                var matches = perimeter.Bars.Where(u =>
                    Math.Abs(u.Centerline[2].Start.X - end.X) <= 1e-6 &&
                    Math.Abs(u.Centerline[2].Start.Y - end.Y) <= 1e-6 &&
                    Math.Abs(u.Centerline[0].Start.Z - row.Placement.ElevationMm) <= 1e-6).ToList();
                Assert.True(matches.Count == 1,
                    $"Expected one U web at {row.SpecKey} endpoint ({end.X:R},{end.Y:R}), Z={row.Placement.ElevationMm:R}; found {matches.Count}.");
            }
        Assert.Empty(BarContainmentValidator.Validate(perimeter.Bars, slabs, 200, 35, 35, 35.05));
    }

    private static string Messages(ReinforcementPlan plan) => string.Join("; ", plan.Diagnostics.Select(d => d.Code + ":" + d.Message));
}
