# RX-native ARMGov — Research (среда + антипаттерны)

Дата: 2026-06-26. Цель: нативная реализация ARMGov в Directum RX (Remote Component в обложке модуля),
**с нуля**, не переписывая standalone-прототип (он нужен для демо).

## Среда (проверено)
- DDS-решения и исходники есть: `HANDOFF/directum-rx-workspace-main/source/{DirRX.ARMGov, DirRX.PrAnlts, DirRX.PrBI}`.
- Эталоны Remote Component (RC) присутствуют: `CRM/.../DirRX-CRMComponents`, `DirRX.Solution.Components`, `crm-spa`.
- node v22 + npm 11 — тулчейн сборки RC рабочий.
- ⚠️ PrAnlts/PrBI на текущем стенде (БД `DirRX261OGVGenAI`) **НЕ развёрнуты** — нет snapshot-таблиц
  `dirrx_prbi_*`/`prhealth`/`blockstat` (есть только платформенная `sungero_wf_blockstate`).
  → данные считаем **самодостаточно в ARMGov** (без зависимости от PrBI).
- Деплой сейчас НЕ выполняем — фаза проектирования. Деплой требует admin-UAC (vm-operator не админ).

## Механизм встраивания: Remote Component (RC)
- React-компонент через Webpack Module Federation, грузится веб-клиентом RX, монтируется в **обложку** (scope `Cover`).
- Эталон dual Card+Cover — `GoalsMap` (в targets); карточные RC — в CRM. Темы — CSS `--rndx-theme_*`, i18n — i18next.
- Данные — server/public-функции RX в сессии пользователя (НЕ прямой psql, НЕ ADO.NET).
- Авторизация/права — нативные (сессия RX + роли модуля).

## ⭐ Антипаттерны RX (канон «9 запретов» + perf-гайд + уроки PROJECT-HISTORY)
Канон: `HANDOFF/.../.claude/rules/_platform-guards.md`. Соблюдаем все:
1. **В клиенте нет `GetAll/Create/Save`** (только сервер; в Refresh/Showing/CanExecute — 0 запросов к БД).
2. **Только `AppliedCodeException.Create(...)`** — не native `throw new ...Exception`.
3. **`.Where()` до `.ToList()`** — не материализовать таблицу.
4. **Нет `Sum/Count/Min/Max/Average` на `GetAll()` без `Where`** (full table scan).
5. **Роли через `Roles.GetAll(r => r.Sid == X).FirstOrDefault()`**, не `IncludedIn(Guid)`.
6. Нет hardcoded чисел/строк в логике — Constants/Resources.
7. Нет silent-логов (`catch + Logger.Error` без re-throw `AppliedCodeException`).
8. Масс-операции в фоне — `Locks.TryLock` + батчи (Take(N)).
9. Нет дублирования логики.

Из perf-гайда (16) и code-patterns (25):
- **Анонимные типы `new {}` ЗАПРЕЩЕНЫ** → Structures (важно: в прототипе они везде — при порте переписать).
- `Calendar.Now/Today` (не `DateTime.Now`); нет `static`-полей (→ свойства), `out` (→ структуры), `is`/`as` (→ `X.Is()/As()`).
- `System.Data` ADO.NET запрещён → платформенный SQL-API (`SQL.CreateConnection`/`Queries.xml`).
- `[Public]` нельзя на generic-методы.
- Бюджет запросов: открытие карточки = 1, сохранение/действие ≤ 5. Кэш — `Sungero.Core.Cache.AddOrUpdate(key,val,ttl)`.
- Большие выборки — `IQueryable` + батчи `.Contains` по 1000; eager loading флажком.

Из PROJECT-HISTORY (прошлый краш ARMGov):
- enum-свойства: **`Code` значений обязан быть непустым и уникальным** (иначе краш старта WebServer на `WebEnumPropertyMetadata`). Либо не вводить enum.
- **Не править `.mtd` руками** в обход DDS (протухает `obj`-кэш → вшивается битая метадата). `obj` руками не удалять.
- Hand-written `.mtd` обязан содержать ПОЛНЫЙ набор top-level коллекций эталона.
- Задеплоенная модель/сборки — в БД (`sungero_ide_fulldeploy*`); деплой требует логина через WebServer (тупик при упавшем вебе) → **бэкап БД до/после**.
- Деплой — admin-UAC; DDS — старая desktop-версия от админа; UTF-8 wrapper для DTC.
- **Не трогать PrAnlts/PrBI**, `etc/_builds*`.

## Reference-файлы
- RC dev: `knowledge-base/guides/26_remote_components.md`, `32_rc_plugin_development.md`, `30_ui_libraries_reports.md`.
- Обложки/виджеты: `20_widgets_covers.md`, фикс локализации обложки: `31_cover_localization_fix.md`.
- Perf/запреты: `16_performance.md`, `25_code_patterns.md`, `.claude/rules/_platform-guards.md`, валидатор `45_package_validator_checks.md`.
- **Слои разработки:** `pages/ru-RU/ds/ds_nastroyka_repozitoriyev.md` (тип репозитория Base/Work), `27_dds_vs_crossplatform_ds.md` (`@solutionType`), `34_applied_solution_packaging.md` (PackageInfo.xml: `IsSolution`/`IsPreviousLayerModule`). **Публикуем в Work-слой, не в Base.**
- Прошлый модуль (источник логики UI/виджетов): `source/DirRX.ARMGov/`.
- Логика метрик (что считать) — из нашего standalone-прототипа (Program.cs): overview/process/тематики/прогноз/исключение уведомлений/имена этапов. **Переиспользуем ЛОГИКУ, а не код.**
