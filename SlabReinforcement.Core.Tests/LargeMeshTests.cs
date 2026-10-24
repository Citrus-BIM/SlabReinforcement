using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class LargeMeshTests
{
    [Fact]
    public void MoreThanTenThousandAdjacentTrianglesFormTheSamePlanAsTheirEnvelope()
    {
        var request = Request();
        const int columns = 100, rows = 60;
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < columns; x++)
            {
                var left = 300 + x * 10;
                var bottom = 300 + y * 10;
                Add(left, bottom, left + 10, bottom, left + 10, bottom + 10);
                Add(left, bottom, left + 10, bottom + 10, left, bottom + 10);
            }
        var plan = ReinforcementPlanner.Build(request);
        Assert.True(plan.CanCreate, string.Join("; ", plan.Diagnostics.Select(d => d.Code)));
        request.Faces = new() { new() { Id = "envelope", ColorKey = "red", Vertices = Rect(300, 300, 1300, 900) } };
        var reference = ReinforcementPlanner.Build(request);
        Assert.Equal(reference.Bars.Select(b => (b.Start.X, b.Start.Y, b.End.X, b.End.Y)),
            plan.Bars.Select(b => (b.Start.X, b.Start.Y, b.End.X, b.End.Y)));

        void Add(double ax, double ay, double bx, double by, double cx, double cy) =>
            request.Faces.Add(new() { Id = request.Faces.Count.ToString(), ColorKey = "red", Vertices = new() { new(ax, ay), new(bx, by), new(cx, cy) } });
    }

    [Fact]
    public void MoreThanTwoHundredThousandSelectedFacesIsRejectedBeforeGeometryWork()
    {
        var request = Request();
        request.Faces = Enumerable.Repeat(new FaceInput { ColorKey = "red", Vertices = Rect(300, 300, 1300, 900) }, 200001).ToList();
        var plan = ReinforcementPlanner.Build(request);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "PLAN_TOO_LARGE");
        Assert.Empty(plan.Bars);
    }

    [Fact]
    public void MoreThanOneMillionInputVerticesIsRejectedBeforeGeometryWork()
    {
        var request = Request();
        request.Faces.Add(new() { ColorKey = "red", Vertices = Enumerable.Repeat(new Point2(1, 2), 1000001).ToList() });
        var plan = ReinforcementPlanner.Build(request);
        Assert.Contains(plan.Diagnostics, d => d.Code == "PLAN_TOO_LARGE");
        Assert.Empty(plan.Bars);
    }

    private static PlanRequest Request() => new()
    {
        Slabs = new() { new() { Shell = Rect(0, 0, 2000, 1500) } },
        Specifications = new() { new() { Key = "r", ColorKey = "red", MaterialKey = "500", DiameterMm = 12, SpacingMm = 200, AnchorageMm = 100 } }
    };
    private static List<Point2> Rect(double a, double b, double c, double d) => new() { new(a, b), new(c, b), new(c, d), new(a, d) };
}
