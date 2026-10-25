using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class ULegLengthCalculatorTests
{
    [Theory]
    [InlineData("B15", 500, 12, 60, 1400, 1440)]
    [InlineData("B20", 500, 12, 60, 1160, 1200)]
    [InlineData("B25", 500, 12, 60, 1000, 1040)]
    [InlineData("B30", 500, 12, 60, 910, 950)]
    [InlineData("B15", 400, 12, 60, 1090, 1130)]
    [InlineData("B20", 400, 12, 60, 910, 950)]
    [InlineData("B25", 400, 12, 60, 780, 820)]
    [InlineData("B30", 400, 12, 60, 710, 750)]
    [InlineData("B20", 500, 16, 80, 1550, 1600)]
    [InlineData("B25", 500, 16, 80, 1330, 1380)]
    [InlineData("B25", 500, 32, 256, 2660, 2810)]
    [InlineData("B25", 500, 36, 288, 3320, 3490)]
    [InlineData("B25", 500, 40, 320, 3690, 3870)]
    [InlineData("B30", 400, 6, 30, 360, 380)]
    public void FullTensionLapAndProjectionMatchCheckedSp63Examples(string concrete, double grade, double diameter,
        double bendInside, double lap, double projection)
    {
        var result = ULegLengthCalculator.Calculate(diameter, diameter, bendInside, concrete, grade);
        Assert.Equal(lap, result.LapMm);
        Assert.Equal(projection, result.LegProjectionMm);
        Assert.Equal((bendInside + diameter) / 2, result.BendCenterlineRadiusMm);
        Assert.True(result.LegProjectionMm - result.BendCenterlineRadiusMm >= result.LapMm);
        Assert.InRange(result.LegProjectionMm - result.BendCenterlineRadiusMm - result.LapMm, 0, 9.99999999);
        Assert.True(result.LapMm >= 20 * diameter && result.LapMm >= 250);
    }

    [Fact]
    public void A400UsesAmended340MpaAndNominalDiameterWhileBendUsesPhysicalDiameter()
    {
        var nominal = ULegLengthCalculator.Calculate(12, 12, 60, "B25", 400);
        var ribbedModel = ULegLengthCalculator.Calculate(12, 13.5, 60, "B25", 400);
        Assert.Equal(388.57142857142856, nominal.BaseAnchorageMm, 8);
        Assert.Equal(nominal.BaseAnchorageMm, ribbedModel.BaseAnchorageMm);
        Assert.Equal(nominal.LapMm, ribbedModel.LapMm);
        Assert.Equal(36.75, ribbedModel.BendCenterlineRadiusMm);
        Assert.True(ribbedModel.LegProjectionMm - ribbedModel.BendCenterlineRadiusMm >= ribbedModel.LapMm);
    }

    [Theory]
    [InlineData("b25")]
    [InlineData("В25")]
    [InlineData(" B25 ")]
    public void ConcreteNotationNormalizesWithoutChoosingAnotherClass(string concrete)
    {
        Assert.Equal(1000, ULegLengthCalculator.Calculate(12, 12, 60, concrete, 500).LapMm);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-12)]
    [InlineData(4)]
    [InlineData(33)]
    [InlineData(35)]
    [InlineData(38)]
    [InlineData(41)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void UnsupportedNominalDiameterIsRejectedWithoutInterpolation(double diameter)
    {
        Assert.ThrowsAny<ArgumentException>(() => ULegLengthCalculator.Calculate(diameter, 12, 60, "B25", 500));
    }

    [Theory]
    [InlineData(240, "А240")]
    [InlineData(600, "А400")]
    [InlineData(0, "А400")]
    [InlineData(double.NaN, "А400")]
    public void UnsupportedSteelHasExplicitFailureWithout500MmFallback(double grade, string message)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => ULegLengthCalculator.Calculate(12, 12, 60, "B25", grade));
        Assert.Contains(message, exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("B35")]
    [InlineData("B22.5")]
    public void UnsupportedConcreteIsRejected(string concrete)
    {
        Assert.ThrowsAny<ArgumentException>(() => ULegLengthCalculator.Calculate(12, 12, 60, concrete, 500));
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(12, 0)]
    [InlineData(-12, 60)]
    [InlineData(12, -60)]
    [InlineData(double.NaN, 60)]
    [InlineData(12, double.PositiveInfinity)]
    [InlineData(double.MaxValue, double.MaxValue)]
    public void InvalidPhysicalBendInputsAreRejected(double modelDiameter, double bendInside)
    {
        Assert.ThrowsAny<ArgumentException>(() => ULegLengthCalculator.Calculate(12, modelDiameter, bendInside, "B25", 500));
    }
}
