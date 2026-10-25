using System;

namespace SlabReinforcement.Core
{
    public static class BarElevation
    {
        // Local Z=0 is the top surface; negative Z points into the host.
        public static double Calculate(bool top, double thicknessMm, double selectedCoverMm,
            double oppositeCoverMm, double diameterMm, double layerOffsetMm)
        {
            foreach (var value in new[] { thicknessMm, selectedCoverMm, oppositeCoverMm, diameterMm, layerOffsetMm })
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                    throw new ArgumentException("Проверьте толщину, защитные слои и смещение: нужны числа не меньше нуля.");
            if (diameterMm <= 0 || thicknessMm <= 0 || selectedCoverMm + oppositeCoverMm + diameterMm + layerOffsetMm > thicknessMm)
                throw new ArgumentException("Арматура не помещается по толщине плиты. Проверьте диаметр и защитные слои.");
            double inset = selectedCoverMm + diameterMm / 2 + layerOffsetMm;
            return top ? -inset : -thicknessMm + inset;
        }
    }
}
