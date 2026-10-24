namespace SlabReinforcement
{
    internal struct CadTransform3D
    {
        public CadTransform3D(CadPoint3D origin, CadPoint3D basisX, CadPoint3D basisY, CadPoint3D basisZ)
        {
            Origin = origin;
            BasisX = basisX;
            BasisY = basisY;
            BasisZ = basisZ;
        }

        public CadPoint3D Origin { get; }
        public CadPoint3D BasisX { get; }
        public CadPoint3D BasisY { get; }
        public CadPoint3D BasisZ { get; }

        public static CadTransform3D Identity
        {
            get
            {
                return new CadTransform3D(
                    new CadPoint3D(0, 0, 0),
                    new CadPoint3D(1, 0, 0),
                    new CadPoint3D(0, 1, 0),
                    new CadPoint3D(0, 0, 1));
            }
        }

        public CadPoint3D OfPoint(CadPoint3D point)
        {
            return new CadPoint3D(
                Origin.X + (BasisX.X * point.X) + (BasisY.X * point.Y) + (BasisZ.X * point.Z),
                Origin.Y + (BasisX.Y * point.X) + (BasisY.Y * point.Y) + (BasisZ.Y * point.Z),
                Origin.Z + (BasisX.Z * point.X) + (BasisY.Z * point.Y) + (BasisZ.Z * point.Z));
        }
    }
}
