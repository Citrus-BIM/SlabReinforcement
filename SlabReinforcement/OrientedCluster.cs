using Autodesk.Revit.DB.Structure;
using System.Collections.Generic;

namespace SlabReinforcement
{
    public class OrientedCluster
    {
        public RebarBarType RebarType { get; set; }
        public double Spacing { get; set; }
        public double MinPrimary { get; set; }
        public double MaxPrimary { get; set; }
        public double MinCross { get; set; }
        public double MaxCross { get; set; }
        public List<OrientedLineData> Lines { get; set; } = new List<OrientedLineData>();
    }
}
