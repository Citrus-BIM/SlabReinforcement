using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SlabReinforcement.Core;
using SlabReinforcement.Integration;

namespace SlabReinforcement.ZonePathSmoke
{
    public sealed partial class ZonePathApplication
    {
        private void RunPerimeterAfterZonesCases(Document doc, SlabGeometrySnapshot slab, RebarBarType mainType)
        {
            Case("main-perimeter-after-local-zone-u-other-type", () => TestMainPerimeterAfterZones(doc, slab, mainType, false));
            Case("main-perimeter-after-local-zone-u-shared-position", () => TestMainPerimeterAfterZones(doc, slab, mainType, true));
            Case("local-zone-u-after-main-perimeter", () => TestZonesAfterMainPerimeter(doc, slab, mainType));
        }

        private object TestMainPerimeterAfterZones(Document doc, SlabGeometrySnapshot original, RebarBarType mainType, bool sharePosition)
        {
            using (var operation = new TransactionGroup(doc, "Zones then main perimeter rollback"))
            {
                operation.Start();
                var zoneType = sharePosition ? mainType : LineMaximumWinner(doc, mainType);
                var types = sharePosition ? new List<RebarBarType> { mainType } : new List<RebarBarType> { mainType, zoneType };
                var p = PerimeterSequenceParameters(original, mainType, zoneType);
                var mesh = PerimeterSequenceMesh(original);
                var firstSession = new ReinforcementSession(original, mesh, types);
                var zonePlan = firstSession.CreateContext().Calculate(p, default);
                Require(zonePlan.CanCreate, "Initial zones failed: " + Diagnostics(zonePlan));
                Require(zonePlan.Bars.Any(b => b.Placement.Component == "Zones")
                    && zonePlan.Bars.Any(b => b.Placement.Component == "PerimeterU"), "Fixture needs actual zones with local U endings.");
                Require(zonePlan.Bars.All(b => b.Placement.TypeKey == zoneType.UniqueId), "Initial zones contain a main background type.");
                RebarPlanWriter.Create(doc, original, firstSession, zonePlan, p);

                // Equivalent to closing the form and reopening the command: reread the
                // host and every actual position, then construct a new Session snapshot.
                var slab = RefreshSequenceSlab(doc, original);
                var firstNative = ScopeNativeElements(doc, slab);
                var firstIds = new HashSet<ElementId>(firstNative.Select(e => e.Id));
                var firstPhysical = ExistingRebarReader.Read(slab);
                Require(firstPhysical.Count == zonePlan.Bars.Count && ExistingReinforcementMatcher.Match(zonePlan.Bars, firstPhysical).IsComplete,
                    "Initial zone write differs from its physical snapshot.");
                var localUIds = new HashSet<ElementId>(firstNative.OfType<Rebar>().Select(e => e.Id));
                var actualLocalU = ExistingRebarReader.Read(slab, localUIds.Contains);
                Require(actualLocalU.Count > 0, "No local U positions were read from Revit.");

                p.CreationScope = ReinforcementCreationScope.Background;
                var nextSession = new ReinforcementSession(slab, mesh, types);
                var mainPlan = nextSession.CreateContext().Calculate(p, default);
                Require(mainPlan.CanCreate, "Existing local U blocked background/main perimeter: " + Diagnostics(mainPlan));
                Require(!mainPlan.Diagnostics.Any(d => d.Code == "PERIMETER_EXISTING_MISMATCH" || d.Code == "PERIMETER_PARTIAL_MATCH"),
                    "Local U was classified as an invalid complete main perimeter.");
                Require(mainPlan.Bars.Any(b => b.Placement.Component == "Background")
                    && mainPlan.Bars.Any(b => b.Placement.Component == "PerimeterU"), "Main operation must add background and main perimeter.");
                Require(mainPlan.Bars.All(b => b.Placement.TypeKey == mainType.UniqueId && b.Placement.Component != "Zones"),
                    "Main operation writes a zone or wrong type.");
                Require(ExistingReinforcementMatcher.Match(mainPlan.Bars, firstPhysical).MatchedCount == 0,
                    "Main operation plans to write a physical bar that already exists.");
                var referencedU = mainPlan.ReferenceBars.Where(b => b.Placement?.Component == "PerimeterU").ToList();
                int reusedLocal = ExistingReinforcementMatcher.Match(referencedU, actualLocalU).MatchedCount;
                if (sharePosition) Require(reusedLocal > 0, "Exact local/main U coincidence was not reused as an existing reference.");
                else Require(reusedLocal == 0, "Different-diameter local U was credited as main perimeter.");

                int mainGroups = RebarPlanWriter.Create(doc, slab, nextSession, mainPlan, p);
                var after = ScopeNativeElements(doc, slab);
                var afterIds = new HashSet<ElementId>(after.Select(e => e.Id));
                var added = after.Where(e => !firstIds.Contains(e.Id)).ToList();
                var actualNew = ExistingRebarReader.Read(slab, id => !firstIds.Contains(id));
                var actualAll = ExistingRebarReader.Read(slab);
                Require(firstIds.IsSubsetOf(afterIds), "Creating background removed an existing zone or local U element.");
                Require(actualAll.Count == firstPhysical.Count + mainPlan.Bars.Count && actualNew.Count == mainPlan.Bars.Count,
                    "Main creation count includes missing or duplicated physical positions.");
                Require(ExistingReinforcementMatcher.Match(firstPhysical, actualAll).IsComplete,
                    "Creating background/main perimeter changed the original zone or local U geometry/type.");
                Require(ExistingReinforcementMatcher.Match(mainPlan.Bars, actualNew).IsComplete,
                    "Main native output does not match calculated groups and positions.");
                Require(actualNew.All(b => b.Placement.TypeKey == mainType.UniqueId), "Main creation wrote the zone's different type.");
                Require(!added.OfType<PathReinforcement>().Any() && !added.OfType<RebarInSystem>().Any(), "Main creation added duplicate native zone systems.");
                RequireNoSequenceDuplicates(actualAll);
                var repeat = VerifyRepeatedMainPerimeter(doc, slab, mesh, types, p, sharePosition ? "shared" : "different-type");
                var result = new
                {
                    sharePosition, zoneType = zoneType.Name, mainType = mainType.Name,
                    originalZonesAndLocalU = firstPhysical.Count, originalLocalU = actualLocalU.Count,
                    reusedLocalU = reusedLocal, mainNewPhysical = actualNew.Count, mainGroups,
                    finalPhysical = actualAll.Count, nativeAdded = ScopeNativeCounts(added), physicalByType = PhysicalTypeCounts(actualAll),
                    originalIdsPreserved = true, originalGeometryPreserved = true, noPhysicalDuplicates = true, repeat
                };
                operation.RollBack();
                return result;
            }
        }

        private object TestZonesAfterMainPerimeter(Document doc, SlabGeometrySnapshot original, RebarBarType type)
        {
            using (var operation = new TransactionGroup(doc, "Main perimeter then zones rollback"))
            {
                operation.Start();
                var types = new List<RebarBarType> { type };
                var mesh = PerimeterSequenceMesh(original);
                var p = PerimeterSequenceParameters(original, type, type);
                p.CreationScope = ReinforcementCreationScope.Background;
                var mainSession = new ReinforcementSession(original, mesh, types);
                var main = mainSession.CreateContext().Calculate(p, default);
                Require(main.CanCreate, "Initial background/main perimeter failed: " + Diagnostics(main));
                RebarPlanWriter.Create(doc, original, mainSession, main, p);
                var slab = RefreshSequenceSlab(doc, original);
                var before = ScopeNativeElements(doc, slab);
                var beforeIds = new HashSet<ElementId>(before.Select(e => e.Id));
                var actualBefore = ExistingRebarReader.Read(slab);

                p.CreationScope = ReinforcementCreationScope.Zones;
                var zonesSession = new ReinforcementSession(slab, mesh, types);
                var zones = zonesSession.CreateContext().Calculate(p, default);
                Require(zones.CanCreate, "Existing main perimeter blocked zones: " + Diagnostics(zones));
                Require(zones.Bars.Any(b => b.Placement.Component == "Zones")
                    && zones.ReferenceBars.Any(b => b.Placement.Component == "Background"), "Zones do not reference the actual existing background.");
                Require(zones.Bars.All(b => b.Placement.Component != "Background"), "Zone operation plans a repeated background.");
                Require(ExistingReinforcementMatcher.Match(zones.Bars, actualBefore).MatchedCount == 0, "Zone operation plans a duplicate existing bar.");
                int created = RebarPlanWriter.Create(doc, slab, zonesSession, zones, p);
                var after = ScopeNativeElements(doc, slab);
                var actualAfter = ExistingRebarReader.Read(slab);
                var actualNew = ExistingRebarReader.Read(slab, id => !beforeIds.Contains(id));
                Require(beforeIds.IsSubsetOf(new HashSet<ElementId>(after.Select(e => e.Id))), "Zone creation removed existing main reinforcement.");
                Require(actualAfter.Count == actualBefore.Count + zones.Bars.Count && actualNew.Count == zones.Bars.Count,
                    "Zone creation wrote extra or missing positions after main perimeter.");
                Require(ExistingReinforcementMatcher.Match(actualBefore, actualAfter).IsComplete
                    && ExistingReinforcementMatcher.Match(zones.Bars, actualNew).IsComplete, "Sequential zone readback differs or existing main bars changed.");
                RequireNoSequenceDuplicates(actualAfter);
                p.CreationScope = ReinforcementCreationScope.Background;
                var repeat = VerifyRepeatedMainPerimeter(doc, slab, mesh, types, p, "reverse");
                var result = new
                {
                    initialMainPhysical = actualBefore.Count, newZoneAndLocalUPhysical = actualNew.Count, finalPhysical = actualAfter.Count,
                    createdGroups = created, referenceBars = zones.ReferenceBars.Count, physicalByType = PhysicalTypeCounts(actualAfter),
                    originalGeometryPreserved = true, noPhysicalDuplicates = true, repeat
                };
                operation.RollBack();
                return result;
            }
        }

        private object VerifyRepeatedMainPerimeter(Document doc, SlabGeometrySnapshot previous, ImportedMeshSnapshot mesh,
            List<RebarBarType> types, PreviewParameters parameters, string label)
        {
            var slab = RefreshSequenceSlab(doc, previous);
            var idsBefore = ScopeDocumentIds(doc);
            var physicalBefore = ExistingRebarReader.Read(slab);
            var session = new ReinforcementSession(slab, mesh, types);
            var plan = session.CreateContext().Calculate(parameters, default);
            var snapshot = new
            {
                slab.Slabs, slab.ThicknessMm, slab.TopCoverMm, slab.BottomCoverMm, slab.SideCoverMm,
                parameters = new
                {
                    parameters.CreationScope, parameters.Direction, parameters.ConcreteClass, parameters.EdgeCoverMm,
                    requestSideCoverMm = parameters.EdgeCoverMm + 0.05, parameters.AutomaticULegLength,
                    parameters.BackgroundLayers, perimeter = new { parameters.Perimeter.SpecKey, parameters.Perimeter.SpacingMm,
                        parameters.Perimeter.Enabled, parameters.Perimeter.Outer, parameters.Perimeter.Openings }
                },
                types = types.Select(t => new { t.UniqueId, t.Name, nominalDiameterMm = t.BarNominalDiameter * 304.8,
                    modelDiameterMm = t.BarModelDiameter * 304.8, bendDiameterMm = t.StandardBendDiameter * 304.8,
                    steelGrade = LineSteelGrade(t) }).ToList(),
                existingBars = physicalBefore, referenceBars = plan.ReferenceBars, diagnostics = plan.Diagnostics,
                exactBackground = plan.ReferenceBars.Where(b => b.Placement?.Component == "Background").Select(b => new
                {
                    b.SpecKey, x0 = b.Start.X.ToString("R", CultureInfo.InvariantCulture), y0 = b.Start.Y.ToString("R", CultureInfo.InvariantCulture),
                    x1 = b.End.X.ToString("R", CultureInfo.InvariantCulture), y1 = b.End.Y.ToString("R", CultureInfo.InvariantCulture),
                    z = b.Placement.ElevationMm.ToString("R", CultureInfo.InvariantCulture),
                    modelDiameter = b.Placement.ModelDiameterMm.ToString("R", CultureInfo.InvariantCulture)
                }).ToList()
            };
            File.WriteAllText(Path.Combine(_run, "perimeter-repeat-" + label + ".json"),
                new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(snapshot));
            Require(plan.Bars.Count == 0 && plan.Groups.Count == 0 && !plan.CanCreate,
                "Repeat main perimeter operation still plans new bars: " + Diagnostics(plan));
            Require(plan.Diagnostics.Any(d => d.Code == "PERIMETER_REUSED"),
                "Repeat main perimeter was not recognized from current physical rows: " + Diagnostics(plan));
            Require(plan.Diagnostics.Where(d => d.IsBlocking).All(d => d.Code == "EMPTY_PLAN"), "Repeat main perimeter failed instead of recognizing existing reinforcement.");
            bool rejected = false;
            try { RebarPlanWriter.Create(doc, slab, session, plan, parameters); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "Writer accepted an empty repeat operation.");
            var physicalAfter = ExistingRebarReader.Read(slab);
            Require(idsBefore.SetEquals(ScopeDocumentIds(doc)) && physicalBefore.Count == physicalAfter.Count
                && ExistingReinforcementMatcher.Match(physicalBefore, physicalAfter).IsComplete, "Repeat main operation changed the model.");
            return new { newBars = plan.Bars.Count, references = plan.ReferenceBars.Count, writerRejectedEmpty = rejected, unchangedPhysical = physicalAfter.Count };
        }

        private static SlabGeometrySnapshot RefreshSequenceSlab(Document doc, SlabGeometrySnapshot previous) =>
            SlabGeometryReader.Read(doc, previous.Host, FindEdge(previous.Host));

        private static void RequireNoSequenceDuplicates(List<PlannedBar> physical)
        {
            var diagnostics = CompositePlanValidator.Validate(physical, allowBarIntersections: true);
            Require(!diagnostics.Any(d => d.IsBlocking), "Sequential creation left invalid or duplicate physical bars: "
                + string.Join("; ", diagnostics.Select(d => d.Code + ": " + d.Message)));
        }

        private static PreviewParameters PerimeterSequenceParameters(SlabGeometrySnapshot slab, RebarBarType mainType, RebarBarType zoneType)
        {
            var p = ScopeParameters(slab, mainType, zoneType);
            p.CreationScope = ReinforcementCreationScope.Zones;
            p.Selections[0].SpacingMm = 300;
            return p;
        }

        private static ImportedMeshSnapshot PerimeterSequenceMesh(SlabGeometrySnapshot slab)
        {
            var points = slab.Slabs.SelectMany(s => s.Shell).ToList();
            double maxX = points.Max(p => p.X), centerY = (points.Min(p => p.Y) + points.Max(p => p.Y)) / 2;
            var mesh = new ImportedMeshSnapshot();
            mesh.Groups.Add(new CadPlaneGroup { Key = "map", Faces = new List<FaceInput> { new FaceInput
            {
                Id = "sequential-zone-edge", ColorKey = "r",
                Vertices = Rect(maxX - 2200, centerY - 300, maxX - 5, centerY + 300)
            } } });
            return mesh;
        }
    }
}
