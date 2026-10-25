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
        private void RunProductionPathBandRegression(Document doc, SlabGeometrySnapshot slab, RebarBarType type)
        {
            foreach (string direction in new[] { "Низ X", "Низ Y", "Верх X", "Верх Y" })
            {
                string captured = direction;
                Case("production-path-bands-" + direction, () =>
                {
                    using (var rollback = new TransactionGroup(doc, "Production path bands rollback"))
                    {
                        rollback.Start();
                        var expected = CreateProductionPathBands(doc, slab, type, captured);
                        var result = VerifyExpandedPaths(doc, expected, false);
                        rollback.RollBack();
                        return result;
                    }
                });
            }
            Case("production-path-bands-rotated-frame", () =>
            {
                using (var rollback = new TransactionGroup(doc, "Rotated production bands rollback"))
                {
                    rollback.Start();
                    using (var tx = new Transaction(doc, "Rotate production band host"))
                    {
                        tx.Start();
                        ElementTransformUtils.RotateElement(doc, slab.Host.Id, Line.CreateBound(XYZ.Zero, XYZ.BasisZ), 37 * Math.PI / 180);
                        tx.Commit();
                    }
                    var rotated = SlabGeometryReader.Read(doc, slab.Host, FindEdge(slab.Host));
                    var expected = CreateProductionPathBands(doc, rotated, type, "Низ Y");
                    var result = VerifyExpandedPaths(doc, expected, false);
                    rollback.RollBack();
                    return result;
                }
            });
            Case("production-path-band-native-array-limit", () => TestProductionBandLimit(doc, slab, type, 1002));
            Case("production-path-band-expanded-multiple-native-children", () => TestProductionBandLimit(doc, slab, type, 1001));
        }

        private object TestProductionBandLimit(Document doc, SlabGeometrySnapshot original, RebarBarType type, int count)
        {
            using (var rollback = new TransactionGroup(doc, "Native array limit rollback"))
            {
                rollback.Start();
                Floor floor;
                using (var tx = new Transaction(doc, "Large array fixture"))
                {
                    tx.Start();
                    // Keep this independent fixture away from both source and normal test
                    // slabs; coincident hosts produce Revit's overlap warning dialog.
                    var corners = new[] { new XYZ(100000 / 304.8, 100000 / 304.8, 0), new XYZ(105000 / 304.8, 100000 / 304.8, 0),
                        new XYZ(105000 / 304.8, 120200 / 304.8, 0), new XYZ(100000 / 304.8, 120200 / 304.8, 0) };
                    var loop = new CurveLoop();
                    for (int i = 0; i < corners.Length; i++) loop.Append(Line.CreateBound(corners[i], corners[(i + 1) % corners.Length]));
                    floor = Floor.Create(doc, new List<CurveLoop> { loop }, original.Host.GetTypeId(), original.Host.LevelId, true, null, 0);
                    tx.Commit();
                }
                var slab = SlabGeometryReader.Read(doc, floor, FindEdge(floor));
                double minX = slab.Slabs.SelectMany(s => s.Shell).Min(p => p.X), minY = slab.Slabs.SelectMany(s => s.Shell).Min(p => p.Y);
                double diameter = type.BarModelDiameter * 304.8;
                var placement = new BarPlacement { Component = "Zones", TypeKey = type.UniqueId, ModelDiameterMm = diameter,
                    Distribution = new Point3(1, 0, 0), ElevationMm = BarElevation.Calculate(false, slab.ThicknessMm,
                    slab.BottomCoverMm, slab.TopCoverMm, diameter, diameter) };
                var bars = Enumerable.Range(0, count).Select(i => new PlannedBar { SpecKey = "limit", PreviewPurpose = PreviewPurpose.Zones,
                    Placement = placement, Start = new Point2(minX + 100 + i * 20, minY + 500), End = new Point2(minX + 100 + i * 20, minY + 2500) }).ToList();
                var group = new BarGroup { SpecKey = "limit", PreviewPurpose = PreviewPurpose.Zones, Placement = placement,
                    Start = bars[0].Start, End = bars[0].End, Count = count, SpacingMm = 20,
                    PathBandStartMm = minX + 90, PathBandEndMm = minX + 90 + count * 20 };
                var plan = new ReinforcementPlan { Bars = bars, Groups = new List<BarGroup> { group } };
                var p = ZoneParameters(slab, type, "Низ Y"); p.CreationScope = ReinforcementCreationScope.Zones;
                RebarPlanWriter.Create(doc, slab, new ReinforcementSession(slab, new ImportedMeshSnapshot(), new List<RebarBarType> { type }), plan, p);
                var path = ScopeNativeElements(doc, slab).OfType<PathReinforcement>().Single();
                var actual = ExistingRebarReader.Read(slab);
                Require(actual.Count == count && ExistingReinforcementMatcher.Match(bars, actual).IsComplete, "Large native array changed physical rows.");
                var curve = ((ModelCurve)doc.GetElement(path.GetCurveElementIds().Single())).GeometryCurve;
                double start = Local(slab, curve.GetEndPoint(0))[0], end = Local(slab, curve.GetEndPoint(1))[0];
                bool fallback = count == 1002;
                double expectedStart = fallback ? bars[0].Start.X - diameter / 2 : group.PathBandStartMm.Value;
                double expectedEnd = fallback ? bars.Last().Start.X + diameter / 2 : group.PathBandEndMm.Value;
                Require(Math.Abs(start - expectedStart) < 0.1 && Math.Abs(end - expectedEnd) < 0.1,
                    "Large native array did not retain its expected outline.");
                if (!fallback) Require(path.GetRebarInSystemIds().Count > 1, "Expanded fixture did not exercise multiple native children.");
                var result = new { physical = actual.Count, originalOutlineRetained = fallback, expandedOutlineRetained = !fallback,
                    expectedExpandedPositionsUpperBound = count + 1,
                    native = ProbePathMetadata(doc, path) };
                rollback.RollBack();
                return result;
            }
        }

        private void RunProductionPathBands(Autodesk.Revit.ApplicationServices.Application app, ref Document document,
            SlabGeometrySnapshot slab, RebarBarType type)
        {
            var doc = document;
            var expectations = new List<BoundaryProbeExpectation>();
            foreach (string direction in new[] { "Низ X", "Низ Y", "Верх X", "Верх Y" })
            {
                string captured = direction;
                Case("production-path-bands-" + direction, () =>
                {
                    var expected = CreateProductionPathBands(doc, slab, type, captured);
                    expected.Name = captured;
                    var result = VerifyExpandedPaths(doc, expected, false);
                    expectations.Add(expected);
                    if (captured == "Низ X") ExportProductionBandViews(doc, slab, expected);
                    return result;
                });
            }
            Require(expectations.Count == 4, "All four production directions must pass before persistence verification.");
            string saved = Path.Combine(_run, "production-path-bands.rvt");
            Require(!File.Exists(saved), "Production band SaveAs target already exists.");
            doc.SaveAs(saved, new SaveAsOptions { OverwriteExistingFile = false });
            doc.Close(false);
            document = null;
            document = app.OpenDocumentFile(saved);
            var reopened = document;
            _report["productionSavedCopy"] = saved;
            foreach (var expected in expectations)
            {
                var captured = expected;
                Case("production-path-bands-reopened-" + expected.Name, () => VerifyExpandedPaths(reopened, captured, true));
            }
        }

        private BoundaryProbeExpectation CreateProductionPathBands(Document doc, SlabGeometrySnapshot slab, RebarBarType type, string direction)
        {
            bool alongX = direction.EndsWith("X", StringComparison.Ordinal), top = direction.StartsWith("Верх", StringComparison.Ordinal);
            double diameter = type.BarModelDiameter * 304.8;
            double z = BarElevation.Calculate(top, slab.ThicknessMm, top ? slab.TopCoverMm : slab.BottomCoverMm,
                top ? slab.BottomCoverMm : slab.TopCoverMm, diameter, alongX ? 0 : diameter);
            var points = slab.Slabs.SelectMany(s => s.Shell).ToList();
            double minX = points.Min(p => p.X), minY = points.Min(p => p.Y);
            double cross = alongX ? minY : minX, along = (alongX ? minX : minY) + 1000;
            var expected = new BoundaryProbeExpectation { HostUniqueId = slab.Host.UniqueId, AlongX = alongX, MaximumSpacing = true };
            var parameters = ZoneParameters(slab, type, direction);
            parameters.CreationScope = ReinforcementCreationScope.Zones;
            var typeList = new List<RebarBarType> { type };
            var beforeIds = new HashSet<ElementId>(ScopeNativeElements(doc, slab).Select(e => e.Id));
            var firstPlan = new ReinforcementPlan();
            for (int group = 0; group < 2; group++)
                AddProductionBand(firstPlan, expected, type, alongX, z, along, cross + group * 1400, 7, "joined-" + group);
            RebarPlanWriter.Create(doc, slab, new ReinforcementSession(slab, new ImportedMeshSnapshot(), typeList), firstPlan, parameters);
            var firstIds = new HashSet<ElementId>(ScopeNativeElements(doc, slab).Where(e => !beforeIds.Contains(e.Id)).Select(e => e.Id));
            var measured = ExistingRebarReader.Read(slab, firstIds.Contains);
            Require(measured.Count == 14 && ExistingReinforcementMatcher.Match(firstPlan.Bars, measured).IsComplete,
                "Production first write did not preserve fourteen moved rows.");

            // A separate writer call must reread/verify moved systems as references,
            // including the minimum valid two-row zone at the adjacent boundary.
            var nextPlan = new ReinforcementPlan { ReferenceBars = firstPlan.Bars.ToList() };
            AddProductionBand(nextPlan, expected, type, alongX, z, along, cross + 2800, 2, "next-two-row");
            RebarPlanWriter.Create(doc, slab, new ReinforcementSession(slab, new ImportedMeshSnapshot(), typeList), nextPlan, parameters);
            var added = ScopeNativeElements(doc, slab).Where(e => !beforeIds.Contains(e.Id)).ToList();
            Require(added.OfType<Rebar>().Count() == 0, "Production bands created standalone rebars.");
            var paths = added.OfType<PathReinforcement>().OrderBy(p => p.Id.IntegerValue).ToList();
            Require(paths.Count == 3, "Three native zone paths were expected.");
            expected.PathUniqueIds = paths.Select(p => p.UniqueId).ToList();
            Require(ExistingReinforcementMatcher.Match(firstPlan.Bars, ExistingRebarReader.Read(slab, firstIds.Contains)).IsComplete,
                "The second production operation changed existing moved rows.");
            return expected;
        }

        private static void AddProductionBand(ReinforcementPlan plan, BoundaryProbeExpectation expected, RebarBarType type,
            bool alongX, double z, double along, double bandStart, int count, string key)
        {
            const double step = 200;
            var placement = new BarPlacement { Component = "Zones", TypeKey = type.UniqueId, ModelDiameterMm = type.BarModelDiameter * 304.8,
                ElevationMm = z, Distribution = alongX ? new Point3(0, 1, 0) : new Point3(1, 0, 0) };
            var bars = new List<PlannedBar>();
            for (int i = 0; i < count; i++)
            {
                double row = bandStart + step / 2 + i * step;
                bars.Add(new PlannedBar { SpecKey = key, PreviewPurpose = PreviewPurpose.Zones, Placement = placement,
                    Start = alongX ? new Point2(along, row) : new Point2(row, along),
                    End = alongX ? new Point2(along + 2000, row) : new Point2(row, along + 2000) });
            }
            var group = new BarGroup { SpecKey = key, PreviewPurpose = PreviewPurpose.Zones, Placement = placement,
                Start = bars[0].Start, End = bars[0].End, Count = count, SpacingMm = step,
                PathBandStartMm = bandStart, PathBandEndMm = bandStart + count * step };
            plan.Bars.AddRange(bars); plan.Groups.Add(group); expected.Bars.AddRange(bars);
            expected.Sketches.Add(Tuple.Create(
                alongX ? new Point3(along, bandStart, 0) : new Point3(bandStart, along + 2000, 0),
                alongX ? new Point3(along, bandStart + count * step, 0) : new Point3(bandStart + count * step, along + 2000, 0)));
        }

        private void ExportProductionBandViews(Document doc, SlabGeometrySnapshot slab, BoundaryProbeExpectation expected)
        {
            var paths = expected.PathUniqueIds.Take(2).Select(id => (PathReinforcement)doc.GetElement(id)).ToList();
            var points = expected.Bars.Take(14).SelectMany(b => new[] { new Point3(b.Start.X, b.Start.Y, 0), new Point3(b.End.X, b.End.Y, 0) })
                .Concat(expected.Sketches.Take(2).SelectMany(s => new[] { s.Item1, s.Item2 }))
                .Select(p => World(slab, p.X, p.Y, p.Z)).ToList();
            var viewType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .First(v => v.ViewFamily == ViewFamily.StructuralPlan || v.ViewFamily == ViewFamily.FloorPlan);
            foreach (bool showBars in new[] { false, true })
            {
                ViewPlan view;
                using (var tx = new Transaction(doc, "Disposable production band proof view"))
                {
                    tx.Start();
                    view = ViewPlan.Create(doc, viewType.Id, slab.Host.LevelId);
                    view.Name = "Native joined bands " + (showBars ? "physical rows" : "boundaries");
                    view.ViewTemplateId = ElementId.InvalidElementId;
                    foreach (var filter in view.GetFilters().ToList()) view.RemoveFilter(filter);
                    view.DetailLevel = ViewDetailLevel.Fine;
                    view.DisplayStyle = DisplayStyle.Wireframe;
                    view.Scale = 20;
                    var visible = new List<ElementId> { slab.Host.Id };
                    view.SetCategoryHidden(slab.Host.Category.Id, false);
                    var hostBounds = slab.Host.get_BoundingBox(null);
                    double levelZ = ((Level)doc.GetElement(slab.Host.LevelId)).Elevation;
                    using (var range = view.GetViewRange())
                    {
                        foreach (var plane in new[] { PlanViewPlane.TopClipPlane, PlanViewPlane.CutPlane, PlanViewPlane.BottomClipPlane, PlanViewPlane.ViewDepthPlane })
                            range.SetLevelId(plane, slab.Host.LevelId);
                        range.SetOffset(PlanViewPlane.TopClipPlane, hostBounds.Max.Z - levelZ + 1);
                        range.SetOffset(PlanViewPlane.CutPlane, hostBounds.Max.Z - levelZ + 0.5);
                        range.SetOffset(PlanViewPlane.BottomClipPlane, hostBounds.Min.Z - levelZ - 0.5);
                        range.SetOffset(PlanViewPlane.ViewDepthPlane, hostBounds.Min.Z - levelZ - 1);
                        view.SetViewRange(range);
                    }
                    foreach (var path in paths)
                    {
                        visible.Add(path.Id);
                        visible.AddRange(path.GetCurveElementIds());
                        view.SetCategoryHidden(path.Category.Id, false);
                        foreach (Category subcategory in path.Category.SubCategories) view.SetCategoryHidden(subcategory.Id, false);
                        view.SetElementOverrides(path.Id, new OverrideGraphicSettings().SetProjectionLineColor(new Color(170, 0, 210)).SetProjectionLineWeight(3));
                        foreach (var id in path.GetRebarInSystemIds())
                        {
                            if (showBars) visible.Add(id);
                            view.SetCategoryHidden(doc.GetElement(id).Category.Id, false);
                            ((RebarInSystem)doc.GetElement(id)).SetUnobscuredInView(view, true);
                            view.SetElementOverrides(id, new OverrideGraphicSettings().SetProjectionLineColor(new Color(40, 140, 170)).SetProjectionLineWeight(1));
                        }
                    }
                    view.IsolateElementsTemporary(visible);
                    view.ConvertTemporaryHideIsolateToPermanent();
                    view.SetElementOverrides(slab.Host.Id, new OverrideGraphicSettings().SetProjectionLineColor(new Color(90, 90, 90)).SetProjectionLineWeight(2)
                        .SetSurfaceForegroundPatternVisible(false).SetSurfaceBackgroundPatternVisible(false));
                    var crop = view.CropBox;
                    var cropPoints = points.Select(point => crop.Transform.Inverse.OfPoint(point)).ToList();
                    crop.Min = new XYZ(cropPoints.Min(p => p.X) - 250 / 304.8, cropPoints.Min(p => p.Y) - 250 / 304.8, crop.Min.Z);
                    crop.Max = new XYZ(cropPoints.Max(p => p.X) + 250 / 304.8, cropPoints.Max(p => p.Y) + 250 / 304.8, crop.Max.Z);
                    view.CropBox = crop;
                    view.CropBoxActive = true; view.CropBoxVisible = false;
                    tx.Commit();
                }
                var options = new ImageExportOptions { FilePath = Path.Combine(_run, showBars ? "production-bands-with-rows" : "production-bands-boundaries"),
                    ExportRange = ExportRange.SetOfViews, ZoomType = ZoomFitType.FitToPage, PixelSize = 1600,
                    ImageResolution = ImageResolution.DPI_150, HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG };
                options.SetViewsAndSheets(new List<ElementId> { view.Id });
                doc.ExportImage(options);
            }
            _report["productionBandImages"] = Directory.GetFiles(_run, "production-bands-*.png");
        }
    }
}
