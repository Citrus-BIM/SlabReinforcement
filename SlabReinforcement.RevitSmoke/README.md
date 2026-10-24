# Revit smoke runner

Temporary `IExternalApplication` for tests inside installed **Revit 2023**. Its project links the current production integration source and Core project; it does not replace the installed CITRUS module.

The runner compiles the production Integration source into its own test DLL. Actual Revit API operations are exercised, including committed bar geometry inspection. Its runtime probe also loads the actual `bin/R2023/SlabReinforcement.dll`, resolves all types, constructs the command entry point and renders both production WPF tabs offscreen on the Revit UI thread with real model bar types. It does not invoke `Execute`, interactive picking or Create. Settings and model reinforcement must remain unchanged by this probe. Standalone WPF validation remains a separate UiSmoke. Final results and exact binary hashes are documented in `docs/validation/2026-10-24-revit-smoke.md`.

The application opens only `artifacts/revit-smoke/input.rvt`, inventories floors, CAD imports and bar types, and tests current production integration in the Revit API. Test cases run inside transaction groups and roll back. The original user model is never opened by the application.

The suite covers:

- Four straight reinforcement directions, four background layers and U-shaped perimeter bars on outer edges and openings, both separately and together. The current MVP explicitly rejects any `PerimeterLongitudinal` component in its plan.
- The same slab rotated 37 degrees using the same stable edge reference, plus a temporary 2000 × 2000 mm opening.
- Combined background/perimeter cases deliberately pass legacy flags (`UseUbars=false`, `UseLongitudinalBars=true`) and require only U-shaped perimeter bars in the resulting plan. The saved demonstration uses the current explicit U-only settings.
- Original DXF metadata fallback for anonymous CAD meshes, explicit metre units, a rotated and translated import, and rejection of incorrect units. Source and placed vertex sets must match in both directions. A real source zone also uses fixed manual test parameters; any geometric blocker is recorded without changing the parameters to obtain a pass.
- Positive and zero total-area demand with compatible background, insufficient specified steel, unconfirmed calculation axes, displaced layers, and repeated creation against existing model reinforcement.
- Adjacent colours assigned the same bar specification, compared with a single-colour reference, including actual creation and rollback for both plans.
- Existing closed stirrup fine-detail geometry: compare the production reader against adjusted Revit API curves and verify that a collision missed by the simplified planar axis is blocked.

Only after all required cases succeed, the runner creates a demonstration assembly in the copied document, saves `artifacts/revit-smoke/MVP-demo.rvt`, and exports PNGs from new Fine-detail isometric views: the whole floor and a 3 × 3 m corner. The views and report label their parameters as **tests, not project reinforcement**. Otherwise no demonstration is saved. The copy closes without further saving.

Straight cases use a synthetic demand field inset 1000 mm from the actual slab boundary and **zero anchorage**. They test the actual API writer, geometry, array layout and rollback, not structural adequacy or interpretation of a DXF legend. The first exploratory full-slab fixture correctly failed zone length rounding and is preserved in the run history.

Outputs are `run.log`, `progress.json`, `results.json` and `completed.flag` under `artifacts/revit-smoke`. `COMPLETED` means the runner finished; inspect every case's `status` before declaring the checks passed. Unexpected dialogs are logged and are not automatically accepted.

The production fallback uses `SlabReinforcement.Dxf`, a reusable streaming ASCII reader for model-space `3DFACE` entities. It supports TrueColor and ACI/ByLayer, explicitly rejects geometry inside reachable blocks, and never infers drawing units. A source plane containing unresolved ByBlock colours is excluded in full. Binary DXF and other mesh entity types are outside this MVP. Limits are 512 MiB, 1,000,000 direct faces, and 4,000,000 placed mesh vertices. The ACI palette retains the netDxf MIT notice.

Build the production R2023 configuration first, then the runner:

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' .\SlabReinforcement\SlabReinforcement.csproj -restore -p:Configuration=R2023
dotnet build .\SlabReinforcement.RevitSmoke\SlabReinforcement.RevitSmoke.csproj -c Release
.\SlabReinforcement.RevitSmoke\Start-Smoke.ps1 -SkipBuild
```

Before launch, the script copies the exact R2023 Core, Dxf, NetTopologySuite and supporting runtime libraries into the isolated runner folder and verifies their hashes. It never edits the production build directory. `environment.json` records the product DLL and dependency hashes; the runtime probe confirms that the loaded Core/Dxf/NTS match them. Its images are `MVP-product-runtime-BackgroundTab.png` and `MVP-product-runtime-ZonesTab.png`.

The unique manifest is `bin/Release/net48/CodexSlabReinforcementSmoke.addin`, AddInId `DB0C7F38-BC2A-431F-A857-AD910EE6E4AE`, beside the journal and assembly. The launch script sets this folder as the working directory. Journal playback isolates the test from the user's add-in folder. No persistent trust setting or user manifest is modified. An initial exploratory manifest in the user add-in folder was removed before the successful journal launch. `environment.json` records the original and tested binary SHA256 hashes.

The application requests normal Revit exit after closing the copied document. `Start-Smoke.ps1` refuses to start while its previous test process remains alive. Previous logs and reports move into timestamped history folders. The script only replaces its own `artifacts/revit-smoke/input.rvt` copy.

The journal approach follows [Autodesk's add-in loading guidance](https://blog.autodesk.io/loading-an-add-in-with-a-journal-file/) and the [pyRevit runner's primary implementation](https://github.com/pyrevitlabs/pyRevit/blob/master/dev/pyRevitLabs/pyRevitLabs.PyRevit/PyRevitRunner.cs).
