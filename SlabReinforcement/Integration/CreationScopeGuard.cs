using SlabReinforcement.Core;
using System;
using System.Linq;

namespace SlabReinforcement.Integration
{
    internal static class CreationScopeGuard
    {
        // Check before opening any Revit transaction. References may belong to another
        // component, but every newly written physical bar and set must match this operation.
        internal static void Validate(ReinforcementPlan plan, ReinforcementCreationScope scope)
        {
            if (scope == ReinforcementCreationScope.All) return;
            if (scope != ReinforcementCreationScope.Background && scope != ReinforcementCreationScope.Zones)
                throw new ArgumentException("Выберите вкладку фона или зон и пересчитайте.");
            if (plan.Bars.Any(b => !Allows(b.Placement?.Component, b.PreviewPurpose, scope))
                || plan.Groups.Any(g => !Allows(g.Placement?.Component, g.PreviewPurpose, scope)))
                throw new ArgumentException("Расчёт содержит арматуру другой вкладки. Пересчитайте выбранную вкладку.");
        }

        private static bool Allows(string component, PreviewPurpose purpose, ReinforcementCreationScope scope)
        {
            if (component == "Background") return scope == ReinforcementCreationScope.Background;
            if (component == "Zones") return scope == ReinforcementCreationScope.Zones;
            if (component != "PerimeterU") return false;
            var required = scope == ReinforcementCreationScope.Background ? PreviewPurpose.Background : PreviewPurpose.Zones;
            return (purpose & required) != 0;
        }
    }
}
