using SlabReinforcement.Core;
using Xunit;

public class PlacementTests
{
    [Theory]
    [InlineData(true, 200, 25, 12, 0, -31)]
    [InlineData(false, 200, 25, 12, 0, -169)]
    [InlineData(false, 200, 25, 12, 16, -153)]
    public void PlacesBarInsideThickness(bool top, double thickness, double cover, double diameter, double offset, double expected)
        => Assert.Equal(expected, BarElevation.Calculate(top, thickness, cover, 25, diameter, offset));

    [Fact]
    public void RejectsLayerOutsideHost()
        => Assert.Throws<ArgumentException>(() => BarElevation.Calculate(true, 80, 25, 25, 20, 20));

    [Theory]
    [InlineData(12, "B20", 500, 580)]
    [InlineData(16, "B25", 400, 540)]
    [InlineData(12.00000001, "B20", 500.00000001, 580)]
    public void PreservesExistingAnchorage(double diameter, string concrete, double steel, double expected)
        => Assert.Equal(expected, LegacyAnchorageTable.GetLength(diameter, concrete, steel));

    [Fact]
    public void RejectsUnsupportedDiameter()
        => Assert.Throws<ArgumentException>(() => LegacyAnchorageTable.GetLength(13, "B20", 500));
}
