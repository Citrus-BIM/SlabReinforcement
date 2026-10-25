using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace SlabReinforcement.Core
{
    /// <summary>Completes edges without lower-row framing while preserving every aligned bar.</summary>
    internal static class HybridPerimeterPlanner
    {
        internal static ReinforcementPlan Build(PerimeterRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.BackgroundPlan == null) return PerimeterPlanner.BuildIndependent(request, token);
            var background = request.BackgroundPlan;
            var bars = background.Bars.Concat(background.ReferenceBars).Where(b => b?.Placement?.Component == "Background").ToList();
            var layers = request.BackgroundLayers;
            // A malformed supplied background must not silently become an independent layout.
            if (background.Diagnostics.Any(d => d.IsBlocking) || layers == null || layers.Any(l => l == null)
                || layers.GroupBy(l => l.Key).Any(g => g.Count() > 1)
                || bars.Any(b => !layers.Any(l => l.Key == b.SpecKey)))
                return BackgroundPerimeterPlanner.Build(request, token);
            var lowerDirections = layers.Where(l => !l.Top && bars.Any(b => b.SpecKey == l.Key)).Select(l => l.AlongX).Distinct().Count();
            if (lowerDirections == 0) return PerimeterPlanner.BuildIndependent(request, token);

            var aligned = BackgroundPerimeterPlanner.Build(request, token);
            // A valid partial lower grid can miss a small selected opening entirely.
            // In that case all its sides are unframed; invalid background geometry
            // or any other detail failure must still block rather than change modes.
            if (lowerDirections == 1 && aligned.Bars.Count == 0 && aligned.Groups.Count == 0
                && aligned.PerimeterUEdges.Count == 0
                && aligned.Diagnostics.Any(d => d.IsBlocking && d.Code == "PERIMETER_BACKGROUND_REQUIRED")
                && aligned.Diagnostics.All(d => !d.IsBlocking || d.Code == "PERIMETER_BACKGROUND_REQUIRED"))
            {
                var manual = PerimeterPlanner.BuildIndependent(request, token);
                manual.Diagnostics.InsertRange(0, aligned.Diagnostics.Where(d => !d.IsBlocking));
                if (manual.CanCreate) manual.Diagnostics.Add(new PlanDiagnostic { Code = "PERIMETER_MANUAL_COMPLETION",
                    Message = "Нижнего фона нет. Максимальный шаг П-обрамления: "
                        + request.SpacingMm.ToString("0.##", CultureInfo.CurrentCulture) + " мм." });
                return manual;
            }
            // The established two-direction behavior remains exact, including oblique
            // edges with both X and Y bars. Only a missing lower direction needs completion.
            if (!aligned.CanCreate || lowerDirections == 2) return aligned;
            var excludedEdges = new HashSet<string>(aligned.PerimeterUEdges.Select(e => EdgeKey(e.Start, e.End)), StringComparer.Ordinal);
            var independent = PerimeterPlanner.BuildIndependent(request, token, excludedEdges);
            if (independent.Diagnostics.Any(d => d.IsBlocking))
                return new ReinforcementPlan { Diagnostics = aligned.Diagnostics.Concat(independent.Diagnostics).ToList() };
            if (independent.Bars.Count == 0) return aligned;
            if ((long)aligned.Bars.Count + independent.Bars.Count > CompositePlanValidator.MaximumBars)
                return new ReinforcementPlan { Diagnostics = new List<PlanDiagnostic> { new PlanDiagnostic {
                    Code = "PERIMETER_BACKGROUND_LIMIT", Message = "Общее число П-стержней превышает 200 000.", IsBlocking = true } } };

            // Both producers number their bars locally. Keep all group/edge references
            // unambiguous when the independently placed part is appended.
            string ManualKey(string key) => "Manual|" + key;
            foreach (var bar in independent.Bars) bar.SpecKey = ManualKey(bar.SpecKey);
            foreach (var group in independent.Groups) group.SpecKey = ManualKey(group.SpecKey);
            foreach (var edge in independent.PerimeterUEdges)
            {
                edge.Id = ManualKey(edge.Id);
                edge.BarKeys = edge.BarKeys.Select(ManualKey).ToList();
            }
            aligned.Bars.AddRange(independent.Bars);
            aligned.Groups.AddRange(independent.Groups);
            aligned.PerimeterUEdges.AddRange(independent.PerimeterUEdges);
            aligned.Diagnostics.AddRange(independent.Diagnostics);
            aligned.Diagnostics.Add(new PlanDiagnostic { Code = "PERIMETER_MANUAL_COMPLETION",
                Message = "На сторонах без нижнего фона максимальный шаг П-обрамления: "
                    + request.SpacingMm.ToString("0.##", CultureInfo.CurrentCulture) + " мм. Остальные П — по рядам фона." });
            aligned.Diagnostics.AddRange(CompositePlanValidator.Validate(aligned.Bars, token, allowBarIntersections: true));
            if (aligned.Diagnostics.Any(d => d.IsBlocking))
            { aligned.Bars.Clear(); aligned.Groups.Clear(); aligned.PerimeterUEdges.Clear(); }
            return aligned;
        }

        internal static string EdgeKey(Point2 a, Point2 b)
        {
            // Quantization is used for edge identity only, never for station geometry.
            string Point(Point2 p) => Math.Round(p.X, 6).ToString("R", CultureInfo.InvariantCulture) + ","
                + Math.Round(p.Y, 6).ToString("R", CultureInfo.InvariantCulture);
            string first = Point(a), last = Point(b);
            return StringComparer.Ordinal.Compare(first, last) < 0 ? first + "|" + last : last + "|" + first;
        }
    }
}
