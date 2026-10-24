using System.Collections.Generic;

namespace SlabReinforcement
{
    public class ZonePreviewSummary
    {
        public int TotalFaces { get; set; }
        public int HorizontalFaces { get; set; }
        public int SkippedFaces { get; set; }
        public int ColorCount { get; set; }
        public int SelectedColorCount { get; set; }
        public int EstimatedZoneCount { get; set; }
        public int GroupCount { get; set; }
        public int TrivialClusterCount { get; set; }
        public int MissingRebarTypeFaceCount { get; set; }
        public int InvalidSpacingFaceCount { get; set; }
        public string LevelsSummary { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
