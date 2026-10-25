using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SlabReinforcement.Core
{
    public sealed class CadFace
    {
        public string Id { get; set; }
        public string Layer { get; set; }
        public string ColorKey { get; set; }
        public List<Point3> Vertices { get; set; } = new List<Point3>();
    }
    public sealed class CadPlaneGroup
    {
        public string Key { get; set; }
        public string Layer { get; set; }
        public double ElevationMm { get; set; }
        public List<FaceInput> Faces { get; set; } = new List<FaceInput>();
    }
    public sealed class CadPlanarGroupResult
    {
        public List<CadPlaneGroup> Groups { get; set; } = new List<CadPlaneGroup>();
        public int NonPlanarFaceCount { get; set; }
        public int DegenerateFaceCount { get; set; }
    }
    public static class CadPlanarGroups
    {
        /// <summary>
        /// Splits imported model geometry into selectable CAD layers and parallel planes.
        /// Coordinates must already be expressed in the slab frame, in millimetres.
        /// Nothing is selected or assigned a reinforcement specification automatically.
        /// </summary>
        public static CadPlanarGroupResult Group(IEnumerable<CadFace> faces, double toleranceMm = 1)
        {
            if (faces == null) throw new ArgumentNullException(nameof(faces));
            if (!GeometryKernel.Finite(toleranceMm) || toleranceMm <= 0)
                throw new ArgumentOutOfRangeException(nameof(toleranceMm));
            var result = new CadPlanarGroupResult();
            var planar = new List<MeasuredFace>();
            foreach (var face in faces)
            {
                if (face == null || face.Vertices == null)
                    throw new ArgumentException("Не найдена часть расчётной подложки. Выберите подложку заново.", nameof(faces));
                if (face.Vertices.Any(p => !GeometryKernel.Finite(p.X) || !GeometryKernel.Finite(p.Y) || !GeometryKernel.Finite(p.Z)))
                    throw new ArgumentException("Не удалось прочитать координаты участка подложки: " + face.Id, nameof(faces));
                if (face.Vertices.Count < 3)
                {
                    result.DegenerateFaceCount++;
                    continue;
                }
                var min = face.Vertices.Min(p => p.Z);
                var max = face.Vertices.Max(p => p.Z);
                if (max - min > toleranceMm)
                {
                    result.NonPlanarFaceCount++;
                    continue;
                }
                // Translate to the first vertex to avoid area cancellation at large
                // survey coordinates. Projection changes neither X nor Y.
                var origin = face.Vertices[0];
                var twiceArea = 0.0;
                for (var i = 1; i + 1 < face.Vertices.Count; i++)
                    twiceArea += (face.Vertices[i].X - origin.X) * (face.Vertices[i + 1].Y - origin.Y)
                        - (face.Vertices[i].Y - origin.Y) * (face.Vertices[i + 1].X - origin.X);
                if (Math.Abs(twiceArea) <= 2 * GeometryKernel.Epsilon * GeometryKernel.Epsilon)
                {
                    result.DegenerateFaceCount++;
                    continue;
                }
                planar.Add(new MeasuredFace { Source = face, MinimumZ = min, MaximumZ = max,
                    Elevation = min + face.Vertices.Average(p => p.Z - min) });
            }
            foreach (var layer in planar.GroupBy(f => f.Source.Layer ?? string.Empty, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                CadPlaneGroup current = null;
                double minimumZ = 0;
                var ordinal = 0;
                foreach (var face in layer.OrderBy(f => f.MinimumZ).ThenBy(f => f.MaximumZ).ThenBy(f => f.Source.Id, StringComparer.Ordinal)
                    .ThenBy(f => f.Source.ColorKey, StringComparer.Ordinal))
                {
                    // Anchor the entire group's Z range to its first (lowest) face.
                    // Comparing to the previous face would chain unrelated levels.
                    if (current == null || face.MaximumZ - minimumZ > toleranceMm)
                    {
                        minimumZ = face.MinimumZ;
                        ordinal++;
                        current = new CadPlaneGroup
                        {
                            Layer = layer.Key, ElevationMm = face.Elevation,
                            Key = layer.Key.Length.ToString(CultureInfo.InvariantCulture) + ":" + layer.Key + "@" + minimumZ.ToString("R", CultureInfo.InvariantCulture)
                                + "#" + ordinal.ToString(CultureInfo.InvariantCulture)
                        };
                        result.Groups.Add(current);
                    }
                    current.Faces.Add(new FaceInput
                    {
                        Id = face.Source.Id, ColorKey = face.Source.ColorKey,
                        Vertices = face.Source.Vertices.Select(p => new Point2(p.X, p.Y)).ToList()
                    });
                }
            }
            return result;
        }

        private sealed class MeasuredFace
        {
            internal CadFace Source;
            internal double MinimumZ, MaximumZ, Elevation;
        }
    }
}
