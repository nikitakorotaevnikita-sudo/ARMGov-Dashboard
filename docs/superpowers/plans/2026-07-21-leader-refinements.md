# Доработки стартовой руководителя (4 пункта) — план

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps use checkbox (`- [ ]`).

**Goal:** (1) В drill-модалке поручений показывать по каждому: состояние, краткое содержание, состояние исполнения, срок. (2) Фото в лид-блоке — по полу (по ФИО). (3) Кнопку «ИИ-аналитика» разместить компактно (в строке заголовка), не на всю ширину. (4) Блок «Топ вопросов» на стартовой — тепловой картой (переиспользуя `renderTreemap`).

**Architecture:** Бэкенд — расширить `/api/leader/tasks` (`BuildLeaderTasks`) полями `summary` (`t.actionitemtai_recman_sungero`) и `execState` (`t.executionstate_recman_sungero`, маппинг в рус.). Фронтенд — переверстать строки drill-модалки; гендер-эвристика по ФИО + два пула фото; перенос кнопки ИИ в строку заголовка; `renderStartAppeals` → `renderTreemap`.

**Tech Stack:** C# (.NET 10, HttpListener + Npgsql, read-only), ванильный JS `index.html`. Автотестов нет — верификация build+curl+Playwright.

## Global Constraints
- Сборка/запуск ТОЛЬКО через `C:/dotnet10/dotnet.exe` (net10), порт 5080.
- READ-ONLY (только SELECT). `{NoticeNotIn}` в запросах к assignment.
- Фото ТОЛЬКО data-URI (статик-сервер текст-only). Токены tokens.css (нет --shadow/--ink).
- Поля `actionitemtai_recman_sungero`/`executionstate_recman_sungero` — родные для процесса «Поручения»; у appeals/npa-задач могут быть пустыми → fallback (summary→subject, execState→«—»). Это ожидаемо.
- Не ломать существующее: `togglePinObj`/личный контроль, лид-блок, обращения, светофор.

---

### Task 1: Бэкенд — `summary` + `execState` в `/api/leader/tasks`

**Files:** Modify `Program.cs` — `BuildLeaderTasks` (499-549); добавить словарь `ExecStateNames` + хелпер `ExecStateName` (рядом со `StageNames`, ~122).

**Interfaces:**
- Produces: item получает 2 новых поля — `summary:string` (краткое содержание поручения) и `execState:string` (рус. состояние исполнения). Остальные поля без изменений.

- [ ] **Step 1: Словарь состояний исполнения**

Рядом со `StageNames` (Program.cs ~122-146) добавить:
```csharp
    static readonly Dictionary<string, string> ExecStateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OnExecution"] = "На исполнении",
        ["OnControl"] = "На контроле",
        ["Executed"] = "Исполнено",
        ["Aborted"] = "Прекращено",
    };
    static string ExecStateName(string s) => string.IsNullOrEmpty(s) ? "—" : (ExecStateNames.TryGetValue(s, out var n) ? n : s);
```

- [ ] **Step 2: Расширить SELECT и item в `BuildLeaderTasks`**

В SELECT (Program.cs:524-526) добавить два поля (после `perf`):
```csharp
            "select a.id, coalesce(nullif(a.subject::text,''),'(без темы)') subj, a.discriminator::text disc, a.deadline, a.task, " +
            "t.discriminator::text tdisc, coalesce(r.name,'(не назначен)') perf, " +
            "coalesce(nullif(t.actionitemtai_recman_sungero::text,''),'') summary, " +
            "coalesce(t.executionstate_recman_sungero::text,'') execstate " +
```
В чтении (после `string perf = r.GetString(6);`) добавить:
```csharp
                string summ = r.GetString(7); string execst = r.GetString(8);
```
В `items.Add(new {...})` добавить поля (после `performer = perf`):
```csharp
                    , summary = string.IsNullOrEmpty(summ) ? subj : summ, execState = ExecStateName(execst)
```

- [ ] **Step 3: Сборка** — `C:/dotnet10/dotnet.exe build` → Build succeeded.

- [ ] **Step 4: Проверка**

Запустить сервер (убить занятый 5080). Взять performer id из `/api/leaders?by=performer`, затем `curl -s "http://localhost:5080/api/leader/tasks?by=performer&id=<ID>"` — у item есть `summary` (непустое для поручений) и `execState` (напр. «На исполнении»/«На контроле»/«—»). Остановить сервер.

- [ ] **Step 5: Commit**
```bash
git add Program.cs
git commit -m "feat(leader): summary (краткое содержание) + execState (состояние исполнения) в /api/leader/tasks"
```

---

### Task 2: Фронтенд — строки drill-модалки: состояние, краткое содержание, состояние исполнения, срок

**Files:** Modify `index.html` — `renderLeaderTasks` (618-629).

**Interfaces:** Consumes item с новыми `summary`/`execState` (Task 1) + существующие `overdue/dueKind/dueLabel/process/stage`.

- [ ] **Step 1: Переверстать строку**

Заменить тело `.map` в `renderLeaderTasks` (index.html:622-628) так, чтобы по каждому поручению были видны 4 вещи — **состояние**, **краткое содержание**, **состояние исполнения**, **срок**:
```javascript
  el.innerHTML=items.map(function(t){var on=pset[t.id];
    var col=t.dueKind==='overdue'?'var(--red)':t.dueKind==='soon'?'var(--amber)':'var(--muted)';
    var stateLbl=t.overdue?'Просрочено':'В работе';
    var stateCol=t.overdue?'var(--red)':'var(--accent)';
    return '<div class="listrow" style="display:flex;gap:12px;align-items:flex-start;padding:10px 2px;border-bottom:1px solid var(--border)">'+
      '<div style="flex:1;min-width:0">'+
        '<div style="font-weight:700;color:var(--navy);font-size:13.5px;line-height:1.3">'+esc(t.summary||t.subject)+'</div>'+
        '<div class="subtle" style="font-size:12px;margin-top:3px;display:flex;flex-wrap:wrap;gap:2px 12px">'+
          '<span><b style="color:'+stateCol+'">'+stateLbl+'</b></span>'+
          '<span>Исполнение: '+esc(t.execState||'—')+'</span>'+
          '<span style="color:'+col+'">'+esc(t.dueLabel)+'</span>'+
          (t.process?'<span>'+esc(t.process)+'</span>':'')+
        '</div>'+
      '</div>'+
      '<button class="'+(on?'btn':'btn-ghost')+'" style="flex:none" onclick="togglePinObjById('+t.id+')">'+(on?'📌 на контроле':'📌 закрепить')+'</button></div>';
  }).join('');
```

- [ ] **Step 2: Проверка (Playwright)**

Собрать/запустить. Открыть стартовую → клик по карточке подчинённого → в модалке каждая строка показывает: жирным краткое содержание, ниже — «В работе/Просрочено», «Исполнение: …», срок, процесс. Кнопка 📌 работает. console чистая. Скриншот модалки в scratchpad.

- [ ] **Step 3: Commit**
```bash
git add index.html
git commit -m "feat(leader): drill-модалка — состояние, краткое содержание, состояние исполнения, срок"
```

---

### Task 3: Фронтенд — фото по полу (эвристика по ФИО + два пула)

**Files:** Modify `index.html` — строка `var LEADER_PHOTOS=[...]` (544) → два массива; `leaderCard` (545-558); добавить `guessGender`.

**Interfaces:** Consumes `x.name` (ФИО из `/api/leaders`), `x.id`. Produces `LEADER_PHOTOS_M`/`LEADER_PHOTOS_F`, `guessGender(name)`.

- [ ] **Step 1: Сгенерировать два пула фото (муж/жен), стиль — деловой**

Выполнить (скачивает по 6 мужских и 6 женских портретов, пишет `scratch_photos.js`):
```bash
cd "c:/Users/vm-operator/Desktop/ARMGov-Dashboard" && python - <<'PY'
import urllib.request, base64
def pool(gender, nums):
    out=[]
    for n in nums:
        req=urllib.request.Request("https://randomuser.me/api/portraits/"+gender+"/"+str(n)+".jpg",headers={"User-Agent":"Mozilla/5.0"})
        out.append('"data:image/jpeg;base64,'+base64.b64encode(urllib.request.urlopen(req,timeout=20).read()).decode()+'"')
    return out
m=pool("men",[32,51,75,12,45,3]); f=pool("women",[44,68,21,33,50,9])
open("scratch_photos.js","w",encoding="utf-8").write("var LEADER_PHOTOS_M=["+",".join(m)+"];\nvar LEADER_PHOTOS_F=["+",".join(f)+"];\n")
print("ok m=%d f=%d"%(len(m),len(f)))
PY
```
Заменить строку 544 (`var LEADER_PHOTOS=[...]`) содержимым `scratch_photos.js` (две строки `LEADER_PHOTOS_M`/`LEADER_PHOTOS_F`). Удалить `scratch_photos.js` (не коммитить).
*(Примечание: randomuser — гендерно-корректные сток-портреты; «официальный вид чиновника РФ» — best-effort в рамках оффлайн-эмбеда.)*

- [ ] **Step 2: `guessGender` + выбор пула в `leaderCard`**

Перед `leaderCard` добавить:
```javascript
function guessGender(name){
  var p=String(name||'').replace(/["]/g,'').trim().split(/\s+/);
  var pat=p[2]||'';
  if(/(вна|чна|ична)$/i.test(pat))return 'f';
  if(/(вич|ыч|ич)$/i.test(pat))return 'm';
  var first=p[1]||'';
  if(/[ая]$/i.test(first) && !/^(никита|илья|фома|кузьма|лука|савва|данила|ерёма|фёдора?)$/i.test(first))return 'f';
  return 'm';
}
```
В `leaderCard` (index.html:552) заменить строку выбора фото:
```javascript
  else { var pool=guessGender(x.name)==='f'?LEADER_PHOTOS_F:LEADER_PHOTOS_M; var ph=pool[Math.abs(x.id)%pool.length]; av='<div class="ld-av" style="padding:0;overflow:hidden"><img src="'+ph+'" alt="" style="width:100%;height:100%;object-fit:cover"></div>'; }
```

- [ ] **Step 3: Проверка (Playwright)**

Открыть стартовую (вкладка «Подчинённые»). Expected: у «Концева Надежда Ивановна», «Концева Вера» — женские фото; у «Иванов Иван Иванович», «Босов Александр», «Захаров Андрей Александрович» и др. — мужские. Проверить соответствие визуально (скриншот) + через eval сопоставить пол по имени и наличие корректного пула (можно ограничиться скриншотом). console чистая.

- [ ] **Step 4: Commit**
```bash
git add index.html
git commit -m "fix(leader): фото в карточках по полу (эвристика по ФИО, муж/жен пулы)"
```

---

### Task 4: Фронтенд — компактная кнопка ИИ + «Топ вопросов» тепловой картой

**Files:** Modify `index.html` — `renderStart` заголовок (461) + удалить блок кнопки (470); `renderTreemap` (1125, добавить необязательную высоту); `renderStartAppeals` (1192-1203).

**Interfaces:** Consumes `renderTreemap(tm,height)`, `APP.treemap`.

- [ ] **Step 1: Кнопка ИИ — в строку заголовка**

Заменить строку 461 (заголовок) на флекс-строку с компактной кнопкой справа:
```javascript
  var html='<div class="row" style="justify-content:space-between;align-items:flex-start;gap:12px;margin-bottom:4px">'+
    '<div><div class="h2" style="margin:0">Обзор для руководителя</div>'+
    '<div class="sub">Общая картина по процессам: где всё идёт штатно, а где копятся проблемы и срываются сроки'+perLbl+'. Нажмите на процесс — откроется подробная сводка.</div></div>'+
    '<button class="btn-ghost" onclick="openAi()" style="white-space:nowrap;flex:none">🧠 ИИ-аналитика и прогноз</button></div>';
```
Удалить блок кнопки на всю ширину (строка 470 целиком):
```javascript
  html+='<div style="margin-bottom:20px"><button class="btn" onclick="openAi()" ...>🧠 ИИ-аналитика и прогноз</button></div>';
```

- [ ] **Step 2: `renderTreemap` — необязательная высота**

В `renderTreemap` (index.html:1125,1128) добавить параметр:
```javascript
function renderTreemap(tm,height){
  if(!tm||!tm.length)return '<div class="sub">нет данных</div>';
  var maxTp=0;tm.forEach(function(s){(s.topics||[]).forEach(function(t){if(t.pct>maxTp)maxTp=t.pct;});});
  var H=height||440,contentH=H-52;
```
(остальное тело без изменений; существующий вызов `renderTreemap(d.treemap)` в `renderAppeals` продолжит работать с H=440.)

- [ ] **Step 3: `renderStartAppeals` — тепловая карта**

Заменить тело `fill()` (index.html:1194-1199) так, чтобы вместо списка топ-5 рисовалась компактная тепловая карта:
```javascript
  function fill(){var d=APP;if(!d||d.error){el.innerHTML='';return;}
    el.innerHTML='<div class="cardhead" style="margin-bottom:8px"><div class="card-h" style="margin:0">Обращения граждан · тематики</div><span class="subtle" style="font-size:12px">всего '+d.kpi.requests+' обращений · раздел → тема · размер и цвет = доля</span></div>'+
      renderTreemap(d.treemap,300)+
      '<div class="listrow" style="text-align:center;color:var(--accent);font-weight:600;padding:9px 0 2px" onclick="goAppeals()">Все тематики обращений →</div>';
  }
```

- [ ] **Step 4: Проверка (Playwright)**

Открыть стартовую. Expected: (а) кнопка «🧠 ИИ-аналитика и прогноз» — справа в строке заголовка «Обзор для руководителя», НЕ отдельной широкой линией; клик открывает ИИ-модалку. (б) блок обращений — тепловая карта (плитки тем, `.tm-tile`), а не список; клик по плитке открывает тему; ссылка «Все тематики обращений →» ведёт на полный экран. console чистая. Скриншот стартовой.

- [ ] **Step 5: Commit**
```bash
git add index.html
git commit -m "feat(start): компактная кнопка ИИ в заголовке + тепловая карта тематик обращений"
```

---

## Self-Review
**Spec coverage:**
- Модалка: состояние/краткое содержание/состояние исполнения/срок → Task 1 (поля) + Task 2 (верстка). ✓
- Фото по полу → Task 3 (guessGender + пулы). ✓
- Компактная кнопка ИИ → Task 4 Step 1. ✓
- Топ вопросов → тепловая карта → Task 4 Steps 2-3 (renderTreemap). ✓

**Placeholder scan:** нет; код приведён целиком.

**Type consistency:** `summary`/`execState` (Task 1) ↔ `t.summary`/`t.execState` (Task 2); `LEADER_PHOTOS_M/F`+`guessGender` (Task 3) ↔ `leaderCard`; `renderTreemap(tm,height)` (Task 4) обратносовместим (height по умолчанию 440).

**Примечания:**
- `execState`/`summary` пустые у appeals/npa-задач → fallback (summary→subject, execState→«—»). Ожидаемо.
- Фото — гендерно-корректные сток-портреты (randomuser); «официальный» вид — best-effort в рамках оффлайн data-URI.
- `guessGender` — эвристика; редкие ФИО без отчества/нетипичные имена могут ошибиться (fallback → муж.).
