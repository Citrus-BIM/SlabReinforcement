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
        public double SpacingMm { get; set; } = 200;
        public double LegLengthMm { get; set; } = 500;
    }

    public sealed class PreviewParameters
    {
        public string SourceGroupKey { get; set; }
        public string ConcreteClass { get; set; }
        public string Direction { get; set; }
        public double EdgeCoverMm { get; set; }
        public double LayerOffsetMm { get; set; }
        public double MergeToleranceMm { get; set; }
        public double RoundIncrementMm { get; set; }
        public bool UseCutLengths { get; set; }
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
        public string SourceElevationLabel { get; set; } = "ΔZ от плиты";
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
