using SlabReinforcement.Core;
using SlabReinforcement.Integration;
using Xunit;

namespace SlabReinforcement.Core.Tests;

public class CreationScopeGuardTests
{
    [Theory]
    [InlineData(ReinforcementCreationScope.Background, "Background", PreviewPurpose.Background)]
    [InlineData(ReinforcementCreationScope.Background, "PerimeterU", PreviewPurpose.Background)]
    [InlineData(ReinforcementCreationScope.Zones, "Zones", PreviewPurpose.Zones)]
    [InlineData(ReinforcementCreationScope.Zones, "PerimeterU", PreviewPurpose.Zones)]
    public void AllowsOnlyTheRequestedCreationAndReferencesFromOtherComponents(
        ReinforcementCreationScope scope, string component, PreviewPurpose purpose)
    {
        var plan = Plan(component, purpose);
        plan.ReferenceBars.Add(new PlannedBar { Placement = new BarPlacement { Component = "Background" } });
        CreationScopeGuard.Validate(plan, scope);
    }

    [Theory]
    [InlineData(ReinforcementCreationScope.Background, "Zones", PreviewPurpose.Zones)]
    [InlineData(ReinforcementCreationScope.Background, "PerimeterU", PreviewPurpose.Zones)]
    [InlineData(ReinforcementCreationScope.Zones, "Background", PreviewPurpose.Background)]
    [InlineData(ReinforcementCreationScope.Zones, "PerimeterU", PreviewPurpose.Background)]
    [InlineData(ReinforcementCreationScope.Zones, "PerimeterU", PreviewPurpose.None)]
    [InlineData(ReinforcementCreationScope.Background, null, PreviewPurpose.None)]
    public void RejectsForeignBarsAndGroupsIndependently(
        ReinforcementCreationScope scope, string component, PreviewPurpose purpose)
    {
        var barsOnly = Plan(component, purpose);
        barsOnly.Groups.Clear();
        Assert.Throws<ArgumentException>(() => CreationScopeGuard.Validate(barsOnly, scope));
        var groupsOnly = Plan(component, purpose);
        groupsOnly.Bars.Clear();
        Assert.Throws<ArgumentException>(() => CreationScopeGuard.Validate(groupsOnly, scope));
    }

    [Fact]
    public void LegacyCombinedCallRemainsCompatibleButUnknownScopesAreRejected()
    {
        var plan = Plan(null, PreviewPurpose.None);
        CreationScopeGuard.Validate(plan, ReinforcementCreationScope.All);
        Assert.Throws<ArgumentException>(() => CreationScopeGuard.Validate(plan, (ReinforcementCreationScope)99));
    }

    private static ReinforcementPlan Plan(string component, PreviewPurpose purpose) => new()
    {
        Bars = new() { new PlannedBar { Placement = new BarPlacement { Component = component }, PreviewPurpose = purpose } },
        Groups = new() { new BarGroup { Placement = new BarPlacement { Component = component }, PreviewPurpose = purpose } }
    };
}
