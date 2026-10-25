using System.Collections.Generic;
using System.Linq;

namespace SlabReinforcement.Core
{
    public sealed class PolygonInput
    {
        public List<Point2> Shell { get; set; } = new List<Point2>();
        public List<List<Point2>> Holes { get; set; } = new List<List<Point2>>();
    }

    public sealed class FaceInput
    {
        public string Id { get; set; }
        public string ColorKey { get; set; }
        public List<Point2> Vertices { get; set; } = new List<Point2>();
    }

    public sealed class ReinforcementSpec
    {
        public string Key { get; set; }
        public string ColorKey { get; set; }
        // Optional explicit identity; absent keys never imply that two types are interchangeable.
        public string TypeKey { get; set; }
        public string MaterialKey { get; set; }
        public double DiameterMm { get; set; }
        public double? ModelDiameterMm { get; set; }
        public double EffectiveModelDiameterMm => ModelDiameterMm ?? DiameterMm;
        public double SpacingMm { get; set; }
        public double AnchorageMm { get; set; }
        // Actual required additional steel area for this source assignment; null is manual geometry.
        public double? RequiredAreaMm2PerM { get; set; }
        public double? ElevationMm { get; set; }
    }

    public sealed class PlanOptions
    {
        public bool AlongX { get; set; } = true;
        public double EdgeCoverMm { get; set; } = 25;
        public double MergeToleranceMm { get; set; } = 50;
        public double RoundIncrementMm { get; set; } = 10;
        public double MinimumLengthMm { get; set; } = 2;
        public bool UseCutLengths { get; set; }
        public double GridOffsetMm { get; set; }
        public bool CoverageHalfSpacing { get; set; }
        public bool UsePerimeterUForZoneEnds { get; set; }
        public bool AllowBarIntersections { get; set; }
    }

    public sealed class PlanRequest
    {
        public List<PolygonInput> Slabs { get; set; } = new List<PolygonInput>();
        public List<FaceInput> Faces { get; set; } = new List<FaceInput>();
        public List<ReinforcementSpec> Specifications { get; set; } = new List<ReinforcementSpec>();
        public PlanOptions Options { get; set; } = new PlanOptions();
        public ReinforcementPlan PerimeterPlan { get; set; }
        public AutomaticZoneEndOptions AutomaticZoneEnds { get; set; }
        // Per-zone type settings. Empty preserves the single-type API above.
        public Dictionary<string, AutomaticZoneEndOptions> AutomaticZoneEndTypes { get; set; }
            = new Dictionary<string, AutomaticZoneEndOptions>();
    }

    // Optional local U details at zone ends; independent of the main perimeter plan.
    public sealed class AutomaticZoneEndOptions
    {
        public string TypeKey { get; set; }
        public double DiameterMm { get; set; }
        public double ModelDiameterMm { get; set; }
        public double BendDiameterMm { get; set; }
        public double LegLengthMm { get; set; } = 500;
        // Zero keeps legacy geometric-only details; positive requires this straight overlap.
        public double RequiredLapMm { get; set; }
        public double ThicknessMm { get; set; }
        public double TopCoverMm { get; set; }
        public double BottomCoverMm { get; set; }
        public double SideCoverMm { get; set; }
        public bool Top { get; set; }
    }

    [System.Flags]
    public enum PreviewPurpose { None = 0, Background = 1, Zones = 2 }

    public sealed class PlannedBar
    {
        public Point2 Start { get; set; }
        public Point2 End { get; set; }
        public string SpecKey { get; set; }
        public string ColorKey { get; set; }
        public PreviewPurpose PreviewPurpose { get; set; }
        public BarPlacement Placement { get; set; }
        public List<CenterlineSegment> Centerline { get; set; }
        public List<PerimeterZoneEndConnection> EndConnections { get; set; } = new List<PerimeterZoneEndConnection>();
    }

    public enum BarLayoutRule { NumberWithSpacing, MaximumSpacing }

    public sealed class BarGroup
    {
        public Point2 Start { get; set; }
        public Point2 End { get; set; }
        // Native path outline bounds, in local mm along the distribution axis.
        // They do not move the physical rows or change Count/SpacingMm.
        public double? PathBandStartMm { get; set; }
        public double? PathBandEndMm { get; set; }
        public int Count { get; set; }
        public double SpacingMm { get; set; }
        public BarLayoutRule LayoutRule { get; set; }
        public double MaximumSpacingMm { get; set; }
        public double ArrayLengthMm { get; set; }
        public string SpecKey { get; set; }
        public string ColorKey { get; set; }
        public PreviewPurpose PreviewPurpose { get; set; }
        public BarPlacement Placement { get; set; }
        public List<CenterlineSegment> Centerline { get; set; }
        public List<PerimeterZoneEndConnection> EndConnections { get; set; } = new List<PerimeterZoneEndConnection>();
    }

    public sealed class BarPlacement
    {
        public string TypeKey { get; set; }
        public string Component { get; set; }
        public double ElevationMm { get; set; }
        public double ModelDiameterMm { get; set; }
        public Point3 Distribution { get; set; }
    }

    // Midpoint is present only for a circular arc; coordinates are local millimetres.
    public sealed class CenterlineSegment
    {
        public Point3 Start { get; set; }
        public Point3 End { get; set; }
        public Point3? Midpoint { get; set; }
    }

    public sealed class PlanDiagnostic
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public string SourceId { get; set; }
        public bool IsBlocking { get; set; }
    }

    public sealed class PerimeterUEdge
    {
        // Explicit stations from physical background or zone rows, rather than a generated
        // perimeter-spacing array. Inward follows each row and can meet an oblique edge.
        public bool FollowsBackgroundRows { get; set; }
        public bool AlongX { get; set; }
        public string Id { get; set; }
        public Point2 Start { get; set; }
        public Point2 End { get; set; }
        public Point2 Inward { get; set; }
        public double TopZ { get; set; }
        public double BottomZ { get; set; }
        public double ModelDiameterMm { get; set; }
        public double BendRadiusMm { get; set; }
        public double LegLengthMm { get; set; }
        public double SideCoverMm { get; set; }
        public double SpacingMm { get; set; }
        public List<double> StationsMm { get; set; } = new List<double>();
        public double StationRangeStartMm { get; set; }
        public double StationRangeEndMm { get; set; }
        public List<string> BarKeys { get; set; } = new List<string>();
    }

    // Geometric evidence for an explicitly accepted manual edge detail, not anchorage credit.
    public sealed class PerimeterZoneEndConnection
    {
        public bool AtStart { get; set; }
        public string EdgeId { get; set; }
        public double OverlapMm { get; set; }
        public double StationOffsetMm { get; set; }
        public double WebOffsetMm { get; set; }
    }

    public sealed class ReinforcementPlan
    {
        public List<PlannedBar> Bars { get; set; } = new List<PlannedBar>();
        public List<BarGroup> Groups { get; set; } = new List<BarGroup>();
        public List<PlanDiagnostic> Diagnostics { get; set; } = new List<PlanDiagnostic>();
        public List<PolygonInput> DemandRegions { get; set; } = new List<PolygonInput>();
        public List<PerimeterUEdge> PerimeterUEdges { get; set; } = new List<PerimeterUEdge>();
        // Kept separate until session composition: Bars/Groups here remain zone-only.
        public ReinforcementPlan AutomaticZoneEndPlan { get; set; }
        // Existing physical bars used by this plan. Never written again; rechecked before and after creation.
        public List<PlannedBar> ReferenceBars { get; set; } = new List<PlannedBar>();
        public bool CanCreate => Bars.Count > 0 && !Diagnostics.Any(d => d.IsBlocking);
    }
}
