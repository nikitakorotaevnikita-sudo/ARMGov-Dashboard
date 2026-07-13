# «Личный контроль» в блоке «Мои задания» — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Дать демо-руководителю вручную закреплять задания своего инбокса на дашборде («личный контроль») — 📌 сверху блока «Мои задания» + авто-добор по сроку, с сохранением выбора в localStorage.

**Architecture:** Вся логика пина и слияния — на клиенте (`index.html`). Бэкенд (`Program.cs`) лишь расширяет `/api/my/tasks`, отдавая полный список активных заданий вместо топ-3. Выбор хранится в `localStorage`, ключ привязан к пользователю. Модалка выбора и рендер блока переиспользуют существующие паттерны прототипа.

**Tech Stack:** C# (.NET, ASP-подобный самописный HTTP-роутер + Npgsql/PostgreSQL), ванильный HTML/JS в одном файле `index.html`. Автотест-фреймворка в прототипе нет — верификация ручная через запуск локального сервера и браузер.

## Global Constraints

- Прототип **read-only** к продуктивной БД — никаких INSERT/UPDATE/DELETE. Пользовательское состояние только в `localStorage`.
- Все запросы к `sungero_wf_assignment` обязаны исключать уведомления через `{NoticeList}` (см. существующий `BuildMyTasks`).
- Демо-пользователь захардкожен: `DemoUserId=53`, `DemoUserName="Босов Александр"`.
- Веб-прототип правим здесь; перенос в RX-native — отдельный цикл после демо, НЕ в этом плане.
- Ссылки на карточки — через существующий `RxTaskLink(...)`, формат RX не менять.
- Стиль кода — как в файле: ванильный JS, строковая конкатенация HTML, классы `.btn`/`.btn-ghost`/`.overlay`/`.modalbox`/`.card`/`.cardhead`/`.card-h`/`.subtle`.

---

### Task 1: Бэкенд — `/api/my/tasks` отдаёт полный список активных заданий

**Files:**
- Modify: `Program.cs:462-495` (метод `BuildMyTasks`)

**Interfaces:**
- Consumes: существующие хелперы `StageName(disc,i)`, `ProcNameByDisc(tdisc)`, `RxTaskLink(taskId,tdisc)`, константы `DemoUserId`, `DemoUserName`, `NoticeList`, строка подключения `Cs`.
- Produces: JSON-ответ `/api/my/tasks` вида
  `{ user:string, active:long, overdue:long, all: Array<{ id:long, subject:string, stage:string, process:string, deadline:string|null, overdue:bool, dueKind:"overdue"|"soon"|"none", dueLabel:string, rxLink:string }>, top: <как раньше> }`.
  На этом шаге `top` СОХРАНЯЕТСЯ (чтобы текущий клиент не сломался), плюс добавляется `all`.

- [ ] **Step 1: Заменить тело `BuildMyTasks` — второй запрос без `limit 3`, наполнение `all`**

Заменить блок построения `top` (строки ~472-494) так, чтобы список собирался без лимита (кап 50) в переменную `all`, а `top` вычислялся как первые 3 элемента `all`:

```csharp
        var all = new List<object>();
        using (var cmd = new NpgsqlCommand(
            "select a.id, coalesce(nullif(a.subject::text,''),'(без темы)') subj, a.discriminator::text disc, a.deadline, a.task, t.discriminator::text tdisc " +
            "from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task " +
            $"where a.performer={DemoUserId} and a.discriminator not in ({NoticeList}) and a.status::text='InProcess' " +
            "order by case when a.deadline is not null and a.deadline<now() then 0 when a.deadline is not null then 1 else 2 end, a.deadline asc limit 50", c))
        using (var r = cmd.ExecuteReader())
        {
            var now = DateTime.Now;
            while (r.Read())
            {
                long aid = r.GetInt64(0); string subj = r.GetString(1);
                string disc = r.IsDBNull(2) ? null : r.GetString(2);
                DateTime? dl = r.IsDBNull(3) ? (DateTime?)null : r.GetDateTime(3);
                long taskId = r.GetInt64(4); string tdisc = r.IsDBNull(5) ? null : r.GetString(5);
                bool ov = dl.HasValue && dl.Value < now;
                string dueKind, dueLabel;
                if (!dl.HasValue) { dueKind = "none"; dueLabel = "без срока"; }
                else { int days = (int)Math.Round(Math.Abs((dl.Value - now).TotalDays)); dueKind = ov ? "overdue" : "soon"; dueLabel = ov ? ("просрочено на " + days + " дн") : ("срок через " + days + " дн"); }
                all.Add(new { id = aid, subject = subj, stage = StageName(disc, 0), process = ProcNameByDisc(tdisc), deadline = dl?.ToString("yyyy-MM-dd"), overdue = ov, dueKind, dueLabel, rxLink = RxTaskLink(taskId, tdisc) });
            }
        }
        var top = all.Count > 3 ? all.GetRange(0, 3) : all;
        return new { user = DemoUserName, active, overdue, all, top };
```

- [ ] **Step 2: Собрать проект**

Run: `dotnet build` в корне репозитория.
Expected: Build succeeded, 0 ошибок.

- [ ] **Step 3: Запустить сервер и проверить ответ endpoint**

Запустить прототип (как обычно запускается локальный сервер), затем:
Run: `curl -s http://localhost:<port>/api/my/tasks`
Expected: JSON содержит ключ `all` (массив), в каждом элементе есть `id`, `subject`, `dueKind`, `rxLink`; `active`/`overdue` — числа; `top` по-прежнему присутствует.

- [ ] **Step 4: Commit**

```bash
git add Program.cs
git commit -m "feat(mytasks): endpoint отдаёт полный список активных заданий (all)"
```

---

### Task 2: Фронтенд — хранилище пинов, рендер блока с закреплёнными, модалка выбора

**Files:**
- Modify: `index.html` — добавить HTML модалки рядом с другими `.overlay`-блоками (после `#appealModal`, ~строка 185); переписать `renderMyTasks` (~461-477); добавить JS-хелперы и функции модалки рядом.

**Interfaces:**
- Consumes: ответ `/api/my/tasks` с полем `all` (из Task 1); существующие `esc(s)`, классы CSS `.btn-ghost`/`.overlay`/`.modalbox`/`.subtle`/`.card-h`/`.cardhead`; переменную с id пользователя — если в JS её нет, использовать данные ответа: ключ строим по `d.user` недопустимо (может меняться) → используем фиксированный суффикс из ответа. Поскольку id пользователя на клиент не приходит, ключ localStorage строим как `armgov.mycontrol.demo` (демо-пользователь один).
- Produces: `loadPins()→number[]`, `savePins(arr:number[])`, `togglePin(id:number)`, `openMyControl()`, `closeMyControl()`, `renderMyControlModal()`; глобальную `MYTASKS` (кэш последнего ответа `all` для перерисовки без повторного fetch).

- [ ] **Step 1: Добавить хелперы localStorage для пинов**

Рядом с `loadLayout`/`saveLayout` (~655) добавить:

```javascript
var MYTASKS=null; // кэш последнего ответа /api/my/tasks (для модалки и перерисовки)
function pinKey(){return 'armgov.mycontrol.demo';}
function loadPins(){try{var a=JSON.parse(localStorage.getItem(pinKey()));return Array.isArray(a)?a:[];}catch(e){return [];}}
function savePins(a){try{localStorage.setItem(pinKey(),JSON.stringify(a));}catch(e){}}
function togglePin(id){var p=loadPins();var i=p.indexOf(id);if(i>=0)p.splice(i,1);else p.push(id);savePins(p);
  renderMyTasks(MYTASKS); if(!document.getElementById('mycontrolModal').classList.contains('hidden'))renderMyControlModal();}
```

- [ ] **Step 2: Переписать `renderMyTasks` — принимать данные, мержить пины + авто-добор**

Заменить функцию `renderMyTasks` (~461-477). Она либо делает fetch (если вызвана без аргумента), либо рендерит из переданного `d` (при перерисовке после toggle):

```javascript
function renderMyTasks(d){
  if(!d){fetch('/api/my/tasks',{cache:'no-store'}).then(function(r){return r.json();}).then(function(x){MYTASKS=x;renderMyTasks(x);}).catch(function(){var el=document.getElementById('start-mytasks');if(el)el.innerHTML='';});return;}
  MYTASKS=d;
  var el=document.getElementById('start-mytasks'); if(!el) return;
  var all=d.all||[];
  var pins=loadPins();
  // самозаживление: убрать из пинов id, которых больше нет в all
  var alive={};all.forEach(function(t){alive[t.id]=t;});
  var cleaned=pins.filter(function(id){return alive[id];});
  if(cleaned.length!==pins.length)savePins(cleaned);
  // закреплённые в порядке пина (кап 5)
  var pinned=cleaned.map(function(id){return alive[id];}).slice(0,5);
  var pinnedSet={};pinned.forEach(function(t){pinnedSet[t.id]=1;});
  // авто-добор по срочности (all уже отсортирован сервером), пока total<3 и не более 5 всего
  var show=pinned.slice();
  for(var i=0;i<all.length && show.length<Math.max(3,pinned.length) && show.length<5;i++){if(!pinnedSet[all[i].id])show.push(all[i]);}
  var head='<div class="cardhead" style="margin-bottom:10px"><div class="card-h" style="margin:0">📋 Мои задания · '+esc(d.user||'')+'</div>'+
    '<div class="row" style="gap:12px;align-items:center"><div class="subtle" style="font-size:12px">в работе '+(d.active||0)+' · <span style="color:var(--red)">просрочено '+(d.overdue||0)+'</span></div>'+
    '<button class="btn-ghost" onclick="openMyControl()">⚙ Настроить контроль</button></div></div>';
  if(!show.length){el.innerHTML=head+'<div class="sub">нет активных заданий</div>';return;}
  el.innerHTML=head+'<div class="grid" style="grid-template-columns:repeat(auto-fit,minmax(240px,1fr))">'+
    show.map(function(t){var col=t.dueKind==='overdue'?'var(--red)':t.dueKind==='soon'?'var(--amber)':'var(--muted)';
      var pin=pinnedSet[t.id]?'<span title="на личном контроле" style="float:right">📌</span>':'';
      return '<a href="'+t.rxLink+'" target="_blank" style="text-decoration:none;color:inherit;display:block;border:1px solid var(--border);border-left:3px solid '+col+';border-radius:var(--radius-sm);padding:12px">'+
        '<div style="font-weight:600;font-size:14px;line-height:1.3;margin-bottom:6px">'+pin+esc(t.subject)+'</div>'+
        '<div class="subtle" style="font-size:12px;margin-bottom:6px">'+esc(t.stage)+(t.process?' · '+esc(t.process):'')+'</div>'+
        '<div style="font-size:12px;font-weight:700;color:'+col+'">'+esc(t.dueLabel)+'</div></a>';
    }).join('')+'</div>';
}
```

- [ ] **Step 3: Добавить HTML модалки `#mycontrolModal`**

После блока `#appealModal` (рядом с другими `.overlay`, ~строка 185) вставить:

```html
<div id="mycontrolModal" class="overlay hidden" onclick="if(event.target===this)closeMyControl()">
  <div class="modalbox" style="max-width:720px">
    <div class="row" style="justify-content:space-between;align-items:flex-start">
      <div style="font-size:18px;font-weight:700">📌 Задания на личный контроль</div>
      <button class="btn-ghost" onclick="closeMyControl()">✕</button>
    </div>
    <div class="subtle" style="font-size:12px;margin-top:4px">Отметьте задания, которые всегда держать на дашборде. Закреплённые показываются сверху блока «Мои задания».</div>
    <div id="mycontrol-list" style="max-height:64vh;overflow:auto;margin-top:14px"></div>
  </div>
</div>
```

- [ ] **Step 4: Добавить `openMyControl`/`closeMyControl`/`renderMyControlModal`**

Рядом с `openAi`/`closeAi` (~489-494) добавить:

```javascript
function openMyControl(){document.getElementById('mycontrolModal').classList.remove('hidden');renderMyControlModal();}
function closeMyControl(){document.getElementById('mycontrolModal').classList.add('hidden');}
function renderMyControlModal(){
  var el=document.getElementById('mycontrol-list');if(!el)return;
  var all=(MYTASKS&&MYTASKS.all)||[];
  var pins=loadPins();var pinnedSet={};pins.forEach(function(id){pinnedSet[id]=1;});
  if(!all.length){el.innerHTML='<div class="sub">нет активных заданий</div>';return;}
  el.innerHTML=all.map(function(t){var col=t.dueKind==='overdue'?'var(--red)':t.dueKind==='soon'?'var(--amber)':'var(--muted)';
    var on=pinnedSet[t.id];
    return '<div class="listrow" style="display:flex;gap:12px;align-items:center;padding:10px 2px;border-bottom:1px solid var(--border)">'+
      '<div style="flex:1;min-width:0"><div style="font-weight:600;font-size:14px;line-height:1.3">'+esc(t.subject)+'</div>'+
      '<div class="subtle" style="font-size:12px">'+esc(t.stage)+(t.process?' · '+esc(t.process):'')+' · <span style="color:'+col+'">'+esc(t.dueLabel)+'</span></div></div>'+
      '<button class="'+(on?'btn':'btn-ghost')+'" style="flex-shrink:0" onclick="togglePin('+t.id+')">'+(on?'📌 на контроле':'📌 закрепить')+'</button></div>';
  }).join('');
}
```

- [ ] **Step 5: Запустить прототип и проверить сценарий в браузере**

Запустить локальный сервер, открыть стартовую страницу «Обзор для руководителя».
Expected (проверить по шагам):
1. Блок «Мои задания» показывает задания как раньше + в шапке кнопка «⚙ Настроить контроль».
2. Клик по кнопке → модалка со списком всех активных заданий, у каждого «📌 закрепить».
3. Закрепить 2 задания → кнопки становятся «📌 на контроле» (стиль `.btn`); в блоке эти задания уходят наверх с 📌; остальные места добиваются авто-топом.
4. Перезагрузить страницу (F5) → закреплённые сохранились и снова сверху.
5. Снять пин в модалке → задание теряет 📌, блок перерисовывается.

- [ ] **Step 6: Commit**

```bash
git add index.html
git commit -m "feat(mytasks): личный контроль — закрепление заданий (📌 + модалка выбора)"
```

---

### Task 3: Бэкенд — убрать устаревшее поле `top`

**Files:**
- Modify: `Program.cs` (метод `BuildMyTasks`, строка с `return`)

**Interfaces:**
- Consumes: `all` (уже наполняется в Task 1).
- Produces: ответ без `top` — `{ user, active, overdue, all }`. Клиент (Task 2) уже использует только `all`.

- [ ] **Step 1: Убрать вычисление и возврат `top`**

Удалить строку `var top = all.Count > 3 ? all.GetRange(0, 3) : all;` и изменить return:

```csharp
        return new { user = DemoUserName, active, overdue, all };
```

- [ ] **Step 2: Собрать и проверить**

Run: `dotnet build` — Expected: Build succeeded.
Перезапустить сервер, обновить страницу — Expected: блок «Мои задания» и модалка работают как в Task 2 (клиент не зависит от `top`).

- [ ] **Step 3: Commit**

```bash
git add Program.cs
git commit -m "refactor(mytasks): убрать устаревшее поле top из ответа"
```

---

## Self-Review

**Spec coverage:**
- Область = свой инбокс (performer=53) → Task 1 (запрос без изменения фильтра performer/NoticeList). ✓
- Закреплённые сверху + авто-добор, кап 5 / добор до 3 → Task 2 Step 2. ✓
- Модалка со списком + 📌-переключатель → Task 2 Steps 3-4. ✓
- Персист в localStorage по пользователю → Task 2 Step 1 (`armgov.mycontrol.demo`). ✓
- Самозаживление завершённых пинов → Task 2 Step 2 (`cleaned`). ✓
- Один запрос кормит блок и модалку → Task 1 (`all`) + `MYTASKS` кэш. ✓
- Удаление `top` → Task 3. ✓

**Placeholder scan:** плейсхолдеров нет — весь код приведён целиком.

**Type consistency:** `all` (Task 1) ↔ `d.all`/`MYTASKS.all` (Task 2/3); поля `id/subject/stage/process/dueKind/dueLabel/rxLink` совпадают; `loadPins/savePins/togglePin/openMyControl/closeMyControl/renderMyControlModal` определены до использования; ключ `armgov.mycontrol.demo` единый.

**Примечание по ключу localStorage:** id пользователя на клиент не передаётся, а демо-пользователь один → ключ фиксированный `armgov.mycontrol.demo`. При будущей многопользовательности (RX-native) ключ будет строиться по реальному id.
