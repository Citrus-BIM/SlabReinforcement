using System.Text.Json;
using SlabReinforcement;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class PreviewProjectionTests
{
    [Theory]
    [InlineData(PreviewScope.All)]
    [InlineData(PreviewScope.Background)]
    [InlineData(PreviewScope.Zones)]
    public void MissingCalculationHasNoPreview(PreviewScope scope)
    {
        Assert.Null(PreviewProjection.Select(null, scope));
    }

    [Fact]
    public void AllPreservesTheCompleteCreationPlanIncludingUnclassifiedContent()
    {
        var plan = MixedPlan();
        var unclassified = Bar("unclassified", PreviewPurpose.None, "Other");
        plan.Bars.Add(unclassified);
        plan.AutomaticZoneEndPlan = new ReinforcementPlan();

        var selected = PreviewProjection.Select(plan, PreviewScope.All);

        Assert.Same(plan, selected);
        Assert.Contains(unclassified, selected.Bars);
        Assert.Same(plan.AutomaticZoneEndPlan, selected.AutomaticZoneEndPlan);
    }

    [Theory]
    [InlineData(PreviewScope.Background, "background", "shared-u", "existing-background", "background-set")]
    [InlineData(PreviewScope.Zones, "zone", "shared-u", "existing-zone", "zone-set")]
    public void TabShowsItsBarsReferencesAndOwnedGroups(
        PreviewScope scope, string primary, string shared, string reference, string group)
    {
        var plan = MixedPlan();

        var selected = PreviewProjection.Select(plan, scope);

        Assert.Equal(new[] { primary, shared }, selected.Bars.Select(b => b.SpecKey));
        Assert.Equal(new[] { reference }, selected.ReferenceBars.Select(b => b.SpecKey));
        Assert.Equal(new[] { group }, selected.Groups.Select(g => g.SpecKey));
    }

    [Theory]
    [InlineData(PreviewPurpose.Zones, "Background", false, true)]
    [InlineData(PreviewPurpose.Zones, "PerimeterU", false, true)]
    [InlineData(PreviewPurpose.Background, "Zones", true, false)]
    [InlineData(PreviewPurpose.Background | PreviewPurpose.Zones, "Other", true, true)]
    public void ExplicitPurposeOverridesLegacyComponentForBarsReferencesAndGroups(
        PreviewPurpose purpose, string component, bool background, bool zones)
    {
        var plan = new ReinforcementPlan
        {
            Bars = new() { Bar("new", purpose, component) },
            ReferenceBars = new() { Bar("existing", purpose, component) },
            Groups = new() { Group("set", purpose, component) }
        };

        AssertMembership(PreviewProjection.Select(plan, PreviewScope.Background), background);
        AssertMembership(PreviewProjection.Select(plan, PreviewScope.Zones), zones);
    }

    [Theory]
    [InlineData("Background", true, false)]
    [InlineData("PerimeterU", true, false)]
    [InlineData("Zones", false, true)]
    [InlineData("Other", false, false)]
    [InlineData(null, false, false)]
    public void OlderPlansWithoutPurposeUseOnlyKnownComponentFallbacks(
        string component, bool background, bool zones)
    {
        var plan = new ReinforcementPlan
        {
            Bars = new() { Bar("new", PreviewPurpose.None, component) },
            ReferenceBars = new() { Bar("existing", PreviewPurpose.None, component) },
            Groups = new() { Group("set", PreviewPurpose.None, component) }
        };

        AssertMembership(PreviewProjection.Select(plan, PreviewScope.Background), background);
        AssertMembership(PreviewProjection.Select(plan, PreviewScope.Zones), zones);
    }

    [Fact]
    public void MissingPlacementDoesNotClassifyAnOlderBarOrGroup()
    {
        var plan = new ReinforcementPlan
        {
            Bars = new() { new PlannedBar { SpecKey = "new" } },
            ReferenceBars = new() { new PlannedBar { SpecKey = "existing" } },
            Groups = new() { new BarGroup { SpecKey = "set" } }
        };

        AssertMembership(PreviewProjection.Select(plan, PreviewScope.Background), false);
        AssertMembership(PreviewProjection.Select(plan, PreviewScope.Zones), false);
    }

    [Fact]
    public void SharedPhysicalURemainsOneBarAndDoesNotInventAZoneCreationGroup()
    {
        var shared = Bar("shared-u", PreviewPurpose.Background | PreviewPurpose.Zones, "PerimeterU");
        var backgroundSet = Group("full-perimeter-set", PreviewPurpose.Background, "PerimeterU");
        backgroundSet.Count = 24;
        var plan = new ReinforcementPlan
        {
            Bars = new() { shared, Bar("background-u", PreviewPurpose.Background, "PerimeterU") },
            Groups = new() { backgroundSet }
        };

        var background = PreviewProjection.Select(plan, PreviewScope.Background);
        var zones = PreviewProjection.Select(plan, PreviewScope.Zones);

        Assert.Same(shared, Assert.Single(zones.Bars));
        Assert.Contains(shared, background.Bars);
        Assert.Empty(zones.Groups);
        Assert.Same(backgroundSet, Assert.Single(background.Groups));
        Assert.Equal(24, background.Groups.Sum(g => g.Count));
        Assert.Equal(2, plan.Bars.Count);
        Assert.Single(plan.Groups);
    }

    [Theory]
    [InlineData(PreviewScope.Background)]
    [InlineData(PreviewScope.Zones)]
    public void ExistingSharedUIsDisplayedAsAReferenceAndNeverPromotedToCreation(PreviewScope scope)
    {
        var existing = Bar("existing-shared-u", PreviewPurpose.Background | PreviewPurpose.Zones, "PerimeterU");
        var plan = new ReinforcementPlan { ReferenceBars = new() { existing } };

        var selected = PreviewProjection.Select(plan, scope);

        Assert.Same(existing, Assert.Single(selected.ReferenceBars));
        Assert.Empty(selected.Bars);
        Assert.Empty(selected.Groups);
        Assert.False(selected.CanCreate);
        Assert.Empty(plan.Bars);
    }

    [Fact]
    public void DemandOverlayBelongsOnlyToTheZoneTab()
    {
        var plan = MixedPlan();

        var zones = PreviewProjection.Select(plan, PreviewScope.Zones);
        var background = PreviewProjection.Select(plan, PreviewScope.Background);

        Assert.Equal(plan.DemandRegions, zones.DemandRegions);
        Assert.NotSame(plan.DemandRegions, zones.DemandRegions);
        Assert.Empty(background.DemandRegions);
        Assert.Single(plan.DemandRegions);
    }

    [Theory]
    [InlineData(PreviewScope.Background)]
    [InlineData(PreviewScope.Zones)]
    public void HiddenComponentErrorsRemainVisibleAndKeepCreationBlocked(PreviewScope scope)
    {
        var plan = MixedPlan();
        var blocker = new PlanDiagnostic
        {
            Code = "INVALID_OTHER_COMPONENT", SourceId = "other-component",
            Message = "Ошибка полного плана остаётся после смены вкладки.", IsBlocking = true
        };
        plan.Diagnostics.Add(blocker);

        var selected = PreviewProjection.Select(plan, scope);

        Assert.NotEmpty(selected.Bars);
        Assert.Equal(plan.Diagnostics, selected.Diagnostics);
        Assert.NotSame(plan.Diagnostics, selected.Diagnostics);
        Assert.Contains(blocker, selected.Diagnostics);
        Assert.False(selected.CanCreate);
        Assert.False(plan.CanCreate);
    }

    [Theory]
    [InlineData(PreviewScope.Background, "background-edge", "shared-edge", "existing-background-edge")]
    [InlineData(PreviewScope.Zones, "zone-edge", "shared-edge", "existing-zone-edge")]
    public void EdgeEvidenceFollowsVisibleNewAndExistingBarKeys(
        PreviewScope scope, string primaryEdge, string sharedEdge, string existingEdge)
    {
        var plan = MixedPlan();
        plan.PerimeterUEdges = new()
        {
            Edge("background-edge", "background"),
            Edge("zone-edge", "zone"),
            Edge("shared-edge", "shared-u", "unselected-other-row"),
            Edge("existing-background-edge", "existing-background"),
            Edge("existing-zone-edge", "existing-zone"),
            Edge("unrelated-edge", "absent-bar"),
            Edge("empty-edge")
        };

        var selected = PreviewProjection.Select(plan, scope);

        Assert.Equal(new[] { primaryEdge, sharedEdge, existingEdge }, selected.PerimeterUEdges.Select(e => e.Id));
        Assert.NotSame(plan.PerimeterUEdges, selected.PerimeterUEdges);
        Assert.Equal(7, plan.PerimeterUEdges.Count);
    }

    [Fact]
    public void RepeatedTabSwitchesDoNotAlterTheFullCreationPlanOrItsCounters()
    {
        var plan = MixedPlan();
        plan.PerimeterUEdges.Add(Edge("shared-edge", "shared-u"));
        var before = JsonSerializer.Serialize(plan);

        var background = PreviewProjection.Select(plan, PreviewScope.Background);
        var zones = PreviewProjection.Select(plan, PreviewScope.Zones);
        var backgroundAgain = PreviewProjection.Select(plan, PreviewScope.Background);

        Assert.Equal(before, JsonSerializer.Serialize(plan));
        Assert.Equal(JsonSerializer.Serialize(background), JsonSerializer.Serialize(backgroundAgain));
        Assert.Same(plan, PreviewProjection.Select(plan, PreviewScope.All));
        Assert.NotSame(plan, background);
        Assert.NotSame(plan, zones);
        Assert.NotSame(background, backgroundAgain);
        Assert.True(plan.CanCreate);
    }

    [Theory]
    [InlineData(PreviewScope.Background)]
    [InlineData(PreviewScope.Zones)]
    public void ScopedListsCanBeClearedWithoutRemovingAnythingFromTheCreationPlan(PreviewScope scope)
    {
        var plan = MixedPlan();
        plan.PerimeterUEdges.Add(Edge("shared-edge", "shared-u"));
        var before = JsonSerializer.Serialize(plan);

        var selected = PreviewProjection.Select(plan, scope);
        Assert.NotSame(plan.Bars, selected.Bars);
        Assert.NotSame(plan.ReferenceBars, selected.ReferenceBars);
        Assert.NotSame(plan.Groups, selected.Groups);
        selected.Bars.Clear();
        selected.ReferenceBars.Clear();
        selected.Groups.Clear();
        selected.Diagnostics.Clear();
        selected.DemandRegions.Clear();
        selected.PerimeterUEdges.Clear();

        Assert.Equal(before, JsonSerializer.Serialize(plan));
        Assert.True(plan.CanCreate);
    }

    private static void AssertMembership(ReinforcementPlan selected, bool included)
    {
        int expectedCount = included ? 1 : 0;
        Assert.Equal(expectedCount, selected.Bars.Count);
        Assert.Equal(expectedCount, selected.ReferenceBars.Count);
        Assert.Equal(expectedCount, selected.Groups.Count);
    }

    private static PlannedBar Bar(string key, PreviewPurpose purpose, string component) => new()
    {
        SpecKey = key, PreviewPurpose = purpose,
        Placement = new BarPlacement { Component = component },
        Start = new Point2(0, 100), End = new Point2(1000, 100)
    };

    private static BarGroup Group(string key, PreviewPurpose purpose, string component) => new()
    {
        SpecKey = key, PreviewPurpose = purpose,
        Placement = new BarPlacement { Component = component }, Count = 5, SpacingMm = 200,
        Start = new Point2(0, 100), End = new Point2(1000, 100)
    };

    private static PerimeterUEdge Edge(string id, params string[] keys) => new()
    {
        Id = id, BarKeys = keys.ToList()
    };

    private static ReinforcementPlan MixedPlan() => new()
    {
        Bars = new()
        {
            Bar("background", PreviewPurpose.Background, "Background"),
            Bar("zone", PreviewPurpose.Zones, "Zones"),
            Bar("shared-u", PreviewPurpose.Background | PreviewPurpose.Zones, "PerimeterU")
        },
        ReferenceBars = new()
        {
            Bar("existing-background", PreviewPurpose.Background, "Background"),
            Bar("existing-zone", PreviewPurpose.Zones, "PerimeterU")
        },
        Groups = new()
        {
            Group("background-set", PreviewPurpose.Background, "Background"),
            Group("zone-set", PreviewPurpose.Zones, "Zones")
        },
        Diagnostics = new()
        {
            new PlanDiagnostic { Code = "INFO", Message = "Информация полного плана.", IsBlocking = false }
        },
        DemandRegions = new()
        {
            new PolygonInput
            {
                Shell = new() { new Point2(0, 0), new Point2(1000, 0), new Point2(1000, 1000), new Point2(0, 1000) }
            }
        }
    };
}
