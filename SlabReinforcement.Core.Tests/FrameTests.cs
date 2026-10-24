using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class FrameTests
{
    [Fact]
    public void Points_preserve_their_coordinate_values()
    {
        var point2 = new Point2(125.5, -24.75);
        var point3 = new Point3(125.5, -24.75, 3500);

        Assert.Equal(125.5, point2.X);
        Assert.Equal(-24.75, point2.Y);
        Assert.Equal(125.5, point3.X);
        Assert.Equal(-24.75, point3.Y);
        Assert.Equal(3500, point3.Z);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(45)]
    [InlineData(89)]
    [InlineData(90)]
    public void Edge_angle_defines_an_orthonormal_right_handed_frame(double degrees)
    {
        var angle = degrees * Math.PI / 180;
        var c = Math.Cos(angle);
        var s = Math.Sin(angle);
        var origin = new Point3(1200, -2300, 6400);
        var frame = SlabFrame.FromEdge(origin,
            new Point3(origin.X + 2000 * c, origin.Y + 2000 * s, origin.Z + 50),
            new Point3(0, 0, 5));

        Close(origin, frame.Origin);
        Close(new Point3(c, s, 0), frame.XAxis);
        Close(new Point3(-s, c, 0), frame.YAxis);
        Close(new Point3(0, 0, 1), frame.Normal);
        var local = new Point3(750, 320, 25);
        var world = new Point3(origin.X + 750 * c - 320 * s,
            origin.Y + 750 * s + 320 * c, origin.Z + 25);
        Close(world, frame.ToWorld(local));
        Close(local, frame.ToLocal(world));
        Close(world, frame.ToWorld(frame.ToLocal(world)));
    }

    [Theory]
    [InlineData(0, 17, false)]
    [InlineData(17, 45, false)]
    [InlineData(45, 89, true)]
    [InlineData(89, 90, false)]
    [InlineData(90, 0, true)]
    public void Cad_placement_is_independent_of_slab_frame(double cadDegrees, double slabDegrees, bool mirrored)
    {
        var cadAngle = cadDegrees * Math.PI / 180;
        var slabAngle = slabDegrees * Math.PI / 180;
        var sign = mirrored ? -1 : 1;
        var origin = new Point3(15000, -8000, 4200);
        var cad = new AffinePlacement(origin,
            new Point3(sign * Math.Cos(cadAngle), sign * Math.Sin(cadAngle), 0),
            new Point3(-Math.Sin(cadAngle), Math.Cos(cadAngle), 0), new Point3(0, 0, 1));
        var slab = SlabFrame.FromEdge(origin,
            new Point3(origin.X + 2000 * Math.Cos(slabAngle), origin.Y + 2000 * Math.Sin(slabAngle), origin.Z),
            new Point3(0, 0, 1));
        var local = slab.ToLocal(cad.OfPoint(new Point3(600, 275, 12)));
        var delta = cadAngle - slabAngle;

        Close(new Point3(sign * 600 * Math.Cos(delta) - 275 * Math.Sin(delta),
            sign * 600 * Math.Sin(delta) + 275 * Math.Cos(delta), 12), local);
    }

    [Fact]
    public void Elevated_sloped_frame_preserves_normal_distance()
    {
        var frame = SlabFrame.FromEdge(new Point3(125, -200, 6500),
            new Point3(1125, -194, 6508), new Point3(0, 3, 4));

        Close(new Point3(1, 0, 0), frame.XAxis);
        Close(new Point3(0, 0.8, -0.6), frame.YAxis);
        Close(new Point3(0, 0.6, 0.8), frame.Normal);
        Close(new Point3(325, 70, 6360), frame.ToWorld(new Point3(200, 300, 50)));
        Close(new Point3(200, 300, 50), frame.ToLocal(new Point3(325, 70, 6360)));
    }

    [Fact]
    public void Reversing_the_edge_reverses_both_in_plane_axes()
    {
        var start = new Point3(100, 200, 300);
        var end = new Point3(700, 1000, 300);
        var forward = SlabFrame.FromEdge(start, end, new Point3(0, 0, 1));
        var reverse = SlabFrame.FromEdge(end, start, new Point3(0, 0, 1));

        Close(new Point3(-0.6, -0.8, 0), reverse.XAxis);
        Close(new Point3(0.8, -0.6, 0), reverse.YAxis);
        Close(forward.Normal, reverse.Normal);
        Close(new Point3(1000, 0, 0), reverse.ToLocal(start));
        var world = new Point3(230, 370, 320);
        Close(world, reverse.ToWorld(reverse.ToLocal(world)));
    }

    [Fact]
    public void Large_world_offset_keeps_local_millimetre_dimensions()
    {
        var origin = new Point3(1e12, -1e12, 2e9);
        var frame = SlabFrame.FromEdge(origin,
            new Point3(origin.X + 600, origin.Y + 800, origin.Z), new Point3(0, 0, 1));
        var local = new Point3(1234.567, 345.678, 12.345);

        Close(local, frame.ToLocal(frame.ToWorld(local)), 0.0002);
    }

    [Fact]
    public void Metre_and_millimetre_inputs_agree_after_one_explicit_unit_conversion()
    {
        var metresToMillimetres = new AffinePlacement(new Point3(0, 0, 0),
            new Point3(1000, 0, 0), new Point3(0, 1000, 0), new Point3(0, 0, 1000));
        var placement = new AffinePlacement(new Point3(12000, -7000, 5000),
            new Point3(0, -1, 0), new Point3(-1, 0, 0), new Point3(0, 0, 1));
        var frame = SlabFrame.FromEdge(new Point3(11000, -8000, 5000),
            new Point3(11000, -7000, 5000), new Point3(0, 0, 1));

        var fromMetres = frame.ToLocal(placement.OfPoint(metresToMillimetres.OfPoint(new Point3(2, 3, 0.15))));
        var fromMillimetres = frame.ToLocal(placement.OfPoint(new Point3(2000, 3000, 150)));

        Close(new Point3(-1000, 2000, 150), fromMetres);
        Close(fromMillimetres, fromMetres);
    }

    [Fact]
    public void Affine_placement_preserves_scale_shear_and_reflection()
    {
        var placement = new AffinePlacement(new Point3(10, 20, 30),
            new Point3(-2, 0, 0), new Point3(0.5, 3, 0), new Point3(0, 0, 4));

        Close(new Point3(7.5, 41, 74), placement.OfPoint(new Point3(3, 7, 11)));
        Close(new Point3(-2.5, 21, 44), placement.OfVector(new Point3(3, 7, 11)));
    }

    public static IEnumerable<object[]> InvalidFrames()
    {
        var zero = new Point3(0, 0, 0);
        var edge = new Point3(1000, 0, 0);
        var normal = new Point3(0, 0, 1);
        yield return new object[] { zero, zero, normal };
        yield return new object[] { zero, new Point3(1e-12, 0, 0), normal };
        yield return new object[] { zero, edge, zero };
        yield return new object[] { zero, edge, new Point3(0, 0, 1e-15) };
        yield return new object[] { zero, new Point3(0, 0, 1000), normal };
        yield return new object[] { zero, new Point3(1e-12, 0, 1000), normal };
        yield return new object[] { new Point3(double.NaN, 0, 0), edge, normal };
        yield return new object[] { zero, new Point3(0, double.PositiveInfinity, 0), normal };
        yield return new object[] { zero, edge, new Point3(0, 0, double.NegativeInfinity) };
        yield return new object[] { new Point3(-double.MaxValue, 0, 0), new Point3(double.MaxValue, 0, 0), normal };
    }

    [Theory]
    [MemberData(nameof(InvalidFrames))]
    public void Invalid_or_near_degenerate_frames_are_rejected(Point3 start, Point3 end, Point3 normal)
    {
        Assert.Throws<ArgumentException>(() => SlabFrame.FromEdge(start, end, normal));
    }

    [Fact]
    public void Coordinate_conversions_reject_nonfinite_input()
    {
        var frame = SlabFrame.FromEdge(new Point3(0, 0, 0), new Point3(1000, 0, 0), new Point3(0, 0, 1));
        var placement = new AffinePlacement(new Point3(0, 0, 0),
            new Point3(1, 0, 0), new Point3(0, 1, 0), new Point3(0, 0, 1));
        var invalid = new Point3(double.NaN, 0, 0);

        Assert.Throws<ArgumentException>(() => frame.ToLocal(invalid));
        Assert.Throws<ArgumentException>(() => frame.ToWorld(invalid));
        Assert.Throws<ArgumentException>(() => placement.OfPoint(invalid));
        Assert.Throws<ArgumentException>(() => placement.OfVector(invalid));
        Assert.Throws<ArgumentException>(() => new AffinePlacement(invalid,
            new Point3(1, 0, 0), new Point3(0, 1, 0), new Point3(0, 0, 1)));
    }

    private static void Close(Point3 expected, Point3 actual, double tolerance = 1e-8)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Z - actual.Z), 0, tolerance);
    }
}
