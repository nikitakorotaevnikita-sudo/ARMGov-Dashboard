# Analytics harness v2 — технический контракт

Дата: 2026-09-21. Статус: управляемый Qwen workflow включён по умолчанию.
Спека: [2026-09-20-qwen-maf-analytics-workflow-design.md](../superpowers/specs/2026-09-20-qwen-maf-analytics-workflow-design.md).

## Маршруты и DTO

### `POST /api/ai/analysis`

Локальный вызов (тот же контур доступа, что у `/api/ai/sql/run`). Canvas «Аналитика по запросу» использует этот маршрут.

**Request (`AnalysisRequest`):**

```json
{
  "question": "Сравни количество поручений …",
  "selections": [
    { "mention": "Иванов", "employeeId": 12345 }
  ]
}
```

- `question` — 1…4000 символов.
- `selections` — до 10 подтверждённых сущностей после уточнения; сервер повторно проверяет ID.

**Response (`AnalysisResponse`, camelCase):**

| Поле | Описание |
|---|---|
| `runId` | Идентификатор запуска |
| `status` | `completed` · `needs_clarification` · `no_data` · `incomplete` · `failed` |
| `report` | Проверенный отчёт (`RenderedReport`) или `null` |
| `datasets` | Проекции сохранённых результатов (`StoredResult[]`) |
| `steps` | Ход анализа (`AgentStep[]`: tool, status, elapsedMs, resultId, error) |
| `elapsedMs` | Длительность |
| `warnings` | Предупреждения (усечение, частичный результат) |
| `clarification` | `{ question, candidates[] }` при неоднозначном ФИО |
| `error` | `HarnessError` при `failed` |

Старый `POST /api/ai/sql` сохранён как legacy compatibility (текстовый JSON-протокол). Новый контракт отчётов на него не распространяется.

## Production workflow и rollback

`Analytics.Engine` выбирает runner один раз в начале запроса вместе с endpoint, model, token и
connection string:

- `workflow` (по умолчанию) — `AnalysisWorkflow` на Microsoft Agent Framework;
- `legacy` — прежний `AnalysisAgent` для быстрого отката;
- любое другое значение — `failed` / `invalid_analytics_engine` до обращения к модели.

Имя модели не участвует в выборе runner. Оба engine (`workflow` и `legacy`) работают
только с `Qwen/Qwen3.8-27B` через Ario HTTP-адаптер. Снимок GigaChat, другого
провайдера или неизвестной модели завершает запрос `failed` /
`unsupported_analytics_model` до OAuth/HTTP и до запуска runner. Тихого отката на
GigaChat или на другую модель нет. Пресеты GigaChat в бэк-офисе остаются для чата
дашборда, не для `/api/ai/analysis`. Пакет
`Microsoft.Agents.AI.Workflows` закреплён на `1.21.0` в `packages.lock.json`.

Стадии workflow: `prepare`, `plan`, `resolve_entities`, `dashboard_metric` или `execute_sql`,
`draft_report`, `validate_report`. Модель создаёт типизированный план, SQL и черновик отчёта;
сервер управляет переходами, разрешает сущности, связывает период и ID сотрудников, выполняет
не более одного запроса данных и проверяет ссылки отчёта на реальные `resultId`.

Legacy runner — тот же Qwen endpoint/модель, что и workflow, на прежнем
`AnalysisAgent`. Это откат архитектуры, не fallback на GigaChat.

Граница данных не изменилась: `SqlGuard` + `SqlScopePolicy`, `BEGIN READ ONLY`,
`statement_timeout`, каталог разрешённых отношений, максимум 200 строк и evidence binding через
`ResultStore` / `ReportValidator`.

## Локальный запуск

```powershell
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
dotnet build -c Release
bin\Release\net10.0\armgov-standalone.exe
```

Проверки:

```bash
dotnet run --project tools/harness-tests -c Release -- all
python tests/analysis_smoke.py http://localhost:5080
python tests/smoke.py http://localhost:5080
node tests/charts.test.js
node tests/analysis.test.js
```

Живые сценарии (модель + RX):

```powershell
# Для каждого engine: изменить Analytics.Engine, перезапустить свежую Release-сборку,
# затем выполнить ровно 3 × 10 запросов.
python tests/analysis_smoke.py http://localhost:5080 --live --engine legacy --repetitions 3 --artifact artifacts/analytics-eval/legacy.json
python tests/analysis_smoke.py http://localhost:5080 --live --engine workflow --repetitions 3 --artifact artifacts/analytics-eval/workflow.json
```

Оба прогона используют один endpoint и один снимок БД. JSON-артефакты содержат только case ID,
status, elapsed ms, безопасные поля шагов, result IDs и effective SQL. Строки, заголовки,
provider payload, токены и connection string не сохраняются. Код выхода `1` означает
семантическое падение; `2` разрешён только при недоступности provider/RX.

Self-contained проверка поставки:

```powershell
dotnet restore --locked-mode
dotnet publish -c Release -r win-x64 --self-contained true --no-restore -o .\artifacts\workflow-publish
```

В publish должны присутствовать `armgov-standalone.exe`, `catalog.json`, статика и сборки
Microsoft Agent Framework.

Тестовая БД (read-only integration, не RX):

```bash
docker compose -f db/compose.yml up -d
$env:ARMGOV_TEST_DB = "Host=127.0.0.1;Port=55439;Database=harness_test;Username=harness_reader;Password=..."
dotnet run --project tools/harness-tests -c Release -- db
```

## Чтение run trace

В UI — блок «Ход анализа» (`steps`): номер, стабильное имя шага, статус, ms, `resultId`, код ошибки.
Скрытые рассуждения модели не являются контрактом.

Стабильные имена: `prepare`, `plan`, `resolve_entities`, `dashboard_metric` / `execute_sql`,
`draft_report`, `validate_report`. Типичные коды ошибок: `unknown_result`, `missing_context`,
`invalid_report`, `provider_protocol_error`, `invalid_analytics_engine`,
`unsupported_analytics_model`.

## Лимиты и partial states

| Лимит | Значение |
|---|---|
| Бюджет запуска | 120 с |
| Вызовы модели | ≤ 5: plan, SQL, report, ≤1 SQL repair, ≤1 report repair |
| Строк на результат | 200 |
| Колонок | 60 |
| Байт на результат / запуск | 1 МиБ / 4 МиБ |
| Одновременных SQL | 2 |
| `statement_timeout` | 10 с |
| Тело POST | 64 KiB |

Partial: `incomplete` (бюджет/отмена/неисправленный отчёт), усечение строк/колонок/ячеек с явным `truncation` и предупреждением. `no_data` ≠ `failed`.

## Read-only transaction vs role isolation

**Read-only transaction** (первая поставка): каждый SQL харнесса — `BEGIN READ ONLY`, `SET LOCAL statement_timeout`, фиксированный `search_path`. Блокирует `INSERT`/`nextval` даже при широких правах учётки прототипа.

**Role isolation** (промышленная эксплуатация): отдельная роль БД с `GRANT SELECT` только на объекты каталога, без `CREATE`/`WRITE`, не superuser. **BLOCKED:** права эксплуатационной роли на стенде RX не верифицированы этой поставкой; приложение не выполняет DDL/GRANT в RX.

Диагностика прав (ручная, read-only):

```sql
select rolname, rolsuper, rolcreaterole, rolcreatedb
from pg_roles where rolname = current_user;
select table_schema, table_name, privilege_type
from information_schema.table_privileges
where grantee = current_user and table_schema = 'public'
order by 1, 2;
```

## SqlScopePolicy и smoke

`/api/ai/sql/check` и `/api/ai/sql/run` используют общий `SqlGuard` + `SqlScopePolicy`. Запросы к `pg_settings`, `information_schema` и прочим объектам вне allowlist каталога **намеренно отклоняются** — справка по схеме через серверные инструменты, не произвольный SQL модели.

## Контрольный SELECT (personal count)

Персональное количество поручений: `count(distinct root.id)` по `assignment.performer`, дедупликация корня `coalesce(nullif(maintask,0), id)`, фильтр `root.discriminator` поручений, период по `root.created`, исключение Notice-типов. Не смешивать с соисполнительством/ответственностью.

Скелет независимой проверки (параметры задаёт тест, не модель) — см. `tests/analysis_smoke.py` и `DbTests.RootDeduplicationCountsDistinctInstructions` на `harness_test` (два assignment одному исполнителю на одном root → count=1).

## Файлы

| Путь | Назначение |
|---|---|
| `Harness/` | Модули харнесса |
| `Program.Harness.cs` | HTTP `/api/ai/analysis`, wiring |
| `analysis.js` | Рендер verified report на Canvas |
| `tests/analysis_smoke.py` | Offline/live HTTP + semantic cases |
| `tests/fixtures/analysis-cases.json` | Сценарии с machine assertions |
| `tests/fixtures/analysis-workflow-cases.json` | 10 сравнительных live-сценариев |
| `tools/harness-tests/` | Offline unit/integration runner |
