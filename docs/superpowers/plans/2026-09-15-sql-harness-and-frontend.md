# ИИ-харнесс запросов к БД и выравнивание фронтенда — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Дать ИИ-чату прототипа режим, в котором модель сама достаёт данные — сначала из готовых инструментов дашборда, а для нестандартных срезов запросом к базе RX — и привести фронтенд прототипа к дизайн-языку presale-dashboard.

**Architecture:** Многошаговый агент внутри `Program.cs`: на каждом шаге модель возвращает один JSON-объект (`tool` / `schema` / `sql` / `answer`), код его исполняет и возвращает результат следующим сообщением. Ключевые метрики берутся у тех же сборщиков, что рисуют экран; произвольный SQL проходит валидатор и выполняется в read-only транзакции. Ответ отдаётся одним JSON с протоколом шагов, чат показывает его раскрывающимся блоком «Размышления».

**Tech Stack:** .NET 10, `System.Net.HttpListener`, Npgsql, `System.Text.Json`, ванильный JS в `index.html`, smoke-тесты на Python (`tests/smoke.py`).

**Spec:** `docs/superpowers/specs/2026-09-15-sql-harness-and-frontend-design.md`

## Global Constraints

- **Только `SELECT`.** Приложение никогда не пишет в БД RX. Правка, добавляющая `INSERT`/`UPDATE`/`DELETE` или DDL — ошибка, а не фича.
- **Секретов в коде нет.** Пароль БД и токен LLM живут только в `config.json` рядом с exe или в env `ARMGOV_DB_PASSWORD` / `ARMGOV_LLM_TOKEN`.
- **Метрики должны сходиться друг с другом.** Цифра в ответе ИИ обязана совпадать с цифрой на экране.
- **Уведомления исключаются из статистики** (типы `*Notice`/`*Notification`).
- Ветка работы: `ai-sql-harness`. Язык кода, комментариев, UI и коммитов — русский.
- Перед сборкой в новой сессии PowerShell: `$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`.
- Сборка и запуск: `dotnet build -c Release && bin\Release\net10.0\armgov-standalone.exe`. `index.html`, `style.css`, `tokens.css` читаются сервером на каждый запрос — правка фронта не требует пересборки, но файл должен быть обновлён в `bin\Release\net10.0\`.
- Прогон тестов: `python tests/smoke.py http://localhost:5080`. До начала работ — `PASS=53 FAIL=0`.

## Структура файлов

| Файл | Ответственность |
|---|---|
| `style.css` (создать) | Все стили прототипа: перенесены из `index.html`, компоненты выровнены по presale |
| `inter-400.woff2`, `inter-600.woff2`, `inter-700.woff2` (создать) | Шрифт Inter локально, без Google Fonts |
| `index.html` (правка) | Убрать блок `<style>`, подключить `style.css`; тумблер и блок размышлений в чате |
| `armgov-standalone.csproj` (правка) | Копирование `style.css` и шрифтов в вывод сборки |
| `Program.cs` (правка) | `SqlCheck`, `SqlRun`, `ToolCall`, `SchemaHelp`, `SqlAgentAsk`, четыре эндпоинта |
| `tests/smoke.py` (правка) | Проверки валидатора, инструментов, маршрутизации и цикла |

---

### Task 1: Стили из `index.html` — в `style.css`

Чистый перенос без единого изменения внешнего вида. Отдельной задачей — чтобы следующая (выравнивание компонентов) читалась в диффе как содержательная правка, а не тонула в переносе 200 строк.

**Files:**
- Create: `style.css`
- Modify: `index.html` (блок `<style>`, строки 6–114), `armgov-standalone.csproj`
- Test: `tests/smoke.py` (существующий прогон)

**Interfaces:**
- Consumes: ничего
- Produces: файл `style.css`, отдаваемый сервером по `/style.css` (обработчик статики в `Program.cs:340` уже умеет `.css`)

- [ ] **Step 1: Снять эталонные скриншоты пяти экранов**

Запустить прототип и сохранить скриншоты «до»: стартовая, «Аналитика процессов», «Поручения», «Обращения граждан», «Бэк-офис». Без них проверить «внешний вид не изменился» нечем.

```bash
dotnet build -c Release && start bin\Release\net10.0\armgov-standalone.exe
```

- [ ] **Step 2: Перенести содержимое `<style>` в `style.css`**

Вырезать из `index.html` всё между `<style>` и `</style>` и положить в новый файл `style.css` без изменений. В начало файла — шапку:

```css
/* =========================================================================
   АРМ руководителя — компонентные стили. Токены (цвета/размеры/тени) — в tokens.css.
   Дизайн-язык Directum Platform.OGV, общий с presale-dashboard.
   ========================================================================= */
```

- [ ] **Step 3: Подключить файл в `index.html`**

Заменить блок `<style>…</style>` на строку после `tokens.css`:

```html
<link rel="stylesheet" href="/tokens.css">
<link rel="stylesheet" href="/style.css">
```

- [ ] **Step 4: Добавить копирование в сборку**

В `armgov-standalone.csproj`, в `ItemGroup` со статикой, рядом с `tokens.css`:

```xml
<None Include="style.css" CopyToOutputDirectory="PreserveNewest" />
```

- [ ] **Step 5: Пересобрать и проверить, что ничего не поехало**

```bash
dotnet build -c Release && python tests/smoke.py http://localhost:5080
```

Ожидается: `PASS=53 FAIL=0`. Затем открыть все пять экранов и сравнить со скриншотами из шага 1 — расхождений быть не должно ни одного: это перенос, а не правка.

- [ ] **Step 6: Коммит**

```bash
git add style.css index.html armgov-standalone.csproj
git commit -m "refactor(css): вынести стили прототипа из index.html в style.css"
```

---

### Task 2: Шрифт Inter локально

Токен `--font` в `tokens.css` уже ссылается на Inter, но сам шрифт не подключён — прототип рисуется в Segoe UI, а presale в Inter.

**Files:**
- Create: `inter-400.woff2`, `inter-600.woff2`, `inter-700.woff2`
- Modify: `style.css`, `armgov-standalone.csproj`, `Program.cs:340-347` (типы контента статики)

**Interfaces:**
- Consumes: `style.css` из Task 1
- Produces: `@font-face` для Inter 400/600/700; `--font` начинает резолвиться

- [ ] **Step 1: Положить файлы шрифта в корень проекта**

Взять три файла подмножества latin+cyrillic с [rsms.me/inter](https://rsms.me/inter/) (лицензия SIL OFL) и назвать `inter-400.woff2`, `inter-600.woff2`, `inter-700.woff2`. Кириллица обязательна — интерфейс русский.

- [ ] **Step 2: Научить сервер отдавать woff2**

В `Program.cs`, в обработчике статики, где определяется `ct` (строка ~344), добавить тип:

```csharp
var ct = fname.EndsWith(".css") ? "text/css"
       : fname.EndsWith(".svg") ? "image/svg+xml"
       : fname.EndsWith(".js") ? "application/javascript"
       : fname.EndsWith(".woff2") ? "font/woff2"
       : "application/octet-stream";
```

Тут же рядом есть ветка, читающая файл как UTF-8 текст (`File.ReadAllText`) — для шрифта она не годится, нужен байтовый вывод. Добавить перед ней:

```csharp
if (fname.EndsWith(".woff2"))
{
    var bytes = File.ReadAllBytes(fp);
    ctx.Response.StatusCode = 200; ctx.Response.ContentType = "font/woff2";
    ctx.Response.AddHeader("Cache-Control", "public, max-age=604800");
    ctx.Response.ContentLength64 = bytes.Length;
    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
    ctx.Response.OutputStream.Close();
    return;
}
```

- [ ] **Step 3: Объявить `@font-face` в начале `style.css`**

Сразу после шапки файла, до всех правил:

```css
@font-face{font-family:'Inter';font-style:normal;font-weight:400;font-display:swap;src:url('/inter-400.woff2') format('woff2')}
@font-face{font-family:'Inter';font-style:normal;font-weight:600;font-display:swap;src:url('/inter-600.woff2') format('woff2')}
@font-face{font-family:'Inter';font-style:normal;font-weight:700;font-display:swap;src:url('/inter-700.woff2') format('woff2')}
```

- [ ] **Step 4: Добавить шрифты в сборку**

В `armgov-standalone.csproj`:

```xml
<None Include="inter-400.woff2" CopyToOutputDirectory="PreserveNewest" />
<None Include="inter-600.woff2" CopyToOutputDirectory="PreserveNewest" />
<None Include="inter-700.woff2" CopyToOutputDirectory="PreserveNewest" />
```

- [ ] **Step 5: Проверить, что шрифт реально приехал**

Пересобрать, открыть прототип и выполнить в консоли браузера:

```js
document.fonts.check('600 14px Inter')
```

Ожидается `true`. Плюс `curl -s -o /dev/null -w "%{http_code} %{content_type}\n" http://localhost:5080/inter-400.woff2` → `200 font/woff2`.

- [ ] **Step 6: Прогнать smoke и закоммитить**

```bash
python tests/smoke.py http://localhost:5080
git add inter-*.woff2 style.css armgov-standalone.csproj Program.cs
git commit -m "feat(ui): подключить Inter локально, без Google Fonts"
```

---

### Task 3: Компоненты по дизайн-языку presale

**Files:**
- Modify: `style.css`
- Reference: `C:\Users\Korotaev_NO\Desktop\Проекты\presale-dashboard\static\style.css`

**Interfaces:**
- Consumes: `style.css`, Inter из Task 2
- Produces: выровненные `.btn`, поля ввода, вкладки, таблицы, KPI-карточки, чат-бабблы

- [ ] **Step 1: Кнопки**

Заменить существующие правила `.btn` / `.btn-ghost` в `style.css` на вариант presale:

```css
.btn{display:inline-flex;align-items:center;justify-content:center;gap:7px;min-height:40px;padding:8px 18px;
  border:1px solid transparent;border-radius:var(--radius-sm);background:var(--accent);color:#fff;
  font-size:13px;font-weight:600;line-height:1.2;cursor:pointer;font-family:inherit}
.btn:hover{filter:brightness(1.06)}
.btn:active{transform:translateY(1px)}
.btn:disabled{opacity:.6;cursor:not-allowed;filter:none}
.btn-ghost{background:transparent;border-color:var(--border);color:var(--muted)}
.btn-ghost:hover{border-color:var(--accent);color:var(--accent);filter:none}
.btn-sm{min-height:32px;padding:6px 12px;font-size:12px}
```

- [ ] **Step 2: Поля ввода с фокус-кольцом**

```css
input,select,textarea{font:inherit}
input[type=text],input[type=number],select,textarea{
  padding:9px 12px;min-height:38px;border:1px solid var(--border);border-radius:var(--radius-sm);
  background:var(--surface);color:var(--text);outline:none}
input[type=text]:focus,input[type=number]:focus,select:focus,textarea:focus{
  border-color:var(--accent);box-shadow:0 0 0 3px var(--accent-soft)}
```

- [ ] **Step 3: Таблицы**

Привести табличные правила к presale: шапка на `--surface-2`, заглавные буквы, `tabular-nums` в числовых ячейках.

```css
table th{background:var(--surface-2);color:var(--muted);font-size:11px;font-weight:700;
  text-transform:uppercase;letter-spacing:.06em;padding:9px 12px;text-align:left}
table td{padding:9px 12px;border-bottom:1px solid var(--border);font-variant-numeric:tabular-nums}
table tbody tr:last-child td{border-bottom:0}
```

- [ ] **Step 4: KPI-карточка с верхней акцентной гранью**

```css
.kpi{border-top:3px solid var(--accent)}
.kpi-label{font-size:11.5px;font-weight:600;color:var(--muted);letter-spacing:.04em;text-transform:uppercase;margin-bottom:6px}
.kpi-value{font-size:26px;font-weight:700;color:var(--text);letter-spacing:-.015em;line-height:1.1;font-variant-numeric:tabular-nums}
```

Существующие правила `.kpi`, `.kpi-label`, `.kpi-value` в `style.css` заменить, а не дублировать.

- [ ] **Step 5: Вывести оставшиеся хардкод-цвета в токены**

Найти их:

```bash
grep -o "#[0-9a-fA-F]\{3,6\}\b" style.css index.html | sort | uniq -c
```

Ожидается 12 вхождений: `#b3471f`, `#7a1f12`, `#1f3a5f`, `#FBE9E9`, `#E6F4EA` и подобные. Каждое заменить ближайшим токеном: красные → `var(--red)` / `var(--red-soft)`, зелёные → `var(--green)` / `var(--green-soft)`, тёмно-синие → `var(--title)`. Цвета в SVG-генераторах (`Program.cs`, функция `txt`) уже используют `var(--muted)` — их не трогать.

- [ ] **Step 6: Сверить пять экранов и прогнать smoke**

```bash
python tests/smoke.py http://localhost:5080
```

Ожидается `PASS=53 FAIL=0`. Экраны сравнить со скриншотами Task 1: изменения должны быть только в типографике, кнопках, полях, таблицах и KPI — компоновка и состав блоков прежние.

- [ ] **Step 7: Коммит**

```bash
git add style.css index.html
git commit -m "style(ui): выровнять компоненты прототипа по дизайн-языку presale"
```

---

### Task 4: Валидатор SQL и эндпоинт проверки

Первый кирпич харнесса и единственное место, где ошибка стоит дорого. Поэтому тесты пишутся раньше кода.

**Files:**
- Modify: `Program.cs` (новый регион перед `// ---------- helpers ----------`, роутинг `Program.cs:322-324`)
- Test: `tests/smoke.py`

**Interfaces:**
- Consumes: ничего
- Produces:
  - `static (bool ok, string reason, string effective) SqlCheck(string sql)` — валидатор без побочных эффектов
  - `GET /api/ai/sql/check?q=<sql>` → `{ok: bool, reason: string|null, effective: string|null}`

- [ ] **Step 1: Написать падающие проверки в smoke**

В конец `tests/smoke.py`, перед секцией «ИТОГ», добавить:

```python
# ---------------- ХАРНЕСС: валидатор SQL ----------------
section("Валидатор SQL  /api/ai/sql/check")
import urllib.parse
def sqlcheck(q):
    st, d = _req("/api/ai/sql/check?q=" + urllib.parse.quote(q))
    return d
try:
    d = sqlcheck("select count(*) from sungero_wf_task limit 1")
    check("корректный запрос пропущен", d.get("ok") is True, d.get("reason") or "")

    d = sqlcheck("drop table sungero_wf_task")
    check("drop table отклонён", d.get("ok") is False and "drop" in (d.get("reason") or ""))

    d = sqlcheck("select 1; delete from sungero_wf_task")
    check("два оператора отклонены", d.get("ok") is False)

    d = sqlcheck("select 1 -- безобидно\n; delete from sungero_wf_task")
    check("маскировка комментарием не проходит", d.get("ok") is False)

    d = sqlcheck("select /* delete */ count(*) from sungero_wf_task")
    check("запрет не срабатывает на слове в комментарии",
          d.get("ok") is True, d.get("reason") or "")

    d = sqlcheck("select id from sungero_wf_task")
    check("запросу без limit добавлена обёртка",
          d.get("ok") is True and "limit 200" in (d.get("effective") or "").lower(),
          d.get("effective"))

    d = sqlcheck("update sungero_wf_task set subject = 'x'")
    check("update отклонён", d.get("ok") is False)
except Exception as e:
    check("валидатор доступен", False, str(e))
```

- [ ] **Step 2: Убедиться, что проверки падают**

```bash
python tests/smoke.py http://localhost:5080
```

Ожидается: семь новых `[FAIL]` — эндпоинта ещё нет (HTTP 404 → исключение → `валидатор доступен: False`).

- [ ] **Step 3: Реализовать валидатор**

В `Program.cs` добавить регион (потребуется `using System.Text.RegularExpressions;` — проверить, есть ли он в шапке файла, и добавить при отсутствии):

```csharp
    // ---------- ИИ-харнесс: валидатор SQL ----------
    // Первый из двух рубежей (второй — read-only транзакция в SqlRun).
    // Проверяется и исполняется ОДИН И ТОТ ЖЕ текст: комментарии вырезаются до проверки,
    // иначе валидатор смотрел бы на одно, а база получала другое.
    static readonly string[] SqlDeny = {
        "insert","update","delete","drop","alter","create","truncate","grant","revoke",
        "copy","vacuum","call","do","set","reset","begin","commit","rollback",
        "dblink","pg_read_file","pg_ls_dir","lo_import","lo_export","pg_sleep" };

    static (bool ok, string reason, string effective) SqlCheck(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return (false, "пустой запрос", null);
        var s = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        s = Regex.Replace(s, @"--[^\n]*", " ");
        s = s.Trim();
        while (s.EndsWith(";")) s = s.Substring(0, s.Length - 1).Trim();
        if (s.Contains(";")) return (false, "разрешён только один оператор", null);
        var low = s.ToLowerInvariant();
        if (!(low.StartsWith("select") || low.StartsWith("with")))
            return (false, "запрос должен начинаться с SELECT или WITH", null);
        foreach (var w in SqlDeny)
            if (Regex.IsMatch(low, @"\b" + w + @"\b"))
                return (false, "запрещённая конструкция: " + w, null);
        var eff = Regex.IsMatch(low, @"\blimit\b") ? s : "select * from (" + s + ") t limit 200";
        return (true, null, eff);
    }
```

- [ ] **Step 4: Добавить эндпоинт**

В роутинг `Program.cs`, рядом с остальными `/api/ai/*` (строка ~324):

```csharp
            case "/api/ai/sql/check":
            {
                var (ok, reason, eff) = SqlCheck(q["q"]);
                J(ctx, new { ok, reason, effective = eff });
                return;
            }
```

- [ ] **Step 5: Пересобрать и убедиться, что проверки прошли**

```bash
dotnet build -c Release && python tests/smoke.py http://localhost:5080
```

Ожидается `PASS=60 FAIL=0`.

- [ ] **Step 6: Коммит**

```bash
git add Program.cs tests/smoke.py
git commit -m "feat(ai): валидатор SQL и эндпоинт проверки для харнесса"
```

---

### Task 5: Исполнитель SQL в read-only транзакции

**Files:**
- Modify: `Program.cs` (регион харнесса, рядом с `SqlCheck`)
- Test: `tests/smoke.py`

**Interfaces:**
- Consumes: `SqlCheck` из Task 4
- Produces: `static (List<string> cols, List<object[]> rows, int ms, bool truncated) SqlRun(string effective, int maxRows = 200)`

- [ ] **Step 1: Написать падающую проверку**

В `tests/smoke.py`, в ту же секцию харнесса:

```python
try:
    st, d = _req("/api/ai/sql/run?q=" + urllib.parse.quote(
        "select count(*) as c from sungero_wf_task"))
    check("исполнитель вернул строки", isinstance(d.get("rows"), list) and len(d["rows"]) == 1)
    check("исполнитель вернул колонки", d.get("cols") == ["c"])
    check("исполнитель отдаёт время", isinstance(d.get("ms"), int))

    st, d = _req("/api/ai/sql/run?q=" + urllib.parse.quote(
        "update sungero_wf_task set subject='x'"))
    check("запись не исполняется", bool(d.get("error")))
except Exception as e:
    check("исполнитель доступен", False, str(e))
```

- [ ] **Step 2: Убедиться, что проверки падают**

```bash
python tests/smoke.py http://localhost:5080
```

Ожидается четыре новых `[FAIL]`.

- [ ] **Step 3: Реализовать исполнитель**

```csharp
    // Второй рубеж: даже пропущенная валидатором запись будет отклонена самим PostgreSQL.
    // statement_timeout не даёт повесить стенд тяжёлым джойном.
    static (List<string> cols, List<object[]> rows, int ms, bool truncated) SqlRun(string effective, int maxRows = 200)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cols = new List<string>(); var rows = new List<object[]>(); bool more = false;
        using var c = new NpgsqlConnection(Cs); c.Open();
        using var tx = c.BeginTransaction();
        using (var pre = new NpgsqlCommand("set transaction read only; set local statement_timeout = '10s'", c, tx))
            pre.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand(effective, c, tx))
        using (var rd = cmd.ExecuteReader())
        {
            int n = Math.Min(rd.FieldCount, 60);
            for (int i = 0; i < n; i++) cols.Add(rd.GetName(i));
            while (rd.Read())
            {
                if (rows.Count >= maxRows) { more = true; break; }
                var r = new object[n];
                for (int i = 0; i < n; i++)
                {
                    var v = rd.IsDBNull(i) ? null : rd.GetValue(i);
                    r[i] = v is string s ? Trunc(s, 200) : v;
                }
                rows.Add(r);
            }
        }
        tx.Rollback();
        return (cols, rows, (int)sw.ElapsedMilliseconds, more);
    }
```

- [ ] **Step 4: Добавить эндпоинт**

```csharp
            case "/api/ai/sql/run":
            {
                var (ok, reason, eff) = SqlCheck(q["q"]);
                if (!ok) { J(ctx, new { error = reason }); return; }
                try
                {
                    var (cols, rows, ms, truncated) = SqlRun(eff);
                    J(ctx, new { cols, rows, ms, truncated, effective = eff });
                }
                catch (Exception ex) { J(ctx, new { error = ex.Message }); }
                return;
            }
```

- [ ] **Step 5: Проверить и убедиться в откате транзакции**

```bash
dotnet build -c Release && python tests/smoke.py http://localhost:5080
```

Ожидается `PASS=64 FAIL=0`. Дополнительно — ручная проверка, что read-only транзакция действительно блокирует запись мимо валидатора:

```bash
dotnet run --project tools/dbq -- "select count(*) from sungero_wf_task"
```

Число до и после прогона smoke должно совпадать.

- [ ] **Step 6: Коммит**

```bash
git add Program.cs tests/smoke.py
git commit -m "feat(ai): исполнение SQL в read-only транзакции с таймаутом и лимитами"
```

---

### Task 6: Реестр готовых инструментов

Девять инструментов — это те же сборщики, что рисуют экран. Идёт раньше цикла агента намеренно: сам по себе даёт полезный результат и проверяется без участия модели.

**Files:**
- Modify: `Program.cs` (регион харнесса), роутинг
- Test: `tests/smoke.py`

**Interfaces:**
- Consumes: существующие сборщики `BuildOverview(period)`, `BuildProcess(key, period)`, `BuildLeaders(by)`, `BuildLeaderTasks(by, id)`, `BuildStuck(key, period)`, `BuildByKind(key, period)`, `BuildDepartments(key, period)`, `BuildMyTasks()`, `BuildAppealTopics()`
- Produces:
  - `static object ToolCall(string name, JsonElement args)`
  - `static readonly (string name, string args, string desc)[] ToolCatalog`
  - `GET /api/ai/tools` → каталог; `GET /api/ai/tool?name=&args=<json>` → результат инструмента

- [ ] **Step 1: Написать падающие проверки**

```python
# ---------------- ХАРНЕСС: инструменты ----------------
section("Инструменты агента  /api/ai/tools")
try:
    st, cat = _req("/api/ai/tools")
    names = [t["name"] for t in cat.get("tools", [])]
    check("в каталоге девять инструментов", len(names) == 9, str(len(names)))
    for n in ["overview","process","leaders","leader_tasks","stuck",
              "by_kind","departments","my_tasks","appeal_topics"]:
        check(f"инструмент {n} в каталоге", n in names)

    st, d = _req("/api/ai/tool?name=overview&args=%7B%7D")
    check("overview через инструмент отвечает", "region" in d, str(list(d)[:4]))

    # согласованность: инструмент и эндпоинт экрана дают одно и то же
    st, direct = _req("/api/overview")
    check("инструмент overview совпадает с /api/overview",
          d.get("region", {}).get("throughput") == direct.get("region", {}).get("throughput"))

    st, d = _req("/api/ai/tool?name=leaders&args=" + urllib.parse.quote('{"by":"dept"}'))
    check("leaders(by=dept) отвечает", isinstance(d.get("items"), list))

    st, d = _req("/api/ai/tool?name=нет_такого&args=%7B%7D")
    check("неизвестный инструмент даёт понятную ошибку", "неизвестный" in (d.get("error") or ""))
except Exception as e:
    check("каталог инструментов доступен", False, str(e))
```

- [ ] **Step 2: Убедиться, что проверки падают**

```bash
python tests/smoke.py http://localhost:5080
```

- [ ] **Step 3: Реализовать каталог и вызов**

```csharp
    // ---------- ИИ-харнесс: готовые инструменты ----------
    // Ключевые цифры агент не считает сам: он берёт их у тех же сборщиков, что рисуют
    // экран. Иначе чат и дашборд разойдутся, а это нарушает правило о сходимости метрик.
    static readonly (string name, string args, string desc)[] ToolCatalog = {
        ("overview", "period?", "KPI по всем процессам: в работе, срок сегодня, просрочено, соблюдение сроков, что горит"),
        ("process", "key, period?", "Воронка и здоровье процесса. key: poruchenia | appeals | npa"),
        ("leaders", "by", "Исполнение по людям и структуре. by: performer | dept | bu. Содержит coOverdue — просрочку у соисполнителей"),
        ("leader_tasks", "by, id", "Задачи конкретного сотрудника или подразделения из leaders"),
        ("stuck", "key, period?", "Где застревает работа: долгострои и узкие места процесса"),
        ("by_kind", "key, period?", "Разрез процесса по видам поручений"),
        ("departments", "key, period?", "Разрез процесса по подразделениям"),
        ("my_tasks", "", "Личный контроль руководителя: его поручения и задания"),
        ("appeal_topics", "", "Тематики обращений граждан: разделы, темы, топ вопросов"),
    };

    static object ToolCall(string name, JsonElement args)
    {
        string S(string k, string def = null) =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty(k, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : def;
        switch (name)
        {
            case "overview": return BuildOverview(S("period"));
            case "process": return BuildProcess(S("key", "poruchenia"), S("period"));
            case "leaders": return BuildLeaders(S("by", "performer"));
            case "leader_tasks": return BuildLeaderTasks(S("by", "performer"), S("id"));
            case "stuck": return BuildStuck(S("key", "poruchenia"), S("period"));
            case "by_kind": return BuildByKind(S("key", "poruchenia"), S("period"));
            case "departments": return BuildDepartments(S("key", "poruchenia"), S("period"));
            case "my_tasks": return BuildMyTasks();
            case "appeal_topics": return BuildAppealTopics();
            default: throw new Exception("неизвестный инструмент: " + name);
        }
    }
```

- [ ] **Step 4: Добавить эндпоинты**

```csharp
            case "/api/ai/tools":
                J(ctx, new { tools = ToolCatalog.Select(t => new { name = t.name, args = t.args, desc = t.desc }) });
                return;
            case "/api/ai/tool":
            {
                try
                {
                    using var argDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(q["args"]) ? "{}" : q["args"]);
                    J(ctx, ToolCall(q["name"], argDoc.RootElement));
                }
                catch (Exception ex) { J(ctx, new { error = ex.Message }); }
                return;
            }
```

Если `using System.Linq;` в шапке `Program.cs` отсутствует — добавить.

- [ ] **Step 5: Проверить**

```bash
dotnet build -c Release && python tests/smoke.py http://localhost:5080
```

Ожидается `PASS=78 FAIL=0`. Важнее прочего — зелёная проверка «инструмент overview совпадает с /api/overview»: она и есть гарантия сходимости метрик.

- [ ] **Step 6: Коммит**

```bash
git add Program.cs tests/smoke.py
git commit -m "feat(ai): реестр из девяти готовых инструментов поверх сборщиков дашборда"
```

---

### Task 7: Словарь схемы и справка по таблице

**Files:**
- Modify: `Program.cs` (регион харнесса)
- Test: `tests/smoke.py`

**Interfaces:**
- Consumes: `Cs` (строка подключения)
- Produces:
  - `static string SchemaCore()` — текст словаря ядра для промпта
  - `static object SchemaHelp(string table)` → `{table, columns:[{name, type, samples:[…]}]}`
  - `GET /api/ai/schema?table=<имя>`

- [ ] **Step 1: Написать падающие проверки**

```python
section("Словарь схемы  /api/ai/schema")
try:
    st, d = _req("/api/ai/schema?table=sungero_wf_assignment")
    cols = [c["name"] for c in d.get("columns", [])]
    check("справка по таблице отдаёт колонки", len(cols) > 5, str(len(cols)))
    check("в колонках есть deadline", "deadline" in cols)
    check("у колонки есть тип", bool(d.get("columns", [{}])[0].get("type")))
    st, d = _req("/api/ai/schema?table=нет_такой_таблицы")
    check("несуществующая таблица — понятная ошибка", bool(d.get("error")) or d.get("columns") == [])
except Exception as e:
    check("справка по схеме доступна", False, str(e))
```

- [ ] **Step 2: Убедиться, что проверки падают**

```bash
python tests/smoke.py http://localhost:5080
```

- [ ] **Step 3: Реализовать словарь ядра**

Таблицы — ровно те, что прототип уже использует (проверено по `Program.cs`):

```csharp
    // ---------- ИИ-харнесс: схема ----------
    // Словарь ядра: только таблицы, которые дашборд реально использует. Всё остальное
    // агент добирает действием schema — так промпт остаётся коротким, а модель не слепа.
    static string SchemaCore() => string.Join("\n", new[] {
        "sungero_wf_task — задачи (поручения, обращения, НПА). id, subject, created, maintask (корневая задача),",
        "  assigneetai_recman_sungero (ответственный исполнитель), supervisor_recman_sungero (контролёр),",
        "  isundercontrol_recman_sungero (на контроле), processkind.",
        "sungero_wf_assignment — задания внутри задач. id, task, maintask, performer (исполнитель),",
        "  status (InProcess | Completed | Aborted), deadline (срок), completed (факт), created.",
        "sungero_core_recipient — сотрудники и подразделения. id, name,",
        "  department_company_sungero (подразделение), emplbunit_company_sungero (НОР).",
        "sungero_recman_taicoassignees — соисполнители поручения: task, assignee.",
        "sungero_recman_taiparts — пункты поручения: task, assignee.",
        "sungero_recman_taipartscoasgs — соисполнители пункта: task, coassignee.",
        "sungero_wf_workflowhistory — история движения по маршруту.",
        "sungero_system_entitytype — типы сущностей; из них берутся типы-уведомления.",
        "sungero_content_edoc — документы; name содержит тему обращения.",
        "sungero_wf_processkind — виды процессов.",
        "sungero_company_jobtitle — должности.",
        "sungero_parties_counterparty — контрагенты.",
        "",
        "ПРАВИЛА РАСЧЁТА (обязательны — иначе цифры разойдутся с дашбордом):",
        "— просрочка: status = 'InProcess' и deadline < now();",
        "— статус Aborted в статистику не входит;",
        "— уведомления (типы *Notice/*Notification) исключаются;",
        "— одно поручение = одна корневая задача maintask, дедупликация по ней.",
    });

    static object SchemaHelp(string table)
    {
        if (string.IsNullOrWhiteSpace(table) || !Regex.IsMatch(table, @"^[a-z0-9_]+$"))
            return new { error = "недопустимое имя таблицы" };
        var cols = new List<object>();
        using var c = new NpgsqlConnection(Cs); c.Open();
        using (var cmd = new NpgsqlCommand(
            "select column_name, data_type from information_schema.columns " +
            "where table_name = @t order by ordinal_position limit 80", c))
        {
            cmd.Parameters.AddWithValue("t", table);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) cols.Add(new { name = rd.GetString(0), type = rd.GetString(1) });
        }
        if (cols.Count == 0) return new { error = "таблица не найдена: " + table };
        return new { table, columns = cols };
    }
```

- [ ] **Step 4: Добавить эндпоинт**

```csharp
            case "/api/ai/schema":
                try { J(ctx, SchemaHelp(q["table"])); }
                catch (Exception ex) { J(ctx, new { error = ex.Message }); }
                return;
```

- [ ] **Step 5: Проверить**

```bash
dotnet build -c Release && python tests/smoke.py http://localhost:5080
```

Ожидается `PASS=82 FAIL=0`.

- [ ] **Step 6: Коммит**

```bash
git add Program.cs tests/smoke.py
git commit -m "feat(ai): словарь ядра схемы и справка по таблице для агента"
```

---

### Task 8: Цикл агента и эндпоинт `/api/ai/sql`

**Files:**
- Modify: `Program.cs` (регион харнесса), роутинг
- Test: `tests/smoke.py`

**Interfaces:**
- Consumes: `SqlCheck`, `SqlRun` (Task 4–5), `ToolCall`, `ToolCatalog` (Task 6), `SchemaCore`, `SchemaHelp` (Task 7), `LlmChat(object[] messages, int maxTokens, double temperature)`
- Produces: `POST /api/ai/sql` → `{reply, preloaded, steps, elapsedMs, truncated}` либо `{error}`

- [ ] **Step 1: Написать падающие проверки**

```python
section("Цикл агента  /api/ai/sql")
try:
    st, d = _req("/api/ai/sql", method="POST", body={"messages": [
        {"role": "user", "content": "Сколько заданий просрочено?"}]}, timeout=180)
    check("ответ непустой", bool(d.get("reply")), (d.get("error") or "")[:120])
    check("протокол шагов есть", isinstance(d.get("steps"), list))
    check("предзагрузка отработала", d.get("preloaded") == ["overview", "processes"])
    check("вопрос из готовых метрик не потребовал SQL",
          all(s.get("action") != "sql" for s in d.get("steps", [])),
          str([s.get("action") for s in d.get("steps", [])]))
    check("уложились в бюджет", isinstance(d.get("elapsedMs"), int) and d["elapsedMs"] < 70000)

    st, d = _req("/api/ai/sql", method="POST", body={"messages": [
        {"role": "user", "content":
         "Сколько заданий создано в 2023 году? Это не считает дашборд, нужен запрос."}]},
        timeout=180)
    check("нестандартный вопрос дошёл до SQL",
          any(s.get("action") == "sql" for s in d.get("steps", [])),
          str([s.get("action") for s in d.get("steps", [])]))
    check("у шага SQL виден текст запроса",
          any(s.get("sql") for s in d.get("steps", []) if s.get("action") == "sql"))
except Exception as e:
    check("агент доступен", False, str(e))
```

- [ ] **Step 2: Убедиться, что проверки падают**

```bash
python tests/smoke.py http://localhost:5080
```

- [ ] **Step 3: Реализовать системный промпт**

```csharp
    // ---------- ИИ-харнесс: цикл агента ----------
    const int AgentMaxSteps = 5;
    const int AgentBudgetMs = 60000;

    static string AgentSystemPrompt()
    {
        var tools = string.Join("\n", ToolCatalog.Select(t =>
            "— " + t.name + "(" + t.args + "): " + t.desc));
        return
"Ты — аналитик процессов региона. Отвечаешь руководителю по-русски, кратко, цифрами.\n" +
"Данные добываешь сам, по одному действию за раз. Каждый ответ — РОВНО ОДИН JSON-объект, без текста вокруг:\n" +
"{\"thought\":\"одно-два предложения\",\"action\":\"tool\",\"tool\":\"имя\",\"args\":{…}}\n" +
"{\"thought\":\"…\",\"action\":\"schema\",\"table\":\"имя_таблицы\"}\n" +
"{\"thought\":\"…\",\"action\":\"sql\",\"query\":\"SELECT …\",\"purpose\":\"что считаем\"}\n" +
"{\"thought\":\"…\",\"action\":\"answer\",\"text\":\"итоговый ответ\"}\n\n" +
"ИНСТРУМЕНТЫ (готовые метрики дашборда — те же числа, что видит руководитель на экране):\n" + tools + "\n\n" +
"ПРАВИЛО ВЫБОРА: если вопрос закрывается инструментом — обязан вызвать инструмент.\n" +
"action=sql разрешён ТОЛЬКО для среза, которого не даёт ни один инструмент.\n" +
"Если число уже есть в предзагруженных данных ниже — не вызывай ничего, сразу answer.\n\n" +
"SQL: только SELECT, один оператор, PostgreSQL. Схема ядра:\n" + SchemaCore() + "\n\n" +
"Не выдумывай числа: в ответе только то, что вернули инструменты или запрос. " +
"Максимум " + AgentMaxSteps + " действий — расходуй их экономно.";
    }
```

- [ ] **Step 4: Реализовать разбор ответа модели**

Модель нередко оборачивает JSON в ```-блок или добавляет текст вокруг — берём первый сбалансированный объект:

```csharp
    static JsonElement? AgentParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int start = text.IndexOf('{');
        while (start >= 0)
        {
            int depth = 0; bool inStr = false, esc = false;
            for (int i = start; i < text.Length; i++)
            {
                char ch = text[i];
                if (esc) { esc = false; continue; }
                if (ch == '\\' && inStr) { esc = true; continue; }
                if (ch == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (ch == '{') depth++;
                else if (ch == '}' && --depth == 0)
                {
                    var frag = text.Substring(start, i - start + 1);
                    try { return JsonDocument.Parse(frag).RootElement.Clone(); }
                    catch { break; }
                }
            }
            start = text.IndexOf('{', start + 1);
        }
        return null;
    }
```

- [ ] **Step 5: Реализовать цикл**

```csharp
    static object SqlAgentAsk(string body)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var steps = new List<object>();
        var msgs = new List<object> { new { role = "system", content = AgentSystemPrompt() } };

        // Предзагрузка: размер промпта почти ничего не стоит (замер в спеке),
        // зато частые вопросы закрываются без единого шага и цифрой с экрана.
        var pre = new { overview = BuildOverview(null), processes = BuildProcesses() };
        msgs.Add(new { role = "user", content = "Предзагруженные данные дашборда (JSON):\n" +
                                                 JsonSerializer.Serialize(pre) });
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("messages", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var m in arr.EnumerateArray())
                {
                    var role = m.TryGetProperty("role", out var rr) ? rr.GetString() : null;
                    var content = m.TryGetProperty("content", out var cc) ? cc.GetString() : null;
                    if ((role == "user" || role == "assistant") && !string.IsNullOrEmpty(content))
                        msgs.Add(new { role, content });
                }
        }
        catch (Exception ex) { return new { error = "bad request: " + ex.Message }; }

        string reply = null; bool truncated = false;
        for (int n = 1; n <= AgentMaxSteps; n++)
        {
            if (sw.ElapsedMilliseconds > AgentBudgetMs) { truncated = true; break; }
            var stepSw = System.Diagnostics.Stopwatch.StartNew();
            string raw;
            try { raw = LlmChat(msgs.ToArray(), 700, 0.2); }
            catch (Exception ex) { return new { error = "ИИ недоступен: " + ex.Message }; }
            var parsed = AgentParse(raw);
            if (parsed == null)
            {
                msgs.Add(new { role = "assistant", content = raw });
                msgs.Add(new { role = "user", content = "Ответ не разобран. Верни РОВНО один JSON-объект без текста вокруг." });
                steps.Add(new { n, action = "error", thought = "", error = "ответ не разобран", ms = (int)stepSw.ElapsedMilliseconds });
                continue;
            }
            var el = parsed.Value;
            string Str(string k) => el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
            string action = Str("action"), thought = Str("thought");
            msgs.Add(new { role = "assistant", content = raw });

            if (action == "answer") { reply = Str("text"); steps.Add(new { n, action, thought, ms = (int)stepSw.ElapsedMilliseconds }); break; }

            if (action == "tool")
            {
                var name = Str("tool");
                var args = el.TryGetProperty("args", out var a) ? a : default;
                try
                {
                    var res = ToolCall(name, args);
                    var json = Trunc(JsonSerializer.Serialize(res), 12000);
                    msgs.Add(new { role = "user", content = "Результат " + name + ":\n" + json });
                    steps.Add(new { n, action, thought, tool = name, ms = (int)stepSw.ElapsedMilliseconds });
                }
                catch (Exception ex)
                {
                    msgs.Add(new { role = "user", content = "Инструмент упал: " + ex.Message });
                    steps.Add(new { n, action, thought, tool = name, error = ex.Message, ms = (int)stepSw.ElapsedMilliseconds });
                }
                continue;
            }

            if (action == "schema")
            {
                var t = Str("table");
                var res = SchemaHelp(t);
                msgs.Add(new { role = "user", content = "Схема " + t + ":\n" + Trunc(JsonSerializer.Serialize(res), 6000) });
                steps.Add(new { n, action, thought, table = t, ms = (int)stepSw.ElapsedMilliseconds });
                continue;
            }

            if (action == "sql")
            {
                var query = Str("query");
                var (ok, reason, eff) = SqlCheck(query);
                if (!ok)
                {
                    msgs.Add(new { role = "user", content = "Запрос отклонён: " + reason + ". Исправь." });
                    steps.Add(new { n, action, thought, sql = query, error = reason, ms = (int)stepSw.ElapsedMilliseconds });
                    continue;
                }
                try
                {
                    var (cols, rows, ms, more) = SqlRun(eff, 50);
                    msgs.Add(new { role = "user", content = "Результат запроса:\n" +
                        JsonSerializer.Serialize(new { cols, rows, truncated = more }) });
                    steps.Add(new { n, action, thought, sql = query, purpose = Str("purpose"),
                                    cols, rows = rows.Count, preview = rows.Take(5),
                                    ms = (int)stepSw.ElapsedMilliseconds });
                }
                catch (Exception ex)
                {
                    msgs.Add(new { role = "user", content = "Запрос упал с ошибкой:\n" + ex.Message + "\nИсправь и повтори." });
                    steps.Add(new { n, action, thought, sql = query, error = ex.Message, ms = (int)stepSw.ElapsedMilliseconds });
                }
                continue;
            }

            msgs.Add(new { role = "user", content = "Неизвестное действие: " + action });
            steps.Add(new { n, action, thought, error = "неизвестное действие", ms = (int)stepSw.ElapsedMilliseconds });
        }

        if (reply == null)
        {
            truncated = true;
            msgs.Add(new { role = "user", content = "Шаги закончились. Ответь по уже собранным данным одним текстом, без JSON." });
            try { reply = LlmChat(msgs.ToArray(), 600, 0.3); }
            catch (Exception ex) { return new { error = "ИИ недоступен: " + ex.Message }; }
        }
        return new { reply, preloaded = new[] { "overview", "processes" },
                     steps, elapsedMs = (int)sw.ElapsedMilliseconds, truncated };
    }
```

- [ ] **Step 6: Добавить эндпоинт**

```csharp
            case "/api/ai/sql": J(ctx, SqlAgentAsk(ReadBody(ctx))); return;
```

- [ ] **Step 7: Проверить**

```bash
dotnet build -c Release && python tests/smoke.py http://localhost:5080
```

Ожидается `PASS=89 FAIL=0`. Если проверка «вопрос из готовых метрик не потребовал SQL» падает — это не баг кода, а слабое правило маршрутизации: усилить формулировку в `AgentSystemPrompt` и добавить пример «Сколько просрочено → ответ из предзагруженных данных, без действий».

- [ ] **Step 8: Коммит**

```bash
git add Program.cs tests/smoke.py
git commit -m "feat(ai): многошаговый агент с инструментами, схемой и SQL"
```

---

### Task 9: Тумблер и блок размышлений в чате

**Files:**
- Modify: `index.html` (разметка панели чата — строка ~258; функции `sendChat`, `renderChat` — строки ~905–925), `style.css`

**Interfaces:**
- Consumes: `POST /api/ai/sql` из Task 8
- Produces: режим «Глубокий анализ (SQL)» в дровере чата, раскрывающийся блок «Размышления»

- [ ] **Step 1: Добавить тумблер в разметку**

В `index.html`, в блок `#chat-quick` (строка ~257), перед ним вставить:

```html
<div style="padding:8px 14px 0;background:var(--surface)">
  <label class="agent-toggle"><input type="checkbox" id="agent-mode" onchange="saveAgentMode()"> Глубокий анализ (SQL)</label>
</div>
```

- [ ] **Step 2: Добавить стили в `style.css`**

```css
.agent-toggle{display:inline-flex;align-items:center;gap:8px;font-size:13px;color:var(--muted);cursor:pointer}
.agent-toggle input{accent-color:var(--accent)}
.think{margin-top:8px;border-top:1px solid var(--border);padding-top:6px}
.think>summary{cursor:pointer;font-size:12px;color:var(--muted);list-style:none}
.think>summary:hover{color:var(--accent)}
.think-step{margin:8px 0;padding-left:10px;border-left:2px solid var(--border)}
.think-act{font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.05em;color:var(--accent)}
.think-thought{font-size:12.5px;color:var(--text);margin:2px 0}
.think-sql{font-family:var(--font-mono);font-size:11.5px;background:var(--surface-2);border:1px solid var(--border);
  border-radius:var(--radius-sm);padding:8px 10px;overflow-x:auto;white-space:pre-wrap;margin-top:4px}
.think-meta{font-size:11px;color:var(--subtle);margin-top:3px}
```

- [ ] **Step 3: Сохранять положение тумблера**

Рядом с прочими функциями чата в `index.html`:

```js
function saveAgentMode(){try{localStorage.setItem('armgov-agent',document.getElementById('agent-mode').checked?'1':'0');}catch(e){}}
function loadAgentMode(){try{var v=localStorage.getItem('armgov-agent');if(v==='1')document.getElementById('agent-mode').checked=true;}catch(e){}}
```

Вызов `loadAgentMode()` добавить в `toggleChat()` там, где уже вызывается `renderChatQuick()`.

- [ ] **Step 4: Научить `sendChat` маршрутизировать по тумблеру**

Заменить тело `sendChat` (строка ~915):

```js
function sendChat(){
  var inp=document.getElementById('chat-input');var t=(inp.value||'').trim();if(!t)return;
  var agent=document.getElementById('agent-mode')&&document.getElementById('agent-mode').checked;
  CHAT.push({role:'user',content:t});inp.value='';
  renderChat([{role:'assistant',content:agent?'… смотрю данные':'… думаю'}]);
  var btn=document.getElementById('chat-send');btn.disabled=true;
  fetch(agent?'/api/ai/sql':'/api/ai/chat',{method:'POST',headers:{'Content-Type':'application/json'},
    body:JSON.stringify({messages:CHAT})}).then(function(r){return r.json();}).then(function(d){
    CHAT.push({role:'assistant',content:d.error?('⚠ ИИ недоступен: '+d.error):(d.reply||'(пустой ответ)'),steps:d.steps,elapsedMs:d.elapsedMs,truncated:d.truncated});
    renderChat();btn.disabled=false;
  }).catch(function(e){CHAT.push({role:'assistant',content:'⚠ ошибка связи: '+e});renderChat();btn.disabled=false;});
}
```

- [ ] **Step 5: Нарисовать блок размышлений**

Добавить функцию и подмешать её в пузырь ассистента:

```js
function thinkBlock(m){
  if(!m.steps||!m.steps.length)return '';
  var secs=((m.elapsedMs||0)/1000).toFixed(1).replace('.',',');
  var body=m.steps.map(function(s){
    var h='<div class="think-step"><div class="think-act">'+esc(s.action||'')+(s.tool?' · '+esc(s.tool):'')+(s.table?' · '+esc(s.table):'')+'</div>';
    if(s.thought)h+='<div class="think-thought">'+esc(s.thought)+'</div>';
    if(s.sql)h+='<div class="think-sql">'+esc(s.sql)+'</div>';
    if(s.error)h+='<div class="think-meta" style="color:var(--red)">'+esc(s.error)+'</div>';
    var meta=[];if(typeof s.rows==='number')meta.push(s.rows+' строк');if(s.ms)meta.push(s.ms+' мс');
    if(meta.length)h+='<div class="think-meta">'+meta.join(' · ')+'</div>';
    return h+'</div>';
  }).join('');
  var warn=m.truncated?'<div class="think-meta" style="color:var(--amber)">Модель не уложилась в лимит шагов — ответ по собранным данным</div>':'';
  return '<details class="think"><summary>Размышления · '+m.steps.length+' шагов · '+secs+' с</summary>'+body+warn+'</details>';
}
```

В `chatBubble` дописать блок после содержимого — для этого функция должна принимать сообщение целиком:

```js
function chatBubble(m){
  var me=m.role==='user';
  return '<div style="display:flex;justify-content:'+(me?'flex-end':'flex-start')+';margin-bottom:10px"><div style="max-width:86%;padding:9px 12px;border-radius:12px;font-size:13px;line-height:1.45;background:'+(me?'var(--accent)':'var(--surface-2)')+';color:'+(me?'#fff':'var(--text)')+'">'+(me?esc(m.content):mdLite(m.content))+(me?'':thinkBlock(m))+'</div></div>';
}
```

И поправить три вызова в `renderChat`:

```js
function renderChat(extra){
  var el=document.getElementById('chat-msgs');if(!el)return;
  var h=CHAT.map(chatBubble).join('');
  if(extra)h+=extra.map(chatBubble).join('');
  el.innerHTML=h;el.scrollTop=el.scrollHeight;
}
```

- [ ] **Step 6: Проверить руками**

Обновить страницу (пересборка не нужна — `index.html` читается на каждый запрос, но файл должен быть обновлён в `bin\Release\net10.0\`). Проверить:

1. тумблер выключен → вопрос идёт в старый чат, блока размышлений нет;
2. тумблер включён → «Сколько заданий просрочено?» → ответ + строка «Размышления · N шагов · T с»;
3. клик по строке раскрывает шаги, у шага `sql` виден текст запроса;
4. перезагрузка страницы сохраняет положение тумблера.

- [ ] **Step 7: Прогнать smoke и закоммитить**

```bash
python tests/smoke.py http://localhost:5080
git add index.html style.css
git commit -m "feat(ui): режим глубокого анализа и блок размышлений в чате"
```

---

### Task 10: Документация

**Files:**
- Modify: `CLAUDE.md`, `README.md`

- [ ] **Step 1: Дописать в `CLAUDE.md`**

В раздел «Проверка после изменений» — актуальное число проверок smoke (по факту после Task 9). В «Где что документировано» — ссылки на спеку и этот план. В «Известные хрупкости» добавить:

```markdown
- **ИИ-харнесс исполняет сгенерированный моделью SQL.** Два рубежа: валидатор `SqlCheck`
  (один оператор, только SELECT/WITH, деней-лист, принудительный LIMIT) и read-only
  транзакция с `statement_timeout`. Остаточный риск — исполнимый, но логически неверный
  запрос: он даст точное и при этом неправильное число. Поэтому ключевые метрики агент
  берёт готовыми инструментами, а не считает сам, и SQL виден пользователю.
```

- [ ] **Step 2: Дописать в `README.md`**

Раздел про ИИ: описать режим «Глубокий анализ (SQL)», список из девяти инструментов, эндпоинты `/api/ai/sql`, `/api/ai/sql/check`, `/api/ai/sql/run`, `/api/ai/tools`, `/api/ai/tool`, `/api/ai/schema`.

- [ ] **Step 3: Коммит**

```bash
git add CLAUDE.md README.md
git commit -m "docs: описать ИИ-харнесс и режим глубокого анализа"
```

---

## Проверка плана на полноту

Сверка с разделами спеки:

| Раздел спеки | Задача |
|---|---|
| §4 компоненты | Task 4–9 |
| §5 инструменты и маршрутизация | Task 6 (реестр), Task 8 (правило в промпте, предзагрузка, пометка источника) |
| §5а протокол | Task 8 |
| §6 гардрейлы | Task 4 (валидатор), Task 5 (read-only транзакция) |
| §7 контракт ответа | Task 8 (сервер), Task 9 (отрисовка) |
| §8 фронтенд | Task 1–3 |
| §9 тестирование | проверки внутри Task 4–8, ручной прогон в Task 9 |
| §10 порядок работ | порядок задач: фронтенд → валидатор → исполнитель → инструменты → схема → цикл → UI |
