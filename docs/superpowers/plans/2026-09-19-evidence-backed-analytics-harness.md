# Evidence-backed analytics harness — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Реализовать первую сквозную поставку аналитики: вопрос → нативный вызов GigaChat → read-only SQL → результаты с ID → проверяемый отчёт в Canvas, сохранив Qwen и существующий дашборд.

**Architecture:** Модули харнеса работают внутри существующего .NET-приложения. Провайдер генерирует действия, сервер исполняет разрешённые инструменты и сохраняет фактические результаты. ReportValidator связывает показатели и визуализации с этими результатами; произвольный текст модели не становится подтверждённым числовым ответом.

**Tech Stack:** .NET 10, C#, HttpListener, существующие вендоренные Npgsql и Microsoft.Extensions.Logging.Abstractions, System.Text.Json, ванильный JavaScript/SVG, Node для JS-тестов, Python stdlib для HTTP smoke. Без новых production-зависимостей в первой поставке.

**Spec:** `docs/superpowers/specs/2026-09-19-evidence-backed-analytics-harness-design.md`.

**Статус:** подготовлен для передачи другому агенту; задачи не исполнялись. Пользователь разрешил перейти от спецификации к подробному плану 2026-09-19. Перед реализацией исполнитель получает рассмотренный план и согласованный способ выполнения.

## Global Constraints

- «Приложение выполняет только чтение БД» — никаких DML, DDL, GRANT/REVOKE в RX. DDL тестовой базы допускается только в специально созданном локальном контейнере, не через конфиг RX.
- «Сохраняем .NET 10, HttpListener, Npgsql и существующий браузерный клиент».
- «Текущий провайдер — GigaChat-2. Поддержка Qwen должна сохраниться».
- «Интерфейс первой поставки — текущая “Аналитика по запросу”».
- «Общий бюджет — 120 секунд, до восьми обращений к модели, из них не более двух повторов после ошибки протокола или отчёта».
- «statement_timeout 10 секунд; не более двух одновременно исполняемых запросов харнеса; ожидание слота входит в общий бюджет запуска».
- «До 200 строк, 60 колонок и 1 МиБ сериализованного результата на исполнение; до 4 МиБ на запуск». Модель видит сначала 20 строк.
- «Ссылки на другие запуски запрещены». Реестр существует только в рамках запуска; постоянного хранилища нет.
- Русский UI; существующие метрики и исключение уведомлений сохраняются; секреты только в ignored config/env, не в логах, фикстурах или коммитах.
- AST-политика PostgreSQL, эксплуатационная установка роли, многопользовательский доступ, постоянные отчёты и DOCX/PDF находятся вне этой поставки.
- Локальная успешная проверка не доказывает изоляцию прав учётки RX и не означает готовность к промышленной эксплуатации.

## Review Focus

1. Модель прислала «12 и 8» без источника либо с неизвестным resultId: отклонить, не показывать как completed. Проверки задач 7–9.
2. ФИО неоднозначно/склонено, найдено подразделение вместо сотрудника или прислан посторонний ID: разрешение личности и повторная проверка выбора. Проверки задач 4, 8, 10.
3. Одно поручение содержит несколько заданий; граничная дата, завершение и создание перепутаны: дедупликация и явная метрика. Проверки задач 4, 11.
4. Урезана колонка, отдельная ячейка, байтовый бюджет или общий результат: нельзя нарисовать полный рейтинг/доли по частичному набору. Проверки задач 2, 3, 7, 10.
5. Параллельные прогоны, OAuth 401, отмена во время ожидания слота/схемы/SQL, смена провайдера в бэк-офисе: единый бюджет, снимок настроек, освобождение ресурсов. Проверки задач 3, 5, 6, 8, 9.

## 0. Передача, исходники и порядок исполнения

Рабочая копия:
`C:\Users\Korotaev_NO\Documents\Codex\2026-09-19\c-users-korotaev-no-desktop-armgov\work\ARMGov-Dashboard`.

Remote: `https://github.com/nikitakorotaevnikita-sudo/ARMGov-Dashboard.git`.
Базовый код: `5494232e0d96a050f24188dec60d8d08dd95e0bc`, ветка `analytics-canvas`.
Локальная ветка документации: `docs/harness-v2-design`, спецификация сохранена коммитом `c4e624c`.
Документы этой передачи коммитятся локально; не считать, что они уже опубликованы в origin.

**Не перепутать:** `C:\Users\Korotaev_NO\Desktop\Проекты\ARMGov-Dashboard` находится на `mvp-prod`. В ней нет актуального харнеса. Не переключать и не чистить эту папку ради задачи.

В нашей рабочей копии ignored `config.json` настроен на GigaChat-2. На порту 5080 запускался экземпляр из `bin/Release/net10.0`. Перед перезапуском установить актуальные PID и ExecutablePath; не доверять PID из переписки. `server.stdout.log` и `server.stderr.log` не включать в коммиты. Не выводить конфиг целиком.

До начала прочитать `CLAUDE.md`, спецификацию, этот план, затем выбранный execution skill Superpowers. Код и тесты не писать до review плана владельцем. Пользователь передаёт работу другому агенту; создание новой задачи или запуск агента в этой сессии не требуется.

При ошибке dubious ownership использовать **только для команд этой известной рабочей копии** `git -c safe.directory=<absolute-repo-path> ...`. Не добавлять глобальный wildcard.

На исполнении применить `using-git-worktrees`: отдельная ветка реализации от точного базового кода с документами. Не делать повторный clone при уже подходящей изоляции. Из ignored конфигурации переносить секреты только локально, без коммита, при необходимости запуска.

Рекомендуемое исполнение: subagent-driven, задачи строго по зависимости, spec-review и code-review после каждой. Общие контракты сначала; агенты не меняют их независимо. Альтернатива — последовательное native execution с финальным независимым ревью. Выбор делает владелец при передаче.

Последовательность: 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 → 9 → 10 → 11. Провайдеры 5/6 независимы от UI, но параллелить их стоит только после фиксации контрактов. Не объединять задачи 3 и 7: SQL и проверка отчёта имеют разные границы доверия.

### Общие команды в PowerShell

```powershell
$dotnetExe = 'C:\Program Files\dotnet\dotnet.exe'
& $dotnetExe build -c Release
node tests/charts.test.js
# После задачи 1:
& $dotnetExe run --project tools/harness-tests -c Release -- all
# Выбор набора, например:
& $dotnetExe run --project tools/harness-tests -c Release -- reports
```

Baseline до изменений: build и charts; сохранить итог в рабочем журнале вне git. Полный smoke обращается к стенду и модели — запускать осознанно на этапе интеграции. Недоступность окружения записывать как BLOCKED, не PASS.

## 1. Файлы и интерфейсы

Новые файлы не добавлять в tools/dbq или в Program.cs единым большим блоком.

| Файлы | Назначение |
|---|---|
| `Harness/Models/Contracts.cs`, `Harness/Models/HarnessJson.cs` | Wire DTO и сериализация |
| `Harness/Results/ResultStore.cs`, `Harness/Results/ResultLimiter.cs` | Фактические результаты и лимиты |
| `Harness/Execution/SqlGuard.cs`, `SqlScopePolicy.cs`, `ReadOnlyExecutor.cs` | Проверка SQL и исполнение |
| `Harness/Catalog/AnalyticsCatalog.cs`, `EmployeeResolver.cs`, `catalog.json` | Схема, метрики, поиск сущностей |
| `Harness/Providers/IModelProvider.cs`, `GigaChatProvider.cs`, `GigaChatTokenProvider.cs`, `QwenProvider.cs` | Транспорт и нормализация действий |
| `Harness/Agent/ToolDefinitions.cs`, `ToolDispatcher.cs`, `AnalysisAgent.cs`, `RunContext.cs` | Выполнение запуска |
| `Harness/Reports/ReportValidator.cs`, `ReportRenderer.cs` | Проверяемый отчёт |
| `Harness/Hosting/AnalysisEndpoint.cs`, `Program.Harness.cs` | HTTP и подключение к существующим билдерам |
| `analysis.js`, `tests/analysis.test.js` | Чистое представление нового отчёта |
| `tools/harness-tests/*` | Самостоятельный BCL test runner без NuGet |
| `tests/analysis_smoke.py`, `tests/fixtures/analysis-cases.json` | HTTP и сценарии |
| `tools/harness-tests/db/compose.yml`, `init.sql` | Изолированная тестовая БД |
| `docs/technical/analytics-harness-v2.md` | Контракт, запуск, ограничения и контрольный SELECT |

Существующие точки интеграции: `Program.cs`: LlmChatCore, SqlCheck, SqlRun, SchemaCore, SchemaHelp, ToolCall, IsLocalCall, GetConfigMasked, SaveConfigFromBody. `index.html`: askCanvas, renderCanvasBody, thinkBlock. `charts.js`: pickViews, renderView. Номера строк не являются контрактом — искать по именам.

### Общий контракт (вводится задачей 1)

Namespace `ArmGov.Harness`; JSON camelCase; enum на проводе — строки. Все идентификаторы и суммы long/decimal сериализуются без преобразования через double. UI отображает значения больше JS safe integer строкой; для графика преобразование допустимо только после проверки безопасного диапазона, иначе таблица и предупреждение.

```csharp
public record AnalysisRequest(string Question, EntitySelection[] Selections);
public record EntitySelection(string Mention, long EmployeeId);
public record PeriodSpec(string Kind, int? Months, DateTimeOffset? From, DateTimeOffset? To); // all|months|range
public record AnalysisContextSpec(string MetricId, PeriodSpec Period);
public record ColumnSpec(string Name, string Title, string Type); // string|number|date|boolean
public record Truncation(bool Rows, bool Columns, string[] Cells, bool Bytes);
public record QuerySpec(string Sql, Dictionary<string, JsonElement> Parameters,
    string MetricId, DateTimeOffset? From, DateTimeOffset? To);
public record QueryResult(string Source, ColumnSpec[] Columns, JsonElement[][] Rows,
    string? EffectiveSql, DateTimeOffset RetrievedAt, Truncation Truncation,
    string[] Warnings);
public record StoredResult(string RunId, string ResultId, QueryResult Data);
public record ResultPage(string RunId, string ResultId, ColumnSpec[] Columns,
    JsonElement[][] Rows, int Offset, int StoredRowCount, bool HasMore,
    Truncation Truncation, string[] Warnings);
public record CellRef(string ResultId, int Row, string Column);
public record FactSpec(string Id, string Operation, CellRef[] Inputs); // cell|sum|difference|ratio
public record BlockSpec(string Kind, string ResultId, string[] Columns,
    Dictionary<string, JsonElement>? EqualsFilter, int? Limit);
public record Interpretation(string MetricId, string Label, string Unit,
    DateTimeOffset? From, DateTimeOffset? To, string? DateField);
public record ReportSpec(string Title, Interpretation Interpretation,
    BlockSpec[] Blocks, FactSpec[] Facts, string[] TextTemplates, string? Commentary);
public record HarnessError(string Code, string Message, bool Retryable);
public record ValidationResult(bool Ok, HarnessError[] Errors);
public record ToolDefinition(string Name, string Description, JsonElement Parameters);
public record ModelAction(string Name, JsonElement Arguments, JsonElement AssistantMessage);
public record AgentStep(int N, string Tool, string Status, long ElapsedMs,
    string? ResultId, HarnessError? Error);
public record Clarification(string Question, EmployeeCandidate[] Candidates);
public record EmployeeCandidate(long Id, string Name, string Department);
public record RenderedReport(string Title, Interpretation Interpretation,
    string[] VerifiedText, Dictionary<string, JsonElement> Facts,
    BlockSpec[] Blocks, string? Commentary);
public record AnalysisResponse(string RunId, string Status,
    RenderedReport? Report, StoredResult[] Datasets, AgentStep[] Steps,
    long ElapsedMs, string[] Warnings, Clarification? Clarification,
    HarnessError? Error);
```

DTO names are C#; во внешнем ответе: `runId/status/report/datasets/steps/elapsedMs/warnings/clarification/error`. status: completed|needs_clarification|no_data|incomplete|failed. Поле interpretation находится в report при наличии отчёта; для остальных статусов допустимо отсутствие, не заполнять догадкой.

`CellRef.Row` — устойчивый индекс в неизменяемом StoredResult, до фильтров/сортировки для UI; это не позиция на графике. `EqualsFilter` проверяется по типам, сопоставляется с исходными строками; для личностей фильтр только по колонке ID. Limit влияет на показ, не меняет хранимый результат.

```csharp
public interface IModelProvider {
    Task<ModelAction> NextAsync(IReadOnlyList<JsonElement> messages,
        IReadOnlyList<ToolDefinition> tools, CancellationToken ct);
    JsonElement Feedback(ModelAction action, object result);
}
public interface IQueryExecutor {
    Task<QueryResult> ExecuteAsync(QuerySpec query, CancellationToken ct);
}
public interface IEmployeeResolver {
    Task<EmployeeCandidate[]> SearchAsync(string[] tokens, CancellationToken ct);
    Task<EmployeeCandidate?> GetAsync(long id, CancellationToken ct);
}
```

Публичные сигнатуры этих типов — общий контракт задач; любые изменения вносить сначала сюда и во все потребители.

## Task 1: Контракты и выполняемые offline-тесты

**Files:** Create `Harness/Models/Contracts.cs`, `Harness/Models/HarnessJson.cs`, `tools/harness-tests/Harness.Tests.csproj`, `tools/harness-tests/Program.cs`, `tools/harness-tests/Check.cs`, `tools/harness-tests/ContractsTests.cs`. Modify `armgov-standalone.csproj` только при необходимости nullable для новых файлов (предпочесть `#nullable enable` в новых файлах).

**Interfaces:** создаёт все DTO выше и `HarnessJson.Options` (camelCase, PropertyNameCaseInsensitive=false, MaxDepth=32, unknown fields rejected для входных действий). Test helper: `Check.True(bool)`, `Check.Equal<T>(T,T)`, `Check.Throws<T>(Action)`.

- [ ] **1. Создать test project**, который ссылается на приложение. В исходном csproj `tools/**/*.cs` уже исключены — сохранить это.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>Harness.Tests</AssemblyName>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="../../armgov-standalone.csproj" /></ItemGroup>
</Project>
```

Runner обнаруживает public static parameterless методы классов `*Tests`, выполняет void/Task, считает PASS/FAIL, возвращает exit 1 при любом FAIL. Первый аргумент выбирает имя класса без Tests, case-insensitive, либо all. Неизвестный набор или ноль найденных тестов — exit 2. Не подключать реальные DB/LLM при all; отдельный аргумент db включает только DbTests.

- [ ] **2. Добавить failing тест сериализации.**

```csharp
public static void WireNamesAreStable() {
    var req = new AnalysisRequest("Сравни поручения", Array.Empty<EntitySelection>());
    var json = JsonSerializer.Serialize(req, HarnessJson.Options);
    Check.True(json.Contains("\"question\""));
    Check.True(!json.Contains("\"Question\""));
}
public static void UnknownInputFieldIsRejected() {
    Check.Throws<JsonException>(() => JsonSerializer.Deserialize<AnalysisRequest>(
        "{\"question\":\"q\",\"selections\":[],\"connectionString\":\"x\"}", HarnessJson.Options));
}
```

- [ ] **3. RED:** `dotnet run --project tools/harness-tests -c Release -- contracts`. Сначала компиляция покажет отсутствующие DTO; после добавления DTO подтвердить падающий assertion на неверных настройках JSON, затем исправить опции. Не считать компиляционную ошибку единственным доказательством поведения.
- [ ] **4. GREEN:** реализовать DTO выше и опции; добавить null/empty question validation в отдельный `AnalysisRequestValidator.Validate(AnalysisRequest)` → ValidationResult. Вход: 1–4000 символов, selections максимум 10, positive ID, непустой Mention; неизвестные поля отклонять. Тесты границ 0/4000/4001 и null selections.
- [ ] **5. Проверить build + contracts; commit:** `test(harness): add contracts and offline test runner`.

## Task 2: Реестр всех результатов и раздельные ограничения

**Files:** Create `Harness/Results/ResultStore.cs`, `ResultLimiter.cs`, `tools/harness-tests/ResultsTests.cs`.

**Interfaces:** `new ResultStore(string runId, int byteLimit=4194304)`; `StoredResult Add(QueryResult)`; `StoredResult Get(string runId,string resultId)`; `ResultPage Page(string runId,string resultId,int offset=0,int take=20)`; `StoredResult[] All()`; `ResultLimiter.Limit(QueryResult, int byteLimit=1048576)` → QueryResult. Валидационные ошибки — `HarnessException(HarnessError)` из Contracts.cs, ввести в этой задаче.

- [ ] **1. RED-тесты:**

```csharp
public static void PreviousResultRemainsAddressable() {
    var store = new ResultStore("run-a");
    var data = new QueryResult("sql", new[]{new ColumnSpec("n","Количество","number")},
        new[]{new[]{JsonSerializer.SerializeToElement(7)}}, "select 7", DateTimeOffset.UtcNow,
        new Truncation(false,false,Array.Empty<string>(),false), Array.Empty<string>());
    var first = store.Add(data);
    store.Add(data with { Source = "tool:leaders" });
    Check.Equal(7, store.Get("run-a", first.ResultId).Data.Rows[0][0].GetInt32());
    Check.Throws<HarnessException>(() => store.Get("run-b", first.ResultId));
}
```

Ещё тесты: 201 строка, 61 колонка, строка >200 символов, вложенный массив >50 элементов, byteLimit=512 и общий бюджет=1024, границы страницы, zero rows, decimal/int64. При лимите колонок отмечать Columns; при изменении ячейки Cells содержит `row:column`; лимит байтов не маскировать под Rows.

- [ ] **2. Запуск:** `dotnet run --project tools/harness-tests -c Release -- results` → FAIL.
- [ ] **3. Реализация:** immutable deep clone JsonElement, ID монотонные `r1/r2` внутри run; clone массивов при Add и отдаче. Не сохранять ссылки на изменяемые внешние массивы. Подсчитывать UTF-8 bytes для полного QueryResult после ограничения каждой добавляемой строки; строки добавлять только целиком. Метаданные, которые сами превышают лимит, дают `result_metadata_too_large`; не зацикливаться на пустом результате. Общий лимит проверять до регистрации, ошибка `run_result_budget_exceeded`.

```csharp
if (runId != _runId || !_results.TryGetValue(resultId, out var result))
    throw new HarnessException(new("unknown_result", "Источник этого запуска не найден", true));
```

- [ ] **4. GREEN:** весь results и contracts; mutation-тест меняет исходный массив после Add и не влияет на store.
- [ ] **5. Commit:** `feat(harness): retain bounded immutable query results`.

## Task 3: Единый контролируемый SQL и отмена

**Files:** Create `Harness/Execution/SqlGuard.cs`, `SqlScopePolicy.cs`, `ReadOnlyExecutor.cs`, `tools/harness-tests/SqlTests.cs`, `DbTests.cs`, `db/compose.yml`, `db/init.sql`. Modify `Program.cs` в SqlCheck/SqlRun и csproj ссылок test project на Npgsql при необходимости.

**Interfaces:** `SqlGuard.Check(string)` → `(bool Ok,string? Reason,string? Effective)`; `SqlScopePolicy.Check(string sql, IReadOnlySet<string> allowedRelations)` → ValidationResult; `ReadOnlyExecutor(string connectionString,IReadOnlySet<string> allowedRelations,SemaphoreSlim slots)` implements IQueryExecutor. Semaphore один на процесс для старого и нового харнеса. QuerySpec параметры только scalar/null; зарезервированные `from/to/asOf` формирует сервер, не модель.

- [ ] **1. Сначала characterization:** перенести набор вредоносных строк из tests/smoke.py в SqlTests; вызвать ещё старый guard через reflection `typeof(AnalysisRequest).Assembly.GetType("Program").GetMethod("SqlCheck", BindingFlags.Static | BindingFlags.NonPublic)`, без HTTP и без исполнения. После переноса guard старый метод остаётся тонкой обёрткой, новые тесты вызывают публичный SqlGuard; временную reflection-зависимость убрать. Новый RED:

```csharp
public static void ScopeRejectsUnknownRelation() {
    var allowed = new HashSet<string>{"public.sungero_wf_task"};
    Check.True(!SqlScopePolicy.Check("select * from private.payroll", allowed).Ok);
    Check.True(SqlScopePolicy.Check("select count(*) from public.sungero_wf_task", allowed).Ok);
    Check.True(!SqlScopePolicy.Check("select public.run_external_command('x')", allowed).Ok);
}
```

- [ ] **2. RED:** `dotnet run --project tools/harness-tests -c Release -- sql`.
- [ ] **3. Реализация политики:** текущий разбор литералов/комментариев и deny-list перенести без ослабления. Дополнительный scanner различает строки, identifiers, скобки, WITH bindings, FROM/JOIN и function calls. Разрешить только явно распознанное подмножество SELECT/CTE/UNION/агрегатов/оконных функций; неизвестную конструкцию отклонять как unsupported_sql с указанием причины. Проверять каждый физический relation, включая вложенные запросы и ветви UNION. Системные каталоги доступны через серверный catalog tool, не произвольным SQL модели. Для unqualified relation разрешается только public + заданный search_path. Разрешённые функции перечислены явно (count/sum/avg/min/max/coalesce/nullif/date_trunc/extract/round/percentile_cont/row_number/rank/dense_rank/lag/lead/lower/upper/trim/length); расширение только с тестом. Table-valued functions, пользовательские типы/операторы/функции, рекурсивные CTE и нераспознанные casts отклоняются. Не выдавать scanner за полный PostgreSQL AST или security sandbox.

Тесты вложенного relation, кавыченных имён, dollar/E-строк, комментариев, CTE alias versus physical table, schema qualification, опасной функции внутри SELECT/CTE, разрешённых агрегатов и windows обязательны. Для прежнего HTTP /sql/run сохранить форму ответа, но scope-policy единая; прежний произвольный доступ вне каталога теперь намеренно отклоняется и отражается в smoke.

- [ ] **4. Исполнитель:** исключительно async с CT на Open/Begin/ExecuteReader/Read/Rollback; BeginTransaction + SET TRANSACTION READ ONLY, SET LOCAL statement_timeout=10000, фиксированный search_path. Настройки транзакции — серверные константы, не часть generated SQL. Timeout также ограничен остатком CT. Await slots.WaitAsync(ct) перед открытием соединения, release в finally только после успешного захвата. EffectiveSQL из guard, значения — NpgsqlParameter с проверяемым типом, никогда интерполяцией. Чтение 201-й строки — только для признака Rows. Передать данные в ResultLimiter. Готовые серверные запросы каталога используют этот же лимитер/слоты/CT через внутренний доверенный путь, недоступный аргументам LLM.

```csharp
await _slots.WaitAsync(ct);
try {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(ct);
    // Транзакция и команды получают ct; reader всегда dispose до завершения транзакции.
} finally { _slots.Release(); }
```

- [ ] **5. Read-only integration:** compose содержит PostgreSQL 17, binding только `127.0.0.1:55439`, отдельную `harness_test` БД и одноразовую тестовую роль. В init.sql создать изолированные таблицы, seed и SELECT-only роль. Пароль тестовой роли задаётся env запуска контейнера, не совпадает с RX. В DbTests требовать `ARMGOV_TEST_DB`, host loopback, database строго harness_test; при несоответствии аварийно завершать до открытия подключения. Test пытается INSERT/CREATE **на тестовом сервере** как внутри read-only, так и напрямую under SELECT-only role; затем SELECT подтверждает неизменность данных. Test container никогда не берёт config.json.

Проверить Cancel во время WaitAsync, Open, Read; 3 запроса при двух слотах; после ошибок слоты снова доступны. Выполнение тестов БД не входит в all.

- [ ] **6. GREEN:** sql, db, build; сохранить фактические результаты. Commit `feat(harness): centralize bounded read-only SQL execution`.

## Task 4: Каталог и разрешение сотрудников

**Files:** Create `Harness/Catalog/AnalyticsCatalog.cs`, `EmployeeResolver.cs`, `catalog.json`, `tools/harness-tests/CatalogTests.cs`. Modify `.csproj` CopyToOutputDirectory для catalog.json.

**Interfaces:** `AnalyticsCatalog.Search(string,int take=10)` → JsonElement; `Describe(string schema,string table,string[] fields)` → JsonElement; `AllowedRelations` IReadOnlySet<string>; `EmployeeResolver` implements IEmployeeResolver; `MetricDefinition` record (Id, Unit, DateField, Definition), `GetMetric(string)` → MetricDefinition. Каталог и списки не изменяются во время run.

- [ ] **1. RED:**

```csharp
public static void PersonalCountHasExplicitGrain() {
    var catalog = AnalyticsCatalog.Load("Harness/Catalog/catalog.json");
    var metric = catalog.GetMetric("personal_instruction_count");
    Check.Equal("поручение", metric.Unit);
    Check.Equal("root.created", metric.DateField);
    Check.True(metric.Definition.Contains("distinct"));
}
```

Catalog.Load принимает явно заданный путь; production путь от AppContext.BaseDirectory, test путь от найденного repo root, а не случайного cwd.

- [ ] **2. Запуск catalog → FAIL.** Подготовить фикстуры с двумя Ивановыми, подразделением с похожим именем, Босовым, отсутствующим человеком; IDs фиктивные. Данные реальных сотрудников не коммитить.
- [ ] **3. Реализация каталога:** перенести 12 таблиц SchemaCore и связи из реального кода, включая direction/cardinality. Для первой поставки каталог разрешённых полей ручной и версионируемый; Describe сопоставляет его с фактической information_schema только для явно разрешённой schema/table. Несуществующие поля возвращают schema_mismatch, а не догадку. Примеры только явно разрешённых status/processkind-полей, не произвольные ФИО/тексты. Указать отдельные определения assigned instruction count, completed assignment count и KPI, чтобы не подменять факт завершения поручения закрытием одного задания.

```json
{"id":"personal_instruction_count","unit":"поручение","dateField":"root.created",
 "definition":"count(distinct root.id) по поручениям с заданием выбранному performer; уведомления исключены; количество всех поручений, не только выполненных"}
```

- [ ] **4. Поиск:** параметризованные токены, максимум 5, длина 1–100; экранировать `%`, `_`, escape character для ILIKE. Все токены должны совпасть в name, порядок не важен. Максимум 20 кандидатов + флаг переполнения; передать его через структурированную ошибку `too_many_candidates` вместо молчаливого усечения. Только сотрудники по типу/дискриминатору из существующего BuildLeaders, не все recipients; проверить этот фильтр чтением схемы стенда. Не исключать исторически закрытого сотрудника молча: вернуть статус в описание кандидата, если поле доступно. GetAsync возвращает только допустимого сотрудника. При склонении модель может повторить поиск по нормализованным токенам; fuzzy auto-selection не вводить.
- [ ] **5. GREEN:** catalog и db: найдено 2 → уточнение, найдено 0 → явно нет кандидата, подразделение не кандидат, ID не существует, wildcard вход не становится фильтром всех строк. Commit `feat(harness): add metric catalog and employee resolution`.

## Task 5: Нативный GigaChat с сохранением истории функций

**Files:** Create `Harness/Providers/IModelProvider.cs`, `GigaChatProvider.cs`, `GigaChatTokenProvider.cs`, `tools/harness-tests/GigaChatTests.cs`, `FakeHttpHandler.cs`.

**Interfaces:** IModelProvider выше. `GigaChatProvider(HttpClient client,GigaChatTokenProvider tokens,string model,Uri endpoint)`; `GigaChatTokenProvider(HttpClient client,string authorizationKey,string scope,TimeProvider time)`; `Task<string> GetAsync(CancellationToken)`, `void Invalidate()`. FakeHttpHandler принимает `Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>`; сохраняет обезличенные request bodies теста, не Authorization.

- [ ] **1. RED:** подставной HTTP возвращает message с пустым content, function_call.name=`execute_sql`, arguments object и functions_state_id=`state-1`.

```csharp
// После NextAsync action содержит исходное assistant message целиком.
Check.Equal("execute_sql", action.Name);
Check.Equal("state-1", action.AssistantMessage.GetProperty("functions_state_id").GetString());
var feedback = provider.Feedback(action, new { resultId = "r1" });
Check.Equal("function", feedback.GetProperty("role").GetString());
Check.Equal("execute_sql", feedback.GetProperty("name").GetString());
Check.Equal("r1", JsonDocument.Parse(feedback.GetProperty("content").GetString()!)
    .RootElement.GetProperty("resultId").GetString());
```

В тесте action/provider создаются через публичные конструкторы и FakeHttpHandler; OAuth fake возвращает access_token=`test-only`, expires_at. Запрос хранит функции и предыдущий assistant message; не пересобирает и не теряет state.

- [ ] **2. Запуск gigachat → FAIL.** Второй тест присылает просто «12 и 8» без function_call: ожидается HarnessException `unstructured_response`, не action report.
- [ ] **3. Реализация:** system prompt не требует текстового JSON для GigaChat. functions содержат фактические схемы ToolDefinitions; имена и arguments строго валидируются, extra fields отклоняются. `function_call=auto`; допустим только известный нативный вызов, включая submit_report/clarify. В content не искать замаскированные вызовы и не исполнять их. Truncated finish_reason=length не трактовать как корректное действие.
- [ ] **4. OAuth:** текущие URL брать из сохранённого пресета; ключ не логировать. Cache по credentials/scope, SemaphoreSlim async, обновление с запасом 1 мин. Один retry при 401, с тем же CT и телом; никакого нового бюджета. Не переключать scope молча: использовать настроенный scope и возвращать понятную ошибку. 429 учитывать Retry-After только если укладывается в остаток, не создавать неограниченные retries. HttpClient без отключения проверки TLS. Настройки провайдера — snapshot на run.
- [ ] **5. GREEN:** пустой content+function_call, malformed arguments, неизвестная функция, state round-trip, 401→200, повторный 401, OAuth cancel, ожидание token lock cancel, expired token, 429 budget; secrets отсутствуют в исключениях. Commit `feat(harness): support native GigaChat function calls`.

## Task 6: Адаптер Qwen и единый каталог инструментов

**Files:** Create `Harness/Providers/QwenProvider.cs`, `Harness/Agent/ToolDefinitions.cs`, `tools/harness-tests/QwenTests.cs`, `ToolsTests.cs`.

**Interfaces:** QwenProvider(HttpClient,string token,string model,Uri endpoint) implements IModelProvider. `ToolDefinitions.All` IReadOnlyList<ToolDefinition>; `ToolDefinitions.Validate(string,JsonElement)` → ValidationResult. Schema validator поддерживает реально используемые object/array/string/integer/number/boolean/null, required, enum, limits, additionalProperties=false; неизвестный keyword в собственных схемах — ошибка теста, а не молчаливое игнорирование.

- [ ] **1. RED:** parse scripted Qwen action и сравнить Name/Arguments с эквивалентным GigaChat действием. Два цитируемых JSON и финальное действие: брать последнее валидное по прежнему правилу, затем строго проверять schema. Невалидный/обрезанный JSON → protocol error.

```json
{"action":"sql","query":"select count(*) n from public.sungero_wf_task","purpose":"Проверка"}
```

Ожидается нормализованный execute_sql с `sql`, пустыми parameters и metricId, который модель должна получить из текущего каталога; отсутствие обязательного metricId вызывает исправление, не автоподстановку случайной метрики.

- [ ] **2. Запуск qwen/tools → FAIL.**
- [ ] **3. Реализация:** `enable_thinking=false`, Bearer, прежний endpoint; retry/cancel в общем бюджете. Qwen получает схему действий нового харнеса в system prompt; старые raw действия `tool/schema/sql/answer` разрешены только как совместимый синтаксис, при этом answer без валидной ReportSpec не становится completed. Feedback role=user с JSON envelope `{tool,result}`; GigaChat остаётся role=function. Обычные старые summary/chat endpoints не переключать на новый контракт.

Точные инструменты и основные аргументы:

```text
search_catalog {query:string}
describe_table {schema:string,table:string,fields:string[]}
find_employees {tokens:string[]}
set_context {metricId:string,period:PeriodSpec}
dashboard_metric {name:enum(ToolCatalog),args:object}
execute_sql {sql:string,parameters:object,metricId:string}
read_result {resultId:string,offset:integer,take:integer[1..20]}
submit_report {report:ReportSpec}
clarify {question:string,candidateIds:integer[]}
```

dashboard_metric args проверять по конкретному name: period допустим только у поддерживающих билдеров; неизвестные аргументы не игнорируются. Произвольные URL, роли, config и SQL в других инструментах запрещены. Для execute_sql разрешены параметры scalar/null с именами `[a-z][a-z0-9_]{0,31}`; from/to/asOf добавляются сервером. set_context выбирает известную метрику и структурированный период, но не числовые факты; сервер возвращает каноническую Interpretation. execute_sql/dashboard_metric/submit_report требуют установленного контекста. Изменить контекст после первого результата нельзя: вернуть context_locked, чтобы отчёт не переименовал уже собранные данные; новый смысл требует нового запуска.

- [ ] **4. GREEN:** Qwen JSON fence/explanation, missing field, unknown action, multiple candidates, extra config property, слишком глубокий JSON, tool argument type, все schema проходят self-validation. Commit `feat(harness): normalize providers to validated analytics tools`.

## Task 7: Проверка отчёта и серверный расчёт фактов

**Files:** Create `Harness/Reports/ReportValidator.cs`, `ReportRenderer.cs`, `tools/harness-tests/ReportsTests.cs`.

**Interfaces:** `ReportValidator.Validate(ReportSpec,ResultStore,Interpretation)` → ValidationResult; `ReportRenderer.Render(ReportSpec,ResultStore,Interpretation)` → RenderedReport. Interpretation приходит от RunContext после проверки metricId, периода и selected IDs, не принимается от report без сравнения. Title/Commentary — escaped plain text, not HTML.

- [ ] **1. RED:**

```csharp
public static void MissingEvidenceCannotBecomeReport() {
    var store = new ResultStore("a");
    var meaning = new Interpretation("personal_instruction_count","Поручения","поручение",null,null,null);
    var spec = new ReportSpec("Сравнение", meaning,
        new[]{new BlockSpec("bars","r404",new[]{"name","n"},null,null)},
        new[]{new FactSpec("ivan","cell",new[]{new CellRef("r404",0,"n")})},
        new[]{"У сотрудника {{ivan}} поручений"},null);
    Check.True(!ReportValidator.Validate(spec,store,meaning).Ok);
}
```

- [ ] **2. Запуск reports → FAIL.** Положительный тест: две реальные fixture rows 7/3 → факты 7/3, difference=4, ratio=7/3 по decimal, соответствующие поля графика. Эти числа — искусственная фикстура, не ответы по стенду.
- [ ] **3. Реализация:** validate kind enum table/bars/line/shares/kpi; columns существуют и без дублей; bars минимум label+number, line date+number, shares неотрицательное number и полная выборка, kpi ровно одна строка после фильтра. Filter проверяется по source types; value не выдаётся за result row. CellRef проверяется до чтения. Operations: cell ровно 1 input, sum ≥1, difference/ratio ровно 2; все operand cells конечные numeric и не усечены. Ratio zero → null и warning. Decimal overflow → ошибка, не wrap. Aggregates над усечёнными наборами запрещены; таблица и ограниченный bars разрешены с подписью partial. Row number всегда относительно StoredResult.

```csharp
// Использовать decimal.TryParse(InvariantCulture), не double для финансов/целых итогов.
// facts вычисляются на сервере; затем replace только известных {{factId}}.
// Неизвестный placeholder, повторяющийся factId и лишний numeric literal => validation error.
```

TextTemplates допускают текст и placeholders: ASCII/Unicode цифры вне placeholders запрещены, чтобы «12 и 8» не миновали renderer. Период и единицы показываются отдельными серверными полями. Это не семантическое доказательство текста: числительные словами и неподтверждённые причинные выводы остаются риском; Commentary маркируется «Комментарий модели, не проверен» и не смешивается с VerifiedText. Для гарантированных KPI отображать серверные подписи каталога и значения facts, не полагаться на авторский текст модели.

- [ ] **4. GREEN:** чужой run, неизвестная колонка, Row за пределом, altered interpretation, overflow, division by zero, injection label, invalid filter, truncated cell, shares partial, literal 12/8, повтор fact ID, фильтр по ID вместо display name. Commit `feat(harness): render reports from verified result references`.

## Task 8: Цикл агента и диспетчер инструментов

**Files:** Create `Harness/Agent/RunContext.cs`, `AnalysisAgent.cs`, `ToolDispatcher.cs`, `tools/harness-tests/AgentTests.cs`, `ScriptedProvider.cs`.

**Interfaces:** `RunContext(string runId,TimeProvider clock,CancellationToken ct)` owns deadline, Results, Interpretation, candidates and selections; `AnalysisAgent(IModelProvider,ToolDispatcher,TimeProvider)`; `Task<AnalysisResponse> RunAsync(AnalysisRequest,CancellationToken)`; `ToolDispatcher(AnalyticsCatalog,IEmployeeResolver,IQueryExecutor,Func<string,JsonElement,CancellationToken,Task<QueryResult>> dashboard)`; `Task<object> ExecuteAsync(ModelAction,RunContext,CancellationToken)`. ScriptedProvider принимает ModelAction[] и считает вызовы, записывает feedback, не обращается к сети.

- [ ] **1. RED:** ScriptedProvider предлагает report без SQL → validation error, следующий вызов не исправляет → после двух repair возвращается incomplete, не completed. Второй сценарий: find→execute_sql→submit_report, два источника, report ссылается на первый; ожидается completed.

```csharp
Check.True(response.Status != "completed");
Check.True(response.Report == null);
Check.True(response.Steps.Any(s => s.Error?.Code == "unknown_result"));
Check.True(provider.CallCount <= 8);
```

Переменные response/provider в тесте создаются через конструкторы выше; canned QueryResult возвращает fake IQueryExecutor, fake IEmployeeResolver содержит явно заданные candidates. Для времени использовать подставной TimeProvider и cancellable delays, не ждать 120 реальных секунд в offline-тестах.

- [ ] **2. Запуск agent → FAIL.**
- [ ] **3. Реализация цикла:** начать monotonic deadline, snapshot config в hosting, определить/проверить interpretation. ModelAction валидировать до ExecuteAsync. К сообщениям добавлять исходный AssistantMessage и Feedback; при ошибке тоже valid function feedback. Использовать один общий repair counter для protocol/report (max2), общий max8 включает retries модели; SQL corrections потребляют шаги и время. Финального свободного LlmChat после исчерпания нет. Один вызов модели → не более одного действия. Отдельное execution error не сохраняет предыдущий resultId как будто это новый результат.

```text
protocol failure -> structured feedback -> retry if budget permits
valid tool -> execute -> store result -> feedback(ResultPage)
valid report -> validator -> completed only on success
ambiguous entity -> needs_clarification with actual candidates
deadline/step budget -> incomplete with existing results and warning
provider unavailable before useful results -> failed
resolved entity + empty data -> no_data (не fabricated zero)
```

- [ ] **4. Interpretation:** dispatcher обрабатывает set_context до запросов данных. `kind=all` требует отсутствия остальных полей; `kind=months` требует months от 1 до 1200 и отсутствия From/To; `kind=range` требует двух ISO8601 дат с offset, From<To и отсутствия Months. Для months вычислить To=clock.GetUtcNow(), From=To.AddMonths(-Months); asOf фиксируется один раз при создании run. Границы включительно From, исключительно To; значение 12 не превращать в календарный год. Относительный период фиксировать от времени начала run и передавать `from/to/asOf` server parameters. Для известного metricId сервер задаёт unit/dateField; metricId неизвестен → запрос каталога/уточнение. При явно указанном периоде произвольный SQL обязан ссылаться на `@from/@to` для назначенного временного поля; консервативная проверка формы, не обещание доказать произвольный SQL. Неизвестная форма → clarification/validation, не молчаливая замена. Проверяемое совпадение смысла генерируемого SQL с метрикой обеспечивать сценариями; ограничения честно показывать в документации. Tests: months=12 на конце месяца, range timezone offsets, invalid/reversed range, повтор set_context после результата, execute_sql до контекста. Для произвольной метрики, отсутствующей в каталоге, использовать явно описанный generic_query с unit «строка результата» и без обещания бизнес-KPI; требовать явную подпись определения и проверяемые источники. Новые бизнес-определения модель не записывает в каталог автоматически.
- [ ] **5. Selections:** каждый переданный ID снова читается resolver.GetAsync; не принимать произвольный resultId из тела запроса. При нескольких candidates пользователь выбирает; не позволять clarify выдумать candidateIds. Если запрос про двух сотрудников, одна найденная строка не превращается в сравнение двух. Нет автоматического доступа к истории другого run.
- [ ] **6. GREEN:** бюджет/repair, cancel ожиданий, два run не видят results друг друга, tool injection из текста результата, duplicate native call, malformed JSON, wrong person ID, ambiguous/absent person, Qwen path, graceful provider failures. Commit `feat(harness): orchestrate evidence-backed analysis runs`.

## Task 9: HTTP и интеграция старых билдеров

**Files:** Create `Harness/Hosting/AnalysisEndpoint.cs`, `Program.Harness.cs`, `tools/harness-tests/HostingTests.cs`. Modify `Program.cs` class → partial class, router и конфигурация; `armgov-standalone.csproj` включение статики каталога.

**Interfaces:** `AnalysisEndpoint.HandleAsync(HttpListenerContext,CancellationToken)`; `Program.Harness.cs` создаёт snapshot settings и зависимости; Dashboard adapter передаётся в ToolDispatcher delegate выше. Старые приватные методы остаются доступными через partial Program, не делать Program целиком public API.

- [ ] **1. RED HTTP tests:** POST /api/ai/analysis с fake agent; GET→405; non-local→403; body>64 KiB→413; malformed→400; invalid selection→400; unknown JSON field→400; normal response camelCase. Заголовки проверять до дорогостоящей работы. Content-Type application/json; POST без body не вызывает модель.

```python
status, body = request("POST", "/api/ai/analysis", {"question":"Сравни", "selections":[]})
assert status == 200
assert body["status"] in {"completed","needs_clarification","no_data","incomplete","failed"}
assert "runId" in body and "steps" in body
```

В C# HostingTests использовать loopback HttpListener на свободном test port и HttpClient; request выше описывает wire expectation для будущего analysis_smoke.py. Non-local/Host проверку дополнительно вынести в чистую функцию, чтобы не зависеть от сетевого окружения.

- [ ] **2. Реализация:** router локальную проверку берёт из IsLocalCall, а не Prefix. `HandleAsync(...).GetAwaiter().GetResult()` допускается только на границе существующего ThreadPool request handler; внутри харнеса async. Method/cors/origin не расширять. По возможности same-origin проверка Origin для изменяющих состояние HTTP-вызовов; `localhost:5080`/фактический host port, не wildcard. Исключения клиенту без connection string/token/raw HTTP headers.
- [ ] **3. Готовые метрики:** нельзя просто `Task.Run(ToolCall)` с timeout — это оставит запросы к RX выполняться после отмены. Ввести CT-aware overloads только используемых в новом dispatcher билдеров; старые callers передают CancellationToken.None. Общий server-owned query helper регистрирует NpgsqlCommand.Cancel, задаёт таймаут от остатка, закрывает reader/connection в finally. Snapshot connection/параметров обязателен; исключить shared mutable period/settings между run. Если конкретный билдер нельзя безопасно адаптировать в этой задаче — не объявлять его в new ToolDefinitions до адаптации и не считать задачу законченной, пока все девять заявленных инструментов не покрыты.
- [ ] **4. Старые /api/ai/sql/check/run также используют общий SqlGuard/SqlScopePolicy/слоты; response shape сохраняется. /summary/chat/explain используют прежний протокол, config/test остаётся рабочим. Новые поля конфигурации, если вводятся для отдельной analytics DB role, маскировать по существующей схеме; fallback на текущую DB отмечать как local prototype, не автоматически признанную read-only роль.
- [ ] **5. GREEN:** hosting + all; проверить отсутствующую настройку, смену модели в середине run (run продолжает snapshot), защищённую статику `/config.json`, disabled feature doesn't break dashboard. Commit `feat(api): expose validated analytics endpoint`.

## Task 10: Canvas и уточнение личности

**Files:** Create `analysis.js`, `tests/analysis.test.js`. Modify `index.html` askCanvas/renderCanvasBody, `style.css`, `armgov-standalone.csproj`, whitelist static route Program.cs.

**Interfaces:** browser functions `analysisViewModel(response)` → pure object; `renderAnalysis(response)` → escaped HTML; `analysisSelectionRequest(originalQuestion,mention,employeeId,selections)` → AnalysisRequest. Для legacy charts преобразование `toChartDataset(storedResult,block)` сохраняет source/resultId, cols/rows/truncated; client не пересчитывает подтверждённые facts.

- [ ] **1. RED JS:** использовать существующий tests/charts.test.js подход загрузки plain script без DOM.

```javascript
const assert = require('node:assert/strict');
const fs = require('node:fs');
const api = new Function(fs.readFileSync('analysis.js','utf8') +
  '\nreturn {analysisViewModel,renderAnalysis,analysisSelectionRequest};')();
const html = api.renderAnalysis({runId:'x',status:'failed',report:null,datasets:[],steps:[],
  warnings:[],error:{code:'bad_report',message:'<script>alert(1)</script>'}});
assert(!html.includes('<script>'));
assert(!html.includes('chart.from'));
```

- [ ] **2. Запуск node tests/analysis.test.js → FAIL.** Ещё fixtures: completed 2 blocks, no_data, clarification 2 employees, incomplete with table, unsafe integer, partial shares.
- [ ] **3. Реализация:** askCanvas POST новый route; сохранять entire response в CANVAS history. Для clarification отображать проверенные ФИО+подразделение и кнопки выбора; отправлять исходный question+накопленные selections. Сервер остаётся источником проверки IDs. verifiedText, title, labels, errors, Commentary escape; SQL показывать в раскрываемом блоке escaped pre, не HTML. Commentary отдельной подписью, не рядом с verified KPI без маркировки. Для no_data показать отсутствие, не нулевой график. Для incomplete показывать предупреждение и только таблицы с явным «частичный результат».
- [ ] **4. Подключить analysis.js после charts.js, включить в CopyToOutputDirectory и static allowlist. Старые страницы продолжают получать esc из charts.js. UI подпись «Ход анализа»; старые thinkBlock вызовы не ломать. История вопросов — отдельные визуальные элементы с пробелами/отступами, длинные подписи обрезать без склейки. Данные периодов и определения показывать над графиками.
- [ ] **5. GREEN:** analysis/charts, API smoke статических файлов. Ручная проверка в браузере: старый обзор, новый отчёт, выбор сотрудника, смена вида, возврат из истории, ошибка модели, длинное ФИО, светлая/тёмная тема. Перезапускать только собственный server process после проверки пути; обновить статику в bin. Commit `feat(canvas): show verified reports and entity clarification`.

## Task 11: Сквозные сценарии, документация и итоговое ревью

**Files:** Create `tests/analysis_smoke.py`, `tests/fixtures/analysis-cases.json`, `docs/technical/analytics-harness-v2.md`; update tests/smoke.py для intentional scope denials, README.md и CLAUDE.md только в изменённых разделах.

**Interfaces:** `python tests/analysis_smoke.py http://localhost:5080 --live` выполняет живые сценарии, возвращает nonzero при FAIL и отдельный code 2 при BLOCKED. Без --live проверяет HTTP/статику и сценарии fake provider только в test-host, не подменяет production модель автоматически.

- [ ] **1. Зафиксировать fixture cases** с машинными ожиданиями, не эталонным текстом модели:

```json
[
 {"id":"unbacked_numbers","question":"Сравни поручения двух сотрудников",
  "assertions":["no_completed_without_result","no_unknown_result_reference"]},
 {"id":"personal_count","question":"Сравни количество поручений у Ивана Иванова и Босова Александра за 12 месяцев",
  "assertions":["resolved_ids_or_clarification","root_deduplication","period_matches","same_source_in_chart_and_facts"]},
 {"id":"departments","question":"Покажи просрочку по подразделениям",
  "assertions":["dashboard_metric_used","dataset_not_empty_or_explicit_no_data"]},
 {"id":"empty_period","question":"Покажи поручения за период без данных",
  "assertions":["no_data_not_fabrication"]}
]
```

- [ ] **2. Независимый контроль:** для employee count на тестовой БД создать root с двумя assignment одному исполнителю и проверить count=1. В живом тесте получить подтверждённые IDs, взять точные from/to из ответа и выполнить контрольный SELECT отдельным кодом, не копией SQL модели. Логи содержат IDs тестовой фикстуры; реальные ФИО и сырые данные не коммитить.

Скелет контрольного SQL (параметры и NoticeList передаёт тест, не модель):

```sql
select p.id as employee_id, p.name, count(distinct root.id) as instructions
from public.sungero_core_recipient p
left join (
  public.sungero_wf_assignment a
  join public.sungero_wf_task t on t.id = a.task
  join public.sungero_wf_task root on root.id = coalesce(nullif(t.maintask,0),t.id)
) on a.performer = p.id
 and root.discriminator = 'c290b098-12c7-487d-bb38-73e2c98f9789'
 and root.created >= @from and root.created < @to
 and not (a.discriminator = any(@notice_ids))
where p.id = any(@employee_ids)
group by p.id,p.name order by p.id
```

До живого исполнения сверить тип discriminator (uuid/текст) и соответствующий NpgsqlDbType; отсутствие notice list — BLOCKED, не расчёт с пустым исключением. Эта формула считает персональные поручения по existing assignment.performer, не ответственность/соисполнительство, что указывается в interpretation. Если живая схема показывает иной root linkage, остановить эту проверку, задокументировать расхождение с существующим BuildLeaderTasks, не подгонять эталон под ответ модели.

- [ ] **3. Выполнить проверки один раз после последних правок:** build; all offline; db; charts; analysis JS; existing smoke; analysis live. Для модели минимум три повторения основного вопроса (без прошлой истории) и три вопроса на разные разрезы. При неоднозначном ФИО пройти уточнение; no_data на стенде не заменяет успешный fixture test с данными. Прохождение API доступности не означает прохождение semantic evaluation.
- [ ] **4. Документация:** новые route/DTO, локальный запуск, прочтение run trace, лимиты и partial states, отличие role isolation от read-only transaction. Список административных предпосылок роли и read-only диагностический SELECT разрешений; не выполнять DDL RX и не прописывать секреты. Обновить пример GigaChat native flow; старую JSON поддержку назвать Qwen compatibility. Сохранить статус промышленной изоляции отдельным пунктом BLOCKED при непроверенных правах.
- [ ] **5. Review всего изменения:** закрыты ли пять Review Focus, нет ли обхода store/report validation, mutation SQL в production, незавершённых background SQL, plaintext токенов, неожиданной смены метрик, fallbacks с выдуманными цифрами. Reviewer использует свежий контекст и spec+plan+diff; результаты ревью фиксируются, критические/важные замечания исправляются с тестом.
- [ ] **6. Commit:** `test(harness): verify evidence-backed analytics end to end`. Итог владельцу: что выполнено, команды и результаты, заблокированные проверки, ограничения. Не пушить, не деплоить и не объявлять успех живого сценария без фактического вывода. Выполнить выбранный finishing skill только в пределах разрешённого действия; текущая передача не является разрешением merge в mvp-prod.

## Самопроверка плана и покрытие спецификации

| Спецификация | Задачи |
|---|---|
| Цель, границы модулей и совместимость | 1, 6, 9, 10 |
| Read-only, лимиты, общий бюджет и отмена | 2, 3, 5, 8, 9 |
| Каталог, метрики и личности | 4, 8, 11 |
| Native GigaChat, Qwen, общий протокол | 5, 6, 8 |
| Все результаты, ID, preview и truncation | 2, 3, 8 |
| Проверка отчёта, facts, error states | 7, 8, 10 |
| HTTP/Canvas/уточнения | 9, 10 |
| Offline/live/regression и подтверждение значений | 1–11 |
| Права эксплуатационной роли | 3, 11: диагностика и административная зависимость; изменение RX исключено |
| AST, persistence, export | Явно вне текущего плана |

До передачи проверены: наличие red/green шагов для каждой задачи; определение общих DTO и interfaces до потребителей; failure cases Review Focus распределены по тестам; ограничения последующих поставок не превращены в скрытые обязанности первой. Примеры кода в плане задают тестируемые контракты и ключевую логику, а не заменяют реализацию всех файлов. Исполнитель обязан показать фактически падающий тест до правки и проходящий после неё.

## Финальная проверка готовности к передаче

- [ ] Владелец рассмотрел этот план и подтвердил объём первой поставки.
- [ ] При передаче указан метод выполнения: subagent-driven (рекомендован) либо native.
- [ ] Исполнитель получил спецификацию вместе с планом и доступ к коду от указанного baseline.
- [ ] Исполнитель понимает, что здесь создан только план; новая интеграция GigaChat ещё не реализована.
