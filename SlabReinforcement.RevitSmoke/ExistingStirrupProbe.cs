using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SlabReinforcement.Core;
using SlabReinforcement.Integration;

namespace SlabReinforcement.RevitSmoke
{
    internal static class ExistingStirrupProbe
    {
        public static object Run(Document doc, SlabGeometrySnapshot slab, RebarBarType type)
        {
            var failures = new List<string>();
            var pairs = new[]
            {
                new[] { RebarHookOrientation.Right, RebarHookOrientation.Left },
                new[] { RebarHookOrientation.Left, RebarHookOrientation.Right },
                new[] { RebarHookOrientation.Left, RebarHookOrientation.Left },
                new[] { RebarHookOrientation.Right, RebarHookOrientation.Right }
            };
            int before = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).GetElementCount();
            foreach (bool overlap in new[] { false, true })
                foreach (var pair in pairs)
                {
                    using (var scope = new TransactionGroup(doc, "Temporary adjusted existing stirrup"))
                    {
                        scope.Start();
                        try
                        {
                            Rebar rebar;
                            var shell = slab.Slabs.SelectMany(p => p.Shell).ToList();
                            double x = (shell.Min(p => p.X) + shell.Max(p => p.X)) / 2;
                            double y = (shell.Min(p => p.Y) + shell.Max(p => p.Y)) / 2;
                            double z = -slab.ThicknessMm / 2;
                            var points = new List<Point3>
                            {
                                new Point3(x - 500, y - 350, z), new Point3(x + 500, y - 350, z),
                                new Point3(x + 500, y + 350, z), new Point3(x - 500, y + 350, z),
                                new Point3(x - 500, y - 350, z)
                            };
                            if (overlap) points.Add(new Point3(x - 300, y - 350, z));
                            Func<Point3, XYZ> world = p => SlabGeometryReader.ToXyz(slab.Frame.ToWorld(p));
                            var curves = Enumerable.Range(0, points.Count - 1).Select(i => (Curve)Line.CreateBound(world(points[i]), world(points[i + 1]))).ToList();
                            var normal = new XYZ(slab.Frame.Normal.X, slab.Frame.Normal.Y, slab.Frame.Normal.Z);
                            using (var transaction = new Transaction(doc, "Create existing stirrup fixture"))
                            {
                                transaction.Start();
                                transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(new RollBackErrors()).SetClearAfterRollback(true));
                                var hook = RebarHookType.Create(doc, Math.PI * 0.75, 10);
                                hook.Style = RebarStyle.StirrupTie;
                                type.SetHookPermission(hook.Id, true);
                                rebar = Rebar.CreateFromCurves(doc, RebarStyle.StirrupTie, type, hook, hook, slab.Host, normal, curves, pair[0], pair[1], true, true);
                                if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Stirrup transaction did not commit.");
                            }
                            var coarse = rebar.GetTransformedCenterlineCurves(false, false, false, MultiplanarOption.IncludeAllMultiplanarCurves, 0);
                            var adjusted = rebar.GetTransformedCenterlineCurves(true, false, false, MultiplanarOption.IncludeAllMultiplanarCurves, 0);
                            Func<XYZ, Point3> local = p => slab.Frame.ToLocal(SlabGeometryReader.ToPoint(p));
                            double shift = adjusted.SelectMany(c => new[] { c.GetEndPoint(0), c.Evaluate(0.5, true), c.GetEndPoint(1) }).Max(p => Math.Abs(local(p).Z - z));
                            if (shift < 0.1) { failures.Add("No physical adjustment for " + overlap + "/" + string.Join(",", pair)); continue; }
                            bool unsupported = adjusted.Any(c => !(c is Line) && !(c is Arc));
                            List<PlannedBar> existing;
                            try { existing = ExistingRebarReader.Read(slab, id => id.Equals(rebar.Id)); }
                            catch (ArgumentException error)
                            {
                                if (!unsupported) throw;
                                return new { result = "UNSUPPORTED_ADJUSTED_CURVES_BLOCKED", shiftMm = shift, message = error.Message, curveTypes = adjusted.Select(c => c.GetType().Name).Distinct().ToList(), overlap };
                            }
                            if (unsupported) throw new InvalidOperationException("Unsupported adjusted curves were accepted.");
                            var read = existing.Single();
                            var expected = ReadPath(adjusted, local);
                            if (read.Centerline.Count != expected.Count) throw new InvalidOperationException("Reader lost adjusted curve segments.");
                            for (int i = 0; i < expected.Count; i++)
                            {
                                if (Distance(read.Centerline[i].Start, expected[i].Start) > 1e-6 || Distance(read.Centerline[i].End, expected[i].End) > 1e-6 ||
                                    read.Centerline[i].Midpoint.HasValue != expected[i].Midpoint.HasValue ||
                                    expected[i].Midpoint.HasValue && Distance(read.Centerline[i].Midpoint.Value, expected[i].Midpoint.Value) > 1e-6)
                                    throw new InvalidOperationException("Reader differs from fine-detail adjusted geometry.");
                            }
                            var shiftedLine = adjusted.OfType<Line>().Where(c => c.Length * 304.8 > 40 && Math.Abs(local(c.Evaluate(0.5, true)).Z - z) > 0.1)
                                .OrderByDescending(c => Math.Abs(local(c.Evaluate(0.5, true)).Z - z)).First();
                            double diameter = type.BarModelDiameter * 304.8;
                            var offset = normal * (Math.Sign(local(shiftedLine.Evaluate(0.5, true)).Z - z) * diameter * 0.75 / 304.8);
                            var candidate = new PlannedBar
                            {
                                SpecKey = "AdjustedExistingCollisionProbe",
                                Placement = new BarPlacement { TypeKey = type.UniqueId, Component = "Zones", ModelDiameterMm = diameter },
                                Centerline = new List<CenterlineSegment> { new CenterlineSegment { Start = local(shiftedLine.Evaluate(0.4, true) + offset), End = local(shiftedLine.Evaluate(0.6, true) + offset) } }
                            };
                            var coarseBar = new PlannedBar { Placement = read.Placement, Centerline = ReadPath(coarse, local), SpecKey = "CoarseExisting" };
                            var coarseDiagnostics = CompositePlanValidator.Validate(new[] { coarseBar, candidate }, existingBarCount: 1);
                            var fineDiagnostics = CompositePlanValidator.Validate(new[] { read, candidate }, existingBarCount: 1);
                            if (coarseDiagnostics.Any(d => d.IsBlocking)) throw new InvalidOperationException("Fixture candidate must be clear of the simplified plane.");
                            if (!fineDiagnostics.Any(d => d.Code == "COMPOSITE_BAR_COLLISION" && d.IsBlocking))
                                throw new InvalidOperationException("Collision with the adjusted existing stirrup was missed.");
                            return new
                            {
                                result = "ADJUSTED_READER_MATCH_AND_COLLISION_PASS", shiftMm = shift, diameterMm = diameter,
                                coarseSegments = coarse.Count, adjustedSegments = adjusted.Count, readerSegments = read.Centerline.Count,
                                candidateDistanceToAdjustedMm = diameter * 0.75, coarseCandidateAllowed = true,
                                fineCandidateBlocked = true, overlap, hookOrientations = pair.Select(p => p.ToString()).ToList(),
                                fineDiagnostics, fixtureAttempts = failures
                            };
                        }
                        catch (Exception error) { failures.Add(overlap + "/" + string.Join(",", pair) + ": " + error); }
                        finally
                        {
                            scope.RollBack();
                            if (new FilteredElementCollector(doc).OfClass(typeof(Rebar)).GetElementCount() != before)
                                throw new InvalidOperationException("Adjusted stirrup fixture rollback changed rebar count.");
                        }
                    }
                }
            throw new InvalidOperationException("No valid adjusted-stirrup fixture: " + string.Join(" | ", failures));
        }

        private static List<CenterlineSegment> ReadPath(IEnumerable<Curve> curves, Func<XYZ, Point3> local) => curves.Select(c => new CenterlineSegment
        { Start = local(c.GetEndPoint(0)), End = local(c.GetEndPoint(1)), Midpoint = c is Arc ? (Point3?)local(c.Evaluate(0.5, true)) : null }).ToList();
        private static double Distance(Point3 a, Point3 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
        private sealed class RollBackErrors : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                var messages = accessor.GetFailureMessages();
                if (messages.Any(m => m.GetSeverity() == FailureSeverity.Error)) return FailureProcessingResult.ProceedWithRollBack;
                foreach (var message in messages) if (message.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(message);
                return FailureProcessingResult.Continue;
            }
        }
    }
}
