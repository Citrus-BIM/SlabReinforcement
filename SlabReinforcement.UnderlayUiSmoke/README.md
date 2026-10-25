# Revit 2023 modal underlay UI probe

This isolated, opt-in harness loads the actual product DLL/BAML in Revit 2023 and exercises four real `Window.ShowDialog()` loops on the same offscreen WPF window. It does not invoke user clicks, `PickObject`, the production creation command, or any write transaction. A copied baseline supplies real Revit bar types and read-only API queries between modal loops. Synthetic contexts and controlled pure preview delegates make stale-result cancellation deterministic; this probe does not test reinforcement algorithms.

The probe checks:

- Empty-source startup on the Background tab; `RequestUnderlaySelection()` cancels the active preview, calls `Hide()`, and returns from `ShowDialog()` without closing the window.
- `ConsumeUnderlaySelectionRequest()` returns true once, then false.
- `ApplyUnderlay()` replaces context/colors, preserves background and general editors, clears source-specific acknowledgments, color selections and numerical demand, and selects Zones.
- `ResumePreview()` allows the same window to reopen. A deliberately delayed old worker completes after the new plan is displayed and must not replace it.
- Cancel-return without applying a new source preserves the old context and choices.
- Same-RGB type/spacing survive replacement, but As/selection do not. Detached old color editors must no longer invalidate the new preview.
- A one-group to two-group replacement clears the source selection, current faces/colors and ready plan.
- Closing the final modal and then calling `Close()` again is harmless; settings and document state remain unchanged; WPF binding errors are absent.

Each modal stage has a 12-second dispatcher watchdog. Background worker gates are released in cleanup. The copied document is closed without saving. The launcher records existing Revit PIDs, creates its own process with `-WindowStyle Hidden` and a journal-local manifest, and never uses or closes pre-existing processes. Only the harness process posts ExitRevit after cleanup.

Build the final product R2023 first, then:

```powershell
dotnet build .\SlabReinforcement.UnderlayUiSmoke\SlabReinforcement.UnderlayUiSmoke.csproj -c Release
.\SlabReinforcement.UnderlayUiSmoke\Start-UnderlayUiSmoke.ps1 `
  -Baseline .\artifacts\single-slab-revit\run-20261024-130245\single-slab-input.rvt -SkipBuild
```

The launcher stages exact product dependency bytes after the harness build. Freeze product binaries while the process is running. Outputs use a new `artifacts/underlay-ui-smoke/run-*` directory; the existing reinforcement smoke entry points are untouched.

`Check-UnderlayContract.ps1` runs under Windows PowerShell 5.1 and reads assembly metadata without execution. The archived prior product lacks all four new methods (`old-contract.json`, contract false). The new product has them (`new-contract.json`, contract true).

Verified actual run: `artifacts/underlay-ui-smoke/run-20261024-150717`, 24 October 2026, Revit 2023 build 23.0.11.19. **7/7 PASS**, four modal cycles, zero binding errors, zero transactions, unchanged settings/baseline/copy. PID 3880 exited. Actual product SHA256: `2450739AE616F709BDC9E5F936532E2E8D05D546C77BF58E3E17293BF3320476`. Real BAML screenshots for empty, applied, replaced and multi-group sources were rendered and visually reviewed. See that run's `results.json`, `environment.json`, `integrity-after.json` and `protocol.md`.

Optional `-BackgroundAudit` runs the separate read-only `TryCaptureParameters` audit instead of the modal source lifecycle suite. Actual run `run-20261024-152353` reproduced the P2 issue where invalid zone merge/offset/rounding text blocks a background-only operation with IncludeZones=false. Its 8 passing audit checks mean the behavior was reproduced; no production fix was applied. The mode also records actual UI presentation of a mocked reference-only plan. See that run's `audit.md` and `results.json`; no model writes or settings saves occur.

Separate optional `-NativeAreaProbe` is intentionally a **copy-only model-writing POC**, unlike the UI modes. It creates exactly four native AreaReinforcement systems, one active layer each, Ø12/200 on the actual host boundary, then saves/reopens a new artifacts RVT. No production code or input file is changed. Actual run `run-20261024-153614` produced 4 Areas / 22 RebarInSystem sets / 812 bars, native MaximumSpacing, all-position centreline readback unchanged. With zero additional offsets, separate X/Y systems did **not** stack automatically: bottom axes both -159 mm and top axes both -41 mm from the slab top. This unresolved zero-separation geometry is recorded as POC evidence, not delivered design. Compact template/schedule metadata, PNG and `native-area-poc.md` accompany the result. The original readonly UI audit modes retain their no-write behavior.

`-AreaTemplateInventory` is a separate read-only mode for all bar types and existing native Area/RIS partition parameters. Run `run-20261024-154031` used a copy of the native POC and found 10 existing `-пог.м.` types, including Ø12, without editing shared type flags. See `template-type-inventory.md`, `bar-types.csv` and `integrity-after.json`. Probe modes are mutually exclusive.

`-NativePathProbe` is a separate copy-only POC on one 2000 mm straight edge of the real baseline. It uses the template's ordinary `ø12` and existing U shape `21`, never edits host cover or shared types, and creates one native Path. Final run `run-20261024-161123`: **PASS**, 1 Path / 1 RIS set / 11 bars, actual MaximumSpacing 198.8 mm under the 200 mm limit, 35 mm top/bottom/web cover, full centreline Save/Reopen match. Supported route: Create without shape, then assign PrimaryBarShapeId; FarSide/flip=true; sketch offset 35 mm yields web axis 41 mm. The direct shape overload rejects the zero-hook request in this template. The native length parameter 500 mm yields axis projection 494 mm and straight legs 458 mm; no anchorage claim is made. See final `protocol.md`, `geometry-summary.json`, PNG and RVT. Previous runs are diagnostics: 160722 saved/reopened but had the wrong edge orientation; it is not the accepted perimeter result. No diagonal/full-perimeter/integration testing is implied.
