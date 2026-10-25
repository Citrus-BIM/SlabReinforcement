# Зоны по траектории и точный предпросмотр — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Preserve the existing working-tree improvements; edit only assigned files.

**Goal:** Создавать послойные зоны штатными PathReinforcement, унифицировать близкие торцы с редактируемым допуском 600 мм и показывать действительные ряды фона и П-обрамления.

**Architecture:** NTS остаётся источником проверенных контуров, рядов и групп. Для компонента Zones writer создаёт PathReinforcement, затем читает все физические дочерние позиции и сверяет их с планом. Фон и обрамление сохраняют свои способы создания. Пользовательское дополнительное смещение зон исключается; высота берётся из общей схемы фона. Предпросмотр использует тот же план без разрежения рядов.

**Tech Stack:** C#, WPF, NetTopologySuite, Revit API R2019–R2027; actual API verification in isolated Revit 2023.

## Accepted behavior

- Один выбранный слой зон за запуск: Низ X/Y либо Верх X/Y.
- Одна унифицированная группа из минимум двух стержней — штатная система PathReinforcement. По уточнению пользователя одиночные ряды не являются зонами: их исключаем из плана и предпросмотра после унификации.
- Торцы сравниваются с исходной первой полосой группы в пределах пользовательского допуска; несовместимые типы/шаги/высоты не объединяются.
- Объединение не пересекает отверстия и сохраняет исходную требуемую длину каждого ряда. Закреплённые П-концы перепроверяются.
- Допуск задаётся в UI, сохраняется; начальное значение 600 мм.
- Дополнительного пользовательского сдвига по высоте нет. Внутреннее API-смещение Path допустимо только для достижения вычисленной отметки соответствующего слоя.
- Все фактические ряды фона/П отображаются согласованно; подсказка навигации расположена вне геометрии.

## Task 1 — Uniform zone groups

Files: `SlabReinforcement.Core/ReinforcementPlanner.cs`, new `SlabReinforcement.Core.Tests/ZoneUnificationTests.cs`.

- [x] Add regression fixtures with sloped demand, growing and shrinking endpoints, equivalent types, holes, and U-connected endpoints. Assert preserved required endpoints and `Math.Abs(row.End - firstRow.End) <= tolerance` for grouped initial runs.
- [x] Run the new tests and capture failing behavior.
- [x] Bound grouping against original first-row endpoints instead of previously expanded endpoints. Regroup only provably compatible equal-endpoint supplemental/connected rows; validate every final physical row.
- [x] Run Core test suite, including existing coverage and perimeter-end cases.

## Task 2 — Native system readback

Files: `SlabReinforcement/Integration/ExistingRebarReader.cs`; shared helper if necessary within Integration.

- [x] Replace blanket RebarInSystem rejection with actual position enumeration, respecting host and include filters.
- [x] Read transformed full centerlines, type, physical diameter and unique position identity for native Path/Area children.
- [x] Keep unsupported-curve and size diagnostics; reject systems without inspectable physical bars rather than silently ignoring them.
- [x] Verify API signatures in local version assemblies and build R2019/R2023/R2027.

## Task 3 — Path writer and height

Files: new `SlabReinforcement/Integration/ZonePathWriter.cs`, existing `RebarPlanWriter.cs`, `ReinforcementSession.cs`, WPF offset controls and session tests.

- [x] Add failing integration/session assertions for zero extra zone height, native Path object count, full child readback, first/last rows and geometric equality.
- [x] Route `group.Placement.Component == "Zones"` to native Path creation. Configure bar type, straight shape, one bar side, face, length and distribution to match the plan. Verify after regeneration and correct only system placement parameters/sketch to achieve the checked plan.
- [x] Track parent and child IDs when filtering post-creation geometry; retain atomic rollback and full-duplicate checks.
- [x] Remove editable zone-offset field, ignore persisted old offset values, and use the shared X/Y elevation convention.
- [x] Build plugin and execute isolated actual-Revit cases for four directions, native readback/reuse, holes, rotated axes, minimum two-bar zones and rejected single-row zones.

## Task 4 — Honest preview

Files: `Preview/SlabPreviewCanvas.cs`, `Preview/SchematicPreviewBars.cs`, `SlabReinforcementWPF.xaml`, `SlabReinforcement.UiSmoke/Program.cs`.

- [x] Reproduce sparse background/P visualization and overlaying navigation hint.
- [x] Draw real positions from the accepted plan without independent row sampling. Show selected height/layer clearly while preserving matching lower-background/P context.
- [x] Move the navigation hint outside the drawing area and keep it readable when resized.
- [x] Verify row/end matching and render the actual WPF preview for visual inspection.

## Task 5 — Verification and delivery

- [x] Run `dotnet test SlabReinforcement.Core.Tests/SlabReinforcement.Core.Tests.csproj`.
- [x] Run `dotnet run --project SlabReinforcement.UiSmoke` and inspect generated preview.
- [x] Build plugin target versions and inspect warnings/errors; run actual Revit 2023 tests only on artifact copies, never user models.
- [x] Review final changes against accepted requirements, correct findings, and record exact tested binary and remaining limitations.
- [x] Return the bin path and a concise result with test evidence. Do not overwrite installed CITRUS binaries unless separately authorized.
