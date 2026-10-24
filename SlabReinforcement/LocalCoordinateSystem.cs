using Autodesk.Revit.DB;
using System;

namespace SlabReinforcement
{
    public class LocalCoordinateSystem
    {
        public XYZ Origin { get; }
        public XYZ XAxis { get; }
        public XYZ YAxis { get; }

        public LocalCoordinateSystem(XYZ origin, XYZ xAxis, XYZ yAxis)
        {
            Origin = new XYZ(origin.X, origin.Y, 0);
            XAxis = NormalizePlanVector(xAxis);
            YAxis = NormalizePlanVector(yAxis);
        }

        public UV ToLocal(XYZ point)
        {
            XYZ planPoint = new XYZ(point.X - Origin.X, point.Y - Origin.Y, 0);
            return new UV(planPoint.DotProduct(XAxis), planPoint.DotProduct(YAxis));
        }

        public XYZ ToWorld(double u, double v)
        {
            return new XYZ(
                Origin.X + (XAxis.X * u) + (YAxis.X * v),
                Origin.Y + (XAxis.Y * u) + (YAxis.Y * v),
                0);
        }

        private static XYZ NormalizePlanVector(XYZ vector)
        {
            XYZ planVector = new XYZ(vector.X, vector.Y, 0);
            if (planVector.GetLength() < 1e-9)
            {
                throw new ArgumentException("Local axis cannot be zero-length.", nameof(vector));
            }

            return planVector.Normalize();
        }
    }
}
