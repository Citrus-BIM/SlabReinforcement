using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SlabReinforcement.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SlabReinforcement.Integration
{
    internal static class SlabCoverSettings
    {
        public const string ExistingReinforcementMessage = "Арматура уже есть. Защитный слой меняйте в свойствах плиты, затем откройте команду заново.";

        // These methods use cached DTOs only and are safe on the preview worker.
        public static SlabCoverValues FromSnapshot(SlabGeometrySnapshot slab) =>
            new SlabCoverValues(slab.TopCoverMm, slab.BottomCoverMm, slab.SideCoverMm);

        public static SlabCoverValues Resolve(SlabGeometrySnapshot slab, PreviewParameters parameters) =>
            SlabCoverValues.Resolve(parameters.TopCoverMm, parameters.BottomCoverMm, parameters.EdgeCoverMm,
                FromSnapshot(slab), slab.ThicknessMm);

        // The remaining methods must run in the external command's Revit API context.
        public static SlabCoverValues Read(Floor floor) => new SlabCoverValues(
            ReadDistance(floor, BuiltInParameter.CLEAR_COVER_TOP),
            ReadDistance(floor, BuiltInParameter.CLEAR_COVER_BOTTOM),
            ReadDistance(floor, BuiltInParameter.CLEAR_COVER_OTHER));

        public static bool HasReinforcement(Floor floor)
        {
            var host = RebarHostData.GetRebarHostData(floor);
            if (host == null) throw new InvalidOperationException("Плита не допускает армирование. Проверьте материал и несущие свойства.");
            return host.GetRebarsInHost().Count > 0 || host.GetAreaReinforcementsInHost().Count > 0
                || host.GetPathReinforcementsInHost().Count > 0 || host.GetRebarContainersInHost().Count > 0
                || host.GetFabricAreasInHost().Count > 0 || host.GetFabricSheetsInHost().Count > 0;
        }

        public static void VerifyBeforeWrite(SlabGeometrySnapshot slab, SlabCoverValues selected)
        {
            var current = Read(slab.Host);
            if (!current.Matches(FromSnapshot(slab)))
                throw new InvalidOperationException("Защитный слой изменился. Заново откройте команду.");
            if (!selected.Matches(current) && HasReinforcement(slab.Host))
                throw new InvalidOperationException(ExistingReinforcementMessage);
        }

        /// <summary>Assigns cover type ids on this floor only, inside the caller's transaction.</summary>
        public static bool Apply(Floor floor, SlabCoverValues selected)
        {
            var document = floor.Document;
            if (!document.IsModifiable)
                throw new InvalidOperationException("Не удалось изменить защитный слой. Заново откройте команду.");
            var current = Read(floor);
            if (selected.Matches(current)) return false;
            var types = new FilteredElementCollector(document).OfClass(typeof(RebarCoverType))
                .Cast<RebarCoverType>().ToList();
            Set(BuiltInParameter.CLEAR_COVER_TOP, current.TopMm, selected.TopMm);
            Set(BuiltInParameter.CLEAR_COVER_BOTTOM, current.BottomMm, selected.BottomMm);
            Set(BuiltInParameter.CLEAR_COVER_OTHER, current.SideMm, selected.SideMm);
            if (!Read(floor).Matches(selected))
                throw new InvalidOperationException("Revit не сохранил выбранные защитные слои плиты.");
            return true;

            void Set(BuiltInParameter id, double previousMm, double valueMm)
            {
                if (Math.Abs(previousMm - valueMm) <= SlabCoverValues.ComparisonToleranceMm) return;
                var parameter = floor.get_Parameter(id);
                if (parameter == null || parameter.StorageType != StorageType.ElementId || parameter.IsReadOnly)
                    throw new InvalidOperationException("Не удалось изменить защитный слой. Проверьте доступность плиты для редактирования.");
                var type = types.FirstOrDefault(t => Math.Abs(t.CoverDistance * 304.8 - valueMm) <= SlabCoverValues.ComparisonToleranceMm);
                if (type == null)
                {
                    string stem = "Защитный слой " + valueMm.ToString("0.######", CultureInfo.InvariantCulture) + " мм";
                    var names = new HashSet<string>(types.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
                    string name = stem;
                    for (int suffix = 2; names.Contains(name); suffix++) name = stem + " (" + suffix + ")";
                    type = RebarCoverType.Create(document, name, valueMm / 304.8);
                    if (type == null) throw new InvalidOperationException("Revit не создал тип защитного слоя.");
                    types.Add(type);
                }
                // Never change CoverDistance on a shared type: that would affect other hosts.
                if (!parameter.Set(type.Id))
                    throw new InvalidOperationException("Не удалось назначить защитный слой выбранной плите.");
            }
        }

        private static double ReadDistance(Floor floor, BuiltInParameter id)
        {
            var parameter = floor.get_Parameter(id);
            var cover = parameter == null || parameter.StorageType != StorageType.ElementId
                ? null : floor.Document.GetElement(parameter.AsElementId()) as RebarCoverType;
            if (cover == null) throw new InvalidOperationException("В свойствах плиты задайте защитные слои арматуры.");
            return cover.CoverDistance * 304.8;
        }
    }
}
