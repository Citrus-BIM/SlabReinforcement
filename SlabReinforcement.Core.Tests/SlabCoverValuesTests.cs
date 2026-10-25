using SlabReinforcement.Core;
using Xunit;

public class SlabCoverValuesTests
{
    [Fact]
    public void MissingLegacyFaceValuesUseThisHostsCovers()
    {
        var host = new SlabCoverValues(35, 40, 45);
        var values = SlabCoverValues.Resolve(null, null, 30, host, 200);
        Assert.Equal(35, values.TopMm);
        Assert.Equal(40, values.BottomMm);
        Assert.Equal(30, values.SideMm);
        Assert.Equal(45, host.SideMm);
    }

    [Fact]
    public void ExplicitCoversCanBeSmallerThanThePreviousHostsValues()
    {
        var host = new SlabCoverValues(40, 40, 40);
        var values = SlabCoverValues.Resolve(25, 30, 35, host, 200);
        Assert.Equal(25, values.TopMm);
        Assert.Equal(30, values.BottomMm);
        Assert.Equal(35, values.SideMm);
        Assert.False(values.Matches(host));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    public void InvalidValuesAreRejectedForEveryFace(double invalid)
    {
        Assert.Throws<ArgumentException>(() => new SlabCoverValues(invalid, 25, 25));
        Assert.Throws<ArgumentException>(() => new SlabCoverValues(25, invalid, 25));
        Assert.Throws<ArgumentException>(() => new SlabCoverValues(25, 25, invalid));
    }

    [Theory]
    [InlineData(100, 100, 200)]
    [InlineData(110, 100, 200)]
    [InlineData(25, 25, 0)]
    [InlineData(25, 25, double.NaN)]
    public void CoverMustLeaveRoomWithinTheSlab(double top, double bottom, double thickness)
    {
        Assert.Throws<ArgumentException>(() => SlabCoverValues.Resolve(top, bottom, 25,
            new SlabCoverValues(25, 25, 25), thickness));
    }

    [Fact]
    public void UnitConversionNoiseDoesNotCountAsAChange()
    {
        var values = new SlabCoverValues(35, 40, 45);
        Assert.True(values.Matches(new SlabCoverValues(35 + 0.0000001, 40, 45)));
        Assert.False(values.Matches(new SlabCoverValues(35.001, 40, 45)));
        Assert.False(values.Matches(new SlabCoverValues(35, 40.001, 45)));
        Assert.False(values.Matches(new SlabCoverValues(35, 40, 45.001)));
    }
}
