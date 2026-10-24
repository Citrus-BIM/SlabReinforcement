namespace SlabReinforcement
{
    public class ZoneModelingOptions
    {
        public double AdjacentElementsToleranceMm { get; set; }
        public double ZoneMergeToleranceMm { get; set; }
        public double HorizontalFaceToleranceMm { get; set; }
        public double ElevationGroupingToleranceMm { get; set; }
        public string SelectedReinforcementDirection { get; set; }

        public bool AlongLocalX =>
            !string.IsNullOrWhiteSpace(SelectedReinforcementDirection) &&
            SelectedReinforcementDirection.EndsWith("X");
    }
}
