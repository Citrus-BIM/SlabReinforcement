using System;

namespace SlabReinforcement.Core
{
    /// <summary>Concrete cover to the outer surface of a bar, in millimetres.</summary>
    public sealed class SlabCoverValues
    {
        public const double ComparisonToleranceMm = 0.000001;

        public double TopMm { get; }
        public double BottomMm { get; }
        public double SideMm { get; }

        public SlabCoverValues(double topMm, double bottomMm, double sideMm)
        {
            foreach (double value in new[] { topMm, bottomMm, sideMm })
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                    throw new ArgumentException("Введите защитные слои числами не меньше нуля.");
            TopMm = topMm;
            BottomMm = bottomMm;
            SideMm = sideMm;
        }

        public static SlabCoverValues Resolve(double? topMm, double? bottomMm, double sideMm,
            SlabCoverValues host, double thicknessMm)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            var result = new SlabCoverValues(topMm ?? host.TopMm, bottomMm ?? host.BottomMm, sideMm);
            if (double.IsNaN(thicknessMm) || double.IsInfinity(thicknessMm) || thicknessMm <= 0
                || result.TopMm + result.BottomMm >= thicknessMm)
                throw new ArgumentException("Между защитными слоями нет места для арматуры. Проверьте толщину плиты.");
            return result;
        }

        public bool Matches(SlabCoverValues other) => other != null
            && Math.Abs(TopMm - other.TopMm) <= ComparisonToleranceMm
            && Math.Abs(BottomMm - other.BottomMm) <= ComparisonToleranceMm
            && Math.Abs(SideMm - other.SideMm) <= ComparisonToleranceMm;
    }
}
