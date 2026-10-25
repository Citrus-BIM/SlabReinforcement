using System;
using Autodesk.Revit.DB;

namespace SlabReinforcement.Integration
{
    /// <summary>Writes optional instance metadata on newly created native reinforcement.</summary>
    internal static class ReinforcementDrawingParameters
    {
        private static readonly Guid ConstructionMark = new Guid("5d369dfb-17a2-4ae2-a1a1-bdfc33ba7405");

        // The caller owns the Revit transaction. For native systems set the
        // partition on the parent; its physical children may expose it read-only.
        public static void Apply(Element target, string partition, Element host)
        {
            if (target == null) throw new ArgumentNullException(nameof(target), "Не найдена арматура для заполнения параметров.");
            if (!string.IsNullOrWhiteSpace(partition))
                SetOptionalInstanceText(target, target.get_Parameter(BuiltInParameter.NUMBER_PARTITION_PARAM), partition);
            ApplyHostMetadata(target, host);
        }

        public static void ApplyHostMetadata(Element target, Element host)
        {
            if (target == null) throw new ArgumentNullException(nameof(target), "Не найдена арматура для заполнения параметров.");
            if (host == null) return;
            var hostMark = host.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            string value = hostMark?.StorageType == StorageType.String ? hostMark.AsString() : null;
            if (string.IsNullOrWhiteSpace(value)) return;
            SetOptionalInstanceText(target, target.get_Parameter(ConstructionMark), value);
        }

        private static void SetOptionalInstanceText(Element target, Parameter parameter, string value)
        {
            // No fallback through the element type: Org.Zone, concrete class,
            // p.m. flags and shape marking may be shared or formula-driven.
            if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.String
                || parameter.Element == null || !parameter.Element.Id.Equals(target.Id)) return;
            if (string.Equals(parameter.AsString(), value, StringComparison.Ordinal)) return;
            if (!parameter.Set(value))
                throw new InvalidOperationException("Не удалось заполнить параметр армирования «" + parameter.Definition.Name + "».");
        }
    }
}
