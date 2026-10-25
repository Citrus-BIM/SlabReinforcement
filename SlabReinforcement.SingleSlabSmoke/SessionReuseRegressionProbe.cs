using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using NetTopologySuite.Geometries;
using SlabReinforcement.Core;
using SlabReinforcement.Integration;

namespace SlabReinforcement.SingleSlabSmoke
{
    public sealed partial class SingleSlabApplication
    {
        // These fixtures use the real host and a deliberately synthetic interior demand patch.
        // They test Session bookkeeping, not the source-map legend or structural adequacy.
        // The document is never saved; each fixture also rolls back its complete transaction group.
        private void RunSessionReuseRegressions(Autodesk.Revit.ApplicationServices.Application application,
            int hostId, string stableEdge)
        {
            Document doc = null;
            try
            {
                doc = application.OpenDocumentFile(Path.Combine(_run, "single-slab-input.rvt"));
                RequireSessionRegression(RebarCount(doc) == 0, "Session regressions require the empty baseline.");
                var slab = SlabGeometryReader.Read(doc, (Floor)doc.GetElement(new ElementId(hostId)),
                    Reference.ParseFromStableRepresentation(doc, stableEdge));
                ValidateFootprint(slab, "session-regression-input");
                var types = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToList();
                var type = types.Where(t => Math.Abs(t.BarNominalDiameter * 304.8 - 12) < 0.01 && Grade(t) == 500)
                    .OrderBy(t => t.Name, StringComparer.Ordinal).First();
                var mesh = SessionRegressionInteriorMesh(slab);
                _report["sessionRegressionDemand"] = new
                {
                    purpose = "SYNTHETIC INTERIOR PATCH FOR SESSION REUSE REGRESSIONS ONLY",
                    source = mesh.Groups.Single().Key,
                    vertices = mesh.Groups.Single().Faces.Single().Vertices,
                    originalDxfUnchanged = true,
                    modelSaved = false
                };
                RunSessionRegressionFixture(doc, "session-single-y-fixture", () =>
                    CheckSingleYSessionReuse(doc, slab, mesh, types, type));
                RunSessionRegressionFixture(doc, "session-u-reference-fixture", () =>
                    CheckUReferenceWithoutEdgePermission(doc, slab, mesh, types, type));
                RequireSessionRegression(RebarCount(doc) == 0, "Regression rollback left physical reinforcement behind.");
            }
            catch (Exception error)
            {
                Add("session-reuse-regressions", "FAILED", new { message = error.ToString() });
            }
            finally
            {
                if (doc != null && doc.IsValidObject) doc.Close(false);
            }
        }

        private void RunSessionRegressionFixture(Document doc, string name, Action action)
        {
            RequireSessionRegression(RebarCount(doc) == 0, "A prior fixture was not rolled back.");
            using (var scope = new TransactionGroup(doc, name))
            {
                scope.Start();
                try { action(); }
                catch (Exception error) { Add(name, "FAILED", new { message = error.ToString() }); }
                finally { scope.RollBack(); }
            }
            RequireSessionRegression(RebarCount(doc) == 0, "Fixture rollback did not restore the empty baseline.");
        }

        private void CheckSingleYSessionReuse(Document doc, SlabGeometrySnapshot slab, ImportedMeshSnapshot mesh,
            List<RebarBarType> types, RebarBarType type)
        {
            var initial = BackgroundParameters(slab, type, "background");
            foreach (var layer in initial.BackgroundLayers) layer.Enabled = layer.Direction == "Низ Y";
            var firstSession = new ReinforcementSession(slab, new ImportedMeshSnapshot(), types);
            var first = firstSession.CreateContext().Calculate(initial, CancellationToken.None);
            RequireSessionRegression(first.CanCreate && first.Bars.All(b => b.SpecKey == "Background|Низ Y"),
                "Single-Y fixture did not produce exactly its requested background layer.");
            RebarPlanWriter.Create(doc, slab, firstSession, first, initial);
            var firstComparison = CompareFullCentrelines(first.Bars, ExistingRebarReader.Read(slab));
            double actualY = first.Bars[0].Placement.ElevationMm;
            var beforeIds = RebarUniqueIds(doc);

            // All disabled rows still have valid types and spacing, as in the WPF capture.
            // Previously disabled X shifted the expected Y plane and caused a second Y to be written.
            var repeat = new ReinforcementSession(slab, mesh, types).CreateContext().Calculate(initial, CancellationToken.None);
            RequireSessionRegression(repeat.Bars.Count == 0 && repeat.Groups.Count == 0,
                "Repeating an existing single-Y layer proposed duplicate physical bars.");
            RequireSessionRegression(repeat.ReferenceBars.Count == first.Bars.Count
                && repeat.ReferenceBars.All(b => Math.Abs(b.Placement.ElevationMm - actualY) <= 1e-6),
                "Disabled X changed the elevation or count of the reused Y reference.");
            RequireSessionRegression(repeat.Diagnostics.Any(d => d.Code == "BACKGROUND_REUSED" && !d.IsBlocking)
                && repeat.Diagnostics.Where(d => d.IsBlocking).All(d => d.Code == "EMPTY_PLAN"),
                "Pure reuse should be an empty write plan, without a mismatch/collision failure.");
            var repeatComparison = CompareFullCentrelines(first.Bars, repeat.ReferenceBars);
            Add("session-single-y-repeat-no-duplicate", "PASS", new
            {
                expectedY = actualY,
                repeatedNewBars = repeat.Bars.Count,
                references = repeat.ReferenceBars.Count,
                firstComparison,
                repeatComparison,
                repeat.Diagnostics
            });

            var zones = SessionRegressionZoneParameters(slab, type, mesh, "Низ Y", false);
            var zonePlan = new ReinforcementSession(slab, mesh, types).CreateContext().Calculate(zones, CancellationToken.None);
            RequireSessionRegression(zonePlan.CanCreate && zonePlan.Bars.All(b => b.Placement.Component == "Zones"
                && Math.Abs(b.Placement.ElevationMm - actualY) <= 1e-6),
                "Zones did not retain the actual single-Y background plane.");
            Add("session-single-y-zones-preserve-elevation", "PASS", new
            {
                expectedY = actualY,
                zoneBars = zonePlan.Bars.Count,
                elevations = zonePlan.Bars.Select(b => b.Placement.ElevationMm).Distinct().ToList(),
                zonePlan.Diagnostics
            });

            var addX = BackgroundParameters(slab, type, "background");
            foreach (var layer in addX.BackgroundLayers) layer.Enabled = layer.Direction == "Низ X";
            var stack = new ReinforcementSession(slab, mesh, types).CreateContext().Calculate(addX, CancellationToken.None);
            RequireSessionRegression(!stack.CanCreate && stack.Diagnostics.Any(d => d.Code == "BACKGROUND_STACK_MISMATCH" && d.IsBlocking),
                "Adding outer X to an existing outer Y did not block the incompatible stack.");
            RequireSessionRegression(beforeIds.SetEquals(RebarUniqueIds(doc)), "Read-only Session calculations changed element IDs.");
            Add("session-single-y-add-x-stack-blocked", "PASS", new { stack.Diagnostics, originalElementsUnchanged = true });

            // Same physical diameter/geometry is not permission to silently reuse another type,
            // nor to write a geometrically identical second layer when intersections are allowed.
            RebarBarType alternateType;
            using (var transaction = new Transaction(doc, "Temporary alternate rebar type for Session regression"))
            {
                transaction.Start();
                alternateType = (RebarBarType)type.Duplicate(type.Name + " — Session regression " + Guid.NewGuid().ToString("N"));
                transaction.Commit();
            }
            var alternateTypes = types.Concat(new[] { alternateType }).ToList();
            var differentType = BackgroundParameters(slab, type, "background");
            foreach (var layer in differentType.BackgroundLayers)
            {
                layer.Enabled = layer.Direction == "Низ Y";
                if (layer.Enabled) layer.SpecKey = alternateType.UniqueId;
            }
            var mismatch = new ReinforcementSession(slab, mesh, alternateTypes).CreateContext()
                .Calculate(differentType, CancellationToken.None);
            RequireSessionRegression(!mismatch.CanCreate && mismatch.Diagnostics.Any(d => d.Code == "BACKGROUND_EXISTING_MISMATCH" && d.IsBlocking),
                "A different type with identical physical geometry allowed a duplicate Y background.");
            RequireSessionRegression(beforeIds.SetEquals(RebarUniqueIds(doc)), "Type-mismatch validation changed physical reinforcement.");
            Add("session-single-y-same-diameter-different-type-blocked", "PASS", new
            {
                originalType = type.UniqueId,
                alternateType = alternateType.UniqueId,
                modelDiameterMm = alternateType.BarModelDiameter * 304.8,
                differentType.AllowBarIntersections,
                mismatch.Diagnostics,
                originalElementsUnchanged = true
            });
        }

        private void CheckUReferenceWithoutEdgePermission(Document doc, SlabGeometrySnapshot slab, ImportedMeshSnapshot mesh,
            List<RebarBarType> types, RebarBarType type)
        {
            var initial = BackgroundParameters(slab, type, "background-u");
            var firstSession = new ReinforcementSession(slab, new ImportedMeshSnapshot(), types);
            var first = firstSession.CreateContext().Calculate(initial, CancellationToken.None);
            RequireSessionRegression(first.CanCreate && first.Bars.Any(b => b.Placement.Component == "PerimeterU"),
                "Common background/U fixture was not createable.");
            RebarPlanWriter.Create(doc, slab, firstSession, first, initial);
            CompareFullCentrelines(first.Bars, ExistingRebarReader.Read(slab));
            var originalIds = RebarUniqueIds(doc);
            double expectedZ = first.Bars.First(b => b.SpecKey == "Background|Низ X").Placement.ElevationMm;
            var parameters = SessionRegressionZoneParameters(slab, type, mesh, "Низ X", false);
            var session = new ReinforcementSession(slab, mesh, types);
            var plan = session.CreateContext().Calculate(parameters, CancellationToken.None);
            RequireSessionRegression(plan.CanCreate && plan.Bars.All(b => b.Placement.Component == "Zones"
                && Math.Abs(b.Placement.ElevationMm - expectedZ) <= 1e-6 && b.EndConnections.Count == 0),
                "Disabling edge endings changed the existing background plane or proposed common-component duplicates.");
            RequireSessionRegression(plan.Diagnostics.Any(d => d.Code == "PERIMETER_REUSED" && !d.IsBlocking)
                && plan.Diagnostics.Any(d => d.Code == "BACKGROUND_REUSED" && !d.IsBlocking),
                "Common U/background were not referenced with both create and edge-ending permissions off.");
            var referenceComparison = CompareFullCentrelines(first.Bars, plan.ReferenceBars);

            var permissionOn = SessionRegressionZoneParameters(slab, type, mesh, "Низ X", true);
            var withPermission = session.CreateContext().Calculate(permissionOn, CancellationToken.None);
            RequireSessionRegression(withPermission.CanCreate, "Interior comparison plan with U permission was blocked.");
            var toggleComparison = CompareFullCentrelines(plan.Bars, withPermission.Bars);
            int sets = RebarPlanWriter.Create(doc, slab, session, plan, parameters);
            var addedIds = RebarUniqueIds(doc);
            addedIds.ExceptWith(originalIds);
            RequireSessionRegression(addedIds.Count == sets && sets == plan.Groups.Count,
                "Interior zone write created an unexpected number of elements.");
            var originalComparison = CompareFullCentrelines(first.Bars,
                ExistingRebarReader.Read(slab, id => originalIds.Contains(doc.GetElement(id).UniqueId)));
            var addedComparison = CompareFullCentrelines(plan.Bars,
                ExistingRebarReader.Read(slab, id => addedIds.Contains(doc.GetElement(id).UniqueId)));
            Add("session-existing-u-internal-zones-permission-off", "PASS", new
            {
                expectedZ,
                actualZoneElevations = plan.Bars.Select(b => b.Placement.ElevationMm).Distinct().ToList(),
                newBars = plan.Bars.Count,
                newSets = sets,
                references = plan.ReferenceBars.Count,
                referenceComparison,
                toggleComparison,
                originalComparison,
                addedComparison,
                parameters,
                plan.Diagnostics,
                syntheticInteriorDemand = true,
                saved = false
            });
        }

        private static PreviewParameters SessionRegressionZoneParameters(SlabGeometrySnapshot slab, RebarBarType type,
            ImportedMeshSnapshot mesh, string direction, bool edgePermission)
        {
            var group = mesh.Groups.Single();
            var parameters = CombinedParameters(slab, type, group, group.Faces.Select(f => f.ColorKey).Distinct(), edgePermission);
            parameters.Direction = direction;
            parameters.Perimeter.Enabled = false;
            foreach (var layer in parameters.BackgroundLayers) layer.Enabled = false;
            return parameters;
        }

        private static ImportedMeshSnapshot SessionRegressionInteriorMesh(SlabGeometrySnapshot slab)
        {
            var factory = new GeometryFactory();
            Func<List<Point2>, LinearRing> ring = points => factory.CreateLinearRing(points
                .Concat(new[] { points[0] }).Select(p => new Coordinate(p.X, p.Y)).ToArray());
            var polygons = slab.Slabs.Select(p => (NetTopologySuite.Geometries.Geometry)factory.CreatePolygon(
                ring(p.Shell), p.Holes.Select(ring).ToArray())).ToList();
            var host = polygons.Aggregate((a, b) => a.Union(b));
            var interior = host.Buffer(-2000);
            RequireSessionRegression(!interior.IsEmpty, "Host has no interior patch with room for unchanged straight anchorage.");
            var point = interior.InteriorPoint.Coordinate;
            var centre = new Coordinate(Math.Round(point.X / 200) * 200, Math.Round(point.Y / 200) * 200);
            var vertices = new List<Point2>
            {
                new Point2(centre.X - 500, centre.Y - 500), new Point2(centre.X + 500, centre.Y - 500),
                new Point2(centre.X + 500, centre.Y + 500), new Point2(centre.X - 500, centre.Y + 500)
            };
            var patch = factory.CreatePolygon(ring(vertices));
            RequireSessionRegression(host.Covers(patch.Buffer(1000)), "Synthetic demand patch is not safely internal to the actual host.");
            return new ImportedMeshSnapshot
            {
                Groups = new List<CadPlaneGroup>
                {
                    new CadPlaneGroup
                    {
                        Key = "SessionRegression|SyntheticInterior", Layer = "Synthetic Session regression only",
                        Faces = new List<FaceInput>
                        {
                            new FaceInput { Id = "synthetic-interior-patch", ColorKey = "RGB(7, 77, 177)", Vertices = vertices }
                        }
                    }
                }
            };
        }

        private static void RequireSessionRegression(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
