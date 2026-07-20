# Перенос новой стартовой руководителя в прототип — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Перенести утверждённый макет стартовой «Обзор для руководителя» в рабочий web-прототип: новый лид-блок «Исполнение по подчинённым/подразделениям» (сквозные метрики по 3 процессам, ручной выбор состава), перекомпоновка стартовой (убрать Hero-KPI и «Что горит»), сохранить контрольные поручения / обращения / светофор.

**Architecture:** Бэкенд — новый read-only эндпоинт `/api/leaders?by=performer|dept`, агрегирующий `sungero_wf_assignment` по исполнителю или подразделению сквозь все 3 процесса (`Procs`), по образцу `BuildWorkload`/`BuildDepartments`. Фронтенд — новый рендер лид-блока в `index.html` с вкладками, лентой агрегатов, карточками (инициалы/иконка, без фото — фото были только в макете), модалкой «Настроить состав» (выбор в localStorage, как личный контроль). Перекомпоновка `renderStart()`.

**Tech Stack:** C# (.NET 10, HttpListener + Npgsql/PostgreSQL, read-only), ванильный JS в `index.html`. Автотестов нет — верификация: `C:/dotnet10/dotnet.exe build` + curl эндпоинта + браузер через python Playwright.

## Global Constraints

- Сборка/запуск ТОЛЬКО через `C:/dotnet10/dotnet.exe` (проект net10; дефолтный `dotnet` — SDK8). Порт `http://localhost:5080/`.
- Прототип **read-only** к БД — только SELECT, никаких INSERT/UPDATE/DELETE. Пользовательский выбор — только в `localStorage`.
- Каждый запрос к `sungero_wf_assignment` ОБЯЗАН исключать уведомления через `{NoticeNotIn}` (алиас `a`) — иначе метрики раздуваются.
- Процессы берутся из статического `Procs` (Program.cs:104-109): `poruchenia`, `appeals`, `npa`. НПА классифицируется по `ilike` темы (приблизительно) — это ожидаемо.
- Демо-пользователь один: `DemoUserId=53`. Ключи localStorage — фиксированные (`armgov.leaders.performer`, `armgov.leaders.dept`), как `armgov.mycontrol.demo`.
- Фото исполнителей в прототипе НЕТ — карточки используют аватар-инициалы (люди) и иконку 🏛️ (подразделения). Не тянуть внешние изображения.
- Стиль кода — как в файле: строковый SQL, ванильный JS, существующие CSS-классы. Не ломать существующие блоки (обращения `/api/appeals/topics`, светофор `/api/overview`, личный контроль `/api/my/tasks`).

---

### Task 1: Бэкенд — эндпоинт `/api/leaders` (сквозные агрегаты по исполнителю/подразделению)

**Files:**
- Modify: `Program.cs` — добавить `case "/api/leaders"` в роутинг (после строки 185, рядом с `/api/my/tasks`) и метод `BuildLeaders(string by)` (рядом с `BuildWorkload`, ~Program.cs:849).

**Interfaces:**
- Consumes: `Procs` (список процессов), `PeriodClause`, `NoticeNotIn`, `sungero_wf_assignment`/`sungero_wf_task`/`sungero_core_recipient`.
- Produces: JSON `/api/leaders?by=performer|dept` →
  `{ by:string, items: Array<{ id:long, name:string, kind:"performer"|"dept", inwork:int, overdue:int, exp7:int, risk:bool, processes: Array<{key:string, name:string, inwork:int, overdue:int}> }> }`.
  Каждый элемент = исполнитель ИЛИ подразделение; `processes` — по одному на каждый из 3 процессов (даже если 0); `risk = overdue>0`; `exp7` = в работе со сроком в ближайшие 7 дней.

- [ ] **Step 1: Добавить роут**

В `Handle` после строки `case "/api/my/tasks": ...` добавить:

```csharp
            case "/api/leaders": JCached(ctx, ck, () => BuildLeaders(q["by"])); return;
```

- [ ] **Step 2: Реализовать `BuildLeaders`**

Добавить метод (рядом с `BuildWorkload`). Группировка задаётся `by`: `performer` (по исполнителю) или `dept` (по подразделению исполнителя). Цикл по `Procs` — как в `BuildOverview`; слияние по группе в словарь.

```csharp
    // ---------- /api/leaders (сквозные агрегаты по исполнителю/подразделению для лид-блока) ----------
    static object BuildLeaders(string by)
    {
        bool dept = by == "dept";
        using var c = new NpgsqlConnection(Cs); c.Open();
        // группа -> агрегаты; processes[key] -> (inwork, overdue)
        var groups = new Dictionary<long, (string name, int inwork, int overdue, int exp7,
            Dictionary<string, (int inwork, int overdue)> procs)>();
        foreach (var p0 in Procs)
        {
            string groupId = dept ? "coalesce(e.department_company_sungero,0)" : "a.performer";
            string groupNm = dept ? "coalesce(d.name::text,'(без подразделения)')" : "coalesce(r.name,'(не назначен)')";
            string joins = "join sungero_wf_task t on t.id=a.task left join sungero_core_recipient r on r.id=a.performer";
            if (dept) joins += " left join sungero_core_recipient e on e.id=a.performer left join sungero_core_recipient d on d.id=e.department_company_sungero";
            string sql =
                $"select {groupId} gid, {groupNm} gname, " +
                "count(*) filter (where a.status::text='InProcess') inwork, " +
                "count(*) filter (where a.status::text='InProcess' and a.deadline is not null and a.deadline<now()) overdue, " +
                "count(*) filter (where a.status::text='InProcess' and a.deadline is not null and a.deadline>=now() and a.deadline<now()+interval '7 days') exp7 " +
                $"from sungero_wf_assignment a {joins} " +
                $"where {p0.Where}{NoticeNotIn} and a.performer is not null group by {groupId}, {groupNm}";
            using var cmd = new NpgsqlCommand(sql, c);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                long gid = r.GetInt64(0); string gname = r.GetString(1);
                int iw = (int)r.GetInt64(2), ov = (int)r.GetInt64(3), e7 = (int)r.GetInt64(4);
                if (!groups.TryGetValue(gid, out var g))
                    g = (gname, 0, 0, 0, new Dictionary<string, (int, int)>());
                g.inwork += iw; g.overdue += ov; g.exp7 += e7;
                g.procs[p0.Key] = (iw, ov);
                groups[gid] = g;
            }
        }
        var items = groups.Select(kv => new
        {
            id = kv.Key, name = kv.Value.name, kind = dept ? "dept" : "performer",
            inwork = kv.Value.inwork, overdue = kv.Value.overdue, exp7 = kv.Value.exp7,
            risk = kv.Value.overdue > 0,
            processes = Procs.Select(p => new {
                key = p.Key, name = p.Name,
                inwork = kv.Value.procs.TryGetValue(p.Key, out var x) ? x.inwork : 0,
                overdue = kv.Value.procs.TryGetValue(p.Key, out var y) ? y.overdue : 0
            }).ToList()
        })
        .Where(x => x.inwork > 0 || x.overdue > 0)   // не показываем пустые группы
        .OrderByDescending(x => x.overdue).ThenByDescending(x => x.inwork)
        .ToList();
        return new { by = dept ? "dept" : "performer", items };
    }
```

- [ ] **Step 3: Сборка**

Run: `C:/dotnet10/dotnet.exe build` в корне репо.
Expected: `Build succeeded`, 0 ошибок.

- [ ] **Step 4: Проверить эндпоинт**

Запустить сервер `C:/dotnet10/dotnet.exe run` (убить занятый порт 5080 при необходимости), затем:
Run: `curl -s "http://localhost:5080/api/leaders?by=dept"` и `curl -s "http://localhost:5080/api/leaders?by=performer"`
Expected: JSON `{by, items:[...]}`; у элемента есть `id,name,kind,inwork,overdue,exp7,risk,processes[3]`; `processes` содержит ключи `poruchenia/appeals/npa`. Остановить сервер.

- [ ] **Step 5: Commit**

```bash
git add Program.cs
git commit -m "feat(leaders): эндпоинт /api/leaders — сквозные метрики по исполнителю/подразделению"
```

---

### Task 2: Фронтенд — лид-блок «Исполнение по подчинённым/подразделениям»

**Files:**
- Modify: `index.html` — CSS (рядом с существующими карточными стилями), контейнер `#start-leaders` в `renderStart` (см. Task 4 для точного места; на этом шаге добавить контейнер в начало сборки стартовой), функции `renderLeaders`/`leaderCard`/`pt` рядом с `renderMyTasks` (~473).

**Interfaces:**
- Consumes: `/api/leaders?by=` (Task 1); существующие `esc()`, CSS-переменные (`--navy/--red/--amber/--green/--accent/--border`), паттерн модалки (`.overlay.hidden`+`.modalbox`).
- Produces: `renderLeaders()`, глобальные `LEADERS={performer:[],dept:[]}` (кэш), `curLeadTab` (0=подчинённые,1=подразделения); хелперы выбора (Task 3).

- [ ] **Step 1: CSS лид-блока**

Добавить в `<style>` (рядом с прочими карточными стилями):

```css
.ld-strip{display:flex;gap:22px;flex-wrap:wrap;background:var(--surface);border:1px solid var(--border);border-radius:12px;padding:12px 18px;margin-bottom:14px}
.ld-ag{display:flex;flex-direction:column;gap:2px}
.ld-ag .l{font-size:11px;color:var(--muted);font-weight:700;text-transform:uppercase;letter-spacing:.03em}
.ld-ag .v{font-size:20px;font-weight:800;color:var(--navy)} .ld-ag .v.r{color:var(--red)} .ld-ag .v.a{color:var(--amber)}
.ld-tabs{display:flex;gap:4px;margin-bottom:14px;border-bottom:1px solid var(--border)}
.ld-tab{padding:9px 15px;font-weight:700;font-size:13.5px;color:var(--muted);cursor:pointer;border-bottom:2px solid transparent;margin-bottom:-1px}
.ld-tab.on{color:var(--navy);border-bottom-color:var(--accent)}
.ld-tab .b{margin-left:7px;background:var(--surface-2);color:var(--muted);border-radius:999px;padding:1px 8px;font-size:11.5px}
.ld-grid{display:grid;grid-template-columns:repeat(3,1fr);gap:14px}
.ld-card{background:var(--surface);border:1px solid var(--border);border-radius:12px;box-shadow:var(--shadow);padding:15px 16px;cursor:pointer}
.ld-card.risk{border-top:3px solid var(--red)} .ld-card.ok{border-top:3px solid var(--green)}
.ld-h{display:flex;gap:12px;align-items:center;margin-bottom:12px}
.ld-av{width:46px;height:46px;border-radius:11px;background:var(--surface-2);display:flex;align-items:center;justify-content:center;font-weight:800;color:var(--navy);font-size:16px;flex:none}
.ld-nm{font-weight:800;color:var(--navy);font-size:14.5px;line-height:1.2}
.ld-row{display:grid;grid-template-columns:1fr auto auto;gap:10px;align-items:center;padding:6px 0;border-top:1px dashed var(--border)}
.ld-row .l{font-size:12.5px;color:var(--ink)} .ld-row .c{font-weight:800;color:var(--navy);font-size:14px;min-width:40px;text-align:right}
.ld-row .o{font-size:11.5px;font-weight:800;min-width:70px;text-align:right}
.ld-row .o.r{color:var(--red)} .ld-row .o.g{color:var(--green)}
.ld-foot{margin-top:11px;display:flex;justify-content:space-between;align-items:center}
.ld-pill{font-size:11.5px;font-weight:800;padding:4px 10px;border-radius:999px}
.ld-pill.risk{background:#FBE9E9;color:var(--red)} .ld-pill.ok{background:#E6F4EA;color:var(--green)}
.ld-exp{font-size:12px;color:var(--amber);font-weight:700}
@media (max-width:1150px){.ld-grid{grid-template-columns:repeat(2,1fr)}}
```

- [ ] **Step 2: Рендер лид-блока**

Добавить рядом с `renderMyTasks` (index.html:~473):

```javascript
var LEADERS={performer:null,dept:null}, curLeadTab=0;
function ldKey(by){return 'armgov.leaders.'+by;}
function ldLoadSel(by){try{var a=JSON.parse(localStorage.getItem(ldKey(by)));return Array.isArray(a)?a:null;}catch(e){return null;}}
function ldSaveSel(by,a){try{localStorage.setItem(ldKey(by),JSON.stringify(a));}catch(e){}}
function ldInitials(nm){var p=String(nm||'').replace(/["]/g,'').trim().split(/\s+/);return ((p[0]||'?')[0]||'')+((p[1]||'')[0]||'');}
function ldSelected(by,items){
  var sel=ldLoadSel(by);
  if(sel===null){ // дефолт: подразделения — все; подчинённые — топ-8 по просрочке
    return by==='dept'?items:items.slice(0,8);
  }
  var s={};sel.forEach(function(id){s[id]=1;});return items.filter(function(x){return s[x.id];});
}
function leaderCard(x,by){
  var procRows=x.processes.map(function(p){
    var ov=p.overdue>0?'<span class="o r">'+p.overdue+' просроч.</span>':'<span class="o g">в норме</span>';
    return '<div class="ld-row"><span class="l">'+esc(p.name)+'</span><span class="c">'+p.inwork+'</span>'+ov+'</div>';
  }).join('');
  var av=by==='dept'?'🏛️':esc(ldInitials(x.name));
  return '<div class="ld-card '+(x.risk?'risk':'ok')+'">'+
    '<div class="ld-h"><div class="ld-av">'+av+'</div><div class="ld-nm">'+esc(x.name)+'</div></div>'+
    procRows+
    '<div class="ld-foot">'+(x.risk?'<span class="ld-pill risk">⚠ Под риском</span>':'<span class="ld-pill ok">✓ в норме</span>')+
    '<span class="ld-exp">истекает 7 дн: '+x.exp7+'</span></div></div>';
}
function renderLeaders(){
  var el=document.getElementById('start-leaders'); if(!el) return;
  function draw(){
    var by=curLeadTab===0?'performer':'dept';
    var items=LEADERS[by]||[];
    var shown=ldSelected(by,items);
    var totIn=0,totOv=0,totExp=0,risk=0,appeals=0,poru=0;
    items.forEach(function(x){totIn+=x.inwork;totOv+=x.overdue;totExp+=x.exp7;if(x.risk)risk++;
      x.processes.forEach(function(p){if(p.key==='appeals')appeals+=p.inwork;if(p.key==='poruchenia')poru+=p.inwork;});});
    var strip='<div class="ld-strip">'+
      '<div class="ld-ag"><span class="l">Поручений в работе</span><span class="v">'+poru+'</span></div>'+
      '<div class="ld-ag"><span class="l">Под риском</span><span class="v a">'+risk+'</span></div>'+
      '<div class="ld-ag"><span class="l">Просрочено</span><span class="v r">'+totOv+'</span></div>'+
      '<div class="ld-ag"><span class="l">Истекает 7 дней</span><span class="v a">'+totExp+'</span></div>'+
      '<div class="ld-ag"><span class="l">Обращений в работе</span><span class="v">'+appeals+'</span></div></div>';
    var head='<div class="cardhead" style="margin-bottom:10px"><div class="card-h" style="margin:0">Исполнение по подчинённым</div>'+
      '<button class="btn-ghost" onclick="openLeadComp()">⚙ Настроить состав</button></div>';
    var tabs='<div class="ld-tabs"><div class="ld-tab '+(curLeadTab===0?'on':'')+'" onclick="ldTab(0)">Подчинённые <span class="b">'+(ldSelected("performer",LEADERS.performer||[]).length)+'</span></div>'+
      '<div class="ld-tab '+(curLeadTab===1?'on':'')+'" onclick="ldTab(1)">Подразделения <span class="b">'+(ldSelected("dept",LEADERS.dept||[]).length)+'</span></div></div>';
    var grid=shown.length?('<div class="ld-grid">'+shown.map(function(x){return leaderCard(x,by);}).join('')+'</div>')
      :'<div class="sub" style="padding:16px">Ничего не выбрано — нажмите «⚙ Настроить состав».</div>';
    el.innerHTML=head+strip+tabs+grid;
  }
  if(LEADERS.performer&&LEADERS.dept){draw();return;}
  Promise.all([fetch('/api/leaders?by=performer').then(function(r){return r.json();}),
               fetch('/api/leaders?by=dept').then(function(r){return r.json();})])
    .then(function(res){LEADERS.performer=res[0].items||[];LEADERS.dept=res[1].items||[];draw();})
    .catch(function(){el.innerHTML='';});
}
function ldTab(i){curLeadTab=i;renderLeaders();}
```

- [ ] **Step 3: Проверка (браузер)**

Собрать/запустить прототип, открыть стартовую. Expected: лид-блок «Исполнение по подчинённым» показывает ленту агрегатов, вкладки Подчинённые/Подразделения, карточки с инициалами (люди) и 🏛️ (подразделения), строки Поручения/Согласование НПА/Обращения граждан с числами и «N просроч./в норме», флаг «⚠ Под риском / ✓ в норме», «истекает 7 дн: N». Переключение вкладок работает. (Контейнер `#start-leaders` и вызов `renderLeaders()` подключаются в Task 4.)

- [ ] **Step 4: Commit**

```bash
git add index.html
git commit -m "feat(leaders): лид-блок «Исполнение по подчинённым/подразделениям» на стартовой"
```

---

### Task 3: Фронтенд — модалка «Настроить состав» (выбор в localStorage)

**Files:**
- Modify: `index.html` — HTML модалки `#leadCompModal` (рядом с `#mycontrolModal`), функции `openLeadComp/closeLeadComp/toggleLead/renderLeadComp`.

**Interfaces:**
- Consumes: `LEADERS` (Task 2), `ldLoadSel/ldSaveSel/ldSelected` (Task 2), паттерн модалки.
- Produces: `openLeadComp()`, `closeLeadComp()`, `toggleLead(by,id)`, `renderLeadComp()`.

- [ ] **Step 1: HTML модалки**

После блока `#mycontrolModal` добавить:

```html
<div id="leadCompModal" class="overlay hidden" onclick="if(event.target===this)closeLeadComp()">
  <div class="modalbox" style="max-width:720px">
    <div class="row" style="justify-content:space-between;align-items:flex-start">
      <div><div style="font-size:18px;font-weight:700">⚙ Настроить состав дашборда</div>
      <div class="subtle" style="font-size:12px;margin-top:4px">Выберите подчинённых и подразделения, контроль над которыми держите на дашборде.</div></div>
      <button class="btn-ghost" onclick="closeLeadComp()">✕</button>
    </div>
    <div style="max-height:64vh;overflow:auto;margin-top:14px">
      <div class="subtle" style="font-weight:700;text-transform:uppercase;font-size:11px;margin:6px 0 4px">Подчинённые</div>
      <div id="leadCompP"></div>
      <div class="subtle" style="font-weight:700;text-transform:uppercase;font-size:11px;margin:16px 0 4px">Подразделения</div>
      <div id="leadCompD"></div>
    </div>
  </div>
</div>
```

- [ ] **Step 2: Функции модалки**

Рядом с `openMyControl`/`closeMyControl`:

```javascript
function openLeadComp(){document.getElementById('leadCompModal').classList.remove('hidden');renderLeadComp();}
function closeLeadComp(){document.getElementById('leadCompModal').classList.add('hidden');}
function toggleLead(by,id){
  var items=LEADERS[by]||[];
  var sel=ldLoadSel(by); if(sel===null){sel=ldSelected(by,items).map(function(x){return x.id;});}
  var i=sel.indexOf(id); if(i>=0)sel.splice(i,1); else sel.push(id);
  ldSaveSel(by,sel); renderLeaders(); renderLeadComp();
}
function renderLeadComp(){
  function list(by,elId){
    var items=LEADERS[by]||[];
    var selIds={};ldSelected(by,items).forEach(function(x){selIds[x.id]=1;});
    document.getElementById(elId).innerHTML=items.map(function(x){
      var on=selIds[x.id];
      return '<div class="listrow" style="display:flex;gap:12px;align-items:center;padding:9px 2px;border-bottom:1px solid var(--border)">'+
        '<div style="flex:1;min-width:0"><div style="font-weight:700;color:var(--navy);font-size:13.5px">'+esc(x.name)+'</div>'+
        '<div class="subtle" style="font-size:12px">в работе '+x.inwork+' · <span style="color:var(--red)">просрочено '+x.overdue+'</span></div></div>'+
        '<button class="'+(on?'btn':'btn-ghost')+'" onclick="toggleLead(\''+by+'\','+x.id+')">'+(on?'✓ на контроле':'добавить')+'</button></div>';
    }).join('')||'<div class="sub">нет данных</div>';
  }
  list('performer','leadCompP'); list('dept','leadCompD');
}
```

- [ ] **Step 3: Проверка (браузер)**

Открыть стартовую → «⚙ Настроить состав» → модалка с двумя группами (Подчинённые / Подразделения). Добавить/убрать элемент → карточки в сетке и счётчики на вкладках меняются вживую; выбор переживает F5 (localStorage). Expected именно это.

- [ ] **Step 4: Commit**

```bash
git add index.html
git commit -m "feat(leaders): модалка «Настроить состав» — выбор подчинённых/подразделений (localStorage)"
```

---

### Task 4: Перекомпоновка `renderStart` (лид сверху; убрать Hero-KPI и «Что горит»)

**Files:**
- Modify: `index.html` — функция `renderStart` (index.html:408-472).

**Interfaces:**
- Consumes: `renderLeaders` (Task 2), существующие `renderMyTasks`, `renderStartAppeals`, светофор (`PROCS`).
- Produces: обновлённый порядок стартовой: **Лид-блок → Контрольные поручения (Мои задания) → Обращения → Светофор процессов**. ИИ-точки (кнопка `/api/ai/summary`, ИИ-разбор обращений) не трогаем.

- [ ] **Step 1: Убрать Hero-KPI и «Что горит», добавить контейнер лид-блока**

В `renderStart` (index.html:408-472):
1. Удалить сборку hero-KPI (строки ~416-429: блоки `throughput`/`bottleneck`/`longrunners` и обёртку `.grid` для них).
2. Удалить блок «Что горит сейчас» (`burning`, строки ~435-440).
3. Перед контейнером `#start-mytasks` (строка ~432) добавить контейнер лид-блока:

```javascript
  html+='<div class="card" id="start-leaders" style="margin-bottom:20px"></div>';
```

Итоговый порядок сборки `html` в `renderStart`: заголовок → `#start-leaders` → `#start-mytasks` → кнопка ИИ (оставить) → `#start-appeals` → светофор процессов.

- [ ] **Step 2: Вызвать `renderLeaders` при загрузке стартовой**

В конце `renderStart`, рядом с существующими `renderStartAppeals(); renderMyTasks();` (строки ~449, 470-471 — оба места, где стартовая финализируется), добавить `renderLeaders();`:

```javascript
  document.getElementById('view-start').innerHTML=html;
  renderLeaders();
  renderStartAppeals();
  renderMyTasks();
```
(Убедиться, что `renderLeaders()` добавлен во ВСЕ ветки, где рендерится стартовая — их две: ранний `return` при `!ovHas('svetofor')` ~строка 449 и основная ~470.)

- [ ] **Step 3: Проверка сборки и в браузере**

`C:/dotnet10/dotnet.exe build` → Build succeeded. Запустить, открыть стартовую. Expected: сверху — лид-блок «Исполнение по подчинённым», затем «Мои задания» (контрольные, с «⚙ Настроить контроль»), затем обращения, затем светофор процессов. Hero-KPI («Соблюдение сроков/Узкое горлышко/Застрявшие») и «Что горит» отсутствуют. JS-ошибок в консоли нет.

- [ ] **Step 4: Commit**

```bash
git add index.html
git commit -m "refactor(start): перекомпоновка стартовой — лид-блок сверху, убраны Hero-KPI и «Что горит»"
```

---

## Self-Review

**Spec coverage:**
- Лид-блок по подчинённым/подразделениям (сквозные метрики 3 процессов) → Task 1 (эндпоинт) + Task 2 (рендер). ✓
- Ручной выбор состава (нет иерархии в БД) → Task 3 (модалка + localStorage), дефолт в `ldSelected`. ✓
- Метрики карточки: Поручения/Согласование НПА/Обращения + просрочка + риск + истекает 7 дн → Task 1 `processes[]`/`exp7`/`risk`, Task 2 `leaderCard`. ✓
- Убрать Hero-KPI и «Что горит», лид сверху → Task 4. ✓
- Контрольные/обращения/светофор сохранены (переиспользуются) → Task 4 (порядок), не трогаем эндпоинты. ✓
- read-only + `{NoticeNotIn}` + dotnet10 + фикс.ключи localStorage → Global Constraints, соблюдены в коде Task 1/2/3. ✓

**Placeholder scan:** плейсхолдеров нет — код приведён целиком.

**Type consistency:** ответ `/api/leaders` (`items[].{id,name,kind,inwork,overdue,exp7,risk,processes[]}`) ↔ фронт (`LEADERS[by]`, `leaderCard`, `renderLeadComp`, `ldSelected`); ключи процессов `poruchenia/appeals/npa` из `Procs` совпадают; ключи localStorage `armgov.leaders.performer|dept` едины в `ldKey/ldLoadSel/ldSaveSel/toggleLead`.

**Примечания:**
- НПА-метрики приблизительны (классификация по `ilike`) — ожидаемо, зафиксировано в Global Constraints.
- Фото исполнителей были только в макете; в проде — инициалы/иконка (нет источника фото и запрета на внешние картинки).
- Дефолт выбора: подразделения — все; подчинённые — топ-8 по просрочке (пока руководитель не настроил состав).
