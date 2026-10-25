using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SlabReinforcement.Core;

namespace SlabReinforcement.Integration
{
    internal sealed class SlabGeometrySnapshot
    {
        public Floor Host { get; set; }
        public SlabFrame Frame { get; set; }
        public List<PolygonInput> Slabs { get; set; }
        public double ThicknessMm { get; set; }
        public double TopCoverMm { get; set; }
        public double BottomCoverMm { get; set; }
        public double SideCoverMm { get; set; }
        public string AxisDescription { get; set; }
    }

    /// <summary>Reads all Revit geometry on the caller's Revit API thread.</summary>
    internal static class SlabGeometryReader
    {
        private const double MillimetresPerFoot = 304.8;
        private const double PlaneToleranceMm = 0.05;
        private const double CurveToleranceMm = 0.05;
        private const double ParallelTolerance = 1e-8;

        public static SlabGeometrySnapshot Read(Document document, Floor floor, Reference edgeReference)
        {
            if (document == null) throw new ArgumentNullException(nameof(document), "Откройте проект и запустите команду снова.");
            if (floor == null) throw new ArgumentNullException(nameof(floor), "Не найдена выбранная плита. Выберите плиту заново.");
            if (edgeReference == null || !edgeReference.ElementId.Equals(floor.Id))
                throw new InvalidOperationException("Выберите прямое ребро выбранной плиты для направления X.");

            var edge = floor.GetGeometryObjectFromReference(edgeReference) as Edge;
            var line = edge == null ? null : edge.AsCurve() as Line;
            if (line == null || !line.IsBound)
                throw new InvalidOperationException("Направление X должно быть задано прямым ребром плиты.");

            using (var compound = floor.FloorType.GetCompoundStructure())
            {
                if (compound == null || compound.VariableLayerIndex >= 0)
                    throw new InvalidOperationException("Нужна плита постоянной толщины.");
            }

            var topFaces = ReadPlanarFaces(floor, HostObjectUtils.GetTopFaces(floor), "верхней");
            var top = topFaces[0];
            var normal = top.FaceNormal.Normalize();
            if (normal.Z < 0) normal = normal.Negate();
            if (normal.Z <= 1e-6)
                throw new InvalidOperationException("Почти вертикальная плита не поддерживается.");

            foreach (var face in topFaces)
                RequireSamePlane(face, top.Origin, normal, "Верх плиты должен лежать в одной плоскости.");

            var edgeStart = ProjectToPlane(line.GetEndPoint(0), top.Origin, normal);
            var edgeEnd = ProjectToPlane(line.GetEndPoint(1), top.Origin, normal);
            SlabFrame frame;
            try
            {
                frame = SlabFrame.FromEdge(ToPoint(edgeStart), ToPoint(edgeEnd),
                    new Point3(normal.X, normal.Y, normal.Z));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException("Выбранное ребро почти перпендикулярно плите. Выберите ребро вдоль её контура.", ex);
            }

            var slabs = ReadFootprints(topFaces, frame, 0);
            if (slabs.Count == 0)
                throw new InvalidOperationException("Не удалось получить замкнутый верхний контур плиты.");

            var bottomFaces = ReadPlanarFaces(floor, HostObjectUtils.GetBottomFaces(floor), "нижней");
            var bottomOrigin = bottomFaces[0].Origin;
            foreach (var face in bottomFaces)
                RequireSamePlane(face, bottomOrigin, normal, "Низ плиты должен быть плоским и параллельным верху.");

            // Measure along the slab normal, including for a sloped floor.
            var thickness = top.Origin.Subtract(bottomOrigin).DotProduct(normal) * MillimetresPerFoot;
            if (double.IsNaN(thickness) || double.IsInfinity(thickness) || thickness <= PlaneToleranceMm)
                throw new InvalidOperationException("Не удалось определить толщину плиты. Проверьте её верхнюю и нижнюю поверхности.");

            var bottomFootprints = ReadFootprints(bottomFaces, frame, -thickness);
            slabs = SlabFootprint.MatchNormalExtrusion(slabs, bottomFootprints, CurveToleranceMm);
            using (var options = new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false })
            {
                var geometry = floor.get_Geometry(options);
                if (geometry == null || ValidateExtrusionFaces(geometry, normal, top.Origin, bottomOrigin, 0) == 0)
                    throw new InvalidOperationException("Не удалось проверить боковые грани плиты. Создание арматуры отменено.");
            }

            var topCover = ReadCover(document, floor, BuiltInParameter.CLEAR_COVER_TOP, "верхний");
            var bottomCover = ReadCover(document, floor, BuiltInParameter.CLEAR_COVER_BOTTOM, "нижний");
            var sideCover = ReadCover(document, floor, BuiltInParameter.CLEAR_COVER_OTHER, "боковой");
            if (topCover + bottomCover >= thickness)
                throw new InvalidOperationException("Нет места между защитными слоями. Проверьте их толщину и толщину плиты.");

            var angle = Math.Atan2(frame.XAxis.Y, frame.XAxis.X) * 180 / Math.PI;
            var slope = Math.Acos(Math.Min(1, Math.Max(-1, frame.Normal.Z))) * 180 / Math.PI;
            return new SlabGeometrySnapshot
            {
                Host = floor,
                Frame = frame,
                Slabs = slabs,
                ThicknessMm = thickness,
                TopCoverMm = topCover,
                BottomCoverMm = bottomCover,
                SideCoverMm = sideCover,
                AxisDescription = string.Format(CultureInfo.CurrentCulture,
                    "X: выбранное ребро ({0:0.##}°); Y: поперёк; уклон плиты {1:0.##}°", angle, slope)
            };
        }

        public static Point3 ToPoint(XYZ point)
        {
            return new Point3(point.X * MillimetresPerFoot, point.Y * MillimetresPerFoot, point.Z * MillimetresPerFoot);
        }

        public static XYZ ToXyz(Point3 point)
        {
            return new XYZ(point.X / MillimetresPerFoot, point.Y / MillimetresPerFoot, point.Z / MillimetresPerFoot);
        }

        private static List<PolygonInput> ReadFootprints(IEnumerable<PlanarFace> faces, SlabFrame frame, double elevationMm)
        {
            var result = new List<PolygonInput>();
            foreach (var face in faces)
            {
                var loops = new List<IEnumerable<Point2>>();
                foreach (var loop in face.GetEdgesAsCurveLoops())
                {
                    var points = new List<Point2>();
                    foreach (var curve in loop)
                    {
                        var samples = SampleCurve(curve);
                        // The final point belongs to the next edge, or closes this loop.
                        for (int i = 0; i < samples.Count - 1; i++)
                        {
                            var local = frame.ToLocal(ToPoint(samples[i]));
                            if (Math.Abs(local.Z - elevationMm) > PlaneToleranceMm)
                                throw new InvalidOperationException("Контур плиты должен лежать в одной плоскости.");
                            points.Add(new Point2(local.X, local.Y));
                        }
                    }
                    loops.Add(points);
                }
                // Each Revit face owns its holes. Union in the core removes internal seams.
                result.AddRange(PolygonTopology.FromLoops(loops));
            }
            return result;
        }

        private static int ValidateExtrusionFaces(GeometryElement geometry, XYZ normal, XYZ topOrigin, XYZ bottomOrigin, int depth)
        {
            if (depth > 16)
                throw new InvalidOperationException("Геометрия плиты слишком сложная для чтения. Упростите модель плиты.");
            var solidCount = 0;
            foreach (GeometryObject item in geometry)
            {
                var instance = item as GeometryInstance;
                if (instance != null)
                {
                    using (var placed = instance.GetInstanceGeometry())
                        solidCount += ValidateExtrusionFaces(placed, normal, topOrigin, bottomOrigin, depth + 1);
                    continue;
                }
                var solid = item as Solid;
                if (solid == null || solid.Faces.Size == 0 || solid.Volume <= 0) continue;
                solidCount++;
                foreach (Face face in solid.Faces)
                {
                    var planar = face as PlanarFace;
                    if (planar != null)
                    {
                        var faceNormal = planar.FaceNormal.Normalize();
                        if (faceNormal.CrossProduct(normal).GetLength() <= ParallelTolerance)
                        {
                            var distanceToTop = Math.Abs(planar.Origin.Subtract(topOrigin).DotProduct(normal)) * MillimetresPerFoot;
                            var distanceToBottom = Math.Abs(planar.Origin.Subtract(bottomOrigin).DotProduct(normal)) * MillimetresPerFoot;
                            if (Math.Min(distanceToTop, distanceToBottom) <= PlaneToleranceMm) continue;
                        }
                        else if (Math.Abs(faceNormal.DotProduct(normal)) <= ParallelTolerance) continue;
                    }
                    var cylinder = face as CylindricalFace;
                    if (cylinder != null && cylinder.Axis.Normalize().CrossProduct(normal).GetLength() <= ParallelTolerance)
                        continue;
                    throw new InvalidOperationException("Торцы плиты должны быть перпендикулярны её поверхности. "
                        + "Скосы пока не поддерживаются.");
                }
            }
            return solidCount;
        }

        private static List<PlanarFace> ReadPlanarFaces(Floor floor, IList<Reference> references, string side)
        {
            var result = new List<PlanarFace>();
            foreach (var reference in references)
            {
                var face = floor.GetGeometryObjectFromReference(reference) as PlanarFace;
                if (face == null)
                    throw new InvalidOperationException("У " + side + " поверхности плиты есть неплоская грань. Нужна плоская поверхность.");
                result.Add(face);
            }
            if (result.Count == 0)
                throw new InvalidOperationException("Не удалось получить грани " + side + " поверхности плиты.");
            return result;
        }

        private static void RequireSamePlane(PlanarFace face, XYZ origin, XYZ normal, string message)
        {
            var angularDifference = face.FaceNormal.Normalize().CrossProduct(normal).GetLength();
            var distance = Math.Abs(face.Origin.Subtract(origin).DotProduct(normal)) * MillimetresPerFoot;
            if (angularDifference > ParallelTolerance || distance > PlaneToleranceMm)
                throw new InvalidOperationException(message);
        }

        private static XYZ ProjectToPlane(XYZ point, XYZ origin, XYZ normal)
        {
            return point.Subtract(normal.Multiply(point.Subtract(origin).DotProduct(normal)));
        }

        private static double ReadCover(Document document, Floor floor, BuiltInParameter parameterId, string label)
        {
            var parameter = floor.get_Parameter(parameterId);
            var cover = parameter == null || parameter.StorageType != StorageType.ElementId
                ? null : document.GetElement(parameter.AsElementId()) as RebarCoverType;
            if (cover == null || double.IsNaN(cover.CoverDistance) || double.IsInfinity(cover.CoverDistance) || cover.CoverDistance < 0)
                throw new InvalidOperationException("В свойствах плиты проверьте " + label + " защитный слой арматуры.");
            return cover.CoverDistance * MillimetresPerFoot;
        }

        private static List<XYZ> SampleCurve(Curve curve)
        {
            if (curve is Line && curve.IsBound)
                return new List<XYZ> { curve.GetEndPoint(0), curve.GetEndPoint(1) };

            var arc = curve as Arc;
            if (arc == null)
                throw new InvalidOperationException("Сплайны и эллипсы не поддерживаются. Нужны прямые участки и дуги окружности.");

            // Revit's Tessellate() is only display-accurate. Bound the sagitta explicitly instead.
            var radiusMm = arc.Radius * MillimetresPerFoot;
            var sweep = arc.IsBound ? arc.Length / arc.Radius : 2 * Math.PI;
            var halfAngle = Math.Acos(Math.Max(-1, Math.Min(1, 1 - CurveToleranceMm / radiusMm)));
            if (halfAngle <= 0 || double.IsNaN(halfAngle))
                throw new InvalidOperationException("Дуга контура слишком пологая для точного расчёта. Упростите контур плиты.");
            var requiredSegments = Math.Ceiling(sweep / (2 * halfAngle));
            if (requiredSegments > 10000)
                throw new InvalidOperationException("Дуга контура слишком сложная для расчёта. Упростите контур плиты.");
            var segments = Math.Max(4, (int)requiredSegments);
            var result = new List<XYZ>(segments + 1);
            for (int i = 0; i <= segments; i++)
                result.Add(arc.IsBound
                    ? arc.Evaluate((double)i / segments, true)
                    : arc.Evaluate(2 * Math.PI * i / segments, false));
            return result;
        }
    }
}
