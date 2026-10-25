using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SlabReinforcement.Core;

namespace SlabReinforcement.Integration
{
    // Writes native systems. The caller verifies every physical child against the
    // same plan used by the preview before accepting its TransactionGroup.
    internal sealed class ZonePathWriter
    {
        private readonly Document _document;
        private readonly SlabGeometrySnapshot _slab;
        private readonly SlabCoverValues _covers;
        private readonly ElementId _pathTypeId;
        private readonly ElementId _straightShapeId;
#if !R2019 && !R2020 && !R2021
        private readonly List<PathRows> _expandedPaths = new List<PathRows>();
        private const int MaximumNativePositions = 1002;
#endif

        public ZonePathWriter(Document document, SlabGeometrySnapshot slab, SlabCoverValues covers)
        {
            _document = document; _slab = slab; _covers = covers;
            if (!ReinforcementSettings.GetReinforcementSettings(document).HostStructuralRebar)
                throw new ArgumentException("В настройках армирования по площади и траектории включите создание стержней.");
            _pathTypeId = new FilteredElementCollector(document).OfClass(typeof(PathReinforcementType)).FirstElementId();
            if (_pathTypeId == ElementId.InvalidElementId)
                throw new ArgumentException("Загрузите тип армирования по траектории для создания зон.");
            _straightShapeId = new FilteredElementCollector(document).OfClass(typeof(RebarShape)).Cast<RebarShape>()
                .OrderBy(shape => shape.Name, StringComparer.Ordinal)
                .FirstOrDefault(shape => shape.GetRebarShapeDefinition() is RebarShapeDefinitionBySegments definition
                    && definition.NumberOfSegments == 1)?.Id;
            if (_straightShapeId == null)
                throw new ArgumentException("Загрузите форму прямого стержня для создания зон.");
        }

        public PathReinforcement Create(BarGroup group, RebarBarType type, bool top)
        {
            if (group.Placement == null || group.Count < 2 || (group.Centerline != null && group.Centerline.Count > 0))
                throw new ArgumentException("В зоне должно быть не меньше двух стержней.");
            var distribution = group.Placement.Distribution;
            bool alongX = Math.Abs(distribution.Y - 1) < 1e-8 && Math.Abs(distribution.X) < 1e-8;
            bool alongY = Math.Abs(distribution.X - 1) < 1e-8 && Math.Abs(distribution.Y) < 1e-8;
            if (!alongX && !alongY || Math.Abs(distribution.Z) > 1e-8
                || alongX && Math.Abs(group.End.Y - group.Start.Y) > 1e-6
                || alongY && Math.Abs(group.End.X - group.Start.X) > 1e-6)
                throw new ArgumentException("Направление зоны не совпало с плитой. Заново выберите ребро X.");
            double length = alongX ? group.End.X - group.Start.X : group.End.Y - group.Start.Y;
            if (length <= 0 || group.SpacingMm <= 0)
                throw new ArgumentException("Не определены длина или шаг. Пересчитайте зоны.");
            double diameter = group.Placement.ModelDiameterMm;
            double radius = diameter / 2;
            double span = (group.Count - 1) * group.SpacingMm;
            // Native Path insets the first and last bar axes by their radius from
            // the sketch endpoints. Include those radii so the actual rows agree.
            var anchor = alongX ? group.Start : group.End;
            var first = new Point3(anchor.X - distribution.X * radius, anchor.Y - distribution.Y * radius, 0);
            var last = new Point3(anchor.X + distribution.X * (span + radius), anchor.Y + distribution.Y * (span + radius), 0);
#if !R2019 && !R2020 && !R2021
            bool expanded = UsePathBand(group, alongX, radius, span);
            if (expanded)
            {
                first = alongX ? new Point3(anchor.X, group.PathBandStartMm.Value, 0) : new Point3(group.PathBandStartMm.Value, anchor.Y, 0);
                last = alongX ? new Point3(anchor.X, group.PathBandEndMm.Value, 0) : new Point3(group.PathBandEndMm.Value, anchor.Y, 0);
            }
#endif
            var curve = Line.CreateBound(World(first), World(last));
            if (curve.Length <= _document.Application.ShortCurveTolerance)
                throw new ArgumentException("Зона слишком узкая для Revit. Проверьте шаг.");
            var path = PathReinforcement.Create(_document, _slab.Host, new List<Curve> { curve }, false,
                _pathTypeId, type.Id, ElementId.InvalidElementId, ElementId.InvalidElementId);
            if (path == null) throw new InvalidOperationException("Армирование не создано: Revit не создал зону по траектории.");
            path.PrimaryBarShapeId = _straightShapeId;
            Set(path, BuiltInParameter.PATH_REIN_ALTERNATING, 0);
            Set(path, BuiltInParameter.PATH_REIN_FACE_SLAB, top ? 0 : 1);
            // REBAR_SYSTEM_LAYOUT_RULE uses the native system value 3 for maximum
            // spacing (not the RebarLayoutRule enum used by standalone Rebar).
            Set(path, BuiltInParameter.REBAR_SYSTEM_LAYOUT_RULE, 3);
            Set(path, BuiltInParameter.PATH_REIN_SPACING, group.SpacingMm / 304.8);
            Set(path, BuiltInParameter.PATH_REIN_LENGTH_1, length / 304.8);
            double nativeAxis = top ? -_covers.TopMm - radius : -_slab.ThicknessMm + _covers.BottomMm + radius;
            // This API offset only locates the common section layer (not a user
            // shift of zone reinforcement away from its corresponding background).
            path.AdditionalOffset = (top ? nativeAxis - group.Placement.ElevationMm : group.Placement.ElevationMm - nativeAxis) / 304.8;
#if !R2019 && !R2020 && !R2021
            if (expanded) _expandedPaths.Add(new PathRows { Path = path, Group = group, AlongX = alongX });
#endif
            return path;
        }

        // Must run in the caller's second transaction, after native system children exist.
        // Older Revit versions retain the original outlines: individual system-bar editing
        // was introduced in 2022. Null band metadata also preserves the legacy path.
        public void PreserveRows()
        {
#if !R2019 && !R2020 && !R2021
            foreach (var entry in _expandedPaths)
            {
                var group = entry.Group;
                var children = entry.Path.GetRebarInSystemIds().Select(id => _document.GetElement(id) as RebarInSystem).ToList();
                if (children.Count == 0 || children.Any(child => child == null || !child.CanEditIndividualBars()))
                    throw new InvalidOperationException("Армирование не создано: при стыковке зон сместились ряды.");
                int nativeCount = children.Sum(child => child.NumberOfBarPositions);
                if (nativeCount < group.Count || nativeCount > MaximumNativePositions)
                    throw new InvalidOperationException("Армирование не создано: при стыковке зон изменились стержни.");
                // Revit can split one long straight Path into several RebarInSystem
                // children (for example 1000 + 2 positions). Preserve one global grid.
                var positions = children.SelectMany(child => Enumerable.Range(0, child.NumberOfBarPositions).Select(index =>
                {
                    var curves = child.GetTransformedCenterlineCurves(true, false, false, index);
                    if (curves.Count != 1 || !(curves[0] is Line))
                        throw new InvalidOperationException("Армирование не создано: при стыковке зон изменилась форма стержня.");
                    var midpoint = curves[0].Evaluate(0.5, true);
                    var local = _slab.Frame.ToLocal(new Point3(midpoint.X * 304.8, midpoint.Y * 304.8, midpoint.Z * 304.8));
                    return new NativePosition { Child = child, Index = index, Midpoint = midpoint, Station = entry.AlongX ? local.Y : local.X };
                })).OrderBy(position => position.Station).ToList();
                var selected = Enumerable.Range(0, group.Count)
                    .Select(row => positions[(int)Math.Round(row * (positions.Count - 1d) / (group.Count - 1))]).ToList();
                var included = new HashSet<NativePosition>(selected);
                if (included.Count != group.Count)
                    throw new InvalidOperationException("Армирование не создано: стержни зоны сместились с рассчитанных рядов.");
                foreach (var position in positions)
                    position.Child.SetBarIncluded(included.Contains(position), position.Index);
                for (int row = 0; row < selected.Count; row++)
                {
                    var position = selected[row];
                    var distribution = group.Placement.Distribution;
                    var target = World(new Point3((group.Start.X + group.End.X) / 2 + distribution.X * row * group.SpacingMm,
                        (group.Start.Y + group.End.Y) / 2 + distribution.Y * row * group.SpacingMm, group.Placement.ElevationMm));
                    position.Child.MoveBarInSet(position.Index, Transform.CreateTranslation(target - position.Midpoint));
                }
                // Keep the path's requested spacing/summary. Native raw position count and
                // child interval still describe the unmodified distribution; Quantity and
                // physical readback account for excluded/moved bars and are checked below.
                if (Math.Abs(entry.Path.get_Parameter(BuiltInParameter.PATH_REIN_SPACING).AsDouble() * 304.8 - group.SpacingMm) > 0.1)
                    throw new InvalidOperationException("Армирование не создано: Revit изменил шаг зоны.");
            }
#endif
        }

#if !R2019 && !R2020 && !R2021
        private static bool UsePathBand(BarGroup group, bool alongX, double radius, double span)
        {
            if (!group.PathBandStartMm.HasValue && !group.PathBandEndMm.HasValue) return false;
            double firstRow = alongX ? group.Start.Y : group.Start.X;
            if (!group.PathBandStartMm.HasValue || !group.PathBandEndMm.HasValue
                || !Finite(group.PathBandStartMm.Value) || !Finite(group.PathBandEndMm.Value)
                || group.PathBandStartMm.Value > firstRow - radius + 1e-6
                || group.PathBandEndMm.Value < firstRow + span + radius - 1e-6
                || group.PathBandEndMm.Value <= group.PathBandStartMm.Value)
                throw new ArgumentException("Стержни выходят за границы зоны. Пересчитайте зоны.");
            // The expanded native array can contain extra positions before exclusion.
            // Retain the original outline near Revit's limit, without splitting the zone,
            // changing its physical count or attempting an oversized native array.
            double positionsUpperBound = Math.Ceiling((group.PathBandEndMm.Value - group.PathBandStartMm.Value) / group.SpacingMm - 1e-9) + 1;
            return positionsUpperBound <= MaximumNativePositions;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private sealed class PathRows
        {
            public PathReinforcement Path;
            public BarGroup Group;
            public bool AlongX;
        }
        private sealed class NativePosition
        {
            public RebarInSystem Child;
            public int Index;
            public XYZ Midpoint;
            public double Station;
        }
#endif

        public static HashSet<ElementId> PhysicalIds(IEnumerable<PathReinforcement> paths)
        {
            var ids = new HashSet<ElementId>();
            foreach (var path in paths)
            {
                var children = path.GetRebarInSystemIds();
                if (children.Count == 0)
                    throw new InvalidOperationException("Армирование не создано: зона получилась без стержней. Проверьте настройки армирования по траектории.");
                ids.UnionWith(children);
            }
            return ids;
        }

        private XYZ World(Point3 point) => SlabGeometryReader.ToXyz(_slab.Frame.ToWorld(point));
        private static Parameter Writable(PathReinforcement path, BuiltInParameter id)
        {
            var parameter = path.get_Parameter(id);
            if (parameter == null || parameter.IsReadOnly)
                throw new InvalidOperationException("Не удалось изменить параметр зоны: " + LabelUtils.GetLabelFor(id) + ".");
            return parameter;
        }
        private static void Set(PathReinforcement path, BuiltInParameter id, int value)
        { if (!Writable(path, id).Set(value)) throw new InvalidOperationException("Не удалось задать параметр зоны: " + LabelUtils.GetLabelFor(id) + "."); }
        private static void Set(PathReinforcement path, BuiltInParameter id, double value)
        { if (!Writable(path, id).Set(value)) throw new InvalidOperationException("Не удалось задать параметр зоны: " + LabelUtils.GetLabelFor(id) + "."); }
    }
}
