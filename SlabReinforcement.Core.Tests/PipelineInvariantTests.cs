using SlabReinforcement.Core;
using Xunit;

public class PipelineInvariantTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(17, false)]
    [InlineData(45, false)]
    [InlineData(89, true)]
    [InlineData(90, true)]
    public void RotatingTheModelPreservesTheActualPlan(double worldAngleDegrees, bool mirrorCad)
    {
        var expected = Calculate(0, mirrorCad);
        var actual = Calculate(worldAngleDegrees, mirrorCad);
        Assert.True(expected.CanCreate, string.Join(";", expected.Diagnostics.Select(d => d.Message)));
        Assert.True(actual.CanCreate, string.Join(";", actual.Diagnostics.Select(d => d.Message)));
        Assert.Equal(expected.Bars.Count, actual.Bars.Count);
        for (int i = 0; i < expected.Bars.Count; i++)
        {
            var a = expected.Bars[i]; var b = actual.Bars[i];
            Assert.InRange(Math.Abs(a.Start.X - b.Start.X), 0, 1e-5);
            Assert.InRange(Math.Abs(a.Start.Y - b.Start.Y), 0, 1e-5);
            Assert.InRange(Math.Abs(a.End.X - b.End.X), 0, 1e-5);
            Assert.InRange(Math.Abs(a.End.Y - b.End.Y), 0, 1e-5);
        }
    }

    private static ReinforcementPlan Calculate(double angleDegrees, bool mirror)
    {
        double a = angleDegrees * Math.PI / 180;
        var world = new AffinePlacement(new Point3(1000000, -2000000, 15000),
            new Point3(Math.Cos(a), Math.Sin(a), 0), new Point3(-Math.Sin(a), Math.Cos(a), 0), new Point3(0, 0, 1));
        var frame = SlabFrame.FromEdge(world.OfPoint(new Point3(0, 0, 0)), world.OfPoint(new Point3(4000, 0, 0)), new Point3(0, 0, 1));
        Point2 Recover(Point3 local)
        {
            var p = frame.ToLocal(world.OfPoint(local));
            return new Point2(p.X, p.Y);
        }
        List<Point2> Ring(double x1, double y1, double x2, double y2) => new[]
        {
            new Point3(x1,y1,0), new Point3(x2,y1,0), new Point3(x2,y2,0), new Point3(x1,y2,0)
        }.Select(Recover).ToList();
        double c = 17 * Math.PI / 180; // The CAD axes deliberately differ from the slab edge.
        var cad = new AffinePlacement(new Point3(950, 1300, -1200),
            new Point3((mirror ? -1 : 1) * Math.Cos(c), (mirror ? -1 : 1) * Math.Sin(c), 0),
            new Point3(-Math.Sin(c), Math.Cos(c), 0), new Point3(0, 0, 1));
        var face = new[] { new Point3(-180,-450,0), new Point3(180,-450,0), new Point3(180,450,0), new Point3(-180,450,0) }
            .Select(cad.OfPoint).Select(Recover).ToList();
        return ReinforcementPlanner.Build(new PlanRequest
        {
            Slabs = new() { new() { Shell = Ring(0, 0, 4000, 3000), Holes = new() { Ring(1800, 1000, 2300, 2200) } } },
            Faces = new() { new() { Id = "mesh", ColorKey = "red", Vertices = face } },
            Specifications = new() { new() { Key = "d12", ColorKey = "red", MaterialKey = "500", DiameterMm = 12, SpacingMm = 200, AnchorageMm = 100 } },
            Options = new() { EdgeCoverMm = 25, MergeToleranceMm = 50, RoundIncrementMm = 10 }
        });
    }
}
