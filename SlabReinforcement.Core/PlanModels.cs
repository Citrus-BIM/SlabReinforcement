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
    }

    public sealed class PlanRequest
    {
        public List<PolygonInput> Slabs { get; set; } = new List<PolygonInput>();
        public List<FaceInput> Faces { get; set; } = new List<FaceInput>();
        public List<ReinforcementSpec> Specifications { get; set; } = new List<ReinforcementSpec>();
        public PlanOptions Options { get; set; } = new PlanOptions();
    }

    public sealed class PlannedBar
    {
        public Point2 Start { get; set; }
        public Point2 End { get; set; }
        public string SpecKey { get; set; }
        public string ColorKey { get; set; }
        public BarPlacement Placement { get; set; }
        public List<CenterlineSegment> Centerline { get; set; }
    }

    public sealed class BarGroup
    {
        public Point2 Start { get; set; }
        public Point2 End { get; set; }
        public int Count { get; set; }
        public double SpacingMm { get; set; }
        public string SpecKey { get; set; }
        public string ColorKey { get; set; }
        public BarPlacement Placement { get; set; }
        public List<CenterlineSegment> Centerline { get; set; }
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

    public sealed class ReinforcementPlan
    {
        public List<PlannedBar> Bars { get; set; } = new List<PlannedBar>();
        public List<BarGroup> Groups { get; set; } = new List<BarGroup>();
        public List<PlanDiagnostic> Diagnostics { get; set; } = new List<PlanDiagnostic>();
        public List<PolygonInput> DemandRegions { get; set; } = new List<PolygonInput>();
        public bool CanCreate => Bars.Count > 0 && !Diagnostics.Any(d => d.IsBlocking);
    }
}
