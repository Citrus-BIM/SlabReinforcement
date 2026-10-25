using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using NetTopologySuite.Geometries;

namespace SlabReinforcement.Core
{
    public static class BackgroundCoverageValidator
    {
        /// <summary>
        /// Guards uniform nominal background credit over selected demand inside the host.
        /// Uses actual straight bars and at most half a specified pitch on either side;
        /// deliberately does not trim demand to the inset allowed for bar centrelines.
        /// Material, face, elevation, end detailing and As values must be checked by caller.
        /// </summary>
        public static List<PlanDiagnostic> Validate(IEnumerable<FaceInput> faces, IEnumerable<PolygonInput> slabs,
            IEnumerable<PlannedBar> backgroundBars, double spacingMm, bool alongX, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (faces == null || slabs == null || backgroundBars == null || !GeometryKernel.Finite(spacingMm) || spacingMm <= 0)
                    throw new ArgumentException("Для проверки фона нужны контур плиты, стержни и шаг больше нуля.");
                var regions = new List<Geometry>();
                var hosts = new List<Geometry>();
                long vertices = 0;
                foreach (var face in faces)
                {
                    token.ThrowIfCancellationRequested();
                    if (face?.Vertices == null) throw new ArgumentException("Не найден контур участка расчётной подложки.");
                    vertices += face.Vertices.Count;
                    if (regions.Count >= GeometryKernel.MaximumFaces || vertices > GeometryKernel.MaximumVertices)
                        throw new ArgumentException("Слишком много участков или точек подложки. Уменьшите выбранную область.");
                    regions.Add(GeometryKernel.Factory.CreatePolygon(GeometryKernel.ReadRing(face.Vertices)));
                }
                foreach (var slab in slabs)
                {
                    token.ThrowIfCancellationRequested();
                    if (slab?.Shell == null || slab.Holes == null || slab.Holes.Any(h => h == null))
                        throw new ArgumentException("Не заданы контуры плиты.");
                    vertices += slab.Shell.Count + slab.Holes.Sum(h => (long)h.Count);
                    if (hosts.Count >= GeometryKernel.MaximumPolygons || vertices > GeometryKernel.MaximumVertices)
                        throw new ArgumentException("Слишком много контуров или точек. Уменьшите выбранную область.");
                    hosts.Add(GeometryKernel.ReadPolygon(slab));
                }
                var groups = new List<BarGroup>();
                foreach (var bar in backgroundBars)
                {
                    token.ThrowIfCancellationRequested();
                    if (bar == null || (bar.Centerline != null && bar.Centerline.Count > 0))
                        throw new ArgumentException("Вычесть фон можно только по прямым стержням выбранного направления.");
                    if (groups.Count >= CompositePlanValidator.MaximumBars) throw new ArgumentException("Слишком много стержней фона для проверки. Уменьшите выбранную область.");
                    var transverseA = alongX ? bar.Start.Y : bar.Start.X;
                    var transverseB = alongX ? bar.End.Y : bar.End.X;
                    var a = alongX ? bar.Start.X : bar.Start.Y; var b = alongX ? bar.End.X : bar.End.Y;
                    if (!GeometryKernel.Finite(a) || !GeometryKernel.Finite(b) || !GeometryKernel.Finite(transverseA) || !GeometryKernel.Finite(transverseB)
                        || Math.Abs(transverseA - transverseB) > GeometryKernel.Epsilon || Math.Abs(a - b) <= GeometryKernel.Epsilon)
                        throw new ArgumentException("Стержень фона направлен иначе, чем выбранное направление зон.");
                    groups.Add(new BarGroup { Start = a <= b ? bar.Start : bar.End, End = a <= b ? bar.End : bar.Start, Count = 1, SpacingMm = spacingMm });
                }
                token.ThrowIfCancellationRequested();
                var demand = GeometryKernel.AreaOnly(GeometryKernel.Intersect(GeometryKernel.Union(regions), GeometryKernel.Union(hosts)));
                if (demand.IsEmpty) return new List<PlanDiagnostic> { Error("BACKGROUND_COVERAGE_EMPTY", "Выбранные участки подложки находятся вне плиты. Фон не вычтен.") };
                var missing = DemandCoverage.Uncovered(demand, groups, spacingMm, alongX, token, true);
                if (missing.Area <= GeometryKernel.Epsilon * GeometryKernel.Epsilon) return new List<PlanDiagnostic>();
                return new List<PlanDiagnostic> { Error("BACKGROUND_COVERAGE_GAP", "Фон не вычтен: есть участки без фона. Их площадь "
                    + missing.Area.ToString("G4", CultureInfo.CurrentCulture) + " мм²; участков " + GeometryKernel.Polygons(missing).Count() + ".") };
            }
            catch (ArgumentException ex) { return new List<PlanDiagnostic> { Error("BACKGROUND_COVERAGE_INVALID", ex.Message) }; }
            catch (TopologyException) { return new List<PlanDiagnostic> { Error("BACKGROUND_COVERAGE_INVALID", "Фон не проверен. Проверьте контур плиты и существующие стержни.") }; }
        }
        private static PlanDiagnostic Error(string code, string message) => new PlanDiagnostic { Code = code, Message = message, IsBlocking = true };
    }
}
