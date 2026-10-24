using NetTopologySuite.Geometries;
using SlabReinforcement.Core;

// Runs without Revit or Autodesk assemblies. All coordinates are explicitly millimetres.
var baselineRequest = Request(p => p);
var baseline = ReinforcementPlanner.Build(baselineRequest);
CheckPlan(baselineRequest, baseline);
Console.WriteLine($"PASS rectangle with hole: {baseline.Bars.Count} bars, {baseline.Groups.Count} groups");

RunPlacement(37, 0, false);
RunPlacement(37, 15, false);
RunPlacement(37, 15, true);

var impossible = Request(p => p);
impossible.Specifications[0].AnchorageMm = 150;
var blocked = ReinforcementPlanner.Build(impossible);
Expect(!blocked.CanCreate && blocked.Diagnostics.Any(d => d.Code == "ANCHORAGE_DOES_NOT_FIT" && d.IsBlocking),
    "Required anchorage at the slab boundary must block creation.");
Console.WriteLine("PASS insufficient anchorage: creation blocked with ANCHORAGE_DOES_NOT_FIT");
Console.WriteLine("Pure NTS geometry smoke passed.");

void RunPlacement(double angleDegrees, double slopeDegrees, bool mirror)
{
    var angle = angleDegrees * Math.PI / 180;
    var slope = slopeDegrees * Math.PI / 180;
    var c = Math.Cos(angle);
    var s = Math.Sin(angle);
    var ct = Math.Cos(slope);
    var st = Math.Sin(slope);
    var origin = new Point3(12000, -8000, 9500);
    var basisX = new Point3((mirror ? -1 : 1) * c, (mirror ? -1 : 1) * s, 0);
    var basisY = new Point3(-s * ct, c * ct, st);
    var normal = new Point3(s * st, -c * st, ct);
    var placement = new AffinePlacement(origin, basisX, basisY, normal);
    var frame = SlabFrame.FromEdge(placement.OfPoint(new Point3(0, 0, 0)), placement.OfPoint(new Point3(3000, 0, 0)), normal);

    Point2 IntoSlabFrame(Point2 point)
    {
        var local = frame.ToLocal(placement.OfPoint(new Point3(point.X, point.Y, 0)));
        Expect(Math.Abs(local.Z) < 1e-7, "Placed source face must remain on the slab plane.");
        return new Point2(local.X, local.Y);
    }
    var request = Request(IntoSlabFrame);
    var plan = ReinforcementPlanner.Build(request);
    CheckPlan(request, plan);

    Point2 BackToSource(Point2 point)
    {
        var world = frame.ToWorld(new Point3(point.X, point.Y, 0));
        var delta = new Point3(world.X - origin.X, world.Y - origin.Y, world.Z - origin.Z);
        return new Point2(Dot(delta, basisX), Dot(delta, basisY));
    }
    var expected = baseline.Bars.Select(b => Key(b.Start, b.End)).OrderBy(k => k).ToArray();
    var actual = plan.Bars.Select(b => Key(BackToSource(b.Start), BackToSource(b.End))).OrderBy(k => k).ToArray();
    Expect(expected.SequenceEqual(actual), "Placement must preserve the same source bars after conversion back from the slab frame.");
    Console.WriteLine($"PASS placement: rotation {angleDegrees}°, slope {slopeDegrees}°, mirror {mirror}; {plan.Bars.Count} bars match baseline");
}

static PlanRequest Request(Func<Point2, Point2> project)
{
    List<Point2> Rectangle(double x0, double y0, double x1, double y1) => new[]
    {
        new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1)
    }.Select(project).ToList();
    return new PlanRequest
    {
        Slabs = new List<PolygonInput>
        {
            new PolygonInput { Shell = Rectangle(0, 0, 3000, 2000), Holes = new List<List<Point2>> { Rectangle(1200, 600, 1800, 1400) } }
        },
        Faces = new List<FaceInput> { new FaceInput { Id = "mesh", ColorKey = "red", Vertices = Rectangle(0, 0, 3000, 2000) } },
        Specifications = new List<ReinforcementSpec>
        {
            new ReinforcementSpec { Key = "12/200", ColorKey = "red", MaterialKey = "A500", DiameterMm = 12, SpacingMm = 200 }
        },
        Options = new PlanOptions { RoundIncrementMm = 1 }
    };
}

static void CheckPlan(PlanRequest request, ReinforcementPlan plan)
{
    Expect(plan.CanCreate, string.Join("; ", plan.Diagnostics.Select(d => d.Code + ": " + d.Message)));
    var factory = new GeometryFactory();
    LinearRing Ring(List<Point2> points) => factory.CreateLinearRing(points.Concat(new[] { points[0] }).Select(p => new Coordinate(p.X, p.Y)).ToArray());
    var input = request.Slabs.Single();
    var host = factory.CreatePolygon(Ring(input.Shell), input.Holes.Select(Ring).ToArray());
    foreach (var bar in plan.Bars)
    {
        var line = factory.CreateLineString(new[] { new Coordinate(bar.Start.X, bar.Start.Y), new Coordinate(bar.End.X, bar.End.Y) });
        Expect(host.Covers(line), "A final bar penetrates a hole or leaves the slab.");
        Expect(host.Boundary.Distance(line) >= 31 - 1e-6, "A final bar violates cover plus half the diameter.");
    }
    var expanded = plan.Groups.SelectMany(g => Enumerable.Range(0, g.Count).Select(i => Key(
        new Point2(g.Start.X, g.Start.Y + i * g.SpacingMm), new Point2(g.End.X, g.End.Y + i * g.SpacingMm)))).OrderBy(k => k);
    Expect(expanded.SequenceEqual(plan.Bars.Select(b => Key(b.Start, b.End)).OrderBy(k => k)), "Group expansion and preview bars differ.");
    Expect(plan.Groups.All(g => g.Count > 0 && g.Count <= 1002), "A group violates the Revit array count limit.");
}

static string Key(Point2 start, Point2 end) => $"{Math.Round(start.X, 5)},{Math.Round(start.Y, 5)}:{Math.Round(end.X, 5)},{Math.Round(end.Y, 5)}";
static double Dot(Point3 a, Point3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
static void Expect(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
