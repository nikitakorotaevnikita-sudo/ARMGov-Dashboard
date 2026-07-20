# Лид-блок: сток-фото + drill-модалка поручений с личным контролем — план

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** (1) Показать сток-фото в карточках вкладки «Подчинённые» лид-блока. (2) По клику на карточку подчинённого/подразделения открывать модалку с его поручениями, где можно взять поручение на личный контроль — выбранные (в т.ч. чужие) появляются в блоке «Мои задания» сверху с 📌.

**Architecture:** Бэкенд — новый read-only эндпоинт `/api/leader/tasks?by=performer|dept&id=` (поручения группы сквозь 3 процесса, форма как `BuildMyTasks.all`). Фронтенд — фото как data-URI (статик-сервер отдаёт только текст, бинарь нельзя); drill-модалка; расширение личного контроля снимками (snapshot) чужих поручений, чтобы они рендерились в «Мои задания» без наличия в собственном инбоксе.

**Tech Stack:** C# (.NET 10, HttpListener + Npgsql, read-only), ванильный JS `index.html`. Автотестов нет — верификация: `C:/dotnet10/dotnet.exe build` + curl + Playwright.

## Global Constraints
- Сборка/запуск ТОЛЬКО через `C:/dotnet10/dotnet.exe` (net10), порт 5080.
- READ-ONLY к БД (только SELECT). Пользовательский выбор — только localStorage.
- Каждый запрос к `sungero_wf_assignment` включает `{NoticeNotIn}` (алиас `a`).
- Процессы из `Procs` (poruchenia/appeals/npa); НПА по `ilike` (приблизительно).
- Статик-сервер прототипа отдаёт только текст → фото ТОЛЬКО как data-URI в `index.html`, не как файлы.
- Личный контроль: существующий ключ `armgov.mycontrol.demo` (массив id) НЕ ломать; снимки чужих поручений — параллельный ключ `armgov.mycontrol.snap`.
- Токены из `tokens.css` (есть `--text`,`--shadow-sm`,`--border`,`--red`,`--navy`,`--muted`,`--surface`,`--surface-2`; НЕТ `--shadow`/`--ink`). Переиспользовать классы модалки `.overlay/.modalbox/.btn/.btn-ghost/.subtle/.listrow/.row`.

---

### Task 1: Бэкенд — `/api/leader/tasks` (поручения подчинённого/подразделения)

**Files:**
- Modify: `Program.cs` — роут после `case "/api/leaders"` и метод `BuildLeaderTasks(string by, string idStr)` рядом с `BuildMyTasks`.

**Interfaces:**
- Consumes: `Procs`, `NoticeNotIn`, `StageName`, `ProcNameByDisc`, `RxTaskLink`.
- Produces: `/api/leader/tasks?by=performer|dept&id=<n>` → `{ name:string, items: Array<{id,subject,stage,process,deadline,overdue,dueKind,dueLabel,rxLink,performer}> }` — активные (InProcess) поручения группы сквозь 3 процесса, отсортированные по срочности, кап 50. Форма items как в `BuildMyTasks.all` + поле `performer`.

- [ ] **Step 1: Роут**

После строки `case "/api/leaders": ...` добавить:
```csharp
            case "/api/leader/tasks": JCached(ctx, ck, () => BuildLeaderTasks(q["by"], q["id"])); return;
```

- [ ] **Step 2: Метод `BuildLeaderTasks`**

Добавить рядом с `BuildMyTasks`:
```csharp
    // ---------- /api/leader/tasks (поручения подчинённого/подразделения для drill-модалки) ----------
    static object BuildLeaderTasks(string by, string idStr)
    {
        bool dept = by == "dept";
        if (!long.TryParse(idStr, out long gid)) return new { name = "", items = new List<object>() };
        string procUnion = "(" + string.Join(" or ", Procs.Select(p => p.Where)) + ")";
        string joins = "join sungero_wf_task t on t.id=a.task left join sungero_core_recipient r on r.id=a.performer";
        string filter = dept
            ? "coalesce(r.department_company_sungero,0)=@id"
            : "a.performer=@id";
        using var c = new NpgsqlConnection(Cs); c.Open();
        string name = "";
        using (var nc = new NpgsqlCommand(dept
            ? "select coalesce(d.name::text,'(без подразделения)') from sungero_core_recipient d where d.id=@id"
            : "select coalesce(name,'(не назначен)') from sungero_core_recipient where id=@id", c))
        { nc.Parameters.AddWithValue("id", gid); var o = nc.ExecuteScalar(); name = o == null || o is DBNull ? "" : o.ToString(); }

        var items = new List<object>();
        using (var cmd = new NpgsqlCommand(
            "select a.id, coalesce(nullif(a.subject::text,''),'(без темы)') subj, a.discriminator::text disc, a.deadline, a.task, " +
            "t.discriminator::text tdisc, coalesce(r.name,'(не назначен)') perf " +
            $"from sungero_wf_assignment a {joins} " +
            $"where {procUnion}{NoticeNotIn} and {filter} and a.status::text='InProcess' " +
            "order by case when a.deadline is not null and a.deadline<now() then 0 when a.deadline is not null then 1 else 2 end, a.deadline asc limit 50", c))
        {
            cmd.Parameters.AddWithValue("id", gid);
            using var r = cmd.ExecuteReader();
            var now = DateTime.Now;
            while (r.Read())
            {
                long aid = r.GetInt64(0); string subj = r.GetString(1);
                string disc = r.IsDBNull(2) ? null : r.GetString(2);
                DateTime? dl = r.IsDBNull(3) ? (DateTime?)null : r.GetDateTime(3);
                long taskId = r.GetInt64(4); string tdisc = r.IsDBNull(5) ? null : r.GetString(5);
                string perf = r.GetString(6);
                bool ov = dl.HasValue && dl.Value < now;
                string dueKind, dueLabel;
                if (!dl.HasValue) { dueKind = "none"; dueLabel = "без срока"; }
                else { int days = (int)Math.Round(Math.Abs((dl.Value - now).TotalDays)); dueKind = ov ? "overdue" : "soon"; dueLabel = ov ? ("просрочено на " + days + " дн") : ("срок через " + days + " дн"); }
                items.Add(new { id = aid, subject = subj, stage = StageName(disc, 0), process = ProcNameByDisc(tdisc), deadline = dl?.ToString("yyyy-MM-dd"), overdue = ov, dueKind, dueLabel, rxLink = RxTaskLink(taskId, tdisc), performer = perf });
            }
        }
        return new { name, items };
    }
```

- [ ] **Step 3: Сборка** — `C:/dotnet10/dotnet.exe build` → Build succeeded.

- [ ] **Step 4: Проверка эндпоинта**

Запустить сервер (убить занятый 5080). Взять реальный id из `curl -s "http://localhost:5080/api/leaders?by=performer"` (поле `id` первого элемента), затем:
`curl -s "http://localhost:5080/api/leader/tasks?by=performer&id=<ID>"` — JSON `{name, items:[...]}`, у item есть `id,subject,stage,process,deadline,dueKind,dueLabel,rxLink,performer`. Аналогично `by=dept&id=<deptID>`. Остановить сервер.

- [ ] **Step 5: Commit**
```bash
git add Program.cs
git commit -m "feat(leader): эндпоинт /api/leader/tasks — поручения подчинённого/подразделения"
```

---

### Task 2: Фронтенд — сток-фото в карточках «Подчинённые»

**Files:**
- Modify: `index.html` — добавить `var LEADER_PHOTOS=[...]` (data-URI) в `<script>` рядом с лид-блоком; изменить `leaderCard` (index.html:533-544) — для `by==='performer'` использовать фото, для `dept` оставить 🏛️.

**Interfaces:**
- Consumes: `x.id` (числовой id группы), существующий `.ld-av` стиль.
- Produces: `LEADER_PHOTOS` (массив строк data-URI), обновлённый `leaderCard`.

- [ ] **Step 1: Сгенерировать пул фото (data-URI)**

Выполнить (скачивает 10 сток-портретов randomuser.me и печатает JS-массив):
```bash
cd "c:/Users/vm-operator/Desktop/ARMGov-Dashboard" && python - <<'PY'
import urllib.request, base64
urls=["men/32","women/44","men/51","women/68","men/75","men/12","women/21","men/45","women/33","men/8"]
parts=[]
for u in urls:
    req=urllib.request.Request("https://randomuser.me/api/portraits/"+u+".jpg",headers={"User-Agent":"Mozilla/5.0"})
    b=urllib.request.urlopen(req,timeout=20).read()
    parts.append('"data:image/jpeg;base64,'+base64.b64encode(b).decode()+'"')
open("scratch_photos.js","w",encoding="utf-8").write("var LEADER_PHOTOS=["+",".join(parts)+"];\n")
print("ok", len(parts))
PY
```
Вставить содержимое `scratch_photos.js` (строку `var LEADER_PHOTOS=[...]`) в `<script>` `index.html` перед функцией `leaderCard` (≈index.html:532). Удалить временный `scratch_photos.js` после вставки (не коммитить его).

- [ ] **Step 2: Обновить `leaderCard`**

Заменить строку выбора аватара (index.html:538) и разметку `.ld-av`:
```javascript
  var av;
  if(by==='dept'){ av='<div class="ld-av">🏛️</div>'; }
  else { var ph=LEADER_PHOTOS[Math.abs(x.id)%LEADER_PHOTOS.length]; av='<div class="ld-av" style="padding:0;overflow:hidden"><img src="'+ph+'" alt="" style="width:100%;height:100%;object-fit:cover"></div>'; }
```
И в возвращаемой разметке заменить `'<div class="ld-av">'+av+'</div>'` на просто `av` (т.к. `av` теперь уже содержит внешний `<div class="ld-av">`). Проверить, что финальный HTML карточки: `<div class="ld-h">'+av+'<div class="ld-nm">...`.

- [ ] **Step 3: Проверка (браузер)**

Собрать/запустить, открыть стартовую (Playwright). Expected: во вкладке «Подчинённые» карточки показывают фото (не инициалы); во вкладке «Подразделения» — 🏛️. Фото стабильны при перезагрузке (детерминизм по id). console чистая. Скриншот в scratchpad.

- [ ] **Step 4: Commit**
```bash
git add index.html
git commit -m "feat(leader): сток-фото в карточках вкладки «Подчинённые»"
```

---

### Task 3: Фронтенд — drill-модалка поручений + личный контроль чужих поручений

**Files:**
- Modify: `index.html` — HTML модалки `#leaderTasksModal` (рядом с `#leadCompModal`); клик по `.ld-card` (в `leaderCard` обернуть в onclick); функции `openLeaderTasks/closeLeaderTasks/renderLeaderTasks`; snapshot-хелперы `loadSnap/saveSnap` + `togglePinObj`; расширить `renderMyTasks` (index.html:490-518) для рендера закреплённых чужих поручений по снимку.

**Interfaces:**
- Consumes: `/api/leader/tasks` (Task 1), `loadPins/savePins` (index.html:797-798), `MYTASKS`, `esc`, паттерн модалки.
- Produces: `openLeaderTasks(by,id,name)`, `closeLeaderTasks()`, `renderLeaderTasks()`, `loadSnap()`, `saveSnap(m)`, `togglePinObj(t)`; обновлённый `renderMyTasks` и `leaderCard` (кликабельность).

- [ ] **Step 1: Snapshot-хелперы и togglePinObj**

Рядом с `pinKey/loadPins/savePins` (index.html:796-800) добавить:
```javascript
function snapKey(){return 'armgov.mycontrol.snap';}
function loadSnap(){try{return JSON.parse(localStorage.getItem(snapKey()))||{};}catch(e){return {};}}
function saveSnap(m){try{localStorage.setItem(snapKey(),JSON.stringify(m));}catch(e){}}
// пин из drill-модалки: храним снимок (поручение может быть не в моём инбоксе)
function togglePinObj(t){
  var p=loadPins(); var snap=loadSnap(); var i=p.indexOf(t.id);
  if(i>=0){p.splice(i,1);delete snap[t.id];}
  else{p.push(t.id);snap[t.id]={id:t.id,subject:t.subject,stage:t.stage,process:t.process,deadline:t.deadline,dueKind:t.dueKind,dueLabel:t.dueLabel,rxLink:t.rxLink,performer:t.performer};}
  savePins(p);saveSnap(snap);
  renderMyTasks(MYTASKS);
  if(!document.getElementById('leaderTasksModal').classList.contains('hidden'))renderLeaderTasks();
}
```

- [ ] **Step 2: Расширить `renderMyTasks` (снимки чужих пинов)**

В `renderMyTasks` заменить блок самозаживления и построения `pinned` (index.html:496-502) на:
```javascript
  var snap=loadSnap();
  var alive={};all.forEach(function(t){alive[t.id]=t;});
  // живой пин = есть в инбоксе (all) ИЛИ есть снимок (чужое поручение)
  var cleaned=pins.filter(function(id){return alive[id]||snap[id];});
  if(cleaned.length!==pins.length)savePins(cleaned);
  var pinned=cleaned.map(function(id){return alive[id]||snap[id];}).slice(0,5);
  var pinnedSet={};pinned.forEach(function(t){pinnedSet[t.id]=1;});
```
И в карточке закреплённого показать исполнителя, если это чужое (есть `t.performer` и его нет в `alive`): в строке `.subtle` добавить `+(snap[t.id]&&!alive[t.id]&&t.performer?' · '+esc(t.performer):'')`. (Вставить в существующую строку stage/process, index.html:515.)

- [ ] **Step 3: Кликабельность карточки лид-блока**

В `leaderCard` (index.html:539) добавить `onclick` и курсор на корневой `.ld-card`:
```javascript
  return '<div class="ld-card '+(x.risk?'risk':'ok')+'" style="cursor:pointer" onclick="openLeaderTasks(\''+by+'\','+x.id+',\''+esc(String(x.name)).replace(/\x27/g,"&#39;")+'\')">'+
```
(остальная разметка карточки без изменений).

- [ ] **Step 4: HTML модалки**

После `#leadCompModal` добавить:
```html
<div id="leaderTasksModal" class="overlay hidden" onclick="if(event.target===this)closeLeaderTasks()">
  <div class="modalbox" style="max-width:760px">
    <div class="row" style="justify-content:space-between;align-items:flex-start">
      <div><div id="ltTitle" style="font-size:18px;font-weight:700">Поручения</div>
      <div class="subtle" style="font-size:12px;margin-top:4px">Отметьте 📌 поручения, которые взять на личный контроль — они появятся в блоке «Мои задания».</div></div>
      <button class="btn-ghost" onclick="closeLeaderTasks()">✕</button>
    </div>
    <div id="ltList" style="max-height:64vh;overflow:auto;margin-top:14px"></div>
  </div>
</div>
```

- [ ] **Step 5: Функции модалки**

Рядом с `openMyControl/closeMyControl`:
```javascript
var LTDATA=null;
function openLeaderTasks(by,id,name){
  document.getElementById('leaderTasksModal').classList.remove('hidden');
  document.getElementById('ltTitle').textContent='Поручения · '+name;
  document.getElementById('ltList').innerHTML='<div class="sub">загрузка…</div>';
  fetch('/api/leader/tasks?by='+by+'&id='+id,{cache:'no-store'}).then(function(r){return r.json();})
    .then(function(d){LTDATA=d;renderLeaderTasks();})
    .catch(function(){document.getElementById('ltList').innerHTML='<div class="sub">не удалось загрузить</div>';});
}
function closeLeaderTasks(){document.getElementById('leaderTasksModal').classList.add('hidden');}
function renderLeaderTasks(){
  var el=document.getElementById('ltList'); if(!el||!LTDATA) return;
  var items=LTDATA.items||[]; var pins=loadPins(); var pset={};pins.forEach(function(id){pset[id]=1;});
  if(!items.length){el.innerHTML='<div class="sub">нет активных поручений</div>';return;}
  el.innerHTML=items.map(function(t){var on=pset[t.id];
    var col=t.dueKind==='overdue'?'var(--red)':t.dueKind==='soon'?'var(--amber)':'var(--muted)';
    return '<div class="listrow" style="display:flex;gap:12px;align-items:center;padding:10px 2px;border-bottom:1px solid var(--border)">'+
      '<div style="flex:1;min-width:0"><div style="font-weight:700;color:var(--navy);font-size:13.5px;line-height:1.3">'+esc(t.subject)+'</div>'+
      '<div class="subtle" style="font-size:12px">'+esc(t.stage)+(t.process?' · '+esc(t.process):'')+' · <span style="color:'+col+'">'+esc(t.dueLabel)+'</span></div></div>'+
      '<button class="'+(on?'btn':'btn-ghost')+'" onclick=\'togglePinObj('+JSON.stringify(t).replace(/\x27/g,"&#39;")+')\'>'+(on?'📌 на контроле':'📌 закрепить')+'</button></div>';
  }).join('');
}
```

- [ ] **Step 6: Проверка (браузер, Playwright)**

Собрать/запустить. Сценарий:
1. Открыть стартовую → клик по карточке подчинённого → модалка «Поручения · <ФИО>» со списком.
2. Закрепить поручение (📌) → кнопка «на контроле»; закрыть модалку → это поручение появилось в блоке «Мои задания» сверху с 📌 и подписью исполнителя.
3. F5 → закреплённое сохранилось (localStorage snap).
4. Клик по карточке подразделения → модалка с поручениями подразделения; пины работают так же.
5. console/pageerror пустые.
Скриншоты (модалка + «Мои задания» с чужим пином) в scratchpad.

- [ ] **Step 7: Commit**
```bash
git add index.html
git commit -m "feat(leader): drill-модалка поручений + личный контроль чужих поручений (снимки)"
```

---

## Self-Review

**Spec coverage:**
- Сток-фото во вкладке «Подчинённые» → Task 2 (LEADER_PHOTOS + leaderCard); dept оставляет 🏛️. ✓
- Клик по подчинённому/подразделению → модалка с поручениями → Task 1 (endpoint) + Task 3 (модалка, клик). ✓
- Выбор поручения на личный контроль → появляется в «Мои задания» (даже чужое) → Task 3 (togglePinObj + snap + renderMyTasks). ✓

**Placeholder scan:** плейсхолдеров нет; код приведён целиком.

**Type consistency:** `/api/leader/tasks` items ↔ `renderLeaderTasks`/`togglePinObj` (поля id/subject/stage/process/deadline/dueKind/dueLabel/rxLink/performer совпадают); ключи localStorage `armgov.mycontrol.demo` (pins, без изменений) + `armgov.mycontrol.snap` (snap) — едины в loadSnap/saveSnap/togglePinObj/renderMyTasks; фото — data-URI (статик-сервер текст-only).

**Примечания:**
- Существующий `togglePin(id)` (own-inbox модалка) не трогаем; для унификации при анпине own-задачи снимок отсутствует — ок.
- НПА-поручения могут иметь `process=""` (ProcNameByDisc не знает npa disc) — в модалке просто без метки процесса, приемлемо.
- Кап закреплённых в «Мои задания» — 5 (как сейчас); чужие и свои в одном списке.
