using SlabReinforcement.Core;
using System.Collections.Generic;

namespace SlabReinforcement
{
    /// <summary>Preserves every physical bar, including coincident projections from different layers.</summary>
    internal static class SchematicPreviewBars
    {
        internal static HashSet<PlannedBar> Select(IEnumerable<PlannedBar> source)
        {
            // Sampling background and perimeter independently breaks the visible correspondence
            // between lower rows and their U-bars. Keep coordinates and membership unchanged.
            return new HashSet<PlannedBar>(source);
        }
    }
}
