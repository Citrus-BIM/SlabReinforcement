namespace SlabReinforcement.Core
{
    /// <summary>A point in a local slab plane, in millimetres.</summary>
    public readonly struct Point2
    {
        public Point2(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }
}
