using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SlabReinforcement.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace SlabReinforcement.Integration
{
    internal sealed class BarTypeSnapshot
    {
        public string Key, Name;
        public double DiameterMm, ModelDiameterMm, BendDiameterMm;
        public double? SteelGrade;
    }

    internal sealed class ReinforcementSession
    {
        private readonly SlabGeometrySnapshot _slab;
        private readonly ImportedMeshSnapshot _mesh;
        private readonly Dictionary<string, BarTypeSnapshot> _types;
        private readonly List<PlannedBar> _existingBars;
        public readonly Dictionary<string, RebarBarType> RevitTypes;
        private static readonly Guid SteelParameter = new Guid("32a47c7f-e91d-4a8e-bf24-927cb679b4d1");

        public ReinforcementSession(SlabGeometrySnapshot slab, ImportedMeshSnapshot mesh, List<RebarBarType> types)
        {
            _slab = slab; _mesh = mesh;
            _existingBars = ExistingRebarReader.Read(slab);
            RevitTypes = types.ToDictionary(t => t.UniqueId);
            _types = types.Select(t => new BarTypeSnapshot
            {
                Key = t.UniqueId, Name = t.Name,
#if R2019 || R2020 || R2021
                DiameterMm = t.BarDiameter * 304.8,
                ModelDiameterMm = t.BarDiameter * 304.8,
#else
                DiameterMm = t.BarNominalDiameter * 304.8,
                ModelDiameterMm = t.BarModelDiameter * 304.8,
#endif
                BendDiameterMm = t.StandardBendDiameter * 304.8,
                SteelGrade = ReadSteelGrade(t)
            }).ToDictionary(t => t.Key);
        }

        public PreviewContext CreateContext() => new PreviewContext
        {
            HostName = _slab.Host.Name, AxisDescription = _slab.AxisDescription,
            Slabs = _slab.Slabs, SourceGroups = _mesh.Groups,
            TopCoverMm = _slab.TopCoverMm, BottomCoverMm = _slab.BottomCoverMm, SideCoverMm = _slab.SideCoverMm,
            Warnings = _mesh.Warnings.Concat(new[] { "Анкеровка — по встроенной таблице." }).ToList(),
            RebarTypeKeys = _types.Values.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.Single().Key),
            Calculate = Calculate
        };

        public static string SpecKey(PreviewSelection selection) => selection.SpecKey + "|" + selection.ColorKey;

        private ReinforcementPlan Calculate(PreviewParameters parameters, CancellationToken token)
        {
            // Worker code: cached numbers and DTOs only, no Revit API access.
            var combined = new ReinforcementPlan();
            if (parameters.EdgeCoverMm < _slab.SideCoverMm - 0.000001)
                return new ReinforcementPlan
                {
                    Diagnostics = new List<PlanDiagnostic> { new PlanDiagnostic
                    {
                        Code = "HOST_SIDE_COVER", IsBlocking = true,
                        Message = "Отступ от края не может быть меньше бокового защитного слоя плиты (" + _slab.SideCoverMm.ToString("0.##", CultureInfo.CurrentCulture) + " мм)."
                    } }
                };
            PerimeterRequest perimeter = null;
            if (parameters.Perimeter?.Enabled == true)
            {
                if (!_types.TryGetValue(parameters.Perimeter.SpecKey ?? string.Empty, out var type))
                    Problem(combined, "PERIMETER_TYPE", "Выберите тип обрамления.");
                else
                {
                    perimeter = new PerimeterRequest
                    {
                        Slabs = _slab.Slabs, TypeKey = type.Key, DiameterMm = type.DiameterMm,
                        ModelDiameterMm = type.ModelDiameterMm, BendDiameterMm = type.BendDiameterMm,
                        ThicknessMm = _slab.ThicknessMm, TopCoverMm = _slab.TopCoverMm, BottomCoverMm = _slab.BottomCoverMm,
                        SideCoverMm = parameters.EdgeCoverMm + 0.05, Outer = parameters.Perimeter.Outer, Openings = parameters.Perimeter.Openings,
                        // The MVP supports U-bars only, including requests restored from older settings.
                        UseUbars = true, UseLongitudinalBars = false,
                        SpacingMm = parameters.Perimeter.SpacingMm, LegLengthMm = parameters.Perimeter.LegLengthMm
                    };
                    Append(combined, PerimeterPlanner.Build(perimeter, token));
                }
            }
            var background = new BackgroundRequest
            {
                Slabs = _slab.Slabs, ThicknessMm = _slab.ThicknessMm, TopCoverMm = _slab.TopCoverMm,
                BottomCoverMm = _slab.BottomCoverMm, SideCoverMm = parameters.EdgeCoverMm + 0.05 + (perimeter?.UseUbars == true ? perimeter.ModelDiameterMm : 0),
                OuterLayerOffsetMm = perimeter == null ? 0 : PerimeterPlanner.ReservedDepth(perimeter)
            };
            foreach (var selected in parameters.BackgroundLayers.Where(l => l.Enabled))
            {
                if (!_types.TryGetValue(selected.SpecKey ?? string.Empty, out var type))
                { Problem(combined, "BACKGROUND_TYPE", "Выберите тип фона «" + selected.Direction + "»."); continue; }
                if (!new[] { "Низ X", "Низ Y", "Верх X", "Верх Y" }.Contains(selected.Direction))
                { Problem(combined, "BACKGROUND_DIRECTION", "Неизвестное направление фона."); continue; }
                background.Layers.Add(new BackgroundLayer
                {
                    Key = "Background|" + selected.Direction, TypeKey = type.Key, Top = selected.Direction.StartsWith("Верх", StringComparison.Ordinal),
                    AlongX = selected.Direction.EndsWith("X", StringComparison.Ordinal), DiameterMm = type.DiameterMm,
                    ModelDiameterMm = type.ModelDiameterMm, SpacingMm = selected.SpacingMm
                });
            }
            if (background.Layers.Count > 0) Append(combined, BackgroundPlanner.Build(background, token));
            if (parameters.IncludeZones) Append(combined, CalculateZones(parameters, background, combined, token));
            if (combined.Bars.Count == 0 && !combined.Diagnostics.Any(d => d.IsBlocking))
                Problem(combined, "EMPTY_PLAN", "Включите фон, обрамление или зоны армирования.");
            if (!combined.Diagnostics.Any(d => d.IsBlocking))
                combined.Diagnostics.AddRange(CompositePlanValidator.Validate(_existingBars.Concat(combined.Bars), token, _existingBars.Count));
            return combined;
        }

        private ReinforcementPlan CalculateZones(PreviewParameters parameters, BackgroundRequest background, ReinforcementPlan combined, CancellationToken token)
        {
            var invalid = new ReinforcementPlan();
            if (parameters.Selections == null || parameters.Selections.Count == 0)
            { Problem(invalid, "NO_ZONE_SELECTION", "Выберите хотя бы один цвет для зон армирования."); return invalid; }
            if (!parameters.CalculationAxesConfirmed)
            { Problem(invalid, "CALCULATION_AXES", "Подтвердите соответствие расчётных осей выбранному ребру плиты."); return invalid; }
            var sourceGroup = _mesh.Groups.SingleOrDefault(g => g.Key == parameters.SourceGroupKey);
            if (sourceGroup == null)
            { Problem(invalid, "SOURCE_PLANE", "Выберите слой и плоскость расчётной подложки."); return invalid; }
            if (!new[] { "Manual", "AdditionalArea", "TotalArea" }.Contains(parameters.DemandMeaning))
            { Problem(invalid, "DEMAND_MEANING", "Выберите смысл значений расчётной карты."); return invalid; }
            bool alongX = parameters.Direction?.EndsWith("X", StringComparison.Ordinal) == true;
            bool top = parameters.Direction?.StartsWith("Верх", StringComparison.Ordinal) == true;
            var matchingBackground = background.Layers.SingleOrDefault(l => l.Top == top && l.AlongX == alongX);
            if (parameters.DemandMeaning == "TotalArea" && matchingBackground != null)
            {
                var positiveColors = new HashSet<string>(parameters.Selections.Where(s => s.RequiredAreaMm2PerM > 0).Select(s => s.ColorKey), StringComparer.Ordinal);
                if (positiveColors.Count > 0)
                    invalid.Diagnostics.AddRange(BackgroundCoverageValidator.Validate(
                        sourceGroup.Faces.Where(f => positiveColors.Contains(f.ColorKey)), _slab.Slabs,
                        combined.Bars.Where(b => b.SpecKey == matchingBackground.Key), matchingBackground.SpacingMm, alongX, token));
                if (invalid.Diagnostics.Any(d => d.IsBlocking)) return invalid;
            }
            var request = new PlanRequest
            {
                Slabs = _slab.Slabs,
                Faces = sourceGroup.Faces.Where(f => parameters.Selections.Any(s => s.ColorKey == f.ColorKey)).ToList(),
                Options = new PlanOptions
                {
                    AlongX = alongX,
                    // Reserve the slab arc tessellation tolerance (0.05 mm) on the safe side.
                    EdgeCoverMm = parameters.EdgeCoverMm + 0.05, MergeToleranceMm = parameters.MergeToleranceMm,
                    RoundIncrementMm = parameters.RoundIncrementMm, UseCutLengths = parameters.UseCutLengths,
                    CoverageHalfSpacing = parameters.DemandMeaning != "Manual",
                    GridOffsetMm = matchingBackground == null ? 0 : matchingBackground.SpacingMm / 2
                }
            };
            var placements = new Dictionary<string, BarPlacement>();
            foreach (var selection in parameters.Selections)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!_types.TryGetValue(selection.SpecKey, out var type)) throw new ArgumentException("Выбранный тип арматуры отсутствует в документе.");
                    if (!type.SteelGrade.HasValue) throw new ArgumentException("У типа «" + type.Name + "» не задан параметр класса стали (GUID " + SteelParameter + ").");
                    if (Math.Abs(type.SteelGrade.Value - 240) < 0.0001)
                        throw new ArgumentException("Для гладкой A240 нужна анкеровка крюком или другим принятым узлом. Прямые стержни зон этого MVP не поддерживают такой узел.");
                    var backgroundPlacement = matchingBackground == null ? null : combined.Bars.FirstOrDefault(b => b.SpecKey == matchingBackground.Key)?.Placement;
                    double z = backgroundPlacement?.ElevationMm ?? BarElevation.Calculate(top, _slab.ThicknessMm,
                        top ? _slab.TopCoverMm : _slab.BottomCoverMm, top ? _slab.BottomCoverMm : _slab.TopCoverMm,
                        type.ModelDiameterMm, background.OuterLayerOffsetMm + parameters.LayerOffsetMm);
                    if (backgroundPlacement != null) z += (top ? -1 : 1) * parameters.LayerOffsetMm;
                    if (z - type.ModelDiameterMm / 2 < -_slab.ThicknessMm + _slab.BottomCoverMm - 1e-6
                        || z + type.ModelDiameterMm / 2 > -_slab.TopCoverMm + 1e-6)
                        throw new ArgumentException("Дополнительная арматура не помещается по защитным слоям.");
                    if (parameters.DemandMeaning != "Manual")
                    {
                        if (!selection.RequiredAreaMm2PerM.HasValue || !Finite(selection.RequiredAreaMm2PerM.Value) || selection.RequiredAreaMm2PerM.Value < 0)
                            throw new ArgumentException("Задайте требуемую площадь для цвета в мм²/м; для диапазона — его верхнюю границу.");
                        double baseArea = 0;
                        if (parameters.DemandMeaning == "TotalArea")
                        {
                            if (matchingBackground == null || backgroundPlacement == null)
                                throw new ArgumentException("Для полной As включите фон этого направления в той же операции.");
                            var bgType = _types[matchingBackground.TypeKey];
                            if (bgType.SteelGrade != type.SteelGrade || parameters.LayerOffsetMm > 1e-6
                                || Math.Abs(bgType.ModelDiameterMm - type.ModelDiameterMm) > 1e-6)
                                throw new ArgumentException("Вычитание фона требует одинакового класса стали, физического диаметра и высоты ряда. Иначе используйте проверенную дополнительную As.");
                            baseArea = Area(bgType.DiameterMm, matchingBackground.SpacingMm);
                        }
                        double deficit = Math.Max(0, selection.RequiredAreaMm2PerM.Value - baseArea);
                        if (deficit <= 1e-6) continue;
                        if (Area(type.DiameterMm, selection.SpacingMm) + 1e-6 < deficit)
                            throw new ArgumentException("Назначенная арматура не покрывает дефицит " + deficit.ToString("0.##", CultureInfo.CurrentCulture) + " мм²/м.");
                    }
                    placements[SpecKey(selection)] = new BarPlacement
                    {
                        TypeKey = type.Key, Component = "Zones", ElevationMm = z, ModelDiameterMm = type.ModelDiameterMm,
                        Distribution = alongX ? new Point3(0, 1, 0) : new Point3(1, 0, 0)
                    };
                    request.Specifications.Add(new ReinforcementSpec
                    {
                        Key = SpecKey(selection), ColorKey = selection.ColorKey, TypeKey = type.Key,
                        MaterialKey = type.SteelGrade.Value.ToString("0.####", CultureInfo.InvariantCulture),
                        DiameterMm = type.DiameterMm, SpacingMm = selection.SpacingMm,
                        ModelDiameterMm = type.ModelDiameterMm,
                        AnchorageMm = LegacyAnchorageTable.GetLength(type.DiameterMm, parameters.ConcreteClass, type.SteelGrade.Value)
                    });
                }
                catch (ArgumentException ex)
                {
                    invalid.Diagnostics.Add(new PlanDiagnostic { Code = "SPECIFICATION", SourceId = selection.ColorKey, Message = ex.Message, IsBlocking = true });
                }
            }
            if (invalid.Diagnostics.Count > 0) return invalid;
            request.Faces = request.Faces.Where(f => request.Specifications.Any(s => s.ColorKey == f.ColorKey)).ToList();
            if (request.Specifications.Count == 0)
            {
                invalid.Diagnostics.Add(new PlanDiagnostic { Code = "BACKGROUND_SUFFICIENT", Message = "По номинальной площади фон покрывает выбранные цвета. Анкеровка в опорах проверяется отдельно." });
                return invalid;
            }
            var plan = ReinforcementPlanner.Build(request, token);
            foreach (var bar in plan.Bars) bar.Placement = placements[bar.SpecKey];
            foreach (var group in plan.Groups) group.Placement = placements[group.SpecKey];
            if (parameters.DemandMeaning == "Manual")
                plan.Diagnostics.Add(new PlanDiagnostic { Code = "MANUAL_PLACEMENT", Message = "Ручная раскладка: численная требуемая As не задана." });
            return plan;
        }

        private static double Area(double diameter, double spacing) => Math.PI * diameter * diameter / 4 * 1000 / spacing;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static void Problem(ReinforcementPlan plan, string code, string message) => plan.Diagnostics.Add(new PlanDiagnostic { Code = code, Message = message, IsBlocking = true });
        private static void Append(ReinforcementPlan target, ReinforcementPlan part)
        { target.Bars.AddRange(part.Bars); target.Groups.AddRange(part.Groups); target.Diagnostics.AddRange(part.Diagnostics); target.DemandRegions.AddRange(part.DemandRegions); }

        private static double? ReadSteelGrade(RebarBarType type)
        {
            var p = type.get_Parameter(SteelParameter);
            if (p == null || !p.HasValue) return null;
            if (p.StorageType == StorageType.Double) return p.AsDouble();
            if (p.StorageType == StorageType.Integer) return p.AsInteger();
            if (p.StorageType == StorageType.String && double.TryParse(p.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double grade)) return grade;
            return null;
        }
    }
}
