using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SlabReinforcement;
using SlabReinforcement.Core;
using SlabReinforcement.Integration;
using Xunit;

public class PerimeterAfterZonesTests
{
    [Theory]
    [InlineData("Низ X")]
    [InlineData("Низ Y")]
    [InlineData("Верх X")]
    [InlineData("Верх Y")]
    public void BackgroundAndMainPerimeterCanFollowLargerZoneEnds(string direction)
    {
        var zones = Calculate(ZoneParameters(direction, "b16"), faces: EndFaces(direction));
        Assert.True(zones.CanCreate, Messages(zones));
        Assert.Contains(zones.Bars, b => b.Placement.Component == "Zones");
        Assert.Contains(zones.Bars, b => b.Placement.Component == "PerimeterU");
        Assert.All(zones.Bars, b => Assert.Equal("b16", b.Placement.TypeKey));
        var existing = AsExisting(zones.Bars);
        string before = JsonSerializer.Serialize(existing);
        var parameters = BackgroundParameters();
        var expected = Calculate(parameters);
        Assert.True(expected.CanCreate, Messages(expected));

        var result = Calculate(parameters, existing);

        Assert.True(result.CanCreate, Messages(result));
        AssertNoPerimeterMismatch(result);
        Assert.Equal(expected.Bars.Count, result.Bars.Count);
        AssertCompleteGeometry(expected.Bars, result.Bars.Concat(result.ReferenceBars));
        Assert.All(result.Bars, b => Assert.Contains(b.Placement.Component, new[] { "Background", "PerimeterU" }));
        Assert.Equal(before, JsonSerializer.Serialize(existing));
        AssertNoNewDuplicates(existing, result.Bars);
        AssertGroupsExpandExactly(result);
        AssertRepeatCreatesNothing(parameters, zones.Bars.Concat(result.Bars), expected);
    }

    [Theory]
    [InlineData("Низ X")]
    [InlineData("Верх X")]
    public void MatchingLocalEndsAreReusedAndRemainingMainEndsAreCreated(string direction)
    {
        var zones = Calculate(ZoneParameters(direction, "b12"), faces: EndFaces(direction, narrow: true));
        Assert.True(zones.CanCreate, Messages(zones));
        var existing = AsExisting(zones.Bars);
        string before = JsonSerializer.Serialize(existing);
        var parameters = BackgroundParameters();
        var expected = Calculate(parameters);
        Assert.True(expected.CanCreate, Messages(expected));
        var expectedU = expected.Bars.Where(b => b.Placement.Component == "PerimeterU").ToList();
        var matches = ExistingReinforcementMatcher.Match(expectedU, existing);
        Assert.InRange(matches.MatchedCount, 1, expectedU.Count - 1);
        Assert.True(zones.Bars.Count(b => b.Placement.Component == "PerimeterU") > matches.MatchedCount);

        var result = Calculate(parameters, existing);

        Assert.True(result.CanCreate, Messages(result));
        AssertNoPerimeterMismatch(result);
        Assert.Equal(expected.Bars.Count - matches.MatchedCount, result.Bars.Count);
        Assert.Equal(matches.MatchedCount, result.ReferenceBars.Count);
        AssertCompleteGeometry(expected.Bars, result.Bars.Concat(result.ReferenceBars));
        Assert.Equal(before, JsonSerializer.Serialize(existing));
        AssertNoNewDuplicates(existing, result.Bars);
        AssertGroupsExpandExactly(result);
        AssertRepeatCreatesNothing(parameters, zones.Bars.Concat(result.Bars), expected);
    }

    [Fact]
    public void ExistingMiddleOfBackgroundAlignedSideLeavesCorrectGroupsOnBothSides()
    {
        var parameters = BackgroundParameters();
        var expected = Calculate(parameters);
        Assert.True(expected.CanCreate, Messages(expected));
        var side = expected.Bars.Where(b => b.Placement.Component == "PerimeterU"
            && b.Centerline.Average(s => s.Start.X) > 3000).OrderBy(b => b.Start.Y).ToList();
        Assert.True(side.Count > 5);
        var existing = AsExisting(side.Skip(side.Count / 2 - 1).Take(3));

        AssertPartialCompletion(parameters, expected, existing);
    }

    [Fact]
    public void TruncatedManualPerimeterPreservesItsActualRedistributedSteps()
    {
        var parameters = BackgroundParameters();
        foreach (var layer in parameters.BackgroundLayers) layer.Enabled = false;
        parameters.Perimeter.SpacingMm = 777;
        var expected = Calculate(parameters);
        Assert.True(expected.CanCreate, Messages(expected));
        Assert.True(expected.Groups.Where(g => g.Count > 1).Select(g => Math.Round(g.SpacingMm, 6)).Distinct().Count() > 1);
        Assert.Contains(expected.Groups, g => g.Count > 1 && Math.Abs(g.SpacingMm - 777) > 1);
        var firstSide = expected.Groups.First(g => g.Count > 3);
        var sideBars = Expand(new[] { firstSide });
        var existing = AsExisting(sideBars.Take(sideBars.Count - 2));

        AssertPartialCompletion(parameters, expected, existing);
    }

    [Fact]
    public void SameGeometryWithDifferentTypeStillBlocksAsPhysicalDuplicate()
    {
        var parameters = BackgroundParameters();
        var expected = Calculate(parameters);
        Assert.True(expected.CanCreate, Messages(expected));
        var existing = AsExisting(expected.Bars.Where(b => b.Placement.Component == "PerimeterU").Take(1));
        existing[0].Placement.TypeKey = "other12";

        var result = Calculate(parameters, existing);

        Assert.False(result.CanCreate);
        Assert.Contains(result.Diagnostics, d => d.IsBlocking && d.Code == "DUPLICATE_BAR");
        AssertNoPerimeterMismatch(result);
        Assert.DoesNotContain(result.ReferenceBars, b => b.Placement.Component == "PerimeterU");
    }

    [Fact]
    public void HybridPerimeterCompletesMissingPositionsOnBackgroundAndManualEdges()
    {
        var parameters = BackgroundParameters();
        foreach (var layer in parameters.BackgroundLayers) layer.Enabled = layer.Direction == "Низ X";
        var expected = Calculate(parameters);
        Assert.True(expected.CanCreate, Messages(expected));
        var aligned = expected.PerimeterUEdges.First(e => e.FollowsBackgroundRows && e.BarKeys.Count > 3);
        var manual = expected.PerimeterUEdges.First(e => !e.FollowsBackgroundRows && e.BarKeys.Count > 3);
        var selectedKeys = new[] { aligned, manual }.SelectMany(e => e.BarKeys.Skip(1).Take(2)).ToHashSet();
        var existing = AsExisting(expected.Bars.Where(b => selectedKeys.Contains(b.SpecKey)));
        Assert.Equal(4, existing.Count);

        AssertPartialCompletion(parameters, expected, existing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OuterAndOpeningArraysSurvivePartialReuseInEitherContourOrientation(bool reverse)
    {
        var host = new PolygonInput
        {
            Shell = new() { new(0, 0), new(8000, 0), new(8000, 6000), new(0, 6000) },
            Holes = new() { new() { new(3000, 2000), new(3000, 4000), new(5000, 4000), new(5000, 2000) } }
        };
        if (reverse) { host.Shell.Reverse(); host.Holes[0].Reverse(); }
        var slabs = new List<PolygonInput> { host };
        var parameters = BackgroundParameters();
        var expected = Calculate(parameters, slabs: slabs);
        Assert.True(expected.CanCreate, Messages(expected));
        Assert.True(expected.PerimeterUEdges.Count >= 8);
        // At least one physical array runs against its edge's station order.
        Assert.Contains(expected.Groups.Where(g => g.Placement.Component == "PerimeterU" && g.Count > 1), group =>
        {
            var edge = expected.PerimeterUEdges.Single(e => e.BarKeys.Contains(group.SpecKey));
            var direction = group.Placement.Distribution;
            return (edge.End.X - edge.Start.X) * direction.X + (edge.End.Y - edge.Start.Y) * direction.Y < -1;
        });
        var selectedKeys = expected.PerimeterUEdges.Where(e => e.BarKeys.Count > 2)
            .Select(e => e.BarKeys[e.BarKeys.Count / 2]).ToHashSet();
        var existing = AsExisting(expected.Bars.Where(b => selectedKeys.Contains(b.SpecKey)));
        Assert.True(existing.Count >= 8);

        AssertPartialCompletion(parameters, expected, existing, slabs);
    }

    [Fact]
    public void LegacyCombinedCalculationReusesExistingMainEndsForNewZonesWithoutDuplicateCreation()
    {
        var parameters = ZoneParameters("Низ X", "b12");
        parameters.CreationScope = ReinforcementCreationScope.All;
        parameters.Perimeter.Enabled = true;
        foreach (var layer in parameters.BackgroundLayers) layer.Enabled = true;
        parameters.Selections[0].SpacingMm = 275;
        var faces = EndFaces("Низ X", narrow: true);
        var expected = Calculate(parameters, faces: faces);
        Assert.True(expected.CanCreate, Messages(expected));
        var shared = Assert.Single(expected.Bars, b => b.Placement.Component == "PerimeterU"
            && b.PreviewPurpose == (PreviewPurpose.Background | PreviewPurpose.Zones));
        var unshared = expected.Bars.First(b => b.Placement.Component == "PerimeterU"
            && b.PreviewPurpose == PreviewPurpose.Background);
        var existing = AsExisting(new[] { shared, unshared });
        string before = JsonSerializer.Serialize(existing);

        var result = Calculate(parameters, existing, faces);

        Assert.True(result.CanCreate, Messages(result));
        AssertNoPerimeterMismatch(result);
        Assert.Equal(expected.Bars.Count - existing.Count, result.Bars.Count);
        Assert.Contains(result.Bars, b => b.Placement.Component == "Zones");
        Assert.Contains(result.Bars, b => b.Placement.Component == "PerimeterU" && b.PreviewPurpose == PreviewPurpose.Zones);
        Assert.Contains(result.ReferenceBars, b => b.Placement.Component == "PerimeterU" && (b.PreviewPurpose & PreviewPurpose.Background) != 0);
        Assert.Contains(result.ReferenceBars, b => b.Placement.Component == "PerimeterU" && (b.PreviewPurpose & PreviewPurpose.Zones) != 0);
        // The writer verifies references one-to-one against physical existing bars.
        // Sharing one U between two demands must not require a second physical U.
        Assert.True(ExistingReinforcementMatcher.Match(result.ReferenceBars, existing).IsComplete,
            "Every reference must resolve to a distinct existing physical bar, as required by the writer.");
        Assert.Equal(existing.Count, result.ReferenceBars.Count);
        var sharedReference = Assert.Single(result.ReferenceBars, b => b.SpecKey == shared.SpecKey);
        Assert.Equal(PreviewPurpose.Background | PreviewPurpose.Zones, sharedReference.PreviewPurpose);
        Assert.Single(result.PerimeterUEdges.Where(e => e.Id.StartsWith("AutoZoneU|"))
            .SelectMany(e => e.BarKeys), key => key == sharedReference.SpecKey);
        AssertEdgeReferencesResolve(result);
        foreach (var edge in expected.PerimeterUEdges.Where(e => !e.Id.StartsWith("AutoZoneU|")))
        {
            var actual = Assert.Single(result.PerimeterUEdges, e => e.Id == edge.Id);
            Assert.Equal(edge.Start, actual.Start);
            Assert.Equal(edge.End, actual.End);
            Assert.Equal(edge.StationRangeStartMm, actual.StationRangeStartMm);
            Assert.Equal(edge.StationRangeEndMm, actual.StationRangeEndMm);
            Assert.Equal(edge.StationsMm, actual.StationsMm);
            Assert.Equal(edge.BarKeys, actual.BarKeys);
        }
        AssertCompleteGeometry(expected.Bars, result.Bars.Concat(existing));
        AssertNoNewDuplicates(existing, result.Bars);
        AssertGroupsExpandExactly(result);
        Assert.Equal(before, JsonSerializer.Serialize(existing));
    }

    private static void AssertPartialCompletion(PreviewParameters parameters, ReinforcementPlan expected, List<PlannedBar> existing,
        List<PolygonInput> slabs = null)
    {
        string before = JsonSerializer.Serialize(existing);
        var result = Calculate(parameters, existing, slabs: slabs);
        Assert.True(result.CanCreate, Messages(result));
        AssertNoPerimeterMismatch(result);
        Assert.Equal(expected.Bars.Count - existing.Count, result.Bars.Count);
        Assert.Equal(existing.Count, result.ReferenceBars.Count);
        AssertEdgeReferencesResolve(result);
        AssertCompleteGeometry(expected.Bars, result.Bars.Concat(result.ReferenceBars));
        Assert.Equal(before, JsonSerializer.Serialize(existing));
        AssertNoNewDuplicates(existing, result.Bars);
        AssertGroupsExpandExactly(result);
        AssertRepeatCreatesNothing(parameters, existing.Concat(result.Bars), expected, slabs);
    }

    private static void AssertRepeatCreatesNothing(PreviewParameters parameters, IEnumerable<PlannedBar> physical, ReinforcementPlan expected,
        List<PolygonInput> slabs = null)
    {
        var repeat = Calculate(parameters, AsExisting(physical), slabs: slabs);
        AssertNoPerimeterMismatch(repeat);
        Assert.Empty(repeat.Bars);
        Assert.Empty(repeat.Groups);
        Assert.False(repeat.CanCreate);
        Assert.Contains(repeat.Diagnostics, d => d.IsBlocking && d.Code == "EMPTY_PLAN");
        Assert.DoesNotContain(repeat.Diagnostics, d => d.Code == "DUPLICATE_BAR");
        AssertEdgeReferencesResolve(repeat);
        AssertCompleteGeometry(expected.Bars, repeat.ReferenceBars);
    }

    private static void AssertEdgeReferencesResolve(ReinforcementPlan plan)
    {
        var physical = plan.Bars.Concat(plan.ReferenceBars).Where(b => b.Placement.Component == "PerimeterU").ToList();
        foreach (string key in plan.PerimeterUEdges.SelectMany(e => e.BarKeys))
            Assert.Single(physical, b => b.SpecKey == key);
    }

    private static void AssertNoPerimeterMismatch(ReinforcementPlan plan) => Assert.DoesNotContain(plan.Diagnostics,
        d => d.Code == "PERIMETER_EXISTING_MISMATCH" || d.Code == "PERIMETER_PARTIAL_MATCH");

    private static void AssertNoNewDuplicates(List<PlannedBar> existing, List<PlannedBar> added) => Assert.DoesNotContain(
        CompositePlanValidator.Validate(existing.Concat(added), existingBarCount: existing.Count, allowBarIntersections: true),
        d => d.Code == "DUPLICATE_BAR");

    private static void AssertCompleteGeometry(IEnumerable<PlannedBar> expected, IEnumerable<PlannedBar> actual)
    {
        var wanted = expected.ToList();
        var physical = AsExisting(actual);
        Assert.Equal(wanted.Count, physical.Count);
        Assert.True(ExistingReinforcementMatcher.Match(wanted, physical, 0.000001).IsComplete,
            "Expected every original physical bar exactly once, with its type and 3D centerline.");
    }

    private static void AssertGroupsExpandExactly(ReinforcementPlan plan) => AssertCompleteGeometry(plan.Bars, Expand(plan.Groups));

    private static List<PlannedBar> Expand(IEnumerable<BarGroup> groups)
    {
        var result = new List<PlannedBar>();
        foreach (var group in groups)
        for (int i = 0; i < group.Count; i++)
        {
            var d = group.Placement.Distribution;
            double step = group.SpacingMm * i;
            Point3 Shift(Point3 p) => new(p.X + d.X * step, p.Y + d.Y * step, p.Z + d.Z * step);
            result.Add(new PlannedBar
            {
                SpecKey = group.SpecKey,
                Start = new(group.Start.X + d.X * step, group.Start.Y + d.Y * step),
                End = new(group.End.X + d.X * step, group.End.Y + d.Y * step),
                Centerline = group.Centerline?.Select(s => new CenterlineSegment
                { Start = Shift(s.Start), End = Shift(s.End), Midpoint = s.Midpoint.HasValue ? Shift(s.Midpoint.Value) : null }).ToList(),
                Placement = new() { TypeKey = group.Placement.TypeKey, Component = group.Placement.Component,
                    ModelDiameterMm = group.Placement.ModelDiameterMm, ElevationMm = group.Placement.ElevationMm + d.Z * step,
                    Distribution = d }
            });
        }
        return result;
    }

    private static PreviewParameters BackgroundParameters() => new()
    {
        CreationScope = ReinforcementCreationScope.Background, IncludeZones = false, Direction = "Низ X", ConcreteClass = "B25",
        TopCoverMm = 35, BottomCoverMm = 35, EdgeCoverMm = 35, AutomaticULegLength = true,
        BackgroundLayers = new[] { "Низ X", "Низ Y", "Верх X", "Верх Y" }.Select(d => new PreviewBackgroundLayer
        { Enabled = true, Direction = d, SpecKey = "b12", SpacingMm = 250 }).ToList(),
        Perimeter = new() { Enabled = true, SpecKey = "b12", SpacingMm = 777 }
    };

    private static PreviewParameters ZoneParameters(string direction, string type)
    {
        var parameters = BackgroundParameters();
        parameters.CreationScope = ReinforcementCreationScope.Zones;
        parameters.IncludeZones = true;
        parameters.Direction = direction;
        parameters.SourceGroupKey = "zone-source";
        parameters.CalculationAxesConfirmed = true;
        parameters.RoundIncrementMm = 1;
        parameters.UsePerimeterUForZoneEnds = true;
        parameters.Perimeter.Enabled = false;
        foreach (var layer in parameters.BackgroundLayers) layer.Enabled = false;
        parameters.Selections = new() { new() { ColorKey = "red", SpecKey = type, SpacingMm = 300 } };
        return parameters;
    }

    private static List<FaceInput> EndFaces(string direction, bool narrow = false)
    {
        double first = narrow ? 1000 : 500, last = narrow ? 2000 : 2300;
        return new() { new() { Id = "zone", ColorKey = "red", Vertices = direction.EndsWith("X")
            ? new() { new(3800, first), new(3995, first), new(3995, last), new(3800, last) }
            : new() { new(first, 2800), new(last, 2800), new(last, 2995), new(first, 2995) } } };
    }

    private static ReinforcementPlan Calculate(PreviewParameters parameters, List<PlannedBar> existing = null, List<FaceInput> faces = null,
        List<PolygonInput> slabs = null)
    {
        var session = (ReinforcementSession)RuntimeHelpers.GetUninitializedObject(typeof(ReinforcementSession));
        Set("_slab", new SlabGeometrySnapshot { ThicknessMm = 200, TopCoverMm = 35, BottomCoverMm = 35, SideCoverMm = 35,
            Slabs = slabs ?? new() { new() { Shell = new() { new(0, 0), new(4000, 0), new(4000, 3000), new(0, 3000) } } } });
        var mesh = new ImportedMeshSnapshot();
        mesh.Groups.Add(new CadPlaneGroup { Key = "zone-source", Faces = faces ?? EndFaces("Низ X") });
        Set("_mesh", mesh);
        Set("_existingBars", existing ?? new());
        Set("_hasExistingReinforcement", existing?.Count > 0);
        Set("_types", new[] { ("b12", 12d), ("b16", 16d), ("other12", 12d) }.ToDictionary(v => v.Item1,
            v => new BarTypeSnapshot { Key = v.Item1, Name = v.Item1, DiameterMm = v.Item2,
                ModelDiameterMm = v.Item2, BendDiameterMm = v.Item2 * 4, SteelGrade = 500 }));
        return (ReinforcementPlan)typeof(ReinforcementSession).GetMethod("Calculate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, new object[] { parameters, CancellationToken.None })!;
        void Set(string name, object value) => typeof(ReinforcementSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, value);
    }

    // ExistingRebarReader retains physical geometry and type, not the original planner component/key.
    private static List<PlannedBar> AsExisting(IEnumerable<PlannedBar> source) => source.Select((b, i) => new PlannedBar
    {
        SpecKey = "Existing|physical|" + i, Start = b.Start, End = b.End,
        Centerline = (b.Centerline ?? new() { new() { Start = new(b.Start.X, b.Start.Y, b.Placement.ElevationMm),
            End = new(b.End.X, b.End.Y, b.Placement.ElevationMm) } }).Select(s => new CenterlineSegment
            { Start = s.Start, End = s.End, Midpoint = s.Midpoint }).ToList(),
        Placement = new() { TypeKey = b.Placement.TypeKey, Component = "Existing", ModelDiameterMm = b.Placement.ModelDiameterMm,
            ElevationMm = b.Placement.ElevationMm, Distribution = b.Placement.Distribution }
    }).ToList();

    private static string Messages(ReinforcementPlan plan) => string.Join("; ", plan.Diagnostics.Select(d => d.Code + ": " + d.Message));
}
