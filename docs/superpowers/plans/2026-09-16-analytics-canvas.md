# Страница «Аналитика по запросу» — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Руководитель задаёт вопрос в свободной форме, страница показывает график по фактическому результату ИИ-харнесса и даёт переключить вид представления.

**Architecture:** Харнесс дополняется полем `dataset` — колонки с типами и строки как есть, взятые из фактического результата (SQL-шаг, ответ инструмента или предзагруженная сводка). Модель может лишь указать, какой массив рисовать, но не переписывает значения. Клиент по типам колонок определяет допустимые виды и рисует их своими SVG-примитивами, вынесенными в новый `charts.js`.

**Tech Stack:** .NET 10, `System.Net.HttpListener`, Npgsql, `System.Text.Json`; ванильный JS в `index.html` и новом `charts.js`; тесты — `tests/smoke.py` (Python) и новый `tests/charts.test.js` (Node 24).

**Spec:** `docs/superpowers/specs/2026-09-16-analytics-canvas-design.md`

## Global Constraints

- **Только `SELECT`.** Приложение никогда не пишет в БД RX. Страница не добавляет новых путей к данным — использует существующий харнесс.
- **Метрики должны сходиться друг с другом.** Цифра на графике обязана совпадать с цифрой в тексте ответа и на дашборде. Обеспечивается тем, что источник один: фактический результат прогона. Модель не переписывает значения.
- **Секретов в коде нет.** Пароль БД и токен LLM живут только в `config.json` рядом с exe.
- **Уведомления исключаются из статистики** (типы `*Notice`/`*Notification`) — это уже внутри сборщиков и словаря схемы.
- Графики рисуются своими SVG-примитивами, без внешних библиотек: интернета в контуре заказчика нет.
- Палитра — только токены из `tokens.css`. Новых цветов не вводить.
- Язык интерфейса, кода, комментариев и коммитов — русский.
- Перед сборкой в новой сессии PowerShell: `$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`.
- Прототип запущен на `http://localhost:5080/`. Перед `dotnet build -c Release` остановить процесс `armgov-standalone`, после сборки запустить снова `bin\Release\net10.0\armgov-standalone.exe` — иначе MSB3027 (файл занят), это не ошибка кода.
- `index.html`, `style.css`, `charts.js` читаются сервером на каждый запрос, но файл должен быть обновлён и в `bin\Release\net10.0\`.
- Прогон: `python tests/smoke.py http://localhost:5080`. На старте работ — `PASS=161 FAIL=0`.
- Кириллица в query-параметрах через curl в bash на Windows коверкается — для ручных проверок использовать `python urllib`.

## Структура файлов

| Файл | Ответственность |
|---|---|
| `Program.cs` (правка) | `SqlRun` отдаёт типы колонок; `DatasetFromJson`, `DatasetFromSql`; поле `dataset` в ответе агента; описание `chart` в промпте; отладочный эндпоинт `/api/ai/dataset/probe` |
| `charts.js` (создать) | `pickViews` — выбор допустимых видов; пять рендереров и `svgGroupedBars`; переехавшие `esc`, `rect`, `txt`, `svgBars`, `svgSpark`, `svgStack`, `legend` |
| `index.html` (правка) | Шестой экран «Аналитика по запросу»: ввод, холст, переключатель, история; удаление переехавших функций |
| `style.css` (правка, Task 7) | Одно правило: активный чип переключателя `.expl.on` |
| `armgov-standalone.csproj` (правка) | Копирование `charts.js` в вывод сборки |
| `tests/charts.test.js` (создать) | Правила выбора вида — чистая функция |
| `tests/smoke.py` (правка) | Контракт датасета и отладочный эндпоинт |

---

### Task 1: `SqlRun` отдаёт типы колонок

Клиент выбирает вид по типам колонок, а сейчас исполнитель возвращает только имена. Тип должна ставить база, а не догадки по значениям.

**Files:**
- Modify: `Program.cs` — функция `SqlRun`, эндпоинт `/api/ai/sql/run`, разбор результата в цикле агента
- Test: `tests/smoke.py`

**Interfaces:**
- Consumes: ничего
- Produces:
  - `static (List<string> cols, List<string> types, List<object[]> rows, int ms, bool truncated) SqlRun(string effective, int maxRows = SqlMaxRows)`
  - `static string ColType(Type t)` → `"text" | "number" | "date" | "bool"`
  - `/api/ai/sql/run` дополнительно отдаёт `types`

- [ ] **Step 1: Написать падающую проверку**

В `tests/smoke.py`, в секцию харнесса (рядом с существующими проверками `/api/ai/sql/run`):

```python
d = sqlrun("select 'текст'::text as t, 42 as n, now() as d, true as b")
check("исполнитель отдаёт типы колонок",
      d.get("types") == ["text", "number", "date", "bool"], str(d.get("types")))
```

- [ ] **Step 2: Прогнать и убедиться, что проверка падает**

```bash
python tests/smoke.py http://localhost:5080
```

Ожидается: одна новая `[FAIL]` — поля `types` в ответе нет.

- [ ] **Step 3: Добавить функцию отображения типа**

В `Program.cs`, в регион «ИИ-харнесс», рядом с `Cell`:

```csharp
    // Тип колонки для клиента: по нему выбирается допустимый вид визуализации.
    // Источник истины — метаданные Npgsql, а не догадки по значениям.
    static string ColType(Type t)
    {
        if (t == typeof(bool)) return "bool";
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return "date";
        if (t == typeof(short) || t == typeof(int) || t == typeof(long) ||
            t == typeof(float) || t == typeof(double) || t == typeof(decimal))
            return "number";
        return "text";
    }
```

- [ ] **Step 4: Расширить `SqlRun`**

Изменить сигнатуру и заполнение колонок:

```csharp
    static (List<string> cols, List<string> types, List<object[]> rows, int ms, bool truncated) SqlRun(
        string effective, int maxRows = SqlMaxRows)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cols = new List<string>(); var types = new List<string>();
        var rows = new List<object[]>(); bool more = false;
        // … существующее открытие соединения и транзакции без изменений …
        using (var cmd = new NpgsqlCommand(effective, c, tx))
        using (var rd = cmd.ExecuteReader())
        {
            int n = Math.Min(rd.FieldCount, 60);
            for (int i = 0; i < n; i++) { cols.Add(rd.GetName(i)); types.Add(ColType(rd.GetFieldType(i))); }
            // … существующее чтение строк без изменений …
        }
        tx.Rollback();
        return (cols, types, rows, (int)sw.ElapsedMilliseconds, more);
    }
```

- [ ] **Step 5: Обновить обоих потребителей**

Кортеж стал пятиэлементным — распаковка ломается в двух местах. В эндпоинте `/api/ai/sql/run`:

```csharp
                    var (cols, types, rows, ms, truncated) = SqlRun(eff);
                    J(ctx, new { cols, types, rows, ms, truncated, effective = eff });
```

В цикле агента (`SqlAgentAsk`), в ветке `action == "sql"` — типы здесь пока не нужны, поэтому discard:

```csharp
                    var (cols, _, rows, ms, more) = SqlRun(eff, SqlMaxRows);
```

Задача 4 заменит `_` на `types`, когда появится потребитель. Discard выбран намеренно: именованная переменная, которую никто не читает, — повод для следующего читателя гадать, забыли её или так задумано.

- [ ] **Step 6: Пересобрать и проверить**

```bash
dotnet build -c Release && python tests/smoke.py http://localhost:5080
```

Ожидается `FAIL=0`. Дополнительно проверить руками, что типы верные:

```bash
python -c "import json,urllib.parse,urllib.request;q=\"select 'a'::text t, 1 n, now() d, true b\";print(json.load(urllib.request.urlopen('http://localhost:5080/api/ai/sql/run?q='+urllib.parse.quote(q)))['types'])"
```

Ожидается `['text', 'number', 'date', 'bool']`.

- [ ] **Step 7: Коммит**

```bash
git add Program.cs tests/smoke.py
git commit -m "feat(ai): исполнитель SQL отдаёт типы колонок"
```

---

### Task 2: Сборка датасета из JSON и отладочный эндпоинт

Ответ инструмента и предзагруженная сводка — это JSON, а не таблица. Нужна функция, которая достаёт из них массив объектов и превращает в датасет. Отладочный эндпоинт даёт детерминированный шов для тестов: без него датасет проверяется только живым прогоном модели.

**Files:**
- Modify: `Program.cs` — регион «ИИ-харнесс», роутинг
- Test: `tests/smoke.py`

**Interfaces:**
- Consumes: `ColType` из Task 1, существующий `ToolCall(name, args)`
- Produces:
  - `static object DatasetFromJson(JsonElement root, string arrayPath, string[] columns, string source, string hint)` → `{source, cols, rows, rowCount, truncated, hint}` либо `null`
  - `GET /api/ai/dataset/probe?tool=<имя>&array=<путь>&columns=<через запятую>` — только локальные вызовы

- [ ] **Step 1: Написать падающие проверки**

```python
section("Датасет для визуализации  /api/ai/dataset/probe")

def probe(qs):
    try:
        st, d = _req("/api/ai/dataset/probe?" + qs)
        return d
    except Exception as e:
        return {"_exc": str(e)}

d = probe("tool=leaders&array=items&columns=name,overdue")
check("датасет собран из массива инструмента", isinstance(d.get("rows"), list) and len(d["rows"]) > 0,
      str(d.get("error") or "")[:80])
check("колонки с типами", [c.get("type") for c in d.get("cols", [])] == ["text", "number"],
      str(d.get("cols")))
check("источник помечен", d.get("source") == "tool:leaders", str(d.get("source")))
check("rowCount заполнен", isinstance(d.get("rowCount"), int) and d["rowCount"] > 0, str(d.get("rowCount")))

d = probe("tool=leaders&array=нет_такого&columns=name")
check("несуществующий массив даёт понятную ошибку", bool(d.get("error")), str(d.get("error"))[:80])

d = probe("tool=leaders&array=items&columns=name,нет_такой_колонки")
check("несуществующая колонка отбрасывается, а не роняет сборку",
      [c["name"] for c in d.get("cols", [])] == ["name"], str(d.get("cols")))
```

- [ ] **Step 2: Прогнать и убедиться, что проверки падают**

```bash
python tests/smoke.py http://localhost:5080
```

Ожидается шесть новых `[FAIL]` — эндпоинта нет (404).

- [ ] **Step 3: Реализовать сборку датасета**

```csharp
    // ---------- Датасет для визуализации ----------
    // Значения берутся ТОЛЬКО из фактического результата: модель может назвать массив
    // и колонки, но не переписывает числа. Иначе график разойдётся с текстом ответа
    // и с дашбордом, а заметить это будет нечем.
    const int DatasetMaxCols = 12;

    static object DatasetFromJson(JsonElement root, string arrayPath, string[] columns,
                                  string source, string hint)
    {
        // Путь вида "items" или "region.items" — по точке вглубь объекта.
        var el = root;
        foreach (var part in (arrayPath ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(part, out el))
                return null;
        }
        if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() == 0) return null;

        var items = el.EnumerateArray().ToList();
        if (items[0].ValueKind != JsonValueKind.Object) return null;

        // Если колонки не названы — берём все поля первого объекта.
        var names = (columns != null && columns.Length > 0)
            ? columns.ToList()
            : items[0].EnumerateObject().Select(p => p.Name).ToList();
        // Несуществующие колонки молча отбрасываем: модель могла ошибиться в имени,
        // и это не повод остаться совсем без графика.
        names = names.Where(n => items[0].TryGetProperty(n, out _)).Take(DatasetMaxCols).ToList();
        if (names.Count == 0) return null;

        var types = names.Select(n => JsonColType(items, n)).ToList();
        var rows = new List<object[]>();
        foreach (var it in items.Take(SqlMaxRows))
        {
            var r = new object[names.Count];
            for (int i = 0; i < names.Count; i++)
                r[i] = it.TryGetProperty(names[i], out var v) ? JsonValue(v) : null;
            rows.Add(r);
        }

        return new
        {
            source,
            cols = names.Select((n, i) => new { name = n, title = n, type = types[i] }),
            rows,
            rowCount = items.Count,
            truncated = items.Count > rows.Count,
            hint
        };
    }

    // Тип колонки по первому непустому значению: у JSON метаданных нет, в отличие от БД.
    static string JsonColType(List<JsonElement> items, string name)
    {
        foreach (var it in items)
        {
            if (!it.TryGetProperty(name, out var v)) continue;
            switch (v.ValueKind)
            {
                case JsonValueKind.Number: return "number";
                case JsonValueKind.True:
                case JsonValueKind.False: return "bool";
                case JsonValueKind.String:
                    return DateTime.TryParse(v.GetString(), out _) ? "date" : "text";
                case JsonValueKind.Null: continue;
                default: return "text";
            }
        }
        return "text";
    }

    static object JsonValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : (object)v.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.String => Trunc(v.GetString(), 200),
        _ => Trunc(v.GetRawText(), 200)
    };
```

- [ ] **Step 4: Добавить отладочный эндпоинт**

В роутинг, рядом с остальными `/api/ai/*`:

```csharp
            case "/api/ai/dataset/probe":
            {
                // Отладочный шов: детерминированная проверка сборки датасета без участия модели.
                if (!IsLocalCall(ctx)) { J(ctx, new { error = "харнесс доступен только при локальном вызове" }); return; }
                try
                {
                    using var argDoc = JsonDocument.Parse("{}");
                    var res = ToolCall(q["tool"], argDoc.RootElement);
                    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(res));
                    var cols = (q["columns"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
                    var ds = DatasetFromJson(doc.RootElement, q["array"], cols, "tool:" + q["tool"], null);
                    if (ds == null) { J(ctx, new { error = "не удалось собрать датасет: проверь имя массива" }); return; }
                    J(ctx, ds);
                }
                catch (Exception ex) { J(ctx, new { error = ex.Message }); }
                return;
            }
```

- [ ] **Step 5: Пересобрать и проверить**

```bash
dotnet build -c Release && python tests/smoke.py http://localhost:5080
```

Ожидается `FAIL=0`. Дополнительно посмотреть глазами, что значения совпадают с инструментом:

```bash
python -c "import json,urllib.request;d=json.load(urllib.request.urlopen('http://localhost:5080/api/ai/dataset/probe?tool=leaders&array=items&columns=name,overdue'));print(d['cols']);print(d['rows'][:3]);print('всего',d['rowCount'])"
```

Сверить первые строки с `/api/leaders?by=performer` — числа обязаны совпасть.

- [ ] **Step 6: Коммит**

```bash
git add Program.cs tests/smoke.py
git commit -m "feat(ai): сборка датасета из ответа инструмента и отладочный эндпоинт"
```

---

### Task 3: Поле `chart` в протоколе агента

Модель должна знать, что может указать, какой массив визуализировать. Без правки промпта поле никогда не появится.

**Files:**
- Modify: `Program.cs` — `AgentSystemPrompt`, `ToolCatalog`

**Interfaces:**
- Consumes: `ToolCatalog` из существующего кода
- Produces: в действии `answer` допустимо необязательное поле `chart` вида `{from, array, columns, view}`

- [ ] **Step 1: Дополнить описание протокола в промпте**

В `AgentSystemPrompt`, после описания четырёх действий, добавить:

```csharp
"\nВИЗУАЛИЗАЦИЯ. В действии answer можно дополнительно указать, что показать графиком:\n" +
"{\"thought\":\"…\",\"action\":\"answer\",\"text\":\"…\"," +
"\"chart\":{\"from\":\"tool:leaders\",\"array\":\"items\",\"columns\":[\"name\",\"overdue\"],\"view\":\"bars\"}}\n" +
"— from: tool:<имя инструмента> | sql | preload:overview;\n" +
"— array: имя массива в ответе (у инструментов leaders, leader_tasks, stuck — items;\n" +
"  у overview и в предзагрузке — processes; для from=sql поле не нужно);\n" +
"— columns: какие поля показать, первым — подпись (текст), далее числовые;\n" +
"— view: пожелание вида (bars | line | shares | kpi | table). Это ПОЖЕЛАНИЕ:\n" +
"  окончательный вид выбирает интерфейс по типам колонок.\n" +
"Значения ты НЕ переписываешь — их подставит код из фактического результата.\n" +
"Поле необязательное: если показывать графиком нечего, не заполняй его.\n"
```

- [ ] **Step 2: Пересобрать и проверить живьём, что модель заполняет поле**

```bash
dotnet build -c Release
```

Затем задать агенту вопрос, закрывающийся инструментом, и посмотреть сырой ответ:

```bash
python -c "
import json,urllib.request
b=json.dumps({'messages':[{'role':'user','content':'Кто из сотрудников хуже всех справляется?'}]}).encode()
r=urllib.request.Request('http://localhost:5080/api/ai/sql',data=b,headers={'Content-Type':'application/json'})
d=json.load(urllib.request.urlopen(r,timeout=300))
print('шаги:',[s.get('action') for s in d.get('steps',[])])
print('dataset:',d.get('dataset'))
"
```

На этом шаге `dataset` ещё `None` — поле собирается в задаче 4. Проверяется другое: модель не сломалась от нового текста в промпте и по-прежнему отвечает через инструмент. Если модель начала путаться в протоколе — сократить формулировку.

- [ ] **Step 3: Прогнать smoke**

```bash
python tests/smoke.py http://localhost:5080
```

Ожидается `FAIL=0`: правка промпта не должна ломать существующие проверки маршрутизации.

- [ ] **Step 4: Коммит**

```bash
git add Program.cs
git commit -m "feat(ai): модель может указать, какой массив визуализировать"
```

---

### Task 4: Поле `dataset` в ответе агента

**Files:**
- Modify: `Program.cs` — `SqlAgentAsk`
- Test: `tests/smoke.py`

**Interfaces:**
- Consumes: `DatasetFromJson` (Task 2), `SqlRun` с типами (Task 1), поле `chart` (Task 3)
- Produces: ответ `/api/ai/sql` содержит необязательное поле `dataset`

- [ ] **Step 1: Написать падающие проверки**

Проверки зависят от живой модели, поэтому оформляются через существующий помощник
`agent_ok(name, d, cond, detail)` из секции «Цикл агента»: он превращает недоступность LLM
в пометку `[ИИ?]`, а не в `FAIL`. `ai_check` здесь **не годится** — он умеет проверять только
длину текстового ответа. Дописать в конец секции «Цикл агента» в `tests/smoke.py`:

```python
# --- датасет для визуализации (страница «Аналитика по запросу») ---
try:
    st, d = _req("/api/ai/sql", method="POST", body={"messages": [
        {"role": "user", "content": "Покажи просрочку по подразделениям"}]}, timeout=180)
    ds = d.get("dataset") or {}
    agent_ok("агент вернул датасет", d, bool(d.get("dataset")),
             "поля dataset нет в ответе")
    agent_ok("у колонок датасета проставлены типы", d,
             bool(ds.get("cols")) and all(c.get("type") in ("text", "number", "date", "bool")
                                          for c in ds["cols"]),
             str(ds.get("cols"))[:120])
    agent_ok("источник датасета указан", d,
             (ds.get("source") or "").split(":")[0] in ("sql", "tool", "preload"),
             str(ds.get("source")))
    agent_ok("строк не больше потолка исполнителя", d,
             len(ds.get("rows") or []) <= 200, str(len(ds.get("rows") or [])))
    agent_ok("rowCount не меньше числа отданных строк", d,
             int(ds.get("rowCount") or 0) >= len(ds.get("rows") or []),
             str(ds.get("rowCount")) + " / " + str(len(ds.get("rows") or [])))
except Exception as e:
    check("датасет: запрос к агенту прошёл", False, str(e))

# Ответ без визуализируемых данных — штатное состояние, а не ошибка (§9 спеки).
try:
    st, d = _req("/api/ai/sql", method="POST", body={"messages": [
        {"role": "user", "content": "Что ты умеешь?"}]}, timeout=180)
    agent_ok("вопрос без данных: ответ есть и без датасета это не ошибка", d,
             bool(d.get("reply")) and not d.get("error"), str(d.get("error") or "")[:80])
except Exception as e:
    check("вопрос без данных: запрос прошёл", False, str(e))
```

- [ ] **Step 2: Прогнать и убедиться, что проверки падают**

```bash
python tests/smoke.py http://localhost:5080
```

- [ ] **Step 3: Запоминать результаты по ходу цикла**

В `SqlAgentAsk`, перед циклом, завести три переменные:

```csharp
        // Фактические результаты, из которых потом собирается датасет.
        // Модель к ним не прикасается — она может только указать, какой из них рисовать.
        object lastToolResult = null; string lastToolName = null;
        List<string> lastSqlCols = null, lastSqlTypes = null; List<object[]> lastSqlRows = null;
```

В ветке `action == "tool"`, сразу после успешного `ToolCall`:

```csharp
                    lastToolResult = res; lastToolName = name;
```

В ветке `action == "sql"`, после успешного `SqlRun`:

```csharp
                    lastSqlCols = cols; lastSqlTypes = types; lastSqlRows = rows;
```

- [ ] **Step 4: Собрать датасет при ответе**

В ветке `action == "answer"`, до `break`, прочитать поле `chart` и построить датасет:

```csharp
            if (action == "answer")
            {
                reply = Str("text");
                try
                {
                    if (el.TryGetProperty("chart", out var ch) && ch.ValueKind == JsonValueKind.Object)
                    {
                        string From(string k) => ch.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                        var from = From("from") ?? "";
                        var arr = From("array");
                        var view = From("view");
                        var colNames = ch.TryGetProperty("columns", out var cc) && cc.ValueKind == JsonValueKind.Array
                            ? cc.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToArray()
                            : Array.Empty<string>();

                        if (from.StartsWith("tool:", StringComparison.Ordinal) && lastToolResult != null)
                        {
                            using var doc2 = JsonDocument.Parse(JsonSerializer.Serialize(lastToolResult));
                            dataset = DatasetFromJson(doc2.RootElement, arr, colNames, "tool:" + lastToolName, view);
                        }
                        else if (from.StartsWith("preload:", StringComparison.Ordinal))
                        {
                            using var doc2 = JsonDocument.Parse(JsonSerializer.Serialize(pre));
                            // В предзагрузке лежит { overview, processes } — путь начинается с этих имён.
                            dataset = DatasetFromJson(doc2.RootElement, arr, colNames, from, view);
                        }
                        else if (from == "sql" && lastSqlRows != null)
                        {
                            dataset = DatasetFromSqlResult(lastSqlCols, lastSqlTypes, lastSqlRows, view);
                        }
                    }
                    // Модель не указала chart, но SQL выполнялся — рисуем его результат:
                    // данные уже добыты, выбрасывать их незачем.
                    if (dataset == null && lastSqlRows != null)
                        dataset = DatasetFromSqlResult(lastSqlCols, lastSqlTypes, lastSqlRows, null);
                }
                catch { /* датасет — необязательная часть ответа: его отсутствие не должно ломать ответ */ }
                steps.Add(new { n, action, thought, ms = (int)stepSw.ElapsedMilliseconds });
                break;
            }
```

Объявить `object dataset = null;` рядом с `reply` перед циклом.

- [ ] **Step 5: Добавить сборку из SQL-результата**

Рядом с `DatasetFromJson`:

```csharp
    static object DatasetFromSqlResult(List<string> cols, List<string> types, List<object[]> rows, string hint)
    {
        if (cols == null || cols.Count == 0) return null;
        int n = Math.Min(cols.Count, DatasetMaxCols);
        var take = rows.Select(r => r.Take(n).ToArray()).ToList();
        return new
        {
            source = "sql",
            cols = cols.Take(n).Select((c, i) => new { name = c, title = c, type = types[i] }),
            rows = take,
            rowCount = rows.Count,
            truncated = false,
            hint
        };
    }
```

- [ ] **Step 6: Отдать поле в ответе**

В обоих `return new { reply, preloaded = … }` добавить `dataset`:

```csharp
        return new { reply, preloaded = new[] { "overview", "processes" },
                     steps, elapsedMs = (int)sw.ElapsedMilliseconds, truncated, dataset };
```

- [ ] **Step 7: Пересобрать и проверить на трёх вопросах**

```bash
dotnet build -c Release
```

Затем прогнать три вопроса и записать результат в отчёт:

```bash
python -c "
import json,urllib.request
for q in ['Покажи просрочку по подразделениям','Сколько заданий просрочено?','Сколько заданий создано по месяцам 2026 года?']:
    b=json.dumps({'messages':[{'role':'user','content':q}]}).encode()
    r=urllib.request.Request('http://localhost:5080/api/ai/sql',data=b,headers={'Content-Type':'application/json'})
    d=json.load(urllib.request.urlopen(r,timeout=300)); ds=d.get('dataset')
    print(q,'->', 'нет датасета' if not ds else (ds['source']+' | '+str([c['name']+':'+c['type'] for c in ds['cols']])+' | строк '+str(len(ds['rows']))))
"
```

- [ ] **Step 8: Прогнать smoke и закоммитить**

```bash
python tests/smoke.py http://localhost:5080
git add Program.cs tests/smoke.py
git commit -m "feat(ai): датасет в ответе агента"
```

---

### Task 5: `charts.js` и правила выбора вида

**Files:**
- Create: `charts.js`, `tests/charts.test.js`
- Modify: `index.html` (подключение файла, удаление переехавших функций), `armgov-standalone.csproj`

`Program.cs` править не нужно: раздача статики уже отдаёт `.js` с типом `application/javascript`
(`Program.cs:426`).

**Interfaces:**
- Consumes: контракт `dataset` из Task 4
- Produces:
  - `pickViews(ds)` → `{views: string[], def: string}`; виды: `kpi | bars | line | shares | table`
  - переехавшие без изменений `esc(s)`, `rect(x,y,w,h,f,r)`, `txt(x,y,s,a,sz,f)`, `svgStack(data,h,showVal,minSlots)`, `svgSpark(data,h)`, `svgBars(data,h,color)`, `legend(col,v,lbl)`

- [ ] **Step 1: Написать тест правил выбора вида**

Создать `tests/charts.test.js`:

```javascript
// Тест правил выбора вида визуализации. Чистая функция от формы данных —
// именно она тихо ломается при любой правке, поэтому проверяется отдельно.
// Запуск: node tests/charts.test.js
const fs = require('fs');
const path = require('path');
const src = fs.readFileSync(path.join(__dirname, '..', 'charts.js'), 'utf8');
// charts.js — обычный браузерный скрипт без экспортов. Выполняем его в отдельной
// области и забираем нужные функции наружу. Через eval() было бы короче, но тогда
// тест зависел бы от нестрогого режима модуля — хрупко и неочевидно.
const api = new Function(src + '\nreturn {pickViews: pickViews};')();
const pickViews = api.pickViews;

let pass = 0, fail = 0;
function check(name, cond, detail) {
  if (cond) { pass++; console.log('  [ OK ] ' + name); }
  else { fail++; console.log('  [FAIL] ' + name + (detail ? ' — ' + detail : '')); }
}
function ds(cols, rows) {
  return { cols: cols.map(c => ({ name: c[0], title: c[0], type: c[1] })), rows: rows, rowCount: rows.length };
}

const oneRow = ds([['всего', 'number'], ['просрочено', 'number']], [[1579, 635]]);
check('одна строка только с числами — плитки KPI',
      pickViews(oneRow).def === 'kpi', JSON.stringify(pickViews(oneRow)));

const catNum = ds([['НОР', 'text'], ['просрочено', 'number']], [['ДИТ', 412], ['ДФ', 311]]);
check('текст и число — столбики по умолчанию', pickViews(catNum).def === 'bars');
check('для текста с числом доступны доли', pickViews(catNum).views.indexOf('shares') >= 0);

const timeNum = ds([['месяц', 'date'], ['создано', 'number']], [['2026-01-01', 10], ['2026-02-01', 20]]);
check('дата и число — динамика по умолчанию', pickViews(timeNum).def === 'line');

const catTwoNum = ds([['НОР', 'text'], ['в работе', 'number'], ['просрочено', 'number']],
                     [['ДИТ', 900, 412]]);
check('текст и два числа — столбики', pickViews(catTwoNum).def === 'bars');

const twoText = ds([['НОР', 'text'], ['вид', 'text'], ['всего', 'number']], [['ДИТ', 'НПА', 5]]);
check('два текста и число — только таблица', pickViews(twoText).views.join() === 'table');

const noNum = ds([['тема', 'text'], ['автор', 'text']], [['а', 'б']]);
check('без чисел — только таблица', pickViews(noNum).views.join() === 'table');

const negative = ds([['НОР', 'text'], ['дельта', 'number']], [['ДИТ', -5], ['ДФ', 7]]);
check('с отрицательными доли не предлагаются', pickViews(negative).views.indexOf('shares') < 0);

const hinted = Object.assign({}, catNum, { hint: 'table' });
check('допустимая подсказка меняет вид по умолчанию', pickViews(hinted).def === 'table');

const badHint = Object.assign({}, catNum, { hint: 'line' });
check('недопустимая подсказка игнорируется', pickViews(badHint).def === 'bars');

// Форма «текст + дата + число» в таблице спеки не описана: фиксируем, что
// побеждает ось времени — иначе правило молча изменится при следующей правке.
const textDateNum = ds([['НОР', 'text'], ['месяц', 'date'], ['создано', 'number']],
                       [['ДИТ', '2026-01-01', 10]]);
check('дата важнее текстовой подписи — динамика', pickViews(textDateNum).def === 'line');

console.log('\nИТОГ: PASS=' + pass + ' FAIL=' + fail);
process.exit(fail ? 1 : 0);
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
node tests/charts.test.js
```

Ожидается падение: файла `charts.js` ещё нет.

- [ ] **Step 3: Создать `charts.js` с правилами выбора**

```javascript
/* =========================================================================
   Визуализация датасета ИИ-харнесса. Виды выбираются по ФОРМЕ данных:
   типы колонок ставит сервер, клиент лишь смотрит на них. Подсказка модели
   (ds.hint) сужает выбор внутри допустимого, но не расширяет его.
   ========================================================================= */

// Какие виды допустимы для этого датасета и какой из них показать первым.
function pickViews(ds){
  var cols=(ds&&ds.cols)||[], rows=(ds&&ds.rows)||[];
  var nums=cols.filter(function(c){return c.type==='number';});
  var texts=cols.filter(function(c){return c.type==='text';});
  var dates=cols.filter(function(c){return c.type==='date';});
  var views=[], def='table';

  if(!nums.length){ views=['table']; def='table'; }
  else if(rows.length===1 && !texts.length && !dates.length){ views=['kpi','table']; def='kpi'; }
  else if(dates.length===1 && nums.length>=1 && nums.length<=3){ views=['line','bars','table']; def='line'; }
  else if(texts.length===1 && nums.length>=1){
    views=['bars','table']; def='bars';
    // Доли осмысленны только для неотрицательных значений одной меры.
    var neg=rows.some(function(r){return r.some(function(v){return typeof v==='number'&&v<0;});});
    if(!neg && nums.length===1) views.splice(1,0,'shares');
  }
  else { views=['table']; def='table'; }

  if(ds&&ds.hint&&views.indexOf(ds.hint)>=0) def=ds.hint;
  return {views:views, def:def};
}
```

- [ ] **Step 4: Прогнать тест**

```bash
node tests/charts.test.js
```

Ожидается `PASS=11 FAIL=0`.

- [ ] **Step 5: Перенести существующие примитивы**

Вырезать из `index.html` функции `esc` (строка 159), `rect`, `txt`, `svgStack`, `svgSpark`,
`svgBars`, `legend` (строки 211–260) и вставить в конец `charts.js` без изменений.

`esc` переносится **обязательно**, хотя это и не рисовалка: `txt` и все будущие рендереры её
вызывают, а Node-тест грузит один только `charts.js`. Оставь `esc` в `index.html` — тест упадёт
с `ReferenceError`. Обратной беды нет: `charts.js` подключается раньше основного скрипта, обе
функции живут в одной глобальной области, и весь существующий код `index.html` продолжает
вызывать `esc` как прежде. Функции `ch` и `hlp` (заголовки карточек) остаются в `index.html` —
они про разметку дашборда, а не про графики.

Подключить файл в `index.html` перед основным скриптом:

```html
<script src="/charts.js"></script>
```

В `armgov-standalone.csproj`, рядом с `style.css`:

```xml
<None Include="charts.js" CopyToOutputDirectory="PreserveNewest" />
```

- [ ] **Step 6: Проверить, что существующие экраны не сломались**

Пересобрать, открыть прототип и посмотреть графики на прежних местах: спарклайны в «Процессах» на стартовом экране, столбики и стопки на странице процесса, треемап в «Тематиках обращений». Затем:

```bash
python tests/smoke.py http://localhost:5080
```

Ожидается `FAIL=0`.

- [ ] **Step 7: Коммит**

```bash
git add charts.js tests/charts.test.js index.html armgov-standalone.csproj
git commit -m "feat(ui): charts.js с правилами выбора вида и переносом SVG-примитивов"
```

---

### Task 6: Пять рендереров

**Files:**
- Modify: `charts.js`
- Test: `tests/charts.test.js`

**Interfaces:**
- Consumes: `pickViews`, перенесённые примитивы (Task 5)
- Produces: `renderView(ds, view)` → HTML-строка; `viewTitle(v)` → название вида для чипа;
  внутренние `dsRoles`, `renderKpi`, `renderBars`, `svgGroupedBars`, `renderLine`,
  `renderShares`, `renderTable`

- [ ] **Step 1: Дописать тест на общий вход**

Сначала забрать из `charts.js` новые функции — заменить строку с `new Function` на:

```javascript
const api = new Function(src + '\nreturn {pickViews: pickViews, renderView: renderView, viewTitle: viewTitle};')();
const pickViews = api.pickViews, renderView = api.renderView, viewTitle = api.viewTitle;
```

Затем, перед итогом:

```javascript
['kpi','bars','line','shares','table'].forEach(function(v){
  var html = renderView(catNum, v);
  check('renderView отдаёт непустую разметку для вида ' + v,
        typeof html === 'string' && html.length > 20, String(html).slice(0, 40));
});
check('renderView на пустом датасете не падает',
      typeof renderView({cols:[],rows:[]}, 'bars') === 'string');
check('у каждого вида есть русское название для чипа',
      ['kpi','bars','line','shares','table'].every(function(v){return viewTitle(v).length > 2;}));

// §7 спеки: два и более показателя рисуются сериями рядом, а не только первый.
// Признак — в разметке присутствуют оба названия колонок (легенда серий).
var twoSeries = ds([['НОР','text'],['в работе','number'],['просрочено','number']],
                   [['ДИТ',900,412],['ДФ',700,311]]);
var twoHtml = renderView(twoSeries, 'bars');
check('при двух показателях рисуются обе серии',
      twoHtml.indexOf('в работе') >= 0 && twoHtml.indexOf('просрочено') >= 0,
      twoHtml.slice(0, 80));

// Молчаливое обрезание запрещено (§7): при >20 категориях должна быть подпись.
var many = [];
for (var i = 0; i < 40; i++) many.push(['НОР ' + i, 40 - i]);
var manyHtml = renderView(ds([['НОР','text'],['просрочено','number']], many), 'bars');
check('при 40 категориях сказано, сколько показано',
      manyHtml.indexOf('20 из 40') >= 0, manyHtml.slice(-140));
```

- [ ] **Step 2: Прогнать — упадёт на отсутствии `renderView`**

```bash
node tests/charts.test.js
```

- [ ] **Step 3: Реализовать рендереры**

Дописать в `charts.js`. Ключевые решения из спеки: сортировка столбиков по убыванию, потолок 20 категорий с честной подписью, кольцо при шести и менее категориях.

```javascript
/* ---------- Рендереры видов ---------- */
// Потолок категорий на графике. Больше двадцати столбиков глазами не читаются,
// но обрезать молча нельзя: иначе руководитель решит, что подразделений двадцать.
var CHART_TOP_N = 20;
// Палитра серий — только токены из tokens.css, новых цветов не вводим.
var CHART_PAL = ['var(--accent)','var(--green)','var(--amber)','var(--red)','var(--blue)','var(--subtle)'];

function viewTitle(v){
  return {kpi:'Плитки',bars:'Столбики',line:'Динамика',shares:'Доли',table:'Таблица'}[v]||v;
}

// Индексы колонок: первая текстовая — подпись, первая датовая — ось времени, числовые — меры.
function dsRoles(ds){
  var cols=ds.cols||[];
  var label=-1,date=-1,nums=[];
  cols.forEach(function(c,i){
    if(c.type==='number')nums.push(i);
    else if(c.type==='date'&&date<0)date=i;
    else if(c.type==='text'&&label<0)label=i;
  });
  return {label:label,date:date,nums:nums};
}

function renderView(ds,view){
  if(!ds||!ds.cols||!ds.cols.length||!ds.rows)return '<div class="sub">нет данных</div>';
  if(view==='kpi')return renderKpi(ds);
  if(view==='bars')return renderBars(ds);
  if(view==='line')return renderLine(ds);
  if(view==='shares')return renderShares(ds);
  return renderTable(ds);
}

function renderKpi(ds){
  var r=ds.rows[0]||[];
  return '<div class="grid" style="grid-template-columns:repeat(auto-fit,minmax(180px,1fr))">'+
    ds.cols.map(function(c,i){
      return '<div class="kpi"><div class="kpi-label">'+esc(c.title)+'</div>'+
             '<div class="kpi-value">'+esc(String(r[i]==null?'—':r[i]))+'</div></div>';
    }).join('')+'</div>';
}

// Подпись об усечении: руководитель должен видеть, что категорий было больше.
function topNote(shown,total){
  return shown>=total?'':'<div class="sub" style="margin:6px 0 0">показаны '+shown+
    ' из '+total+', остальные в таблице</div>';
}

function renderBars(ds){
  var R=dsRoles(ds); if(!R.nums.length)return renderTable(ds);
  var li=R.label>=0?R.label:(R.date>=0?R.date:0);
  // Сортировка по первому показателю: руководителя интересует «у кого хуже».
  var rows=ds.rows.slice().sort(function(a,b){
    return (Number(b[R.nums[0]])||0)-(Number(a[R.nums[0]])||0);});
  var total=rows.length; if(total>CHART_TOP_N)rows=rows.slice(0,CHART_TOP_N);
  var note=topNote(rows.length,total);

  if(R.nums.length===1){
    var mi=R.nums[0];
    return svgBars(rows.map(function(r){
      return {label:String(r[li]),value:Number(r[mi])||0};}),260)+note;
  }
  var labels=rows.map(function(r){return String(r[li]);});
  var series=R.nums.map(function(ci){
    return {name:(ds.cols[ci].title||ds.cols[ci].name),
            values:rows.map(function(r){return Number(r[ci])||0;})};});
  return svgGroupedBars(labels,series,260)+note;
}

// Несколько показателей по одной категории — серии РЯДОМ, не стопкой.
// Стопка уместна для частей целого; два произвольных показателя («в работе» и
// «просрочено») частями целого не являются, и стопка соврала бы про сумму.
function svgGroupedBars(labels,series,h){
  h=h||260; if(!labels.length||!series.length)return '<div class="sub">нет данных</div>';
  var pt=18,pb=34,pl=8,pr=8,ch=h-pt-pb,n=labels.length,k=series.length;
  var W=Math.max(n*(26*k+34),300);
  var mx=1; series.forEach(function(se){se.values.forEach(function(v){if(v>mx)mx=v;});});
  var gw=(W-pl-pr)/n, bw=Math.max(6,Math.min((gw-16)/k,26));
  var body=labels.map(function(lb,i){
    var x0=pl+i*gw+(gw-bw*k)/2, s='';
    series.forEach(function(se,j){
      var v=se.values[i]||0, bh=v/mx*ch, x=x0+j*bw, y=pt+ch-bh;
      s+=rect(x+1,y,bw-2,bh,CHART_PAL[j%CHART_PAL.length],2)+
         txt(x+bw/2,y-4,v,'middle',10,'var(--text)');
    });
    return s+txt(pl+i*gw+gw/2,h-14,lb,'middle',10,'var(--subtle)');
  }).join('');
  var leg=series.map(function(se,j){
    return '<span class="muted" style="font-size:12px"><span style="display:inline-block;width:10px;'+
      'height:10px;border-radius:2px;background:'+CHART_PAL[j%CHART_PAL.length]+
      ';margin-right:5px"></span>'+esc(se.name)+'</span>';}).join('');
  return '<svg viewBox="0 0 '+W+' '+h+'" width="100%" height="'+h+'" preserveAspectRatio="xMidYMid meet" style="max-width:100%;display:block">'+
    rect(pl,pt+ch,W-pl-pr,1,'var(--border)')+body+'</svg>'+
    '<div class="row" style="gap:14px;margin-top:6px">'+leg+'</div>';
}

function renderLine(ds){
  var R=dsRoles(ds); if(R.date<0||!R.nums.length)return renderBars(ds);
  var mi=R.nums[0];
  var data=ds.rows.map(function(r){return {label:String(r[R.date]).slice(0,10),value:Number(r[mi])||0};})
                  .sort(function(a,b){return a.label<b.label?-1:1;});
  var W=Math.max(data.length*56,320),h=260,pt=18,pb=34,pl=40,pr=10,ch=h-pt-pb;
  var mx=Math.max.apply(null,data.map(function(d){return d.value;}))||1;
  var X=function(i){return pl+(data.length>1?i/(data.length-1):0.5)*(W-pl-pr);};
  var Y=function(v){return pt+(1-v/mx)*ch;};
  var seg=data.map(function(d,i){return (i?'L':'M')+X(i).toFixed(1)+' '+Y(d.value).toFixed(1);}).join(' ');
  var dots=data.map(function(d,i){return '<circle cx="'+X(i).toFixed(1)+'" cy="'+Y(d.value).toFixed(1)+'" r="3" fill="var(--accent)"/>'+
    txt(X(i),Y(d.value)-8,d.value,'middle',11,'var(--text)')+txt(X(i),h-14,d.label,'middle',10,'var(--subtle)');}).join('');
  return '<svg viewBox="0 0 '+W+' '+h+'" width="100%" height="'+h+'" preserveAspectRatio="xMidYMid meet" style="max-width:100%;display:block">'+
    rect(pl,pt+ch,W-pl-pr,1,'var(--border)')+
    '<path d="'+seg+'" fill="none" stroke="var(--accent)" stroke-width="2" stroke-linejoin="round"/>'+dots+'</svg>';
}

function renderShares(ds){
  var R=dsRoles(ds); if(!R.nums.length)return renderTable(ds);
  var li=R.label>=0?R.label:0, mi=R.nums[0];
  var data=ds.rows.map(function(r){return {label:String(r[li]),value:Number(r[mi])||0};})
                  .sort(function(a,b){return b.value-a.value;});
  var total=data.reduce(function(s,d){return s+d.value;},0)||1;
  var pal=CHART_PAL;
  if(data.length<=6){
    // Кольцо: на демо «долю» привычно видеть круглой, но при большом числе
    // категорий круг перестаёт читаться — тогда ниже рисуются полосы.
    var cx=110,cy=110,r=80,sw=28,off=0,circ=2*Math.PI*r;
    var arcs=data.map(function(d,i){
      var len=d.value/total*circ;
      var s='<circle cx="'+cx+'" cy="'+cy+'" r="'+r+'" fill="none" stroke="'+pal[i%pal.length]+'" stroke-width="'+sw+
            '" stroke-dasharray="'+len.toFixed(1)+' '+(circ-len).toFixed(1)+'" stroke-dashoffset="'+(-off).toFixed(1)+
            '" transform="rotate(-90 '+cx+' '+cy+')"/>';
      off+=len; return s;
    }).join('');
    var leg=data.map(function(d,i){
      return '<div style="display:flex;align-items:center;gap:8px;padding:3px 0;font-size:13px">'+
        '<span style="width:10px;height:10px;border-radius:2px;background:'+pal[i%pal.length]+'"></span>'+
        '<span style="flex:1">'+esc(d.label)+'</span><b>'+d.value+'</b>'+
        '<span class="subtle">'+(100*d.value/total).toFixed(1)+'%</span></div>';
    }).join('');
    return '<div style="display:flex;gap:24px;align-items:center;flex-wrap:wrap">'+
      '<svg viewBox="0 0 220 220" width="220" height="220">'+arcs+'</svg><div style="flex:1;min-width:220px">'+leg+'</div></div>';
  }
  return data.map(function(d,i){
    var p=100*d.value/total;
    return '<div style="display:flex;align-items:center;gap:10px;padding:4px 0;font-size:13px">'+
      '<span style="width:180px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">'+esc(d.label)+'</span>'+
      '<span style="flex:1;height:10px;background:var(--surface-2);border-radius:5px;overflow:hidden">'+
      '<span style="display:block;height:100%;width:'+p.toFixed(1)+'%;background:'+pal[i%pal.length]+'"></span></span>'+
      '<b style="min-width:48px;text-align:right">'+d.value+'</b>'+
      '<span class="subtle" style="min-width:48px;text-align:right">'+p.toFixed(1)+'%</span></div>';
  }).join('');
}

function renderTable(ds){
  var head=ds.cols.map(function(c){return '<th>'+esc(c.title)+'</th>';}).join('');
  var body=ds.rows.map(function(r){
    return '<tr>'+r.map(function(v,i){
      var num=ds.cols[i]&&ds.cols[i].type==='number';
      return '<td style="text-align:'+(num?'right':'left')+'">'+esc(String(v==null?'—':v))+'</td>';
    }).join('')+'</tr>';
  }).join('');
  return '<div style="overflow:auto;max-height:420px"><table style="width:100%"><thead><tr>'+head+'</tr></thead><tbody>'+body+'</tbody></table></div>';
}
```

- [ ] **Step 4: Прогнать тест**

```bash
node tests/charts.test.js
```

Ожидается `PASS=20 FAIL=0`.

- [ ] **Step 5: Коммит**

```bash
git add charts.js tests/charts.test.js
git commit -m "feat(ui): пять видов визуализации датасета"
```

---

### Task 7: Экран «Аналитика по запросу»

**Files:**
- Modify: `index.html` (навигация, разметка экрана, логика), `style.css`

**Interfaces:**
- Consumes: `pickViews`, `renderView` (Tasks 5–6), поле `dataset` (Task 4), существующий `thinkBlock` из дровера чата
- Produces: экран `view-canvas`, функции `goCanvas()`, `askCanvas()`, `renderCanvas()`, `setCanvasView(v)`, `restoreCanvas(i)`

- [ ] **Step 1: Добавить экран в разметку и навигацию**

В `index.html`, в `<main>`, рядом с прочими экранами:

```html
<div id="view-canvas" class="hidden"></div>
```

В `hideViews` дописать `'view-canvas'` в список. В `renderNav`, после пункта «Аналитика процессов»:

```javascript
  n+='<div class="nav-item'+(CUR==='__canvas'?' active':'')+'" onclick="goCanvas()">📈 Аналитика по запросу</div>';
```

- [ ] **Step 2: Написать логику экрана**

```javascript
/* ---------- Аналитика по запросу ---------- */
var CANVAS=[];          // история сессии: [{q, reply, dataset, steps, elapsedMs, view, views}]
var CANVAS_CUR=-1;      // индекс показанного вопроса
var CANVAS_BUSY=false;

function goCanvas(){CUR='__canvas';CURDATA=null;renderNav();hideViews();
  document.getElementById('view-canvas').classList.remove('hidden');renderCanvas();}

function renderCanvas(){
  var el=document.getElementById('view-canvas');
  var ui=(OVR&&OVR.ui)||{},ps=ui.chatPrompts||[];
  var h='<div class="h2">Аналитика по запросу</div>'+
    '<div class="sub">Спросите про данные — страница покажет график по фактическому результату. Вид можно переключить.</div>'+
    '<div class="card" style="margin-bottom:16px">'+
      '<div class="row" style="gap:8px;align-items:flex-end">'+
        '<textarea id="canvas-input" rows="2" placeholder="Например: просрочка по подразделениям" '+
        'style="flex:1;resize:none;border:1px solid var(--border);border-radius:8px;padding:9px 11px;font:inherit;font-size:14px" '+
        'onkeydown="if(event.key===\'Enter\'&&!event.shiftKey){event.preventDefault();askCanvas();}"></textarea>'+
        '<button class="btn" id="canvas-send" onclick="askCanvas()">Показать</button>'+
      '</div>'+
      (ps.length?'<div class="row" style="gap:6px;flex-wrap:wrap;margin-top:10px">'+ps.map(function(t){
        return '<span class="expl" style="color:var(--accent)" onclick="quickCanvas('+JSON.stringify(t).replace(/"/g,'&quot;')+')">'+esc(t)+'</span>';}).join('')+'</div>':'')+
    '</div>'+
    '<div id="canvas-body"></div>';
  el.innerHTML=h;
  renderCanvasBody();
}

function quickCanvas(t){document.getElementById('canvas-input').value=t;askCanvas();}

function askCanvas(){
  if(CANVAS_BUSY)return;
  var inp=document.getElementById('canvas-input');var q=(inp.value||'').trim();if(!q)return;
  CANVAS_BUSY=true;document.getElementById('canvas-send').disabled=true;
  var t0=Date.now();
  var tick=setInterval(function(){
    var b=document.getElementById('canvas-body');
    if(b)b.innerHTML='<div class="card"><div class="sub" style="margin:0">собираю данные… '+
      Math.round((Date.now()-t0)/1000)+' с</div></div>';
  },500);
  fetch('/api/ai/sql',{method:'POST',headers:{'Content-Type':'application/json'},
    body:JSON.stringify({messages:[{role:'user',content:q}]})})
   .then(function(r){return r.json();}).then(function(d){
    clearInterval(tick);CANVAS_BUSY=false;document.getElementById('canvas-send').disabled=false;
    if(d.error){CANVAS.push({q:q,error:d.error});CANVAS_CUR=CANVAS.length-1;renderCanvasBody();return;}
    var pv=d.dataset?pickViews(d.dataset):{views:[],def:null};
    CANVAS.push({q:q,reply:d.reply,dataset:d.dataset,steps:d.steps,elapsedMs:d.elapsedMs,
                 views:pv.views,view:pv.def});
    CANVAS_CUR=CANVAS.length-1;inp.value='';renderCanvasBody();
  }).catch(function(e){
    clearInterval(tick);CANVAS_BUSY=false;document.getElementById('canvas-send').disabled=false;
    CANVAS.push({q:q,error:String(e)});CANVAS_CUR=CANVAS.length-1;renderCanvasBody();
  });
}

function setCanvasView(v){if(CANVAS[CANVAS_CUR]){CANVAS[CANVAS_CUR].view=v;renderCanvasBody();}}
function restoreCanvas(i){CANVAS_CUR=i;renderCanvasBody();}

function renderCanvasBody(){
  var el=document.getElementById('canvas-body');if(!el)return;
  var it=CANVAS[CANVAS_CUR];
  if(!it){el.innerHTML='<div class="card"><div class="sub" style="margin:0">Задайте вопрос — здесь появится график.</div></div>';return;}
  var h='';
  if(it.error){
    h+='<div class="card" style="border-color:var(--red)"><div class="card-h" style="color:var(--red)">Не получилось</div>'+
       '<div class="sub" style="margin:0">'+esc(it.error)+'</div></div>';
  } else {
    var ds=it.dataset;
    var chips=(it.views||[]).map(function(v){
      return '<span class="expl'+(v===it.view?' on':'')+'" onclick="setCanvasView(\''+v+'\')">'+
             viewTitle(v)+'</span>';}).join('');
    h+='<div class="card">'+
       '<div class="cardhead" style="align-items:center"><div class="card-h" style="margin:0;flex:1">'+esc(it.q)+'</div>'+
       '<div class="row" style="gap:10px">'+chips+'</div></div>'+
       (ds?renderView(ds,it.view):'<div class="sub">для этого вопроса графика нет</div>')+
       (ds?'<div class="sub" style="margin:10px 0 0">данные: '+esc(ds.source)+' · строк '+ds.rowCount+
           (ds.truncated?' · показаны первые '+ds.rows.length:'')+'</div>':'')+
       '</div>';
    if(it.reply)h+='<div class="card" style="margin-top:12px">'+mdLite(it.reply)+
       thinkBlock({steps:it.steps,elapsedMs:it.elapsedMs})+'</div>';
  }
  if(CANVAS.length>1){
    h+='<div class="row" style="gap:8px;flex-wrap:wrap;margin-top:14px;align-items:center">'+
       '<span class="sub" style="margin:0">История:</span>'+
       CANVAS.map(function(c,i){return '<span class="expl" style="color:'+(i===CANVAS_CUR?'var(--accent)':'var(--muted)')+
       '" onclick="restoreCanvas('+i+')">'+esc(c.q.slice(0,40))+'</span>';}).join('')+'</div>';
  }
  el.innerHTML=h;
}
```

- [ ] **Step 3: Добавить стиль активного чипа**

В `style.css`, сразу после правила `.expl:hover` (строка 97):

```css
/* активный вид на холсте «Аналитика по запросу»: тот же чип .expl, но выбранный */
.expl.on{border-color:var(--accent);background:var(--accent-soft);font-weight:600}
```

Больше ничего в `style.css` для этого экрана не нужно: холст — обычная `.card`, история —
ряд `.expl`, плитки KPI используют существующие `.kpi`, `.kpi-label`, `.kpi-value`.

- [ ] **Step 4: Проверить, что `thinkBlock` принимает объект без поля `content`**

`thinkBlock` написан для сообщения чата и читает `m.steps`, `m.elapsedMs`, `m.truncated`. Вызов выше передаёт объект ровно с этими полями — проверить чтением кода функции в `index.html`, что других полей она не требует. Если требует — передать их со значениями по умолчанию, саму функцию не менять: она используется дровером чата.

- [ ] **Step 5: Пересобрать и проверить в браузере**

Скопировать `index.html` и `charts.js` в `bin\Release\net10.0\`, открыть прототип и пройти сценарий:

1. Пункт «📈 Аналитика по запросу» есть в меню и открывается.
2. Вопрос «просрочка по подразделениям» — появляется график, чипы переключателя, подпись источника.
3. Переключение вида работает мгновенно, без обращения к серверу.
4. Вопрос «сколько заданий просрочено» — плитки KPI либо столбики по трём процессам.
5. Второй вопрос добавляет строку истории; клик по первому возвращает его график.
6. Блок «Размышления» раскрывается и показывает шаги.

- [ ] **Step 6: Прогнать оба набора тестов**

```bash
node tests/charts.test.js && python tests/smoke.py http://localhost:5080
```

- [ ] **Step 7: Коммит**

```bash
git add index.html style.css
git commit -m "feat(ui): экран «Аналитика по запросу» с холстом и переключателем видов"
```

---

### Task 8: Документация

**Files:**
- Modify: `README.md`, `CLAUDE.md`

- [ ] **Step 1: Дописать в `README.md`**

В разделе «Возможности» — описание экрана: вопрос в свободной форме, график по фактическому результату харнесса, пять видов, переключатель, история в пределах сессии. В карте эндпоинтов — строка про `/api/ai/dataset/probe` (отладочный, только локальные вызовы). В разделе «Тесты» — про `node tests/charts.test.js` и актуальные числа обоих прогонов. В «Структуре» — про `charts.js`.

- [ ] **Step 2: Дописать в `CLAUDE.md`**

В «Проверку после изменений» — второй тестовый прогон: `node tests/charts.test.js` рядом со smoke, с пояснением, что он покрывает правила выбора вида и не требует ни сервера, ни модели. В «Известные хрупкости» — что страница всегда ходит в `/api/ai/sql` и потому недоступна при сетевом показе, как и режим глубокого анализа.

- [ ] **Step 3: Коммит**

```bash
git add README.md CLAUDE.md
git commit -m "docs: описать страницу «Аналитика по запросу» и тест правил выбора вида"
```

---

## Проверка плана на полноту

| Раздел спеки | Задача |
|---|---|
| §5 контракт `dataset` | Task 1 (типы), Task 2 (сборка), Task 4 (поле в ответе) |
| §6 сборка на сервере: инструмент, SQL, предзагрузка | Task 2 (JSON), Task 4 (три случая и указание модели) |
| §6 дополнение промпта описанием `chart` | Task 3 |
| §7 правила выбора вида | Task 5 (`pickViews` + тест) |
| §7 пять представлений, серии рядом, топ-20 с подписью, кольцо при ≤6 | Task 6 |
| §8 экран, история, подпись источника | Task 7 |
| §9 вырожденные случаи | Task 7 (ошибка, нет датасета, спиннер с временем, усечение) |
| §10 тестирование | Task 5 и 6 (Node, 20 проверок), Task 1, 2, 4 (smoke), Task 7 (браузер) |
| §11 вне объёма | ни одна задача не добавляет выгрузку, схемы связей и сохранение истории |
