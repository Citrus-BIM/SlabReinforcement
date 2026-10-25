using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SlabReinforcement.Core;

namespace SlabReinforcement.UnderlayUiSmoke
{
    public sealed partial class UnderlayApplication
    {
        private const double CurrentToleranceMm = 0.1;

        private void RunCurrentPluginPlan(Autodesk.Revit.ApplicationServices.Application app, ref Document doc)
        {
            _report["scope"] = "API integration test of the exact production DLL: SlabGeometryReader -> ReinforcementSession.Calculate -> RebarPlanWriter.Create. No UI picks and no native Area/Path experiment.";
            string folder = Path.GetDirectoryName(GetType().Assembly.Location);
            string productPath = Path.Combine(folder, "SlabReinforcement.dll");
            string expected = File.ReadAllText(Path.Combine(_run, "expected-product-sha256.txt")).Trim();
            Require(string.Equals(Hash(productPath), expected, StringComparison.OrdinalIgnoreCase), "Production DLL SHA differs from approved build.");
            Require(Hash(typeof(ReinforcementPlan).Assembly.Location) == Hash(Path.Combine(folder, "SlabReinforcement.Core.dll")), "Loaded Core differs from the staged production Core.");
            var product = Assembly.LoadFrom(productPath);
            Require(string.Equals(Hash(product.Location), expected, StringComparison.OrdinalIgnoreCase), "Revit resolved another already-loaded production DLL.");
            _report["production"] = new { path = product.Location, sha256 = Hash(productPath), module = product.ManifestModule.ModuleVersionId,
                corePath = typeof(ReinforcementPlan).Assembly.Location, coreSha256 = Hash(typeof(ReinforcementPlan).Assembly.Location) };
            Check("production-assembly-identity", _report["production"]);

            RunCurrentPluginCase(app, ref doc, product, "35-35-35", 35, 35, 35, true);
            doc.Close(false); doc = null;
            doc = app.OpenDocumentFile(Path.Combine(_run, "input.rvt"));
            RunCurrentPluginCase(app, ref doc, product, "30-35-35", 30, 35, 35, false);
            doc.Close(false); doc = null;
            doc = app.OpenDocumentFile(Path.Combine(_run, "input.rvt"));
            RunCurrentCoverRollback(doc, product);
            // The failing transaction fixture is deliberately never saved.
            doc.Close(false); doc = null;
        }

        private void RunCurrentPluginCase(Autodesk.Revit.ApplicationServices.Application app, ref Document doc,
            Assembly product, string name, double top, double bottom, double side, bool makeViews)
        {
            var fixture = CurrentFixture(doc, product);
            var coverTypesBefore = CurrentCoverTypes(doc);
            var parameters = CurrentParameters(product, fixture.Type, top, bottom, side);
            object session = CurrentSession(product, fixture);
            var plan = CurrentCalculate(session, parameters);
            WriteNative("plugin-" + name + "-plan.json", new { parameters = CurrentParameterEvidence(parameters),
                plan.Diagnostics, plan.Groups, plan.Bars, plan.PerimeterUEdges });
            Require(plan.CanCreate, "Production plan blocked: " + string.Join("; ", plan.Diagnostics.Select(d => d.Code + ": " + d.Message)));
            Require(plan.Bars.Any(b => b.Placement.Component == "PerimeterU"), "Actual production plan contains no U-bars.");
            CurrentAssertLevels(plan.Bars.Where(b => b.Placement.Component == "Background"), fixture.ThicknessMm, top, bottom, fixture.Type.BarModelDiameter * 304.8);
            int sets = (int)CurrentInvoke(product.GetType("SlabReinforcement.Integration.RebarPlanWriter", true).GetMethod("Create"), null,
                doc, fixture.Slab, session, plan, parameters);
            Require(sets == plan.Groups.Count, "Writer set count differs from the accepted plan.");
            CurrentAssertCoverTypesUnchanged(doc, coverTypesBefore);
            var before = CurrentReadAndValidate(doc, fixture, plan, top, bottom, side);
            WriteNative("plugin-" + name + "-before.json", before);
            Check("plugin-" + name + "-physical-readback", new { before.PhysicalBars, before.Sets, before.Signature, before.LayerZ, before.UBars, before.UStationsChecked, before.MaxGapMm });

            string output = Path.Combine(_run, "1-layer-current-plugin-" + name + ".rvt");
            var views = makeViews ? CreateCurrentPluginViews(doc, fixture, before) : new List<ElementId>();
            doc.SaveAs(output, new SaveAsOptions { MaximumBackups = 1 });
            if (views.Count > 0)
            {
                var options = new ImageExportOptions { FilePath = Path.Combine(_run, "current-plugin-" + name), ExportRange = ExportRange.SetOfViews,
                    ZoomType = ZoomFitType.FitToPage, PixelSize = 2200, HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG };
                options.SetViewsAndSheets(views); doc.ExportImage(options);
            }
            doc.Close(false); doc = null;
            string savedHash = Hash(output); // Hash only after closing the RVT sharing lock.
            doc = app.OpenDocumentFile(output);
            var reopened = CurrentFixture(doc, product, requireClean: false, stableEdge: fixture.EdgeStable);
            var after = CurrentReadAndValidate(doc, reopened, plan, top, bottom, side);
            WriteNative("plugin-" + name + "-after.json", after);
            Require(before.Signature == after.Signature && before.PhysicalBars == after.PhysicalBars && before.Sets == after.Sets,
                "Physical geometry or counts changed after saving/reopening.");
            var summary = new { model = output, sha256 = savedHash, actualPluginExecuted = true, execution = "production Session+Writer API, not UI picks",
                covers = new { top, bottom, side }, after.PhysicalBars, after.Sets, after.UBars, after.UStationsChecked, after.LayerZ,
                after.MaxGapMm, before.Signature, saveReopenExact = true, warnings = CurrentWarnings(doc) };
            Check("plugin-" + name + "-save-reopen", summary);
            _report["pluginCase" + name] = summary;
        }

        private void RunCurrentCoverRollback(Document doc, Assembly product)
        {
            var fixture = CurrentFixture(doc, product);
            var coverTypes = CurrentCoverTypes(doc);
            var original = CurrentCovers(doc, fixture.Floor);
            double uniqueTop = 31.234567;
            while (coverTypes.Values.Any(v => Math.Abs(v - uniqueTop) < 0.000001)) uniqueTop += 0.111;
            var parameters = CurrentParameters(product, fixture.Type, uniqueTop, 35, 35);
            var session = CurrentSession(product, fixture);
            var plan = CurrentCalculate(session, parameters);
            Require(plan.CanCreate, "Rollback fixture could not obtain a valid production plan.");
            // The failure occurs in the first group after Writer has applied cover types.
            // No fabricated geometry is committed and this document is never saved.
            plan.Groups[0].Placement.TypeKey = "CURRENT_PLUGIN_ROLLBACK_MISSING_TYPE";
            Exception failure = null;
            try { CurrentInvoke(product.GetType("SlabReinforcement.Integration.RebarPlanWriter", true).GetMethod("Create"), null,
                    doc, fixture.Slab, session, plan, parameters); }
            catch (Exception error) { failure = error; }
            Require(failure is KeyNotFoundException, "Rollback test failed at an unexpected stage: " + failure);
            Require(CurrentCovers(doc, fixture.Floor).SequenceEqual(original), "Failed creation retained changed host cover type IDs.");
            Require(CurrentCoverTypes(doc).OrderBy(p => p.Key).SequenceEqual(coverTypes.OrderBy(p => p.Key)), "Failed creation retained or changed a cover type.");
            Require(new FilteredElementCollector(doc).OfClass(typeof(Rebar)).GetElementCount() == 0, "Failed creation retained Rebar elements.");
            Check("plugin-atomic-cover-rollback", new { selectedTopMm = uniqueTop, originalCoverTypeIds = original, failure = failure.GetType().FullName,
                coverTypeInventoryRestored = true, physicalBars = 0, saved = false, fixtureIsCopy = true });
        }

        private CurrentFixtureData CurrentFixture(Document doc, Assembly product, bool requireClean = true, string stableEdge = null)
        {
            var floor = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().Single();
            if (requireClean) Require(new FilteredElementCollector(doc).OfClass(typeof(Rebar)).GetElementCount() == 0
                && new FilteredElementCollector(doc).OfClass(typeof(RebarInSystem)).GetElementCount() == 0
                && new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcement)).GetElementCount() == 0
                && new FilteredElementCollector(doc).OfClass(typeof(PathReinforcement)).GetElementCount() == 0, "Production fixture must start without reinforcement.");
            var types = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToList();
            var type = types.Where(t => Math.Abs(t.BarNominalDiameter * 304.8 - 12) < 0.01 && Math.Abs(t.BarModelDiameter * 304.8 - 12) < 0.01
                && Numeric(t.LookupParameter("Арм.КлассЧисло")) == 500 && Numeric(t.LookupParameter("Рзм.ПогМетрыВкл")) == 0)
                .OrderBy(t => t.Name == "ø12" ? 0 : 1).ThenBy(t => t.Name, StringComparer.Ordinal).First();
            var topFace = floor.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine })
                .OfType<Solid>().Where(s => s.Volume > 0).SelectMany(s => s.Faces.Cast<Face>()).OfType<PlanarFace>()
                .Where(f => f.FaceNormal.Z > .99).OrderByDescending(f => f.Origin.Z).First();
            var edge = topFace.EdgeLoops.Cast<EdgeArray>().SelectMany(a => a.Cast<Edge>()).Where(e => e.AsCurve() is Line)
                .Where(e => Math.Abs(((Line)e.AsCurve()).Direction.Y) < 1e-8).OrderByDescending(e => e.AsCurve().Length).First();
            var edgeReference = stableEdge == null ? edge.Reference : Reference.ParseFromStableRepresentation(doc, stableEdge);
            object slab = CurrentInvoke(product.GetType("SlabReinforcement.Integration.SlabGeometryReader", true).GetMethod("Read"), null, doc, floor, edgeReference);
            var frame = (SlabFrame)Get(slab, "Frame");
            var data = new CurrentFixtureData { Floor = floor, Slab = slab, Frame = frame, Types = types, Type = type,
                ThicknessMm = (double)Get(slab, "ThicknessMm"), Slabs = (List<PolygonInput>)Get(slab, "Slabs"), EdgeStable = edgeReference.ConvertToStableRepresentation(doc) };
            Require(Math.Abs(data.ThicknessMm - 200) < .01, "Expected a 200 mm test slab.");
            Require(data.Slabs.Count == 1 && data.Slabs[0].Holes.Count == 0 && data.Slabs[0].Shell.Count == 14, "Unexpected clean first-layer footprint.");
            if (requireClean) Check("plugin-clean-fixture", new { floorId = floor.Id.IntegerValue, floor.Name,
                type = type.Name, nominalMm = type.BarNominalDiameter * 304.8, physicalMm = type.BarModelDiameter * 304.8,
                bendMm = type.StandardBendDiameter * 304.8, edgeStable = edge.Reference.ConvertToStableRepresentation(doc),
                edgeStart = Mm(edge.AsCurve().GetEndPoint(0)), edgeEnd = Mm(edge.AsCurve().GetEndPoint(1)), data.ThicknessMm });
            return data;
        }

        private static object CurrentParameters(Assembly product, RebarBarType type, double top, double bottom, double side)
        {
            object parameters = Activator.CreateInstance(product.GetType("SlabReinforcement.PreviewParameters", true));
            Set(parameters, "TopCoverMm", (double?)top); Set(parameters, "BottomCoverMm", (double?)bottom); Set(parameters, "EdgeCoverMm", side);
            Set(parameters, "IncludeZones", false); Set(parameters, "ConcreteClass", "B25"); Set(parameters, "Direction", "Низ X");
            Set(parameters, "AllowBarIntersections", true);
            var layers = (IList)Get(parameters, "BackgroundLayers");
            foreach (string direction in new[] { "Низ X", "Низ Y", "Верх X", "Верх Y" })
            {
                var layer = Activator.CreateInstance(product.GetType("SlabReinforcement.PreviewBackgroundLayer", true));
                Set(layer, "Enabled", true); Set(layer, "Direction", direction); Set(layer, "SpecKey", type.UniqueId); Set(layer, "SpacingMm", 200d); layers.Add(layer);
            }
            var perimeter = Get(parameters, "Perimeter"); Set(perimeter, "Enabled", true); Set(perimeter, "Outer", true); Set(perimeter, "Openings", false);
            Set(perimeter, "SpecKey", type.UniqueId); Set(perimeter, "SpacingMm", 200d); Set(perimeter, "LegLengthMm", 500d);
            return parameters;
        }

        private static object CurrentSession(Assembly product, CurrentFixtureData fixture)
        {
            object mesh = Activator.CreateInstance(product.GetType("SlabReinforcement.Integration.ImportedMeshSnapshot", true), true);
            return Activator.CreateInstance(product.GetType("SlabReinforcement.Integration.ReinforcementSession", true),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { fixture.Slab, mesh, fixture.Types }, CultureInfo.InvariantCulture);
        }

        private static ReinforcementPlan CurrentCalculate(object session, object parameters)
        {
            var context = CurrentInvoke(session.GetType().GetMethod("CreateContext"), session);
            var calculate = (Delegate)Get(context, "Calculate");
            try { return (ReinforcementPlan)calculate.DynamicInvoke(parameters, CancellationToken.None); }
            catch (TargetInvocationException error) when (error.InnerException != null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }

        private static object CurrentInvoke(MethodInfo method, object target, params object[] arguments)
        {
            try { return method.Invoke(target, arguments); }
            catch (TargetInvocationException error) when (error.InnerException != null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }

        private CurrentEvidence CurrentReadAndValidate(Document doc, CurrentFixtureData fixture, ReinforcementPlan expected,
            double top, double bottom, double side)
        {
            var result = new CurrentEvidence();
            var bars = new List<PlannedBar>(); var physicalSetIds = new List<int>();
            foreach (var rebar in new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().Where(r => r.GetHostId().Equals(fixture.Floor.Id)))
            {
                var type = (RebarBarType)doc.GetElement(rebar.GetTypeId());
                var firstPoints = new List<Point3>(); int count = 0;
                for (int position = 0; position < rebar.NumberOfBarPositions; position++)
                {
                    if (!rebar.DoesBarExistAtPosition(position)) continue;
                    var curves = rebar.GetTransformedCenterlineCurves(true, false, false, MultiplanarOption.IncludeAllMultiplanarCurves, position);
                    var path = new List<CenterlineSegment>();
                    foreach (var curve in curves)
                    {
                        Require(curve is Line || curve is Arc, "Unexpected actual Revit curve kind.");
                        Point3 Local(XYZ p) => fixture.Frame.ToLocal(new Point3(p.X * 304.8, p.Y * 304.8, p.Z * 304.8));
                        path.Add(new CenterlineSegment { Start = Local(curve.GetEndPoint(0)), End = Local(curve.GetEndPoint(1)),
                            Midpoint = curve is Arc ? (Point3?)Local(curve.Evaluate(.5, true)) : null });
                    }
                    Require(path.Count > 0, "Empty Revit centreline."); firstPoints.Add(path[0].Start); count++;
                    bars.Add(new PlannedBar { SpecKey = "Readback|" + rebar.UniqueId + "|" + position, Centerline = path,
                        Start = new Point2(path[0].Start.X, path[0].Start.Y), End = new Point2(path[path.Count - 1].End.X, path[path.Count - 1].End.Y),
                        Placement = new BarPlacement { TypeKey = type.UniqueId, Component = "Readback", ModelDiameterMm = type.BarModelDiameter * 304.8, ElevationMm = path[0].Start.Z } });
                    physicalSetIds.Add(rebar.Id.IntegerValue);
                }
                double maxGap = firstPoints.Zip(firstPoints.Skip(1), (a, b) => CurrentDistance(a, b)).DefaultIfEmpty(0).Max();
                result.SetDetails.Add(new CurrentSetEvidence { Id = rebar.Id.IntegerValue, Type = type.Name, Quantity = rebar.Quantity,
                    Positions = rebar.NumberOfBarPositions, PhysicalBars = count, Layout = rebar.LayoutRule.ToString(), MaxGapMm = maxGap });
                result.MaxGapMm = Math.Max(result.MaxGapMm, maxGap);
            }
            Require(bars.Count == expected.Bars.Count, "Actual physical bar count differs from production preview.");
            var match = ExistingReinforcementMatcher.Match(expected.Bars, bars, CurrentToleranceMm);
            Require(match.IsComplete && match.MatchedCount == bars.Count, "Actual curves differ from accepted production plan.");
            foreach (var m in match.Matches)
            {
                var actual = bars[m.ActualIndex]; var planned = expected.Bars[m.ExpectedIndex];
                actual.Placement.Component = planned.Placement.Component; actual.ColorKey = planned.SpecKey;
                var set = result.SetDetails.Single(s => s.Id == physicalSetIds[m.ActualIndex]);
                if (!set.Components.Contains(planned.Placement.Component)) set.Components.Add(planned.Placement.Component);
            }
            var errors = CompositePlanValidator.Validate(bars, allowBarIntersections: true)
                .Concat(BarContainmentValidator.Validate(bars, fixture.Slabs, fixture.ThicknessMm, top, bottom, side + .05)).Where(d => d.IsBlocking).ToList();
            Require(errors.Count == 0, "Physical readback geometry/cover/duplicates failed: " + string.Join("; ", errors.Select(d => d.Code + ": " + d.Message)));
            var actualBackground = bars.Where(b => b.Placement.Component == "Background").ToList();
            CurrentAssertLevels(actualBackground, fixture.ThicknessMm, top, bottom, fixture.Type.BarModelDiameter * 304.8, actualKeys: true);
            result.LayerZ = actualBackground.GroupBy(b => b.ColorKey).ToDictionary(g => g.Key,
                g => g.Select(b => Math.Round(b.Placement.ElevationMm, 6)).Distinct().ToList());
            Require(result.SetDetails.All(s => s.MaxGapMm <= 200 + CurrentToleranceMm), "A physical set exceeds 200 mm actual spacing.");
            Require(result.SetDetails.All(s => s.Positions <= 1 || s.Layout == "MaximumSpacing"), "A multi-bar production set does not use native MaximumSpacing layout.");
            Require(result.SetDetails.All(s => s.Positions == s.PhysicalBars && s.Quantity == s.PhysicalBars), "A production set has missing/excluded positions.");
            var anchors = new List<Point3>();
            foreach (var bar in bars.Where(b => b.Placement.Component == "PerimeterU"))
            {
                var legs = bar.Centerline.Where(s => !s.Midpoint.HasValue && Math.Abs(s.Start.Z - s.End.Z) < .01).OrderBy(s => s.Start.Z).ToList();
                Require(legs.Count == 2 && bar.Centerline.Count == 5, "Physical U does not have two horizontal legs and five rounded segments.");
                bool alongX = Math.Abs(legs[0].Start.X - legs[0].End.X) > Math.Abs(legs[0].Start.Y - legs[0].End.Y);
                string lowerKey = "Background|Низ " + (alongX ? "X" : "Y");
                string upperKey = "Background|Верх " + (alongX ? "X" : "Y");
                double station = alongX ? legs[0].Start.Y : legs[0].Start.X;
                Require(actualBackground.Any(b => b.ColorKey == lowerKey && Math.Abs(b.Placement.ElevationMm - legs[0].Start.Z) <= CurrentToleranceMm
                    && Math.Abs((alongX ? b.Start.Y : b.Start.X) - station) <= CurrentToleranceMm), "U lower station/elevation does not follow an actual lower background row.");
                Require(actualBackground.Any(b => b.ColorKey == upperKey && Math.Abs(b.Placement.ElevationMm - legs[1].Start.Z) <= CurrentToleranceMm),
                    "U upper elevation differs from its corresponding upper background layer.");
                var web = bar.Centerline.Single(s => !s.Midpoint.HasValue && Math.Abs(s.Start.X - s.End.X) < .01
                    && Math.Abs(s.Start.Y - s.End.Y) < .01 && Math.Abs(s.Start.Z - s.End.Z) > .01);
                anchors.Add(new Point3(web.Start.X, web.Start.Y, legs[0].Start.Z));
                result.UStationsChecked++;
            }
            var lower = actualBackground.Where(b => b.ColorKey == "Background|Низ X" || b.ColorKey == "Background|Низ Y").ToList();
            Require(anchors.Count == lower.Count * 2, "Expected one physical U at each end of every lower background bar.");
            foreach (var bar in lower)
                foreach (var endpoint in new[] { bar.Centerline[0].Start, bar.Centerline[0].End })
                {
                    Require(anchors.Any(a => CurrentDistance(a, endpoint) <= CurrentToleranceMm * 2),
                        "A lower background endpoint has no matching physical U web at the same station/elevation.");
                    result.LowerEndpointsChecked++;
                }
            var actualCovers = CurrentCoverDistances(doc, fixture.Floor);
            Require(Math.Abs(actualCovers[0] - top) < .000001 && Math.Abs(actualCovers[1] - bottom) < .000001 && Math.Abs(actualCovers[2] - side) < .000001,
                "Host cover parameters differ from accepted values.");
            Require(new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcement)).GetElementCount() == 0
                && new FilteredElementCollector(doc).OfClass(typeof(PathReinforcement)).GetElementCount() == 0
                && new FilteredElementCollector(doc).OfClass(typeof(RebarInSystem)).GetElementCount() == 0, "Production test accidentally mixed native Area/Path systems.");
            result.PhysicalBars = bars.Count; result.Sets = result.SetDetails.Count; result.UBars = bars.Count(b => b.Placement.Component == "PerimeterU");
            result.Bars = bars; result.CoversMm = actualCovers;
            string canonical = string.Join("\n", bars.Select(CurrentBarKey).OrderBy(k => k, StringComparer.Ordinal));
            using (var sha = SHA256.Create()) result.Signature = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "");
            return result;
        }

        private static void CurrentAssertLevels(IEnumerable<PlannedBar> bars, double thickness, double top, double bottom, double diameter, bool actualKeys = false)
        {
            var expected = new Dictionary<string, double> { ["Background|Низ X"] = -thickness + bottom + diameter / 2,
                ["Background|Низ Y"] = -thickness + bottom + diameter * 1.5,
                ["Background|Верх X"] = -top - diameter / 2, ["Background|Верх Y"] = -top - diameter * 1.5 };
            var actual = bars.GroupBy(b => actualKeys ? b.ColorKey : b.SpecKey).ToDictionary(g => g.Key, g => g.ToList());
            Require(actual.Count == 4, "Expected four separate background directions.");
            foreach (var item in expected) Require(actual.ContainsKey(item.Key) && actual[item.Key].All(b => Math.Abs(b.Placement.ElevationMm - item.Value) <= CurrentToleranceMm),
                "Background elevation differs from user layer formula: " + item.Key);
        }

        private static int[] CurrentCovers(Document doc, Floor floor) => new[] { BuiltInParameter.CLEAR_COVER_TOP, BuiltInParameter.CLEAR_COVER_BOTTOM, BuiltInParameter.CLEAR_COVER_OTHER }
            .Select(p => floor.get_Parameter(p).AsElementId().IntegerValue).ToArray();
        private static double[] CurrentCoverDistances(Document doc, Floor floor) => CurrentCovers(doc, floor)
            .Select(id => ((RebarCoverType)doc.GetElement(new ElementId(id))).CoverDistance * 304.8).ToArray();
        private static Dictionary<int, double> CurrentCoverTypes(Document doc) => new FilteredElementCollector(doc).OfClass(typeof(RebarCoverType)).Cast<RebarCoverType>()
            .ToDictionary(t => t.Id.IntegerValue, t => t.CoverDistance * 304.8);
        private static void CurrentAssertCoverTypesUnchanged(Document doc, Dictionary<int, double> before)
        {
            var after = CurrentCoverTypes(doc);
            Require(before.All(p => after.ContainsKey(p.Key) && Math.Abs(after[p.Key] - p.Value) < 1e-9), "Writer edited/deleted an existing shared cover type.");
        }
        private static object CurrentParameterEvidence(object parameters) => new { topCoverMm = Get(parameters, "TopCoverMm"), bottomCoverMm = Get(parameters, "BottomCoverMm"),
            sideCoverMm = Get(parameters, "EdgeCoverMm"), layerSpacingMm = 200, perimeterLegMm = 500, lowerGridControlsPerimeter = true, actualDxfZonesRequested = false };
        private static double CurrentDistance(Point3 a, Point3 b) => Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y)+(a.Z-b.Z)*(a.Z-b.Z));
        private static string CurrentPointKey(Point3 p) => string.Join(",", new[] { p.X, p.Y, p.Z }.Select(v => Math.Round(v, 6).ToString("R", CultureInfo.InvariantCulture)));
        private static string CurrentBarKey(PlannedBar bar) => bar.Placement.TypeKey + "|" + string.Join("|", bar.Centerline.Select(s =>
        {
            string a = CurrentPointKey(s.Start), b = CurrentPointKey(s.End);
            if (string.CompareOrdinal(a, b) > 0) { string swap = a; a = b; b = swap; }
            return a + ":" + b + (s.Midpoint.HasValue ? ":A:" + CurrentPointKey(s.Midpoint.Value) : ":L");
        }).OrderBy(k => k, StringComparer.Ordinal));
        private static object CurrentWarnings(Document doc) => doc.GetWarnings().Select(w => new { description = w.GetDescriptionText(),
            severity = w.GetSeverity().ToString(), failureId = w.GetFailureDefinitionId().Guid }).ToList();

        private List<ElementId> CreateCurrentPluginViews(Document doc, CurrentFixtureData fixture, CurrentEvidence evidence)
        {
            using (var transaction = new Transaction(doc, "API test result view"))
            {
                transaction.Start();
                var floor = fixture.Floor;
                var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
                var view = View3D.CreateIsometric(doc, type.Id); view.Name = "ТЕСТ ПЛАГИНА — фон X Y и П по нижним рядам";
                var box = floor.get_BoundingBox(null); view.SetSectionBox(new BoundingBoxXYZ { Min = box.Min - new XYZ(1, 1, 1), Max = box.Max + new XYZ(1, 1, 1) }); view.IsSectionBoxActive = true;
                OrientNative(view, (box.Min + box.Max) / 2, new XYZ(-1, -1, 1));
                Style(view, hideFloor: false);

                // Choose a real 90-degree convex corner with at least 4 m on each side.
                // The detail section box is aligned with the actual slab-local axes.
                var shell = fixture.Slabs.Single().Shell;
                double areaSign = Math.Sign(shell.Select((p, i) => p.X * shell[(i + 1) % shell.Count].Y - shell[(i + 1) % shell.Count].X * p.Y).Sum());
                var corners = new List<Tuple<Point2, Point2, Point2>>();
                for (int i = 0; i < shell.Count; i++)
                {
                    var p = shell[i]; var previous = shell[(i + shell.Count - 1) % shell.Count]; var next = shell[(i + 1) % shell.Count];
                    double ax = previous.X - p.X, ay = previous.Y - p.Y, bx = next.X - p.X, by = next.Y - p.Y;
                    double a = Math.Sqrt(ax * ax + ay * ay), b = Math.Sqrt(bx * bx + by * by);
                    if (a < 4000 || b < 4000 || Math.Abs(ax * bx + ay * by) > a * b * 1e-8 || (bx * ay - by * ax) * areaSign <= 0) continue;
                    corners.Add(Tuple.Create(p, new Point2(ax / a, ay / a), new Point2(bx / b, by / b)));
                }
                Require(corners.Count > 0, "No suitable convex corner for the representative detail view.");
                var corner = corners.OrderBy(c => c.Item1.X + c.Item1.Y).First();
                double inwardX = corner.Item2.X + corner.Item3.X, inwardY = corner.Item2.Y + corner.Item3.Y;
                var centre = new Point3(corner.Item1.X + inwardX * 1300, corner.Item1.Y + inwardY * 1300, -fixture.ThicknessMm / 2);
                var detail = View3D.CreateIsometric(doc, type.Id); detail.Name = "СТАРТ — угол плиты — голубой фон и оранжевые П";
                var transform = Transform.Identity;
                transform.Origin = new XYZ(fixture.Frame.Origin.X, fixture.Frame.Origin.Y, fixture.Frame.Origin.Z) / 304.8;
                transform.BasisX = new XYZ(fixture.Frame.XAxis.X, fixture.Frame.XAxis.Y, fixture.Frame.XAxis.Z);
                transform.BasisY = new XYZ(fixture.Frame.YAxis.X, fixture.Frame.YAxis.Y, fixture.Frame.YAxis.Z);
                transform.BasisZ = new XYZ(fixture.Frame.Normal.X, fixture.Frame.Normal.Y, fixture.Frame.Normal.Z);
                detail.SetSectionBox(new BoundingBoxXYZ { Transform = transform,
                    Min = new XYZ(centre.X - 1600, centre.Y - 1600, -fixture.ThicknessMm - 100) / 304.8,
                    Max = new XYZ(centre.X + 1600, centre.Y + 1600, 100) / 304.8 });
                detail.IsSectionBoxActive = true;
                var centreWorld = fixture.Frame.ToWorld(centre);
                var direction = transform.BasisX * -inwardX + transform.BasisY * -inwardY + transform.BasisZ * .75;
                OrientNative(detail, new XYZ(centreWorld.X, centreWorld.Y, centreWorld.Z) / 304.8, direction);
                Style(detail, hideFloor: true);
                var start = StartingViewSettings.GetStartingViewSettings(doc); if (start.IsAcceptableStartingView(detail.Id)) start.ViewId = detail.Id;
                Require(transaction.Commit() == TransactionStatus.Committed, "Result views could not be saved.");
                _report["representativeViews"] = new { full = new { id = view.Id.IntegerValue, view.Name }, start = new { id = detail.Id.IntegerValue, detail.Name },
                    cornerLocalMm = new[] { corner.Item1.X, corner.Item1.Y }, boxSizeMm = 3200, floorHiddenOnlyInDetail = true,
                    backgroundColor = "#469CD4", perimeterColor = "#E7762B", geometryChanged = false };
                return new List<ElementId> { view.Id, detail.Id };

                void Style(View3D target, bool hideFloor)
                {
                    target.ViewTemplateId = ElementId.InvalidElementId; target.DetailLevel = ViewDetailLevel.Fine; target.DisplayStyle = DisplayStyle.Wireframe;
                    target.AreAnnotationCategoriesHidden = true;
                    var floorStyle = new OverrideGraphicSettings().SetProjectionLineColor(new Color(190, 197, 202)).SetProjectionLineWeight(1)
                        .SetSurfaceForegroundPatternVisible(false).SetSurfaceBackgroundPatternVisible(false)
                        .SetCutForegroundPatternVisible(false).SetCutBackgroundPatternVisible(false).SetSurfaceTransparency(100);
                    target.SetElementOverrides(floor.Id, floorStyle);
                    if (hideFloor) target.HideElements(new List<ElementId> { floor.Id });
                    foreach (var set in evidence.SetDetails)
                    {
                        var rebar = (Rebar)doc.GetElement(new ElementId(set.Id)); rebar.SetUnobscuredInView(target, true);
                        var color = set.Components.Contains("PerimeterU") ? new Color(231, 118, 43) : new Color(70, 156, 212);
                        target.SetElementOverrides(rebar.Id, new OverrideGraphicSettings().SetProjectionLineWeight(1).SetCutLineWeight(1)
                            .SetProjectionLineColor(color).SetCutLineColor(color));
                    }
                }
            }
        }

        private sealed class CurrentFixtureData
        {
            public Floor Floor; public object Slab; public SlabFrame Frame; public List<RebarBarType> Types; public RebarBarType Type;
            public double ThicknessMm; public List<PolygonInput> Slabs; public string EdgeStable;
        }
        private sealed class CurrentEvidence
        {
            public int PhysicalBars { get; set; } public int Sets { get; set; } public int UBars { get; set; } public int UStationsChecked { get; set; } public int LowerEndpointsChecked { get; set; }
            public double MaxGapMm { get; set; } public double[] CoversMm { get; set; } public string Signature { get; set; }
            public Dictionary<string, List<double>> LayerZ { get; set; }
            public List<CurrentSetEvidence> SetDetails { get; set; } = new List<CurrentSetEvidence>();
            public List<PlannedBar> Bars { get; set; }
        }
        private sealed class CurrentSetEvidence
        {
            public int Id { get; set; } public string Type { get; set; } public int Quantity { get; set; } public int Positions { get; set; }
            public int PhysicalBars { get; set; } public string Layout { get; set; } public double MaxGapMm { get; set; }
            public List<string> Components { get; set; } = new List<string>();
        }
    }
}
