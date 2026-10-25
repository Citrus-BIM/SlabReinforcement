using SlabReinforcement.Core;
using System.Collections.Generic;
using System.Linq;

namespace SlabReinforcement
{
    public enum PreviewScope { All, Background, Zones }

    /// <summary>A display-only selection. The full calculation remains the creation plan.</summary>
    public static class PreviewProjection
    {
        public static ReinforcementPlan Select(ReinforcementPlan plan, PreviewScope scope)
        {
            if (plan == null || scope == PreviewScope.All) return plan;
            var purpose = scope == PreviewScope.Background ? PreviewPurpose.Background : PreviewPurpose.Zones;
            var bars = plan.Bars.Where(b => Includes(b.PreviewPurpose, b.Placement, purpose)).ToList();
            var references = plan.ReferenceBars.Where(b => Includes(b.PreviewPurpose, b.Placement, purpose)).ToList();
            var keys = new HashSet<string>(bars.Concat(references).Select(b => b.SpecKey));
            return new ReinforcementPlan
            {
                Bars = bars,
                ReferenceBars = references,
                // A shared physical U may be visible in both tabs, while its original set
                // belongs to the background. It is not an additional zone set to create.
                Groups = plan.Groups.Where(g => Includes(g.PreviewPurpose, g.Placement, purpose)).ToList(),
                Diagnostics = plan.Diagnostics.ToList(),
                DemandRegions = scope == PreviewScope.Zones ? plan.DemandRegions.ToList() : new List<PolygonInput>(),
                PerimeterUEdges = plan.PerimeterUEdges.Where(e => e.BarKeys.Any(keys.Contains)).ToList()
            };
        }

        private static bool Includes(PreviewPurpose actual, BarPlacement placement, PreviewPurpose requested)
        {
            if (actual == PreviewPurpose.None)
            {
                if (placement?.Component == "Zones") actual = PreviewPurpose.Zones;
                else if (placement?.Component == "Background" || placement?.Component == "PerimeterU")
                    actual = PreviewPurpose.Background;
            }
            return (actual & requested) != 0;
        }
    }
}
