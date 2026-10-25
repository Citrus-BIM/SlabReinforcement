using System;

namespace SlabReinforcement.Core
{
    /// <summary>
    /// A right-handed orthonormal slab frame. Positions and local coordinates use millimetres;
    /// the three basis vectors are dimensionless. Local Z preserves signed distance to the plane.
    /// </summary>
    public sealed class SlabFrame
    {
        private const double MinimumEdgeLengthMm = 1e-9;
        private const double MinimumNormalLength = 1e-12;
        private const double MinimumProjectedDirectionLength = 1e-10;

        private SlabFrame(Point3 origin, Point3 xAxis, Point3 yAxis, Point3 normal)
        {
            Origin = origin;
            XAxis = xAxis;
            YAxis = yAxis;
            Normal = normal;
        }

        public Point3 Origin { get; }
        public Point3 XAxis { get; }
        public Point3 YAxis { get; }
        public Point3 Normal { get; }

        /// <summary>
        /// Projects the selected edge onto the supplied plane, using its direction as local X
        /// and normal cross X as local Y. Rejects zero and numerically unstable directions.
        /// </summary>
        public static SlabFrame FromEdge(Point3 start, Point3 end, Point3 normal)
        {
            RequireFinite(start, nameof(start));
            RequireFinite(end, nameof(end));
            var n = Normalize(normal, MinimumNormalLength, nameof(normal));
            var edge = Normalize(Subtract(end, start), MinimumEdgeLengthMm, nameof(end));
            var projected = Subtract(edge, Multiply(n, Dot(edge, n)));
            var x = Normalize(projected, MinimumProjectedDirectionLength, nameof(end));
            var y = Normalize(Cross(n, x), MinimumProjectedDirectionLength, nameof(end));

            // Re-orthogonalize after projection to remove residual floating-point normal components.
            x = Normalize(Cross(y, n), MinimumProjectedDirectionLength, nameof(end));
            return new SlabFrame(start, x, y, n);
        }

        public Point3 ToLocal(Point3 point)
        {
            RequireFinite(point, nameof(point));
            var delta = Subtract(point, Origin);
            RequireFinite(delta, nameof(point));
            var result = new Point3(Dot(delta, XAxis), Dot(delta, YAxis), Dot(delta, Normal));
            RequireFinite(result, nameof(point));
            return result;
        }

        public Point3 ToWorld(Point3 point)
        {
            RequireFinite(point, nameof(point));
            var result = new Point3(
                Origin.X + point.X * XAxis.X + point.Y * YAxis.X + point.Z * Normal.X,
                Origin.Y + point.X * XAxis.Y + point.Y * YAxis.Y + point.Z * Normal.Y,
                Origin.Z + point.X * XAxis.Z + point.Y * YAxis.Z + point.Z * Normal.Z);
            RequireFinite(result, nameof(point));
            return result;
        }

        private static Point3 Normalize(Point3 value, double minimumLength, string parameterName)
        {
            RequireFinite(value, parameterName);
            var scale = Math.Max(Math.Abs(value.X), Math.Max(Math.Abs(value.Y), Math.Abs(value.Z)));
            if (scale == 0)
                throw new ArgumentException("Направление плиты не определено. Проверьте выбранную поверхность и отрезок.", parameterName);

            // Scaling before computing the length avoids overflow when squaring large components.
            var scaled = new Point3(value.X / scale, value.Y / scale, value.Z / scale);
            var length = Math.Sqrt(Dot(scaled, scaled));
            if (scale <= minimumLength / length)
                throw new ArgumentException("Выберите более длинный отрезок вдоль поверхности плиты.", parameterName);
            return new Point3(scaled.X / length, scaled.Y / length, scaled.Z / length);
        }

        private static Point3 Subtract(Point3 a, Point3 b) => new Point3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        private static Point3 Multiply(Point3 value, double scale) => new Point3(value.X * scale, value.Y * scale, value.Z * scale);
        private static double Dot(Point3 a, Point3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        private static Point3 Cross(Point3 a, Point3 b) => new Point3(
            a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        private static void RequireFinite(Point3 point, string parameterName)
        {
            if (!Finite(point.X) || !Finite(point.Y) || !Finite(point.Z))
                throw new ArgumentException("Не удалось определить положение или направление плиты: неверные координаты.", parameterName);
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
