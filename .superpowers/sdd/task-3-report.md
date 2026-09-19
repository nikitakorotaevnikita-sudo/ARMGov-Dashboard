# Task 3 — отчёт о выполнении

Дата: 2026-09-19  
Ветка: `feat/evidence-harness-v2`

## Результат

Реализованы публичные `SqlGuard`, `SqlScopePolicy` и асинхронный
`ReadOnlyExecutor`. Старый `Program.SqlCheck` стал обёрткой общей политики,
а старый исполнитель использует общий процессный `SemaphoreSlim(2, 2)`.
Добавлены изолированный PostgreSQL 17 compose и DB-тесты, не читающие
`config.json`.

## TDD-свидетельства

1. Characterization до переноса: вредоносные строки из smoke вызвали старый
   `Program.SqlCheck` через reflection — `PASS=3 FAIL=0`.
2. RED новых контрактов: сборка `sql` завершилась 16 ошибками CS0103/CS0246,
   поскольку три новых класса отсутствовали.
3. RED политики операторов: `PASS=8 FAIL=1` на пользовательском `#=#`.
4. GREEN offline SQL: `PASS=9 FAIL=0`.

## Покрытие

- deny-list сохранён, включая разрезанные комментариями и кавыченные имена;
- строки, E-/dollar-строки, комментарии и завершающий `;`;
- whitelist отношений в CTE, вложенных запросах и UNION;
- неизвестные relation/function/operator, table-valued function, recursive CTE,
  schema-qualified function/type отклоняются;
- разрешённые агрегаты, окна, scalar casts и unqualified `public`;
- параметры только scalar/null, зарезервированные имена серверные;
- отмена ожидания слота без утечки;
- DB suite содержит read-only transaction, SELECT-only role, 201-ю строку,
  три запроса на двух слотах и отмену тяжёлого чтения.

## Проверки

- `sql`: **PASS 9 / FAIL 0**;
- `results`: **PASS 9 / FAIL 0**;
- `contracts`: **PASS 14 / FAIL 0**;
- `dotnet build -c Release`: **PASS**, 0 warning / 0 error;
- `db`: **BLOCKED** — Docker daemon недоступен и `ARMGOV_TEST_DB` не задан.
  Никакое подключение к RX и чтение `config.json` не выполнялось.

## Ограничения

- Политика — консервативный scanner разрешённого подмножества, не PostgreSQL AST
  и не самостоятельная security sandbox.
- Отмена Open/Read, права роли и конкурентность подготовлены в DB suite, но
  фактически не подтверждены из-за BLOCKED окружения.
