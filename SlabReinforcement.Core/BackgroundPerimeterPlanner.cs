using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Buffer;

namespace SlabReinforcement.Core
{
    /// <summary>U-bars at the ends of actual lower background rows, in the slab-local frame.</summary>
    internal static class BackgroundPerimeterPlanner
    {
        private const double Epsilon = 1e-6;
        private const double BoundaryTolerance = 1e-5;

        internal static ReinforcementPlan Build(PerimeterRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var result = new ReinforcementPlan();
            try
            {
                Validate(request);
                var layers = request.BackgroundLayers.ToDictionary(l => l.Key, StringComparer.Ordinal);
                var virtualPlacements = BackgroundPlanner.GetPlacements(new BackgroundRequest {
                    Layers = request.BackgroundLayers, ThicknessMm = request.ThicknessMm,
                    TopCoverMm = request.TopCoverMm, BottomCoverMm = request.BottomCoverMm, SideCoverMm = request.SideCoverMm
                });
                var rows = new List<Row>();
                var heights = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var bar in request.BackgroundPlan.Bars.Concat(request.BackgroundPlan.ReferenceBars))
                {
                    token.ThrowIfCancellationRequested();
                    if (bar?.Placement?.Component != "Background") continue;
                    if (!layers.TryGetValue(bar.SpecKey ?? string.Empty, out var layer))
                        throw new DetailException("PERIMETER_BACKGROUND_INVALID", "Не определён слой существующего фона. Проверьте его направление и высоту.");
                    var row = ReadRow(bar, layer);
                    if (heights.TryGetValue(layer.Key, out double z) && Math.Abs(z - row.Z) > Epsilon)
                        throw new DetailException("PERIMETER_BACKGROUND_INVALID", "Стержни одного слоя фона лежат на разных высотах. Выровняйте их.");
                    heights[layer.Key] = row.Z;
                    if (!layer.Top) rows.Add(row);
                }
                if (rows.Count == 0)
                    throw new DetailException("PERIMETER_BACKGROUND_REQUIRED", "Для привязки П-обрамления к рядам нужен нижний фон X или Y.");
                if (rows.Count > CompositePlanValidator.MaximumBars / 2)
                    throw new DetailException("PERIMETER_BACKGROUND_LIMIT", "Слишком много концов нижнего фона для П-обрамления. Уменьшите выбранную область.");

                var host = GeometryKernel.Union(request.Slabs.Select(GeometryKernel.ReadPolygon));
                double radius = request.ModelDiameterMm / 2;
                double bendRadius = (request.BendDiameterMm + request.ModelDiameterMm) / 2;
                var allowed = host.Buffer(-request.SideCoverMm - radius,
                    new BufferParameters { JoinStyle = JoinStyle.Mitre, MitreLimit = 5, SimplifyFactor = 0 });
                var originalEdges = Boundaries(host);
                var axisEdges = Boundaries(allowed);
                if (axisEdges.Count == 0)
                    throw new DetailException("PERIMETER_BACKGROUND_COVER", "П-обрамление фона не помещается. Проверьте диаметр и боковой защитный слой.");
                var originalIndex = Index(originalEdges);
                var axisIndex = Index(axisEdges);
                var rowCuts = new Dictionary<(bool, double), Cuts>();
                var edgeBars = new Dictionary<(string, bool, double, double, double, double), List<LocatedBar>>();
                var used = new HashSet<(bool, double, double, double, double, double)>();
                var box = host.EnvelopeInternal;

                foreach (var row in rows.OrderBy(r => r.Layer.Key, StringComparer.Ordinal).ThenBy(r => r.Station).ThenBy(r => r.Min))
                {
                    token.ThrowIfCancellationRequested();
                    var upper = request.BackgroundLayers.SingleOrDefault(l => l.Top && l.AlongX == row.Layer.AlongX);
                    if (upper == null)
                        throw new DetailException("PERIMETER_TOP_LAYER_REQUIRED", "Выберите тип верхнего фона того же направления. Включать его необязательно.");
                    double topZ = heights.TryGetValue(upper.Key, out double actualTop) ? actualTop : virtualPlacements[upper.Key].ElevationMm;
                    CheckHeight(request, row, topZ, radius, bendRadius);
                    var cutKey = (row.Layer.AlongX, row.Station);
                    if (!rowCuts.TryGetValue(cutKey, out var cuts))
                    {
                        var line = SliceLine(box, row.Layer.AlongX, row.Station);
                        cuts = new Cuts { Host = Intervals(GeometryKernel.Intersect(host, line), row.Layer.AlongX),
                            Allowed = Intervals(GeometryKernel.Intersect(allowed, line), row.Layer.AlongX) };
                        rowCuts.Add(cutKey, cuts);
                    }
                    var concrete = cuts.Host.FirstOrDefault(i => i.Min <= row.Min + Epsilon && i.Max >= row.Max - Epsilon);
                    if (concrete == null)
                        throw new DetailException("PERIMETER_BACKGROUND_INVALID", "Нижний фон пересекает отверстие или край плиты. Исправьте его положение.");
                    var safe = cuts.Allowed.Where(i => i.Min >= concrete.Min - Epsilon && i.Max <= concrete.Max + Epsilon
                        && i.Max >= row.Min - Epsilon && i.Min <= row.Max + Epsilon).ToList();
                    if (safe.Count == 0)
                        safe = ParallelBoundaryIntervals(axisIndex, box, row.Layer.AlongX, row.Station)
                            .Where(i => i.Min >= concrete.Min - Epsilon && i.Max <= concrete.Max + Epsilon
                                && i.Max >= row.Min - Epsilon && i.Min <= row.Max + Epsilon).ToList();

                    foreach (bool atStart in new[] { true, false })
                    {
                        token.ThrowIfCancellationRequested();
                        double sign = atStart ? 1 : -1;
                        var inward = row.Layer.AlongX ? new Point2(sign, 0) : new Point2(0, sign);
                        var originalPoint = Point(atStart ? concrete.Min : concrete.Max, row.Station, row.Layer.AlongX);
                        var sourceEdge = FindBoundary(originalIndex, originalPoint, inward);
                        if (sourceEdge == null)
                            throw new DetailException("PERIMETER_BACKGROUND_INVALID", "Не найден край возле конца фона. Проверьте контур и положение стержня.");
                        if (sourceEdge.Hole ? !request.Openings : !request.Outer) continue;
                        if (safe.Count == 0)
                            throw new DetailException("PERIMETER_BACKGROUND_COVER", "П-стержень у конца фона не помещается. Проверьте диаметр и защитный слой.");
                        var interval = atStart ? safe[0] : safe[safe.Count - 1];
                        double longitudinal = atStart ? interval.Min : interval.Max;
                        if (interval.Max - interval.Min < request.LegLengthMm - Epsilon)
                            throw new DetailException("PERIMETER_BACKGROUND_LEG", "Полка П-обрамления фона не помещается между краями или отверстиями.");
                        var web = Point(longitudinal, row.Station, row.Layer.AlongX);
                        var boundary = FindBoundary(axisIndex, web, inward);
                        if (boundary == null)
                            throw new DetailException("PERIMETER_BACKGROUND_INVALID", "П-обрамление фона не размещено. Проверьте контур и боковой защитный слой.");
                        var identity = (row.Layer.AlongX, Round(web.X), Round(web.Y), Round(row.Z), Round(topZ), sign);
                        if (!used.Add(identity)) continue;
                        var path = Path(web, inward, row.Z, topZ, bendRadius, request.LegLengthMm);
                        var distribution = row.Layer.AlongX ? new Point3(0, 1, 0) : new Point3(1, 0, 0);
                        var bar = new PlannedBar { SpecKey = "PerimeterU|" + result.Bars.Count,
                            ColorKey = "RGB(219, 100, 48)", Centerline = path,
                            Start = new Point2(path[0].Start.X, path[0].Start.Y), End = new Point2(path[4].End.X, path[4].End.Y),
                            Placement = new BarPlacement { TypeKey = request.TypeKey, Component = "PerimeterU",
                                ModelDiameterMm = request.ModelDiameterMm, ElevationMm = row.Z, Distribution = distribution } };
                        result.Bars.Add(bar);
                        var key = (boundary.Id, row.Layer.AlongX, row.Z, topZ, inward.X, inward.Y);
                        if (!edgeBars.TryGetValue(key, out var items)) edgeBars[key] = items = new List<LocatedBar>();
                        items.Add(new LocatedBar { Bar = bar, Edge = boundary, Web = web, Row = row.Station, Inward = inward,
                            Station = Dot(web, boundary.A, boundary.Along), AlongX = row.Layer.AlongX,
                            BottomZ = row.Z, TopZ = topZ, NominalSpacing = row.Layer.SpacingMm });
                    }
                }
                foreach (var items in edgeBars.Values)
                {
                    token.ThrowIfCancellationRequested();
                    var ordered = items.OrderBy(b => b.Station).ToList();
                    var first = ordered[0];
                    result.PerimeterUEdges.Add(new PerimeterUEdge { Id = "BG|" + result.PerimeterUEdges.Count,
                        FollowsBackgroundRows = true, AlongX = first.AlongX, Start = first.Edge.A, End = first.Edge.B, Inward = first.Inward,
                        BottomZ = first.BottomZ, TopZ = first.TopZ, ModelDiameterMm = request.ModelDiameterMm,
                        BendRadiusMm = bendRadius, LegLengthMm = request.LegLengthMm, SideCoverMm = request.SideCoverMm,
                        SpacingMm = ordered.Count > 1 ? ordered.Zip(ordered.Skip(1), (a, b) => b.Station - a.Station).Max() : first.NominalSpacing,
                        StationRangeStartMm = Math.Max(0, Math.Min(first.Edge.Length, first.Station)),
                        StationRangeEndMm = Math.Max(0, Math.Min(first.Edge.Length, ordered.Last().Station)),
                        StationsMm = ordered.Select(b => Math.Max(0, Math.Min(first.Edge.Length, b.Station))).ToList(),
                        BarKeys = ordered.Select(b => b.Bar.SpecKey).ToList() });
                    AddGroups(result, items, token);
                }
                if (result.Bars.Count == 0)
                    throw new DetailException("PERIMETER_BACKGROUND_REQUIRED", "У выбранных краёв нет концов нижнего фона, к которым можно привязать П-обрамление.");
                result.Diagnostics.AddRange(BarContainmentValidator.Validate(result.Bars, request.Slabs, request.ThicknessMm,
                    request.TopCoverMm, request.BottomCoverMm, request.SideCoverMm, token));
            }
            catch (DetailException ex) { result.Diagnostics.Add(Error(ex.Code, ex.Message)); }
            catch (ArgumentException ex) { result.Diagnostics.Add(Error("PERIMETER_BACKGROUND_INVALID", ex.Message)); }
            catch (TopologyException) { result.Diagnostics.Add(Error("PERIMETER_BACKGROUND_INVALID", "П-обрамление не рассчитано. Проверьте контур плиты и нижний фон.")); }
            if (result.Diagnostics.Any(d => d.IsBlocking)) { result.Bars.Clear(); result.Groups.Clear(); result.PerimeterUEdges.Clear(); }
            return result;
        }

        private static void Validate(PerimeterRequest request)
        {
            if (request.Slabs == null || request.Slabs.Count == 0 || request.Slabs.Count > GeometryKernel.MaximumPolygons
                || string.IsNullOrWhiteSpace(request.TypeKey) || request.BackgroundLayers == null || request.BackgroundLayers.Count == 0
                || request.BackgroundLayers.Count > 4 || request.BackgroundPlan.Diagnostics.Any(d => d.IsBlocking))
                throw new ArgumentException("Для П-обрамления выберите плиту, тип П-стержня и проверьте настройки фона.");
            if (!request.UseUbars || request.UseLongitudinalBars)
                throw new ArgumentException("По рядам фона поддерживаются только П-стержни, без продольного обрамления.");
            if (!request.Outer && !request.Openings) throw new ArgumentException("Выберите, где нужно П-обрамление фона: по наружному краю плиты или вокруг отверстий.");
            if (new[] { request.DiameterMm, request.ModelDiameterMm, request.BendDiameterMm, request.ThicknessMm, request.LegLengthMm }.Any(v => !Finite(v) || v <= 0)
                || new[] { request.TopCoverMm, request.BottomCoverMm, request.SideCoverMm }.Any(v => !Finite(v) || v < 0))
                throw new ArgumentException("Неверные размеры П-обрамления. Проверьте тип арматуры, толщину плиты и защитные слои.");
            if (request.BackgroundLayers.Any(l => l == null || string.IsNullOrWhiteSpace(l.Key) || !Finite(l.ModelDiameterMm) || l.ModelDiameterMm <= 0
                || !Finite(l.SpacingMm) || l.SpacingMm <= 0)
                || request.BackgroundLayers.GroupBy(l => l.Key).Any(g => g.Count() > 1)
                || request.BackgroundLayers.GroupBy(l => new { l.Top, l.AlongX }).Any(g => g.Count() > 1))
                throw new ArgumentException("Проверьте типы и шаги фона. Каждый слой должен быть указан один раз.");
        }

        private static Row ReadRow(PlannedBar bar, BackgroundLayer layer)
        {
            if (!Finite(bar.Placement.ModelDiameterMm) || bar.Placement.ModelDiameterMm <= 0)
                throw new DetailException("PERIMETER_BACKGROUND_INVALID", "Не удалось определить диаметр стержня фона. Проверьте его тип.");
            var a = new Point3(bar.Start.X, bar.Start.Y, bar.Placement.ElevationMm);
            var b = new Point3(bar.End.X, bar.End.Y, bar.Placement.ElevationMm);
            if (bar.Centerline != null && bar.Centerline.Count > 0)
            {
                if (bar.Centerline.Count != 1 || bar.Centerline[0] == null || bar.Centerline[0].Midpoint.HasValue)
                    throw new DetailException("PERIMETER_BACKGROUND_INVALID", "Для П-обрамления по фону нужны прямые нижние стержни X или Y.");
                a = bar.Centerline[0].Start; b = bar.Centerline[0].End;
            }
            if (new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z }.Any(v => !Finite(v) || Math.Abs(v) > 1e9)
                || Math.Abs(a.Z - b.Z) > Epsilon || Math.Abs(layer.AlongX ? a.Y - b.Y : a.X - b.X) > Epsilon)
                throw new DetailException("PERIMETER_BACKGROUND_INVALID", "Фон отклоняется от направления X/Y или высоты слоя. Исправьте его положение.");
            double min = Math.Min(layer.AlongX ? a.X : a.Y, layer.AlongX ? b.X : b.Y);
            double max = Math.Max(layer.AlongX ? a.X : a.Y, layer.AlongX ? b.X : b.Y);
            if (max - min <= Epsilon) throw new ArgumentException("У стержня фона нулевая длина. Проверьте существующий фон.");
            // Preserve the actual slice coordinate. Rounding a row on an inset edge
            // can move it across that edge and select a different, much shorter interval.
            // Rounded coordinates are used only for duplicate identities, never geometry.
            return new Row { Layer = layer, Min = min, Max = max, Station = layer.AlongX ? a.Y : a.X, Z = a.Z };
        }

        private static void CheckHeight(PerimeterRequest request, Row row, double topZ, double radius, double bendRadius)
        {
            double bottomCover = row.Z + request.ThicknessMm - radius;
            double topCover = -topZ - radius;
            if (bottomCover < request.BottomCoverMm - Epsilon || topCover < request.TopCoverMm - Epsilon)
                throw new DetailException("PERIMETER_BACKGROUND_COVER", "П-стержень Ø" + request.DiameterMm.ToString("0.##", CultureInfo.CurrentCulture)
                    + " по " + (row.Layer.AlongX ? "X" : "Y") + ": слой снизу " + bottomCover.ToString("0.##", CultureInfo.CurrentCulture)
                    + " мм, сверху " + topCover.ToString("0.##", CultureInfo.CurrentCulture) + " мм. Уменьшите диаметр П.");
            if (topZ - row.Z < 2 * bendRadius + 2 || request.LegLengthMm <= bendRadius + 2)
                throw new DetailException("PERIMETER_BACKGROUND_BEND", "Загиб П-обрамления фона не помещается. Проверьте диаметр и загиб П-стержня.");
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

        private static void AddGroups(ReinforcementPlan plan, List<LocatedBar> items, CancellationToken token)
        {
            var ordered = items.OrderBy(b => b.Row).ToList();
            for (int i = 0; i < ordered.Count;)
            {
                token.ThrowIfCancellationRequested();
                var first = ordered[i];
                int count = 1;
                double step = i + 1 < ordered.Count ? ordered[i + 1].Row - first.Row : 0;
                double maximum = first.NominalSpacing;
                while (step > Epsilon && step <= maximum + Epsilon && i + count < ordered.Count && count < 1002
                    && Math.Abs(ordered[i + count].Row - first.Row - count * step) <= Epsilon
                    && Math.Abs(first.AlongX ? ordered[i + count].Web.X - first.Web.X : ordered[i + count].Web.Y - first.Web.Y) <= Epsilon
                    && (int)Math.Ceiling((ordered[i + count].Row - first.Row) / maximum) + 1 == count + 1) count++;
                var bar = first.Bar;
                // Native maximum-spacing layout must reproduce the existing rows exactly.
                // Split a run instead of inserting or dropping bars to satisfy its nominal step.
                double length = count > 1 ? ordered[i + count - 1].Row - first.Row : 0;
                plan.Groups.Add(new BarGroup { Start = bar.Start, End = bar.End, SpecKey = bar.SpecKey, ColorKey = bar.ColorKey,
                    Placement = bar.Placement, Centerline = bar.Centerline, Count = count, SpacingMm = count > 1 ? length / (count - 1) : 0,
                    LayoutRule = BarLayoutRule.MaximumSpacing, MaximumSpacingMm = maximum, ArrayLengthMm = length });
                i += count;
            }
        }

        private static List<Boundary> Boundaries(Geometry geometry)
        {
            var result = new List<Boundary>();
            foreach (var polygon in GeometryKernel.ToInputs(geometry))
            {
                AddRing(polygon.Shell, false);
                foreach (var hole in polygon.Holes) AddRing(hole, true);
            }
            if (result.Count > 2000) throw new ArgumentException("Контур П-обрамления фона содержит более 2000 отрезков. Упростите контур.");
            return result;
            void AddRing(List<Point2> points, bool hole)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    var a = points[i]; var b = points[(i + 1) % points.Count];
                    double length = Math.Sqrt((b.X-a.X)*(b.X-a.X) + (b.Y-a.Y)*(b.Y-a.Y));
                    if (length <= Epsilon) continue;
                    result.Add(new Boundary { Id = result.Count.ToString(CultureInfo.InvariantCulture), A = a, B = b, Hole = hole,
                        Length = length, Along = new Point2((b.X-a.X)/length, (b.Y-a.Y)/length),
                        Segment = new LineSegment(new Coordinate(a.X, a.Y), new Coordinate(b.X, b.Y)) });
                }
            }
        }

        private static STRtree<Boundary> Index(List<Boundary> boundaries)
        {
            var result = new STRtree<Boundary>();
            foreach (var b in boundaries) result.Insert(new Envelope(b.A.X, b.B.X, b.A.Y, b.B.Y), b);
            result.Build(); return result;
        }

        private static Boundary FindBoundary(STRtree<Boundary> index, Point2 point, Point2 inward)
        {
            var envelope = new Envelope(point.X, point.X, point.Y, point.Y); envelope.ExpandBy(BoundaryTolerance);
            return index.Query(envelope).Where(b => b.Segment.Distance(new Coordinate(point.X, point.Y)) <= BoundaryTolerance)
                .Select(b => new { Edge = b, Projection = -b.Along.Y * inward.X + b.Along.X * inward.Y })
                .Where(b => b.Projection > 1e-10).OrderByDescending(b => b.Projection).ThenBy(b => b.Edge.Id, StringComparer.Ordinal)
                .Select(b => b.Edge).FirstOrDefault();
        }

        private static LineString SliceLine(Envelope box, bool x, double row) => GeometryKernel.Factory.CreateLineString(x
            ? new[] { new Coordinate(box.MinX - 1, row), new Coordinate(box.MaxX + 1, row) }
            : new[] { new Coordinate(row, box.MinY - 1), new Coordinate(row, box.MaxY + 1) });

        private static List<Interval> ParallelBoundaryIntervals(STRtree<Boundary> index, Envelope box, bool x, double row)
        {
            // A Revit round-trip can put an inset-boundary row a fraction of a
            // micron outside an exact polygon slice. Reuse only that parallel
            // boundary's original interval, within the existing cover tolerance.
            // Neither the inset nor the actual row is moved; final containment
            // still checks every generated U-bar at its measured coordinates.
            var envelope = x ? new Envelope(box.MinX, box.MaxX, row, row)
                : new Envelope(row, row, box.MinY, box.MaxY);
            envelope.ExpandBy(Epsilon);
            return index.Query(envelope)
                .Where(b => x ? b.A.Y == b.B.Y && Math.Abs(b.A.Y - row) <= Epsilon
                    : b.A.X == b.B.X && Math.Abs(b.A.X - row) <= Epsilon)
                .Select(b => new Interval { Min = x ? Math.Min(b.A.X, b.B.X) : Math.Min(b.A.Y, b.B.Y),
                    Max = x ? Math.Max(b.A.X, b.B.X) : Math.Max(b.A.Y, b.B.Y) })
                .OrderBy(i => i.Min).ToList();
        }

        private static List<Interval> Intervals(Geometry geometry, bool x)
        {
            var result = new List<Interval>();
            foreach (var line in Lines(geometry))
            {
                var box = line.EnvelopeInternal;
                var interval = new Interval { Min = x ? box.MinX : box.MinY, Max = x ? box.MaxX : box.MaxY };
                if (interval.Max - interval.Min > Epsilon) result.Add(interval);
            }
            var merged = new List<Interval>();
            foreach (var interval in result.OrderBy(i => i.Min))
            {
                if (merged.Count == 0 || interval.Min > merged[merged.Count - 1].Max + Epsilon) merged.Add(interval);
                else merged[merged.Count - 1].Max = Math.Max(merged[merged.Count - 1].Max, interval.Max);
            }
            return merged;
        }
        private static IEnumerable<LineString> Lines(Geometry geometry)
        {
            if (geometry is LineString line && !line.IsEmpty) yield return line;
            else if (geometry is GeometryCollection collection)
                for (int i = 0; i < collection.NumGeometries; i++) foreach (var item in Lines(collection.GetGeometryN(i))) yield return item;
        }
        private static Point2 Point(double longitudinal, double row, bool x) => x ? new Point2(longitudinal, row) : new Point2(row, longitudinal);
        private static double Dot(Point2 p, Point2 origin, Point2 direction) => (p.X-origin.X)*direction.X + (p.Y-origin.Y)*direction.Y;
        private static double Round(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
        private static bool Finite(double value) => GeometryKernel.Finite(value);
        private static PlanDiagnostic Error(string code, string message) => new PlanDiagnostic { Code = code, Message = message, IsBlocking = true };
        private sealed class DetailException : Exception { internal string Code; internal DetailException(string code, string message) : base(message) { Code = code; } }
        private sealed class Row { internal BackgroundLayer Layer; internal double Min, Max, Station, Z; }
        private sealed class Interval { internal double Min, Max; }
        private sealed class Cuts { internal List<Interval> Host, Allowed; }
        private sealed class Boundary { internal string Id; internal Point2 A, B, Along; internal double Length; internal bool Hole; internal LineSegment Segment; }
        private sealed class LocatedBar { internal PlannedBar Bar; internal Boundary Edge; internal Point2 Web, Inward;
            internal bool AlongX; internal double Station, Row, TopZ, BottomZ, NominalSpacing; }
    }
}
