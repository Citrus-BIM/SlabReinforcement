using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace SlabReinforcement.Core
{
    internal sealed class LineDemand
    {
        internal ReinforcementSpec Spec;
        internal Geometry Region;
    }

    // A line is a connected longitudinal slice of the selected, host-clipped map.
    // Connectivity uses geometric precision only, never the endpoint merge tolerance.
    internal sealed class ConnectedLineDemandNormalizer
    {
        private const double Epsilon = GeometryKernel.Epsilon;
        private readonly List<LineDemand> sources;
        private readonly bool alongX;
        private readonly Dictionary<string, ReinforcementSpec> representatives;
        private readonly STRtree<LineDemand> index = new STRtree<LineDemand>();
        private readonly Dictionary<double, List<OwnedInterval>> rows = new Dictionary<double, List<OwnedInterval>>();
        private readonly bool equivalent;
        private readonly Envelope extent = new Envelope();
        internal bool RequiresOwnership => !equivalent;

        internal ConnectedLineDemandNormalizer(List<LineDemand> sources, bool alongX)
        {
            this.sources = sources;
            this.alongX = alongX;
            representatives = new Dictionary<string, ReinforcementSpec>(StringComparer.Ordinal);
            foreach (var group in sources.GroupBy(d => new {
                TypeKey = string.IsNullOrWhiteSpace(d.Spec.TypeKey) ? null : d.Spec.TypeKey,
                IndividualKey = string.IsNullOrWhiteSpace(d.Spec.TypeKey) ? d.Spec.Key : null,
                d.Spec.MaterialKey, d.Spec.DiameterMm, ModelDiameter = d.Spec.EffectiveModelDiameterMm,
                d.Spec.SpacingMm, d.Spec.AnchorageMm, d.Spec.ElevationMm }))
            {
                var representative = Rank(group.Select(d => d.Spec)).First();
                foreach (var member in group) representatives[member.Spec.Key] = representative;
            }
            equivalent = representatives.Values.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() <= 1;
            foreach (var source in sources)
            {
                extent.ExpandToInclude(source.Region.EnvelopeInternal);
                foreach (var polygon in GeometryKernel.Polygons(source.Region))
                    index.Insert(polygon.EnvelopeInternal, new LineDemand { Spec = source.Spec, Region = polygon });
            }
            index.Build();
        }

        internal List<LineDemand> Normalize(ReinforcementPlan result, CancellationToken token)
        {
            if (sources.Count == 0) return new List<LineDemand>();
            var selected = GeometryKernel.Union(sources.Select(s => s.Region));
            if (equivalent)
            {
                var winner = representatives[sources[0].Spec.Key];
                ValidateWinner(winner, sources.Select(s => s.Spec), result);
                return new List<LineDemand> { new LineDemand { Spec = winner, Region = selected } };
            }
            // Noded boundaries include sloping intersections, as well as input and
            // host-clipping vertices. Between two levels, slice connectivity is fixed.
            var boundaries = GeometryKernel.Union(sources.Select(s => s.Region.Boundary));
            var levels = boundaries.Coordinates.Select(c => alongX ? c.Y : c.X).Distinct().OrderBy(v => v).ToArray();
            if (levels.Length > 200000) return TooLarge(result);
            var pieces = new Dictionary<string, List<Geometry>>(StringComparer.Ordinal);
            var winners = new Dictionary<string, ReinforcementSpec>(StringComparer.Ordinal);
            int pieceCount = 0;
            var extent = selected.EnvelopeInternal;
            double left = (alongX ? extent.MinX : extent.MinY) - 1;
            double right = (alongX ? extent.MaxX : extent.MaxY) + 1;
            for (int i = 1; i < levels.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                double low = levels[i - 1], high = levels[i];
                if (high <= low) continue;
                var bandEnvelope = alongX ? new Envelope(left, right, low, high) : new Envelope(low, high, left, right);
                var candidates = index.Query(bandEnvelope);
                if (candidates.Count == 0) continue;
                var band = GeometryKernel.Factory.ToGeometry(bandEnvelope);
                // Work on locally indexed polygons instead of all source faces at every level.
                var strip = GeometryKernel.AreaOnly(GeometryKernel.Intersect(
                    GeometryKernel.Union(candidates.Select(c => c.Region)), band));
                foreach (var component in GeometryKernel.Polygons(strip))
                {
                    token.ThrowIfCancellationRequested();
                    var members = candidates.Where(c => c.Region.EnvelopeInternal.Intersects(component.EnvelopeInternal)
                        && GeometryKernel.Intersect(c.Region, component).Area > Epsilon * Epsilon).Select(c => c.Spec).Distinct().ToList();
                    if (members.Count == 0) continue;
                    var winner = representatives[Rank(members).First().Key];
                    ValidateWinner(winner, members, result);
                    if (!pieces.TryGetValue(winner.Key, out var list)) pieces[winner.Key] = list = new List<Geometry>();
                    list.Add(component);
                    if (++pieceCount > 200000) return TooLarge(result);
                    winners[winner.Key] = winner;
                }
            }
            return pieces.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => {
                token.ThrowIfCancellationRequested();
                return new LineDemand { Spec = winners[p.Key], Region = GeometryKernel.AreaOnly(GeometryKernel.Union(p.Value)) };
            }).ToList();
        }

        internal List<OwnedInterval> AtRow(double row)
        {
            if (rows.TryGetValue(row, out var cached)) return cached;
            double min = alongX ? extent.MinX : extent.MinY, max = alongX ? extent.MaxX : extent.MaxY;
            var line = GeometryKernel.Factory.CreateLineString(new[] { Coordinate(min - 1, row), Coordinate(max + 1, row) });
            var intervals = new List<OwnedInterval>();
            foreach (var source in index.Query(line.EnvelopeInternal))
                Collect(GeometryKernel.Intersect(source.Region, line), source.Spec, intervals);
            var connected = new List<OwnedInterval>();
            foreach (var interval in intervals.OrderBy(s => s.Start).ThenBy(s => s.End))
            {
                var previous = connected.LastOrDefault();
                // Preserve every positive gap in the same canonical geometry used
                // by the polygon overlay; a tolerance merge would invent ownership
                // across a slot that Normalize correctly kept disconnected.
                if (previous == null || interval.Start > previous.End)
                    connected.Add(new OwnedInterval { Start = interval.Start, End = interval.End, Spec = interval.Spec });
                else
                {
                    previous.End = Math.Max(previous.End, interval.End);
                    previous.Spec = Rank(new[] { previous.Spec, interval.Spec }).First();
                }
            }
            foreach (var interval in connected) interval.Spec = representatives[interval.Spec.Key];
            rows[row] = connected;
            return connected;
        }

        internal bool HasOtherOwner(double row, double start, double end, string key) => !equivalent
            && AtRow(row).Any(i => i.Spec.Key != key && Math.Min(end, i.End) - Math.Max(start, i.Start) > Epsilon);

        internal void ValidateFinalNumericCoverage(ReinforcementPlan result, CancellationToken token)
        {
            // Removed singleton trials cannot provide numerical credit. Adjacent
            // final types may contribute, using their actual pitch and same steel
            // class, only where their assigned area satisfies this source deficit.
            var specs = sources.ToDictionary(d => d.Spec.Key, d => d.Spec, StringComparer.Ordinal);
            var groups = result.Groups.GroupBy(g => g.SpecKey).ToList();
            foreach (var source in sources.Where(d => d.Spec.RequiredAreaMm2PerM.HasValue))
            {
                token.ThrowIfCancellationRequested();
                Geometry remaining = source.Region;
                foreach (var assigned in groups)
                {
                    token.ThrowIfCancellationRequested();
                    var spec = specs[assigned.Key];
                    if (spec.MaterialKey != source.Spec.MaterialKey
                        || Math.PI * spec.DiameterMm * spec.DiameterMm * 250 / spec.SpacingMm + Epsilon < source.Spec.RequiredAreaMm2PerM.Value) continue;
                    remaining = DemandCoverage.Uncovered(remaining, assigned.ToList(), spec.SpacingMm, alongX, token, true);
                    if (remaining.Area <= Epsilon * Epsilon) break;
                }
                if (remaining.Area > Epsilon * Epsilon)
                    result.Diagnostics.Add(new PlanDiagnostic { Code = "NUMERIC_FINAL_COVERAGE_GAP", SourceId = source.Spec.Key, IsBlocking = true,
                        Message = "После пропуска одиночных стержней арматуры недостаточно. Измените шаг или границы зон." });
            }
        }

        private static List<LineDemand> TooLarge(ReinforcementPlan result)
        {
            result.Diagnostics.Add(new PlanDiagnostic { Code = "PLAN_TOO_LARGE", IsBlocking = true,
                Message = "Выбранная подложка слишком сложная для расчёта зон. Уменьшите выбранную область." });
            return new List<LineDemand>();
        }

        private void Collect(Geometry geometry, ReinforcementSpec spec, List<OwnedInterval> output)
        {
            if (geometry is LineString line && !line.IsEmpty)
            {
                var e = line.EnvelopeInternal;
                double start = alongX ? e.MinX : e.MinY, end = alongX ? e.MaxX : e.MaxY;
                // Even a very narrow positive source interval participates in the
                // assignment. Minimum bar length is checked on the full connected
                // interval later, after the winner has been selected.
                if (end > start) output.Add(new OwnedInterval { Start = start, End = end, Spec = spec });
            }
            else if (geometry is GeometryCollection collection)
                for (int i = 0; i < collection.NumGeometries; i++) Collect(collection.GetGeometryN(i), spec, output);
        }

        private Coordinate Coordinate(double value, double row) => alongX ? new Coordinate(value, row) : new Coordinate(row, value);
        private static IOrderedEnumerable<ReinforcementSpec> Rank(IEnumerable<ReinforcementSpec> specs) => specs
            .OrderByDescending(s => s.DiameterMm).ThenBy(s => s.SpacingMm)
            .ThenBy(s => s.TypeKey ?? "", StringComparer.Ordinal).ThenBy(s => s.Key, StringComparer.Ordinal);

        private static void ValidateWinner(ReinforcementSpec winner, IEnumerable<ReinforcementSpec> sources, ReinforcementPlan result)
        {
            double area = Math.PI * winner.DiameterMm * winner.DiameterMm * 250 / winner.SpacingMm;
            foreach (var source in sources.Where(s => s.RequiredAreaMm2PerM.HasValue))
            {
                string code = source.MaterialKey != winner.MaterialKey ? "LINE_ASSIGNMENT_MATERIAL_MISMATCH"
                    : source.RequiredAreaMm2PerM.Value > area + Epsilon ? "LINE_ASSIGNMENT_AREA_DEFICIT" : null;
                if (code == null || result.Diagnostics.Any(d => d.Code == code && d.SourceId == source.Key)) continue;
                result.Diagnostics.Add(new PlanDiagnostic { Code = code, SourceId = source.Key, IsBlocking = true,
                    Message = code == "LINE_ASSIGNMENT_MATERIAL_MISMATCH"
                        ? "В ряду изменился класс стали. Пересчитайте требуемую площадь арматуры."
                        : "Наибольшего диаметра с выбранным шагом недостаточно. Уменьшите шаг этого типа." });
            }
        }

        internal sealed class OwnedInterval
        {
            internal double Start, End;
            internal ReinforcementSpec Spec;
        }
    }
}
