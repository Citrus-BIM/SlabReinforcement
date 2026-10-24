using SlabReinforcement.Dxf;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class DxfPlacementTests
{
    [Theory]
    [InlineData("ACI:1",255,0,0)]
    [InlineData("ACI:3",0,255,0)]
    [InlineData("ACI:5",0,0,255)]
    [InlineData("ACI:7",255,255,255)]
    [InlineData("ACI:12",165,0,0)]
    [InlineData("#123456",18,52,86)]
    public void Palette_preserves_source_rgb(string key, int r, int g, int b)
    {
        Assert.True(DxfPalette.TryRgb(key,out byte actualR,out byte actualG,out byte actualB));
        Assert.Equal(r,actualR);Assert.Equal(g,actualG);Assert.Equal(b,actualB);
    }
    [Theory]
    [InlineData("ACI:0")]
    [InlineData("ACI:256")]
    [InlineData("ACI:900")]
    public void Undefined_colors_are_not_coerced_to_black(string key)=>Assert.False(DxfPalette.TryRgb(key,out _,out _,out _));

    [Fact]
    public void Placement_checks_all_distinct_vertices_across_spatial_bucket_boundary()
    {
        var source=new[]{new Point3(-0.01,0,0),new Point3(200,0,0),new Point3(0,200,0),new Point3(200,0,0)};
        var placed=new[]{new Point3(0.01,0,0),new Point3(200,0.01,0),new Point3(0,200,0.01)};
        Assert.Equal(3,DxfVertexAlignment.Validate(source,placed,0.2));
    }
    [Fact]
    public void A_subset_of_the_placed_drawing_is_not_the_same_source()
    {
        var source=new[]{new Point3(0,0,0),new Point3(100,0,0),new Point3(0,100,0)};
        var placed=source.Concat(new[]{new Point3(200,200,0)});
        Assert.Throws<ArgumentException>(()=>DxfVertexAlignment.Validate(source,placed,0.2));
    }
    [Fact]
    public void Wrong_units_and_double_transform_are_rejected()
    {
        var raw=new[]{new Point3(1,2,0),new Point3(2,2,0),new Point3(1,3,0)};
        var transform=new AffinePlacement(new Point3(1000,2000,0),new Point3(0,1000,0),new Point3(-1000,0,0),new Point3(0,0,1000));
        var placed=raw.Select(transform.OfPoint).ToArray();
        Assert.Equal(3,DxfVertexAlignment.Validate(placed,placed,0.2));
        Assert.Throws<ArgumentException>(()=>DxfVertexAlignment.Validate(raw,placed,0.2));
        Assert.Throws<ArgumentException>(()=>DxfVertexAlignment.Validate(placed.Select(transform.OfPoint),placed,0.2));
    }
}
