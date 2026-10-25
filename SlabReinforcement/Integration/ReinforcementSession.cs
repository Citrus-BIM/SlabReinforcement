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
        private readonly bool _hasExistingReinforcement;
        public readonly Dictionary<string, RebarBarType> RevitTypes;
        private static readonly Guid SteelParameter = new Guid("32a47c7f-e91d-4a8e-bf24-927cb679b4d1");

        public ReinforcementSession(SlabGeometrySnapshot slab, ImportedMeshSnapshot mesh, List<RebarBarType> types)
        {
            _slab = slab; _mesh = mesh;
            _hasExistingReinforcement = SlabCoverSettings.HasReinforcement(slab.Host);
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
            Warnings = _mesh.Warnings.Concat(new[] { "Анкеровка — по таблице. Прочность узлов с П-окончаниями зон проверьте отдельно." }).ToList(),
            RebarTypeKeys = _types.Values.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.Single().Key),
            Calculate = Calculate
        };

        public static string SpecKey(PreviewSelection selection) => selection.SpecKey + "|" + selection.ColorKey;

        private ReinforcementPlan Calculate(PreviewParameters parameters, CancellationToken token)
        {
            // Worker code: cached numbers and DTOs only, no Revit API access.
            var combined = new ReinforcementPlan();
            if(!Enum.IsDefined(typeof(ReinforcementCreationScope),parameters.CreationScope))
            { Problem(combined,"CREATION_SCOPE","Выберите вкладку фона или зон и пересчитайте.");return combined; }
            bool zonesOnly=parameters.CreationScope==ReinforcementCreationScope.Zones;
            bool createMain=!zonesOnly;
            bool createZones=zonesOnly||parameters.CreationScope==ReinforcementCreationScope.All&&parameters.IncludeZones;
            SlabCoverValues covers;
            try { covers = SlabCoverSettings.Resolve(_slab, parameters); }
            catch (ArgumentException exception)
            {
                Problem(combined, "INVALID_HOST_COVER", exception.Message);
                return combined;
            }
            if (_hasExistingReinforcement && !covers.Matches(SlabCoverSettings.FromSnapshot(_slab)))
            {
                Problem(combined, "EXISTING_REBAR_COVER_CHANGE", SlabCoverSettings.ExistingReinforcementMessage);
                return combined;
            }
            var background = new BackgroundRequest
            {
                Slabs = _slab.Slabs, ThicknessMm = _slab.ThicknessMm, TopCoverMm = covers.TopMm,
                BottomCoverMm = covers.BottomMm, SideCoverMm = covers.SideMm + 0.05,
                // The background defines the slab layers; U-bars follow it, never shift it.
                OuterLayerOffsetMm = 0
            };
            var configuredBackground = new BackgroundRequest
            {
                Slabs = background.Slabs, ThicknessMm = background.ThicknessMm, TopCoverMm = background.TopCoverMm,
                BottomCoverMm = background.BottomCoverMm, SideCoverMm = background.SideCoverMm, OuterLayerOffsetMm = background.OuterLayerOffsetMm
            };
            var placementDefinitions=new List<BackgroundLayer>();
            foreach (var selected in parameters.BackgroundLayers)
            {
                bool createLayer=createMain&&selected.Enabled;
                if (!_types.TryGetValue(selected.SpecKey ?? string.Empty, out var type))
                { if (createLayer) Problem(combined, "BACKGROUND_TYPE", "Выберите тип арматуры для фона «" + selected.Direction + "»."); continue; }
                if (!new[] { "Низ X", "Низ Y", "Верх X", "Верх Y" }.Contains(selected.Direction))
                { if (createLayer) Problem(combined, "BACKGROUND_DIRECTION", "Выберите слой фона: низ или верх, X или Y."); continue; }
                var layer = new BackgroundLayer
                {
                    Key = "Background|" + selected.Direction, TypeKey = type.Key, Top = selected.Direction.StartsWith("Верх", StringComparison.Ordinal),
                    AlongX = selected.Direction.EndsWith("X", StringComparison.Ordinal), DiameterMm = type.DiameterMm,
                    ModelDiameterMm = type.ModelDiameterMm, SpacingMm = selected.SpacingMm
                };
                // A zone Y needs the configured outer X diameter, not an enabled
                // new X layer or a valid, unused background spacing value.
                placementDefinitions.Add(layer);
                if (!Finite(selected.SpacingMm) || selected.SpacingMm <= 0)
                { if (createLayer) Problem(combined, "BACKGROUND_SPACING", "Шаг фона должен быть больше нуля."); continue; }
                configuredBackground.Layers.Add(layer);
                if (createLayer) background.Layers.Add(layer);
            }
            // Layer position is defined by all four configured descriptors, independently
            // of which layers are created in this operation.
            background.PlacementLayers = zonesOnly?placementDefinitions:configuredBackground.Layers;
            var reusedBackgroundKeys = new HashSet<string>();
            if (_existingBars.Count > 0 && configuredBackground.Layers.Count > 0)
            {
                foreach (var layer in configuredBackground.Layers)
                {
                    if(parameters.CreationScope!=ReinforcementCreationScope.All)
                    {
                        bool relevant=zonesOnly?layer.Key=="Background|"+parameters.Direction:
                            background.Layers.Any(l=>l.Key==layer.Key)
                            ||parameters.Perimeter?.Enabled==true&&!layer.Top
                            ||layer.AlongX&&background.Layers.Any(l=>!l.AlongX&&l.Top==layer.Top);
                        if(!relevant)continue;
                    }
                    // A disabled X row must not move the expected Y row. Read candidate
                    // elevations from actual straight bars, then verify the whole layer.
                    var candidateRequest = new BackgroundRequest
                    {
                        Slabs = background.Slabs, ThicknessMm = background.ThicknessMm, TopCoverMm = background.TopCoverMm,
                        BottomCoverMm = background.BottomCoverMm, SideCoverMm = background.SideCoverMm,
                        OuterLayerOffsetMm = background.OuterLayerOffsetMm, Layers = new List<BackgroundLayer> { layer },
                        // The candidate proves XY/type identity; its Z is replaced
                        // with measured elevations below. An existing Y layer does
                        // not depend on a configured virtual X layer to be recognized.
                        PlacementLayers = zonesOnly?new List<BackgroundLayer>():configuredBackground.Layers
                    };
                    var candidate = BackgroundPlanner.Build(candidateRequest, token);
                    if (!candidate.CanCreate) continue;
                    var existingRows = _existingBars.Where(b => Math.Abs(b.Placement.ModelDiameterMm - layer.ModelDiameterMm) < 0.000001 && b.Centerline?.Count == 1
                        && !b.Centerline[0].Midpoint.HasValue && Math.Abs(b.Centerline[0].Start.Z - b.Centerline[0].End.Z) < 0.0001
                        && (layer.Top ? b.Centerline[0].Start.Z >= -_slab.ThicknessMm / 2 - 0.000001 : b.Centerline[0].Start.Z <= -_slab.ThicknessMm / 2 + 0.000001)
                        && (layer.AlongX ? Math.Abs(b.Centerline[0].Start.Y - b.Centerline[0].End.Y) : Math.Abs(b.Centerline[0].Start.X - b.Centerline[0].End.X)) < 0.0001)
                        .ToList();
                    var elevations = existingRows.Select(b => b.Centerline[0].Start.Z).GroupBy(z => Math.Round(z, 6)).Select(g => g.First()).ToList();
                    if (elevations.Count > 64) { Problem(combined, "BACKGROUND_LEVELS", "Не удалось распознать фон: слишком много высот стержней. Проверьте слои."); continue; }
                    double? foundElevation = null;
                    ExistingReinforcementMatchResult foundMatch = null;
                    int partialMatches = 0;
                    foreach (double elevation in elevations)
                    {
                        foreach (var bar in candidate.Bars) bar.Placement.ElevationMm = elevation;
                        var match = ExistingReinforcementMatcher.Match(candidate.Bars, _existingBars, cancellationToken: token);
                        partialMatches = Math.Max(partialMatches, match.MatchedCount);
                        if (!match.IsComplete) continue;
                        if (foundElevation.HasValue)
                        { Problem(combined, "BACKGROUND_AMBIGUOUS", "Найдено несколько слоёв фона «" + layer.Key.Replace("Background|", "") + "». Проверьте их типы и высоты."); foundElevation = null; break; }
                        foundElevation = elevation;
                        foundMatch = match;
                    }
                    if (foundElevation.HasValue)
                    {
                        reusedBackgroundKeys.Add(layer.Key);
                        // Keep measured endpoints and elevations for the subsequent U layout.
                        // Candidate geometry establishes identity only; it is not an exact readback.
                        foreach (var match in foundMatch.Matches)
                            combined.ReferenceBars.Add(BackgroundReference(_existingBars[match.ActualIndex], layer));
                        if (!background.Layers.Any(l => l.Key == layer.Key)) background.Layers.Add(layer);
                    }
                    else if (partialMatches > 0 && (createZones && layer.Key == "Background|" + parameters.Direction
                        || background.Layers.Any(l => l.Key == layer.Key)))
                        Problem(combined, "BACKGROUND_PARTIAL_MATCH", "Существующий фон «" + layer.Key.Replace("Background|", "") + "» не совпал. Проверьте тип, шаг, высоту и полноту слоя.");
                    else if (createZones && layer.Key == "Background|" + parameters.Direction || background.Layers.Any(l => l.Key == layer.Key))
                    {
                        bool differentType = false;
                        foreach (var actualType in existingRows.Where(b => b.Placement.TypeKey != layer.TypeKey).GroupBy(b => b.Placement.TypeKey))
                        {
                            foreach (double elevation in actualType.Select(b => b.Centerline[0].Start.Z).GroupBy(z => Math.Round(z, 6)).Select(g => g.First()))
                            {
                                foreach (var bar in candidate.Bars)
                                { bar.Placement.TypeKey = actualType.Key; bar.Placement.ElevationMm = elevation; }
                                if (ExistingReinforcementMatcher.Match(candidate.Bars, _existingBars, cancellationToken: token).MatchedCount > 0)
                                { differentType = true; break; }
                            }
                            if (differentType) break;
                        }
                        if (differentType) Problem(combined, "BACKGROUND_EXISTING_MISMATCH", "Фон уже есть, но другого типа. Выберите его тип в настройках.");
                    }
                }
            }
            if (createMain&&background.Layers.Count > 0)
            {
                var backgroundPlan = BackgroundPlanner.Build(background, token);
                MarkPreviewPurpose(backgroundPlan, PreviewPurpose.Background);
                // Reference rows retain the measured elevation/grid, even if another direction is added now.
                foreach (var key in reusedBackgroundKeys)
                {
                    var expected = backgroundPlan.Bars.FirstOrDefault(b => b.SpecKey == key);
                    var reference = combined.ReferenceBars.First(b => b.SpecKey == key);
                    bool top = background.Layers.First(l => l.Key == key).Top;
                    if (expected != null && Math.Abs(expected.Placement.ElevationMm - reference.Placement.ElevationMm) > 0.1
                        && background.Layers.Any(l => l.Top == top && !reusedBackgroundKeys.Contains(l.Key)))
                        Problem(combined, "BACKGROUND_STACK_MISMATCH", "Высота нового фона не совпала с существующим. Проверьте типы X/Y.");
                    backgroundPlan.Bars.RemoveAll(b => b.SpecKey == key);
                    backgroundPlan.Groups.RemoveAll(g => g.SpecKey == key);
                }
                Append(combined, backgroundPlan);
            }
            if (reusedBackgroundKeys.Count > 0)
                combined.Diagnostics.Add(new PlanDiagnostic { Code = "BACKGROUND_REUSED", Message = "Фон уже создан. Совпадение проверено." });
            if (combined.Diagnostics.Any(d => d.IsBlocking)) return combined;
            var perimeterPlan = createMain?CalculatePerimeter(parameters, covers, configuredBackground.Layers, combined, token):null;
            if (combined.Diagnostics.Any(d => d.IsBlocking)) return combined;
            if (createZones)
            {
                var zones = CalculateZones(parameters, background, combined, perimeterPlan, token);
                // Explicitly skipped one-row zones do not invalidate valid new background/U bars.
                // An operation with no new bars, or any other zone failure, remains blocked.
                if (combined.Bars.Count > 0 && zones.Bars.Count == 0 && zones.Groups.Count == 0
                    && zones.Diagnostics.Any(d => d.Code == "SINGLE_ROW_ZONES_SKIPPED" && !d.IsBlocking)
                    && zones.Diagnostics.All(d => !d.IsBlocking || d.Code == "NO_BARS"))
                    zones.Diagnostics.RemoveAll(d => d.Code == "NO_BARS" && d.IsBlocking);
                Append(combined, zones);
            }
            if (combined.Bars.Count == 0 && !combined.Diagnostics.Any(d => d.IsBlocking))
                Problem(combined, "EMPTY_PLAN", combined.ReferenceBars.Count > 0
                    ? "Новых стержней нет. Учтена существующая арматура."
                    : "Выберите фон, П-обрамление фона или цвета зон.");
            if (!combined.Diagnostics.Any(d => d.IsBlocking))
                combined.Diagnostics.AddRange(CompositePlanValidator.Validate(_existingBars.Concat(combined.Bars), token, _existingBars.Count,
                    allowBarIntersections: true));
            return combined;
        }

        private ReinforcementPlan CalculatePerimeter(PreviewParameters parameters, SlabCoverValues covers,
            List<BackgroundLayer> configuredLayers, ReinforcementPlan combined, CancellationToken token)
        {
            var options = parameters.Perimeter ?? new PreviewPerimeterOptions();
            if (!options.Enabled) return null;

            var backgroundBars = combined.Bars.Concat(combined.ReferenceBars)
                .Where(b => b.Placement?.Component == "Background").ToList();
            var lowerKeys = new HashSet<string>(configuredLayers.Where(l => !l.Top).Select(l => l.Key));
            bool hasLowerBackground = backgroundBars.Any(b => lowerKeys.Contains(b.SpecKey));
            if (!_types.TryGetValue(options.SpecKey ?? string.Empty, out var type))
            {
                Problem(combined, "PERIMETER_TYPE", "Выберите тип арматуры для П-обрамления фона.");
                return null;
            }
            double legLength = options.LegLengthMm;
            if (parameters.AutomaticULegLength)
            {
                try
                {
                    var length = ULength(type, parameters.ConcreteClass);
                    legLength = length.LegProjectionMm;
                    combined.Diagnostics.Add(ULengthDiagnostic("PERIMETER_U_LENGTH", "П-обрамление фона", type, length));
                }
                catch (ArgumentException exception) { Problem(combined, "PERIMETER_U_LENGTH", exception.Message); return null; }
            }
            var request = new PerimeterRequest
            {
                Slabs = _slab.Slabs, TypeKey = type.Key, DiameterMm = type.DiameterMm,
                ModelDiameterMm = type.ModelDiameterMm, BendDiameterMm = type.BendDiameterMm,
                ThicknessMm = _slab.ThicknessMm, TopCoverMm = covers.TopMm, BottomCoverMm = covers.BottomMm,
                SideCoverMm = covers.SideMm + 0.05, Outer = options.Outer, Openings = options.Openings,
                UseUbars = true, UseLongitudinalBars = false,
                // Existing lower rows remain authoritative. Missing sides use the manual step.
                SpacingMm = options.SpacingMm, LegLengthMm = legLength,
                BackgroundPlan = hasLowerBackground ? new ReinforcementPlan { Bars = backgroundBars } : null,
                BackgroundLayers = configuredLayers,
                CompleteMissingBackgroundEdges = true
            };
            var plan = PerimeterPlanner.Build(request, token);
            MarkPreviewPurpose(plan, PreviewPurpose.Background);
            if (!plan.CanCreate)
            {
                Append(combined, plan);
                return plan;
            }
            var matchResult = ExistingReinforcementMatcher.Match(plan.Bars, _existingBars, cancellationToken: token);
            // A U shape does not identify a complete slab perimeter: it may be a
            // local zone ending. Reuse only verified physical matches and add the
            // missing positions, preserving all unrelated existing reinforcement.
            Append(combined, MissingPerimeterBars(plan, matchResult, token));
            // The zone-end geometry reader needs the complete perimeter, including
            // positions supplied by existing bars. Only the appended plan is reduced.
            return plan;
        }

        private static ReinforcementPlan MissingPerimeterBars(ReinforcementPlan plan,
            ExistingReinforcementMatchResult matches, CancellationToken token)
        {
            if (matches.MatchedCount == 0) return plan;
            var reused = new HashSet<string>(matches.Matches.Select(m => plan.Bars[m.ExpectedIndex].SpecKey), StringComparer.Ordinal);
            var result = new ReinforcementPlan
            {
                Bars = plan.Bars.Where(b => !reused.Contains(b.SpecKey)).ToList(),
                ReferenceBars = plan.Bars.Where(b => reused.Contains(b.SpecKey)).ToList(),
                PerimeterUEdges = plan.PerimeterUEdges,
                Diagnostics = plan.Diagnostics.ToList()
            };
            if (result.Bars.Count > 0)
            {
                var bars = plan.Bars.ToDictionary(b => b.SpecKey, StringComparer.Ordinal);
                var locations = new Dictionary<string, (PerimeterUEdge Edge, int Offset)>(StringComparer.Ordinal);
                foreach (var edge in plan.PerimeterUEdges)
                    for (int i = 0; i < edge.BarKeys.Count; i++) locations.Add(edge.BarKeys[i], (edge, i));
                foreach (var group in plan.Groups)
                {
                    token.ThrowIfCancellationRequested();
                    if (!locations.TryGetValue(group.SpecKey, out var location)) return InvalidPerimeterReuse(result);
                    var keys = location.Edge.BarKeys;
                    int direction = 1;
                    bool Fits(int offset, int sign)
                    {
                        int index = location.Offset + sign * offset;
                        return index >= 0 && index < keys.Count && bars.TryGetValue(keys[index], out var member)
                            && IsPerimeterGroupMember(group, member, offset);
                    }
                    // Background bars are interleaved between opposite ends in
                    // plan.Bars. Edge stations give the actual order of each array;
                    // that order may be reversed relative to its distribution vector.
                    if (group.Count > 1 && !Fits(1, direction)) direction = -1;
                    for (int i = 0; i < group.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!Fits(i, direction)) return InvalidPerimeterReuse(result);
                    }
                    for (int offset = 0; offset < group.Count;)
                    {
                        if (reused.Contains(keys[location.Offset + direction * offset])) { offset++; continue; }
                        int start = offset++;
                        while (offset < group.Count && !reused.Contains(keys[location.Offset + direction * offset])) offset++;
                        var first = bars[keys[location.Offset + direction * start]];
                        result.Groups.Add(new BarGroup
                        {
                            Start = first.Start, End = first.End, SpecKey = first.SpecKey, ColorKey = first.ColorKey,
                            Placement = first.Placement, Centerline = first.Centerline, PreviewPurpose = group.PreviewPurpose,
                            Count = offset - start, SpacingMm = group.SpacingMm, LayoutRule = group.LayoutRule,
                            MaximumSpacingMm = group.MaximumSpacingMm, ArrayLengthMm = (offset - start - 1) * group.SpacingMm
                        });
                    }
                }
                if (result.Groups.Sum(g => g.Count) != result.Bars.Count) return InvalidPerimeterReuse(result);
            }
            result.Diagnostics.Add(new PlanDiagnostic { Code = "PERIMETER_REUSED",
                Message = result.Bars.Count == 0
                    ? "П-стержни уже есть: " + reused.Count + ". Добавлять нечего."
                    : "П-стержней уже есть: " + reused.Count + ". Добавится: " + result.Bars.Count + "." });
            return result;
        }

        private static ReinforcementPlan InvalidPerimeterReuse(ReinforcementPlan plan)
        {
            Problem(plan, "PERIMETER_REUSE_LAYOUT", "Не удалось исключить повторные П-стержни. Пересчитайте П-обрамление фона.");
            return plan;
        }

        private static bool IsPerimeterGroupMember(BarGroup group, PlannedBar bar, int offset)
        {
            if (group.Placement == null || bar.Placement == null || group.Centerline == null || bar.Centerline == null
                || group.Placement.TypeKey != bar.Placement.TypeKey
                || Math.Abs(group.Placement.ModelDiameterMm - bar.Placement.ModelDiameterMm) > 0.00001
                || group.Centerline.Count != bar.Centerline.Count) return false;
            var direction = group.Placement.Distribution;
            double norm = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y + direction.Z * direction.Z);
            if (!Finite(norm) || norm <= 0) return false;
            double shift = group.SpacingMm * offset / norm;
            bool Same(Point3 a, Point3 b) => Math.Abs(a.X + direction.X * shift - b.X) <= 0.00001
                && Math.Abs(a.Y + direction.Y * shift - b.Y) <= 0.00001
                && Math.Abs(a.Z + direction.Z * shift - b.Z) <= 0.00001;
            for (int i = 0; i < group.Centerline.Count; i++)
            {
                var a = group.Centerline[i]; var b = bar.Centerline[i];
                if (a.Midpoint.HasValue != b.Midpoint.HasValue || !Same(a.Start, b.Start) || !Same(a.End, b.End)
                    || a.Midpoint.HasValue && !Same(a.Midpoint.Value, b.Midpoint.Value)) return false;
            }
            return true;
        }

        private static PlannedBar BackgroundReference(PlannedBar measured, BackgroundLayer layer) => new PlannedBar
        {
            Start = measured.Start, End = measured.End, SpecKey = layer.Key,
            PreviewPurpose = PreviewPurpose.Background,
            Centerline = measured.Centerline?.Select(s => new CenterlineSegment
                { Start = s.Start, End = s.End, Midpoint = s.Midpoint }).ToList(),
            Placement = new BarPlacement
            {
                TypeKey = measured.Placement.TypeKey, Component = "Background",
                ModelDiameterMm = measured.Placement.ModelDiameterMm, ElevationMm = measured.Placement.ElevationMm,
                Distribution = layer.AlongX ? new Point3(0, 1, 0) : new Point3(1, 0, 0)
            }
        };

        private ReinforcementPlan CalculateZones(PreviewParameters parameters, BackgroundRequest background, ReinforcementPlan combined,
            ReinforcementPlan perimeterPlan, CancellationToken token)
        {
            var invalid = new ReinforcementPlan();
            if (parameters.UsePerimeterUForZoneEnds)
            {
                if (parameters.DemandMeaning != "Manual")
                { Problem(invalid, "ZONE_U_REQUIRES_MANUAL", "Для П-окончаний зон выберите ручное назначение. Прочность узла проверьте отдельно."); return invalid; }
            }
            if (parameters.Selections == null || parameters.Selections.Count == 0)
            { Problem(invalid, "NO_ZONE_SELECTION", "Выберите хотя бы один цвет для зон армирования."); return invalid; }
            if (!parameters.CalculationAxesConfirmed)
            { Problem(invalid, "CALCULATION_AXES", "Не определено направление X. Заново выберите плиту и ребро X."); return invalid; }
            var sourceGroup = _mesh.Groups.SingleOrDefault(g => g.Key == parameters.SourceGroupKey);
            if (sourceGroup == null)
            { Problem(invalid, "SOURCE_PLANE", "Выберите слой и высоту расчётной подложки."); return invalid; }
            if (!new[] { "Manual", "AdditionalArea", "TotalArea" }.Contains(parameters.DemandMeaning))
            { Problem(invalid, "DEMAND_MEANING", "Выберите способ назначения арматуры."); return invalid; }
            bool alongX = parameters.Direction?.EndsWith("X", StringComparison.Ordinal) == true;
            bool top = parameters.Direction?.StartsWith("Верх", StringComparison.Ordinal) == true;
            var matchingBackground = background.Layers.SingleOrDefault(l => l.Top == top && l.AlongX == alongX);
            if (parameters.DemandMeaning == "TotalArea" && matchingBackground != null)
            {
                var positiveColors = new HashSet<string>(parameters.Selections.Where(s => s.RequiredAreaMm2PerM > 0).Select(s => s.ColorKey), StringComparer.Ordinal);
                if (positiveColors.Count > 0)
                    invalid.Diagnostics.AddRange(BackgroundCoverageValidator.Validate(
                        sourceGroup.Faces.Where(f => positiveColors.Contains(f.ColorKey)), _slab.Slabs,
                        combined.Bars.Concat(combined.ReferenceBars).Where(b => b.SpecKey == matchingBackground.Key)
                            .Select(StraightBackgroundCoverageBar), matchingBackground.SpacingMm, alongX, token));
                if (invalid.Diagnostics.Any(d => d.IsBlocking)) return invalid;
            }
            var request = new PlanRequest
            {
                Slabs = _slab.Slabs,
                PerimeterPlan = parameters.UsePerimeterUForZoneEnds ? perimeterPlan : null,
                Faces = sourceGroup.Faces.Where(f => parameters.Selections.Any(s => s.ColorKey == f.ColorKey)).ToList(),
                Options = new PlanOptions
                {
                    AlongX = alongX,
                    UsePerimeterUForZoneEnds = parameters.UsePerimeterUForZoneEnds,
                    AllowBarIntersections = true,
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
                    if (!_types.TryGetValue(selection.SpecKey, out var type)) throw new ArgumentException("Тип арматуры не найден. Выберите его заново.");
                    if (!type.SteelGrade.HasValue) throw new ArgumentException("У типа «" + type.Name + "» укажите класс арматуры в свойствах типа.");
                    if (Math.Abs(type.SteelGrade.Value - 240) < 0.0001)
                        throw new ArgumentException("Зоны из гладкой A240 пока не поддерживаются. Выберите периодическую арматуру.");
                    var backgroundPlacement = matchingBackground == null ? null : combined.Bars.Concat(combined.ReferenceBars).FirstOrDefault(b => b.SpecKey == matchingBackground.Key)?.Placement;
                    // Zones use the same section layers as the background. Legacy user
                    // offsets are intentionally ignored, including values restored from XML.
                    double virtualOffset = 0;
                    if (backgroundPlacement == null && !alongX)
                    {
                        // A separately created Y zone retains the configured X/Y order.
                        // Its own radius defines the axis, even when the disabled Y background
                        // uses a different diameter. Measured matching-background Z takes precedence.
                        var outerX = background.PlacementLayers.SingleOrDefault(l => l.Top == top && l.AlongX);
                        if (outerX == null || !Finite(outerX.ModelDiameterMm) || outerX.ModelDiameterMm <= 0)
                            throw new ArgumentException("Для высоты зоны Y выберите тип фона X с той же стороны. Включать фон необязательно.");
                        virtualOffset += outerX.ModelDiameterMm;
                    }
                    double z = backgroundPlacement?.ElevationMm ?? BarElevation.Calculate(top, _slab.ThicknessMm,
                        top ? background.TopCoverMm : background.BottomCoverMm, top ? background.BottomCoverMm : background.TopCoverMm,
                        type.ModelDiameterMm, virtualOffset);
                    if (z - type.ModelDiameterMm / 2 < -_slab.ThicknessMm + background.BottomCoverMm - 1e-6
                        || z + type.ModelDiameterMm / 2 > -background.TopCoverMm + 1e-6)
                        throw new ArgumentException("Зона не помещается по высоте. Проверьте толщину плиты, диаметры и защитные слои.");
                    double? requiredAreaMm2PerM = null;
                    if (parameters.DemandMeaning != "Manual")
                    {
                        if (!selection.RequiredAreaMm2PerM.HasValue || !Finite(selection.RequiredAreaMm2PerM.Value) || selection.RequiredAreaMm2PerM.Value < 0)
                            throw new ArgumentException("Укажите площадь арматуры каждого цвета в мм²/м. Для диапазона берите максимум.");
                        double baseArea = 0;
                        if (parameters.DemandMeaning == "TotalArea")
                        {
                            if (matchingBackground == null || backgroundPlacement == null)
                                throw new ArgumentException("Сначала создайте фон выбранного слоя и направления. Затем пересчитайте зоны.");
                            var bgType = _types[matchingBackground.TypeKey];
                            if (bgType.SteelGrade != type.SteelGrade
                                || Math.Abs(bgType.ModelDiameterMm - type.ModelDiameterMm) > 1e-6)
                                throw new ArgumentException("Фон нельзя вычесть: не совпали класс стали, фактический диаметр или высота.");
                            baseArea = Area(bgType.DiameterMm, matchingBackground.SpacingMm);
                        }
                        double deficit = Math.Max(0, selection.RequiredAreaMm2PerM.Value - baseArea);
                        if (deficit <= 1e-6) continue;
                        if (Area(type.DiameterMm, selection.SpacingMm) + 1e-6 < deficit)
                            throw new ArgumentException("Арматуры недостаточно. Нужна дополнительная площадь " + deficit.ToString("0.##", CultureInfo.CurrentCulture) + " мм²/м.");
                        // The common line may choose a larger diameter with a wider
                        // assigned pitch. Preserve the source demand for that final check.
                        requiredAreaMm2PerM = deficit;
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
                        RequiredAreaMm2PerM = requiredAreaMm2PerM,
                        ModelDiameterMm = type.ModelDiameterMm,
                        ElevationMm = z,
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
                invalid.Diagnostics.Add(new PlanDiagnostic { Code = "BACKGROUND_SUFFICIENT", Message = "Площади фона достаточно. Анкеровку в опорах проверьте отдельно." });
                return invalid;
            }
            if (parameters.UsePerimeterUForZoneEnds)
            {
                foreach (string key in request.Specifications.Select(s => s.TypeKey).Distinct())
                {
                    var type = _types[key];
                    double legLength = parameters.ZoneEndLegLengthMm, requiredLap = 0;
                    if (parameters.AutomaticULegLength)
                    {
                        try
                        {
                            var length = ULength(type, parameters.ConcreteClass);
                            legLength = length.LegProjectionMm; requiredLap = length.LapMm;
                            invalid.Diagnostics.Add(ULengthDiagnostic("ZONE_U_LENGTH", "П-окончания зон", type, length));
                        }
                        catch (ArgumentException exception) { Problem(invalid, "ZONE_U_LENGTH", exception.Message); return invalid; }
                    }
                    request.AutomaticZoneEndTypes[key] = new AutomaticZoneEndOptions
                    {
                        TypeKey = type.Key, DiameterMm = type.DiameterMm, ModelDiameterMm = type.ModelDiameterMm,
                        BendDiameterMm = type.BendDiameterMm, LegLengthMm = legLength, RequiredLapMm = requiredLap,
                        ThicknessMm = _slab.ThicknessMm, TopCoverMm = background.TopCoverMm, BottomCoverMm = background.BottomCoverMm,
                        SideCoverMm = parameters.EdgeCoverMm + 0.05, Top = top
                    };
                }
                request.AutomaticZoneEnds = request.AutomaticZoneEndTypes.Values.First();
            }
            var plan = ReinforcementPlanner.Build(request, token);
            plan.Diagnostics.AddRange(invalid.Diagnostics);
            foreach (var bar in plan.Bars)
            { bar.Placement = placements[bar.SpecKey]; bar.PreviewPurpose |= PreviewPurpose.Zones; }
            foreach (var group in plan.Groups)
            { group.Placement = placements[group.SpecKey]; group.PreviewPurpose |= PreviewPurpose.Zones; }
            if (plan.AutomaticZoneEndPlan != null)
            {
                ReuseAutomaticZoneEnds(plan.AutomaticZoneEndPlan, combined, token);
                Append(plan, plan.AutomaticZoneEndPlan);
            }
            if (parameters.DemandMeaning == "Manual")
                plan.Diagnostics.Add(new PlanDiagnostic { Code = "MANUAL_PLACEMENT", Message = "Площадь арматуры не проверяется." });
            return plan;
        }

        private static double Area(double diameter, double spacing) => Math.PI * diameter * diameter / 4 * 1000 / spacing;

        private static ULegLengthResult ULength(BarTypeSnapshot type, string concreteClass)
        {
            if (!type.SteelGrade.HasValue)
                throw new ArgumentException("У типа «" + type.Name + "» укажите класс арматуры для расчёта длины П-стержня.");
            return ULegLengthCalculator.Calculate(type.DiameterMm, type.ModelDiameterMm, type.BendDiameterMm, concreteClass, type.SteelGrade.Value);
        }

        private static PlanDiagnostic ULengthDiagnostic(string code, string component, BarTypeSnapshot type, ULegLengthResult length) =>
            new PlanDiagnostic { Code = code, Message = component + " «" + type.Name + "»: нахлёст "
                + length.LapMm.ToString("0.##", CultureInfo.CurrentCulture) + " мм; полка "
                + length.LegProjectionMm.ToString("0.##", CultureInfo.CurrentCulture) + " мм." };

        private static PlannedBar StraightBackgroundCoverageBar(PlannedBar bar)
        {
            // The coverage API accepts XY straight bars. Keep measured 3D paths on
            // reference bars and flatten only a verified single horizontal line.
            var path = bar.Centerline;
            if (path?.Count != 1 || path[0].Midpoint.HasValue
                || Math.Abs(path[0].Start.Z - path[0].End.Z) > 0.000001) return bar;
            return new PlannedBar
            {
                Start = new Point2(path[0].Start.X, path[0].Start.Y),
                End = new Point2(path[0].End.X, path[0].End.Y)
            };
        }

        private void ReuseAutomaticZoneEnds(ReinforcementPlan endings, ReinforcementPlan combined, CancellationToken token)
        {
            if (endings.Bars.Count == 0) return;
            var available = _existingBars.Concat(combined.Bars).ToList();
            var matches = ExistingReinforcementMatcher.Match(endings.Bars, available, cancellationToken: token);
            if (matches.MatchedCount == 0) return;
            var matched = new HashSet<int>(matches.Matches.Select(m => m.ExpectedIndex));
            // In the compatibility All scope, main-perimeter reuse may already
            // reference the same existing physical U. Keep one reference so the
            // writer can still verify references one-to-one against the model.
            var referenced = ExistingReinforcementMatcher.Match(combined.ReferenceBars, _existingBars, cancellationToken: token)
                .Matches.ToDictionary(m => m.ActualIndex, m => combined.ReferenceBars[m.ExpectedIndex]);
            void Redirect(string from, string to)
            {
                foreach (var edge in endings.PerimeterUEdges)
                    for (int i = 0; i < edge.BarKeys.Count; i++)
                        if (edge.BarKeys[i] == from) edge.BarKeys[i] = to;
            }
            foreach (var match in matches.Matches)
            {
                var expected = endings.Bars[match.ExpectedIndex];
                if (match.ActualIndex < _existingBars.Count)
                {
                    if (referenced.TryGetValue(match.ActualIndex, out var reference))
                    {
                        reference.PreviewPurpose |= expected.PreviewPurpose;
                        Redirect(expected.SpecKey, reference.SpecKey);
                    }
                    else
                    {
                        endings.ReferenceBars.Add(expected);
                        referenced.Add(match.ActualIndex, expected);
                    }
                }
                else
                {
                    // One new perimeter bar may serve both components. Mark that
                    // physical member, not its whole background array.
                    var actual=available[match.ActualIndex];
                    actual.PreviewPurpose|=expected.PreviewPurpose;
                    Redirect(expected.SpecKey, actual.SpecKey);
                }
            }
            var indices = endings.Bars.Select((b, i) => new { b.SpecKey, Index = i }).ToDictionary(x => x.SpecKey, x => x.Index);
            var groups = new List<BarGroup>();
            foreach (var group in endings.Groups)
            {
                int first = indices[group.SpecKey];
                for (int offset = 0; offset < group.Count;)
                {
                    if (matched.Contains(first + offset)) { offset++; continue; }
                    int start = offset++;
                    while (offset < group.Count && !matched.Contains(first + offset)) offset++;
                    var bar = endings.Bars[first + start];
                    groups.Add(new BarGroup { Start = bar.Start, End = bar.End, SpecKey = bar.SpecKey, ColorKey = bar.ColorKey,
                        PreviewPurpose = group.PreviewPurpose,
                        Placement = bar.Placement, Centerline = bar.Centerline, Count = offset - start, SpacingMm = group.SpacingMm,
                        LayoutRule = group.LayoutRule, MaximumSpacingMm = group.MaximumSpacingMm,
                        ArrayLengthMm = Math.Max(0, offset - start - 1) * group.SpacingMm });
                }
            }
            endings.Groups = groups;
            endings.Bars = endings.Bars.Where((b, i) => !matched.Contains(i)).ToList();
            endings.Diagnostics.Add(new PlanDiagnostic { Code = "ZONE_ENDS_REUSED",
                Message = "Совпадающих П-окончаний зон: " + matches.MatchedCount + "." });
        }
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static void MarkPreviewPurpose(ReinforcementPlan plan,PreviewPurpose purpose)
        {
            foreach(var bar in plan.Bars.Concat(plan.ReferenceBars))bar.PreviewPurpose|=purpose;
            foreach(var group in plan.Groups)group.PreviewPurpose|=purpose;
        }
        private static void Problem(ReinforcementPlan plan, string code, string message) => plan.Diagnostics.Add(new PlanDiagnostic { Code = code, Message = message, IsBlocking = true });
        private static void Append(ReinforcementPlan target, ReinforcementPlan part)
        { target.Bars.AddRange(part.Bars); target.Groups.AddRange(part.Groups); target.Diagnostics.AddRange(part.Diagnostics); target.DemandRegions.AddRange(part.DemandRegions); target.PerimeterUEdges.AddRange(part.PerimeterUEdges); target.ReferenceBars.AddRange(part.ReferenceBars); }

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
