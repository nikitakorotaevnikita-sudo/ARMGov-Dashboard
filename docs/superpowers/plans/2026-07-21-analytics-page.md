# Вторая стартовая страница «Аналитика процессов» — план

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development или executing-plans. Steps — checkbox (`- [ ]`).

**Goal:** Добавить в прототип вторую дашборд-страницу **«Аналитика процессов»** (процессный срез: Hero-KPI + «Что горит» + светофор процессов + обращения) как отдельный пункт навигации. Текущая стартовая («Обзор для руководителя» с лид-блоком подчинённых) остаётся без изменений.

**Architecture:** Чисто фронтенд (`index.html`). Новый вид `#view-analytics`, пункт нав `goAnalytics()` (CUR='__analytics'), функция `renderAnalytics()` (переиспользует OVR/PROCS/SEV/hlp/openProcModal/svgSpark/deltaArrow как старая стартовая). `renderStartAppeals` параметризуется id-контейнера (чтобы обращения жили и на `#start-appeals`, и на `#an-appeals` без коллизии id). Личных «Мои задания» на новой странице нет.

**Tech Stack:** ванильный JS `index.html`. Автотестов нет — верификация Playwright.

## Global Constraints
- Сборка/запуск через `C:/dotnet10/dotnet.exe` (net10), порт 5080. `index.html` — статика.
- Ванильный JS, существующие классы/токены. Не ломать «Обзор для руководителя» (renderStart), обращения, светофор, навигацию.
- Данные из уже загруженного `OVR` (region.throughput/bottleneck/longRunners, whatsBurning, processes→PROCS). Новых эндпоинтов не добавлять.

---

### Task 1: Страница «Аналитика процессов» (вид + нав + рендер)

**Files:** Modify `index.html` — `<main>` (125, +контейнер вида); `renderNav` (368-372, +пункт); `hideViews` (377); +`goAnalytics`; +`renderAnalytics`; параметризовать `renderStartAppeals` (1192-1203).

**Interfaces:** Consumes `OVR`, `PROCS`, `SEV`, `hlp`, `ovHas`?(не используем — рендерим безусловно), `openProcModal`, `svgSpark`, `deltaArrow`, `sevRank`, `NOWM`, `PERIOD/PERIOD_LABEL`, `renderTreemap`, `renderStartAppeals`.

- [ ] **Step 1: Контейнер вида**

В `<main>` (index.html:125) добавить `#view-analytics` (после `#view-start`):
```html
<main><div id="view-start"></div><div id="view-analytics" class="hidden"></div><div id="view-process" class="hidden"></div><div id="view-appeals" class="hidden"></div><div id="view-backoffice" class="hidden"></div></main>
```

- [ ] **Step 2: Пункт навигации + routing**

В `renderNav` (index.html:368) сразу после пункта «Обзор для руководителя» добавить пункт «Аналитика процессов»:
```javascript
  n+='<div class="nav-item'+(CUR==='__analytics'?' active':'')+'" onclick="goAnalytics()">Аналитика процессов</div>';
```
В `hideViews` (index.html:377) добавить `'view-analytics'` в массив.
Рядом с `goStart` (index.html:378) добавить:
```javascript
function goAnalytics(){CUR='__analytics';CURDATA=null;renderNav();hideViews();document.getElementById('view-analytics').classList.remove('hidden');renderAnalytics();}
```

- [ ] **Step 3: Параметризовать `renderStartAppeals`**

Заменить сигнатуру/начало `renderStartAppeals` (index.html:1192-1194) так, чтобы принимать id контейнера (по умолчанию 'start-appeals'), и использовать его везде вместо литерала:
```javascript
function renderStartAppeals(elId){
  elId=elId||'start-appeals';
  var el=document.getElementById(elId);if(!el)return;
  function fill(){var d=APP;if(!d||d.error){el.innerHTML='';return;}
    el.innerHTML='<div class="cardhead" style="margin-bottom:8px"><div class="card-h" style="margin:0">Обращения граждан · тематики</div><span class="subtle" style="font-size:12px">всего '+d.kpi.requests+' обращений · раздел → тема · размер и цвет = доля</span></div>'+
      renderTreemap(d.treemap,300)+
      '<div class="listrow" style="text-align:center;color:var(--accent);font-weight:600;padding:9px 0 2px" onclick="goAppeals()">Все тематики обращений →</div>';
  }
  if(APP){fill();return;}
  el.innerHTML='<div class="sub">загрузка тематик обращений…</div>';
  fetch('/api/appeals/topics',{cache:'no-store'}).then(function(r){return r.json();}).then(function(d){APP=d;if(document.getElementById(elId))fill();}).catch(function(){var e=document.getElementById(elId);if(e)e.innerHTML='';});
}
```
(существующие вызовы `renderStartAppeals()` в `renderStart` работают как раньше — id по умолчанию.)

- [ ] **Step 4: `renderAnalytics`**

Добавить рядом с `renderStart` функцию (Hero-KPI + «Что горит» + обращения(`an-appeals`) + светофор; БЕЗ личных «Мои задания»; блоки рендерятся безусловно — это выделенная аналитическая страница):
```javascript
function renderAnalytics(){
  var el=document.getElementById('view-analytics');
  if(!OVR){el.innerHTML='<div class="sub">загрузка…</div>';return;}
  var reg=OVR.region||{},thr=reg.throughput||{},bn=reg.bottleneck||{},lr=reg.longRunners||{};
  var thrCol=SEV[thr.color]?SEV[thr.color].c:'var(--text)',thrAcc=SEV[thr.color]?SEV[thr.color].a:'accent-blue';
  var perLbl=PERIOD!=='all'?' · '+PERIOD_LABEL[PERIOD].toLowerCase():'';
  var html='<div class="h2">Аналитика процессов</div><div class="sub">Пропускная способность, узкие места и здоровье процессов'+perLbl+'. Нажмите на процесс — откроется подробная сводка.</div>';
  // HERO
  var hero='';
  hero+='<div class="card '+thrAcc+'"><div class="cardhead"><div class="kpi-label">Соблюдение сроков'+hlp('Соблюдение сроков','throughput',null)+'</div></div>'+
    '<div class="row" style="gap:8px;align-items:baseline;margin:8px 0 4px"><span style="font-size:42px;font-weight:700;color:'+thrCol+'">'+(thr.pct!=null?thr.pct:'—')+'%</span><span class="subtle">в срок</span></div>'+
    '<div class="subtle" style="font-size:12px">'+(thr.onTimeTotal||0)+' из '+(thr.ratedTotal||0)+' заданий с наступившим сроком — вовремя</div>'+
    (thr.deltaPp!=null?'<div style="font-size:12px;margin-top:5px">'+deltaArrow(thr.deltaPp)+' <span class="subtle">к прошлому месяцу</span></div>':'')+'</div>';
  hero+='<div class="card accent-amber" style="cursor:pointer" onclick="'+((bn&&bn.processKey)?'openProcModal(\''+bn.processKey+'\')':'')+'"><div class="cardhead"><div class="kpi-label">Где работа застревает'+hlp('Где работа застревает','bottleneck',null)+'</div></div>'+
    ((bn&&bn.stage)?'<div style="font-size:20px;font-weight:700;margin:8px 0 3px;line-height:1.25">'+esc(bn.process)+' <span class="subtle">→</span> '+esc(bn.stage)+'</div>'+
      '<div class="subtle" style="font-size:13px">медиана ожидания <b style="color:var(--amber)">'+bn.medianDays+' дн</b> · в очереди '+bn.queue+'</div>':'<div class="sub" style="margin-top:8px">нет активных заданий</div>')+'</div>';
  var lrcol=(lr.count>0)?'var(--red)':'var(--green)';
  hero+='<div class="card accent-red"><div class="cardhead"><div class="kpi-label">Застрявшие задания'+hlp('Застрявшие задания','longrunners',null)+'</div></div>'+
    '<div class="row" style="gap:8px;align-items:baseline;margin:8px 0 4px"><span style="font-size:42px;font-weight:700;color:'+lrcol+'">'+(lr.count!=null?lr.count:'—')+'</span><span class="subtle">заданий</span></div>'+
    '<div class="subtle" style="font-size:12px">просрочены более чем на '+(lr.thresholdDays||3)+' дня</div></div>';
  html+='<div class="grid" style="grid-template-columns:repeat(auto-fit,minmax(250px,1fr));margin-bottom:20px">'+hero+'</div>';
  // Что горит
  var wb=OVR.whatsBurning||[];
  if(wb.length)html+='<div class="card" style="margin-bottom:20px"><div class="cardhead" style="margin-bottom:10px"><div class="card-h" style="margin:0">🔥 Что горит сейчас'+hlp('Что горит сейчас','burning',null)+'</div></div>'+
    wb.map(function(b){var sv=SEV[b.severity]||SEV.green;
      return '<div class="listrow" onclick="openProcModal(\''+b.processKey+'\')" style="display:flex;gap:12px;align-items:center;padding:9px 2px"><span style="width:10px;height:10px;border-radius:50%;background:'+sv.c+';flex-shrink:0"></span><span style="font-weight:600;min-width:200px">'+esc(b.process)+'</span><span class="muted" style="font-size:13px">'+esc(b.headline)+'</span></div>';
    }).join('')+'</div>';
  // Обращения (свой контейнер, без коллизии id)
  html+='<div class="card" id="an-appeals" style="margin-bottom:20px"></div>';
  // Светофор процессов
  html+='<div class="cardhead" style="margin:0 0 6px"><div class="card-h" style="margin:0">Процессы'+hlp('Процессы','svetofor',null)+'</div></div>';
  var sc='16px 1.7fr 0.8fr 0.9fr 1.4fr 0.9fr 2.2fr 18px';
  html+='<div class="card" style="padding:6px 20px">';
  html+='<div style="display:grid;grid-template-columns:'+sc+';gap:14px;font-size:11px;color:var(--subtle);text-transform:uppercase;letter-spacing:.04em;padding:8px 0 6px;border-bottom:1px solid var(--border)">'+
    '<span></span><span>Процесс</span><span>Здоровье</span><span>Количество</span><span>Узкое горлышко</span><span>Просрочено</span><span>Тренд дисциплины (% в срок)</span><span></span></div>';
  html+=PROCS.slice().sort(function(a,b){return sevRank(a.severity)-sevRank(b.severity)||(b.overdue-a.overdue);}).map(function(p){var sv=SEV[p.severity]||SEV.green;
    var tr=(p.trend||[]).filter(function(d){return d.month<=NOWM;}),ld=null;for(var i=tr.length-1;i>=0;i--){var tt=(tr[i].ontime||0)+(tr[i].overdue||0);if(tt>0){ld=Math.round(100*(tr[i].ontime||0)/tt);break;}}
    var ldCol=ld==null?'var(--muted)':(ld>=75?'var(--green)':ld>=50?'var(--amber)':'var(--red)');
    return '<div class="listrow" onclick="openProcModal(\''+p.key+'\')" style="display:grid;grid-template-columns:'+sc+';gap:14px;align-items:center;padding:14px 0">'+
      '<span style="width:12px;height:12px;border-radius:50%;background:'+sv.c+'"></span>'+
      '<span><span style="font-size:15px;font-weight:600">'+esc(p.name)+'</span>'+(p.chronic?' <span class="badge b-red" style="font-size:10px;padding:1px 6px">⚠ хрон.</span>':'')+(p.throughputDelta!=null?' <span style="font-size:11px">'+deltaArrow(p.throughputDelta)+'</span>':'')+'</span>'+
      '<span><span style="font-size:18px;font-weight:700;color:'+sv.c+'">'+p.health+'</span><span class="subtle" style="font-size:11px">/100</span></span>'+
      '<span><span style="font-size:18px;font-weight:700">'+(p.total!=null?p.total:'—')+'</span><span class="subtle" style="font-size:11px"> · в работе '+(p.inwork!=null?p.inwork:'—')+'</span></span>'+
      '<span style="font-size:13px;line-height:1.25"><span style="color:var(--navy);font-weight:600">'+esc(p.bottleneckStage||'—')+'</span>'+(p.bottleneckMedianDays?'<span class="badge b-amber" style="margin-left:6px;font-size:11px;padding:1px 7px;white-space:nowrap">'+p.bottleneckMedianDays+' дн</span>':'')+'</span>'+
      '<span style="font-size:15px;font-weight:700;color:'+(p.overdue>0?'var(--red)':'var(--muted)')+'">'+p.overdue+'</span>'+
      '<span style="display:flex;align-items:center;gap:10px"><span style="flex:1;min-width:60px">'+svgSpark(tr,40)+'</span>'+(ld!=null?'<span style="font-size:14px;font-weight:700;color:'+ldCol+'">'+ld+'%</span>':'')+'</span>'+
      '<span class="subtle" style="text-align:right;font-size:18px">›</span></div>';
  }).join('');
  html+='</div>';
  el.innerHTML=html;
  renderStartAppeals('an-appeals');
}
```

- [ ] **Step 5: Проверка (Playwright)**

Собрать/запустить (сервер 5080). Сценарий:
1. Открыть http://localhost:5080/ — по умолчанию «Обзор для руководителя» (лид-блок подчинённых) — без изменений.
2. В сайдбаре есть пункт «Аналитика процессов» (после «Обзор для руководителя»); клик → показывается `#view-analytics`: Hero-KPI («Соблюдение сроков»/«Где работа застревает»/«Застрявшие задания»), «🔥 Что горит сейчас», тепловая карта обращений (`#an-appeals` с `.tm-tile`), таблица «Процессы» (светофор). НЕТ блока «Мои задания».
3. Клик по процессу в светофоре/«Что горит» → openProcModal работает.
4. Переключение назад на «Обзор для руководителя» (goStart) — лид-блок и «Мои задания» на месте (обе страницы не конфликтуют, `#start-appeals` и `#an-appeals` не пересекаются).
5. console/pageerror пустые (favicon 404 игнор). Скриншот «Аналитики процессов» в scratchpad.

- [ ] **Step 6: Commit**
```bash
git add index.html
git commit -m "feat(nav): вторая страница «Аналитика процессов» (процессный срез) + пункт навигации"
```

---

## Self-Review
**Spec coverage:** новая страница «Аналитика процессов» (Hero-KPI+Что горит+светофор+обращения, без «Мои задания») + нав-пункт → Task 1. «Обзор для руководителя» не тронут. ✓
**Placeholder scan:** нет; код целиком.
**Type consistency:** `renderStartAppeals(elId)` обратносовместим (default 'start-appeals'); `#an-appeals`≠`#start-appeals` (нет коллизии id); `goAnalytics`/CUR='__analytics'/hideViews/renderNav согласованы; renderAnalytics использует существующие хелперы.
**Примечания:** блоки на аналитической странице рендерятся безусловно (без `ovHas`-гейта) — это выделенная страница; данные из OVR всегда есть. Обращения — тепловая карта (как на обзоре), консистентно.
