# New-template Revit 2023 test

Opt-in `IExternalApplication` for the supplied Weandrevit 2021 structural template and cleaned DXF. This is independent of `SlabReinforcement.RevitSmoke`; its outputs are never replaced or moved.

The launch script creates a unique `artifacts/template-smoke/run-*` directory. It copies the RTE, reference RVT and DXF there and records the source/copy SHA256. It stages exact Core, Dxf, NTS and supporting DLLs from production `SlabReinforcement/bin/R2023` into its own runner directory, verifies their hashes and records the product and harness hashes. No production binaries, installed add-ins or trust settings are modified.

The Revit UI-thread Idling handler:

1. Calls `Application.NewProjectDocument` with the copied RTE, upgrades it to Revit 2023, and inventories actual template bar/floor/material/cover/shared-parameter data before creating model geometry.
2. Reads the copied reference RVT. It reconstructs only its horizontal footprint, placement and 200 mm thickness using a real concrete template floor type. Template cover defaults are retained. It does not copy reinforcement, source bar types or the old 10 mm covers.
3. Imports the cleaned DXF explicitly in millimetres with preserved colours, origin placement and no automatic correction of near-horizontal/vertical lines. It matches the reference total transform and validates source/placed vertices in both directions through production `DxfMeshReader`.
4. Saves `new-template-input.rvt` before reinforcement tests. Its starting view shows the new slab and DXF, and a baseline PNG is exported.
5. Tests every admissible DXF plane separately: all recognised colours, upper Y, fixed manual nominal Ø12/200, B25 table anchorage. No numerical As is invented. Steel grade is read from the actual production GUID; absence is reported rather than inferred from type names. The largest admissible plane's green colour is an additional independent case.
6. Tests four background layers, U bars, their combination, a 37° slab rotation with the same stable edge reference, and a temporary 2 × 2 m opening. It also tests the main DXF map plus background and U bars together in one production Session/Writer call, without tuning parameters after a blocker.
7. Rolls back each fixture. For successful cases, independently reopens the untouched baseline, recalculates, writes and saves the corresponding demonstration RVT, exports PNGs, closes it and reopens it to verify bar/set counts. Model hashing occurs only after closing the document.

Demonstrations are separate because they are different test cases: `success-demo.rvt` contains all colours of the main DXF map, `green-zone-demo.rvt` the green colour alone, `background-u-demo.rvt` the background/U assembly, and `combined-demo.rvt` all three components together if valid. A blocked case produces diagnostics and no demonstration for that case. Each saved model starts on its TEST overview. The 3 m zone detail is centred on actual reinforcement; the background/U detail shows the slab corner.

The actual production DLL/BAML is also loaded and its two WPF tabs rendered offscreen with template bar types. No `Execute`, interactive picking or Create button is invoked, and settings/rebar counts must remain unchanged. The numerical Session/Writer checks compile linked current production Integration sources against the exact shipped algorithm libraries.

Build and launch from the workspace root:

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' .\SlabReinforcement\SlabReinforcement.csproj -restore -p:Configuration=R2023
dotnet build .\SlabReinforcement.TemplateSmoke\SlabReinforcement.TemplateSmoke.csproj -c Release
.\SlabReinforcement.TemplateSmoke\Start-TemplateSmoke.ps1 -SkipBuild
```

The script defaults to the explicitly supplied P: template and Desktop source files; parameters can override those paths. It refuses to start if its previous test process is alive. Its unique manifest is beside the journal under `bin/Release/net48`, GUID `8D36DD88-5BF0-45D3-A65A-AC07AF1E01F9`. No user add-in folder is touched. Progress and unexpected modal dialogs are logged; dialogs are not blindly accepted. Revit is asked to exit normally after closing all copies.

Read `results.json`, not only `completed.flag`: `COMPLETED` means the runner finished, while `allRequestedCasesPassed` and each PASS/BLOCKED/FAILED status describe the result. `inventory.json` preserves the unmodified template inventory. Demonstration values are test assignments, not a structural design or an interpretation of an absent DXF legend.
