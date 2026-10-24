using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Buffer;

namespace SlabReinforcement.Core
{
    /// <summary>
    /// Detects demand lobes lost between scanlines. This is a sampling-completeness
    /// guard, not a structural tributary-area calculation. It never changes bars.
    /// </summary>
    internal static class DemandCoverage
    {
        private const double Epsilon = GeometryKernel.Epsilon;

        internal static bool IsCovered(Geometry demand, List<BarGroup> groups,
            double spacing, bool alongX, CancellationToken token) =>
            Uncovered(demand, groups, spacing, alongX, token).Area <= Epsilon * Epsilon;

        internal static Geometry Uncovered(Geometry demand, List<BarGroup> groups,
            double spacing, bool alongX, CancellationToken token, bool halfSpacing = false)
        {
            if (groups.Count == 0) return demand;
            var patches = new List<Geometry>();
            foreach (var group in groups)
            {
                token.ThrowIfCancellationRequested();
                var start = alongX ? group.Start.X : group.Start.Y;
                var end = alongX ? group.End.X : group.End.Y;
                var first = alongX ? group.Start.Y : group.Start.X;
                // Manual geometry retains its original full-pitch sampling envelope.
                // Numeric modes must not credit one bar over a nearly two-pitch strip.
                // Only the outer reach changes; actual array member pitch stays intact.
                // Longitudinal reach is never inflated: a narrow lobe that extends
                // past all adjacent actual bar ends remains outside this envelope.
                var reach = halfSpacing ? spacing / 2 : spacing;
                var low = first - reach;
                var high = first + (group.Count - 1) * group.SpacingMm + reach;
                patches.Add(GeometryKernel.Factory.CreatePolygon(new[]
                {
                    Coordinate(start, low, alongX), Coordinate(end, low, alongX),
                    Coordinate(end, high, alongX), Coordinate(start, high, alongX),
                    Coordinate(start, low, alongX)
                }));
            }
            token.ThrowIfCancellationRequested();
            var footprint = GeometryKernel.Union(patches);
            // Numerical tolerance is one micron, unrelated to the merge tolerance.
            footprint = footprint.Buffer(Epsilon, new BufferParameters { JoinStyle = JoinStyle.Mitre, SimplifyFactor = 0 });
            token.ThrowIfCancellationRequested();
            return GeometryKernel.AreaOnly(GeometryKernel.Difference(demand, footprint));
        }

        private static Coordinate Coordinate(double value, double row, bool alongX) => alongX
            ? new Coordinate(value, row) : new Coordinate(row, value);
    }
}
