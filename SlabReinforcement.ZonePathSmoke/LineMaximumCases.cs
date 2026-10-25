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
        private void RunLineMaximumCases(Document doc, SlabGeometrySnapshot slab, RebarBarType smaller)
        {
            foreach (string direction in new[] { "Низ X", "Низ Y", "Верх X", "Верх Y" })
            {
                string captured = direction;
                Case("line-maximum-diameter-native-" + direction, () => TestLineMaximum(doc, slab, smaller, captured));
            }
            Case("line-maximum-background-scope-unchanged", () => TestLineMaximumBackground(doc, slab, smaller));
        }

        private object TestLineMaximum(Document doc, SlabGeometrySnapshot slab, RebarBarType smaller, string direction)
        {
            using (var operation = new TransactionGroup(doc, "Mixed diameter line rollback"))
            {
                operation.Start();
                var winner = LineMaximumWinner(doc, smaller);
                bool alongX = direction.EndsWith("X", StringComparison.Ordinal);
                bool top = direction.StartsWith("Верх", StringComparison.Ordinal);
                var mesh = LineMaximumMesh(slab, alongX, out double lineStart, out double lineEnd);
                var p = LineMaximumParameters(slab, smaller, winner, direction);
                var session = new ReinforcementSession(slab, mesh, new List<RebarBarType> { smaller, winner });
                var plan = session.CreateContext().Calculate(p, default);
                Require(plan.CanCreate, "Mixed diameter line calculation failed: " + Diagnostics(plan));
                var zones = plan.Bars.Where(b => b.Placement?.Component == "Zones").ToList();
                var endings = plan.Bars.Where(b => b.Placement?.Component == "PerimeterU").ToList();
                Require(zones.Count >= 2 && endings.Count > 0, "The fixture needs a zone and automatic U at the clipped end.");
                Require(plan.Bars.Count == zones.Count + endings.Count, "Inactive background was included in the Zones operation.");
                Require(plan.Bars.All(b => b.Placement.TypeKey == winner.UniqueId), "A smaller-diameter color survived the common line selection.");
                Require(plan.Groups.Where(g => g.Placement.Component == "Zones").All(g => Math.Abs(g.SpacingMm - 300) < 1e-6),
                    "The common line did not keep the selected 16 mm type's own 300 mm pitch.");
                Require(plan.ReferenceBars.Count == 0, "Inactive background became fictitious existing reinforcement.");
                double modelDiameter = winner.BarModelDiameter * 304.8;
                double expectedZ = BarElevation.Calculate(top, slab.ThicknessMm,
                    top ? slab.TopCoverMm : slab.BottomCoverMm, top ? slab.BottomCoverMm : slab.TopCoverMm,
                    modelDiameter, alongX ? 0 : smaller.BarModelDiameter * 304.8);
                Require(zones.All(b => Math.Abs(b.Placement.ElevationMm - expectedZ) < 1e-6), "Zone elevation does not use the winning physical diameter.");
                double steel = LineSteelGrade(winner);
                double anchor = LegacyAnchorageTable.GetLength(16, "B25", steel);
                var uLength = ULegLengthCalculator.Calculate(16, modelDiameter, winner.StandardBendDiameter * 304.8, "B25", steel);
                foreach (var bar in zones)
                {
                    double start = alongX ? Math.Min(bar.Start.X, bar.End.X) : Math.Min(bar.Start.Y, bar.End.Y);
                    double end = alongX ? Math.Max(bar.Start.X, bar.End.X) : Math.Max(bar.Start.Y, bar.End.Y);
                    Require(start <= lineStart - anchor + 0.1, "Free line end lacks anchorage for the winning 16 mm diameter.");
                    Require(end > lineEnd - 100, "The winning line was split at the source-color boundary.");
                    Require(bar.EndConnections.Count == 1 && !bar.EndConnections[0].AtStart, "Expected only the far clipped end to use a U detail.");
                    Require(bar.EndConnections[0].OverlapMm + 0.1 >= uLength.LapMm, "Local U overlap was calculated from the smaller type.");
                }
                Require(endings.Count == zones.Count, "Local U count does not follow the common line's actual rows.");
                CheckLineStations(zones, alongX, 300);

                var before = ScopeNativeElements(doc, slab);
                var beforeIds = new HashSet<ElementId>(before.Select(e => e.Id));
                int created = RebarPlanWriter.Create(doc, slab, session, plan, p);
                var added = ScopeNativeElements(doc, slab).Where(e => !beforeIds.Contains(e.Id)).ToList();
                var actual = ExistingRebarReader.Read(slab, id => !beforeIds.Contains(id));
                Require(actual.Count == plan.Bars.Count && ExistingReinforcementMatcher.Match(plan.Bars, actual).IsComplete,
                    "Actual mixed-line centers, types, U details or elevations differ from the calculated plan.");
                Require(actual.All(b => b.Placement.TypeKey == winner.UniqueId), "Native creation wrote a 12 mm zone or local U.");
                var paths = added.OfType<PathReinforcement>().ToList();
                var children = added.OfType<RebarInSystem>().ToList();
                var direct = added.OfType<Rebar>().ToList();
                Require(paths.Count == plan.Groups.Count(g => g.Placement.Component == "Zones"), "Native zone Path count differs.");
                Require(children.Count > 0 && children.All(c => c.GetTypeId() == winner.Id), "Path children do not all use the 16 mm winner.");
                Require(paths.All(path => Math.Abs(path.get_Parameter(BuiltInParameter.PATH_REIN_SPACING).AsDouble() * 304.8 - 300) < 0.1),
                    "Native Path pitch is not the winner's 300 mm pitch.");
                Require(direct.Count == plan.Groups.Count(g => g.Placement.Component == "PerimeterU")
                    && direct.All(b => b.GetTypeId() == winner.Id && Partition(b).Contains("П-обрамление")), "Unexpected independent bars or wrong local U type.");
                Require(added.OfType<AreaReinforcement>().Count() == 0 && PhysicalCountByPartition(doc, slab, added, " фон") == 0,
                    "Zones operation wrote unrelated background reinforcement.");
                var childIds = new HashSet<ElementId>(children.Select(c => c.Id));
                var nativeZones = ExistingRebarReader.Read(slab, childIds.Contains);
                Require(nativeZones.Count == zones.Count && ExistingReinforcementMatcher.Match(zones, nativeZones).IsComplete,
                    "Native Path bars differ from the common-line plan.");
                CheckLineStations(nativeZones, alongX, 300);
                var result = new
                {
                    direction, smallerType = smaller.Name, winningType = winner.Name, selectedNominalDiameterMm = 16,
                    selectedSpacingMm = 300, zonePhysical = zones.Count, localUPhysical = endings.Count,
                    anchorMm = anchor, requiredLapMm = uLength.LapMm, expectedElevationMm = expectedZ,
                    createdGroups = created, native = ScopeNativeCounts(added), physicalByType = PhysicalTypeCounts(actual),
                    physicalMatched = true, smallerZoneChunks = 0, backgroundPhysical = 0
                };
                operation.RollBack();
                return result;
            }
        }

        private object TestLineMaximumBackground(Document doc, SlabGeometrySnapshot slab, RebarBarType smaller)
        {
            using (var operation = new TransactionGroup(doc, "Mixed colors ignored by background rollback"))
            {
                operation.Start();
                var winner = LineMaximumWinner(doc, smaller);
                var mesh = LineMaximumMesh(slab, true, out _, out _);
                var types = new List<RebarBarType> { smaller, winner };
                var p = LineMaximumParameters(slab, smaller, winner, "Низ X");
                p.CreationScope = ReinforcementCreationScope.Background;
                var mixedSession = new ReinforcementSession(slab, mesh, types);
                var mixed = mixedSession.CreateContext().Calculate(p, default);
                var controlParameters = LineMaximumParameters(slab, smaller, winner, "Низ X");
                controlParameters.CreationScope = ReinforcementCreationScope.Background;
                controlParameters.Selections.Clear();
                var control = new ReinforcementSession(slab, new ImportedMeshSnapshot(), types).CreateContext().Calculate(controlParameters, default);
                Require(mixed.CanCreate && control.CanCreate, "Background control failed: " + Diagnostics(mixed) + "; " + Diagnostics(control));
                // Planned rows share a layer SpecKey; the readback matcher deliberately
                // rejects duplicate actual identities. Give this plan-to-plan comparison
                // unique fixture identities without changing either plan's geometry.
                var mixedPhysical = mixed.Bars.Select((bar, index) => new PlannedBar
                {
                    SpecKey = "background-control-position-" + index, Start = bar.Start, End = bar.End,
                    Placement = bar.Placement, Centerline = bar.Centerline
                }).ToList();
                Require(mixed.Bars.Count == control.Bars.Count && mixed.Groups.Count == control.Groups.Count
                    && ExistingReinforcementMatcher.Match(control.Bars, mixedPhysical).IsComplete,
                    "Mixed zone colors changed the background plan: mixed " + mixed.Bars.Count + "/" + mixed.Groups.Count
                    + ", control " + control.Bars.Count + "/" + control.Groups.Count + " bars/groups.");
                RebarPlanWriter.Create(doc, slab, mixedSession, mixed, p);
                var native = ScopeNativeElements(doc, slab);
                var actual = ExistingRebarReader.Read(slab);
                Require(actual.Count == control.Bars.Count && ExistingReinforcementMatcher.Match(control.Bars, actual).IsComplete,
                    "Background writer output differs from the control without zone colors.");
                Require(actual.All(b => b.Placement.TypeKey == smaller.UniqueId), "Background creation adopted the inactive 16 mm zone type.");
                Require(!native.OfType<PathReinforcement>().Any() && !native.OfType<RebarInSystem>().Any(), "Background scope created an inactive mixed-color Path.");
                var result = new { physical = actual.Count, native = ScopeNativeCounts(native), inactiveColors = 2, sameAsWithoutZoneMap = true };
                operation.RollBack();
                return result;
            }
        }

        private static RebarBarType LineMaximumWinner(Document doc, RebarBarType smaller)
        {
            Require(Math.Abs(smaller.BarNominalDiameter * 304.8 - 12) < 1e-6, "Expected a 12 mm source fixture type.");
            double steel = LineSteelGrade(smaller);
            var winner = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>()
                .Where(t => Math.Abs(t.BarNominalDiameter * 304.8 - 16) < 1e-6 && Math.Abs(LineSteelGrade(t) - steel) < 1e-6)
                .OrderBy(t => t.Name.Contains("пог") ? 1 : 0).ThenBy(t => t.Name, StringComparer.Ordinal).FirstOrDefault();
            Require(winner != null, "The disposable baseline has no 16 mm type with the same steel grade as the 12 mm fixture.");
            return winner;
        }

        private static double LineSteelGrade(RebarBarType type) =>
            type.get_Parameter(new Guid("32a47c7f-e91d-4a8e-bf24-927cb679b4d1"))?.AsDouble() ?? double.NaN;

        private static PreviewParameters LineMaximumParameters(SlabGeometrySnapshot slab, RebarBarType smaller, RebarBarType winner, string direction)
        {
            var p = ScopeParameters(slab, smaller, winner);
            p.Direction = direction;
            p.CreationScope = ReinforcementCreationScope.Zones;
            p.Selections = new List<PreviewSelection>
            {
                new PreviewSelection { ColorKey = "small", SpecKey = smaller.UniqueId, SpacingMm = 100 },
                new PreviewSelection { ColorKey = "large", SpecKey = winner.UniqueId, SpacingMm = 300 }
            };
            return p;
        }

        private static ImportedMeshSnapshot LineMaximumMesh(SlabGeometrySnapshot slab, bool alongX, out double start, out double end)
        {
            var points = slab.Slabs.SelectMany(s => s.Shell).ToList();
            double maxPrimary = alongX ? points.Max(p => p.X) : points.Max(p => p.Y);
            double minCross = alongX ? points.Min(p => p.Y) : points.Min(p => p.X);
            start = maxPrimary - 3000;
            double seam = maxPrimary - 1500;
            end = maxPrimary - 5;
            var smaller = alongX ? Rect(start, minCross + 1200, seam, minCross + 2400) : Rect(minCross + 1200, start, minCross + 2400, seam);
            var larger = alongX ? Rect(seam, minCross + 1200, end, minCross + 2400) : Rect(minCross + 1200, seam, minCross + 2400, end);
            var mesh = new ImportedMeshSnapshot();
            mesh.Groups.Add(new CadPlaneGroup { Key = "map", Faces = new List<FaceInput>
            {
                new FaceInput { Id = "line-small-12-at-100", ColorKey = "small", Vertices = smaller },
                new FaceInput { Id = "line-large-16-at-300", ColorKey = "large", Vertices = larger }
            } });
            return mesh;
        }

        private static void CheckLineStations(IReadOnlyList<PlannedBar> bars, bool alongX, double spacing)
        {
            var stations = bars.Select(b => alongX ? b.Start.Y : b.Start.X).OrderBy(v => v).ToList();
            for (int i = 1; i < stations.Count; ++i)
                Require(Math.Abs(stations[i] - stations[i - 1] - spacing) < 0.1, "Actual line rows do not keep the winning type's pitch.");
        }
    }
}
