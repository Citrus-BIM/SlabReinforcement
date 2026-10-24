using System;
using System.Collections.Generic;
using System.Linq;
using SlabReinforcement.Core;

namespace SlabReinforcement.Dxf
{
    public static class DxfVertexAlignment
    {
        public static int Validate(IEnumerable<Point3> expected, IEnumerable<Point3> placed, double toleranceMm)
        {
            if (double.IsNaN(toleranceMm) || double.IsInfinity(toleranceMm) || toleranceMm <= 0) throw new ArgumentOutOfRangeException(nameof(toleranceMm));
            var sourcePoints=expected.ToList();var placedPoints=placed.ToList();
            int count=Match(sourcePoints,placedPoints,toleranceMm);
            Match(placedPoints,sourcePoints,toleranceMm);
            return count;
        }
        private static int Match(IEnumerable<Point3> expected, IEnumerable<Point3> placed, double toleranceMm)
        {
            var actual = new Dictionary<Cell, List<Point3>>();
            foreach (var point in placed)
            {
                var cell = new Cell(point, toleranceMm);
                if (!actual.TryGetValue(cell, out var bucket)) actual[cell] = bucket = new List<Point3>();
                bucket.Add(point);
            }
            if (actual.Count == 0) throw new ArgumentException("В подложке нет сетки для проверки исходного DXF.");
            var seen = new HashSet<Cell>();
            foreach (var point in expected)
            {
                if (!seen.Add(new Cell(point, 0.000001))) continue;
                var cell = new Cell(point, toleranceMm);
                bool found = false;
                for (int dx = -1; dx <= 1 && !found; dx++)
                    for (int dy = -1; dy <= 1 && !found; dy++)
                        for (int dz = -1; dz <= 1 && !found; dz++)
                            if (actual.TryGetValue(cell.Offset(dx, dy, dz), out var bucket))
                                foreach (var candidate in bucket)
                                    if (DistanceSquared(point, candidate) <= toleranceMm * toleranceMm) { found = true; break; }
                if (!found) throw new ArgumentException("Исходный DXF не совпадает полностью с геометрией выбранной подложки (допуск " + toleranceMm + " мм). Проверьте файл, единицы, масштаб, состав и обновление связи. Расчёт отменён.");
            }
            if (seen.Count < 3) throw new ArgumentException("В DXF недостаточно различных вершин для проверки размещения.");
            return seen.Count;
        }
        private static double DistanceSquared(Point3 a, Point3 b) => (a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y)+(a.Z-b.Z)*(a.Z-b.Z);
        private readonly struct Cell : IEquatable<Cell>
        {
            private readonly long x, y, z;
            public Cell(Point3 point, double size)
            {
                foreach (var v in new[] { point.X, point.Y, point.Z }) if (double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v/size)>long.MaxValue-2) throw new ArgumentException("Недопустимые координаты DXF.");
                x = (long)Math.Floor(point.X/size); y = (long)Math.Floor(point.Y/size); z = (long)Math.Floor(point.Z/size);
            }
            private Cell(long a, long b, long c) { x=a;y=b;z=c; }
            public Cell Offset(int dx,int dy,int dz)=>new Cell(x+dx,y+dy,z+dz);
            public bool Equals(Cell other)=>x==other.x&&y==other.y&&z==other.z;
            public override bool Equals(object value)=>value is Cell other&&Equals(other);
            public override int GetHashCode()=>unchecked((x.GetHashCode()*397 ^ y.GetHashCode())*397 ^ z.GetHashCode());
        }
    }
}
