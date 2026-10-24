namespace SlabReinforcement
{
    internal struct CadPoint3D
    {
        public CadPoint3D(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        public CadPoint3D Scale(double factor)
        {
            return new CadPoint3D(X * factor, Y * factor, Z * factor);
        }
    }
}
