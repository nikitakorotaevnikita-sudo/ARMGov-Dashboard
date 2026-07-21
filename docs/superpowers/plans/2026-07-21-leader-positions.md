# Должности в карточках подчинённых — план

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development / executing-plans. Steps — checkbox (`- [ ]`).

**Goal:** В лид-блоке «Исполнение по подчинённым» (страница «Обзор для руководителя») показывать должность сотрудника под именем.

**Architecture:** Бэкенд — `/api/leaders?by=performer` (`BuildLeaders`) добавляет поле `position` (должность из `sungero_company_jobtitle.name` через `r.jobtitle_company_sungero`). Для вкладки `dept` position пустой. Фронт — `leaderCard` выводит должность строкой под именем (только performer, если непусто).

**Tech Stack:** C# (net10, Npgsql, read-only), ванильный JS `index.html`. Автотестов нет — build+curl+Playwright.

## Global Constraints
- Сборка/запуск через `C:/dotnet10/dotnet.exe` (net10), порт 5080.
- READ-ONLY (только SELECT). `{NoticeNotIn}` не убирать.
- Не ломать «Обзор»/лид-блок/вкладку «Подразделения» (там должности нет).

---

### Task 1: Должность в `/api/leaders` + карточке

**Files:** Modify `Program.cs` — `BuildLeaders` (916-964); `index.html` — `leaderCard` (545-558).

**Interfaces:** Produces item.`position:string` (должность performer; пусто для dept). Consumes в `leaderCard`.

- [ ] **Step 1: Бэкенд — поле `position` в `BuildLeaders`**

В цикле по `Procs` (Program.cs:924-948):
- Добавить выражение должности и join только для performer:
```csharp
            string groupPos = dept ? "''::text" : "coalesce(jt.name::text,'')";
            string joins = "join sungero_wf_task t on t.id=a.task left join sungero_core_recipient r on r.id=a.performer";
            if (dept) joins += " left join sungero_core_recipient e on e.id=a.performer left join sungero_core_recipient d on d.id=e.department_company_sungero";
            else joins += " left join sungero_company_jobtitle jt on jt.id=r.jobtitle_company_sungero";
```
- В SELECT добавить `{groupPos} gpos` третьим полем (после `gname`) и в GROUP BY:
```csharp
            string sql =
                $"select {groupId} gid, {groupNm} gname, {groupPos} gpos, " +
                "count(*) filter (where a.status::text='InProcess') inwork, " +
                "count(*) filter (where a.status::text='InProcess' and a.deadline is not null and a.deadline<now()) overdue, " +
                "count(*) filter (where a.status::text='InProcess' and a.deadline is not null and a.deadline>=now() and a.deadline<now()+interval '7 days') exp7 " +
                $"from sungero_wf_assignment a {joins} " +
                $"where {p0.Where}{NoticeNotIn} and a.performer is not null group by {groupId}, {groupNm}, {groupPos}";
```
- Расширить кортеж словаря `groups` полем `pos` и сдвинуть индексы reader (gid=0, gname=1, gpos=2, inwork=3, overdue=4, exp7=5):
```csharp
        var groups = new Dictionary<long, (string name, string pos, int inwork, int overdue, int exp7,
            Dictionary<string, (int inwork, int overdue)> procs)>();
        ...
            while (r.Read())
            {
                long gid = r.GetInt64(0); string gname = r.GetString(1); string gpos = r.GetString(2);
                int iw = (int)r.GetInt64(3), ov = (int)r.GetInt64(4), e7 = (int)r.GetInt64(5);
                if (!groups.TryGetValue(gid, out var g))
                    g = (gname, gpos, 0, 0, 0, new Dictionary<string, (int, int)>());
                g.inwork += iw; g.overdue += ov; g.exp7 += e7;
                g.procs[p0.Key] = (iw, ov);
                groups[gid] = g;
            }
```
- В item добавить `position`:
```csharp
            id = kv.Key, name = kv.Value.name, position = kv.Value.pos, kind = dept ? "dept" : "performer",
```

- [ ] **Step 2: Фронт — должность под именем в `leaderCard`**

В `leaderCard` (index.html:553-554) в шапке карточки обернуть имя+должность:
```javascript
    '<div class="ld-h">'+av+'<div><div class="ld-nm">'+esc(x.name)+'</div>'+(x.position?'<div class="subtle" style="font-size:12px;margin-top:2px">'+esc(x.position)+'</div>':'')+'</div></div>'+
```
(остальная разметка карточки без изменений; для dept `x.position` пустой → строка не выводится.)

- [ ] **Step 3: Проверка**

- `C:/dotnet10/dotnet.exe build` → Build succeeded.
- Сервер на 5080 (останавливать по PID процесса `dotnet`). `curl -s "http://localhost:5080/api/leaders?by=performer"` — у item есть `position` (напр. «Глава»/«Заместитель Главы» у топ-исполнителей; может быть пусто у некоторых). `?by=dept` — `position` пустой.
- Playwright: открыть стартовую (вкладка «Подчинённые») → под именем в карточках видна должность (там, где она есть); переключить на «Подразделения» → должности нет (только 🏛️+название). console чистая. Скриншот в scratchpad.

- [ ] **Step 4: Commit**
```bash
git add Program.cs index.html
git commit -m "feat(leader): должность под именем в карточках подчинённых"
```

## Self-Review
**Spec coverage:** должность в карточке performer → Task 1 (position в API + leaderCard). dept без должности. ✓
**Placeholder scan:** нет; код целиком.
**Type consistency:** `position` (Program.cs item) ↔ `x.position` (leaderCard); reader-индексы сдвинуты на +1 после добавления `gpos`; GROUP BY включает `{groupPos}`.
**Примечания:** у части исполнителей должность может быть пустой (нет `jobtitle`) → строка не выводится, это ок.
