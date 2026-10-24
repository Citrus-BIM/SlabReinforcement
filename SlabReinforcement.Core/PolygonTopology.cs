using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Geometries;

namespace SlabReinforcement.Core
{
    public static class PolygonTopology
    {
        public static List<PolygonInput> FromLoops(IEnumerable<IEnumerable<Point2>> loops)
        {
            if (loops == null) throw new ArgumentException("Контуры плиты не заданы.", nameof(loops));
            var polygons = new List<Polygon>();
            var vertexCount = 0;
            foreach (var loop in loops)
            {
                if (polygons.Count >= GeometryKernel.MaximumPolygons)
                    throw new ArgumentException("Слишком много контуров плиты.", nameof(loops));
                var ring = GeometryKernel.ReadRing(loop);
                vertexCount += ring.NumPoints;
                if (vertexCount > GeometryKernel.MaximumVertices)
                    throw new ArgumentException("Слишком много вершин контуров плиты.", nameof(loops));
                polygons.Add(GeometryKernel.Factory.CreatePolygon(ring));
            }

            // Winding is unreliable for imported/CAD loops. Strict containment defines
            // the parent, and even/odd nesting defines shells, holes and islands.
            polygons = polygons.OrderByDescending(p => p.Area)
                .ThenBy(p => p.EnvelopeInternal.MinX).ThenBy(p => p.EnvelopeInternal.MinY).ToList();
            var parent = Enumerable.Repeat(-1, polygons.Count).ToArray();
            var depth = new int[polygons.Count];
            for (var i = 0; i < polygons.Count; i++)
            {
                for (var j = 0; j < i; j++)
                {
                    if (!polygons[i].EnvelopeInternal.Intersects(polygons[j].EnvelopeInternal)) continue;
                    if (polygons[i].Boundary.Intersects(polygons[j].Boundary))
                        throw new ArgumentException("Контуры плиты пересекаются, совпадают или касаются друг друга.", nameof(loops));
                    if (polygons[j].Contains(polygons[i]) && (parent[i] < 0 || polygons[j].Area < polygons[parent[i]].Area))
                        parent[i] = j;
                }
                depth[i] = parent[i] < 0 ? 0 : depth[parent[i]] + 1;
            }

            var result = new List<PolygonInput>();
            for (var i = 0; i < polygons.Count; i++)
            {
                if (depth[i] % 2 != 0) continue;
                var shell = new PolygonInput { Shell = GeometryKernel.ToPoints(polygons[i].ExteriorRing, false) };
                for (var j = i + 1; j < polygons.Count; j++)
                    if (parent[j] == i)
                        shell.Holes.Add(GeometryKernel.ToPoints(polygons[j].ExteriorRing, true));
                result.Add(shell);
            }
            return result;
        }
    }
}
