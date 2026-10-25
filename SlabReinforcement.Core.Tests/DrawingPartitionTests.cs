using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class DrawingPartitionTests
{
    [Theory]
    [InlineData(false, true, "низ X")]
    [InlineData(false, false, "низ Y")]
    [InlineData(true, true, "верх X")]
    [InlineData(true, false, "верх Y")]
    public void LayerRolesMatchOfficialNativeContainsFilters(bool top, bool alongX, string layer)
    {
        string background = DrawingPartition.ForBackground(top, alongX);
        string zone = DrawingPartition.ForZone(top, alongX);
        Assert.Equal(layer + " фон", background);
        Assert.Equal(layer + " доп", zone);
        Assert.DoesNotContain("доп", background);
        Assert.DoesNotContain("фон", zone);
        Assert.Contains(layer, background);
        Assert.Contains(layer, zone);
    }

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(-400, 0, true)]
    [InlineData(0, 1, false)]
    [InlineData(0, -400, false)]
    public void OrthogonalUBarsRemainVisibleOnBothFacesOfTheirDirection(double x, double y, bool alongX)
    {
        string partition = DrawingPartition.ForPerimeter(new Point3(x, y, 0));
        string axis = alongX ? "X" : "Y";
        string other = alongX ? "Y" : "X";
        Assert.Equal("низ " + axis + " верх " + axis + " П-обрамление", partition);
        Assert.True(PassesOfficialPhysicalPlanFilters(partition, "низ " + axis));
        Assert.True(PassesOfficialPhysicalPlanFilters(partition, "верх " + axis));
        Assert.False(PassesOfficialPhysicalPlanFilters(partition, "низ " + other));
        Assert.False(PassesOfficialPhysicalPlanFilters(partition, "верх " + other));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(-3, 4)]
    [InlineData(1000, 0.01)]
    public void ObliqueUBarsRemainVisibleOnAllFourDirectionalPlans(double x, double y)
    {
        string partition = DrawingPartition.ForPerimeter(new Point3(x, y, 0));
        Assert.Equal("низ X верх X низ Y верх Y П-обрамление", partition);
        foreach (string layer in new[] { "низ X", "низ Y", "верх X", "верх Y" })
            Assert.True(PassesOfficialPhysicalPlanFilters(partition, layer));
    }

    [Fact]
    public void AxisClassificationUsesRelativeToleranceAndIgnoresRoundoff()
    {
        Assert.Equal(DrawingPartition.ForPerimeter(new Point3(1, 0, 0)),
            DrawingPartition.ForPerimeter(new Point3(400, 1e-9, 0)));
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(0, 0, 0)]
    [InlineData(double.NaN, 1, 0)]
    [InlineData(double.PositiveInfinity, 1, 0)]
    public void InvalidLegDirectionDoesNotInventADrawingClassification(double x, double y, double z)
    {
        Assert.Throws<ArgumentException>(() => DrawingPartition.ForPerimeter(new Point3(x, y, z)));
    }

    [Theory]
    [InlineData("Background|Низ X", "низ X фон")]
    [InlineData("Background|Верх Y", "верх Y фон")]
    public void BackgroundUsesItsOwnLayerRatherThanSelectedZoneLayer(string key, string expected)
    {
        var group = new BarGroup { SpecKey = key, Placement = new() { Component = "Background" } };
        Assert.Equal(expected, DrawingPartition.ForGroup(group, "Верх X"));
    }

    [Fact]
    public void ZoneUsesSelectedLayer()
    {
        var group = new BarGroup { SpecKey = "color", Placement = new() { Component = "Zones" } };
        Assert.Equal("низ Y доп", DrawingPartition.ForGroup(group, "Низ Y"));
    }

    [Fact]
    public void PerimeterUsesHorizontalLegInsteadOfDistributionOrVerticalWeb()
    {
        var group = new BarGroup
        {
            Placement = new() { Component = "PerimeterU", Distribution = new(0, 1, 0) },
            Centerline = new()
            {
                new() { Start = new(0, 0, -160), End = new(0, 0, -40) },
                new() { Start = new(0, 0, -40), End = new(500, 0, -40) }
            }
        };
        Assert.Equal("низ X верх X П-обрамление", DrawingPartition.ForGroup(group, "Верх Y"));
    }

    [Fact]
    public void UnknownComponentIsNotAssignedInventedMetadata()
    {
        Assert.Null(DrawingPartition.ForGroup(new BarGroup { Placement = new() { Component = "Other" } }, "Низ X"));
    }

    // These are the two relevant official native rules: the opposite layer is
    // hidden and Rebar with "доп" is hidden while its Path representation remains.
    // Host-mark and p.m. filters are intentionally outside this pure partition test.
    private static bool PassesOfficialPhysicalPlanFilters(string partition, string layer) =>
        partition.Contains(layer, StringComparison.OrdinalIgnoreCase)
        && !partition.Contains("доп", StringComparison.OrdinalIgnoreCase);
}
