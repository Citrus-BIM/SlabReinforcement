namespace SlabReinforcement.Core
{
    /// <summary>A three-dimensional position or vector; positions use millimetres.</summary>
    public readonly struct Point3
    {
        public Point3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double X { get; }
        public double Y { get; }
        public double Z { get; }
    }
}
