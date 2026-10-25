using System.Reflection;
using SlabReinforcement.Core;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class AutomaticZoneEndPlannerTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    public void LocalUUsesExactZoneRowFaceAndPhysicalCover(bool top, bool alongX, bool atStart)
    {
        var request = Request(top, alongX); var planner = new Probe(request);
        Assert.True(planner.Connect(41, 1959, 807, atStart, out var connection), planner.Reason);
        Assert.Equal(0, connection.StationOffsetMm);
        var plan = planner.Complete(Bar(request, 41, 1959, 807, connection));
        var u = Assert.Single(plan.Bars); Assert.Single(plan.Groups); Assert.Single(plan.PerimeterUEdges);
        Assert.Equal(PreviewPurpose.Zones,u.PreviewPurpose);
        Assert.Equal(PreviewPurpose.Zones,plan.Groups[0].PreviewPurpose);
        Assert.Equal(1, plan.Groups[0].Count);
        Assert.Equal(807, alongX ? u.Centerline[2].Start.Y : u.Centerline[2].Start.X, 6);
        Assert.Equal(atStart ? 41 : 1959, alongX ? u.Centerline[2].Start.X : u.Centerline[2].Start.Y, 6);
        Assert.Equal(request.Specifications[0].ElevationMm, top ? u.Centerline[4].End.Z : u.Centerline[0].Start.Z);
        Assert.Empty(BarContainmentValidator.Validate(plan.Bars, request.Slabs, 200, 35, 35, 35));
    }

    [Fact]
    public void TrialsNeverCreateOrphansAndFinalRowsFormExactArray()
    {
        var request = Request(); var planner = new Probe(request);
        Assert.True(planner.Connect(41, 1959, 300, true, out _));
        var final = new List<PlannedBar>();
        foreach (double row in new[] { 700d, 900, 1100 })
        {
            Assert.True(planner.Connect(41, 1959, row, true, out var connection));
            final.Add(Bar(request, 41, 1959, row, connection));
        }
        Assert.Empty(planner.Complete().Bars);
        var plan = planner.Complete(final.ToArray());
        Assert.Equal(3, plan.Bars.Count);
        var group = Assert.Single(plan.Groups); Assert.Equal(3, group.Count); Assert.Equal(200, group.SpacingMm);
        Assert.Equal(BarLayoutRule.NumberWithSpacing, group.LayoutRule);
        Assert.DoesNotContain(plan.Bars, b => Math.Abs(b.Centerline[2].Start.Y - 300) < 1e-6);
        Assert.Equal(3, Assert.Single(plan.PerimeterUEdges).StationsMm.Count);
    }

    [Fact]
    public void LocalUEndUsesOpeningBoundaryAndDoesNotCrossHole()
    {
        var request = Request(); request.Slabs[0].Holes.Add(new() { new(1000, 600), new(1400, 600), new(1400, 1400), new(1000, 1400) });
        var planner = new Probe(request);
        Assert.True(planner.Connect(41, 959, 1000, false, out var connection), planner.Reason);
        var plan = planner.Complete(Bar(request, 41, 959, 1000, connection));
        var u = Assert.Single(plan.Bars); Assert.Equal(959, u.Centerline[2].Start.X, 6);
        Assert.True(u.Centerline[0].Start.X < u.Centerline[2].Start.X);
        Assert.Empty(BarContainmentValidator.Validate(plan.Bars, request.Slabs, 200, 35, 35, 35));
    }

    [Theory]
    [InlineData("diameter")]
    [InlineData("bend")]
    [InlineData("leg")]
    [InlineData("interior")]
    public void ImpossibleCandidateFailsWithoutAnAutomaticBar(string failure)
    {
        var request = Request();
        if (failure == "diameter") request.AutomaticZoneEnds.ModelDiameterMm = 20;
        if (failure == "bend") request.AutomaticZoneEnds.BendDiameterMm = 180;
        if (failure == "leg") request.AutomaticZoneEnds.LegLengthMm = 2100;
        var planner = new Probe(request);
        Assert.False(planner.Connect(failure == "interior" ? 400 : 41, 1959, 1000, true, out _));
        Assert.False(string.IsNullOrWhiteSpace(planner.Reason));
        Assert.Empty(planner.Complete().Bars);
    }

    [Fact]
    public void RequiredEdgeIsStableAcrossRowsButCannotSwitchSides()
    {
        var planner = new Probe(Request());
        Assert.True(planner.Connect(41, 1959, 700, true, out var first));
        Assert.True(planner.Connect(41, 1959, 900, true, out var next, first.EdgeId));
        Assert.Equal(first.EdgeId, next.EdgeId);
        Assert.False(planner.Connect(41, 1959, 900, false, out _, first.EdgeId));
    }

    [Fact]
    public void ObliqueBoundaryUsesNormalCoverAndKeepsTheExactZoneRow()
    {
        var request = Request();
        request.Slabs[0].Shell = new() { new(0, 0), new(2000, 0), new(2000, 2000), new(1000, 2000) };
        request.AutomaticZoneEnds.DiameterMm = request.AutomaticZoneEnds.ModelDiameterMm = 10;
        var planner = new Probe(request); var final = new List<PlannedBar>();
        foreach (double row in new[] { 700d, 900 })
        {
            double start = row / 2 + 41 * Math.Sqrt(1.25);
            Assert.True(planner.Connect(start, 1959, row, true, out var connection), planner.Reason);
            Assert.Equal(Math.Sqrt(1.25), connection.WebOffsetMm, 6);
            final.Add(Bar(request, start, 1959, row, connection));
        }
        var plan = planner.Complete(final.ToArray());
        Assert.Equal(2, plan.Bars.Count); Assert.Equal(2, plan.Groups.Count);
        Assert.All(plan.Groups, g => Assert.Equal(1, g.Count));
        Assert.Single(plan.PerimeterUEdges);
        foreach (var bar in plan.Bars)
        {
            var web = bar.Centerline[2].Start;
            Assert.Equal(web.Y / 2 + 40 * Math.Sqrt(1.25), web.X, 6);
            Assert.Equal(-159, bar.Centerline[0].Start.Z);
            Assert.Equal(-40, bar.Centerline[4].End.Z);
        }
        Assert.Empty(BarContainmentValidator.Validate(plan.Bars, request.Slabs, 200, 35, 35, 35));
    }

    [Fact]
    public void GroupSlicesAndStationProofContainEveryPhysicalBarExactlyOnce()
    {
        var request = Request(); var planner = new Probe(request); var final = new List<PlannedBar>();
        foreach (double row in new[] { 300d, 500, 900, 1200, 1500 })
            foreach (bool start in new[] { true, false })
            {
                Assert.True(planner.Connect(41, 1959, row, start, out var connection));
                final.Add(Bar(request, 41, 1959, row, connection));
            }
        // Multiple demand specifications may use the same physical end.
        final.Add(final[0]);
        var plan = planner.Complete(final.ToArray());
        Assert.Equal(10, plan.Bars.Count);
        Assert.Equal(10, plan.Bars.Select(b => b.SpecKey).Distinct().Count());
        int offset = 0;
        foreach (var group in plan.Groups)
        {
            Assert.Equal(plan.Bars[offset].SpecKey, group.SpecKey);
            for (int i = 0; i < group.Count; i++)
            {
                var bar = plan.Bars[offset + i];
                Assert.Equal(group.Centerline[2].Start.X, bar.Centerline[2].Start.X, 6);
                Assert.Equal(group.Centerline[2].Start.Y + i * group.SpacingMm, bar.Centerline[2].Start.Y, 6);
            }
            offset += group.Count;
        }
        Assert.Equal(plan.Bars.Count, offset);
        Assert.Equal(plan.Bars.Select(b => b.SpecKey).OrderBy(k => k), plan.PerimeterUEdges.SelectMany(e => e.BarKeys).OrderBy(k => k));
    }

    [Fact]
    public void FinalRowMovedAwayFromTheEdgeCannotReuseTrialEvidence()
    {
        var request = Request(); var planner = new Probe(request);
        Assert.True(planner.Connect(41, 1959, 700, true, out var connection));
        var plan = planner.Complete(Bar(request, 400, 1959, 700, connection));
        Assert.Empty(plan.Bars);
        Assert.Contains(plan.Diagnostics, d => d.Code == "AUTOMATIC_ZONE_END_INVALID" && d.IsBlocking);
    }

    [Fact]
    public void SameGeometryWithDifferentZoneTypesKeepsDistinctIdsTypesAndRowSteps()
    {
        var request = Request();
        var first = request.Specifications[0];
        var second = new ReinforcementSpec { Key = "second-zone", TypeKey = "second-type", DiameterMm = 12,
            ModelDiameterMm = 12, SpacingMm = 150, ElevationMm = -159 };
        request.Specifications.Add(second);
        request.AutomaticZoneEndTypes[first.TypeKey] = Settings(first.TypeKey);
        request.AutomaticZoneEndTypes[second.TypeKey] = Settings(second.TypeKey);
        var planner = new Probe(request); var final = new List<PlannedBar>();
        string firstId = null, secondId = null;
        foreach (var spec in new[] { first, second })
            foreach (double row in spec == first ? new[] { 700d, 900 } : new[] { 700d, 850, 1000 })
            {
                Assert.True(planner.Connect(41, 1959, row, true, out var connection, spec: spec), planner.Reason);
                var bar = Bar(request, 41, 1959, row, connection); bar.SpecKey = spec.Key; final.Add(bar);
                if (spec == first) firstId = connection.EdgeId; else secondId = connection.EdgeId;
            }
        Assert.NotEqual(firstId, secondId);
        var plan = planner.Complete(final.ToArray());
        Assert.Equal(5, plan.Bars.Count); Assert.Equal(5, plan.Bars.Select(b => b.SpecKey).Distinct().Count());
        Assert.Equal(2, plan.Groups.Count); Assert.Equal(2, plan.PerimeterUEdges.Count);
        var a = Assert.Single(plan.Groups, g => g.Placement.TypeKey == first.TypeKey);
        var b = Assert.Single(plan.Groups, g => g.Placement.TypeKey == second.TypeKey);
        Assert.Equal(2, a.Count); Assert.Equal(200, a.SpacingMm);
        Assert.Equal(3, b.Count); Assert.Equal(150, b.SpacingMm);
        int cursor = 0;
        foreach (var group in plan.Groups)
        {
            Assert.Equal(group.SpecKey, plan.Bars[cursor].SpecKey);
            Assert.All(plan.Bars.Skip(cursor).Take(group.Count), bar => Assert.Equal(group.Placement.TypeKey, bar.Placement.TypeKey));
            cursor += group.Count;
        }
        var onlyFirst = planner.Complete(final.Where(b => b.SpecKey == first.Key).ToArray());
        Assert.Equal(2, onlyFirst.Bars.Count);
        Assert.All(onlyFirst.Bars, bar => Assert.Equal(first.TypeKey, bar.Placement.TypeKey));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrMismatchedZoneTypeNeverUsesDefaultUType(bool mismatched)
    {
        var request = Request();
        request.AutomaticZoneEndTypes[mismatched ? request.Specifications[0].TypeKey : "unrelated-type"] = Settings("unrelated-type");
        var planner = new Probe(request);
        Assert.False(planner.Connect(41, 1959, 700, true, out _));
        Assert.False(string.IsNullOrWhiteSpace(planner.Reason));
        Assert.Empty(planner.Complete().Bars);
    }

    [Fact]
    public void EachZoneTypeOwnsItsPhysicalDiameterBendAndOppositeFace()
    {
        var request = Request();
        var first = request.Specifications[0];
        var second = new ReinforcementSpec { Key = "zone16", TypeKey = "type16", DiameterMm = 16,
            ModelDiameterMm = 16, SpacingMm = 150, ElevationMm = -157 };
        request.Specifications.Add(second);
        request.AutomaticZoneEndTypes[first.TypeKey] = Settings(first.TypeKey);
        var settings16 = Settings(second.TypeKey);
        settings16.DiameterMm = settings16.ModelDiameterMm = 16; settings16.BendDiameterMm = 80;
        request.AutomaticZoneEndTypes[second.TypeKey] = settings16;
        var planner = new Probe(request);
        Assert.True(planner.Connect(41, 1959, 700, true, out var connection12, spec: first), planner.Reason);
        Assert.True(planner.Connect(43, 1957, 900, true, out var connection16, spec: second), planner.Reason);
        var bar12 = Bar(request, 41, 1959, 700, connection12);
        var bar16 = Bar(request, 43, 1957, 900, connection16); bar16.SpecKey = second.Key;
        var plan = planner.Complete(bar12, bar16);
        Assert.Equal(2, plan.Bars.Count);
        var u16 = Assert.Single(plan.Bars, b => b.Placement.TypeKey == second.TypeKey);
        Assert.Equal(16, u16.Placement.ModelDiameterMm);
        Assert.Equal(43, u16.Centerline[2].Start.X, 6);
        Assert.Equal(-157, u16.Centerline[0].Start.Z);
        Assert.Equal(-43, u16.Centerline[4].End.Z);
        Assert.Equal(48, Assert.Single(plan.PerimeterUEdges, e => e.ModelDiameterMm == 16).BendRadiusMm);
        Assert.Empty(BarContainmentValidator.Validate(plan.Bars, request.Slabs, 200, 35, 35, 35));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredStraightLapRejectsShortZonesEvenWhenFullUItselfFits(bool useTypeMap)
    {
        var request = Request(); request.AutomaticZoneEnds.LegLengthMm = 1030; request.AutomaticZoneEnds.RequiredLapMm = 1000;
        if (useTypeMap)
        {
            request.AutomaticZoneEnds.TypeKey = request.Specifications[0].TypeKey;
            request.AutomaticZoneEndTypes[request.Specifications[0].TypeKey] = request.AutomaticZoneEnds;
        }
        var planner = new Probe(request);
        Assert.False(planner.Connect(41, 600, 700, true, out _));
        Assert.Contains("1000", planner.Reason);
        Assert.Empty(planner.Complete().Bars);
        Assert.True(planner.Connect(41, 1071, 700, true, out var connection), planner.Reason);
        Assert.Equal(1000, connection.OverlapMm, 6);
        var plan = planner.Complete(Bar(request, 41, 1071, 700, connection));
        Assert.Single(plan.Bars);
    }

    private static AutomaticZoneEndOptions Settings(string typeKey) => new()
    {
        TypeKey = typeKey, DiameterMm = 12, ModelDiameterMm = 12, BendDiameterMm = 48, LegLengthMm = 500,
        ThicknessMm = 200, TopCoverMm = 35, BottomCoverMm = 35, SideCoverMm = 35
    };

    private static PlanRequest Request(bool top = false, bool alongX = true) => new()
    {
        Slabs = new() { new() { Shell = new() { new(0, 0), new(2000, 0), new(2000, 2000), new(0, 2000) } } },
        Options = new() { AlongX = alongX, EdgeCoverMm = 35 },
        Specifications = new() { new() { Key = "zone", TypeKey = "zone-type", DiameterMm = 12, ModelDiameterMm = 12,
            SpacingMm = 200, ElevationMm = top ? -41 : -159 } },
        AutomaticZoneEnds = new() { TypeKey = "U-type", DiameterMm = 12, ModelDiameterMm = 12, BendDiameterMm = 48,
            ThicknessMm = 200, TopCoverMm = 35, BottomCoverMm = 35, SideCoverMm = 35, LegLengthMm = 500, Top = top }
    };

    private static PlannedBar Bar(PlanRequest request, double start, double end, double row, PerimeterZoneEndConnection connection) => new()
    {
        Start = request.Options.AlongX ? new(start, row) : new(row, start),
        End = request.Options.AlongX ? new(end, row) : new(row, end), SpecKey = "zone", EndConnections = new() { connection }
    };

    // Test the intentionally internal geometry boundary without making it a product API.
    private sealed class Probe
    {
        private readonly object instance;
        private readonly Type type = typeof(PlanRequest).Assembly.GetType("SlabReinforcement.Core.AutomaticZoneEndPlanner", true)!;
        private readonly PlanRequest request;
        internal Probe(PlanRequest request)
        {
            this.request = request;
            instance = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { request, CancellationToken.None }, null)!;
        }
        internal string Reason => (string)type.GetProperty("LastFailureReason", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
        internal bool Connect(double start, double end, double row, bool atStart, out PerimeterZoneEndConnection connection, string edge = null,
            ReinforcementSpec spec = null)
        {
            object[] args = { start, end, row, spec ?? request.Specifications[0], atStart, null!, edge! };
            bool result = (bool)type.GetMethod("TryConnect", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args)!;
            connection = (PerimeterZoneEndConnection)args[5]; return result;
        }
        internal ReinforcementPlan Complete(params PlannedBar[] bars) => (ReinforcementPlan)type.GetMethod("Complete", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, new object[] { bars })!;
    }
}
