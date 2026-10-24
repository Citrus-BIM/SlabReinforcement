using System.Collections.Generic;
using System.Linq;

namespace SlabReinforcement
{
    public class ZoneAnalysisResult
    {
        public List<FiniteElementFace> SourceFaces { get; set; } = new List<FiniteElementFace>();
        public List<FiniteElementFace> HorizontalFaces { get; set; } = new List<FiniteElementFace>();
        public List<int> LevelsMm { get; set; } = new List<int>();
        public string LevelsSummary { get; set; } = "—";
        public string AngleSummary { get; set; } = "—";
        public List<string> Warnings { get; set; } = new List<string>();
        public List<ZoneGroupAnalysis> Groups { get; set; } = new List<ZoneGroupAnalysis>();

        public int SourceFaceCount => SourceFaces.Count;
        public int HorizontalFaceCount => HorizontalFaces.Count;
        public int SkippedNonHorizontalFaceCount => SourceFaceCount - HorizontalFaceCount;
        public int MissingRebarTypeFaceCount => HorizontalFaces.Count(face => face.RebarType == null);
        public int InvalidSpacingFaceCount => HorizontalFaces.Count(face => face.Spacing <= 0);
        public int GroupCount => Groups.Count;
        public int ClusterCount => Groups.Sum(group => group.Clusters.Count);
        public int TrivialClusterCount => Groups.Sum(group => group.TrivialClusterCount);
        public int EstimatedZoneCount => Groups.Sum(group => group.EstimatedZoneCount);
    }
}
