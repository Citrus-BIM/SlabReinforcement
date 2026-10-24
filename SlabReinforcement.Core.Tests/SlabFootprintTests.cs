using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class SlabFootprintTests
{
    [Fact]
    public void Identical_shells_and_openings_form_a_normal_extrusion()
    {
        var top = Slab(0, 0, 3000, 2000);
        top.Holes.Add(Rect(1000, 500, 1500, 1200));
        var bottom = Slab(0, 0, 3000, 2000);
        bottom.Holes.Add(Rect(1000, 500, 1500, 1200));
        bottom.Shell.Reverse();

        var result = SlabFootprint.MatchNormalExtrusion(new[] { top }, new[] { bottom });

        Assert.Single(result);
        Assert.Single(result[0].Holes);
        Assert.Equal(3000, result[0].Shell.Max(p => p.X));
    }

    [Fact]
    public void Sloped_slab_with_vertical_sides_is_rejected()
    {
        var width = 1000 / Math.Cos(Math.PI / 6);
        var bottomShift = -200 * Math.Tan(Math.PI / 6);
        var top = Slab(0, 0, width, 1000);
        var bottom = Slab(bottomShift, 0, width + bottomShift, 1000);

        Assert.Throws<ArgumentException>(() =>
            SlabFootprint.MatchNormalExtrusion(new[] { top }, new[] { bottom }));
    }

    [Fact]
    public void Small_outline_difference_returns_only_the_shared_footprint()
    {
        var top = Slab(0, 0, 3000, 2000);
        var bottom = Slab(0.04, 0, 3000.04, 2000);

        var result = SlabFootprint.MatchNormalExtrusion(new[] { top }, new[] { bottom });

        Assert.Single(result);
        Assert.Equal(0.04, result[0].Shell.Min(p => p.X), 8);
        Assert.Equal(3000, result[0].Shell.Max(p => p.X), 8);
    }

    [Fact]
    public void Different_opening_even_with_identical_outer_boundary_is_rejected()
    {
        var top = Slab(0, 0, 3000, 2000);
        top.Holes.Add(Rect(1000, 500, 1500, 1200));
        var bottom = Slab(0, 0, 3000, 2000);
        bottom.Holes.Add(Rect(1010, 500, 1510, 1200));

        Assert.Throws<ArgumentException>(() =>
            SlabFootprint.MatchNormalExtrusion(new[] { top }, new[] { bottom }));
    }

    [Fact]
    public void Missing_bottom_opening_is_rejected()
    {
        var top = Slab(0, 0, 3000, 2000);
        top.Holes.Add(Rect(1000, 500, 1500, 1200));

        Assert.Throws<ArgumentException>(() =>
            SlabFootprint.MatchNormalExtrusion(new[] { top }, new[] { Slab(0, 0, 3000, 2000) }));
    }

    [Fact]
    public void Split_coplanar_faces_are_compared_as_one_union()
    {
        var top = new[] { Slab(0, 0, 1500, 2000), Slab(1500, 0, 3000, 2000) };
        var result = SlabFootprint.MatchNormalExtrusion(top, new[] { Slab(0, 0, 3000, 2000) });

        Assert.Single(result);
        Assert.Equal(3000, result[0].Shell.Max(p => p.X));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_tolerance_is_rejected(double tolerance)
    {
        var slab = new[] { Slab(0, 0, 3000, 2000) };
        Assert.Throws<ArgumentException>(() => SlabFootprint.MatchNormalExtrusion(slab, slab, tolerance));
    }

    private static PolygonInput Slab(double x0, double y0, double x1, double y1) => new() { Shell = Rect(x0, y0, x1, y1) };
    private static List<Point2> Rect(double x0, double y0, double x1, double y1) => new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };
}
