using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SlabReinforcement.Core;
using SlabReinforcement.Integration;

namespace SlabReinforcement.ZonePathSmoke
{
    public sealed partial class ZonePathApplication
    {
        private void RunCreationScopeCases(Document doc, SlabGeometrySnapshot slab, RebarBarType mainType)
        {
            Case("creation-scope-background-ignores-invalid-zones", () => TestBackgroundCreationScope(doc, slab, mainType));
            Case("creation-scope-zones-ignores-enabled-background", () => TestZoneCreationScope(doc, slab, mainType, false));
            Case("creation-scope-zones-reuses-existing-background", () => TestZoneCreationScope(doc, slab, mainType, true));
            Case("creation-scope-rejects-mismatched-plan-before-write", () => TestMismatchedCreationScope(doc, slab, mainType));
        }

        private object TestMismatchedCreationScope(Document doc, SlabGeometrySnapshot slab, RebarBarType mainType)
        {
            using (var operation = new TransactionGroup(doc, "Mismatched active tab rollback"))
            {
                operation.Start();
                var p = ScopeParameters(slab, mainType, mainType);
                p.CreationScope = ReinforcementCreationScope.Background;
                var session = new ReinforcementSession(slab, new ImportedMeshSnapshot(), new List<RebarBarType> { mainType });
                var backgroundPlan = session.CreateContext().Calculate(p, default);
                Require(backgroundPlan.CanCreate, "Mismatch fixture must start with a valid background plan: " + Diagnostics(backgroundPlan));
                Require(backgroundPlan.Bars.Any(b => b.Placement?.Component == "Background"), "Mismatch fixture contains no background.");
                var beforeDocumentIds = ScopeDocumentIds(doc);
                var beforeNative = ScopeNativeElements(doc, slab);
                var beforeNativeIds = new HashSet<ElementId>(beforeNative.Select(e => e.Id));
                var beforePhysical = ExistingRebarReader.Read(slab);

                // Simulate the active tab changing after a background calculation.
                p.CreationScope = ReinforcementCreationScope.Zones;
                bool rejected = false;
                string message = null;
                try { RebarPlanWriter.Create(doc, slab, session, backgroundPlan, p); }
                catch (ArgumentException exception) { rejected = true; message = exception.Message; }

                Require(rejected, "Writer accepted a background plan for the Zones tab.");
                Require(message.Contains("другой вкладки"), "Writer failed for a reason other than the creation scope guard: " + message);
                var afterDocumentIds = ScopeDocumentIds(doc);
                var afterNative = ScopeNativeElements(doc, slab);
                Require(beforeDocumentIds.SetEquals(afterDocumentIds), "Rejected scope changed document element IDs.");
                Require(beforeNativeIds.SetEquals(afterNative.Select(e => e.Id)), "Rejected scope changed native reinforcement IDs/counts.");
                var afterPhysical = ExistingRebarReader.Read(slab);
                Require(afterPhysical.Count == beforePhysical.Count && ExistingReinforcementMatcher.Match(beforePhysical, afterPhysical).IsComplete,
                    "Rejected scope changed physical reinforcement.");
                var result = new
                {
                    rejected, message, planBackgroundBars = backgroundPlan.Bars.Count,
                    documentElementCount = beforeDocumentIds.Count, documentIdsUnchanged = true,
                    nativeBefore = ScopeNativeCounts(beforeNative), nativeAfter = ScopeNativeCounts(afterNative), physicalUnchanged = true
                };
                operation.RollBack();
                return result;
            }
        }

        private object TestBackgroundCreationScope(Document doc, SlabGeometrySnapshot slab, RebarBarType mainType)
        {
            using (var operation = new TransactionGroup(doc, "Background tab creation scope rollback"))
            {
                operation.Start();
                var p = ScopeParameters(slab, mainType, mainType);
                p.CreationScope = ReinforcementCreationScope.Background;
                p.SourceGroupKey = "missing-inactive-source";
                p.CalculationAxesConfirmed = false;
                // These inactive rows are intentionally invalid and have duplicate SpecKeys.
                // Ignoring them during calculation alone is insufficient: the writer must too.
                p.Selections = new List<PreviewSelection>
                {
                    new PreviewSelection { ColorKey = "duplicate", SpecKey = "missing-inactive-type", SpacingMm = double.NaN },
                    new PreviewSelection { ColorKey = "duplicate", SpecKey = "missing-inactive-type", SpacingMm = -100 }
                };
                var session = new ReinforcementSession(slab, new ImportedMeshSnapshot(), new List<RebarBarType> { mainType });
                var plan = session.CreateContext().Calculate(p, default);
                Require(plan.CanCreate, "Inactive zones blocked background calculation: " + Diagnostics(plan));
                Require(plan.Bars.Any(b => b.Placement?.Component == "Background"), "No background rows were planned.");
                Require(plan.Bars.Any(b => b.Placement?.Component == "PerimeterU"), "No main perimeter was planned.");
                Require(plan.Bars.All(b => b.Placement?.Component == "Background" || b.Placement?.Component == "PerimeterU"),
                    "The background tab contains a zone component.");
                Require(plan.Groups.All(g => g.Placement?.Component != "Zones"), "The background tab contains a native zone group.");

                var before = ScopeNativeElements(doc, slab);
                var beforeIds = new HashSet<ElementId>(before.Select(e => e.Id));
                int created = RebarPlanWriter.Create(doc, slab, session, plan, p);
                var added = ScopeNativeElements(doc, slab).Where(e => !beforeIds.Contains(e.Id)).ToList();
                var physical = ExistingRebarReader.Read(slab, id => !beforeIds.Contains(id));
                Require(added.OfType<PathReinforcement>().Count() == 0, "Background creation silently created PathReinforcement.");
                Require(added.OfType<RebarInSystem>().Count() == 0, "Background creation silently created system children.");
                Require(added.OfType<AreaReinforcement>().Count() == 0, "Unexpected area reinforcement was created.");
                Require(added.OfType<Rebar>().Count() == plan.Groups.Count, "Background native group count differs.");
                Require(physical.Count == plan.Bars.Count, "Background physical count differs from scoped plan.");
                Require(physical.All(b => b.Placement.TypeKey == mainType.UniqueId), "Background wrote a hidden zone type.");
                Require(ExistingReinforcementMatcher.Match(plan.Bars, physical).IsComplete, "Background native geometry/type differs.");
                int backgroundPhysical = PhysicalCountByPartition(doc, slab, added, " фон");
                int perimeterPhysical = PhysicalCountByPartition(doc, slab, added, "П-обрамление");
                Require(backgroundPhysical == plan.Bars.Count(b => b.Placement.Component == "Background"), "Background physical partition count differs.");
                Require(perimeterPhysical == plan.Bars.Count(b => b.Placement.Component == "PerimeterU"), "Main P physical partition count differs.");
                var result = new
                {
                    scope = p.CreationScope.ToString(), inactiveZoneSelectionCount = p.Selections.Count,
                    createdGroups = created, backgroundPhysical, perimeterPhysical,
                    native = ScopeNativeCounts(added), physicalByType = PhysicalTypeCounts(physical),
                    allPhysicalMatched = true
                };
                operation.RollBack();
                return result;
            }
        }

        private object TestZoneCreationScope(Document doc, SlabGeometrySnapshot slab, RebarBarType mainType, bool existingBackground)
        {
            using (var operation = new TransactionGroup(doc, "Zone tab creation scope rollback"))
            {
                operation.Start();
                RebarBarType zoneType;
                using (var tx = new Transaction(doc, "Distinct temporary zone type"))
                {
                    tx.Start();
                    zoneType = (RebarBarType)mainType.Duplicate("__scope_zone_" + Guid.NewGuid().ToString("N"));
                    tx.Commit();
                }
                var mesh = ScopeEdgeMesh(slab);
                var p = ScopeParameters(slab, mainType, zoneType);
                var types = new List<RebarBarType> { mainType, zoneType };
                int initialBackgroundPhysical = 0;
                if (existingBackground)
                {
                    p.CreationScope = ReinforcementCreationScope.Background;
                    var firstSession = new ReinforcementSession(slab, mesh, types);
                    var first = firstSession.CreateContext().Calculate(p, default);
                    Require(first.CanCreate, "Existing background fixture failed: " + Diagnostics(first));
                    Require(first.Bars.All(b => b.Placement.Component != "Zones"), "Fixture background scope included zones.");
                    RebarPlanWriter.Create(doc, slab, firstSession, first, p);
                    initialBackgroundPhysical = first.Bars.Count;
                }

                // Keep every background checkbox and main perimeter enabled while creating Zones.
                p.CreationScope = ReinforcementCreationScope.Zones;
                Require(p.BackgroundLayers.All(l => l.Enabled) && p.Perimeter.Enabled, "Fixture lost enabled inactive settings.");
                var beforeElements = ScopeNativeElements(doc, slab);
                var beforeIds = new HashSet<ElementId>(beforeElements.Select(e => e.Id));
                var beforePhysical = ExistingRebarReader.Read(slab);
                var session = new ReinforcementSession(slab, mesh, types);
                var plan = session.CreateContext().Calculate(p, default);
                Require(plan.CanCreate, "Zone scope calculation failed: " + Diagnostics(plan));
                var zones = plan.Bars.Where(b => b.Placement?.Component == "Zones").ToList();
                var localU = plan.Bars.Where(b => b.Placement?.Component == "PerimeterU").ToList();
                Require(zones.Count >= 2 && localU.Count > 0, "Expected native zones and local U endings.");
                Require(plan.Bars.Count == zones.Count + localU.Count, "Zone scope planned a hidden background component.");
                Require(plan.Bars.All(b => b.Placement.TypeKey == zoneType.UniqueId), "Zone scope planned inactive main type.");
                Require(localU.Count == zones.Sum(b => b.EndConnections.Count), "Zone scope added U bars beyond actual zone endpoints.");
                Require(plan.Groups.All(g => g.Placement.TypeKey == zoneType.UniqueId), "Zone scope retained an inactive main group.");
                if (existingBackground)
                {
                    Require(plan.ReferenceBars.Any(b => b.Placement?.Component == "Background"), "Existing background was not referenced.");
                    Require(ExistingReinforcementMatcher.Match(plan.ReferenceBars, beforePhysical).IsComplete, "References are not existing physical bars.");
                }
                else Require(plan.ReferenceBars.Count == 0, "Empty host acquired fictitious existing references.");

                int created = RebarPlanWriter.Create(doc, slab, session, plan, p);
                var afterElements = ScopeNativeElements(doc, slab);
                var afterIds = new HashSet<ElementId>(afterElements.Select(e => e.Id));
                var added = afterElements.Where(e => !beforeIds.Contains(e.Id)).ToList();
                var afterPhysical = ExistingRebarReader.Read(slab);
                var newPhysical = ExistingRebarReader.Read(slab, id => !beforeIds.Contains(id));
                Require(beforeIds.IsSubsetOf(afterIds), "Zone creation removed existing native reinforcement.");
                Require(newPhysical.Count == plan.Bars.Count, "Zone scope wrote extra or missing physical bars.");
                Require(afterPhysical.Count == beforePhysical.Count + plan.Bars.Count, "Zone scope changed existing physical position counts.");
                Require(newPhysical.All(b => b.Placement.TypeKey == zoneType.UniqueId), "Zone creation silently wrote main background/perimeter type.");
                Require(ExistingReinforcementMatcher.Match(plan.Bars, newPhysical).IsComplete, "Native zone/local U geometry does not match the scoped plan.");
                Require(ExistingReinforcementMatcher.Match(beforePhysical, afterPhysical).IsComplete, "Existing background geometry/type changed.");
                var paths = added.OfType<PathReinforcement>().ToList();
                var direct = added.OfType<Rebar>().ToList();
                Require(paths.Count == plan.Groups.Count(g => g.Placement.Component == "Zones"), "Native zone path count differs.");
                Require(direct.Count == plan.Groups.Count(g => g.Placement.Component == "PerimeterU"), "Native local U set count differs.");
                Require(added.OfType<AreaReinforcement>().Count() == 0, "Unexpected area reinforcement was created.");
                Require(added.OfType<RebarInSystem>().All(r => r.GetTypeId() == zoneType.Id), "Native path children use a background type.");
                Require(direct.All(r => r.GetTypeId() == zoneType.Id && Partition(r).Contains("П-обрамление")), "Zone direct bars include an unexpected component.");
                Require(paths.All(path => Partition(path).EndsWith(" доп", StringComparison.Ordinal)), "Native zones lost their drawing partition.");
                Require(PhysicalCountByPartition(doc, slab, added, " фон") == 0, "Zone scope wrote duplicated background rows.");
                var result = new
                {
                    scope = p.CreationScope.ToString(), existingBackground, initialBackgroundPhysical,
                    enabledInactiveBackgroundLayers = p.BackgroundLayers.Count(l => l.Enabled), inactiveMainPerimeter = p.Perimeter.Enabled,
                    referenceBars = plan.ReferenceBars.Count, zonePhysical = zones.Count, localUPhysical = localU.Count,
                    beforePhysical = beforePhysical.Count, afterPhysical = afterPhysical.Count, createdGroups = created,
                    nativeAdded = ScopeNativeCounts(added), newPhysicalByType = PhysicalTypeCounts(newPhysical),
                    mainType = mainType.Name, zoneType = zoneType.Name, existingIdsPreserved = true, existingGeometryPreserved = true
                };
                operation.RollBack();
                return result;
            }
        }

        private static PreviewParameters ScopeParameters(SlabGeometrySnapshot slab, RebarBarType mainType, RebarBarType zoneType)
        {
            var p = ZoneParameters(slab, zoneType, "Низ X");
            p.UsePerimeterUForZoneEnds = true;
            p.AutomaticULegLength = true;
            p.ZoneEndLegLengthMm = double.NaN;
            p.BackgroundLayers = new[] { "Низ X", "Низ Y", "Верх X", "Верх Y" }
                .Select(direction => new PreviewBackgroundLayer
                { Direction = direction, Enabled = true, SpecKey = mainType.UniqueId, SpacingMm = 200 }).ToList();
            p.Perimeter = new PreviewPerimeterOptions
            { Enabled = true, Outer = true, Openings = false, SpecKey = mainType.UniqueId, SpacingMm = 200, LegLengthMm = double.NaN };
            return p;
        }

        private static ImportedMeshSnapshot ScopeEdgeMesh(SlabGeometrySnapshot slab)
        {
            var points = slab.Slabs.SelectMany(s => s.Shell).ToList();
            double maxX = points.Max(p => p.X), minY = points.Min(p => p.Y);
            var mesh = new ImportedMeshSnapshot();
            mesh.Groups.Add(new CadPlaneGroup
            {
                Key = "map", Faces = new List<FaceInput> { new FaceInput
                { Id = "scope-zone-at-edge", ColorKey = "r", Vertices = Rect(maxX - 900, minY + 1000, maxX - 5, minY + 2400) } }
            });
            return mesh;
        }

        private static List<Element> ScopeNativeElements(Document doc, SlabGeometrySnapshot slab)
        {
            var result = new List<Element>();
            result.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().Where(e => e.GetHostId() == slab.Host.Id));
            result.AddRange(new FilteredElementCollector(doc).OfClass(typeof(RebarInSystem)).Cast<RebarInSystem>().Where(e => e.GetHostId() == slab.Host.Id));
            result.AddRange(new FilteredElementCollector(doc).OfClass(typeof(PathReinforcement)).Cast<PathReinforcement>().Where(e => e.GetHostId() == slab.Host.Id));
            result.AddRange(new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcement)).Cast<AreaReinforcement>().Where(e => e.GetHostId() == slab.Host.Id));
            return result;
        }

        private static HashSet<ElementId> ScopeDocumentIds(Document doc)
        {
            var ids = new HashSet<ElementId>(new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElementIds());
            ids.UnionWith(new FilteredElementCollector(doc).WhereElementIsElementType().ToElementIds());
            return ids;
        }

        private static int PhysicalCountByPartition(Document doc, SlabGeometrySnapshot slab, IEnumerable<Element> elements, string marker)
        {
            var ids = new HashSet<ElementId>(elements.Where(e => Partition(e).Contains(marker)).Select(e => e.Id));
            return ExistingRebarReader.Read(slab, ids.Contains).Count;
        }

        private static string Partition(Element element) => element.get_Parameter(BuiltInParameter.NUMBER_PARTITION_PARAM)?.AsString() ?? "";
        private static string Diagnostics(ReinforcementPlan plan) => string.Join("; ", plan.Diagnostics.Select(d => d.Code + ": " + d.Message));
        private static object ScopeNativeCounts(List<Element> elements) => new
        {
            rebarSets = elements.OfType<Rebar>().Count(), paths = elements.OfType<PathReinforcement>().Count(),
            systemChildren = elements.OfType<RebarInSystem>().Count(), areas = elements.OfType<AreaReinforcement>().Count()
        };
        private static object PhysicalTypeCounts(List<PlannedBar> physical) => physical.GroupBy(b => b.Placement.TypeKey)
            .Select(group => new { typeKey = group.Key, physical = group.Count() }).ToList();
    }
}
