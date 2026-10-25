using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SlabReinforcement.Core;
using SlabReinforcement.Integration;

namespace SlabReinforcement.ZonePathSmoke
{
    public sealed partial class ZonePathApplication
    {
        private sealed class BoundaryProbeExpectation
        {
            public string Name;
            public string HostUniqueId;
            public List<string> PathUniqueIds = new List<string>();
            public List<PlannedBar> Bars = new List<PlannedBar>();
            public List<Tuple<Point3, Point3>> Sketches = new List<Tuple<Point3, Point3>>();
            public bool AlongX;
            public bool MaximumSpacing;
        }

        private void RunPathBoundaryProbe(Autodesk.Revit.ApplicationServices.Application app, ref Document document,
            SlabGeometrySnapshot slab, RebarBarType type)
        {
            var doc = document;
            _report["probeApi"] = new
            {
                owner = typeof(RebarInSystem).FullName,
                move = typeof(RebarInSystem).GetMethod("MoveBarInSet", new[] { typeof(int), typeof(Transform) })?.ToString(),
                include = typeof(RebarInSystem).GetMethod("SetBarIncluded", new[] { typeof(bool), typeof(int) })?.ToString(),
                canEdit = typeof(RebarInSystem).GetMethod("CanEditIndividualBars")?.ToString(),
                pathParameters = Enum.GetNames(typeof(BuiltInParameter)).Where(n => n.StartsWith("PATH_REIN_", StringComparison.Ordinal)).ToArray()
            };
            foreach (double width in new[] { 200d, 1000d })
            {
                double captured = width;
                Case("boundary-default-width-" + width, () => ProbeDefaultBoundary(doc, slab, type, captured));
            }
            int fixedRule = DiscoverFixedPathRule(doc, slab, type);
            _report["fixedNumberRuleValue"] = fixedRule;
            var expectations = new List<BoundaryProbeExpectation>();
            foreach (bool maximum in new[] { false, true })
            foreach (string direction in new[] { "Низ X", "Низ Y", "Верх X", "Верх Y" })
            {
                bool capturedMaximum = maximum;
                string capturedDirection = direction;
                string name = (maximum ? "maximum-included" : "fixed-number") + "-" + direction;
                Case("boundary-" + name, () =>
                {
                    var expected = CreateExpandedPaths(doc, slab, type, capturedDirection, capturedMaximum, fixedRule);
                    expected.Name = name;
                    var result = VerifyExpandedPaths(doc, expected, false);
                    expectations.Add(expected);
                    return result;
                });
            }
            Require(expectations.Count > 0, "No expanded native path succeeded; nothing to reopen.");
            string saved = Path.Combine(_run, "expanded-boundary-probe.rvt");
            Require(!File.Exists(saved), "Probe SaveAs target already exists.");
            doc.SaveAs(saved, new SaveAsOptions { OverwriteExistingFile = false });
            doc.Close(false);
            document = null;
            document = app.OpenDocumentFile(saved);
            var reopened = document;
            _report["probeSavedCopy"] = saved;
            foreach (var expected in expectations)
            {
                var captured = expected;
                Case("boundary-reopened-" + expected.Name, () => VerifyExpandedPaths(reopened, captured, true));
            }
        }

        private object ProbeDefaultBoundary(Document doc, SlabGeometrySnapshot slab, RebarBarType type, double width)
        {
            using (var operation = new TransactionGroup(doc, "Default path distribution probe rollback"))
            {
                operation.Start();
                PathReinforcement path;
                using (var tx = new Transaction(doc, "Default Path width"))
                {
                    tx.Start();
                    double x = slab.Slabs.SelectMany(s => s.Shell).Min(p => p.X) + 1000;
                    double y = slab.Slabs.SelectMany(s => s.Shell).Min(p => p.Y) + 1000;
                    path = MakeProbePath(doc, slab, type, new Point3(x, y, 0), new Point3(x, y + width, 0), 2000, false, false);
                    path.get_Parameter(BuiltInParameter.PATH_REIN_SPACING).Set(200 / 304.8);
                    tx.Commit();
                }
                var ids = new HashSet<ElementId>(path.GetRebarInSystemIds());
                var actual = ExistingRebarReader.Read(slab, ids.Contains);
                var rows = actual.Select(b => (b.Start.Y + b.End.Y) / 2).OrderBy(y => y).ToList();
                var result = new
                {
                    width, requestedSpacing = 200, defaultLayoutValue = path.get_Parameter(BuiltInParameter.REBAR_SYSTEM_LAYOUT_RULE).AsInteger(),
                    defaultLayout = path.get_Parameter(BuiltInParameter.REBAR_SYSTEM_LAYOUT_RULE).AsValueString(),
                    native = ProbePathMetadata(doc, path), rows,
                    actualSteps = rows.Skip(1).Zip(rows, (a, b) => a - b).ToArray()
                };
                operation.RollBack();
                return result;
            }
        }

        private int DiscoverFixedPathRule(Document doc, SlabGeometrySnapshot slab, RebarBarType type)
        {
            var trials = new List<object>();
            int? fixedRule = null;
            using (var tx = new Transaction(doc, "Discover native path rule values rollback"))
            {
                tx.Start();
                double x = slab.Slabs.SelectMany(s => s.Shell).Min(p => p.X) + 1000;
                double y = slab.Slabs.SelectMany(s => s.Shell).Min(p => p.Y) + 1000;
                var path = MakeProbePath(doc, slab, type, new Point3(x, y, 0), new Point3(x, y + 1400, 0), 2000, false, false);
                doc.Regenerate();
                // Bounded discovery, not an assumed production enum mapping: accept only
                // the value whose actual native child reports RebarLayoutRule.FixedNumber.
                for (int value = 0; value <= 5; value++)
                using (var sub = new SubTransaction(doc))
                {
                    sub.Start();
                    try
                    {
                        bool set = path.get_Parameter(BuiltInParameter.REBAR_SYSTEM_LAYOUT_RULE).Set(value);
                        doc.Regenerate();
                        var number = path.get_Parameter(BuiltInParameter.PATH_REIN_NUMBER_OF_BARS);
                        var children = path.GetRebarInSystemIds().Select(id => (RebarInSystem)doc.GetElement(id)).ToList();
                        bool recognized = children.Count > 0 && children.All(c => c.LayoutRule == RebarLayoutRule.FixedNumber) && !number.IsReadOnly;
                        trials.Add(new { value, set, label = path.get_Parameter(BuiltInParameter.REBAR_SYSTEM_LAYOUT_RULE).AsValueString(),
                            numberWritable = !number.IsReadOnly, childRules = children.Select(c => c.LayoutRule.ToString()).ToArray(), recognized });
                        if (recognized) fixedRule = value;
                    }
                    catch (Exception exception) { trials.Add(new { value, error = exception.Message }); }
                    finally { sub.RollBack(); }
                }
                tx.RollBack();
            }
            _report["nativeRuleDiscovery"] = trials;
            Require(fixedRule.HasValue, "Could not discover a valid native fixed-number rule.");
            return fixedRule.Value;
        }

        private BoundaryProbeExpectation CreateExpandedPaths(Document doc, SlabGeometrySnapshot slab, RebarBarType type,
            string direction, bool maximum, int fixedRule)
        {
            const int count = 7;
            const double step = 200;
            bool alongX = direction.EndsWith("X", StringComparison.Ordinal), top = direction.StartsWith("Верх", StringComparison.Ordinal);
            double diameter = type.BarModelDiameter * 304.8;
            double z = BarElevation.Calculate(top, slab.ThicknessMm, top ? slab.TopCoverMm : slab.BottomCoverMm,
                top ? slab.BottomCoverMm : slab.TopCoverMm, diameter, alongX ? 0 : diameter);
            var points = slab.Slabs.SelectMany(s => s.Shell).ToList();
            double minX = points.Min(p => p.X), minY = points.Min(p => p.Y);
            double cross = alongX ? minY : minX;
            double along = (alongX ? minX : minY) + (maximum ? 3000 : 500);
            var expected = new BoundaryProbeExpectation { HostUniqueId = slab.Host.UniqueId, AlongX = alongX, MaximumSpacing = maximum };
            using (var tx = new Transaction(doc, "Expanded native path and original rows"))
            {
                tx.Start();
                for (int group = 0; group < 2; group++)
                {
                    double bandStart = cross + group * count * step;
                    double bandEnd = bandStart + count * step;
                    var sketchStart = alongX ? new Point3(along, bandStart, 0) : new Point3(bandStart, along + 2000, 0);
                    var sketchEnd = alongX ? new Point3(along, bandEnd, 0) : new Point3(bandEnd, along + 2000, 0);
                    var path = MakeProbePath(doc, slab, type, sketchStart, sketchEnd, 2000, top, !alongX);
                    path.get_Parameter(BuiltInParameter.REBAR_SYSTEM_LAYOUT_RULE).Set(maximum ? 3 : fixedRule);
                    if (maximum) path.get_Parameter(BuiltInParameter.PATH_REIN_SPACING).Set(step / 304.8);
                    else path.get_Parameter(BuiltInParameter.PATH_REIN_NUMBER_OF_BARS).Set(count);
                    double nativeZ = top ? -slab.TopCoverMm - diameter / 2 : -slab.ThicknessMm + slab.BottomCoverMm + diameter / 2;
                    path.AdditionalOffset = (top ? nativeZ - z : z - nativeZ) / 304.8;
                    doc.Regenerate();
                    var children = path.GetRebarInSystemIds().Select(id => (RebarInSystem)doc.GetElement(id)).ToList();
                    Require(children.Count == 1, "Straight probe expected one RebarInSystem child.");
                    var child = children[0];
                    Require(child.CanEditIndividualBars(), "Actual RebarInSystem does not support individual movement.");
                    Require(child.NumberOfBarPositions >= count, "Expanded path has fewer positions than required rows.");
                    var chosen = Enumerable.Range(0, count).Select(i => (int)Math.Round(i * (child.NumberOfBarPositions - 1d) / (count - 1))).ToList();
                    Require(chosen.Distinct().Count() == count, "Cannot select distinct native positions.");
                    for (int index = 0; index < child.NumberOfBarPositions; index++)
                        if (!chosen.Contains(index)) child.SetBarIncluded(false, index);
                    for (int row = 0; row < count; row++)
                    {
                        int index = chosen[row];
                        double station = bandStart + step / 2 + row * step;
                        var start = alongX ? new Point2(along, station) : new Point2(station, along);
                        var end = alongX ? new Point2(along + 2000, station) : new Point2(station, along + 2000);
                        var actual = child.GetTransformedCenterlineCurves(true, false, false, index);
                        Require(actual.Count == 1 && actual[0] is Line, "Probe child is not a single straight segment.");
                        var target = World(slab, (start.X + end.X) / 2, (start.Y + end.Y) / 2, z);
                        child.MoveBarInSet(index, Transform.CreateTranslation(target - actual[0].Evaluate(0.5, true)));
                        expected.Bars.Add(new PlannedBar
                        {
                            SpecKey = "probe-" + group + "-" + row, Start = start, End = end,
                            Placement = new BarPlacement { Component = "Zones", TypeKey = type.UniqueId, ModelDiameterMm = diameter,
                                ElevationMm = z, Distribution = alongX ? new Point3(0, 1, 0) : new Point3(1, 0, 0) }
                        });
                    }
                    expected.PathUniqueIds.Add(path.UniqueId);
                    expected.Sketches.Add(Tuple.Create(sketchStart, sketchEnd));
                }
                doc.Regenerate();
                Require(tx.Commit() == TransactionStatus.Committed, "Expanded native paths were rejected by Revit.");
            }
            return expected;
        }

        private object VerifyExpandedPaths(Document doc, BoundaryProbeExpectation expected, bool reopened)
        {
            var host = (Floor)doc.GetElement(expected.HostUniqueId);
            var slab = SlabGeometryReader.Read(doc, host, FindEdge(host));
            var paths = expected.PathUniqueIds.Select(id => (PathReinforcement)doc.GetElement(id)).ToList();
            var ids = new HashSet<ElementId>(paths.SelectMany(p => p.GetRebarInSystemIds()));
            var physical = ExistingRebarReader.Read(slab, ids.Contains);
            Require(physical.Count == expected.Bars.Count, "Moved/excluded native position count is wrong.");
            Require(ExistingReinforcementMatcher.Match(expected.Bars, physical).IsComplete, "Moved native rows differ from the original global grid.");
            var containment = BarContainmentValidator.Validate(physical, slab.Slabs, slab.ThicknessMm, slab.TopCoverMm, slab.BottomCoverMm, slab.SideCoverMm + 0.05);
            Require(!containment.Any(d => d.IsBlocking), "Moved rows violate host cover: " + string.Join("; ", containment.Select(d => d.Message)));
            var actualSketches = paths.Select(path => path.GetCurveElementIds().Select(id => (ModelCurve)doc.GetElement(id))
                .Select(curve => new[] { Local(slab, curve.GeometryCurve.GetEndPoint(0)), Local(slab, curve.GeometryCurve.GetEndPoint(1)) }).Single()).ToList();
            for (int i = 0; i < actualSketches.Count; i++)
            {
                var actual = actualSketches[i]; var target = expected.Sketches[i];
                Require(Distance(actual[0], target.Item1) < 0.1 && Distance(actual[1], target.Item2) < 0.1,
                    "Native sketch changed after individual bar movement.");
            }
            int axis = expected.AlongX ? 1 : 0;
            double joinGap = Math.Abs(actualSketches[0][1][axis] - actualSketches[1][0][axis]);
            double slabEdge = slab.Slabs.SelectMany(s => s.Shell).Min(p => expected.AlongX ? p.Y : p.X);
            double edgeGap = Math.Abs(actualSketches[0][0][axis] - slabEdge);
            Require(joinGap < 0.1 && edgeGap < 0.1, "Native boundaries do not meet each other and the slab edge.");
            var rows = physical.Select(b => expected.AlongX ? b.Start.Y : b.Start.X).OrderBy(x => x).ToList();
            var steps = rows.Skip(1).Zip(rows, (a, b) => a - b).ToArray();
            Require(steps.All(s => Math.Abs(s - 200) < 0.1), "Actual global step changed across the joined boundary.");
            if (expected.MaximumSpacing)
                Require(paths.All(path => Math.Abs(path.get_Parameter(BuiltInParameter.PATH_REIN_SPACING).AsDouble() * 304.8 - 200) < 0.1),
                    "Maximum-spacing native parameter no longer says 200 mm.");
            var moves = paths.SelectMany(path => path.GetRebarInSystemIds()).Select(id => (RebarInSystem)doc.GetElement(id))
                .SelectMany(child => Enumerable.Range(0, child.NumberOfBarPositions).Where(child.DoesBarExistAtPosition)
                    .Select(i => LocalVector(slab, child.GetMovedBarTransform(i).Origin)[axis])).ToList();
            Require(moves.Any(x => x > 0.1) && moves.Any(x => x < -0.1), "Probe did not exercise both positive and negative shifts.");
            return new { reopened, maximumSpacing = expected.MaximumSpacing, physical = physical.Count, joinGapMm = joinGap,
                slabEdgeGapMm = edgeGap, actualStepsMm = steps, minMoveMm = moves.Min(), maxMoveMm = moves.Max(),
                physicalGridMatched = true, hostCoverPassed = true, sketches = actualSketches,
                native = paths.Select(path => ProbePathMetadata(doc, path)).ToList() };
        }

        private static PathReinforcement MakeProbePath(Document doc, SlabGeometrySnapshot slab, RebarBarType type,
            Point3 start, Point3 end, double length, bool top, bool alongY)
        {
            var pathType = new FilteredElementCollector(doc).OfClass(typeof(PathReinforcementType)).FirstElementId();
            var shape = new FilteredElementCollector(doc).OfClass(typeof(RebarShape)).Cast<RebarShape>()
                .First(s => s.GetRebarShapeDefinition() is RebarShapeDefinitionBySegments d && d.NumberOfSegments == 1);
            var path = PathReinforcement.Create(doc, slab.Host,
                new List<Curve> { Line.CreateBound(World(slab, start.X, start.Y, start.Z), World(slab, end.X, end.Y, end.Z)) },
                false, pathType, type.Id, ElementId.InvalidElementId, ElementId.InvalidElementId);
            path.PrimaryBarShapeId = shape.Id;
            path.get_Parameter(BuiltInParameter.PATH_REIN_ALTERNATING).Set(0);
            path.get_Parameter(BuiltInParameter.PATH_REIN_FACE_SLAB).Set(top ? 0 : 1);
            path.get_Parameter(BuiltInParameter.PATH_REIN_LENGTH_1).Set(length / 304.8);
            return path;
        }

        private static object ProbePathMetadata(Document doc, PathReinforcement path)
        {
            return new
            {
                pathId = path.Id.IntegerValue,
                ruleValue = path.get_Parameter(BuiltInParameter.REBAR_SYSTEM_LAYOUT_RULE).AsInteger(),
                rule = path.get_Parameter(BuiltInParameter.REBAR_SYSTEM_LAYOUT_RULE).AsValueString(),
                spacingMm = path.get_Parameter(BuiltInParameter.PATH_REIN_SPACING).AsDouble() * 304.8,
                spacingReadOnly = path.get_Parameter(BuiltInParameter.PATH_REIN_SPACING).IsReadOnly,
                numberParameter = path.get_Parameter(BuiltInParameter.PATH_REIN_NUMBER_OF_BARS).AsInteger(),
                summary = path.get_Parameter(BuiltInParameter.PATH_REIN_SUMMARY)?.AsString(),
                summaryDisplay = path.get_Parameter(BuiltInParameter.PATH_REIN_SUMMARY)?.AsValueString(),
                children = path.GetRebarInSystemIds().Select(id => (RebarInSystem)doc.GetElement(id)).Select(child => new
                {
                    id = child.Id.IntegerValue, canEdit = child.CanEditIndividualBars(), rule = child.LayoutRule.ToString(),
                    child.NumberOfBarPositions, child.Quantity, maxSpacingMm = child.MaxSpacing * 304.8, arrayLengthMm = child.ArrayLength * 304.8,
                    includedIndices = Enumerable.Range(0, child.NumberOfBarPositions).Where(child.DoesBarExistAtPosition).ToArray(),
                    spacingParameters = child.Parameters.Cast<Parameter>().Where(p => p.Definition.Name.Contains("Интервал") || p.Definition.Name.Contains("Шаг"))
                        .Select(p => new { name = p.Definition.Name, id = p.Id.IntegerValue, value = p.AsValueString(), readOnly = p.IsReadOnly }).ToList()
                }).ToList()
            };
        }

        private static double[] LocalVector(SlabGeometrySnapshot slab, XYZ vector)
        {
            var origin = slab.Frame.ToLocal(new Point3(0, 0, 0));
            var end = slab.Frame.ToLocal(new Point3(vector.X * 304.8, vector.Y * 304.8, vector.Z * 304.8));
            return new[] { end.X - origin.X, end.Y - origin.Y, end.Z - origin.Z };
        }
        private static double Distance(double[] a, Point3 b) => Math.Sqrt((a[0] - b.X) * (a[0] - b.X) + (a[1] - b.Y) * (a[1] - b.Y) + (a[2] - b.Z) * (a[2] - b.Z));
    }
}
