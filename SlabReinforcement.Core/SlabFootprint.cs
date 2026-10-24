using System;
using System.Collections.Generic;
using System.Linq;

namespace SlabReinforcement.Core
{
    public static class SlabFootprint
    {
        /// <summary>
        /// Confirms top and bottom contours agree when projected along the slab normal.
        /// Returns their intersection so accepted numerical differences never enlarge the host.
        /// Side surfaces must independently be verified as parallel to the normal by the adapter.
        /// </summary>
        public static List<PolygonInput> MatchNormalExtrusion(IEnumerable<PolygonInput> top,
            IEnumerable<PolygonInput> bottom, double toleranceMm = 0.05)
        {
            if (top == null || bottom == null)
                throw new ArgumentException("Не заданы контуры верхней или нижней поверхности плиты.");
            if (!GeometryKernel.Finite(toleranceMm) || toleranceMm < 0)
                throw new ArgumentException("Допуск сравнения контуров должен быть конечным неотрицательным числом.", nameof(toleranceMm));

            var topGeometry = GeometryKernel.Union(top.Select(GeometryKernel.ReadPolygon));
            var bottomGeometry = GeometryKernel.Union(bottom.Select(GeometryKernel.ReadPolygon));
            if (topGeometry.IsEmpty || bottomGeometry.IsEmpty)
                throw new ArgumentException("Контур верхней или нижней поверхности плиты пуст.");

            if (!topGeometry.Buffer(toleranceMm).Covers(bottomGeometry)
                || !bottomGeometry.Buffer(toleranceMm).Covers(topGeometry))
                throw new ArgumentException("Контуры верха и низа плиты не совпадают при проекции по нормали. "
                    + "Наклонные плиты с вертикальными торцами и плиты с изменяющимся сечением пока не поддерживаются.");

            return GeometryKernel.ToInputs(GeometryKernel.Intersect(topGeometry, bottomGeometry));
        }
    }
}
