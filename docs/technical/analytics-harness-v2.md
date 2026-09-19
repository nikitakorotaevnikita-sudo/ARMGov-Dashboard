# Analytics harness v2 — технический контракт

Дата: 2026-09-19. Статус: первая сквозная поставка (evidence-backed analytics).  
Спека: [2026-09-19-evidence-backed-analytics-harness-design.md](../superpowers/specs/2026-09-19-evidence-backed-analytics-harness-design.md).

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

Старый `POST /api/ai/sql` сохранён как **Qwen compatibility** (текстовый JSON-протокол). Новый контракт отчётов на него не распространяется.

### GigaChat native flow

1. Сервер передаёт `functions` (JSON Schema инструментов).
2. Читается `message.function_call` + сохраняется `functions_state_id`.
3. Результат инструмента возвращается сообщением `role=function`.
4. OAuth и кэш токена — только на сервере, в бюджет запуска.

Qwen: тот же набор внутренних действий через текстовый JSON (`QwenProvider`).

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

```bash
python tests/analysis_smoke.py http://localhost:5080 --live
```

Тестовая БД (read-only integration, не RX):

```bash
docker compose -f db/compose.yml up -d
$env:ARMGOV_TEST_DB = "Host=127.0.0.1;Port=55439;Database=harness_test;Username=harness_reader;Password=..."
dotnet run --project tools/harness-tests -c Release -- db
```

## Чтение run trace

В UI — блок «Ход анализа» (`steps`): номер, инструмент, статус, ms, `resultId`, код ошибки.  
Скрытые рассуждения модели не являются контрактом.

Типичные коды ошибок шага: `unknown_result`, `missing_context`, `invalid_report`, `provider_protocol_error`.

## Лимиты и partial states

| Лимит | Значение |
|---|---|
| Бюджет запуска | 120 с |
| Вызовы модели | ≤ 12 (≤ 4 repair) |
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
| `tools/harness-tests/` | Offline unit/integration runner |
