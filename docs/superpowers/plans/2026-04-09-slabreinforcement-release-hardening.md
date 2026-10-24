# SlabReinforcement Release Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** довести текущую версию SlabReinforcement до release-candidate уровня с явными ошибками, понятными предупреждениями и воспроизводимой release-проверкой.

**Architecture:** сохраняем текущий shared geometry-core и WPF-окно, не делаем большой архитектурный рефакторинг перед релизом. Закрываем только release-критичные риски: silent-fail на старте, неочевидные ошибки импорта DXF, слабые пользовательские diagnostics и отсутствие формального release-gate.

**Tech Stack:** C#, WPF, Autodesk Revit API, SDK-style .NET project, netDxf, smoke harness project.

---

### Task 1: Release Docs

**Files:**
- Create: `docs/superpowers/plans/2026-04-09-slabreinforcement-release-hardening.md`
- Create: `docs/release/2026-04-09-slabreinforcement-release-checklist.md`

- [ ] Зафиксировать release-план в репозитории.
- [ ] Добавить checklist с командами сборки, expected results и manual Revit gate.

### Task 2: Safe Command Startup

**Files:**
- Modify: `SlabReinforcement/SlabReinforcementCommand.cs`

- [ ] Убрать пустой `catch` вокруг `GetPluginStartInfo`.
- [ ] Захватывать startup warning и не прерывать работу команды, если telemetry/helper недоступен.
- [ ] Добавить дружелюбную обработку ошибок загрузки DXF и чтения пути подложки.
- [ ] Добавить явные сообщения об ошибках вместо аварийного вылета команды.

### Task 3: Release Diagnostics

**Files:**
- Modify: `SlabReinforcement/SlabReinforcementCommand.cs`
- Modify: `SlabReinforcement/SlabReinforcementWPF.xaml.cs`

- [ ] Протянуть startup/release warnings в пользовательские сообщения команды.
- [ ] Сделать итоговые сообщения согласованными: что пропущено, что не удалось, что требует ручной проверки.
- [ ] Сохранить non-blocking warnings отдельно от фатальных ошибок.

### Task 4: Verification

**Files:**
- Modify: `SlabReinforcement.GeometrySmoke/SlabReinforcement.GeometrySmoke.csproj` при необходимости

- [ ] Прогнать `dotnet build SlabReinforcement.sln -c R2026 -v minimal`.
- [ ] Прогнать compile-level smoke verification.
- [ ] Зафиксировать, проходит ли standalone smoke runtime или остаётся только manual Revit gate.
