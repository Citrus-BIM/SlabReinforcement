using System;

namespace SlabReinforcement.Core
{
    /// <summary>
    /// An explicit affine mapping: origin + basisX * x + basisY * y + basisZ * z.
    /// No units are inferred, no scale is added, and basis vectors are never normalized.
    /// For the usual placement, coordinates and origin are millimetres and the basis is dimensionless.
    /// A caller may deliberately supply scale in the basis for a separate unit-conversion step.
    /// </summary>
    public sealed class AffinePlacement
    {
        private readonly Point3 origin;
        private readonly Point3 basisX;
        private readonly Point3 basisY;
        private readonly Point3 basisZ;

        public AffinePlacement(Point3 origin, Point3 basisX, Point3 basisY, Point3 basisZ)
        {
            RequireFinite(origin, nameof(origin));
            RequireFinite(basisX, nameof(basisX));
            RequireFinite(basisY, nameof(basisY));
            RequireFinite(basisZ, nameof(basisZ));
            this.origin = origin;
            this.basisX = basisX;
            this.basisY = basisY;
            this.basisZ = basisZ;
        }

        public Point3 OfPoint(Point3 point)
        {
            var vector = OfVector(point);
            var result = new Point3(origin.X + vector.X, origin.Y + vector.Y, origin.Z + vector.Z);
            RequireFinite(result, nameof(point));
            return result;
        }

        public Point3 OfVector(Point3 vector)
        {
            RequireFinite(vector, nameof(vector));
            var result = new Point3(
                vector.X * basisX.X + vector.Y * basisY.X + vector.Z * basisZ.X,
                vector.X * basisX.Y + vector.Y * basisY.Y + vector.Z * basisZ.Y,
                vector.X * basisX.Z + vector.Y * basisY.Z + vector.Z * basisZ.Z);
            RequireFinite(result, nameof(vector));
            return result;
        }

        private static void RequireFinite(Point3 point, string parameterName)
        {
            if (!Finite(point.X) || !Finite(point.Y) || !Finite(point.Z))
                throw new ArgumentException("Coordinates and basis vectors must be finite.", parameterName);
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
