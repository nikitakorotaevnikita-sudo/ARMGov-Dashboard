# 04 — Implementation (авторинг скелета ARMGovDash)

Канонические исходники нативного модуля (черновики авторинга по плану `03-plan/skeleton-plan.md`).
Это **исходники для интеграции в DDS-модуль**; финальная генерация `.mtd`, сборка RC, валидация и деплой
выполняются **в сессии воркспейса `HANDOFF/directum-rx-workspace-main`** с подключённым MCP-тулчейном
(`directum-scaffold/validate/deploy`) — в текущей сессии (репо прототипа) он не подключён.

## Что авторено здесь (можно писать руками)
- `source/DirRX.ARMGovDash/Shared/ModuleConstants.cs` — Sid ролей, ключ кэша, GUID процессов.
- `source/DirRX.ARMGovDash/Server/ModuleServerFunctions.cs` — `GetRegionKpi` (raw SQL `ExecuteScalarSQLCommand` + Cache).
- `source/DirRX.ARMGovDash/Server/ModuleInitializer.cs` — роли Manager/Admin (идемпотентно, по Sid).
- `components/ArmGovDashboard/` — Remote Component (React, scope Cover): манифест, loader, control, view, webpack, stub.

## Что GATED (только в HANDOFF-сессии с тулчейном)
- **Task 1:** генерация `Module.mtd` через `directum-scaffold` (`.mtd` руками НЕ писать). Туда же — DisplayName «АРМ Руководителя», Structure `RegionKpi` (узел «Структуры и константы»), DBName `ARMGD`, свежий GUID, IsSolution, Work-слой.
- **Сборка RC:** `npm install && npx webpack` — нужен доступ к приватным `@directum/*` пакетам (в этой сессии нет node_modules/registry для них).
- **validate_all / guid_consistency / package-валидатор** — через `directum-validate`.
- **Task 7 деплой+smoke** — на готовом стенде, admin-UAC, бэкап.

## Интеграция (в HANDOFF-сессии)
1. `directum-scaffold` создаёт модуль `DirRX.ARMGovDash` (Work-слой, IsSolution, без enum) + Structure `RegionKpi` + DisplayName.
2. Скопировать сюда авторенный C# (Server/Shared) в соответствующие узлы модуля.
3. Скопировать `components/ArmGovDashboard/` в `Components` модуля; `npm install && npx webpack`.
4. `directum-validate` → PASS. Затем Task 6 (PackageInfo) и Task 7 (деплой).
