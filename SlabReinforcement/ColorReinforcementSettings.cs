using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SlabReinforcement
{
    public class ColorReinforcementSettings
    {
        public string Color { get; set; }
        public string RebarTypeName { get; set; }
        public double Spacing { get; set; }
        public bool IsSelected { get; set; }
        public string RequiredAreaMm2PerM { get; set; }
    }

    public class BackgroundLayerSettings
    {
        public bool Enabled { get; set; }
        public string Direction { get; set; }
        public string RebarTypeName { get; set; }
        public string SpacingMm { get; set; } = "200";
    }

    public class PerimeterReinforcementSettings
    {
        public bool Enabled { get; set; }
        public bool Outer { get; set; } = true;
        public bool Openings { get; set; } = true;
        public bool UseUbars { get; set; } = true;
        public bool UseLongitudinalBars { get; set; }
        public string RebarTypeName { get; set; }
        public string SpacingMm { get; set; } = "200";
        public string LegLengthMm { get; set; } = "500";
    }
}
