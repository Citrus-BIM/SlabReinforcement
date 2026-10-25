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
            if (plan == null || !plan.CanCreate || plan.Groups.Count == 0) throw new ArgumentException("Исправьте ошибки и пересчитайте армирование.");
            CreationScopeGuard.Validate(plan, parameters.CreationScope);
            if (plan.Bars.Any(b => b.EndConnections.Count > 0)
                && (!parameters.UsePerimeterUForZoneEnds || parameters.DemandMeaning != "Manual"
                    || plan.PerimeterUEdges.Count == 0
                    || !plan.Bars.Concat(plan.ReferenceBars).Any(b => b.Placement?.Component == "PerimeterU")))
                throw new ArgumentException("Не подтверждены П-окончания зон. Проверьте ручное назначение и пересчитайте.");
            var covers = SlabCoverSettings.Resolve(slab, parameters);
            // Re-read in API context: the preview snapshot cannot authorize moving bars
            // that appeared while the dialog was open or changing a subsequently edited host.
            var beforeExisting = ExistingRebarReader.Read(slab);
            SlabCoverSettings.VerifyBeforeWrite(slab, covers);
            VerifyReferences(plan, beforeExisting, slab, covers);
            var selections = plan.Groups.Any(group => group.Placement?.TypeKey == null)
                ? parameters.Selections.ToDictionary(ReinforcementSession.SpecKey)
                : new Dictionary<string, PreviewSelection>();
            bool alongX = parameters.Direction?.EndsWith("X", StringComparison.Ordinal) == true;
            bool top = parameters.Direction?.StartsWith("Верх", StringComparison.Ordinal) == true;
            var failures = new AtomicFailures();
            var created = new List<CreatedSet>();
            var createdPaths = new List<PathReinforcement>();
            ZonePathWriter zoneWriter = null;
            using (var operation = new TransactionGroup(doc, "Создание армирования плиты"))
            {
                operation.Start();
                using (var transaction = new Transaction(doc, "Создание стержней плиты"))
                {
                    transaction.Start();
                    transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                    if (SlabCoverSettings.Apply(slab.Host, covers)) doc.Regenerate();
                    foreach (var group in plan.Groups)
                    {
                        var typeKey = group.Placement?.TypeKey ?? selections[group.SpecKey].SpecKey;
                        var type = session.RevitTypes[typeKey];
                        if (group.Placement?.Component == "Zones")
                        {
                            if (zoneWriter == null) zoneWriter = new ZonePathWriter(doc, slab, covers);
                            var zonePath = zoneWriter.Create(group, type, top);
                            ReinforcementDrawingParameters.Apply(zonePath, DrawingPartition.ForGroup(group, parameters.Direction), slab.Host);
                            createdPaths.Add(zonePath);
                            continue;
                        }
#if R2019 || R2020 || R2021
                        double diameter = type.BarDiameter * 304.8;
#else
                        double diameter = type.BarModelDiameter * 304.8;
#endif
                        double z = group.Placement?.ElevationMm ?? BarElevation.Calculate(top, slab.ThicknessMm, top ? covers.TopMm : covers.BottomMm,
                            top ? covers.BottomMm : covers.TopMm, diameter, 0);
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
                            throw new ArgumentException("Армирование не создано: слишком короткий участок стержня. Проверьте тип арматуры и загибы.");
#if R2027
                        using var terminations = new BarTerminationsData(doc);
                        var rebar = Rebar.CreateFromCurves(doc, RebarStyle.Standard, type, slab.Host, normal, curves, terminations, true, true);
#else
#pragma warning disable CS0618
                        var rebar = Rebar.CreateFromCurves(doc, RebarStyle.Standard, type, null, null, slab.Host, normal, curves,
                            RebarHookOrientation.Left, RebarHookOrientation.Right, true, true);
#pragma warning restore CS0618
#endif
                        if (rebar == null) throw new InvalidOperationException("Армирование не создано: Revit не принял форму стержня.");
                        using (var accessor = rebar.GetShapeDrivenAccessor())
                        {
                            if (group.Count == 1) accessor.SetLayoutAsSingle();
                            else if (group.LayoutRule == BarLayoutRule.MaximumSpacing)
                                accessor.SetLayoutAsMaximumSpacing(group.MaximumSpacingMm / 304.8, group.ArrayLengthMm / 304.8, true, true, true);
                            else accessor.SetLayoutAsNumberWithSpacing(group.Count, group.SpacingMm / 304.8, true, true, true);
                        }
                        created.Add(new CreatedSet { Rebar = rebar, Expected = expected, Count = group.Count, Step = normal.Multiply(group.SpacingMm / 304.8), SpecKey = group.SpecKey, TypeName = type.Name, ModelDiameterMm = diameter,
                            DrawingPartition = DrawingPartition.ForGroup(group, parameters.Direction) });
                    }
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Армирование не создано. Ошибка Revit: " + failures.Message);
                }
                // Shape rules and host constraints can move bars during regeneration/commit.
                // Read the resulting geometry before accepting the encompassing transaction group.
                using (var constraints = new Transaction(doc, "Сохранение положения стержней"))
                {
                    constraints.Start();
                    constraints.SetFailureHandlingOptions(constraints.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                    foreach (var set in created)
                    {
                        PreserveOpenEndpoints(set);
                        // Rebar numbering/partition is available after the creation commit.
                        ReinforcementDrawingParameters.Apply(set.Rebar, set.DrawingPartition, slab.Host);
                    }
                    zoneWriter?.PreserveRows();
                    // Native children inherit their partition from the parent system.
                    // Optional instance metadata is written only after those children exist.
                    foreach (var zonePath in createdPaths)
                        foreach (var childId in zonePath.GetRebarInSystemIds())
                            ReinforcementDrawingParameters.ApplyHostMetadata(doc.GetElement(childId), slab.Host);
                    if (constraints.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Армирование не создано: Revit изменил положение концов. " + failures.Message);
                }
                foreach (var set in created) VerifyCreatedSet(set);
                var createdIds = new HashSet<ElementId>(created.Select(set => set.Rebar.Id));
                createdIds.UnionWith(ZonePathWriter.PhysicalIds(createdPaths));
                var actualExisting = ExistingRebarReader.Read(slab, id => !createdIds.Contains(id));
                var actualNew = ExistingRebarReader.Read(slab, createdIds.Contains);
                VerifyReferences(plan, actualExisting, slab, covers);
                if (!SlabCoverSettings.Read(slab.Host).Matches(covers))
                    throw new InvalidOperationException("Армирование не создано: Revit изменил защитные слои.");
                if (actualNew.Count != plan.Bars.Count)
                    throw new InvalidOperationException("Армирование не создано: число стержней не совпало с предпросмотром.");
                if (createdPaths.Count > 0 && !ExistingReinforcementMatcher.Match(plan.Bars, actualNew).IsComplete)
                    throw new InvalidOperationException("Армирование не создано: зоны не совпали с предпросмотром.");
                var actualDiagnostics = CompositePlanValidator.Validate(actualExisting.Concat(actualNew), default, actualExisting.Count, allowBarIntersections: true);
                actualDiagnostics.AddRange(BarContainmentValidator.Validate(actualNew, slab.Slabs, slab.ThicknessMm,
                    covers.TopMm, covers.BottomMm, covers.SideMm + 0.05));
                if (actualDiagnostics.Any(d => d.IsBlocking))
                    throw new InvalidOperationException("Армирование не создано: ошибка формы или защитного слоя. "
                        + string.Join(" ", actualDiagnostics.Where(d => d.IsBlocking).Select(d => d.Message)));
                if (operation.Assimilate() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Армирование не создано: Revit не сохранил изменения.");
            }
            return plan.Groups.Count;
        }

        private static void VerifyReferences(ReinforcementPlan plan, List<PlannedBar> actualExisting,
            SlabGeometrySnapshot slab, SlabCoverValues covers)
        {
            if (plan.ReferenceBars.Count == 0) return;
            var match = ExistingReinforcementMatcher.Match(plan.ReferenceBars, actualExisting);
            if (!match.IsComplete)
                throw new ArgumentException("Армирование не создано: модель изменилась. Пересчитайте армирование.");
            if (BarContainmentValidator.Validate(match.Matches.Select(m => actualExisting[m.ActualIndex]), slab.Slabs,
                slab.ThicknessMm, covers.TopMm, covers.BottomMm, covers.SideMm + 0.05).Any(d => d.IsBlocking))
                throw new ArgumentException("Армирование не создано: существующие стержни нарушают защитный слой. Исправьте их и пересчитайте.");
        }

        private static void VerifyCreatedSet(CreatedSet set)
        {
            const double toleranceFeet = 0.1 / 304.8;
            if (set.Rebar.NumberOfBarPositions != set.Count)
                throw new InvalidOperationException("Армирование не создано: число стержней не совпало с предпросмотром.");
            var curves = set.Rebar.GetCenterlineCurves(true, true, false, MultiplanarOption.IncludeAllMultiplanarCurves, 0);
            if (curves.Count != set.Expected.Count)
                throw new InvalidOperationException("Армирование не создано: участков стержня " + curves.Count + ", ожидалось " + set.Expected.Count + ".");
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
                        {
                            var error = new InvalidOperationException("Армирование не создано: привязки Revit изменили стержни.");
                            error.Data["RebarId"] = set.Rebar.Id.ToString();
                            error.Data["SpecKey"] = set.SpecKey;
                            error.Data["TypeName"] = set.TypeName;
                            error.Data["ModelDiameterMm"] = set.ModelDiameterMm;
                            error.Data["BarIndex"] = index;
                            error.Data["SetCount"] = set.Count;
                            error.Data["ActualStartMm"] = PointDiagnostic(actualStart);
                            error.Data["ActualEndMm"] = PointDiagnostic(actualEnd);
                            error.Data["ActualMidMm"] = PointDiagnostic(actualMid);
                            error.Data["ExpectedCurvesMm"] = string.Join(" | ", unmatched.Select(c => c.GetType().Name + ": " + PointDiagnostic(c.GetEndPoint(0).Add(shift)) + " -> " + PointDiagnostic(c.GetEndPoint(1).Add(shift)) + " mid " + PointDiagnostic(c.Evaluate(0.5, true).Add(shift))));
#if R2027
                            error.Data["ConstrainedPlacementEnabled"] = "N/A: removed from Revit 2027";
#else
                            error.Data["ConstrainedPlacementEnabled"] = RebarConstraintsManager.IsRebarConstrainedPlacementEnabled;
#endif
                            error.Data["Constraints"] = ConstraintDiagnostic(set.Rebar);
                            throw error;
                        }
                        unmatched.Remove(match);
                    }
                }
            }
        }

        // Revit can snap a straight terminal segment to the infinite cover plane of a neighbouring
        // oblique face. Retain that legitimate host constraint, but explicitly offset it
        // to the already checked endpoint. Never relax the independent geometry checks.
        private static void PreserveOpenEndpoints(CreatedSet set)
        {
            if (set.Expected.Count == 0 || !(set.Expected[0] is Line) || !(set.Expected.Last() is Line)) return;
            var expectedPoints = new[] { set.Expected[0].GetEndPoint(0), set.Expected.Last().GetEndPoint(1) };
            if (expectedPoints[0].DistanceTo(expectedPoints[1]) <= 0.001 / 304.8) return;
            var actualCurves = set.Rebar.GetCenterlineCurves(true, true, false, MultiplanarOption.IncludeAllMultiplanarCurves, 0);
            if (actualCurves.Count != set.Expected.Count || !(actualCurves[0] is Line first) || !(actualCurves.Last() is Line last)) return;
            var actualPoints = new[] { first.GetEndPoint(0), last.GetEndPoint(1) };
            var actualDirections = new[] { first.Direction, last.Direction };
            if (actualPoints[0].DistanceTo(expectedPoints[1]) + actualPoints[1].DistanceTo(expectedPoints[0])
                < actualPoints[0].DistanceTo(expectedPoints[0]) + actualPoints[1].DistanceTo(expectedPoints[1]))
                Array.Reverse(expectedPoints);
            using (var manager = set.Rebar.GetRebarConstraintsManager())
            foreach (var handle in manager.GetAllHandles())
            {
                int endpoint = handle.GetHandleType() == RebarHandleType.StartOfBar ? 0 : handle.GetHandleType() == RebarHandleType.EndOfBar ? 1 : -1;
                if (endpoint < 0) continue;
                var delta = expectedPoints[endpoint].Subtract(actualPoints[endpoint]);
                if (delta.GetLength() <= 0.001 / 304.8 || delta.CrossProduct(actualDirections[endpoint]).GetLength() > 0.001 / 304.8) continue;
                var current = manager.GetCurrentConstraintOnHandle(handle);
                if (current == null || !current.IsToHostFaceOrCover()) continue;
                var reference = current.GetTargetHostFaceReference();
                var doc = set.Rebar.Document;
                var face = doc.GetElement(reference.ElementId).GetGeometryObjectFromReference(reference) as PlanarFace;
                if (face == null) continue;
                string stableReference = reference.ConvertToStableRepresentation(doc);
#if R2026 || R2027
                var candidates = manager.GetConstraintCandidatesForHandle(handle, reference);
#else
                var candidates = manager.GetConstraintCandidatesForHandle(handle);
#endif
                var candidate = candidates.FirstOrDefault(c =>
                    c.IsToHostFaceOrCover() && c.GetConstraintType() == current.GetConstraintType()
                    && c.GetTargetHostFaceReference().ConvertToStableRepresentation(doc) == stableReference);
                if (candidate == null) continue;
                double correction = delta.DotProduct(face.FaceNormal);
                if (candidate.IsToCover()) candidate.SetDistanceToTargetCover(current.GetDistanceToTargetCover() + correction);
                else candidate.SetDistanceToTargetHostFace(current.GetDistanceToTargetHostFace() + correction);
#if R2026 || R2027
                manager.SetPreferredConstraint(candidate);
#else
                manager.SetPreferredConstraintForHandle(handle, candidate);
#endif
            }
        }

        private sealed class CreatedSet
        {
            public Rebar Rebar;
            public List<Curve> Expected;
            public XYZ Step;
            public int Count;
            public string SpecKey, TypeName, DrawingPartition;
            public double ModelDiameterMm;
        }
        private static string PointDiagnostic(XYZ point) => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "({0:R},{1:R},{2:R})", point.X * 304.8, point.Y * 304.8, point.Z * 304.8);

        private static string ConstraintDiagnostic(Rebar rebar)
        {
            var entries = new List<string>();
            using (var manager = rebar.GetRebarConstraintsManager())
            foreach (var handle in manager.GetAllHandles())
            {
                try
                {
                    var constraint = manager.GetCurrentConstraintOnHandle(handle);
                    string entry = handle.GetHandleType() + " " + handle.GetHandleName();
                    if (constraint == null) { entries.Add(entry + " none"); continue; }
                    entry += " " + constraint.GetConstraintType();
                    if (constraint.IsToHostFaceOrCover())
                    {
                        entry += " distanceMm=" + (constraint.IsToCover() ? constraint.GetDistanceToTargetCover() : constraint.GetDistanceToTargetHostFace()) * 304.8;
                        var reference = constraint.GetTargetHostFaceReference();
                        var face = rebar.Document.GetElement(reference.ElementId).GetGeometryObjectFromReference(reference) as PlanarFace;
                        if (face != null) entry += " faceOrigin=" + PointDiagnostic(face.Origin) + " normal=" + face.FaceNormal;
                    }
                    entries.Add(entry);
                }
                catch (Exception e) { entries.Add("diagnostic-error: " + e.Message); }
            }
            return string.Join(" | ", entries);
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
            if(result.Count==0) throw new ArgumentException("Форма без прямых участков пока не поддерживается.");
            return result;
        }
        private static XYZ TangentIntersection(Arc arc)
        {
            var halfChord=arc.GetEndPoint(0).Add(arc.GetEndPoint(1)).Multiply(0.5).Subtract(arc.Center);
            double lengthSquared=halfChord.DotProduct(halfChord);
            if(lengthSquared<1e-16) throw new ArgumentException("Загиб на 180° в этой форме пока не поддерживается.");
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
