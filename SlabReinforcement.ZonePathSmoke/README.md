# Native zone integration regression

Runs the current linked integration code in installed Revit 2023 against an isolated copy of the baseline RVT. The runner creates a rectangular fixture inside that copy, rolls each case back, closes without saving and exits only its own Revit process. No installed add-in or user model is changed.

```powershell
.\SlabReinforcement.ZonePathSmoke\Start-ZonePathSmoke.ps1
```

The default baseline is `artifacts/single-slab-revit/run-20261024-130245/single-slab-input.rvt`. Override with `-Baseline`. Results and process identity are under `artifacts/zone-path-smoke/run-*`; the latest directory is recorded in `last-run-directory.txt`.

Inspect every `cases[].status` in `results.json`: report status `COMPLETED` means all cases ran, not that all passed. Assertions compare actual physical centerlines, count, type and elevation with the same plan the preview consumes.

Cases cover native child readback/filtering, all four layers with seven and two bars, rejection and rollback of a single-row zone, a 37° rotated local frame, an opening, and successive native zones with measured background reuse. Single-row groups are deliberately not supported: a zone contains at least two bars.

Creation-scope cases also check actual native output for the active tab: Background creates background and main perimeter even with invalid duplicate inactive zone selections; Zones creates only native zone paths and local U endings while all background layers and the main perimeter remain enabled. A repeated case first writes actual background/perimeter, then verifies existing IDs and geometry remain unchanged and no background is duplicated. A temporary distinct zone bar type makes unexpected cross-component output observable in physical type counts. Each case rolls back its entire transaction group, including that temporary type.

A fourth case passes a valid background plan to the writer with the Zones scope and requires rejection by the scope guard, with unchanged document/native IDs and physical positions.

`-PathBoundaryProbe` opts into an isolated native-boundary experiment instead of the normal regression cases. It compares the default whole-band layout, expanded fixed-number paths, and expanded maximum-spacing paths with individually moved/included `RebarInSystem` positions. It verifies four directions, adjacent sketch boundaries, the slab edge, actual 200 mm row spacing, native spacing/count metadata and host cover. Only this opt-in experiment saves `expanded-boundary-probe.rvt` inside its fresh run directory, closes and reopens that copy to check persistence. The input and original baseline are never saved.

The production suite adds seven boundary cases: joined native bands in four directions, a 37-degree rotated frame, a 1002-position array-limit fallback, and an expanded 1001-bar zone with multiple native children. The directional cases write another two-row zone with references to the existing individually moved systems. `-ProductionPathBands` runs the four production writer cases, saves a separate `production-path-bands.rvt` in the run directory, and reopens it for four additional persistence checks. This option never saves the input or baseline.

Five mixed-line cases check the winning diameter and its assigned pitch. In four directions, adjoining selected colors use 12 mm bars at 100 mm and 16 mm bars at 300 mm. The actual Session and Writer must use the largest nominal diameter and its own pitch throughout the common line, including native Path children, free-end anchorage, elevation and automatic U endings at the slab edge. Readback checks all physical centerlines and the 300 mm row pitch; no smaller zone fragments or background rows may appear. A separate Background-scope case compares the mixed-map plan and actual written bars with a control without zone colors, ensuring this zone rule does not alter background creation. These fixtures use Manual demand geometry; they do not assert numerical area sufficiency for the wider pitch.

Three sequential-creation cases bring the normal suite to 40 cases. Zones with local U endings are committed first; the host and actual bars are then reread into a fresh Session before background and main perimeter are created. One fixture uses a different zone type; another requires exact shared U positions to be reused while the rest of the main perimeter is added. The reverse order is also checked. Every case preserves the first operation's element IDs and full physical geometry, validates the new type/count/geometry, rejects full duplicates, and repeats the main-perimeter operation to prove no further bars are created. Existing provenance is read from Revit, never substituted from the initial in-memory plan. All three cases roll back their complete transaction groups.

`-ReaderBaseline` is a local regression aid requiring the ignored pre-change reader snapshot in `artifacts/zone-path-work/baseline`. It is expected to fail native-system assertions and is not an acceptance run.
