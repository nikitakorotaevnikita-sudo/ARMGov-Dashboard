# ARMGovDash — Skeleton (сквозной скелет) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development (или executing-plans).
> Шаги — чекбоксы `- [ ]`. **Деплой — gated** (Task 7): выполнять только на готовом стенде + admin-UAC + бэкап.
> Среда DDS/MCP — воркспейс `HANDOFF/directum-rx-workspace-main` (там toolchain). Прототип в корне репо НЕ трогаем.

**Goal:** Пройти весь конвейер нативного ARMGov на одном блоке — модуль `DirRX.ARMGovDash` в Work-слое +
1 server-функция данных + Remote Component в обложке, рендерящий её KPI.

**Architecture:** DDS-модуль (Work-слой, applied-solution .dat, `IsSolution=true`) → server PublicFunction
(данные через платформенный SQL-API + `Cache`) → Remote Component (React/Module Federation, scope `Cover`)
в обложке, вызывает функцию в сессии RX. Без PrBI, без правок Base, без enum.

**Tech Stack:** Directum RX 26.1 (DDS), C# (server), React 18 + Webpack Module Federation (RC), node v22/npm 11,
DeploymentToolCore (.dat), MCP-toolchain (scaffold/validate/deploy).

## Global Constraints (verbatim из research.md / design.md)
- Публикация — **только в Work-слой**, НЕ в Base; базовые модули — `IsPreviousLayerModule`, без правок.
- Имя модуля `ARMGovDash` (код компании `DirRX`), отображаемое **«АРМ Руководителя»**, имя в БД ≤7 (`ARMGD`), свежий GUID, **без enum**.
- `.mtd` руками НЕ править — только через DDS/scaffold (иначе протухает `obj` → краш).
- Антипаттерны (canon 9 + perf): нет `new {}` → **Structures**; только `AppliedCodeException`; нет `GetAll().Count()` без `Where`; `.Where()` до `.ToList()`; `Calendar.Now`; нет `static`/`out`/`is`/`as`/`System.Data` (только `SQL.CreateConnection`/`Queries.xml`); роли по `Sid`; в client 0 запросов к БД; `[Public]` не на generic; тяжёлые агрегаты — за `Cache`.
- Данные — самодостаточно (sungero_wf_*/gd_citizen), исключая уведомления (логика прототипа). Деплой — admin-UAC + бэкап до/после.

## File Structure (Work-слой, воркспейс HANDOFF/source/DirRX.ARMGovDash)
- `DirRX.ARMGovDash.Shared/Module.mtd` — метадата модуля (генерит DDS). Корень решения (`IsSolution`).
- `DirRX.ARMGovDash.Shared/Module.ru.resx` — DisplayName «АРМ Руководителя».
- `DirRX.ARMGovDash.Shared/ModuleConstants.cs` — RoleSid, CacheKey, версия.
- `DirRX.ARMGovDash.Shared/ModuleStructures.cs` — Structure `RegionKpi`.
- `DirRX.ARMGovDash.Server/ModuleServerFunctions.cs` — PublicFunction `GetRegionKpi`.
- `DirRX.ARMGovDash.Server/ModuleInitializer.cs` — роли (идемпотентно).
- `DirRX.ARMGovDash.Server/Queries.xml` (если нужен именованный SQL) — запрос KPI.
- `DirRX.ARMGovDash.Components/ArmGovDashboard/` — Remote Component (React): `component.manifest.js`,
  `loaders/armgov-dashboard-cover-loader.tsx`, `controls/armgov-dashboard/*.tsx`, `webpack.config.js`, `package.json`.
- `PackageInfo.xml` — пакет решения (`IsSolution=true` для ARMGovDash).

Эталоны: модуль — `source/DirRX.ARMGov/`; RC — `CRM/.../DirRX-CRMComponents/`; упаковка — гайд 34.

---

### Task 1: Каркас модуля в Work-слое (DDS scaffold)

**Files:** Create `source/DirRX.ARMGovDash/DirRX.ARMGovDash.Shared/{Module.mtd, Module.ru.resx, ModuleConstants.cs}`, `PackageInfo.xml`.
**Interfaces:** Produces — модуль `DirRX.ARMGovDash` (Name=`ARMGovDash`, CompanyCode=`DirRX`, DisplayName=«АРМ Руководителя», DBName=`ARMGD`, свежий GUID, IsSolution=true, без enum).

- [ ] **Step 1 (scaffold):** Создать модуль через MCP/DDS scaffold (НЕ писать .mtd руками). Параметры: Name `ARMGovDash`, CompanyCode `DirRX`, DBName `ARMGD`, IsSolution. Команды toolchain — из `directum-mcp-server` (`scaffold_module`/аналог; синтаксис — `--help` тулчейна).
- [ ] **Step 2:** В `Module.ru.resx` задать `DisplayName` = «АРМ Руководителя» (через DDS-редактор «именование»).
- [ ] **Step 3:** `ModuleConstants.cs` — константы:
```csharp
namespace DirRX.ARMGovDash.Constants
{
  public static class Module
  {
    public static class RoleSid { public const string Manager = "DirRX.ARMGovDash.Manager"; public const string Admin = "DirRX.ARMGovDash.Admin"; }
    public static class Cache  { public const string RegionKpi = "DirRX.ARMGovDash.RegionKpi"; }
    // GUID-ы процессов (из прототипа): поручения/обращения
    public static class Process { public const string Poruchenia = "c290b098-12c7-487d-bb38-73e2c98f9789"; public const string Appeals = "4ef03457-8b42-4239-a3c5-d4d05e61f0b6"; }
  }
}
```
- [ ] **Step 4 (verify):** `validate_all(full)` + `guid_consistency` → PASS. Package-валидатор Check9 (DisplayName.ru) → ок.
  Команды — toolchain MCP (`validate_all`, `check_guid_consistency`).
- [ ] **Step 5: Commit** `git add source/DirRX.ARMGovDash; git commit -m "ARMGovDash: каркас модуля (Work-слой, IsSolution, DisplayName «АРМ Руководителя»)"`

---

### Task 2: Роли + ModuleInitializer (идемпотентный)

**Files:** Create `DirRX.ARMGovDash.Server/ModuleInitializer.cs`.
**Interfaces:** Produces — роли с Sid `Module.RoleSid.Manager/Admin`.

- [ ] **Step 1:** ModuleInitializer — создание ролей по эталону `targets/.../DTCommons ModuleInitializer` (идемпотентно, поиск по Sid):
```csharp
public override void Initializing(Sungero.Domain.ModuleInitializingEventArgs e)
{
  CreateRole(Constants.Module.RoleSid.Manager, ARMGovDash.Resources.Role_Manager);
  CreateRole(Constants.Module.RoleSid.Admin,   ARMGovDash.Resources.Role_Admin);
}
private static void CreateRole(string sid, string name)
{
  var role = Roles.GetAll(r => r.Sid == sid).FirstOrDefault();   // по Sid, не IncludedIn(Guid)
  if (role == null) { role = Roles.Create(); role.Sid = sid; }
  role.Name = name; role.Save();
}
```
- [ ] **Step 2 (verify):** `validate_all` PASS (нет client GetAll/Create — это Server). Реальное создание ролей — на init при деплое (Task 7).
- [ ] **Step 3: Commit** `git commit -am "ARMGovDash: роли Manager/Admin + идемпотентный ModuleInitializer"`

---

### Task 3: Structure RegionKpi + server PublicFunction GetRegionKpi (данные через SQL-API + Cache)

**Files:** Create `DirRX.ARMGovDash.Shared/ModuleStructures.cs`, `DirRX.ARMGovDash.Server/ModuleServerFunctions.cs` (+ опц. `Queries.xml`).
**Interfaces:** Produces — `Structures.Module.IRegionKpi { int Total; int InWork; int Overdue; }`; `[Public] Structures.Module.IRegionKpi Functions.Module.GetRegionKpi()`.

- [ ] **Step 1:** Structure (НЕ анонимный тип) — через узел «Структуры и константы»:
```csharp
// ModuleStructures.cs
public partial struct RegionKpi { public int Total; public int InWork; public int Overdue; }
```
- [ ] **Step 2:** PublicFunction с кэшем и **платформенным raw-SQL API** `ExecuteScalarSQLCommand`
  (как прежний ARMGov/PrBI; НЕ System.Data, НЕ GetAll().Count). Несколько значений — одной строкой через `||';'||`, затем split:
```csharp
[Public]
public virtual Structures.Module.RegionKpi GetRegionKpi()
{
  Structures.Module.RegionKpi cached;
  if (Cache.TryGetValue(Constants.Module.Cache.RegionKpi, out cached))
    return cached;
  try
  {
    var sql =
      "select count(*)::text||';'||" +
      "count(*) filter (where t.status::text='InProcess')::text||';'||" +
      "count(*) filter (where exists (select 1 from sungero_wf_assignment a where a.task=t.id " +
        "and a.status::text='InProcess' and a.deadline is not null and a.deadline<now()))::text " +
      "from sungero_wf_task t where t.discriminator in ('" +
      Constants.Module.Process.Poruchenia + "','" + Constants.Module.Process.Appeals + "')";
    var raw = Sungero.Docflow.PublicFunctions.Module.ExecuteScalarSQLCommand(sql);  // raw SQL API платформы
    var parts = (raw ?? "0;0;0").Split(';');
    var res = Structures.Module.RegionKpi.Create();   // Structure из DDS-узла «Структуры и константы»
    res.Total   = int.Parse(parts[0]);
    res.InWork  = int.Parse(parts[1]);
    res.Overdue = int.Parse(parts[2]);
    Cache.AddOrUpdate(Constants.Module.Cache.RegionKpi, res, Calendar.Now.AddMinutes(1));   // TTL 60с
    return res;
  }
  catch (Exception ex) { throw AppliedCodeException.Create(ARMGovDash.Resources.KpiQueryFailed_Error, ex); }
}
```
> `RegionKpi` — Structure, создаётся через DDS-узел «Структуры и константы» (генерит `Structures.Module.RegionKpi` с `.Create()`), не free-struct. `ExecuteScalarSQLCommand` — проверенный паттерн прежнего ARMGov (ADR-003).
- [ ] **Step 3 (verify):** `validate_all` PASS; платформенный guard-хук (pre-write) не ругается (нет `new {}`, native throw, GetAll().Count, System.Data). Реальный вызов — Task 7.
- [ ] **Step 4: Commit** `git commit -am "ARMGovDash: RegionKpi Structure + GetRegionKpi (SQL-API + Cache, исключая уведомления-логику позже)"`

---

### Task 4: Remote Component «ArmGovDashboard» (Cover) — каркас и сборка

**Files:** Create `DirRX.ARMGovDash.Components/ArmGovDashboard/{component.manifest.js, package.json, webpack.config.js, loaders/armgov-dashboard-cover-loader.tsx, controls/armgov-dashboard/armgov-dashboard.tsx, controls/armgov-dashboard/armgov-dashboard-view.tsx, host-api-stub.ts}`.
**Interfaces:** Produces — RC `ArmGovDashboard`, loader scope `Cover`, рендерит KPI-плитки.

- [ ] **Step 1:** Скопировать скелет из эталона CRM (`CRM/.../DirRX-CRMComponents/`); переименовать под `ArmGovDashboard`. Манифест — scope Cover:
```javascript
// component.manifest.js
module.exports = {
  vendorName: 'DirRX', componentName: 'ArmGovDash', componentVersion: '1.0',
  controls: [{ name: 'ArmGovDashboard',
    loaders: [{ name: 'armgov-dashboard-cover-loader', scope: 'Cover' }] }]
};
```
- [ ] **Step 2:** View-компонент рендерит 3 KPI-плитки из props (тема — `--rndx-theme_*`):
```tsx
// armgov-dashboard-view.tsx
export const ArmGovDashboardView = ({ kpi }: { kpi?: { total:number; inWork:number; overdue:number } }) =>
  !kpi ? <div>Загрузка…</div> :
  <div style={{display:'flex',gap:12,color:'var(--rndx-theme_text-color)'}}>
    <Kpi label="Всего" v={kpi.total}/><Kpi label="В работе" v={kpi.inWork}/><Kpi label="Просрочено" v={kpi.overdue}/>
  </div>;
const Kpi = ({label,v}:{label:string;v:number}) =>
  <div style={{border:'1px solid var(--rndx-theme_border-color)',borderRadius:8,padding:'10px 16px'}}>
    <div style={{fontSize:24,fontWeight:700}}>{v}</div><div style={{opacity:.7}}>{label}</div></div>;
```
- [ ] **Step 3 (verify build):** `cd .../ArmGovDashboard && npm install && npx webpack` → собирается `remoteEntry.js` без ошибок.
- [ ] **Step 4 (verify standalone):** запустить standalone-режим (host-stub отдаёт mock kpi) → плитки рисуются.
- [ ] **Step 5: Commit** `git add DirRX.ARMGovDash.Components; git commit -m "ARMGovDash: Remote Component (Cover) — каркас KPI-плиток + сборка"`

---

### Task 5: Связка RC → server-данные

**Files:** Modify `controls/armgov-dashboard/armgov-dashboard.tsx` (логика загрузки), `host-api-stub.ts`.
**Interfaces:** Consumes — `Functions.Module.GetRegionKpi` (Task 3). Produces — RC, отображающий реальные KPI.

- [ ] **Step 1:** В control загрузить данные вызовом server-функции через хост-API (точный путь RC→server —
  OData / PublicApi / `[Public]`-функция — **выбрать по эталону CRM RC** на этом шаге; зафиксировать в research):
```tsx
useEffect(() => { hostApi.getRegionKpi().then(setKpi); }, []);  // обёртка над выбранным транспортом
```
- [ ] **Step 2:** В `host-api-stub.ts` — mock (`{total:2393,inWork:..,overdue:..}`) для standalone-отладки.
- [ ] **Step 3 (verify):** standalone-рендер с mock — плитки с числами. Реальные числа — Task 7 (на стенде).
- [ ] **Step 4: Commit** `git commit -am "ARMGovDash RC: загрузка KPI из server-функции (+ stub для standalone)"`

---

### Task 6: Упаковка RC в модуль + PackageInfo

**Files:** Modify `PackageInfo.xml`, разместить сборку RC в `Components` модуля.
**Interfaces:** Produces — пакет решения, готовый к деплою (Work-слой).

- [ ] **Step 1:** Положить `remoteEntry.js` + `metadata.json` (scope Cover) в `Components` модуля (по эталону CRM/targets).
- [ ] **Step 2:** `PackageInfo.xml`: один `PackageModuleItem` `IsSolution=true` (`DirRX.ARMGovDash`); базовые — `IsPreviousLayerModule=true`.
- [ ] **Step 3 (verify):** package-валидатор (45) без Error; `assemble_package` собирает `.dat`.
- [ ] **Step 4: Commit** `git commit -am "ARMGovDash: упаковка RC в Components + PackageInfo (Work-слой, IsSolution)"`

---

### Task 7 (GATED — деплой позже): Деплой в Work-слой + smoke

> Выполнять ТОЛЬКО когда: стенд готов, admin-UAC доступен, снят бэкап БД. Не на текущем этапе (проектирование).

- [ ] **Step 1:** `pg_dump` baseline-бэкап БД (до деплоя).
- [ ] **Step 2:** Подключить Work-репозиторий (`@solutionType: Work`) в конфиге DDS/launcher; убедиться, что ARMGovDash в Work, не в Base.
- [ ] **Step 3:** Деплой `.dat` от admin-UAC (`do-utf8.bat dt deploy` или DDS Publish, старый desktop от админа). `obj` генерится заново (не копировать).
- [ ] **Step 4 (smoke):** WebServer стартует **без `same key`**, PID стабилен >3 мин, `/Client/` 200; в логах `Module 'DirRX.ARMGovDash' load done`, нет `EntityFactory`/`Fatal`.
- [ ] **Step 5 (smoke):** обложка модуля «АРМ Руководителя» рисуется, RC показывает 3 KPI с реальными числами из БД; роли Manager/Admin созданы.
- [ ] **Step 6:** `pg_dump` бэкап после успешного деплоя.
- [ ] **Step 7: Commit** тег/заметка об успешном скелете.

---

## Self-Review
- **Покрытие design:** модуль (Task1) ✓, Work-слой/IsSolution (Task1,6) ✓, роли/initializer (Task2) ✓,
  данные SQL-API+Cache (Task3) ✓, RC Cover (Task4,5) ✓, упаковка (Task6) ✓, деплой/smoke gated (Task7) ✓.
- **Антипаттерны:** Structures (не `new{}`), AppliedCodeException, SQL-API (не System.Data), Sid-роли, Cache, без enum, .mtd через DDS — учтены в шагах.
- **Открытые (резолв на исполнении):** точный путь RC→server (Task5 Step1); точный SQL-API connection (Task3 Step2);
  точные команды MCP scaffold/validate (Task1) — из toolchain `--help`.
- **Типы согласованы:** `RegionKpi{Total,InWork,Overdue}` ↔ RC props `{total,inWork,overdue}` (маппинг при сериализации).
