# Task 7 — отчёт о выполнении

Дата: 2026-09-19  
Ветка: `feat/evidence-harness-v2`  
Коммит: `feat(harness): render reports from verified result references`

## Результат

Добавлены `ReportValidator` и `ReportRenderer`. Блоки отчёта проверяются
против сохранённых результатов текущего запуска: типы, колонки, фильтры,
форма визуализации и полнота набора. Полные рейтинги, доли, линии и KPI
не строятся по усечённым данным; для таблиц и ограниченных bars добавляется
серверная отметка о частичных данных.

Факты вычисляются сервером только из проверенных `CellRef` операциями
`cell`, `sum`, `difference` и `ratio` с точной арифметикой `decimal`.
Неизвестные результаты/колонки, усечённые ячейки и переполнение отклоняются;
деление на ноль возвращает `null` и предупреждение.

Числа в подтверждённом тексте допускаются только через известные
`{{factId}}`. Заголовок, подтверждённый текст и комментарий HTML-экранируются,
а комментарий явно помечается как непроверенный.

## TDD-свидетельства

1. RED: набор `reports` завершился CS0103 — `ReportValidator` и
   `ReportRenderer` отсутствовали.
2. GREEN: `reports` — **PASS 10 / FAIL 0**.
3. Покрыты реальные fixture cells 7/3, точные difference/ratio, неизвестные
   ссылки, altered interpretation, overflow, zero divisor, invalid filters,
   partial shares/ranking, literal digits, duplicate facts и HTML injection.

## Проверки

- `reports`: **PASS 10 / FAIL 0**;
- `all` (без DB и внешней сети): **PASS 85 / FAIL 0**;
- `dotnet build -c Release`: **PASS**, 0 warnings / 0 errors;
- IDE diagnostics: ошибок нет.
