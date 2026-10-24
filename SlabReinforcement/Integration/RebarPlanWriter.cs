using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SlabReinforcement.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SlabReinforcement.Integration
{
    internal static class RebarPlanWriter
    {
        public static int Create(Document doc, SlabGeometrySnapshot slab, ReinforcementSession session,
            ReinforcementPlan plan, PreviewParameters parameters)
        {
            if (plan == null || !plan.CanCreate || plan.Groups.Count == 0) throw new ArgumentException("Нет проверенного плана армирования.");
            var selections = parameters.Selections.ToDictionary(ReinforcementSession.SpecKey);
            bool alongX = parameters.Direction?.EndsWith("X", StringComparison.Ordinal) == true;
            bool top = parameters.Direction?.StartsWith("Верх", StringComparison.Ordinal) == true;
            var failures = new AtomicFailures();
            var created = new List<CreatedSet>();
            using (var operation = new TransactionGroup(doc, "Проверенный план армирования"))
            {
                operation.Start();
                using (var transaction = new Transaction(doc, "Армирование плиты по расчётной подложке"))
                {
                    transaction.Start();
                    transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                    foreach (var group in plan.Groups)
                    {
                        var typeKey = group.Placement?.TypeKey ?? selections[group.SpecKey].SpecKey;
                        var type = session.RevitTypes[typeKey];
#if R2019 || R2020 || R2021
                        double diameter = type.BarDiameter * 304.8;
#else
                        double diameter = type.BarModelDiameter * 304.8;
#endif
                        double z = group.Placement?.ElevationMm ?? BarElevation.Calculate(top, slab.ThicknessMm, top ? slab.TopCoverMm : slab.BottomCoverMm,
                            top ? slab.BottomCoverMm : slab.TopCoverMm, diameter, parameters.LayerOffsetMm);
                        var localNormal = group.Placement?.Distribution ?? (alongX ? new Point3(0, 1, 0) : new Point3(1, 0, 0));
                        var origin = slab.Frame.ToWorld(new Point3(0, 0, 0));
                        var direction = slab.Frame.ToWorld(localNormal);
                        var normal = new XYZ(direction.X-origin.X,direction.Y-origin.Y,direction.Z-origin.Z).Normalize();
                        var path = group.Centerline ?? new List<CenterlineSegment> { new CenterlineSegment
                        { Start = new Point3(group.Start.X, group.Start.Y, z), End = new Point3(group.End.X, group.End.Y, z) } };
                        Func<Point3, XYZ> world = point => SlabGeometryReader.ToXyz(slab.Frame.ToWorld(point));
                        var expected = path.Select(segment => segment.Midpoint.HasValue
                            ? (Curve)Arc.Create(world(segment.Start), world(segment.End), world(segment.Midpoint.Value))
                            : Line.CreateBound(world(segment.Start), world(segment.End))).ToList();
                        var curves = UnfilletedCurves(expected);
                        if (curves.Any(c=>c.Length<=doc.Application.ShortCurveTolerance))
                            throw new ArgumentException("Сегмент стержня меньше допустимого в Revit. Создание отменено целиком.");
#if R2027
                        using var terminations = new BarTerminationsData(doc);
                        var rebar = Rebar.CreateFromCurves(doc, RebarStyle.Standard, type, slab.Host, normal, curves, terminations, true, true);
#else
#pragma warning disable CS0618
                        var rebar = Rebar.CreateFromCurves(doc, RebarStyle.Standard, type, null, null, slab.Host, normal, curves,
                            RebarHookOrientation.Left, RebarHookOrientation.Right, true, true);
#pragma warning restore CS0618
#endif
                        if (rebar == null) throw new InvalidOperationException("Revit не смог создать стержень по проверенной геометрии.");
                        using (var accessor = rebar.GetShapeDrivenAccessor())
                        {
                            if (group.Count == 1) accessor.SetLayoutAsSingle();
                            else accessor.SetLayoutAsNumberWithSpacing(group.Count, group.SpacingMm / 304.8, true, true, true);
                        }
                        created.Add(new CreatedSet { Rebar = rebar, Expected = expected, Count = group.Count, Step = normal.Multiply(group.SpacingMm / 304.8) });
                    }
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit отклонил создание. Изменения отменены. " + failures.Message);
                }
                // Shape rules and host constraints can move bars during regeneration/commit.
                // Read the resulting geometry before accepting the encompassing transaction group.
                foreach (var set in created) VerifyCreatedSet(set);
                var createdIds = new HashSet<ElementId>(created.Select(set => set.Rebar.Id));
                var actualExisting = ExistingRebarReader.Read(slab, id => !createdIds.Contains(id));
                var actualNew = ExistingRebarReader.Read(slab, createdIds.Contains);
                if (actualNew.Count != plan.Bars.Count)
                    throw new InvalidOperationException("Фактическое количество стержней в Revit отличается от предпросмотра. Создание отменено.");
                var actualDiagnostics = CompositePlanValidator.Validate(actualExisting.Concat(actualNew), default, actualExisting.Count);
                actualDiagnostics.AddRange(BarContainmentValidator.Validate(actualNew, slab.Slabs, slab.ThicknessMm,
                    slab.TopCoverMm, slab.BottomCoverMm, parameters.EdgeCoverMm + 0.05));
                if (actualDiagnostics.Any(d => d.IsBlocking))
                    throw new InvalidOperationException("После создания Revit изменил геометрию с нарушением зазора или защитного слоя. Создание отменено. "
                        + string.Join(" ", actualDiagnostics.Where(d => d.IsBlocking).Select(d => d.Message)));
                if (operation.Assimilate() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Не удалось сохранить проверенный план армирования.");
            }
            return plan.Groups.Count;
        }

        private static void VerifyCreatedSet(CreatedSet set)
        {
            const double toleranceFeet = 0.1 / 304.8;
            if (set.Rebar.NumberOfBarPositions != set.Count)
                throw new InvalidOperationException("Количество стержней в Revit отличается от предпросмотра. Создание отменено.");
            var curves = set.Rebar.GetCenterlineCurves(true, true, false, MultiplanarOption.IncludeAllMultiplanarCurves, 0);
            if (curves.Count != set.Expected.Count)
                throw new InvalidOperationException("Форма стержня в Revit отличается от предпросмотра: участков " + curves.Count + ", ожидалось " + set.Expected.Count + ". Создание отменено.");
            using (var accessor = set.Rebar.GetShapeDrivenAccessor())
            {
                for (int index = 0; index < set.Count; index++)
                {
                    var placement = accessor.GetBarPositionTransform(index);
                    var unmatched = new List<Curve>(set.Expected);
                    foreach (var actual in curves)
                    {
                        var actualStart = placement.OfPoint(actual.GetEndPoint(0));
                        var actualEnd = placement.OfPoint(actual.GetEndPoint(1));
                        var actualMid = placement.OfPoint(actual.Evaluate(0.5, true));
                        var shift = set.Step.Multiply(index);
                        var match = unmatched.FirstOrDefault(expected =>
                        {
                            if ((actual is Arc) != (expected is Arc)) return false;
                            var start = expected.GetEndPoint(0).Add(shift); var end = expected.GetEndPoint(1).Add(shift);
                            bool endpoints = actualStart.DistanceTo(start) <= toleranceFeet && actualEnd.DistanceTo(end) <= toleranceFeet
                                || actualStart.DistanceTo(end) <= toleranceFeet && actualEnd.DistanceTo(start) <= toleranceFeet;
                            return endpoints && actualMid.DistanceTo(expected.Evaluate(0.5, true).Add(shift)) <= toleranceFeet;
                        });
                        if (match == null)
                            throw new InvalidOperationException("Ограничения Revit изменили геометрию стержней относительно предпросмотра. Создание отменено целиком.");
                        unmatched.Remove(match);
                    }
                }
            }
        }

        private sealed class CreatedSet
        {
            public Rebar Rebar;
            public List<Curve> Expected;
            public XYZ Step;
            public int Count;
        }

        // Revit's API requires unfilleted curves and applies the bar type's actual bend radius.
        // Retain the rounded centreline separately and compare it after regeneration.
        private static List<Curve> UnfilletedCurves(List<Curve> expected)
        {
            bool closed = expected[0].GetEndPoint(0).DistanceTo(expected[expected.Count-1].GetEndPoint(1)) < 1e-8;
            var result = new List<Curve>();
            for (int i=0;i<expected.Count;i++)
            {
                if (!(expected[i] is Line line)) continue;
                XYZ start=line.GetEndPoint(0),end=line.GetEndPoint(1);
                int previous=i==0?(closed?expected.Count-1:-1):i-1;
                int next=i==expected.Count-1?(closed?0:-1):i+1;
                if(previous>=0 && expected[previous] is Arc before) start=TangentIntersection(before);
                if(next>=0 && expected[next] is Arc after) end=TangentIntersection(after);
                result.Add(Line.CreateBound(start,end));
            }
            if(result.Count==0) throw new ArgumentException("Форма не содержит прямых участков для создания в Revit.");
            return result;
        }
        private static XYZ TangentIntersection(Arc arc)
        {
            var halfChord=arc.GetEndPoint(0).Add(arc.GetEndPoint(1)).Multiply(0.5).Subtract(arc.Center);
            double lengthSquared=halfChord.DotProduct(halfChord);
            if(lengthSquared<1e-16) throw new ArgumentException("Загиб 180° требует отдельного описания формы.");
            return arc.Center.Add(halfChord.Multiply(arc.Radius*arc.Radius/lengthSquared));
        }

        private sealed class AtomicFailures : IFailuresPreprocessor
        {
            public string Message { get; private set; }
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                var errors = accessor.GetFailureMessages().Where(f => f.GetSeverity() == FailureSeverity.Error).ToList();
                if (errors.Count == 0) return FailureProcessingResult.Continue;
                Message = string.Join("\n", errors.Select(f => f.GetDescriptionText()));
                return FailureProcessingResult.ProceedWithRollBack;
            }
        }
    }
}
