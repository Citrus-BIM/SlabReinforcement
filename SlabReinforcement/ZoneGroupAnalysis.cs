using System;
using System.Collections.Generic;
using System.Linq;

namespace SlabReinforcement
{
    public class ZoneGroupAnalysis
    {
        public List<FiniteElementFace> Faces { get; set; } = new List<FiniteElementFace>();
        public LocalCoordinateSystem CoordinateSystem { get; set; }
        public double DominantAngleDegrees { get; set; }
        public List<OrientedFace> OrientedFaces { get; set; } = new List<OrientedFace>();
        public List<OrientedLineData> Lines { get; set; } = new List<OrientedLineData>();
        public List<OrientedCluster> Clusters { get; set; } = new List<OrientedCluster>();

        public int TrivialClusterCount =>
            Clusters.Count(cluster => IsTrivialCluster(cluster));

        public int EstimatedZoneCount =>
            Clusters.Count(cluster => !IsTrivialCluster(cluster));

        public string AngleSummary =>
            double.IsNaN(DominantAngleDegrees)
                ? "—"
                : $"{DominantAngleDegrees:0.0}°";

        public static bool IsTrivialCluster(OrientedCluster cluster)
        {
            return cluster != null &&
                   cluster.Lines.Count == 1 &&
                   cluster.Lines[0].Faces.Count == 1;
        }
    }
}
