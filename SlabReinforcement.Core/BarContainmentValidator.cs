using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Operation.Distance;

namespace SlabReinforcement.Core
{
    public static class BarContainmentValidator
    {
        public const double ArcToleranceMm = 0.01;
        private const double Epsilon = GeometryKernel.Epsilon;

        /// <summary>
        /// Validates the physical cover of actual bars in a slab-local frame whose top
        /// surface is Z=0 and bottom Z=-thickness. Centerline is absolute local 3D mm.
        /// Radius is conservatively reserved in XY and vertically. Does not verify
        /// anchorage, bar-to-bar collision or section capacity.
        /// </summary>
        public static List<PlanDiagnostic> Validate(IEnumerable<PlannedBar> bars, IEnumerable<PolygonInput> slabs,
            double thicknessMm, double topCoverMm, double bottomCoverMm, double sideCoverMm, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var result = new List<PlanDiagnostic>();
            try
            {
                if (bars == null || slabs == null || !Finite(thicknessMm) || thicknessMm <= 0
                    || new[] { topCoverMm, bottomCoverMm, sideCoverMm }.Any(v => !Finite(v) || v < 0))
                    throw new ArgumentException("Не заданы плита, стержни, толщина или защитные слои.");
                var hosts = new List<Geometry>(); long vertices = 0;
                foreach (var slab in slabs)
                {
                    token.ThrowIfCancellationRequested();
                    if (slab?.Shell == null || slab.Holes == null || slab.Holes.Any(h => h == null)) throw new ArgumentException("Не заданы контуры плиты.");
                    vertices += slab.Shell.Count + slab.Holes.Sum(h => (long)h.Count);
                    if (hosts.Count >= GeometryKernel.MaximumPolygons || vertices > GeometryKernel.MaximumVertices) throw new LimitException();
                    hosts.Add(GeometryKernel.ReadPolygon(slab));
                }
                if (hosts.Count == 0) throw new ArgumentException("Не задан бетон плиты.");
                var host = GeometryKernel.Union(hosts);
                var prepared = PreparedGeometryFactory.Prepare(host);
                var boundary = new IndexedFacetDistance(host.Boundary);
                var count = 0; var samples = 0;
                foreach (var bar in bars)
                {
                    token.ThrowIfCancellationRequested();
                    if (++count > CompositePlanValidator.MaximumBars) throw new LimitException();
                    var source = count.ToString(CultureInfo.InvariantCulture);
                    if (bar?.Placement == null || !Finite(bar.Placement.ModelDiameterMm) || bar.Placement.ModelDiameterMm <= Epsilon * 4
                        || bar.Placement.ModelDiameterMm > 1e9 || !Finite(bar.Placement.ElevationMm))
                        throw new ArgumentException("Стержень " + source + ": не заданы физический диаметр или высота.");
                    var radius = bar.Placement.ModelDiameterMm / 2;
                    var path = bar.Centerline;
                    if (path == null || path.Count == 0) path = new List<CenterlineSegment> { new CenterlineSegment
                    { Start = new Point3(bar.Start.X, bar.Start.Y, bar.Placement.ElevationMm), End = new Point3(bar.End.X, bar.End.Y, bar.Placement.ElevationMm) } };
                    if (path.Count > CompositePlanValidator.MaximumPieces) throw new LimitException();
                    V? previous = null;
                    bool containmentFailure = false, uncertain = false;
                    foreach (var segment in path)
                    {
                        token.ThrowIfCancellationRequested();
                        if (segment == null) throw new ArgumentException("Отсутствует участок оси стержня " + source + ".");
                        var a = new V(segment.Start); var b = new V(segment.End);
                        if (!a.Valid || !b.Valid || (a - b).Length <= Epsilon
                            || previous.HasValue && (previous.Value - a).Length > Epsilon)
                            throw new ArgumentException("Ось стержня " + source + " вырождена, разорвана или содержит недопустимые координаты.");
                        previous = b;
                        var points = new List<V> { a, b };
                        double minZ = Math.Min(a.Z, b.Z), maxZ = Math.Max(a.Z, b.Z), error = 0;
                        if (segment.Midpoint.HasValue)
                            points = SampleArc(a, new V(segment.Midpoint.Value), b, radius, token, out minZ, out maxZ, out error);
                        samples += points.Count;
                        if (samples > CompositePlanValidator.MaximumPieces) throw new LimitException();
                        if (minZ - radius < -thicknessMm + bottomCoverMm - Epsilon || maxZ + radius > -topCoverMm + Epsilon)
                            containmentFailure = true;
                        var coordinates = points.Select(p => new Coordinate(p.X, p.Y)).ToArray();
                        Geometry projected = coordinates.All(c => c.Equals2D(coordinates[0]))
                            ? (Geometry)GeometryKernel.Factory.CreatePoint(coordinates[0]) : GeometryKernel.Factory.CreateLineString(coordinates);
                        if (!prepared.Covers(projected)) { containmentFailure = true; continue; }
                        var clearance = sideCoverMm + radius;
                        var distance = boundary.Distance(projected);
                        if (distance < clearance - error - Epsilon) containmentFailure = true;
                        else if (error > 0 && distance <= clearance + error + Epsilon) uncertain = true;
                        else if (error == 0 && distance < clearance - Epsilon) containmentFailure = true;
                    }
                    if (containmentFailure) result.Add(Error("ACTUAL_BAR_CONTAINMENT", "Фактический стержень " + source + " выходит из бетона или нарушает полный защитный слой с учётом физического радиуса.", source));
                    else if (uncertain) result.Add(Error("ACTUAL_CURVE_COVER_UNCERTAIN", "Зазор фактической дуги стержня " + source + " не подтверждён с точностью 0.01 мм; создание не принято.", source));
                    if (result.Count >= 100)
                    {
                        result.Add(Error("ACTUAL_CONTAINMENT_LIMIT", "Обнаружено не менее 100 нарушений защитного слоя. Проверка остановлена."));
                        return result;
                    }
                }
            }
            catch (LimitException) { result.Add(Error("ACTUAL_CONTAINMENT_LIMIT", "Превышен лимит стержней, контуров или точек при проверке фактического защитного слоя.")); }
            catch (ArgumentException ex) { result.Add(Error("ACTUAL_CONTAINMENT_INVALID", ex.Message)); }
            catch (TopologyException) { result.Add(Error("ACTUAL_CONTAINMENT_INVALID", "Не удалось проверить геометрию фактических стержней относительно бетона.")); }
            return result;
        }

        private static List<V> SampleArc(V a, V m, V b, double barRadius, CancellationToken token,
            out double minZ, out double maxZ, out double error)
        {
            if (!m.Valid) throw new ArgumentException("Дуга содержит недопустимые координаты.");
            var u = m - a; var v = b - a; var n = V.Cross(u, v); var n2 = n.Squared;
            if (u.Length <= Epsilon || n2 <= u.Squared * v.Squared * 1e-20) throw new ArgumentException("Три точки дуги вырождены.");
            var center = a + (V.Cross(v, n) * u.Squared + V.Cross(n, u) * v.Squared) / (2 * n2);
            var radius = (a - center).Length;
            if (!Finite(radius) || radius <= barRadius || radius > 1e9) throw new ArgumentException("Недопустимый радиус дуги.");
            var x = (a - center) / radius; var y = V.Cross(n / Math.Sqrt(n2), x);
            double Angle(V p) { var d = p - center; return PositiveAngle(Math.Atan2(V.Dot(d, y), V.Dot(d, x))); }
            var endAngle = Angle(b); var midAngle = Angle(m);
            var sweep = midAngle <= endAngle ? endAngle : endAngle - 2 * Math.PI;
            var step = 4 * Math.Asin(Math.Sqrt(Math.Min(1, ArcToleranceMm / (2 * radius))));
            var needed = Math.Ceiling(Math.Abs(sweep) / step);
            if (!Finite(needed) || needed > CompositePlanValidator.MaximumPieces) throw new LimitException();
            int count = Math.Max(1, (int)needed);
            error = 2 * radius * Math.Pow(Math.Sin(Math.Abs(sweep) / count / 4), 2);
            minZ = Math.Min(a.Z, b.Z); maxZ = Math.Max(a.Z, b.Z);
            // Exact vertical extrema avoid falsely consuming cover at horizontal arc
            // tangencies merely because a sampled polyline has a finite sagitta.
            var theta = Math.Atan2(y.Z, x.Z);
            foreach (var candidate in new[] { theta, theta + Math.PI })
            {
                var offset = sweep >= 0 ? PositiveAngle(candidate) : -PositiveAngle(-candidate);
                if (Math.Abs(offset) > Math.Abs(sweep) + 1e-12) continue;
                var z = center.Z + radius * (x.Z * Math.Cos(offset) + y.Z * Math.Sin(offset));
                minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
            }
            var points = new List<V>(count + 1) { a };
            for (int i = 1; i < count; i++)
            {
                if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                var angle = sweep * i / count;
                points.Add(center + (x * Math.Cos(angle) + y * Math.Sin(angle)) * radius);
            }
            points.Add(b);
            // A vertical circular arc projects to one exact XY interval. Its 3D
            // sagitta does not reduce side cover. Include exact planar extrema so a
            // major arc cannot lose an excursion between its endpoints/samples.
            // A projected ellipse thinner than 1/100 of the contact tolerance is
            // equivalent for this check, including harmless Revit frame roundoff.
            if (2 * radius * Math.Abs(n.Z) / Math.Sqrt(n2) <= Epsilon / 100)
            {
                foreach (var extremum in new[] { Math.Atan2(y.X, x.X), Math.Atan2(y.Y, x.Y) })
                    foreach (var candidate in new[] { extremum, extremum + Math.PI })
                    {
                        var offset = sweep >= 0 ? PositiveAngle(candidate) : -PositiveAngle(-candidate);
                        if (Math.Abs(offset) <= Math.Abs(sweep) + 1e-12)
                            points.Add(center + (x * Math.Cos(offset) + y * Math.Sin(offset)) * radius);
                    }
                error = 0;
            }
            return points;
        }
        private static double PositiveAngle(double angle) { angle %= 2 * Math.PI; return angle < 0 ? angle + 2 * Math.PI : angle; }
        private static bool Finite(double value) => GeometryKernel.Finite(value);
        private static PlanDiagnostic Error(string code, string message, string id = null) => new PlanDiagnostic { Code = code, Message = message, SourceId = id, IsBlocking = true };
        private sealed class LimitException : Exception { }
        private readonly struct V
        {
            public V(double x, double y, double z) { X = x; Y = y; Z = z; }
            public V(Point3 p) : this(p.X, p.Y, p.Z) { }
            public double X { get; }
            public double Y { get; }
            public double Z { get; }
            public double Squared => X * X + Y * Y + Z * Z;
            public double Length => Math.Sqrt(Squared);
            public bool Valid => Finite(X) && Finite(Y) && Finite(Z) && Math.Max(Math.Abs(X), Math.Max(Math.Abs(Y), Math.Abs(Z))) <= 1e9;
            public static V operator +(V a, V b) => new V(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            public static V operator -(V a, V b) => new V(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            public static V operator *(V a, double b) => new V(a.X * b, a.Y * b, a.Z * b);
            public static V operator /(V a, double b) => a * (1 / b);
            public static double Dot(V a, V b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            public static V Cross(V a, V b) => new V(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }
    }
}
