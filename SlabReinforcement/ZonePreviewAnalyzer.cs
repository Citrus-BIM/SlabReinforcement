using System.Collections.Generic;
using System.Linq;

namespace SlabReinforcement
{
    public static class ZonePreviewAnalyzer
    {
        private class ColorAssignment
        {
            public Autodesk.Revit.DB.Structure.RebarBarType RebarType { get; set; }
            public double Spacing { get; set; }
            public bool IsSelected { get; set; }
        }

        public static ZonePreviewSummary ApplyPreview(
            IList<ColorStat> colorStats,
            IDictionary<string, List<FiniteElementFace>> facesByColor,
            ZonePreviewSettings settings)
        {
            Dictionary<string, ColorAssignment> assignments = BuildAssignments(colorStats);
            ApplyAssignments(facesByColor, assignments);

            ZonePreviewSummary summary = new ZonePreviewSummary
            {
                TotalFaces = facesByColor.Values.Sum(faces => faces.Count),
                ColorCount = colorStats.Count,
                SelectedColorCount = colorStats.Count(stat => stat.IsSelected)
            };

            HashSet<int> allLevels = new HashSet<int>();

            foreach (ColorStat stat in colorStats)
            {
                List<FiniteElementFace> faces = facesByColor.TryGetValue(stat.Color, out List<FiniteElementFace> bucket)
                    ? bucket
                    : new List<FiniteElementFace>();

                ZoneAnalysisResult analysis = ZoneGeometryEngine.AnalyzeFaces(faces, settings);
                PopulateColorStat(stat, analysis);

                summary.HorizontalFaces += analysis.HorizontalFaceCount;
                summary.SkippedFaces += analysis.SkippedNonHorizontalFaceCount;

                foreach (int level in analysis.LevelsMm)
                {
                    allLevels.Add(level);
                }
            }

            ZoneAnalysisResult selectedAnalysis = BuildSelectedAnalysis(colorStats, facesByColor, settings);
            summary.EstimatedZoneCount = selectedAnalysis?.EstimatedZoneCount ?? 0;
            summary.GroupCount = selectedAnalysis?.GroupCount ?? 0;
            summary.TrivialClusterCount = selectedAnalysis?.TrivialClusterCount ?? 0;
            summary.MissingRebarTypeFaceCount = selectedAnalysis?.MissingRebarTypeFaceCount ?? 0;
            summary.InvalidSpacingFaceCount = selectedAnalysis?.InvalidSpacingFaceCount ?? 0;
            summary.LevelsSummary = selectedAnalysis?.LevelsSummary ?? "—";

            if (summary.SkippedFaces > 0)
            {
                summary.Warnings.Add($"В импортированном DXF пропущено негоризонтальных граней: {summary.SkippedFaces}.");
            }

            if (allLevels.Count > 1)
            {
                summary.Warnings.Add($"В импортированном DXF обнаружено несколько уровней Z: {ZoneGeometryEngine.FormatLevelSummary(allLevels.OrderBy(level => level).ToList())}.");
            }

            if (summary.SelectedColorCount == 0)
            {
                summary.Warnings.Add("Не выбраны цвета для моделирования зон.");
            }

            if (selectedAnalysis != null)
            {
                if (selectedAnalysis.TrivialClusterCount > 0)
                {
                    AddUnique(summary.Warnings, $"Есть тривиальные кластеры, которые не станут зонами: {selectedAnalysis.TrivialClusterCount}.");
                }

                if (selectedAnalysis.MissingRebarTypeFaceCount > 0)
                {
                    AddUnique(summary.Warnings, $"Горизонтальных face без типа арматуры: {selectedAnalysis.MissingRebarTypeFaceCount}.");
                }

                if (selectedAnalysis.InvalidSpacingFaceCount > 0)
                {
                    AddUnique(summary.Warnings, $"Горизонтальных face с невалидным шагом: {selectedAnalysis.InvalidSpacingFaceCount}.");
                }

                foreach (string warning in selectedAnalysis.Warnings)
                {
                    AddUnique(summary.Warnings, warning);
                }
            }

            if (summary.EstimatedZoneCount == 0 && summary.SelectedColorCount > 0 && selectedAnalysis == null)
            {
                summary.Warnings.Add("Для выбранных цветов не найдено ни одной рабочей зоны. Проверьте допуски и направление.");
            }

            return summary;
        }

        private static void PopulateColorStat(ColorStat stat, ZoneAnalysisResult analysis)
        {
            stat.FaceCount = analysis.SourceFaceCount;
            stat.HorizontalFaceCount = analysis.HorizontalFaceCount;
            stat.SkippedFaceCount = analysis.SkippedNonHorizontalFaceCount;
            stat.GroupCount = analysis.GroupCount;
            stat.LevelsSummary = analysis.LevelsSummary;
            stat.EstimatedZoneCount = analysis.EstimatedZoneCount;
            stat.TrivialClusterCount = analysis.TrivialClusterCount;
            stat.MissingRebarTypeFaceCount = analysis.MissingRebarTypeFaceCount;
            stat.InvalidSpacingFaceCount = analysis.InvalidSpacingFaceCount;
            stat.AngleSummary = analysis.AngleSummary;
            stat.DiagnosticsSummary = $"Групп {analysis.GroupCount}, кластеров {analysis.ClusterCount}, тривиальных {analysis.TrivialClusterCount}";

            List<string> warnings = new List<string>(analysis.Warnings);
            if (analysis.EstimatedZoneCount > 6)
            {
                warnings.Add($"много зон: {analysis.EstimatedZoneCount}");
            }

            if (analysis.TrivialClusterCount > 0)
            {
                warnings.Add($"тривиальных кластеров: {analysis.TrivialClusterCount}");
            }

            if (analysis.MissingRebarTypeFaceCount > 0)
            {
                warnings.Add($"без типа: {analysis.MissingRebarTypeFaceCount}");
            }

            if (analysis.InvalidSpacingFaceCount > 0)
            {
                warnings.Add($"невалидный шаг: {analysis.InvalidSpacingFaceCount}");
            }

            stat.WarningSummary = warnings.Any()
                ? string.Join(", ", warnings)
                : "без замечаний";
            stat.HasWarnings = warnings.Any();
        }

        private static Dictionary<string, ColorAssignment> BuildAssignments(IList<ColorStat> colorStats)
        {
            return colorStats.ToDictionary(
                stat => stat.Color,
                stat => new ColorAssignment
                {
                    RebarType = stat.SelectedRebarType,
                    Spacing = stat.Spacing,
                    IsSelected = stat.IsSelected
                });
        }

        private static void ApplyAssignments(
            IDictionary<string, List<FiniteElementFace>> facesByColor,
            IReadOnlyDictionary<string, ColorAssignment> assignments)
        {
            foreach (KeyValuePair<string, List<FiniteElementFace>> entry in facesByColor)
            {
                string colorKey = entry.Key;
                List<FiniteElementFace> faces = entry.Value;

                if (!assignments.TryGetValue(colorKey, out ColorAssignment assignment))
                {
                    continue;
                }

                foreach (FiniteElementFace face in faces)
                {
                    face.RebarType = assignment.RebarType;
                    face.Spacing = assignment.Spacing;
                }
            }
        }

        private static ZoneAnalysisResult BuildSelectedAnalysis(
            IEnumerable<ColorStat> colorStats,
            IDictionary<string, List<FiniteElementFace>> facesByColor,
            ZonePreviewSettings settings)
        {
            List<FiniteElementFace> selectedFaces = colorStats
                .Where(stat => stat.IsSelected)
                .SelectMany(stat => facesByColor.TryGetValue(stat.Color, out List<FiniteElementFace> faces)
                    ? faces
                    : Enumerable.Empty<FiniteElementFace>())
                .ToList();

            return selectedFaces.Any()
                ? ZoneGeometryEngine.AnalyzeFaces(selectedFaces, settings)
                : null;
        }

        private static void AddUnique(ICollection<string> warnings, string warning)
        {
            if (!warnings.Contains(warning))
            {
                warnings.Add(warning);
            }
        }
    }
}
