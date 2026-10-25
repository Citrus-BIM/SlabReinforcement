using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Buffer;

namespace SlabReinforcement.Core
{
    public static class ZonePathBandPlanner
    {
        private const double Epsilon = GeometryKernel.Epsilon;

        /// <summary>Attaches outline bounds to straight zone groups without altering physical rows.</summary>
        public static void Apply(ReinforcementPlan plan, PlanRequest request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (request == null) throw new ArgumentNullException(nameof(request));
            try
            {
                var host = GeometryKernel.Union(request.Slabs.Select(GeometryKernel.ReadPolygon));
                var facesByColor = request.Faces.GroupBy(f => f.ColorKey).ToDictionary(g => g.Key,
                    g => GeometryKernel.AreaOnly(GeometryKernel.Intersect(host, GeometryKernel.Union(g.Select(f =>
                        GeometryKernel.Factory.CreatePolygon(GeometryKernel.ReadRing(f.Vertices)))))));
                var sources = request.Specifications.Where(s => facesByColor.ContainsKey(s.ColorKey))
                    .Select(s => new LineDemand { Spec = s, Region = facesByColor[s.ColorKey] }).ToList();
                var normalized = new ConnectedLineDemandNormalizer(sources, request.Options.AlongX)
                    .Normalize(new ReinforcementPlan(), token);
                var demand = normalized.ToDictionary(d => d.Spec.Key, d => d.Region, StringComparer.Ordinal);
                Apply(plan, request, host, demand, token);
            }
            catch (TopologyException) { Unavailable(plan, true); }
            catch (ArgumentException) { Unavailable(plan, true); }
        }

        // The main planner supplies its already resolved, priority-filtered demand.
        internal static void Apply(ReinforcementPlan plan, PlanRequest request, Geometry host,
            IReadOnlyDictionary<string, Geometry> demandBySpec, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            foreach (var group in plan.Groups) { group.PathBandStartMm = null; group.PathBandEndMm = null; }
            if (plan.Groups.Count == 0 || plan.Diagnostics.Any(d => d.IsBlocking)) return;
            try
            {
                var coverHost = host.Buffer(-request.Options.EdgeCoverMm,
                    new BufferParameters { JoinStyle = JoinStyle.Mitre, MitreLimit = 5, SimplifyFactor = 0 });
                bool alongX = request.Options.AlongX;
                var items = new List<Band>();
                var index = new STRtree<Band>();
                int unavailable = 0;
                foreach (var group in plan.Groups)
                {
                    token.ThrowIfCancellationRequested();
                    if (group.Count < 2 || group.Centerline?.Count > 0
                        || group.Placement != null && group.Placement.Component != "Zones") continue;
                    var spec = request.Specifications.SingleOrDefault(s => s.Key == group.SpecKey);
                    double radius = (group.Placement?.ModelDiameterMm ?? spec?.EffectiveModelDiameterMm ?? 0) / 2;
                    var item = new Band
                    {
                        Group = group, Radius = radius,
                        A = alongX ? group.Start.X : group.Start.Y, B = alongX ? group.End.X : group.End.Y,
                        First = alongX ? group.Start.Y : group.Start.X, Step = group.SpacingMm
                    };
                    item.Last = item.First + (group.Count - 1) * item.Step;
                    if (!GeometryKernel.Finite(radius) || radius <= 0 || !GeometryKernel.Finite(item.Step) || item.Step < 2 * radius
                        || !GeometryKernel.Finite(item.First) || !GeometryKernel.Finite(item.Last) || item.B <= item.A) { unavailable++; continue; }
                    item.Safe = TrySafeInterval(item, coverHost, alongX);
                    if (!item.Safe) unavailable++;
                    items.Add(item);
                    index.Insert(new Envelope(item.A, item.B, item.First, item.Last), item);
                }
                index.Build();

                // Search only local neighbours; wide arrays do not require an all-pairs scan.
                foreach (var item in items)
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var next in index.Query(new Envelope(item.A, item.B, item.Last, item.Last + item.Step)))
                    {
                        if (ReferenceEquals(item, next) || Math.Min(item.B, next.B) - Math.Max(item.A, next.A) <= Epsilon) continue;
                        double gap = next.First - item.Last;
                        if (gap <= Epsilon || gap > Math.Min(item.Step, next.Step) + Epsilon) continue;
                        item.After.Add(next);
                        item.NextGap = Math.Min(item.NextGap, gap);
                        next.PreviousGap = Math.Min(next.PreviousGap, gap);
                    }
                }
                foreach (var item in items.Where(i => i.Safe))
                {
                    token.ThrowIfCancellationRequested();
                    item.Start = Math.Max(item.Low, item.First - item.Step / 2);
                    item.End = Math.Min(item.High, item.Last + item.Step / 2);
                    if (demandBySpec.TryGetValue(item.Group.SpecKey, out var demand))
                    {
                        // At an external end, a nearby edge is meaningful only if
                        // the same connected selected component reaches that edge.
                        if (item.LowAtBoundary && double.IsPositiveInfinity(item.PreviousGap) && item.First - item.Low <= item.Step + Epsilon
                            && ReachesBoundary(demand, item, item.First, item.Low, alongX)) item.Start = item.Low;
                        if (item.HighAtBoundary && double.IsPositiveInfinity(item.NextGap) && item.High - item.Last <= item.Step + Epsilon
                            && ReachesBoundary(demand, item, item.Last, item.High, alongX)) item.End = item.High;
                    }
                }
                foreach (var item in items.Where(i => i.Safe))
                    foreach (var next in item.After.Where(i => i.Safe))
                    {
                        token.ThrowIfCancellationRequested();
                        double gap = next.First - item.Last;
                        if (gap > item.NextGap + Epsilon || gap > next.PreviousGap + Epsilon) continue;
                        double midpoint = (item.Last + next.First) / 2;
                        if (midpoint < item.Last + item.Radius - Epsilon || midpoint > next.First - next.Radius + Epsilon)
                        {
                            // A common outline cannot cut through either physical
                            // bar face, even when neighbouring types use different diameters.
                            item.Safe = next.Safe = false;
                            unavailable++;
                            continue;
                        }
                        if (midpoint < item.Low - Epsilon || midpoint > item.High + Epsilon
                            || midpoint < next.Low - Epsilon || midpoint > next.High + Epsilon) continue;
                        item.End = midpoint;
                        next.Start = midpoint;
                    }
                foreach (var item in items.Where(i => i.Safe))
                {
                    item.Group.PathBandStartMm = item.Start;
                    item.Group.PathBandEndMm = item.End;
                }
                if (unavailable > 0) Unavailable(plan, false);
            }
            catch (TopologyException) { Unavailable(plan, true); }
            catch (ArgumentException) { Unavailable(plan, true); }
        }

        private static bool TrySafeInterval(Band item, Geometry coverHost, bool alongX)
        {
            // A small probe beyond the search window identifies a host edge
            // exactly one pitch away without treating the window itself as one.
            item.Low = item.First - item.Step - 16 * Epsilon;
            item.High = item.Last + item.Step + 16 * Epsilon;
            var outside = GeometryKernel.Difference(Rectangle(item.A, item.B, item.Low, item.High, alongX), coverHost);
            // Any outside point anywhere along the full bar span forbids its
            // distribution coordinate. Polygon projections are exact intervals.
            foreach (var polygon in GeometryKernel.Polygons(outside))
            {
                var e = polygon.EnvelopeInternal;
                double a = alongX ? e.MinY : e.MinX, b = alongX ? e.MaxY : e.MaxX;
                if (b <= item.First - item.Radius + Epsilon)
                { item.Low = Math.Max(item.Low, b); item.LowAtBoundary |= b >= item.First - item.Step - Epsilon; }
                else if (a >= item.Last + item.Radius - Epsilon)
                { item.High = Math.Min(item.High, a); item.HighAtBoundary |= a <= item.Last + item.Step + Epsilon; }
                else return false; // A hole/concavity already crosses the base rectangular envelope.
            }
            item.Low = Math.Max(item.Low, item.First - item.Step);
            item.High = Math.Min(item.High, item.Last + item.Step);
            return item.Low <= item.First - item.Radius + Epsilon && item.High >= item.Last + item.Radius - Epsilon;
        }

        private static bool ReachesBoundary(Geometry demand, Band item, double row, double boundary, bool alongX)
        {
            if (Math.Abs(row - boundary) <= Epsilon) return false;
            var strip = Rectangle(item.A, item.B, Math.Min(row, boundary), Math.Max(row, boundary), alongX);
            var first = Line(item.A, item.B, row, alongX);
            var last = Line(item.A, item.B, boundary, alongX);
            return GeometryKernel.Polygons(GeometryKernel.Intersect(demand, strip))
                .Any(p => GeometryKernel.Intersect(p, first).Length > Epsilon && GeometryKernel.Intersect(p, last).Length > Epsilon);
        }

        private static Geometry Rectangle(double a, double b, double first, double last, bool alongX) =>
            GeometryKernel.Factory.ToGeometry(alongX ? new Envelope(a, b, first, last) : new Envelope(first, last, a, b));
        private static LineString Line(double a, double b, double row, bool alongX) => GeometryKernel.Factory.CreateLineString(new[]
            { alongX ? new Coordinate(a, row) : new Coordinate(row, a), alongX ? new Coordinate(b, row) : new Coordinate(row, b) });

        private static void Unavailable(ReinforcementPlan plan, bool clearAll)
        {
            if (clearAll) foreach (var group in plan.Groups) { group.PathBandStartMm = null; group.PathBandEndMm = null; }
            if (!plan.Diagnostics.Any(d => d.Code == "ZONE_PATH_BAND_UNAVAILABLE"))
                plan.Diagnostics.Add(new PlanDiagnostic { Code = "ZONE_PATH_BAND_UNAVAILABLE",
                    Message = "Часть границ зон не состыкована: мешают края, отверстия или стержни. Стержни не сдвинуты." });
        }

        private sealed class Band
        {
            internal BarGroup Group;
            internal double A, B, First, Last, Step, Radius, Low, High, Start, End;
            internal double NextGap = double.PositiveInfinity, PreviousGap = double.PositiveInfinity;
            internal bool Safe, LowAtBoundary, HighAtBoundary;
            internal readonly List<Band> After = new List<Band>();
        }
    }
}
