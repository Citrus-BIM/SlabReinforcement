using System;
using System.Collections.Generic;
using System.Threading;
using SlabReinforcement.Core;

namespace SlabReinforcement
{
    public sealed class PreviewSelection
    {
        public string ColorKey { get; set; }
        public string SpecKey { get; set; }
        public double SpacingMm { get; set; }
        public double? RequiredAreaMm2PerM { get; set; }
    }

    public sealed class PreviewBackgroundLayer
    {
        public bool Enabled { get; set; }
        public string Direction { get; set; }
        public string SpecKey { get; set; }
        public double SpacingMm { get; set; } = 200;
    }

    public sealed class PreviewPerimeterOptions
    {
        public bool Enabled { get; set; }
        public bool Outer { get; set; } = true;
        public bool Openings { get; set; } = true;
        public bool UseUbars { get; set; } = true;
        public bool UseLongitudinalBars { get; set; }
        public string SpecKey { get; set; }
        // Manual maximum spacing for edges without matching lower background rows.
        // Invalid/NaN is permitted when both actual lower directions make it unused.
        public double SpacingMm { get; set; } = 200;
        public double LegLengthMm { get; set; } = 500;
    }

    public enum ReinforcementCreationScope { All = 0, Background, Zones }

    public sealed class PreviewParameters
    {
        // All preserves existing API callers; the production form chooses its active tab.
        public ReinforcementCreationScope CreationScope { get; set; }
        public string SourceGroupKey { get; set; }
        public string ConcreteClass { get; set; }
        public string Direction { get; set; }
        // Null preserves the host cover for legacy callers; the form supplies explicit values.
        public double? TopCoverMm { get; set; }
        public double? BottomCoverMm { get; set; }
        public double EdgeCoverMm { get; set; }
        // Retained for compatibility with saved/test callers; zone placement ignores it.
        public double LayerOffsetMm { get; set; }
        public double MergeToleranceMm { get; set; }
        public double RoundIncrementMm { get; set; }
        public bool UseCutLengths { get; set; }
        // Ordinary bar intersections are expected in this detailing workflow.
        // Geometry, host cover and full-duplicate checks remain independent.
        public bool AllowBarIntersections { get; set; } = true;
        // Automatic local U details only at zone ends that cannot fit straight anchorage.
        // Independent from the slab perimeter; structural adequacy is not inferred.
        public bool UsePerimeterUForZoneEnds { get; set; }
        // The production dialog derives U lengths from concrete and the selected bar types.
        // False preserves explicit dimensions for existing API/test callers.
        public bool AutomaticULegLength { get; set; }
        public double ZoneEndLegLengthMm { get; set; } = 500;
        public bool IncludeZones { get; set; } = true;
        public bool CalculationAxesConfirmed { get; set; }
        // Manual, AdditionalArea or TotalArea. Areas use mm²/m, never inferred from RGB.
        public string DemandMeaning { get; set; } = "Manual";
        public List<PreviewBackgroundLayer> BackgroundLayers { get; set; } = new List<PreviewBackgroundLayer>();
        public PreviewPerimeterOptions Perimeter { get; set; } = new PreviewPerimeterOptions();
        public List<PreviewSelection> Selections { get; set; } = new List<PreviewSelection>();
    }

    // Geometry and keys are captured in the external command before entering the worker.
    public sealed class PreviewContext
    {
        public string HostName { get; set; }
        public string AxisDescription { get; set; }
        public string SourceElevationLabel { get; set; } = "Высота от плиты";
        public double TopCoverMm { get; set; } = 25;
        public double BottomCoverMm { get; set; } = 25;
        public double SideCoverMm { get; set; } = 25;
        public List<PolygonInput> Slabs { get; set; } = new List<PolygonInput>();
        public List<FaceInput> Faces { get; set; } = new List<FaceInput>();
        public List<CadPlaneGroup> SourceGroups { get; set; } = new List<CadPlaneGroup>();
        public List<string> Warnings { get; set; } = new List<string>();
        public Dictionary<string, string> RebarTypeKeys { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public Func<PreviewParameters, CancellationToken, ReinforcementPlan> Calculate { get; set; }
    }
}
