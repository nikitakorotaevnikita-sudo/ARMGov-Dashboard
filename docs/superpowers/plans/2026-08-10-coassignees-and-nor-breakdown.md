# Соисполнители и разрез по НОР — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Руководитель видит на стартовой странице третий разрез — по НОР (наша организация) — и по каждому поручению видит состояние соисполнителей, включая отдельный счётчик «просрочено у соисполнителей».

**Architecture:** Эпик 9 переиспользует готовую механику разрезов: `BuildLeaders`/`BuildLeaderTasks` получают третий вариант `by=bu` по полю `sungero_core_recipient.emplbunit_company_sungero`, фронт получает третью вкладку. Эпик 8 добавляет соисполнителей двумя слоями: детализация в drill-модалке поручений и агрегат `coOverdue` в карточках стартовой. Участники поручения собираются объединением трёх таблиц-коллекций и связываются с заданиями через `sungero_wf_task.maintask`.

**Tech Stack:** C# net10 + Npgsql (read-only, только `SELECT`), ванильный JS в `index.html`, smoke-тест на Python.

**Основание:** `docs/superpowers/specs/2026-08-10-coassignees-discovery.md` — модель данных, субъект и SQL проверены на стенде.

---

## Global Constraints

- **Только `SELECT`.** Ни одного `INSERT`/`UPDATE`/`DELETE`. Нарушение — ошибка, а не фича.
- **`{NoticeNotIn}` не убирать** ни из одного запроса: без него в разрез попадёт информирование и цифры раздуются.
- **`Aborted` не считать просрочкой.** Активная просрочка — только `status='InProcess' and deadline < now()`.
- **Не ломать существующие вкладки** «Подчинённые» и «Подразделения», модалку `openLeaderTasks`, экспорт CSV.
- Сборка: `dotnet` не в PATH новой сессии PowerShell, сначала выполнить строку из шага сборки.
- Порт 5080. `index.html` читается на каждый запрос, но обновлять надо копию в `bin\Release\net10.0\`.

## File Structure

| Файл | Что меняется |
|---|---|
| `Program.cs` | `BuildLeaders` (917-967) — вариант `by=bu` и поле `coOverdue`; `BuildLeaderTasks` (510-…) — фильтр `bu` и блок `co` по каждой задаче |
| `index.html` | `LEADERS`/`curLeadTab` (586), `ldSelected` (591-597), `leaderCard` (609-622), `renderLeaders` (623-651), модалка `openLeaderTasks` (673-…) |
| `tests/smoke.py` | новая секция проверки `/api/leaders?by=bu` и блока соисполнителей |

---

### Task 1: Эпик 9 — разрез по НОР

**Files:**
- Modify: `Program.cs` — `BuildLeaders` (917-967), `BuildLeaderTasks` (510-531)
- Modify: `index.html` — 586, 591-597, 609-622, 623-651

**Interfaces:** `GET /api/leaders?by=bu` → `{by:"bu", items:[{id,name,position:"",kind:"bu",inwork,overdue,exp7,risk,processes:[…]}]}`. `GET /api/leader/tasks?by=bu&id=<id>` → `{name, items:[…]}`.

- [ ] **Step 1: Бэкенд — вариант `bu` в `BuildLeaders`**

В `Program.cs:919` заменить объявление флага и блок построения SQL (919-931):

```csharp
        bool dept = by == "dept";
        bool bu = by == "bu";
        using var c = new NpgsqlConnection(Cs); c.Open();
        // группа -> агрегаты; processes[key] -> (inwork, overdue)
        var groups = new Dictionary<long, (string name, string pos, int inwork, int overdue, int exp7,
            Dictionary<string, (int inwork, int overdue)> procs)>();
        foreach (var p0 in Procs)
        {
            string groupId = bu ? "coalesce(e.emplbunit_company_sungero,0)"
                           : dept ? "coalesce(e.department_company_sungero,0)"
                           : "a.performer";
            string groupNm = bu ? "coalesce(b.name::text,'(без организации)')"
                           : dept ? "coalesce(d.name::text,'(без подразделения)')"
                           : "coalesce(r.name,'(не назначен)')";
            string groupPos = (dept || bu) ? "''::text" : "coalesce(jt.name::text,'')";
            string joins = "join sungero_wf_task t on t.id=a.task left join sungero_core_recipient r on r.id=a.performer";
            if (dept) joins += " left join sungero_core_recipient e on e.id=a.performer left join sungero_core_recipient d on d.id=e.department_company_sungero";
            else if (bu) joins += " left join sungero_core_recipient e on e.id=a.performer left join sungero_core_recipient b on b.id=e.emplbunit_company_sungero";
            else joins += " left join sungero_company_jobtitle jt on jt.id=r.jobtitle_company_sungero";
```

Остальной SQL (932-938) не меняется — он уже собран из этих переменных.

- [ ] **Step 2: Бэкенд — `kind` и `by` в ответе**

В `Program.cs:954` и `966` заменить двухвариантные выражения на трёхвариантные:

```csharp
            id = kv.Key, name = kv.Value.name, position = kv.Value.pos,
            kind = bu ? "bu" : dept ? "dept" : "performer",
```

```csharp
        return new { by = bu ? "bu" : dept ? "dept" : "performer", items };
```

- [ ] **Step 3: Бэкенд — фильтр `bu` в `BuildLeaderTasks`**

В `Program.cs:512-531` заменить флаг, фильтр и получение имени:

```csharp
        bool dept = by == "dept";
        bool bu = by == "bu";
        if (!long.TryParse(idStr, out long gid)) return new { name = "", items = new List<object>() };
        string procUnion = "(" + string.Join(" or ", Procs.Select(p => p.Where)) + ")";
        string joins = "join sungero_wf_task t on t.id=a.task left join sungero_core_recipient r on r.id=a.performer";
        string filter = bu
            ? "coalesce(r.emplbunit_company_sungero,0)=@id"
            : dept
            ? "coalesce(r.department_company_sungero,0)=@id"
            : "a.performer=@id";
        using var c = new NpgsqlConnection(Cs); c.Open();
        string name;
        if (gid == 0)
        {
            name = bu ? "(без организации)" : dept ? "(без подразделения)" : "(не назначен)";
        }
        else
        {
            using (var nc = new NpgsqlCommand(
                bu   ? "select coalesce(b.name::text,'(без организации)') from sungero_core_recipient b where b.id=@id"
              : dept ? "select coalesce(d.name::text,'(без подразделения)') from sungero_core_recipient d where d.id=@id"
                     : "select coalesce(name,'(не назначен)') from sungero_core_recipient where id=@id", c))
            { nc.Parameters.AddWithValue("id", gid); var o = nc.ExecuteScalar(); name = o == null || o is DBNull ? "" : o.ToString(); }
        }
```

- [ ] **Step 4: Сборка и проверка бэкенда**

```bash
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User'); dotnet build -c Release --nologo
```
Ожидается: `Сборка успешно завершена. Ошибок: 0`.

Запустить `bin\Release\net10.0\armgov-standalone.exe`, затем:

```bash
curl -s "http://localhost:5080/api/leaders?by=bu"
```
Ожидается: `"by":"bu"`, в `items` названия организаций («Аппарат Губернатора», «Министерство цифрового развития УР»), `kind":"bu"`, `position":""`. Взять `id` первого элемента и проверить drill:

```bash
curl -s "http://localhost:5080/api/leader/tasks?by=bu&id=<id>"
```
Ожидается: непустой `name` с названием организации и непустой `items`.

Регрессия: `curl -s "http://localhost:5080/api/leaders?by=performer"` — по-прежнему `position` заполнен; `?by=dept` — названия подразделений.

- [ ] **Step 5: Фронт — третья вкладка**

`index.html:586` — добавить ключ `bu`:

```javascript
var LEADERS={performer:null,dept:null,bu:null}, curLeadTab=0;
```

`index.html:593-595` в `ldSelected` — для `bu` показывать все, как для подразделений:

```javascript
  if(sel===null){ // дефолт: подразделения и организации — все; подчинённые — топ-8 по просрочке
    return (by==='dept'||by==='bu')?items:items.slice(0,8);
  }
```

`index.html:615` в `leaderCard` — аватар для организации:

```javascript
  if(by==='dept'){ av='<div class="ld-av">🏛️</div>'; }
  else if(by==='bu'){ av='<div class="ld-av">🏢</div>'; }
```

`index.html:626` — выбор разреза по номеру вкладки:

```javascript
    var by=curLeadTab===0?'performer':(curLeadTab===1?'dept':'bu');
```

`index.html:640-641` — третья вкладка:

```javascript
    var tabs='<div class="ld-tabs"><div class="ld-tab '+(curLeadTab===0?'on':'')+'" onclick="ldTab(0)">Подчинённые <span class="b">'+(ldSelected("performer",LEADERS.performer||[]).length)+'</span></div>'+
      '<div class="ld-tab '+(curLeadTab===1?'on':'')+'" onclick="ldTab(1)">Подразделения <span class="b">'+(ldSelected("dept",LEADERS.dept||[]).length)+'</span></div>'+
      '<div class="ld-tab '+(curLeadTab===2?'on':'')+'" onclick="ldTab(2)">Организации <span class="b">'+(ldSelected("bu",LEADERS.bu||[]).length)+'</span></div></div>';
```

`index.html:646-650` — грузить три разреза:

```javascript
  if(LEADERS.performer&&LEADERS.dept&&LEADERS.bu){draw();return;}
  Promise.all([fetch('/api/leaders?by=performer').then(function(r){return r.json();}),
               fetch('/api/leaders?by=dept').then(function(r){return r.json();}),
               fetch('/api/leaders?by=bu').then(function(r){return r.json();})])
    .then(function(res){LEADERS.performer=res[0].items||[];LEADERS.dept=res[1].items||[];LEADERS.bu=res[2].items||[];draw();})
    .catch(function(){el.innerHTML='';});
```

- [ ] **Step 6: Проверка фронта**

Скопировать фронт в вывод сборки и открыть страницу:

```bash
copy index.html bin\Release\net10.0\index.html
```

В браузере на `http://localhost:5080/`: в блоке «Исполнение по подчинённым» три вкладки, третья — «Организации» с ненулевым счётчиком. Клик по вкладке показывает карточки с 🏢 и названиями организаций. Клик по карточке открывает модалку со списком поручений этой организации. Вкладки «Подчинённые» и «Подразделения» работают как раньше. Консоль браузера без ошибок.

- [ ] **Step 7: Commit**

```bash
git add Program.cs index.html
git commit -m "feat(leader): разрез по НОР — третья вкладка «Организации»"
```

---

### Task 2: Эпик 8 — соисполнители в модалке поручения

**Files:**
- Modify: `Program.cs` — `BuildLeaderTasks`, добавить блок `co` в каждый item
- Modify: `index.html` — рендер строки модалки `openLeaderTasks`

**Interfaces:** каждый item в `/api/leader/tasks` получает `co:{total:int, overdue:int, items:[{name:string, role:string, state:string, deadline:string}]}`.

- [ ] **Step 1: Бэкенд — запрос участников поручения**

В `Program.cs` рядом с `BuildLeaderTasks` добавить метод. Три источника участников — соисполнители поручения, соисполнители пункта, исполнители пунктов; связь заданий с деревом через `maintask`; агрегация до одной строки на человека, чтобы не было дублей:

```csharp
    // Участники поручения кроме самого пользователя: соисполнители поручения,
    // соисполнители пунктов и исполнители других пунктов. Состояние — худшее из заданий.
    static Dictionary<long, List<object>> CoExecutors(NpgsqlConnection c, List<long> headIds, long excludePerson)
    {
        var res = new Dictionary<long, List<object>>();
        if (headIds.Count == 0) return res;
        string ids = string.Join(",", headIds);
        string sql =
            "with parts as (" +
            $" select task head, assignee person, 'соисполнитель поручения' role from sungero_recman_taicoassignees where task in ({ids})" +
            $" union all select task, coassignee, 'соисполнитель пункта' from sungero_recman_taipartscoasgs where task in ({ids})" +
            $" union all select task, assignee, 'исполнитель пункта' from sungero_recman_taiparts where task in ({ids})" +
            ") " +
            "select p.head, coalesce(rc.name,'(не назначен)') nm, min(p.role) role, " +
            " max(case when a.status::text='InProcess' and a.deadline is not null and a.deadline<now() then 3 " +
            "          when a.status::text='InProcess' then 2 " +
            "          when a.id is null then 1 else 0 end) st, " +
            " min(a.deadline) dl " +
            "from parts p " +
            "left join sungero_core_recipient rc on rc.id=p.person " +
            "left join sungero_wf_task ct on ct.maintask=p.head " +
            $"left join sungero_wf_assignment a on a.task=ct.id and a.performer=p.person{NoticeNotIn} " +
            "where p.person is not null and p.person<>@me " +
            "group by p.head, rc.name order by p.head, 4 desc, 2";
        using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("me", excludePerson);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            long head = r.GetInt64(0);
            int st = (int)r.GetInt64(3);
            string state = st == 3 ? "просрочено" : st == 2 ? "в работе" : st == 1 ? "задания нет" : "закрыто";
            if (!res.TryGetValue(head, out var list)) { list = new List<object>(); res[head] = list; }
            list.Add(new
            {
                name = r.GetString(1),
                role = r.GetString(2),
                state,
                deadline = r.IsDBNull(4) ? "" : r.GetDateTime(4).ToString("yyyy-MM-dd")
            });
        }
        return res;
    }
```

- [ ] **Step 2: Бэкенд — присоединить блок `co` к items**

В `BuildLeaderTasks`, после того как собран список задач и до `return`, собрать головные id и обогатить items. Головная задача — `t.maintask`; если в выборке его нет, использовать `t.id`. Добавить в SQL выборки задач поле `t.maintask` и читать его в переменную `head`, затем:

```csharp
        var heads = items.Select(x => (long)((dynamic)x).head).Where(h => h > 0).Distinct().ToList();
        var co = CoExecutors(c, heads, dept || bu ? 0L : gid);
        var withCo = items.Select(x =>
        {
            long h = (long)((dynamic)x).head;
            var list = co.TryGetValue(h, out var l) ? l : new List<object>();
            int ov = list.Count(z => (string)((dynamic)z).state == "просрочено");
            return (object)new
            {
                id = ((dynamic)x).id, subject = ((dynamic)x).subject, stage = ((dynamic)x).stage,
                process = ((dynamic)x).process, deadline = ((dynamic)x).deadline, overdue = ((dynamic)x).overdue,
                dueKind = ((dynamic)x).dueKind, dueLabel = ((dynamic)x).dueLabel, rxLink = ((dynamic)x).rxLink,
                performer = ((dynamic)x).performer, summary = ((dynamic)x).summary, execState = ((dynamic)x).execState,
                co = new { total = list.Count, overdue = ov, items = list }
            };
        }).ToList();
        return new { name, items = withCo };
```

- [ ] **Step 3: Проверка бэкенда**

Пересобрать, перезапустить, затем на группе с известными соисполнителями:

```bash
curl -s "http://localhost:5080/api/leader/tasks?by=performer&id=<id Босова>"
```
Ожидается: у части item непустой `co.total`, у поручения 594 в `co.items` — «Федоров Федор Александрович» и «Фомин Александр» с `state":"просрочено"` и `role":"соисполнитель поручения"`. Дублей одного человека в одном `co.items` быть не должно.

- [ ] **Step 4: Фронт — строка соисполнителей в модалке**

В рендере строки модалки `openLeaderTasks` после блока со сроком добавить компактную строку и раскрытие:

```javascript
    var coHtml='';
    if(x.co&&x.co.total>0){
      var rows=x.co.items.map(function(z){
        var cls=z.state==='просрочено'?'r':(z.state==='в работе'?'a':'g');
        return '<div class="ld-row"><span class="l">'+esc(z.name)+' <span class="subtle">'+esc(z.role)+'</span></span>'+
               '<span class="o '+cls+'">'+esc(z.state)+(z.deadline?' · '+esc(z.deadline):'')+'</span></div>';
      }).join('');
      coHtml='<details style="margin-top:6px"><summary class="subtle" style="cursor:pointer;font-size:12px">'+
        'Соисполнители: '+x.co.total+(x.co.overdue>0?' · <b class="r">просрочено '+x.co.overdue+'</b>':' · в норме')+
        '</summary><div style="margin-top:4px">'+rows+'</div></details>';
    }
```

и вставить `coHtml` в разметку карточки задачи сразу перед её закрывающим `</div>`.

- [ ] **Step 5: Проверка фронта**

Скопировать `index.html` в вывод сборки, открыть стартовую, кликнуть карточку подчинённого с поручениями. Ожидается: у поручений с соисполнителями видна строка «Соисполнители: N · просрочено M», раскрытие показывает список с ролями и состояниями. У поручений без соисполнителей строки нет. Консоль чистая.

- [ ] **Step 6: Commit**

```bash
git add Program.cs index.html
git commit -m "feat(leader): состояние соисполнителей в модалке поручений"
```

---

### Task 3: Эпик 8 — «просрочено у соисполнителей» на стартовой

**Files:**
- Modify: `Program.cs` — `BuildLeaders`, добавить агрегат
- Modify: `index.html` — `leaderCard` и полоса `ld-strip`

**Interfaces:** item в `/api/leaders` получает `coOverdue:int`. Показывается **отдельно** от собственной просрочки, не суммой.

- [ ] **Step 1: Бэкенд — агрегат по соисполнителям**

В `BuildLeaders`, после цикла по `Procs` и до формирования `items`, добавить запрос. Считается только для разреза `performer`; для `dept` и `bu` возвращается пусто:

```csharp
        var coOverdue = new Dictionary<long, int>();
        if (!dept && !bu)
        {
            string sql2 =
                "with my as (select distinct a.performer me, t.maintask head " +
                " from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task " +
                $" where a.performer is not null and t.maintask is not null{NoticeNotIn}), " +
                "parts as (select task head, assignee person from sungero_recman_taicoassignees " +
                " union all select task, coassignee from sungero_recman_taipartscoasgs " +
                " union all select task, assignee from sungero_recman_taiparts), " +
                "co as (select m.me, p.head, p.person, " +
                "  max(case when a2.status::text='InProcess' and a2.deadline is not null and a2.deadline<now() then 1 else 0 end) ov " +
                " from my m join parts p on p.head=m.head and p.person is not null and p.person<>m.me " +
                " left join sungero_wf_task ct on ct.maintask=p.head " +
                " left join sungero_wf_assignment a2 on a2.task=ct.id and a2.performer=p.person " +
                " group by m.me, p.head, p.person) " +
                "select me, sum(ov)::bigint from co group by me";
            using var cmd2 = new NpgsqlCommand(sql2, c);
            cmd2.CommandTimeout = 120;
            using var r2 = cmd2.ExecuteReader();
            while (r2.Read()) coOverdue[r2.GetInt64(0)] = (int)r2.GetInt64(1);
        }
```

- [ ] **Step 2: Бэкенд — поле в item**

В формировании `items` (`Program.cs:952-956`) добавить поле после `exp7`:

```csharp
            inwork = kv.Value.inwork, overdue = kv.Value.overdue, exp7 = kv.Value.exp7,
            coOverdue = coOverdue.TryGetValue(kv.Key, out var cov) ? cov : 0,
            risk = kv.Value.overdue > 0 || (coOverdue.TryGetValue(kv.Key, out var cov2) ? cov2 : 0) > 0,
```

Признак `risk` теперь поднимается и когда своё в порядке, а соисполнители подводят — ровно тот случай, из-за которого требование появилось.

- [ ] **Step 3: Проверка бэкенда и замер времени**

```bash
curl -s -o NUL -w "%{time_total}\n" "http://localhost:5080/api/leaders?by=performer"
```
Ожидается: ответ содержит `coOverdue`, у Иванова Ивана Ивановича и Босова Александра значение больше нуля. Время первого (некэшированного) ответа записать в тикет. Если превышает 5 секунд — не оптимизировать в этой задаче, а зафиксировать как отдельную: запрос обходит дерево поручений и является кандидатом на предрасчёт.

- [ ] **Step 4: Фронт — отдельный счётчик в карточке и в полосе**

В `leaderCard` (`index.html:620-621`) заменить подвал карточки:

```javascript
    '<div class="ld-foot">'+(x.risk?'<span class="ld-pill risk">⚠ Под риском</span>':'<span class="ld-pill ok">✓ в норме</span>')+
    (x.coOverdue>0?'<span class="ld-pill risk" title="Просрочено у соисполнителей по поручениям, где участвует">👥 соисп. '+x.coOverdue+'</span>':'')+
    '<span class="ld-exp">истекает 7 дн: '+x.exp7+'</span></div></div>';
```

В `renderLeaders` (`index.html:629-637`) добавить накопление и ещё одну плитку:

```javascript
    var totOv=0,totExp=0,risk=0,appeals=0,poru=0,totCo=0;
    items.forEach(function(x){totOv+=x.overdue;totExp+=x.exp7;totCo+=(x.coOverdue||0);if(x.risk)risk++;
      x.processes.forEach(function(p){if(p.key==='appeals')appeals+=p.inwork;if(p.key==='poruchenia')poru+=p.inwork;});});
```

```javascript
      '<div class="ld-ag"><span class="l">Просрочено</span><span class="v r">'+totOv+'</span></div>'+
      '<div class="ld-ag"><span class="l">У соисполнителей</span><span class="v r">'+totCo+'</span></div>'+
```

- [ ] **Step 5: Проверка фронта**

Ожидается: на вкладке «Подчинённые» в полосе появилась плитка «У соисполнителей» с ненулевым значением; в карточках, где соисполнители подводят, виден бейдж `👥 соисп. N`. На вкладках «Подразделения» и «Организации» плитка равна нулю и бейджей нет. Консоль чистая.

- [ ] **Step 6: Smoke-тест**

В `tests/smoke.py` добавить секцию после проверки процессов:

```python
print("\n=== Разрезы руководителя и соисполнители ===")
d = get("/api/leaders?by=bu")
ok(d.get("by") == "bu", "leaders by=bu отвечает")
ok(isinstance(d.get("items"), list) and len(d["items"]) > 0, "items непусто")
ok(all("coOverdue" not in i or isinstance(i["coOverdue"], int) for i in d["items"]), "coOverdue — число")
p = get("/api/leaders?by=performer")
ok(any(i.get("coOverdue", 0) > 0 for i in p["items"]), "есть исполнители с просрочкой у соисполнителей")
first = p["items"][0]["id"]
t = get(f"/api/leader/tasks?by=performer&id={first}")
ok(isinstance(t.get("items"), list), "leader/tasks отвечает списком")
ok(all(isinstance(i.get("co", {}).get("total", 0), int) for i in t["items"]), "co.total — число у каждой задачи")
```

Запустить: `python tests/smoke.py http://localhost:5080`. Ожидается `FAIL=0`, число проверок выросло на 6.

- [ ] **Step 7: Commit**

```bash
git add Program.cs index.html tests/smoke.py
git commit -m "feat(leader): просрочка у соисполнителей отдельным счётчиком + smoke"
```

---

## Self-Review

**Spec coverage.**
- Эпик 9, подразделения — уже были в коде, не дублируем. ✓
- Эпик 9, НОР через `emplbunit_company_sungero` (не `businessunit_company_sungero`) — Task 1, шаги 1 и 3. ✓
- Эпик 8, точка отсчёта головное поручение, субъект — участник — Task 2, шаг 1: связь через `ct.maintask=p.head`, поле `assigneetai_recman_sungero` не используется нигде. ✓
- Эпик 8, «показать всех соисполнителей» из трёх источников — Task 2, шаг 1, `parts as (… union all … union all …)`. ✓
- Эпик 8, показывать раздельно, а не суммой — Task 3, шаги 2 и 4: отдельные поля `overdue` и `coOverdue`, отдельные плитка и бейдж. ✓
- Требование «агрегировать до одной строки на человека» — Task 2, шаг 1: `group by p.head, rc.name` с `max(case …)`. ✓
- Требование «вердикт по `status`, а не по метке `completed`» — Task 2, шаг 1 и Task 3, шаг 1: во всех условиях только `status` и `deadline`. ✓
- `Aborted` не попадает в просрочку: условие всегда `status='InProcess'`. ✓
- `{NoticeNotIn}` присутствует в новых запросах. ✓
- Соисполнитель без задания не теряется: `st=1` → состояние «задания нет». ✓

**Placeholder scan:** заглушек нет, весь код приведён целиком.

**Type consistency:** `co:{total,overdue,items[{name,role,state,deadline}]}` — определено в Task 2 шаг 1 и потребляется в шаге 4 под теми же именами. `coOverdue` — целое, определено в Task 3 шаг 2, читается в шаге 4 как `x.coOverdue`. `by=bu` — одна строка `"bu"` в `BuildLeaders`, `BuildLeaderTasks` и трёх местах фронта. `kind:"bu"` согласован с `leaderCard(x,by)`, который принимает `by`, а не `kind`.

**Известные ограничения, не покрываемые планом.**
1. Запрос в Task 3 обходит дерево поручений и на большом объёме может быть медленным. Замер — в шаге 3; оптимизация вынесена отдельной задачей осознанно, чтобы не смешивать функциональность с производительностью.
2. Данные стенда смешивают регионы, поэтому названия организаций в разрезе НОР для демонстрации заказчику не показательны. Логика проверяется, числа — нет.
3. Соисполнители в обращениях на стенде почти не наполнены (5 связей), поэтому вкладка проверяется на поручениях.
