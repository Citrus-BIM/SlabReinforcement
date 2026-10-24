# NTS slab reinforcement implementation plan

> **For agentic workers:** Use superpowers:subagent-driven-development and test-driven-development. Work is authorized by the user on 2026-10-24; execute in this session.

**Goal:** Build reinforcement from colored DXF faces in a frame selected from a slab edge, preserving openings and rotations, with a shared calculation/preview plan and CITRUS WPF UI.

**Architecture:** A netstandard2.0 Core library owns pure millimetre geometry and NTS 2.6.0; a net8.0 xUnit project verifies it without Autodesk assemblies. The Revit adapter snapshots the floor and CAD geometry, the modal WPF window calculates and previews that snapshot, and the command creates exact straight rebar sets from the accepted plan. Keep existing untracked work; do not use the disconnected legacy ZoneGeometryEngine as the production path.

**Tech Stack:** C#, NetTopologySuite 2.6.0, xUnit, WPF/CitrusTheme, Revit 2019–2027 configurations.

## Shared contracts (namespace SlabReinforcement.Core)

All geometry uses millimetres. Public DTOs use writable properties and List<T> unless stated otherwise.

- Point2(double x,double y): immutable X,Y.
- Point3(double x,double y,double z): immutable X,Y,Z.
- SlabFrame.FromEdge(Point3 start,Point3 end,Point3 normal); Origin,XAxis,YAxis,Normal; ToLocal(Point3), ToWorld(Point3). Reject degenerate/nonfinite inputs. Axes are unit vectors, normal canonicalization stays in adapter.
- AffinePlacement(Point3 origin,Point3 basisX,Point3 basisY,Point3 basisZ).OfPoint(Point3). Explicit source unit scaling is caller-owned; transform must never silently add another scale.
- PolygonInput: List<Point2> Shell, List<List<Point2>> Holes.
- FaceInput: string Id, ColorKey; List<Point2> Vertices.
- ReinforcementSpec: string Key,ColorKey; double DiameterMm,SpacingMm,AnchorageMm.
- PlanOptions: bool AlongX=true; double EdgeCoverMm=25, MergeToleranceMm=50, RoundIncrementMm=10, MinimumLengthMm=2; bool UseCutLengths=false.
- PlanRequest: List<PolygonInput> Slabs; List<FaceInput> Faces; List<ReinforcementSpec> Specifications; PlanOptions Options.
- PlannedBar: Point2 Start,End; string SpecKey,ColorKey.
- BarGroup: Point2 Start,End; int Count; double SpacingMm; string SpecKey,ColorKey. Start/End describe first bar; distribution is +Y for AlongX and +X otherwise.
- PlanDiagnostic: string Code,Message,SourceId; bool IsBlocking.
- ReinforcementPlan: List<PlannedBar> Bars; List<BarGroup> Groups; List<PlanDiagnostic> Diagnostics; List<PolygonInput> DemandRegions; bool CanCreate (bars exist and no blocking diagnostics).
- ReinforcementPlanner.Build(PlanRequest,System.Threading.CancellationToken=default).
- PolygonTopology.FromLoops(IEnumerable<IEnumerable<Point2>>) returns List<PolygonInput>, classifies nested loops by containment, preserves islands; invalid loops are rejected.

## Task 1 — frame and placement

Files: Core/Point2.cs, Point3.cs, SlabFrame.cs, AffinePlacement.cs; Core.Tests/FrameTests.cs.
- [ ] Write failing tests for inverse round trips, independent CAD/slab rotations, reflection, mm/metre scale applied once, elevated/sloped planes, reversed edge and degenerate inputs.
- [ ] Implement orthonormal frame: x = normalize(e - n*dot(e,n)), y = n cross x; local coordinates are dot products about Origin.
- [ ] Run `dotnet test SlabReinforcement.Core.Tests -c Release --filter FullyQualifiedName~FrameTests`.

## Task 2 — polygon and reinforcement planner

Files: Core/PlanModels.cs, PolygonTopology.cs, ReinforcementPlanner.cs; Core.Tests/PlannerTests.cs, PolygonTopologyTests.cs.
- [ ] Start with failing behavioural tests: hole splitting, concave host, nested loops, duplicate/permuted FE faces, rotated/reflected inputs, diagonal opening regression.
- [ ] Form demand regions using NTS union/intersection/difference; retain holes, validate all inputs. Resolve overlaps within one compatible material layer conservatively by provided steel area, with deterministic tie breaking.
- [ ] Offset host by edge cover plus bar radius. Place scanlines on a stable local grid; intersect each demand region and host, preserving disconnected intervals.
- [ ] Extend each demand interval by AnchorageMm, round length upward (or next cut length), fit it inside the same host interval without shortening the required interval; report blocking diagnostics when impossible.
- [ ] Group adjacent equal/safely expanded rows only after verifying every final segment against the working host and demand coverage. Final bars are expanded from Groups so preview equals creation.
- [ ] Verify zero/negative/NaN settings, empty selections, insufficient anchorage, tiny geometry, cancellation, and deterministic outputs.
- [ ] Run `dotnet test SlabReinforcement.Core.Tests -c Release`.

## Task 3 — Revit integration

Files: SlabReinforcementCommand.cs; Integration/SlabGeometryReader.cs, DxfFaceReader.cs, ReinforcementSession.cs, RebarPlanWriter.cs, AnchorageTable.cs; csproj/sln.
- [ ] Select floor, straight edge and CAD import; extract planar top/bottom surfaces, actual loops and floor covers. Reject incompatible geometry with useful messages.
- [ ] Snapshot CAD transform and units on Revit thread; load DXF Face3D data, preserve color/duplicates consistently and use local frame. Reject multiple source elevations instead of silently flattening them. Resolve DXF directly or sibling DXF for DWG with explicit failure if absent.
- [ ] Convert UI selections to pure PlanRequest; preserve validated anchorage table behavior, display missing grade/type errors instead of substituting defaults.
- [ ] Create straight Rebar sets from the accepted Groups; validate host and bar types, transform coordinates back including cover/layer elevation. Commit atomically, rollback on errors. Verify layout direction/count and return actual totals.
- [ ] Build R2019/R2024 with VS MSBuild; R2026/R2027 with dotnet. Do not claim runtime Revit verification without running Revit.

## Task 4 — preview and CITRUS window

Files: SlabReinforcementWPF.xaml(.cs), Preview/ReinforcementPreview.cs, Preview/PreviewContracts.cs, SlabReinforcementSettings.cs.
- [ ] Reuse CitrusTheme, resizeable layout, clear selected edge X/Y directions, colors/types/spacing, cover and rounding controls.
- [ ] Modal dialog uses an adapter-supplied pure calculation callback. Debounce changes, cancel obsolete work, disable creation for stale/failed preview. Never access Revit API on worker thread.
- [ ] Render host/holes, CAD demand and final planned bars with fit, zoom/pan and readable legend. The accepted plan object is the object later written to Revit.
- [ ] Validate and persist user settings atomically in LocalAppData; support safe migration of legacy XML; missing saved types require explicit reselection.
- [ ] Verify XAML/resources and a standalone preview smoke harness when possible.

## Task 5 — integration review and verification

- [ ] Review source for frame/unit mistakes, topology loss, unsafe group expansion, stale preview, transaction failure and Revit version incompatibilities.
- [ ] Fix issues with regression tests, run appropriate builds and tests once final edits are complete.
- [ ] Update README with real commands, new workflow, assumptions and manual Revit acceptance cases.

No deployment or changes to live Revit models are part of this implementation session. Existing table values are preserved and are not presented as independently revalidated structural design rules.
