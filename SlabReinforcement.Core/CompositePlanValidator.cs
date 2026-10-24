using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace SlabReinforcement.Core
{
    public static class CompositePlanValidator
    {
        public const int MaximumBars = 200000;
        public const int MaximumPieces = 1000000;
        public const int MaximumCandidatePairs = 2000000;
        public const int MaximumBroadPhaseVisits = 10000000;
        public const double ArcToleranceMm = 0.02;
        private const double ContactToleranceMm = 0.000001;

        /// <summary>
        /// Validates already expanded bars in one slab-local millimetre frame. Centerline,
        /// when supplied, is absolute 3D geometry; Placement.ElevationMm is used only for
        /// Start/End straight bars. The first existingBarCount entries describe existing
        /// bars: only their interactions with new bars are checked. Does not infer
        /// material, anchorage, holes, capacity or a bar's self-intersection.
        /// </summary>
        public static List<PlanDiagnostic> Validate(IEnumerable<PlannedBar> bars, CancellationToken cancellationToken = default, int existingBarCount = 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bars == null) throw new ArgumentNullException(nameof(bars));
            if (existingBarCount < 0) throw new ArgumentOutOfRangeException(nameof(existingBarCount));
            var diagnostics = new List<PlanDiagnostic>();
            var input = new List<PlannedBar>();
            foreach (var bar in bars)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (input.Count == MaximumBars)
                    return new List<PlanDiagnostic> { Error("COMPOSITE_PLAN_TOO_LARGE", "Число стержней превышает 200 000.") };
                input.Add(bar);
            }
            if (existingBarCount > input.Count) throw new ArgumentOutOfRangeException(nameof(existingBarCount));

            var pieces = new List<Piece>();
            for (var i = 0; i < input.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { Expand(input[i], i, pieces, cancellationToken); }
                catch (InvalidGeometryException ex)
                {
                    diagnostics.Add(Error("INVALID_COMPOSITE_BAR", "Стержень " + (i + 1) + ": " + ex.Message, (i + 1).ToString(CultureInfo.InvariantCulture)));
                }
                catch (PlanLimitException)
                {
                    diagnostics.Add(Error("COMPOSITE_PLAN_TOO_LARGE", "Число участков для проверки превышает 1 000 000."));
                    return diagnostics;
                }
            }
            // Invalid bars must never disappear from a partially successful combined result.
            if (diagnostics.Count > 0) return diagnostics;
            if (input.Count <= 1 || existingBarCount == input.Count) return diagnostics;

            var index = new STRtree<Piece>();
            foreach (var piece in pieces) index.Insert(piece.Bounds, piece);
            index.Build();
            var collisions = new HashSet<long>();
            var uncertain = new HashSet<long>();
            var candidates = 0;
            var visited = 0;
            foreach (var piece in pieces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (piece.Bar < existingBarCount) continue;
                foreach (var other in index.Query(piece.Bounds))
                {
                    if ((++visited & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    // Include same-bar and already diagnosed pairs in the work bound:
                    // a densely folded curve must not cause quadratic unbounded work.
                    if (visited > MaximumBroadPhaseVisits)
                    {
                        AddPairDiagnostics(diagnostics, collisions, uncertain);
                        diagnostics.Add(Error("COMPOSITE_PLAN_TOO_LARGE", "Проверка остановлена: более 10 000 000 пересекающихся габаритов участков. Полная проверка коллизий не завершена."));
                        return diagnostics;
                    }
                    if (other.Bar >= piece.Bar || !piece.OverlapsZ(other)) continue;
                    var key = (long)piece.Bar * MaximumBars + other.Bar;
                    if (collisions.Contains(key)) continue;
                    if (++candidates > MaximumCandidatePairs)
                    {
                        AddPairDiagnostics(diagnostics, collisions, uncertain);
                        diagnostics.Add(Error("COMPOSITE_PLAN_TOO_LARGE", "Проверка остановлена: более 2 000 000 близких пар участков. Полная проверка коллизий не завершена."));
                        return diagnostics;
                    }
                    if ((candidates & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var result = Intersects(piece, other);
                    if (result == Intersection.Collision) { collisions.Add(key); uncertain.Remove(key); }
                    else if (result == Intersection.Uncertain) uncertain.Add(key);
                }
            }
            AddPairDiagnostics(diagnostics, collisions, uncertain);
            return diagnostics;
        }

        private static void AddPairDiagnostics(List<PlanDiagnostic> diagnostics, HashSet<long> collisions, HashSet<long> uncertain)
        {
            if (collisions.Count > 0)
                diagnostics.Add(Error("COMPOSITE_BAR_COLLISION", "Объёмное пересечение стержней: " + collisions.Count + " пар. " + Examples(collisions)));
            if (uncertain.Count > 0)
                diagnostics.Add(Error("COMPOSITE_CURVE_CLEARANCE_UNCERTAIN", "Не подтверждён зазор у кривых или торцов: " + uncertain.Count + " пар. Точность аппроксимации дуги — 0.02 мм. " + Examples(uncertain)));
        }

        private static string Examples(IEnumerable<long> keys) => "Номера в плане: " + string.Join(", ", keys.OrderBy(k => k).Take(8)
            .Select(k => (k % MaximumBars + 1).ToString(CultureInfo.InvariantCulture) + "/" + (k / MaximumBars + 1).ToString(CultureInfo.InvariantCulture))) + ".";

        private static PlanDiagnostic Error(string code, string message, string sourceId = null) => new PlanDiagnostic { Code = code, Message = message, SourceId = sourceId, IsBlocking = true };

        private static void Expand(PlannedBar bar, int id, List<Piece> pieces, CancellationToken token)
        {
            if (bar?.Placement == null || !Finite(bar.Placement.ModelDiameterMm)
                || bar.Placement.ModelDiameterMm <= ContactToleranceMm * 4 || bar.Placement.ModelDiameterMm > 1e9)
                throw new InvalidGeometryException("не задан положительный физический диаметр.");
            var radius = bar.Placement.ModelDiameterMm / 2;
            if (!Finite(bar.Placement.ElevationMm)) throw new InvalidGeometryException("высота не является конечным числом.");
            if (bar.Centerline == null || bar.Centerline.Count == 0)
            {
                var start = new V(bar.Start.X, bar.Start.Y, bar.Placement.ElevationMm);
                var end = new V(bar.End.X, bar.End.Y, bar.Placement.ElevationMm);
                ValidateLine(start, end);
                AddPiece(new Piece(id, start, end, radius, 0), pieces);
                return;
            }
            if (bar.Centerline.Count > MaximumPieces) throw new PlanLimitException();
            V? previous = null;
            foreach (var segment in bar.Centerline)
            {
                token.ThrowIfCancellationRequested();
                if (segment == null) throw new InvalidGeometryException("отсутствует участок оси.");
                var start = new V(segment.Start); var end = new V(segment.End);
                ValidateLine(start, end);
                if (previous.HasValue && (previous.Value - start).Length > ContactToleranceMm)
                    throw new InvalidGeometryException("участки оси не соединены.");
                previous = end;
                if (!segment.Midpoint.HasValue) AddPiece(new Piece(id, start, end, radius, 0), pieces);
                else ExpandArc(id, start, new V(segment.Midpoint.Value), end, radius, pieces, token);
            }
        }

        private static void ValidateLine(V start, V end)
        {
            if (!start.IsFinite || !end.IsFinite || (end - start).Length <= ContactToleranceMm)
                throw new InvalidGeometryException("ось содержит нечисловые координаты или нулевую длину.");
            // Coordinates farther than 1,000 km from the local origin compromise the
            // requested micrometre contact tolerance; require a proper local frame.
            if (start.MaxAbs > 1e9 || end.MaxAbs > 1e9)
                throw new InvalidGeometryException("координаты превышают допустимый диапазон локальной системы ±1 000 000 000 мм.");
        }

        private static void ExpandArc(int id, V start, V middle, V end, double radius, List<Piece> pieces, CancellationToken token)
        {
            if (!middle.IsFinite || middle.MaxAbs > 1e9) throw new InvalidGeometryException("неверная промежуточная точка дуги.");
            var a = middle - start; var b = end - start; var cross = V.Cross(a, b);
            var crossSquared = cross.LengthSquared;
            if (a.Length <= ContactToleranceMm || crossSquared <= a.LengthSquared * b.LengthSquared * 1e-20)
                throw new InvalidGeometryException("три точки дуги совпадают или лежат на одной прямой.");
            var center = start + (V.Cross(b, cross) * a.LengthSquared + V.Cross(cross, a) * b.LengthSquared) / (2 * crossSquared);
            var curveRadius = (start - center).Length;
            if (!center.IsFinite || !Finite(curveRadius) || curveRadius <= radius || curveRadius > 1e9)
                throw new InvalidGeometryException("радиус дуги должен превышать радиус стержня.");
            var x = (start - center) / curveRadius;
            var normal = cross / Math.Sqrt(crossSquared);
            var y = V.Cross(normal, x);
            double Angle(V point) { var v = point - center; var value = Math.Atan2(V.Dot(v, y), V.Dot(v, x)); return value < 0 ? value + Math.PI * 2 : value; }
            var endAngle = Angle(end); var midAngle = Angle(middle);
            var sweep = midAngle <= endAngle ? endAngle : endAngle - 2 * Math.PI;
            // Stable sagitta formula even for very large circle radii.
            var angularStep = 4 * Math.Asin(Math.Sqrt(Math.Min(1, ArcToleranceMm / (2 * curveRadius))));
            var needed = Math.Ceiling(Math.Abs(sweep) / angularStep);
            if (!Finite(needed) || needed > MaximumPieces - pieces.Count) throw new PlanLimitException();
            var count = Math.Max(1, (int)needed);
            var error = 2 * curveRadius * Math.Pow(Math.Sin(Math.Abs(sweep) / count / 4), 2);
            var previous = start;
            for (var i = 1; i <= count; i++)
            {
                if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                var theta = sweep * i / count;
                var next = i == count ? end : center + (x * Math.Cos(theta) + y * Math.Sin(theta)) * curveRadius;
                AddPiece(new Piece(id, previous, next, radius, error), pieces);
                previous = next;
            }
        }

        private static void AddPiece(Piece piece, List<Piece> pieces)
        {
            if (pieces.Count == MaximumPieces) throw new PlanLimitException();
            pieces.Add(piece);
        }

        private static Intersection Intersects(Piece a, Piece b)
        {
            var distance = SegmentDistance(a.A, a.B, b.A, b.B, out var alongA, out var alongB);
            var radii = a.Radius + b.Radius;
            var error = a.Error + b.Error;
            if (distance >= radii + error - ContactToleranceMm) return Intersection.Separate;
            // An interior common perpendicular certifies penetration without numerical
            // support iteration, also for very long bars with a tiny crossing angle.
            if (distance < radii - error - ContactToleranceMm
                && alongA * a.Length > ContactToleranceMm && (1 - alongA) * a.Length > ContactToleranceMm
                && alongB * b.Length > ContactToleranceMm && (1 - alongB) * b.Length > ContactToleranceMm)
                return Intersection.Collision;
            // Parallel finite cylinders have an exact elementary test (including flat ends).
            if (error == 0 && V.Cross(a.Direction, b.Direction).LengthSquared < 1e-20)
            {
                var start = V.Dot(b.A - a.A, a.Direction); var end = V.Dot(b.B - a.A, a.Direction);
                var overlap = Math.Min(a.Length, Math.Max(start, end)) - Math.Max(0, Math.Min(start, end));
                return overlap > ContactToleranceMm && distance < radii - ContactToleranceMm ? Intersection.Collision : Intersection.Separate;
            }
            var outer = ConvexIntersection(a, b, error > 0 ? 1 : 0);
            if (outer == Intersection.Separate) return Intersection.Separate;
            if (error == 0) return outer;
            // Chord capsules inflated by sagitta enclose the real curved tube. Contracted
            // chord capsules certify penetration; the intervening band cannot be accepted.
            var inner = ConvexIntersection(a, b, -1);
            return inner == Intersection.Collision ? Intersection.Collision : Intersection.Uncertain;
        }

        // GJK distance/intersection of finite cylinders (straight pieces) or capsules (arc
        // chords). Shrinking each solid by the contact tolerance permits surface contact.
        // Failure to prove either outcome is blocking, never silently called clear.
        private static Intersection ConvexIntersection(Piece a, Piece b, int inflation)
        {
            var origin = (a.A + a.B + b.A + b.B) / 4;
            var scale = Math.Max(1, Math.Max(Math.Max(a.Length, b.Length), (a.A - b.A).Length));
            V Support(V direction) => (a.Support(direction, inflation, origin) - b.Support(-direction, inflation, origin)) / scale;
            var direction = (b.A + b.B - a.A - a.B) / 2;
            if (direction.LengthSquared < 1e-20) direction = new V(1, 0.13, 0.071);
            var simplex = new List<V> { Support(direction) };
            var closest = simplex[0];
            var threshold = Math.Max(1e-14, ContactToleranceMm * 0.05 / scale);
            for (var iteration = 0; iteration < 96; iteration++)
            {
                if (closest.Length <= threshold) return Intersection.Collision;
                var next = Support(-closest);
                if (V.Dot(next, closest) > threshold * closest.Length) return Intersection.Separate;
                if (simplex.Any(p => (p - next).Length <= threshold)) return Intersection.Uncertain;
                simplex.Add(next);
                closest = ClosestToOrigin(simplex);
            }
            return Intersection.Uncertain;
        }

        // Enumerating simplex faces is tiny (at most 15) and avoids orientation-dependent
        // tetrahedron branches at near-coplanar cylinder contacts.
        private static V ClosestToOrigin(List<V> simplex)
        {
            var best = simplex[0]; var bestSquared = best.LengthSquared; var bestMask = 1;
            var count = simplex.Count;
            for (var mask = 1; mask < (1 << count); mask++)
            {
                var selected = new List<V>(4);
                for (var i = 0; i < count; i++) if ((mask & (1 << i)) != 0) selected.Add(simplex[i]);
                if (!ProjectOrigin(selected, out var point)) continue;
                var squared = point.LengthSquared;
                if (squared < bestSquared) { best = point; bestSquared = squared; bestMask = mask; }
            }
            for (var i = count - 1; i >= 0; i--) if ((bestMask & (1 << i)) == 0) simplex.RemoveAt(i);
            return best;
        }

        private static bool ProjectOrigin(List<V> vertices, out V point)
        {
            point = vertices[0];
            if (vertices.Count == 1) return true;
            var n = vertices.Count - 1;
            if (n > 3) return false;
            var edges = new V[n]; var matrix = new double[n, n + 1];
            for (var i = 0; i < n; i++) edges[i] = vertices[i + 1] - vertices[0];
            for (var i = 0; i < n; i++)
            {
                for (var j = 0; j < n; j++) matrix[i, j] = V.Dot(edges[i], edges[j]);
                matrix[i, n] = -V.Dot(edges[i], vertices[0]);
            }
            for (var i = 0; i < n; i++)
            {
                var pivot = i;
                for (var j = i + 1; j < n; j++) if (Math.Abs(matrix[j, i]) > Math.Abs(matrix[pivot, i])) pivot = j;
                if (Math.Abs(matrix[pivot, i]) < 1e-24) return false;
                for (var j = i; j <= n; j++) { var temp = matrix[i, j]; matrix[i, j] = matrix[pivot, j]; matrix[pivot, j] = temp; }
                var factor = matrix[i, i];
                for (var j = i; j <= n; j++) matrix[i, j] /= factor;
                for (var row = 0; row < n; row++) if (row != i)
                {
                    factor = matrix[row, i];
                    for (var column = i; column <= n; column++) matrix[row, column] -= factor * matrix[i, column];
                }
            }
            var sum = 0.0;
            for (var i = 0; i < n; i++) { var weight = matrix[i, n]; if (weight < -1e-12) return false; sum += weight; point += edges[i] * weight; }
            return sum <= 1 + 1e-12;
        }

        private static double SegmentDistance(V p, V q, V r, V s, out double t, out double z)
        {
            var u = q - p; var v = s - r; var w = p - r;
            var a = V.Dot(u, u); var b = V.Dot(u, v); var c = V.Dot(v, v); var d = V.Dot(u, w); var e = V.Dot(v, w);
            // Cross products avoid cancellation in a*c-b*b for nearly parallel lines.
            var cross = V.Cross(u, v); var denominator = cross.LengthSquared;
            t = denominator > 0 ? Clamp(V.Dot(V.Cross(v, w), cross) / denominator) : 0;
            z = (b * t + e) / c;
            if (z < 0) { z = 0; t = Clamp(-d / a); }
            else if (z > 1) { z = 1; t = Clamp((b - d) / a); }
            return (w + u * t - v * z).Length;
        }

        private static double Clamp(double value) => Math.Max(0, Math.Min(1, value));
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private enum Intersection { Separate, Collision, Uncertain }
        private sealed class InvalidGeometryException : Exception { public InvalidGeometryException(string message) : base(message) { } }
        private sealed class PlanLimitException : Exception { }

        private sealed class Piece
        {
            public Piece(int bar, V a, V b, double radius, double error)
            {
                Bar = bar; A = a; B = b; Radius = radius; Error = error;
                Length = (b - a).Length; Direction = (b - a) / Length;
                Bounds = new Envelope(Math.Min(a.X, b.X), Math.Max(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y));
                Bounds.ExpandBy(radius + error);
                MinZ = Math.Min(a.Z, b.Z) - radius - error; MaxZ = Math.Max(a.Z, b.Z) + radius + error;
            }
            public int Bar { get; }
            public V A { get; }
            public V B { get; }
            public V Direction { get; }
            public double Length { get; }
            public double Radius { get; }
            public double Error { get; }
            public Envelope Bounds { get; }
            private double MinZ { get; }
            private double MaxZ { get; }
            public bool OverlapsZ(Piece other) => Math.Min(MaxZ, other.MaxZ) - Math.Max(MinZ, other.MinZ) > ContactToleranceMm;
            public V Support(V vector, int inflation, V origin)
            {
                var radius = Math.Max(0, Radius + inflation * Error - ContactToleranceMm);
                var dot = V.Dot(vector, Direction);
                var center = (A - origin + B - origin) / 2;
                var half = Math.Max(0, Length / 2 - (Error == 0 ? ContactToleranceMm : 0));
                var point = center + Direction * (dot >= 0 ? half : -half);
                var radial = Error > 0 ? vector : vector - Direction * dot;
                var length = radial.Length;
                return length <= 1e-30 ? point : point + radial * (radius / length);
            }
        }

        private readonly struct V
        {
            public V(double x, double y, double z) { X = x; Y = y; Z = z; }
            public V(Point3 p) : this(p.X, p.Y, p.Z) { }
            public double X { get; }
            public double Y { get; }
            public double Z { get; }
            public double LengthSquared => X * X + Y * Y + Z * Z;
            public double Length => Math.Sqrt(LengthSquared);
            public double MaxAbs => Math.Max(Math.Abs(X), Math.Max(Math.Abs(Y), Math.Abs(Z)));
            public bool IsFinite => Finite(X) && Finite(Y) && Finite(Z);
            public static V operator +(V a, V b) => new V(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            public static V operator -(V a, V b) => new V(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            public static V operator -(V a) => new V(-a.X, -a.Y, -a.Z);
            public static V operator *(V a, double b) => new V(a.X * b, a.Y * b, a.Z * b);
            public static V operator /(V a, double b) => a * (1 / b);
            public static double Dot(V a, V b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            public static V Cross(V a, V b) => new V(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }
    }
}
