# Частые запросы к БД Directum RX (через `tools/dbq`)

Запуск: `"C:/dotnet10/dotnet.exe" run --project tools/dbq -- "<SQL>"` (см. также `tools/dbq/explain.sh`).
БД берётся из `config.json` (сейчас `DirRX261OGVGenAI` @ 192.168.52.18). Переопределить: `--db <Database>`.

## Диагностика схемы / объёмов
```sql
-- размеры ключевых таблиц
select relname, n_live_tup from pg_stat_user_tables
where relname in ('sungero_wf_task','sungero_wf_assignment','sungero_core_recipient')
order by n_live_tup desc;

-- индексы таблицы
select indexname, indexdef from pg_indexes where tablename = 'sungero_wf_assignment' order by indexname;

-- типы колонок (важно: status=citext, discriminator=uuid)
select table_name, column_name, data_type, udt_name from information_schema.columns
where table_name in ('sungero_wf_task','sungero_wf_assignment')
  and column_name in ('status','discriminator','deadline','completed','created','task','subject')
order by table_name, column_name;
```

## Типы задач (discriminator)
```sql
select discriminator, count(*) n from sungero_wf_task group by discriminator order by n desc;
```
Известные: `c290b098-…`=Поручения, `4ef03457-…`=Обращения граждан, `83f2a537-…`=осн. тип задач.

## Производительность
```bash
# прогнать EXPLAIN ANALYZE по запросам
tools/dbq/explain.sh "select count(*) from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task where t.discriminator='4ef03457-8b42-4239-a3c5-d4d05e61f0b6' and a.status='InProcess'"
```

## Заметки по оптимизации
- `status` — `citext`. Сравнивать как `status='InProcess'` (НЕ `status::text='InProcess'` — каст отключает индексы по статусу, напр. partial `idx_task_status_only`).
- `discriminator` — `uuid`. Сравнение со строковым литералом индекс использует.
- НПА-фильтр `subject ILIKE '%…%'` — seq scan (gin_trgm-индекс построен на `sungero_system_getnormalizeddisplayvalue(subject)`, а не на `subject`).
