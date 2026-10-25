using SlabReinforcement.Core;
using Xunit;

public class SeparateBackgroundLayerTests
{
    private static BackgroundRequest Request() => new()
    {
        Slabs = new() { new() { Shell = new() { new(0,0),new(4000,0),new(4000,3000),new(0,3000) } } },
        ThicknessMm=250, TopCoverMm=30, BottomCoverMm=40, SideCoverMm=35,
        Layers = new()
        {
            new() { Key="BX",TypeKey="12",Top=false,AlongX=true,DiameterMm=12,ModelDiameterMm=12,SpacingMm=200 },
            new() { Key="BY",TypeKey="16",Top=false,AlongX=false,DiameterMm=16,ModelDiameterMm=16,SpacingMm=200 },
            new() { Key="TX",TypeKey="20",Top=true,AlongX=true,DiameterMm=20,ModelDiameterMm=20,SpacingMm=150 },
            new() { Key="TY",TypeKey="10",Top=true,AlongX=false,DiameterMm=10,ModelDiameterMm=10,SpacingMm=150 }
        }
    };

    [Theory]
    [InlineData("BX",-204)]
    [InlineData("BY",-190)]
    [InlineData("TX",-40)]
    [InlineData("TY",-55)]
    public void CreatingEachRowSeparatelyPreservesTheFourLayerElevations(string key,double expectedZ)
    {
        var request=Request();
        var combined=BackgroundPlanner.Build(request);
        request.PlacementLayers=request.Layers.ToList();
        request.Layers=request.Layers.Where(layer=>layer.Key==key).ToList();
        var separate=BackgroundPlanner.Build(request);
        Assert.True(separate.CanCreate,string.Join(";",separate.Diagnostics.Select(d=>d.Message)));
        Assert.All(separate.Bars,bar=>Assert.Equal(expectedZ,bar.Placement.ElevationMm,6));
        Assert.Equal(combined.Bars.Where(bar=>bar.SpecKey==key).Select(bar=>(bar.Start,bar.End,bar.Placement.ElevationMm)),
            separate.Bars.Select(bar=>(bar.Start,bar.End,bar.Placement.ElevationMm)));
        Assert.DoesNotContain(separate.Bars,bar=>bar.SpecKey!=key);
    }

    [Fact]
    public void ExplicitRowDefinitionsMustIdentifyTheOuterXForY()
    {
        var request=Request();
        request.Layers=request.Layers.Where(layer=>layer.Key=="BY").ToList();
        request.PlacementLayers=request.Layers.ToList();
        var plan=BackgroundPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics,d=>d.IsBlocking&&d.Message.Contains("X"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-12)]
    [InlineData(double.NaN)]
    public void InvalidDisabledOuterDiameterCannotPlaceY(double diameter)
    {
        var request=Request();request.PlacementLayers=request.Layers.ToList();
        request.PlacementLayers[0].ModelDiameterMm=diameter;
        request.Layers=request.Layers.Where(layer=>layer.Key=="BY").ToList();
        Assert.False(BackgroundPlanner.Build(request).CanCreate);
    }
}
