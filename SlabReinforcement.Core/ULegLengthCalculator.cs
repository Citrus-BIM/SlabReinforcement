using System;

namespace SlabReinforcement.Core
{
    public sealed class ULegLengthResult
    {
        public double BaseAnchorageMm { get; internal set; }
        public double LapMm { get; internal set; }
        public double BendCenterlineRadiusMm { get; internal set; }
        public double LegProjectionMm { get; internal set; }
    }

    public static class ULegLengthCalculator
    {
        /// <summary>
        /// SP 63.13330.2018 (amendments 1, 2): heavy concrete, nonprestressed
        /// hot-rolled/thermomechanically treated periodic A400/A500, full steel
        /// utilization and 100% tension laps in one section (alpha2=2).
        /// Numeric steel grades denote class A, never cold-formed B500.
        /// Returns a straight lap plus the actual bend's centerline radius.
        /// This length calculation does not certify the complete edge connection.
        /// See docs/research/2026-10-25-automatic-u-lap-calculation.md for sources.
        /// </summary>
        public static ULegLengthResult Calculate(double diameterMm, double modelDiameterMm,
            double bendInsideDiameterMm, string concreteClass, double steelGrade)
        {
            if (!Finite(diameterMm)) throw DiameterError();
            // Revit's feet-to-mm conversion can leave nominal 12/32/36/40 with
            // machine noise. Preserve the core's 1e-6 mm input precision.
            diameterMm = Math.Round(diameterMm, 6, MidpointRounding.AwayFromZero);
            if (diameterMm < 6 || diameterMm > 40 || diameterMm > 32 && diameterMm != 36 && diameterMm != 40)
                throw DiameterError();
            if (!Finite(modelDiameterMm) || modelDiameterMm <= 0 || !Finite(bendInsideDiameterMm) || bendInsideDiameterMm <= 0)
                throw new ArgumentException("Для полки П нужны положительные диаметры стержня и загиба.");

            decimal rs;
            if (Finite(steelGrade) && Math.Abs(steelGrade - 400) < 0.0001) rs = 340m;
            else if (Finite(steelGrade) && Math.Abs(steelGrade - 500) < 0.0001) rs = 435m;
            else if (Finite(steelGrade) && Math.Abs(steelGrade - 240) < 0.0001)
                throw new ArgumentException("Нахлёст П для А240 не рассчитывается. Нужен отдельный узел анкеровки.");
            else throw new ArgumentException("Расчёт полки П доступен для А400 и А500. Проверьте класс стали.");

            decimal rbt;
            switch ((concreteClass ?? string.Empty).Trim().ToUpperInvariant().Replace('В', 'B'))
            {
                case "B15": rbt = 0.75m; break;
                case "B20": rbt = 0.90m; break;
                case "B25": rbt = 1.05m; break;
                case "B30": rbt = 1.15m; break;
                default: throw new ArgumentException("Для расчёта полки П-стержня выберите тяжёлый бетон B15, B20, B25 или B30.");
            }
            decimal diameter = (decimal)diameterMm;
            decimal eta2 = diameter <= 32m ? 1m : 0.9m;
            decimal basic = rs * diameter / (4m * 2.5m * eta2 * rbt);
            const decimal alpha2 = 2m;
            // As,cal / As,ef = 1. No reduction for additional anchors or a bent end.
            decimal required = Math.Max(alpha2 * basic, Math.Max(0.4m * alpha2 * basic, Math.Max(20m * diameter, 250m)));
            double lap = (double)(decimal.Ceiling(required / 10m) * 10m);
            double bendRadius = (bendInsideDiameterMm + modelDiameterMm) / 2;
            double projection = Math.Ceiling((lap + bendRadius) / 10) * 10;
            if (!Finite(bendRadius) || !Finite(projection) || projection - bendRadius < lap)
                throw new ArgumentException("Полка П не рассчитана. Проверьте диаметр стержня и загиба.");
            return new ULegLengthResult { BaseAnchorageMm = (double)basic, LapMm = lap,
                BendCenterlineRadiusMm = bendRadius, LegProjectionMm = projection };
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static ArgumentException DiameterError() => new ArgumentException(
            "Поддерживаются диаметры 6–32, 36 и 40 мм. Для других нужен отдельный расчёт.");
    }
}
