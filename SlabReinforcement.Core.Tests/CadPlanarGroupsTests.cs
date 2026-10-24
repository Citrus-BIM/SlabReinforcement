using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class CadPlanarGroupsTests
{
    [Fact]
    public void SeparatesRealExportLevelsAndPreservesColorsAndSourceIds()
    {
        var result = CadPlanarGroups.Group(new[]
        {
            Face("middle-red", "layer_elements", 549.45, "red"),
            Face("lower", "layer_elements", -0.55),
            Face("upper", "layer_elements", 1149.45),
            Face("middle-blue", "layer_elements", 549.4497771, "blue")
        });
        Assert.Equal(3, result.Groups.Count);
        Assert.Equal(new[] { -0.55, 549.4497771, 1149.45 }, result.Groups.Select(g => g.ElevationMm));
        Assert.Equal(new[] { "blue", "red" }, result.Groups[1].Faces.Select(f => f.ColorKey).OrderBy(s => s));
        Assert.Contains(result.Groups[1].Faces, f => f.Id == "middle-red");
        Assert.Equal(0, result.NonPlanarFaceCount);
    }

    [Fact]
    public void CoincidentCalculationAndBackgroundLayersRemainSeparate()
    {
        var result = CadPlanarGroups.Group(new[] { Face("a", "KLEENKA", -1075), Face("b", "PLAST", -1075) });
        Assert.Equal(new[] { "KLEENKA", "PLAST" }, result.Groups.Select(g => g.Layer));
        Assert.Equal(2, result.Groups.Select(g => g.Key).Distinct().Count());
        Assert.All(result.Groups, group => Assert.Single(group.Faces));
    }

    [Fact]
    public void NonPlanarLegendAndVerticalFacesDoNotRejectValidPlanarMap()
    {
        var vertical = Face("vertical", "layer_elements", -186);
        vertical.Vertices[1] = new Point3(0, 0, -162);
        vertical.Vertices[2] = new Point3(0, 100, -162);
        var tilted = Face("tilted", "layer_elements", 14.9);
        tilted.Vertices[1] = new Point3(100, 0, 24.13);
        var degenerate = Face("line", "layer_elements", 18);
        degenerate.Vertices = new() { new(0, 0, 18), new(100, 0, 18), new(200, 0, 18) };
        var result = CadPlanarGroups.Group(new[] { Face("map", "layer_elements", 18), vertical, tilted, degenerate });
        Assert.Single(result.Groups);
        Assert.Equal("map", Assert.Single(result.Groups[0].Faces).Id);
        Assert.Equal(2, result.NonPlanarFaceCount);
        Assert.Equal(1, result.DegenerateFaceCount);
    }

    [Fact]
    public void ToleranceDoesNotChainAcrossDistinctPlanesAndOrderingIsStable()
    {
        var input = new[] { Face("c", "L", 1.6), Face("b", "L", .8), Face("a", "L", 0) };
        var result = CadPlanarGroups.Group(input, 1);
        var reversed = CadPlanarGroups.Group(input.AsEnumerable().Reverse(), 1);
        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(new[] { "a", "b" }, result.Groups[0].Faces.Select(f => f.Id));
        Assert.Equal(1.6, result.Groups[1].ElevationMm);
        Assert.Equal(result.Groups.Select(g => g.Key), reversed.Groups.Select(g => g.Key));
    }

    [Fact]
    public void EntireGroupVertexRangeStaysWithinTolerance()
    {
        var a = Face("a", "L", 0);
        a.Vertices[1] = new(100, 0, .9);
        var b = Face("b", "L", .5);
        b.Vertices[1] = new(100, 0, 1.4);
        Assert.Equal(2, CadPlanarGroups.Group(new[] { a, b }, 1).Groups.Count);
    }

    [Fact]
    public void DistinctGroupsWithEqualMeanElevationHaveUniqueStableKeys()
    {
        var first = Face("a", "calc", 0);
        first.Vertices = new() { new(0, 0, 0), new(100, 0, 1), new(0, 100, 1) };
        var second = Face("b", "calc", 0);
        second.Vertices = new() { new(0, 0, .125), new(100, 0, .75), new(0, 100, 1.125) };
        var groups = CadPlanarGroups.Group(new[] { first, second }).Groups;
        Assert.Equal(2, groups.Count);
        Assert.Equal(groups[0].ElevationMm, groups[1].ElevationMm);
        Assert.Equal(2, groups.Select(g => g.Key).Distinct().Count());
        Assert.Equal(groups.Select(g => g.Key), CadPlanarGroups.Group(new[] { second, first }).Groups.Select(g => g.Key));
    }

    [Fact]
    public void RotatedMirroredPlacementIsAlreadyResolvedBeforeGrouping()
    {
        var frame = SlabFrame.FromEdge(new(2000, 1000, 4000), new(2300, 1400, 4000), new(0, 0, 1));
        var original = Face("reflection", "KLEENKA", -100);
        var world = original.Vertices.Select(p => frame.ToWorld(new(-p.X, p.Y, p.Z))).ToList();
        original.Vertices = world.Select(frame.ToLocal).ToList();
        var result = CadPlanarGroups.Group(new[] { original });
        Assert.Single(result.Groups);
        Assert.Equal(-100, result.Groups[0].ElevationMm, 8);
        Assert.Contains(result.Groups[0].Faces[0].Vertices, p => Math.Abs(p.X + 100) < 1e-8);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void RejectsInvalidTolerance(double tolerance) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CadPlanarGroups.Group(Array.Empty<CadFace>(), tolerance));

    private static CadFace Face(string id, string layer, double z, string color = "red") => new()
    {
        Id = id, Layer = layer, ColorKey = color,
        Vertices = new() { new(0, 0, z), new(100, 0, z), new(0, 100, z) }
    };
}
