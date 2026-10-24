using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;

namespace SlabReinforcement.Core
{
    internal static class GeometryKernel
    {
        internal const double Epsilon = 1e-6;
        internal const int MaximumVertices = 1000000;
        internal const int MaximumFaces = 200000;
        internal const int MaximumPolygons = 10000;
        internal static readonly GeometryFactory Factory = new GeometryFactory();

        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        internal static LinearRing ReadRing(IEnumerable<Point2> source)
        {
            if (source == null) throw new ArgumentException("Не задан контур.");
            var points = new List<Coordinate>();
            foreach (var point in source)
            {
                if (!Finite(point.X) || !Finite(point.Y) || Math.Abs(point.X) > 1e10 || Math.Abs(point.Y) > 1e10)
                    throw new ArgumentException("Контур содержит недопустимые координаты.");
                if (points.Count >= MaximumVertices) throw new ArgumentException("В контуре слишком много вершин.");
                // Canonical 1e-6 mm input grid matches Epsilon. Revit frame transforms
                // produce ~1e-11 mm noise at nominal grid boundaries; leaving it in a
                // ring can remove a complete boundary row from an exact NTS slice.
                // Round before removing duplicates and checking validity, so collapsed
                // edges or holes are rejected rather than silently repaired.
                var x = Math.Round(point.X, 6, MidpointRounding.AwayFromZero);
                var y = Math.Round(point.Y, 6, MidpointRounding.AwayFromZero);
                if (points.Count == 0 || x != points[points.Count - 1].X || y != points[points.Count - 1].Y)
                    points.Add(new Coordinate(x, y));
            }
            if (points.Count > 1 && points[0].Equals2D(points[points.Count - 1])) points.RemoveAt(points.Count - 1);
            if (points.Count < 3) throw new ArgumentException("Для контура нужны не менее трёх различных вершин.");
            points.Add(points[0].Copy());
            var ring = Factory.CreateLinearRing(points.ToArray());
            var polygon = Factory.CreatePolygon(ring);
            if (!ring.IsSimple || !polygon.IsValid || polygon.Area <= Epsilon * Epsilon)
                throw new ArgumentException("Контур вырожден или содержит самопересечение.");
            return ring;
        }

        internal static Polygon ReadPolygon(PolygonInput input)
        {
            if (input == null) throw new ArgumentException("Не задан полигон плиты.");
            var shell = ReadRing(input.Shell);
            if (input.Holes == null) throw new ArgumentException("Не задан список отверстий плиты.");
            var holes = input.Holes.Select(ReadRing).ToArray();
            var polygon = Factory.CreatePolygon(shell, holes);
            if (!polygon.IsValid) throw new ArgumentException("Отверстия пересекаются или выходят за наружный контур плиты.");
            return polygon;
        }

        internal static Geometry Union(IEnumerable<Geometry> geometries)
        {
            var list = geometries.ToList();
            if (list.Count == 0) return Factory.CreatePolygon();
            return OverlayNGRobust.Union(list, Factory);
        }

        internal static Geometry Intersect(Geometry a, Geometry b) => OverlayNGRobust.Overlay(a, b, SpatialFunction.Intersection);
        internal static Geometry Difference(Geometry a, Geometry b) => OverlayNGRobust.Overlay(a, b, SpatialFunction.Difference);

        // Area overlays can also contain isolated touching points/lines. They carry no
        // reinforcement demand and must not enter OverlayNG as mixed-dimension input.
        internal static Geometry AreaOnly(Geometry geometry) => geometry is Polygon || geometry is MultiPolygon
            ? geometry : Factory.CreateMultiPolygon(Polygons(geometry).ToArray());

        internal static IEnumerable<Polygon> Polygons(Geometry geometry)
        {
            if (geometry is Polygon polygon)
            {
                if (!polygon.IsEmpty && polygon.Area > Epsilon * Epsilon) yield return polygon;
            }
            else if (geometry is GeometryCollection collection)
            {
                for (var i = 0; i < collection.NumGeometries; i++)
                    foreach (var item in Polygons(collection.GetGeometryN(i))) yield return item;
            }
        }

        internal static List<PolygonInput> ToInputs(Geometry geometry) => Polygons(geometry)
            .OrderByDescending(p => p.Area).ThenBy(p => p.EnvelopeInternal.MinX).ThenBy(p => p.EnvelopeInternal.MinY)
            .Select(p => new PolygonInput
            {
                Shell = ToPoints(p.ExteriorRing, false),
                Holes = Enumerable.Range(0, p.NumInteriorRings).Select(p.GetInteriorRingN)
                    .OrderBy(r => r.EnvelopeInternal.MinX).ThenBy(r => r.EnvelopeInternal.MinY)
                    .Select(r => ToPoints(r, true)).ToList()
            }).ToList();

        internal static List<Point2> ToPoints(LineString ring, bool clockwise)
        {
            var points = ring.Coordinates.Take(ring.NumPoints - 1).Select(p => new Point2(p.X, p.Y)).ToList();
            var area = 0.0;
            for (var i = 0; i < points.Count; i++)
            {
                var next = points[(i + 1) % points.Count];
                area += points[i].X * next.Y - next.X * points[i].Y;
            }
            if ((area < 0) != clockwise) points.Reverse();
            var first = Enumerable.Range(0, points.Count).OrderBy(i => points[i].X).ThenBy(i => points[i].Y).First();
            return Enumerable.Range(0, points.Count).Select(i => points[(first + i) % points.Count]).ToList();
        }
    }
}
