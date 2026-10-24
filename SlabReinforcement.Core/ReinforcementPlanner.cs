using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Buffer;
using NetTopologySuite.Index.Quadtree;
using NetTopologySuite.Index.Strtree;

namespace SlabReinforcement.Core
{
    public static class ReinforcementPlanner
    {
        private const double Epsilon = GeometryKernel.Epsilon;
        private const int MaximumRows = 100000;
        private const int MaximumBars = 200000;
        private const int MaximumArrayCount = 1002;
        private static readonly double[] CutLengths = { 1170, 1300, 1460, 1670, 1950, 2340, 2925, 3900, 4875, 5850, 6825, 7800, 8775, 9750 };

        public static ReinforcementPlan Build(PlanRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new ReinforcementPlan();
            if (!ValidateRequest(request, result)) return result;
            try
            {
                BuildGeometry(request, result, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (PlanSizeException)
            {
                result.Bars.Clear();
                result.Groups.Clear();
                Add(result, "PLAN_TOO_LARGE", "Слишком большой план армирования. Увеличьте шаг или уменьшите выбранную область.");
            }
            catch (TopologyException)
            {
                result.Bars.Clear();
                result.Groups.Clear();
                Add(result, "GEOMETRY_OPERATION_FAILED", "Не удалось выполнить геометрические операции. Проверьте контуры плиты и граней подложки.");
            }
            return result;
        }

        private static bool ValidateRequest(PlanRequest request, ReinforcementPlan result)
        {
            if (request == null || request.Options == null || request.Slabs == null || request.Faces == null || request.Specifications == null)
            {
                Add(result, "INVALID_REQUEST", "Не заданы исходные данные расчёта армирования.");
                return false;
            }
            var options = request.Options;
            if (!GeometryKernel.Finite(options.GridOffsetMm) || !Nonnegative(options.EdgeCoverMm) || !Nonnegative(options.MergeToleranceMm)
                || !Positive(options.RoundIncrementMm) || !Positive(options.MinimumLengthMm))
                Add(result, "INVALID_OPTIONS", "Защитный слой и допуск объединения должны быть неотрицательными, округление и минимальная длина — положительными конечными числами.");
            if (request.Slabs.Count == 0) Add(result, "NO_SLAB", "Не выбран контур плиты.");
            if (request.Faces.Count == 0) Add(result, "NO_DEMAND", "Не выбраны цветные грани для армирования.");
            if (request.Slabs.Count > GeometryKernel.MaximumPolygons || request.Faces.Count > GeometryKernel.MaximumFaces
                || request.Specifications.Count > 1000)
                Add(result, "PLAN_TOO_LARGE", "Слишком много исходных контуров, граней или настроек армирования.");
            long vertexCount = 0;
            foreach (var slab in request.Slabs)
            {
                vertexCount += slab?.Shell?.Count ?? 0;
                if (slab?.Holes != null)
                    foreach (var hole in slab.Holes) vertexCount += hole?.Count ?? 0;
            }
            foreach (var face in request.Faces) vertexCount += face?.Vertices?.Count ?? 0;
            if (vertexCount > GeometryKernel.MaximumVertices)
                Add(result, "PLAN_TOO_LARGE", "Слишком много вершин в исходных контурах.");

            var keys = new HashSet<string>(StringComparer.Ordinal);
            var colors = new HashSet<string>(StringComparer.Ordinal);
            foreach (var spec in request.Specifications)
            {
                if (spec == null || string.IsNullOrWhiteSpace(spec.Key) || string.IsNullOrWhiteSpace(spec.ColorKey)
                    || string.IsNullOrWhiteSpace(spec.MaterialKey) || !Positive(spec.DiameterMm) || !Positive(spec.SpacingMm)
                    || (spec.ModelDiameterMm.HasValue && !Positive(spec.ModelDiameterMm.Value))
                    || spec.SpacingMm < spec.EffectiveModelDiameterMm
                    || !Nonnegative(spec.AnchorageMm) || !GeometryKernel.Finite(spec.DiameterMm * spec.DiameterMm / spec.SpacingMm))
                {
                    Add(result, "INVALID_SPECIFICATION", "Для каждого цвета задайте уникальный ключ, материал, положительные диаметр и шаг, неотрицательную анкеровку. Шаг не может быть меньше физического диаметра стержня.", spec?.Key);
                    continue;
                }
                if (!keys.Add(spec.Key) || !colors.Add(spec.ColorKey))
                    Add(result, "INVALID_SPECIFICATION", "Ключи настроек и цвета не должны повторяться.", spec.Key);
            }
            foreach (var face in request.Faces)
                if (face != null && (string.IsNullOrWhiteSpace(face.ColorKey) || !colors.Contains(face.ColorKey)))
                    Add(result, "UNMAPPED_COLOR", "Для цвета грани подложки не заданы параметры армирования.", face.Id);
            return !result.Diagnostics.Any(d => d.IsBlocking);
        }

        private static void BuildGeometry(PlanRequest request, ReinforcementPlan result, CancellationToken token)
        {
            var slabs = new List<Geometry>();
            for (var i = 0; i < request.Slabs.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                try { slabs.Add(GeometryKernel.ReadPolygon(request.Slabs[i])); }
                catch (ArgumentException ex) { Add(result, "INVALID_SLAB", "Некорректный контур плиты: " + ex.Message, i.ToString(CultureInfo.InvariantCulture)); }
            }
            var faces = new Dictionary<string, List<Geometry>>(StringComparer.Ordinal);
            foreach (var face in request.Faces.OrderBy(f => f?.ColorKey, StringComparer.Ordinal).ThenBy(f => f?.Id, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (face == null) throw new ArgumentException("Грань подложки не задана.");
                    var polygon = GeometryKernel.Factory.CreatePolygon(GeometryKernel.ReadRing(face.Vertices));
                    if (!faces.TryGetValue(face.ColorKey, out var items)) faces[face.ColorKey] = items = new List<Geometry>();
                    items.Add(polygon);
                }
                catch (ArgumentException ex) { Add(result, "INVALID_FACE", "Некорректная грань подложки: " + ex.Message, face?.Id); }
            }
            if (result.Diagnostics.Any(d => d.IsBlocking)) return;
            var host = GeometryKernel.Union(slabs);
            token.ThrowIfCancellationRequested();
            var demands = new List<SpecDemand>();
            foreach (var spec in request.Specifications.OrderByDescending(s => s.DiameterMm * s.DiameterMm / s.SpacingMm)
                .ThenBy(s => s.Key, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (!faces.TryGetValue(spec.ColorKey, out var items)) continue;
                var demand = GeometryKernel.AreaOnly(GeometryKernel.Intersect(GeometryKernel.Union(items), host));
                if (demand.IsEmpty || !GeometryKernel.Polygons(demand).Any())
                {
                    Add(result, "DEMAND_OUTSIDE_SLAB", "Все выбранные грани этого цвета находятся вне контура плиты.", spec.Key);
                    continue;
                }
                demands.Add(new SpecDemand { Spec = spec, Region = demand });
            }
            demands = MergeEquivalentDemands(demands, token);
            result.DemandRegions = GeometryKernel.ToInputs(GeometryKernel.Union(demands.Select(d => d.Region)));
            for (var i = 0; i < demands.Count; i++)
            {
                for (var j = i + 1; j < demands.Count; j++)
                {
                    token.ThrowIfCancellationRequested();
                    if (demands[i].Spec.MaterialKey == demands[j].Spec.MaterialKey
                        || !demands[i].Region.EnvelopeInternal.Intersects(demands[j].Region.EnvelopeInternal)) continue;
                    if (GeometryKernel.Intersect(demands[i].Region, demands[j].Region).Area > Epsilon * Epsilon)
                        Add(result, "INCOMPATIBLE_MATERIAL_OVERLAP", "В одной области пересекаются цвета с разными материалами арматуры. Устраните пересечение или назначьте одинаковый материал.", demands[i].Spec.Key + "/" + demands[j].Spec.Key);
                }
            }
            if (result.Diagnostics.Any(d => d.IsBlocking)) return;

            // Higher As = pi*d^2/(4*s) wins; the common factor is omitted.
            // Subtract the original stronger regions, not progressively altered faces.
            var occupiedByMaterial = new Dictionary<string, Geometry>(StringComparer.Ordinal);
            var groups = new List<WorkingGroup>();
            var allowedBySpec = new Dictionary<string, Geometry>(StringComparer.Ordinal);
            var clearanceBySpec = new Dictionary<string, double>(StringComparer.Ordinal);
            var demandBySpec = new Dictionary<string, Geometry>(StringComparer.Ordinal);
            foreach (var demand in demands)
            {
                token.ThrowIfCancellationRequested();
                var region = demand.Region;
                if (occupiedByMaterial.TryGetValue(demand.Spec.MaterialKey, out var occupied))
                    region = GeometryKernel.AreaOnly(GeometryKernel.Difference(region, occupied));
                occupiedByMaterial[demand.Spec.MaterialKey] = occupied == null
                    ? demand.Region : GeometryKernel.Union(new[] { occupied, demand.Region });
                if (region.IsEmpty || !GeometryKernel.Polygons(region).Any()) continue;
                var cover = request.Options.EdgeCoverMm + demand.Spec.EffectiveModelDiameterMm / 2;
                if (!GeometryKernel.Finite(cover))
                {
                    Add(result, "INVALID_OPTIONS", "Защитный слой вместе с радиусом арматуры выходит за допустимый диапазон.", demand.Spec.Key);
                    continue;
                }
                // Mitred corners conservatively preserve the full clearance around holes.
                var allowed = host.Buffer(-cover, new BufferParameters { JoinStyle = JoinStyle.Mitre, MitreLimit = 5, SimplifyFactor = 0 });
                allowedBySpec[demand.Spec.Key] = allowed;
                clearanceBySpec[demand.Spec.Key] = cover;
                var coveredDemand = GeometryKernel.AreaOnly(GeometryKernel.Intersect(region, allowed));
                // Numerical credit must not silently lose a connected thin tail when
                // the bar-clearance inset erases it. Manual geometry retains its scope.
                demandBySpec[demand.Spec.Key] = request.Options.CoverageHalfSpacing ? region : coveredDemand;
                foreach (var component in GeometryKernel.Polygons(region))
                {
                    token.ThrowIfCancellationRequested();
                    if (!GeometryKernel.Polygons(GeometryKernel.Intersect(component, allowed)).Any())
                        Add(result, "DEMAND_OUTSIDE_COVER", "Для части выбранной области не остаётся места после учёта защитного слоя и диаметра стержня.", demand.Spec.Key);
                }
                if (coveredDemand.IsEmpty) continue;
                var runs = CreateRuns(coveredDemand, allowed, demand.Spec, request.Options, result, token);
                groups.AddRange(CompleteGroups(runs, coveredDemand, allowed, demand.Spec, request.Options, result, token));
                if (groups.Sum(g => (long)g.Rows.Count) > MaximumBars) throw new PlanSizeException();
            }

            foreach (var group in groups.OrderBy(g => g.Spec.Key, StringComparer.Ordinal)
                .ThenBy(g => g.Rows[0].Row).ThenBy(g => g.Start).ThenBy(g => g.End))
                for (var offset = 0; offset < group.Rows.Count; offset += MaximumArrayCount)
                    result.Groups.Add(new BarGroup
                    {
                        Start = Point(group.Start, group.Rows[0].Row + offset * group.Spec.SpacingMm, request.Options.AlongX),
                        End = Point(group.End, group.Rows[0].Row + offset * group.Spec.SpacingMm, request.Options.AlongX),
                        Count = Math.Min(MaximumArrayCount, group.Rows.Count - offset),
                        SpacingMm = group.Spec.SpacingMm, SpecKey = group.Spec.Key, ColorKey = group.Spec.ColorKey
                    });

            // This is the only source of bars: preview and Revit arrays share this result.
            var hostBoundary = host.Boundary;
            foreach (var group in result.Groups)
            {
                token.ThrowIfCancellationRequested();
                for (var i = 0; i < group.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var x = request.Options.AlongX ? 0 : i * group.SpacingMm;
                    var y = request.Options.AlongX ? i * group.SpacingMm : 0;
                    var bar = new PlannedBar
                    {
                        Start = new Point2(group.Start.X + x, group.Start.Y + y),
                        End = new Point2(group.End.X + x, group.End.Y + y),
                        SpecKey = group.SpecKey, ColorKey = group.ColorKey
                    };
                    var row = request.Options.AlongX ? bar.Start.Y : bar.Start.X;
                    var start = request.Options.AlongX ? bar.Start.X : bar.Start.Y;
                    var end = request.Options.AlongX ? bar.End.X : bar.End.Y;
                    // One final check per actual expanded bar also verifies the array
                    // arithmetic and the physical clearance, independent of grouping.
                    if (!SegmentCovered(allowedBySpec[group.SpecKey], start, end, row, request.Options.AlongX)
                        || hostBoundary.Distance(Line(start, end, row, request.Options.AlongX)) < clearanceBySpec[group.SpecKey] - Epsilon)
                    {
                        result.Bars.Clear();
                        result.Groups.Clear();
                        Add(result, "FINAL_CLEARANCE_FAILED", "Итоговый стержень не проходит проверку защитного слоя возле края или отверстия. Создание армирования остановлено.", group.SpecKey);
                        return;
                    }
                    result.Bars.Add(bar);
                }
            }
            result.Bars = result.Bars.OrderBy(b => b.SpecKey, StringComparer.Ordinal)
                .ThenBy(b => request.Options.AlongX ? b.Start.Y : b.Start.X)
                .ThenBy(b => request.Options.AlongX ? b.Start.X : b.Start.Y).ToList();
            VerifyBarCollisions(result, request.Specifications, request.Options.AlongX, token);
            foreach (var demand in demands)
            {
                token.ThrowIfCancellationRequested();
                if (!demandBySpec.TryGetValue(demand.Spec.Key, out var region) || region.IsEmpty) continue;
                var remaining = DemandCoverage.Uncovered(region, result.Groups.Where(g => g.SpecKey == demand.Spec.Key).ToList(),
                    demand.Spec.SpacingMm, request.Options.AlongX, token, request.Options.CoverageHalfSpacing);
                if (remaining.Area > Epsilon * Epsilon)
                    Add(result, "UNCOVERED_DEMAND", "Не охвачены рядами стержней участки: " + GeometryKernel.Polygons(remaining).Count().ToString(CultureInfo.InvariantCulture)
                        + "; площадь " + remaining.Area.ToString("G4", CultureInfo.CurrentCulture) + " мм². Проверьте шаг и форму выбранной области.", demand.Spec.Key);
            }
            if (result.Bars.Count == 0 && !result.Diagnostics.Any(d => d.IsBlocking))
                Add(result, "NO_BARS", "По выбранным областям не удалось сформировать стержни. Проверьте размеры областей и настройки армирования.");
        }

        private static List<SpecDemand> MergeEquivalentDemands(List<SpecDemand> demands, CancellationToken token)
        {
            // The caller ordered demands by descending As and ordinal key. Keep the first
            // original spec as the representative, including its placement/color lookup key.
            // Numeric equality alone cannot prove that distinct Revit types are interchangeable.
            var equivalent = demands.GroupBy(d => new
            {
                TypeKey = string.IsNullOrWhiteSpace(d.Spec.TypeKey) ? null : d.Spec.TypeKey,
                IndividualKey = string.IsNullOrWhiteSpace(d.Spec.TypeKey) ? d.Spec.Key : null,
                d.Spec.MaterialKey,
                d.Spec.DiameterMm,
                ModelDiameterMm = d.Spec.EffectiveModelDiameterMm,
                d.Spec.SpacingMm,
                d.Spec.AnchorageMm
            });
            var result = new List<SpecDemand>();
            foreach (var group in equivalent)
            {
                token.ThrowIfCancellationRequested();
                var members = group.ToList();
                result.Add(members.Count == 1 ? members[0] : new SpecDemand
                {
                    Spec = members[0].Spec,
                    Region = GeometryKernel.AreaOnly(GeometryKernel.Union(members.Select(d => d.Region)))
                });
                token.ThrowIfCancellationRequested();
            }
            return result;
        }

        private static List<Run> CreateRuns(Geometry demand, Geometry allowed, ReinforcementSpec spec, PlanOptions options, ReinforcementPlan result, CancellationToken token)
        {
            var rows = new SortedSet<double>();
            foreach (var polygon in GeometryKernel.Polygons(demand))
            {
                token.ThrowIfCancellationRequested();
                var envelope = polygon.EnvelopeInternal;
                var min = options.AlongX ? envelope.MinY : envelope.MinX;
                var max = options.AlongX ? envelope.MaxY : envelope.MaxX;
                var firstIndex = Math.Ceiling((min - options.GridOffsetMm - Epsilon) / spec.SpacingMm);
                var lastIndex = Math.Floor((max - options.GridOffsetMm + Epsilon) / spec.SpacingMm);
                if (!GeometryKernel.Finite(firstIndex) || !GeometryKernel.Finite(lastIndex) || lastIndex - firstIndex > MaximumRows)
                    throw new PlanSizeException();
                var hit = false;
                var count = Math.Max(0, (int)(lastIndex - firstIndex + 1));
                for (var i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var row = options.GridOffsetMm + (firstIndex + i) * spec.SpacingMm;
                    if (!Slice(polygon, row, options.AlongX).Any(v => v.End - v.Start >= options.MinimumLengthMm - Epsilon)) continue;
                    rows.Add(row);
                    hit = true;
                }
                if (!hit)
                {
                    // A component narrower than a grid step must not vanish between rows.
                    var midpoint = (min + max) / 2;
                    if (Slice(polygon, midpoint, options.AlongX).Any(v => v.End - v.Start >= options.MinimumLengthMm - Epsilon))
                        rows.Add(midpoint);
                    else
                    {
                        var inside = polygon.InteriorPoint.Coordinate;
                        var row = options.AlongX ? inside.Y : inside.X;
                        if (Slice(polygon, row, options.AlongX).Any(v => v.End - v.Start >= options.MinimumLengthMm - Epsilon)) rows.Add(row);
                        else Add(result, "DEMAND_TOO_SMALL", "Часть выбранной области меньше минимальной длины стержня.", spec.Key);
                    }
                }
                if (rows.Count > MaximumRows) throw new PlanSizeException();
            }
            var runs = new List<Run>();
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                runs.AddRange(CreateRowRuns(demand, allowed, row, spec, options, result));
                if (runs.Count > MaximumBars) throw new PlanSizeException();
            }
            var uniqueRuns = new List<Run>();
            foreach (var run in runs.OrderBy(r => r.Row).ThenBy(r => r.Start).ThenBy(r => r.End))
            {
                var previous = uniqueRuns.Count == 0 ? null : uniqueRuns[uniqueRuns.Count - 1];
                if (previous == null || Math.Abs(run.Row - previous.Row) > Epsilon
                    || Math.Abs(run.Start - previous.Start) > Epsilon || Math.Abs(run.End - previous.End) > Epsilon)
                    uniqueRuns.Add(run);
            }
            return uniqueRuns;
        }

        private static List<Run> CreateRowRuns(Geometry demand, Geometry allowed, double row,
            ReinforcementSpec spec, PlanOptions options, ReinforcementPlan result, Geometry missing = null)
        {
            var runs = new List<Run>();
            var hostIntervals = Slice(allowed, row, options.AlongX);
            var demandIntervals = Slice(demand, row, options.AlongX);
            var missingIntervals = missing == null ? null : Slice(missing, row, options.AlongX);
            foreach (var host in hostIntervals)
            {
                var required = new List<Interval>();
                foreach (var interval in demandIntervals)
                {
                    var start = Math.Max(host.Start, interval.Start);
                    var end = Math.Min(host.End, interval.End);
                    if (end - start < options.MinimumLengthMm - Epsilon) continue;
                    // A supplemental row serves only a lost lobe. Do not duplicate
                    // unrelated, already covered intervals elsewhere on the same line.
                    if (missingIntervals != null && !missingIntervals.Any(m => Math.Min(end, m.End) - Math.Max(start, m.Start) > Epsilon)) continue;
                    var a = start - spec.AnchorageMm;
                    var b = end + spec.AnchorageMm;
                    if (a < host.Start - Epsilon || b > host.End + Epsilon)
                    {
                        Add(result, "ANCHORAGE_DOES_NOT_FIT", "Недостаточно места для полной анкеровки возле края плиты или отверстия. Уменьшите область усиления либо измените параметры.", spec.Key);
                        continue;
                    }
                    required.Add(new Interval(a, b));
                }
                foreach (var interval in MergeIntervals(required, options.MergeToleranceMm))
                {
                    if (!TryFit(interval.Start, interval.End, host.Start, host.End, options, out var start, out var end))
                    {
                        Add(result, "ROUNDING_DOES_NOT_FIT", "Округлённая длина стержня не помещается между краем плиты и отверстием с сохранением требуемой анкеровки.", spec.Key);
                        continue;
                    }
                    runs.Add(new Run { Row = row, Start = start, End = end, HostStart = host.Start, HostEnd = host.End });
                }
            }
            return runs;
        }

        private static List<WorkingGroup> CompleteGroups(List<Run> runs, Geometry demand, Geometry allowed,
            ReinforcementSpec spec, PlanOptions options, ReinforcementPlan result, CancellationToken token)
        {
            var groups = GroupRuns(runs, spec, options, token);
            if (result.Diagnostics.Any(d => d.IsBlocking && d.SourceId == spec.Key)) return groups;
            var index = new Quadtree<Run>();
            var rows = new HashSet<double>();
            foreach (var group in groups)
            {
                IndexGroup(group, index);
                foreach (var member in group.Rows) rows.Add(member.Row);
            }
            var addedRows = new HashSet<double>();
            var extendedRows = new HashSet<double>();
            var failures = new List<SupplementFailure>();
            Geometry uncovered = demand;
            for (var pass = 0; pass < 4; pass++)
            {
                token.ThrowIfCancellationRequested();
                uncovered = DemandCoverage.Uncovered(demand, CoverageGroups(groups, options.AlongX), spec.SpacingMm, options.AlongX, token, options.CoverageHalfSpacing);
                if (uncovered.Area <= Epsilon * Epsilon) break;
                var added = false;
                foreach (var missing in GeometryKernel.Polygons(uncovered).OrderBy(p => p.EnvelopeInternal.MinY).ThenBy(p => p.EnvelopeInternal.MinX))
                {
                    token.ThrowIfCancellationRequested();
                    if (TryExtendOneRow(missing, allowed, groups, index, spec, options, token, out var extendedRow))
                    {
                        extendedRows.Add(extendedRow);
                        added = true;
                        continue;
                    }
                    PlanDiagnostic failure = null;
                    foreach (var row in SupplementalRows(missing, index, spec.EffectiveModelDiameterMm, options.AlongX))
                    {
                        token.ThrowIfCancellationRequested();
                        if (!Slice(missing, row, options.AlongX).Any(i => i.End - i.Start >= options.MinimumLengthMm - Epsilon)) continue;
                        var candidateDiagnostics = new ReinforcementPlan();
                        var candidates = CreateRowRuns(demand, allowed, row, spec, options, candidateDiagnostics, missing);
                        if (candidateDiagnostics.Diagnostics.Count > 0)
                        {
                            failure = candidateDiagnostics.Diagnostics[0];
                            continue;
                        }
                        if (candidates.Count == 0) continue;
                        if (candidates.Any(candidate => Collides(candidate, index, spec.EffectiveModelDiameterMm)))
                        {
                            failure = new PlanDiagnostic { Code = "SUPPLEMENTAL_ROW_COLLISION", SourceId = spec.Key,
                                Message = "Для узкого выступа не удалось разместить дополнительный стержень без пересечения с соседними стержнями.", IsBlocking = true };
                            continue;
                        }
                        foreach (var candidate in candidates)
                        {
                            // Keep the accepted main arrays intact. Extra single bars
                            // retain their verified endpoints; regrouping them could
                            // extend them back into a neighbouring bar body.
                            var extraGroup = new WorkingGroup { Spec = spec, Start = candidate.Start, End = candidate.End,
                                HostStart = candidate.HostStart, HostEnd = candidate.HostEnd, Rows = new List<Run> { candidate }, ModifiedForCoverage = true };
                            groups.Add(extraGroup);
                            IndexGroup(extraGroup, index);
                        }
                        rows.Add(row);
                        addedRows.Add(row);
                        if (rows.Count > MaximumRows || groups.Sum(g => (long)g.Rows.Count) > MaximumBars) throw new PlanSizeException();
                        added = true;
                        failure = null;
                        break;
                    }
                    if (failure != null) failures.Add(new SupplementFailure { Region = missing, Diagnostic = failure });
                }
                if (!added) break;
                uncovered = null;
            }
            if (uncovered == null) uncovered = DemandCoverage.Uncovered(demand, CoverageGroups(groups, options.AlongX), spec.SpacingMm, options.AlongX, token, options.CoverageHalfSpacing);
            if (uncovered.Area > Epsilon * Epsilon)
                foreach (var failure in failures)
                {
                    token.ThrowIfCancellationRequested();
                    if (GeometryKernel.Intersect(uncovered, failure.Region).Area > Epsilon * Epsilon)
                        Add(result, failure.Diagnostic.Code, failure.Diagnostic.Message, spec.Key);
                }
            if (addedRows.Count > 0)
                result.Diagnostics.Add(new PlanDiagnostic { Code = "SUPPLEMENTAL_ROWS_CREATED", SourceId = spec.Key,
                    Message = "Для узких выступов добавлены отдельные ряды стержней: " + addedRows.Count.ToString(CultureInfo.InvariantCulture) + ".", IsBlocking = false });
            if (extendedRows.Count > 0)
                result.Diagnostics.Add(new PlanDiagnostic { Code = "ROWS_EXTENDED_FOR_COVERAGE", SourceId = spec.Key,
                    Message = "Для узких выступов удлинены стержни в рядах: " + extendedRows.Count.ToString(CultureInfo.InvariantCulture) + ".", IsBlocking = false });
            return groups;
        }

        private static void IndexGroup(WorkingGroup group, Quadtree<Run> index)
        {
            foreach (var member in group.Rows)
            {
                var actual = new Run { Row = member.Row, Start = group.Start, End = group.End,
                    HostStart = member.HostStart, HostEnd = member.HostEnd, Group = group };
                group.IndexedRows.Add(actual);
                index.Insert(RunEnvelope(actual), actual);
            }
        }

        private static bool TryExtendOneRow(Polygon missing, Geometry allowed, List<WorkingGroup> groups,
            Quadtree<Run> index, ReinforcementSpec spec, PlanOptions options, CancellationToken token, out double extendedRow)
        {
            extendedRow = 0;
            var envelope = missing.EnvelopeInternal;
            var min = options.AlongX ? envelope.MinY : envelope.MinX;
            var max = options.AlongX ? envelope.MaxY : envelope.MaxX;
            var left = options.AlongX ? envelope.MinX : envelope.MinY;
            var right = options.AlongX ? envelope.MaxX : envelope.MaxY;
            var inside = missing.InteriorPoint.Coordinate;
            var middle = options.AlongX ? inside.Y : inside.X;
            var hostEnvelope = allowed.EnvelopeInternal;
            var reach = options.CoverageHalfSpacing ? spec.SpacingMm / 2 : spec.SpacingMm;
            var query = new Envelope(options.AlongX ? hostEnvelope.MinX : hostEnvelope.MinY,
                options.AlongX ? hostEnvelope.MaxX : hostEnvelope.MaxY, min - reach, max + reach);
            foreach (var existing in index.Query(query)
                .Where(r => min >= r.Row - reach - Epsilon && max <= r.Row + reach + Epsilon)
                .OrderBy(r => Math.Abs(r.Row - middle)).ThenBy(r => Math.Max(r.End, right) - Math.Min(r.Start, left) - r.End + r.Start)
                .ThenBy(r => r.Row).ThenBy(r => r.Start).Take(128))
            {
                token.ThrowIfCancellationRequested();
                var requiredStart = Math.Min(existing.Start, left - spec.AnchorageMm);
                var requiredEnd = Math.Max(existing.End, right + spec.AnchorageMm);
                if (!TryFit(requiredStart, requiredEnd, existing.HostStart, existing.HostEnd, options, out var start, out var end)) continue;
                if (Math.Abs(start - existing.Start) <= Epsilon && Math.Abs(end - existing.End) <= Epsilon) continue;
                var replacement = new Run { Row = existing.Row, Start = start, End = end,
                    HostStart = existing.HostStart, HostEnd = existing.HostEnd };
                if (Collides(replacement, index, spec.EffectiveModelDiameterMm, existing)) continue;

                var original = existing.Group;
                var position = original.IndexedRows.IndexOf(existing);
                if (position < 0) throw new InvalidOperationException("Не найден исходный ряд массива.");
                groups.Remove(original);
                AddFragment(0, position);
                AddFragment(position + 1, original.Rows.Count - position - 1);
                index.Remove(RunEnvelope(existing), existing);
                var single = new WorkingGroup { Spec = spec, Start = start, End = end,
                    HostStart = existing.HostStart, HostEnd = existing.HostEnd,
                    Rows = new List<Run> { replacement }, ModifiedForCoverage = true };
                groups.Add(single);
                IndexGroup(single, index);
                extendedRow = existing.Row;
                return true;

                void AddFragment(int offset, int count)
                {
                    if (count <= 0) return;
                    var members = original.Rows.GetRange(offset, count);
                    var fragment = new WorkingGroup { Spec = original.Spec, Start = original.Start, End = original.End,
                        HostStart = members.Max(r => r.HostStart), HostEnd = members.Min(r => r.HostEnd),
                        Rows = members, IndexedRows = original.IndexedRows.GetRange(offset, count), ModifiedForCoverage = original.ModifiedForCoverage };
                    foreach (var item in fragment.IndexedRows) item.Group = fragment;
                    groups.Add(fragment);
                }
            }
            return false;
        }

        private static void VerifyBarCollisions(ReinforcementPlan plan, List<ReinforcementSpec> specifications,
            bool alongX, CancellationToken token)
        {
            if (plan.Bars.Count < 2) return;
            var radii = specifications.ToDictionary(s => s.Key, s => s.EffectiveModelDiameterMm / 2);
            var maximumRadius = radii.Values.Max();
            var index = new STRtree<PhysicalBar>();
            var physical = new List<PhysicalBar>();
            foreach (var bar in plan.Bars)
            {
                token.ThrowIfCancellationRequested();
                var item = new PhysicalBar { Ordinal = physical.Count, Radius = radii[bar.SpecKey],
                    Segment = new Run { Start = alongX ? bar.Start.X : bar.Start.Y, End = alongX ? bar.End.X : bar.End.Y,
                        Row = alongX ? bar.Start.Y : bar.Start.X } };
                physical.Add(item);
                index.Insert(RunEnvelope(item.Segment), item);
            }
            index.Build();
            var collisions = 0L;
            foreach (var bar in physical)
            {
                token.ThrowIfCancellationRequested();
                var run = bar.Segment;
                var reach = bar.Radius + maximumRadius;
                var search = new Envelope(run.Start, run.End, run.Row - reach, run.Row + reach);
                foreach (var other in index.Query(search))
                {
                    token.ThrowIfCancellationRequested();
                    if (other.Ordinal <= bar.Ordinal) continue;
                    // Parallel straight bars with flat ends occupy overlapping volume
                    // only when their longitudinal interiors overlap and their centre
                    // lines are closer than the sum of physical radii. Touching end
                    // faces are not treated as a volumetric collision.
                    if (Math.Min(run.End, other.Segment.End) - Math.Max(run.Start, other.Segment.Start) <= Epsilon) continue;
                    if (Math.Abs(run.Row - other.Segment.Row) < bar.Radius + other.Radius - Epsilon) collisions++;
                }
            }
            if (collisions > 0)
                Add(plan, "FINAL_BAR_COLLISION", "Пересекающиеся пары стержней в итоговом плане: " + collisions.ToString(CultureInfo.InvariantCulture)
                    + ". Проверьте шаг, анкеровку и назначения соседних цветовых областей.");
        }

        private static List<BarGroup> CoverageGroups(IEnumerable<WorkingGroup> groups, bool alongX) => groups.Select(g => new BarGroup
        {
            Start = Point(g.Start, g.Rows[0].Row, alongX), End = Point(g.End, g.Rows[0].Row, alongX),
            Count = g.Rows.Count, SpacingMm = g.Spec.SpacingMm
        }).ToList();

        private static Envelope RunEnvelope(Run run) => new Envelope(run.Start, run.End, run.Row, run.Row);

        private static bool Collides(Run candidate, Quadtree<Run> index, double diameter, Run ignored = null)
        {
            var envelope = RunEnvelope(candidate);
            envelope.ExpandBy(diameter);
            foreach (var existing in index.Query(envelope))
            {
                if (ReferenceEquals(existing, ignored)) continue;
                var across = Math.Abs(candidate.Row - existing.Row);
                if (across >= diameter - Epsilon) continue;
                if (Math.Min(existing.End, candidate.End) - Math.Max(existing.Start, candidate.Start) > Epsilon) return true;
            }
            return false;
        }

        private static IEnumerable<double> SupplementalRows(Polygon missing, Quadtree<Run> index, double diameter, bool alongX)
        {
            var envelope = missing.EnvelopeInternal;
            var min = alongX ? envelope.MinY : envelope.MinX;
            var max = alongX ? envelope.MaxY : envelope.MaxX;
            var inside = missing.InteriorPoint.Coordinate;
            var middle = alongX ? inside.Y : inside.X;
            yield return middle;
            var cuts = new SortedSet<double> { min, max };
            var query = alongX ? envelope.Copy() : new Envelope(envelope.MinY, envelope.MaxY, envelope.MinX, envelope.MaxX);
            query.ExpandBy(diameter);
            foreach (var run in index.Query(query))
                foreach (var offset in new[] { -diameter, diameter })
                {
                    var candidate = run.Row + offset;
                    if (candidate > min && candidate < max) cuts.Add(candidate);
                }
            var values = cuts.ToList();
            var alternatives = new List<double>();
            for (var i = 1; i < values.Count; i++) alternatives.Add((values[i - 1] + values[i]) / 2);
            foreach (var candidate in alternatives.OrderBy(v => Math.Abs(v - middle)).ThenBy(v => v).Take(63))
                if (Math.Abs(candidate - middle) > Epsilon) yield return candidate;
        }

        private static List<WorkingGroup> GroupRuns(List<Run> runs, ReinforcementSpec spec, PlanOptions options, CancellationToken token)
        {
            var finished = new List<WorkingGroup>();
            var active = new List<WorkingGroup>();
            foreach (var row in runs.OrderBy(r => r.Row).ThenBy(r => r.Start).GroupBy(r => r.Row))
            {
                token.ThrowIfCancellationRequested();
                var next = new List<WorkingGroup>();
                var unused = new HashSet<WorkingGroup>(active);
                foreach (var run in row)
                {
                    WorkingGroup match = null;
                    var newStart = run.Start;
                    var newEnd = run.End;
                    foreach (var candidate in active)
                    {
                        if (!unused.Contains(candidate) || Math.Abs(run.Row - candidate.Rows[candidate.Rows.Count - 1].Row - spec.SpacingMm) > Epsilon)
                            continue;
                        if (Math.Abs(candidate.Start - run.Start) > options.MergeToleranceMm + Epsilon
                            || Math.Abs(candidate.End - run.End) > options.MergeToleranceMm + Epsilon) continue;
                        var hostStart = Math.Max(candidate.HostStart, run.HostStart);
                        var hostEnd = Math.Min(candidate.HostEnd, run.HostEnd);
                        if (!TryFit(Math.Min(candidate.Start, run.Start), Math.Max(candidate.End, run.End), hostStart, hostEnd, options, out newStart, out newEnd)) continue;
                        // HostStart/End are the intersection of the connected host
                        // intervals of EVERY prior row. Fitting inside that intersection
                        // proves coverage of every expanded segment in O(1), including
                        // diagonal holes. Final bars receive a separate geometry check.
                        match = candidate;
                        break;
                    }
                    if (match == null)
                    {
                        match = new WorkingGroup { Spec = spec, Start = run.Start, End = run.End, HostStart = run.HostStart, HostEnd = run.HostEnd };
                    }
                    else
                    {
                        unused.Remove(match);
                        match.Start = newStart;
                        match.End = newEnd;
                        match.HostStart = Math.Max(match.HostStart, run.HostStart);
                        match.HostEnd = Math.Min(match.HostEnd, run.HostEnd);
                    }
                    match.Rows.Add(run);
                    next.Add(match);
                }
                finished.AddRange(unused);
                active = next;
            }
            finished.AddRange(active);
            return finished;
        }

        private static bool TryFit(double requiredStart, double requiredEnd, double hostStart, double hostEnd, PlanOptions options, out double start, out double end)
        {
            start = end = 0;
            var length = RoundLength(requiredEnd - requiredStart, options);
            if (!GeometryKernel.Finite(length) || length > hostEnd - hostStart + Epsilon
                || requiredStart < hostStart - Epsilon || requiredEnd > hostEnd + Epsilon) return false;
            // The start must both stay in the host and retain both required endpoints.
            var minimumStart = Math.Max(hostStart, requiredEnd - length);
            var maximumStart = Math.Min(requiredStart, hostEnd - length);
            if (minimumStart > maximumStart + Epsilon) return false;
            start = Math.Max(minimumStart, Math.Min(maximumStart, (requiredStart + requiredEnd - length) / 2));
            end = start + length;
            return true;
        }

        private static double RoundLength(double length, PlanOptions options)
        {
            if (options.UseCutLengths)
                foreach (var cut in CutLengths)
                    if (cut >= length - Epsilon) return cut;
            return Math.Ceiling((length - Epsilon) / options.RoundIncrementMm) * options.RoundIncrementMm;
        }

        private static List<Interval> Slice(Geometry geometry, double row, bool alongX)
        {
            var envelope = geometry.EnvelopeInternal;
            if (geometry.IsEmpty) return new List<Interval>();
            var min = alongX ? envelope.MinX : envelope.MinY;
            var max = alongX ? envelope.MaxX : envelope.MaxY;
            var line = Line(min - 1, max + 1, row, alongX);
            var intersection = GeometryKernel.Intersect(geometry, line);
            var intervals = new List<Interval>();
            CollectLineIntervals(intersection, intervals, alongX);
            return MergeIntervals(intervals, Epsilon);
        }

        private static void CollectLineIntervals(Geometry geometry, List<Interval> result, bool alongX)
        {
            if (geometry is LineString line && !line.IsEmpty)
            {
                var e = line.EnvelopeInternal;
                var start = alongX ? e.MinX : e.MinY;
                var end = alongX ? e.MaxX : e.MaxY;
                if (end - start > Epsilon) result.Add(new Interval(start, end));
            }
            else if (geometry is GeometryCollection collection)
                for (var i = 0; i < collection.NumGeometries; i++) CollectLineIntervals(collection.GetGeometryN(i), result, alongX);
        }

        private static List<Interval> MergeIntervals(IEnumerable<Interval> intervals, double tolerance)
        {
            var result = new List<Interval>();
            foreach (var interval in intervals.OrderBy(v => v.Start).ThenBy(v => v.End))
            {
                if (result.Count == 0 || interval.Start > result[result.Count - 1].End + tolerance)
                    result.Add(new Interval(interval.Start, interval.End));
                else result[result.Count - 1].End = Math.Max(result[result.Count - 1].End, interval.End);
            }
            return result;
        }

        private static bool SegmentCovered(Geometry allowed, double start, double end, double row, bool alongX)
        {
            var line = Line(start, end, row, alongX);
            // Exact predicate normally succeeds. The difference fallback permits only
            // sub-micron endpoint noise from floating-point overlay, never a real slot.
            return allowed.Covers(line) || GeometryKernel.Difference(line, allowed).Length <= Epsilon;
        }

        private static LineString Line(double start, double end, double row, bool alongX)
        {
            var a = Point(start, row, alongX);
            var b = Point(end, row, alongX);
            return GeometryKernel.Factory.CreateLineString(new[] { new Coordinate(a.X, a.Y), new Coordinate(b.X, b.Y) });
        }
        private static Point2 Point(double value, double row, bool alongX) => alongX ? new Point2(value, row) : new Point2(row, value);
        private static bool Positive(double value) => GeometryKernel.Finite(value) && value > 0;
        private static bool Nonnegative(double value) => GeometryKernel.Finite(value) && value >= 0;

        private static void Add(ReinforcementPlan result, string code, string message, string sourceId = null)
        {
            if (!result.Diagnostics.Any(d => d.Code == code && d.SourceId == sourceId))
                result.Diagnostics.Add(new PlanDiagnostic { Code = code, Message = message, SourceId = sourceId, IsBlocking = true });
        }
        private sealed class PlanSizeException : Exception { }
        private sealed class SpecDemand { public ReinforcementSpec Spec; public Geometry Region; }
        private sealed class SupplementFailure { public Geometry Region; public PlanDiagnostic Diagnostic; }
        private sealed class PhysicalBar { public int Ordinal; public Run Segment; public double Radius; }
        private sealed class Interval
        {
            public Interval(double start, double end) { Start = start; End = end; }
            public double Start;
            public double End;
        }
        private sealed class Run
        {
            public WorkingGroup Group;
            public double Row;
            public double Start;
            public double End;
            public double HostStart;
            public double HostEnd;
        }
        private sealed class WorkingGroup
        {
            public bool ModifiedForCoverage;
            public ReinforcementSpec Spec;
            public double Start;
            public double End;
            public double HostStart;
            public double HostEnd;
            public List<Run> Rows = new List<Run>();
            public List<Run> IndexedRows = new List<Run>();
        }
    }
}
