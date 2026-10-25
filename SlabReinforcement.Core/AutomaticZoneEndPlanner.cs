using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Buffer;

namespace SlabReinforcement.Core
{
    internal sealed class AutomaticZoneEndPlanner
    {
        private const double Epsilon = GeometryKernel.Epsilon;
        private const double BoundaryTolerance = 1e-5;
        private readonly PlanRequest request;
        private readonly CancellationToken token;
        private readonly Dictionary<string, Cuts> cuts = new Dictionary<string, Cuts>(StringComparer.Ordinal);
        private readonly Dictionary<double, Geometry> zoneAllowed = new Dictionary<double, Geometry>();
        private readonly Dictionary<string, LocatedBar> candidates = new Dictionary<string, LocatedBar>(StringComparer.Ordinal);
        private readonly Dictionary<string, AutomaticZoneEndPlanner> typeBuilders = new Dictionary<string, AutomaticZoneEndPlanner>(StringComparer.Ordinal);
        private Geometry host, allowed;
        private IPreparedGeometry preparedAllowed;
        private STRtree<Boundary> originalIndex, axisIndex;
        private LocatedBar lastCandidate;

        internal AutomaticZoneEndPlanner(PlanRequest request, CancellationToken token = default)
        { this.request = request; this.token = token; }
        internal string LastFailureReason { get; private set; }

        // A successful trial is geometric evidence only. Complete publishes exclusively
        // the rows retained by final zone grouping, so discarded trials cannot create U-bars.
        internal bool TryConnect(double start, double end, double row, ReinforcementSpec spec, bool atStart,
            out PerimeterZoneEndConnection connection, string requiredEdge = null)
        {
            token.ThrowIfCancellationRequested();
            connection = null; lastCandidate = null; LastFailureReason = null;
            if (RoutesByType)
            {
                if (!TryTypeBuilder(spec?.TypeKey, out var child)) return false;
                bool success = child.TryConnect(start, end, row, spec, atStart, out connection, requiredEdge);
                LastFailureReason = child.LastFailureReason;
                return success;
            }
            try
            {
                Initialize();
                var settings = request.AutomaticZoneEnds;
                if (spec == null || !spec.ElevationMm.HasValue || !Finite(spec.ElevationMm.Value)
                    || !Finite(spec.EffectiveModelDiameterMm) || spec.EffectiveModelDiameterMm <= 0
                    || !Finite(spec.SpacingMm) || spec.SpacingMm <= 0
                    || new[] { start, end, row }.Any(v => !Finite(v)) || end - start <= Epsilon)
                    return Fail("Для П-окончания не определены положение, диаметр или высота зоны.");
                double radius = settings.ModelDiameterMm / 2;
                double bendRadius = (settings.BendDiameterMm + settings.ModelDiameterMm) / 2;
                double oppositeZ = BarElevation.Calculate(!settings.Top, settings.ThicknessMm,
                    settings.Top ? settings.BottomCoverMm : settings.TopCoverMm,
                    settings.Top ? settings.TopCoverMm : settings.BottomCoverMm, settings.ModelDiameterMm, 0);
                double top = settings.Top ? spec.ElevationMm.Value : oppositeZ;
                double bottom = settings.Top ? oppositeZ : spec.ElevationMm.Value;
                if (bottom + settings.ThicknessMm - radius < settings.BottomCoverMm - Epsilon
                    || -top - radius < settings.TopCoverMm - Epsilon)
                    return Fail("П-окончание не помещается по высоте. Проверьте диаметр и защитные слои.");
                if (top - bottom < 2 * bendRadius + 2 || settings.LegLengthMm <= bendRadius + 2)
                    return Fail("Загиб П-окончания не помещается. Проверьте тип арматуры и толщину плиты.");

                var cut = GetCuts(row, spec.EffectiveModelDiameterMm);
                var concrete = cut.Host.FirstOrDefault(i => i.Min <= start + Epsilon && i.Max >= end - Epsilon);
                var zone = cut.Zone.FirstOrDefault(i => i.Min <= start + Epsilon && i.Max >= end - Epsilon);
                if (concrete == null || zone == null)
                    return Fail("Зона пересекает отверстие или нарушает защитный слой. Проверьте участок и диаметр.");
                double endpoint = atStart ? start : end;
                if (Math.Abs(endpoint - (atStart ? zone.Min : zone.Max)) > BoundaryTolerance)
                    return Fail("П-окончание можно разместить только у конца зоны возле края плиты или отверстия.");
                var safe = cut.Allowed.Where(i => i.Min >= concrete.Min - Epsilon && i.Max <= concrete.Max + Epsilon
                    && i.Max >= start - Epsilon && i.Min <= end + Epsilon).ToList();
                if (safe.Count == 0) return Fail("П-окончание не помещается с заданным боковым защитным слоем.");
                var interval = atStart ? safe[0] : safe[safe.Count - 1];
                if (interval.Max - interval.Min < settings.LegLengthMm - Epsilon)
                    return Fail("Полка П-окончания не помещается. Сокращать требуемую длину нельзя.");
                double sign = atStart ? 1 : -1;
                var inward = request.Options.AlongX ? new Point2(sign, 0) : new Point2(0, sign);
                var web = Point(atStart ? interval.Min : interval.Max, row);
                var original = FindBoundary(originalIndex, Point(atStart ? concrete.Min : concrete.Max, row), inward);
                var boundary = FindBoundary(axisIndex, web, inward);
                if (original == null || boundary == null)
                    return Fail("Не найден край для П-окончания. Проверьте контур плиты.");
                double normalProjection = -original.Along.Y * inward.X + original.Along.X * inward.Y;
                double webOffset = sign * (endpoint - (request.Options.AlongX ? web.X : web.Y));
                double expectedOffset = (request.Options.EdgeCoverMm + spec.EffectiveModelDiameterMm / 2
                    - settings.SideCoverMm - radius) / normalProjection;
                if (Math.Abs(webOffset - expectedOffset) > BoundaryTolerance)
                    return Fail("Зона и П-окончание находятся у разных краёв. Проверьте контур плиты.");
                double overlap = Math.Min(settings.LegLengthMm, webOffset + end - start) - Math.Max(bendRadius, webOffset);
                if (overlap <= Epsilon) return Fail("Между прямыми участками зоны и П-стержня нет нахлёста.");
                if (overlap < settings.RequiredLapMm - Epsilon)
                    return Fail("Нахлёст зоны с П-стержнем " + overlap.ToString("0.###", CultureInfo.CurrentCulture)
                        + " мм, требуется " + settings.RequiredLapMm.ToString("0.###", CultureInfo.CurrentCulture) + " мм.");
                string edgeId = "AutoZoneU|T" + settings.TypeKey.Length.ToString(CultureInfo.InvariantCulture) + ":" + settings.TypeKey
                    + "|D" + F(settings.DiameterMm) + "/" + F(settings.ModelDiameterMm) + "|" + boundary.Id + "|" + (request.Options.AlongX ? "X" : "Y")
                    + "|" + F(sign) + "|" + F(bottom) + "|" + F(top);
                if (requiredEdge != null && !string.Equals(edgeId, requiredEdge, StringComparison.Ordinal))
                    return Fail("Конец зоны вышел из П-окончания. Уменьшите допуск объединения зон.");
                string key = edgeId + "|" + F(row);
                if (!candidates.TryGetValue(key, out var candidate))
                {
                    var path = Path(web, inward, bottom, top, bendRadius, settings.LegLengthMm);
                    var projection = GeometryKernel.Factory.CreateLineString(new[] { new Coordinate(web.X, web.Y),
                        new Coordinate(web.X + inward.X * settings.LegLengthMm, web.Y + inward.Y * settings.LegLengthMm) });
                    // Every arc is in this vertical plane and projects inside this same
                    // interval; the top/bottom extrema above are its exact Z extrema.
                    if (!preparedAllowed.Covers(projection)) return Fail("П-окончание выходит за край или нарушает защитный слой.");
                    if (candidates.Count >= CompositePlanValidator.MaximumBars)
                        return Fail("Слишком много вариантов П-окончаний зон. Уменьшите выбранную область.");
                    var bar = new PlannedBar { SpecKey = "AutoZoneUBar|" + key, ColorKey = "RGB(219, 100, 48)",
                        PreviewPurpose = PreviewPurpose.Zones,
                        Start = new Point2(path[0].Start.X, path[0].Start.Y), End = new Point2(path[4].End.X, path[4].End.Y), Centerline = path,
                        Placement = new BarPlacement { TypeKey = settings.TypeKey, Component = "PerimeterU", ModelDiameterMm = settings.ModelDiameterMm,
                            ElevationMm = bottom, Distribution = request.Options.AlongX ? new Point3(0, 1, 0) : new Point3(1, 0, 0) } };
                    candidate = new LocatedBar { Key = key, EdgeId = edgeId, Bar = bar, Edge = boundary, Web = web,
                        Inward = inward, Row = row, Bottom = bottom, Top = top, NominalSpacing = spec.SpacingMm,
                        Station = (web.X - boundary.A.X) * boundary.Along.X + (web.Y - boundary.A.Y) * boundary.Along.Y };
                    candidates.Add(key, candidate);
                }
                lastCandidate = candidate;
                connection = new PerimeterZoneEndConnection { AtStart = atStart, EdgeId = edgeId,
                    OverlapMm = overlap, StationOffsetMm = 0, WebOffsetMm = webOffset };
                return true;
            }
            catch (ArgumentException ex) { return Fail(ex.Message); }
            catch (TopologyException) { return Fail("П-окончание не проверено. Проверьте контур плиты и отверстия."); }
        }

        internal ReinforcementPlan Complete(IReadOnlyList<PlannedBar> finalZoneBars)
        {
            if (RoutesByType) return CompleteTypes(finalZoneBars);
            var result = new ReinforcementPlan();
            var selected = new Dictionary<string, LocatedBar>(StringComparer.Ordinal);
            foreach (var bar in finalZoneBars ?? Array.Empty<PlannedBar>())
            {
                token.ThrowIfCancellationRequested();
                foreach (var connection in bar.EndConnections ?? new List<PerimeterZoneEndConnection>())
                {
                    if (connection?.EdgeId == null || !connection.EdgeId.StartsWith("AutoZoneU|", StringComparison.Ordinal)) continue;
                    var spec = request.Specifications.FirstOrDefault(s => s.Key == bar.SpecKey);
                    double start = request.Options.AlongX ? bar.Start.X : bar.Start.Y;
                    double end = request.Options.AlongX ? bar.End.X : bar.End.Y;
                    double row = request.Options.AlongX ? bar.Start.Y : bar.Start.X;
                    if (!TryConnect(start, end, row, spec, connection.AtStart, out _, connection.EdgeId))
                    {
                        result.Diagnostics.Add(Error("AUTOMATIC_ZONE_END_INVALID", LastFailureReason));
                        return result;
                    }
                    selected[lastCandidate.Key] = lastCandidate;
                }
            }
            if (selected.Count == 0) return result;
            var settings = request.AutomaticZoneEnds;
            foreach (var bucket in selected.Values.GroupBy(c => c.EdgeId).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var items = bucket.OrderBy(c => c.Row).ToList();
                AddGroups(result, items);
                var byStation = items.OrderBy(c => c.Station).ToList();
                var first = byStation[0];
                result.PerimeterUEdges.Add(new PerimeterUEdge { Id = first.EdgeId,
                    // This existing flag selects explicit physical stations in edge verification.
                    FollowsBackgroundRows = true, AlongX = request.Options.AlongX,
                    Start = first.Edge.A, End = first.Edge.B, Inward = first.Inward,
                    BottomZ = first.Bottom, TopZ = first.Top, ModelDiameterMm = settings.ModelDiameterMm,
                    BendRadiusMm = (settings.BendDiameterMm + settings.ModelDiameterMm) / 2,
                    LegLengthMm = settings.LegLengthMm, SideCoverMm = settings.SideCoverMm,
                    SpacingMm = byStation.Count > 1 ? byStation.Zip(byStation.Skip(1), (a, b) => b.Station - a.Station).Max() : first.NominalSpacing,
                    StationRangeStartMm = Clamp(first.Station, first.Edge.Length),
                    StationRangeEndMm = Clamp(byStation.Last().Station, first.Edge.Length),
                    StationsMm = byStation.Select(c => Clamp(c.Station, first.Edge.Length)).ToList(),
                    BarKeys = byStation.Select(c => c.Bar.SpecKey).ToList() });
            }
            result.Diagnostics.AddRange(BarContainmentValidator.Validate(result.Bars, request.Slabs, settings.ThicknessMm,
                settings.TopCoverMm, settings.BottomCoverMm, settings.SideCoverMm, token));
            if (result.Diagnostics.Any(d => d.IsBlocking)) { result.Bars.Clear(); result.Groups.Clear(); result.PerimeterUEdges.Clear(); }
            return result;
        }

        private bool RoutesByType => request?.AutomaticZoneEndTypes?.Count > 0;

        private bool TryTypeBuilder(string typeKey, out AutomaticZoneEndPlanner child)
        {
            child = null;
            if (string.IsNullOrWhiteSpace(typeKey) || !request.AutomaticZoneEndTypes.TryGetValue(typeKey, out var settings)
                || settings == null || !string.Equals(settings.TypeKey, typeKey, StringComparison.Ordinal))
                return Fail("Не найден тип П-окончания. Проверьте тип арматуры зоны.");
            if (!typeBuilders.TryGetValue(typeKey, out child))
            {
                // Share immutable geometric inputs, but each physical type owns its own
                // cover inset, bend dimensions, candidate cache and final grouping.
                child = new AutomaticZoneEndPlanner(new PlanRequest { Slabs = request.Slabs, Faces = request.Faces,
                    Specifications = request.Specifications, Options = request.Options, AutomaticZoneEnds = settings }, token);
                typeBuilders.Add(typeKey, child);
            }
            return true;
        }

        private ReinforcementPlan CompleteTypes(IReadOnlyList<PlannedBar> finalZoneBars)
        {
            var result = new ReinforcementPlan();
            var byType = new Dictionary<string, List<PlannedBar>>(StringComparer.Ordinal);
            foreach (var bar in finalZoneBars ?? Array.Empty<PlannedBar>())
            {
                token.ThrowIfCancellationRequested();
                if (bar?.EndConnections == null || !bar.EndConnections.Any(c => c?.EdgeId?.StartsWith("AutoZoneU|", StringComparison.Ordinal) == true)) continue;
                var spec = request.Specifications.FirstOrDefault(s => s.Key == bar.SpecKey);
                if (!TryTypeBuilder(spec?.TypeKey, out _))
                {
                    result.Diagnostics.Add(Error("AUTOMATIC_ZONE_END_INVALID", LastFailureReason));
                    return result;
                }
                if (!byType.TryGetValue(spec.TypeKey, out var bars)) byType[spec.TypeKey] = bars = new List<PlannedBar>();
                bars.Add(bar);
            }
            // Unused trial types are deliberately absent. Each child preserves the
            // consecutive Bars slices corresponding to its Groups, including singletons.
            foreach (var pair in byType.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var child = typeBuilders[pair.Key].Complete(pair.Value);
                result.Bars.AddRange(child.Bars);
                result.Groups.AddRange(child.Groups);
                result.PerimeterUEdges.AddRange(child.PerimeterUEdges);
                result.Diagnostics.AddRange(child.Diagnostics);
            }
            if (result.Diagnostics.Any(d => d.IsBlocking)) { result.Bars.Clear(); result.Groups.Clear(); result.PerimeterUEdges.Clear(); }
            return result;
        }

        private void Initialize()
        {
            if (host != null) return;
            var settings = request?.AutomaticZoneEnds;
            if (settings == null || request.Options == null || request.Slabs == null || request.Slabs.Count == 0
                || request.Slabs.Count > GeometryKernel.MaximumPolygons || string.IsNullOrWhiteSpace(settings.TypeKey))
                throw new ArgumentException("Для П-окончаний зон выберите плиту и тип арматуры зоны.");
            if (new[] { settings.DiameterMm, settings.ModelDiameterMm, settings.BendDiameterMm, settings.LegLengthMm, settings.ThicknessMm }.Any(v => !Finite(v) || v <= 0)
                || new[] { settings.TopCoverMm, settings.BottomCoverMm, settings.SideCoverMm, settings.RequiredLapMm, request.Options.EdgeCoverMm }.Any(v => !Finite(v) || v < 0))
                throw new ArgumentException("Неверные размеры П-окончания. Проверьте тип арматуры, толщину плиты и защитные слои.");
            var concrete = GeometryKernel.Union(request.Slabs.Select(GeometryKernel.ReadPolygon));
            var inset = Inset(concrete, settings.SideCoverMm + settings.ModelDiameterMm / 2);
            if (inset.IsEmpty) throw new ArgumentException("П-окончание зоны не помещается в плите с заданным боковым защитным слоем.");
            originalIndex = Boundaries(concrete); axisIndex = Boundaries(inset);
            preparedAllowed = PreparedGeometryFactory.Prepare(inset.Buffer(Epsilon));
            host = concrete; allowed = inset;
        }

        private Cuts GetCuts(double row, double diameter)
        {
            string key = row.ToString("R", CultureInfo.InvariantCulture) + "|" + diameter.ToString("R", CultureInfo.InvariantCulture);
            if (cuts.TryGetValue(key, out var result)) return result;
            if (!zoneAllowed.TryGetValue(diameter, out var zone))
                zoneAllowed[diameter] = zone = Inset(host, request.Options.EdgeCoverMm + diameter / 2);
            var box = host.EnvelopeInternal;
            var line = GeometryKernel.Factory.CreateLineString(request.Options.AlongX
                ? new[] { new Coordinate(box.MinX - 1, row), new Coordinate(box.MaxX + 1, row) }
                : new[] { new Coordinate(row, box.MinY - 1), new Coordinate(row, box.MaxY + 1) });
            result = new Cuts { Host = Intervals(GeometryKernel.Intersect(host, line)),
                Allowed = Intervals(GeometryKernel.Intersect(allowed, line)), Zone = Intervals(GeometryKernel.Intersect(zone, line)) };
            cuts.Add(key, result); return result;
        }

        private void AddGroups(ReinforcementPlan result, List<LocatedBar> rows)
        {
            for (int i = 0; i < rows.Count;)
            {
                token.ThrowIfCancellationRequested();
                var first = rows[i]; int count = 1;
                double step = i + 1 < rows.Count ? rows[i + 1].Row - first.Row : 0;
                double longitudinal = request.Options.AlongX ? first.Web.X : first.Web.Y;
                while (step > Epsilon && i + count < rows.Count && count < 1002
                    && Math.Abs(rows[i + count].Row - first.Row - count * step) <= Epsilon
                    && Math.Abs((request.Options.AlongX ? rows[i + count].Web.X : rows[i + count].Web.Y) - longitudinal) <= Epsilon) count++;
                var bar = first.Bar;
                result.Bars.AddRange(rows.Skip(i).Take(count).Select(c => c.Bar));
                result.Groups.Add(new BarGroup { Start = bar.Start, End = bar.End, SpecKey = bar.SpecKey, ColorKey = bar.ColorKey,
                    PreviewPurpose = bar.PreviewPurpose,
                    Placement = bar.Placement, Centerline = bar.Centerline, Count = count, SpacingMm = count > 1 ? step : 0,
                    LayoutRule = BarLayoutRule.NumberWithSpacing, ArrayLengthMm = count > 1 ? step * (count - 1) : 0 });
                i += count;
            }
        }

        private static List<CenterlineSegment> Path(Point2 web, Point2 inward, double bottom, double top, double r, double leg)
        {
            Point3 At(double x, double z) => new Point3(web.X + inward.X * x, web.Y + inward.Y * x, z);
            double q = 1 - Math.Sqrt(.5);
            return new List<CenterlineSegment> {
                new CenterlineSegment { Start = At(leg, bottom), End = At(r, bottom) },
                new CenterlineSegment { Start = At(r, bottom), Midpoint = At(r*q, bottom+r*q), End = At(0, bottom+r) },
                new CenterlineSegment { Start = At(0, bottom+r), End = At(0, top-r) },
                new CenterlineSegment { Start = At(0, top-r), Midpoint = At(r*q, top-r*q), End = At(r, top) },
                new CenterlineSegment { Start = At(r, top), End = At(leg, top) }
            };
        }

        private static STRtree<Boundary> Boundaries(Geometry geometry)
        {
            var index = new STRtree<Boundary>(); int count = 0;
            foreach (var polygon in GeometryKernel.ToInputs(geometry))
                foreach (var ring in new[] { polygon.Shell }.Concat(polygon.Holes))
                    for (int i = 0; i < ring.Count; i++)
                    {
                        var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                        double length = Math.Sqrt((b.X-a.X)*(b.X-a.X) + (b.Y-a.Y)*(b.Y-a.Y));
                        if (length <= Epsilon) continue;
                        if (++count > 2000) throw new ArgumentException("Контур для П-окончания зоны содержит более 2000 отрезков. Упростите контур.");
                        var boundary = new Boundary { Id = F(a.X) + "," + F(a.Y) + ":" + F(b.X) + "," + F(b.Y), A = a, B = b,
                            Along = new Point2((b.X-a.X)/length, (b.Y-a.Y)/length), Length = length,
                            Segment = new LineSegment(new Coordinate(a.X, a.Y), new Coordinate(b.X, b.Y)) };
                        index.Insert(new Envelope(a.X, b.X, a.Y, b.Y), boundary);
                    }
            index.Build(); return index;
        }

        private static Boundary FindBoundary(STRtree<Boundary> index, Point2 p, Point2 inward)
        {
            var envelope = new Envelope(p.X, p.X, p.Y, p.Y); envelope.ExpandBy(BoundaryTolerance);
            return index.Query(envelope).Where(b => b.Segment.Distance(new Coordinate(p.X, p.Y)) <= BoundaryTolerance)
                .Select(b => new { Edge = b, Projection = -b.Along.Y * inward.X + b.Along.X * inward.Y })
                .Where(b => b.Projection > Epsilon).OrderByDescending(b => b.Projection).ThenBy(b => b.Edge.Id, StringComparer.Ordinal)
                .Select(b => b.Edge).FirstOrDefault();
        }

        private List<Interval> Intervals(Geometry geometry)
        {
            var intervals = new List<Interval>();
            foreach (var line in Lines(geometry))
            {
                var box = line.EnvelopeInternal;
                double min = request.Options.AlongX ? box.MinX : box.MinY, max = request.Options.AlongX ? box.MaxX : box.MaxY;
                if (max - min > Epsilon) intervals.Add(new Interval { Min = min, Max = max });
            }
            var merged = new List<Interval>();
            foreach (var item in intervals.OrderBy(v => v.Min))
                if (merged.Count == 0 || item.Min > merged[merged.Count - 1].Max + Epsilon) merged.Add(item);
                else merged[merged.Count - 1].Max = Math.Max(merged[merged.Count - 1].Max, item.Max);
            return merged;
        }
        private static IEnumerable<LineString> Lines(Geometry geometry)
        {
            if (geometry is LineString line && !line.IsEmpty) yield return line;
            else if (geometry is GeometryCollection collection)
                for (int i = 0; i < collection.NumGeometries; i++) foreach (var child in Lines(collection.GetGeometryN(i))) yield return child;
        }
        private bool Fail(string reason) { LastFailureReason = reason; return false; }
        private Point2 Point(double longitudinal, double row) => request.Options.AlongX ? new Point2(longitudinal, row) : new Point2(row, longitudinal);
        private static Geometry Inset(Geometry geometry, double distance) => geometry.Buffer(-distance,
            new BufferParameters { JoinStyle = JoinStyle.Mitre, MitreLimit = 5, SimplifyFactor = 0 });
        private static bool Finite(double value) => GeometryKernel.Finite(value);
        private static double Clamp(double station, double length) => Math.Max(0, Math.Min(length, station));
        private static string F(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero).ToString("R", CultureInfo.InvariantCulture);
        private static PlanDiagnostic Error(string code, string message) => new PlanDiagnostic { Code = code, Message = message, IsBlocking = true };
        private sealed class Interval { internal double Min, Max; }
        private sealed class Cuts { internal List<Interval> Host, Allowed, Zone; }
        private sealed class Boundary { internal string Id; internal Point2 A, B, Along; internal double Length; internal LineSegment Segment; }
        private sealed class LocatedBar { internal string Key, EdgeId; internal PlannedBar Bar; internal Boundary Edge;
            internal Point2 Web, Inward; internal double Row, Bottom, Top, Station, NominalSpacing; }
    }
}
