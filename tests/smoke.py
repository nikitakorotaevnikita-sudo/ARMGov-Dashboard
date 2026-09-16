#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Функциональный smoke-тест ARMGov Dashboard (описывает ожидаемую функциональность).
Запуск:  python tests/smoke.py [http://localhost:5080]
Зависимостей нет (urllib из stdlib). Сервер должен быть запущен.

Секция ИИ (LLM Ario) проверяется отдельно: пока модель недоступна — фиксируем
graceful-ошибку; в понедельник, когда модель поднимут, ожидаем содержательный ответ.
"""
import sys, json, time, urllib.request, urllib.error
try: sys.stdout.reconfigure(encoding="utf-8")  # читаемый вывод кириллицы
except Exception: pass

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5080").rstrip("/")
PASS, FAIL, AINOTE = 0, 0, []

def _req(path, method="GET", body=None, timeout=180):
    url = BASE + path
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(url, data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.status, json.loads(r.read().decode("utf-8"))

def _req_raw(path, raw_bytes, timeout=60):
    # Как _req, но без json.dumps: нужен для проверки заведомо БИТОГО тела запроса
    # (_req всегда сериализует body в валидный JSON, а тут нужны сырые невалидные байты).
    url = BASE + path
    req = urllib.request.Request(url, data=raw_bytes, method="POST",
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.status, json.loads(r.read().decode("utf-8"))

def check(name, cond, detail=""):
    global PASS, FAIL
    if cond: PASS += 1; print(f"  [ OK ] {name}" + (f" — {detail}" if detail else ""))
    else:    FAIL += 1; print(f"  [FAIL] {name}" + (f" — {detail}" if detail else ""))

def section(t): print("\n=== " + t + " ===")

# ---------------- ДАННЫЕ: обзор ----------------
section("Обзор региона  /api/overview")
try:
    st, ov = _req("/api/overview")
    check("HTTP 200", st == 200)
    reg = ov.get("region", {})
    check("region.throughput.pct есть", isinstance(reg.get("throughput", {}).get("pct"), int))
    check("region.bottleneck присутствует", "bottleneck" in reg)
    check("region.longRunners.count есть", isinstance(reg.get("longRunners", {}).get("count"), int))
    check("whatsBurning — список", isinstance(ov.get("whatsBurning"), list))
    check("processes >= 1", len(ov.get("processes", [])) >= 1)
    ui = ov.get("ui", {})
    check("ui.profiles непусто", len(ui.get("profiles", [])) >= 1)
    check("ui.chatPrompts непусто", len(ui.get("chatPrompts", [])) >= 1)
    check("ui.thresholds есть", "thresholds" in ui)
    keys = [p["key"] for p in ov.get("processes", [])]
except Exception as e:
    check("overview доступен", False, str(e)); keys = ["poruchenia"]

# ---------------- ДАННЫЕ: процесс ----------------
section("Процесс  /api/process (на примере poruchenia)")
KEY = "poruchenia" if "poruchenia" in keys else (keys[0] if keys else "poruchenia")
task_id = None
try:
    st, d = _req(f"/api/process?key={KEY}")
    check("HTTP 200", st == 200)
    check("kpi.total есть", isinstance(d.get("kpi", {}).get("total"), int))
    check("stages — список", isinstance(d.get("stages"), list))
    check("routeHist — список", isinstance(d.get("routeHist"), list))
    check("risk (normal/atRisk/critical/overdue)", all(k in d.get("risk", {}) for k in ["normal","atRisk","critical","overdue"]))
    check("trend — список", isinstance(d.get("trend"), list))
    # метрики H1 / process-mining
    check("flow.effPct есть (поток-эффективность)", isinstance(d.get("flow", {}).get("effPct"), (int, float)))
    check("flow.sample >= 0", d.get("flow", {}).get("sample", -1) >= 0)
    check("pickup.avgHours есть (скорость реакции)", isinstance(d.get("pickup", {}).get("avgHours"), (int, float)))
    check("pickup.byStage — список", isinstance(d.get("pickup", {}).get("byStage"), list))
    check("firstTimeRight.pct есть (с первого раза)", isinstance(d.get("firstTimeRight", {}).get("pct"), int))
    check("loops.loopPct есть (петли)", isinstance(d.get("loops", {}).get("loopPct"), int))
    check("loops.transitions — список", isinstance(d.get("loops", {}).get("transitions"), list))
    check("backlog — список месяцев (приток/отток)", isinstance(d.get("backlog"), list))
    check("returnAuthors — список (битва правок)", isinstance(d.get("returnAuthors"), list))
    check("decision.medianHours есть", "medianHours" in d.get("decision", {}))
except Exception as e:
    check("process доступен", False, str(e))

# ---------------- ДАННЫЕ: вспомогательные эндпоинты ----------------
section("Доп. эндпоинты процесса")
dept_id = None
for path, label, validate in [
    (f"/api/process/stuck?key={KEY}", "stuck (просроченные)", lambda j: isinstance(j.get("items"), list)),
    (f"/api/process/workload?key={KEY}", "workload (загрузка)", lambda j: isinstance(j.get("items"), list)),
    (f"/api/process/departments?key={KEY}", "departments (ведомства)", lambda j: isinstance(j.get("items"), list)),
    (f"/api/process/by-kind?key={KEY}", "by-kind (вид рассмотрения)", lambda j: isinstance(j.get("items"), list)),
]:
    try:
        st, j = _req(path)
        check(label, st == 200 and validate(j))
        if "stuck" in path and j.get("items"): task_id = j["items"][0].get("id")
        if "departments" in path and j.get("items"): dept_id = j["items"][0].get("deptId")
    except Exception as e:
        check(label, False, str(e))

if dept_id is not None:
    try:
        st, j = _req(f"/api/process/dept-tasks?key={KEY}&dept={dept_id}")
        check("dept-tasks (поручения подразделения)", st == 200 and isinstance(j.get("items"), list))
    except Exception as e:
        check("dept-tasks", False, str(e))
if task_id:
    try:
        st, j = _req(f"/api/task?id={task_id}")
        check("task (карточка задания + маршрут)", st == 200 and "header" in j and isinstance(j.get("stages"), list))
        check("task.rxLink (ссылка в RX)", bool(j.get("header", {}).get("rxLink")))
    except Exception as e:
        check("task", False, str(e))

# ---------------- ПЕРИОД ----------------
section("Фильтр периода")
try:
    st, j = _req(f"/api/process?key={KEY}&period=quarter")
    check("period=quarter отрабатывает", st == 200 and "kpi" in j)
except Exception as e:
    check("period", False, str(e))

# Нераспознанный period не должен всплывать голой пятисоткой (было — до фикса раунда 2):
# PeriodClause сама бросает исключение на неизвестное значение (это правильно), но
# эндпоинты обязаны перехватывать его и отдавать HTTP 200 с error, как остальной прототип.
BAD_PERIOD = urllib.parse.quote("мусор")
try:
    st, j = _req(f"/api/overview?period={BAD_PERIOD}")
    check("overview: period=мусор -> HTTP 200 (не 500), error по-русски",
          st == 200 and isinstance(j.get("error"), str) and "period" in j.get("error", ""))
except Exception as e:
    check("overview: period=мусор -> HTTP 200, а не 500", False, str(e))

try:
    bad_period_eps = [
        f"/api/process?key={KEY}",
        f"/api/process/stuck?key={KEY}",
        f"/api/process/workload?key={KEY}",
        f"/api/process/departments?key={KEY}",
        f"/api/process/by-kind?key={KEY}",
    ]
    ok_all = True
    for path in bad_period_eps:
        st, j = _req(path + f"&period={BAD_PERIOD}")
        if not (st == 200 and isinstance(j.get("error"), str)):
            ok_all = False
    check("остальные эндпоинты списка: мусорный period -> 200 с error, не 500", ok_all)
except Exception as e:
    check("остальные эндпоинты списка: мусорный period", False, str(e))

# ---------------- БЭК-ОФИС / КОНФИГ ----------------
section("Бэк-офис  /api/config (секреты маскируются)")
try:
    st, c = _req("/api/config")
    check("HTTP 200", st == 200)
    check("db.hasPassword — флаг, не значение", isinstance(c.get("db", {}).get("hasPassword"), bool))
    check("пароль НЕ возвращается", "password" not in c.get("db", {}) and "Password" not in c.get("db", {}))
    check("llm.hasToken — флаг, не значение", isinstance(c.get("llm", {}).get("hasToken"), bool)
          and "token" not in c.get("llm", {}) and "Token" not in c.get("llm", {}))
    check("profiles/thresholds/chatPrompts отдаются", all(k in c for k in ["profiles","thresholds","chatPrompts"]))
except Exception as e:
    check("config", False, str(e))

# ---------------- ЭКСПОРТ CSV ----------------
section("Экспорт CSV  /api/export")
for what in ["overdue", "workload", "departments"]:
    try:
        with urllib.request.urlopen(BASE + f"/api/export?what={what}&key={KEY}", timeout=60) as r:
            ct = r.headers.get("Content-Type", ""); body = r.read().decode("utf-8")
            check(f"export {what}: text/csv + заголовок", "text/csv" in ct and body.strip().count("\n") >= 1,
                  f"{body.strip().count(chr(10))+1} строк")
    except Exception as e:
        check(f"export {what}", False, str(e))

# ---------------- СОИСПОЛНИТЕЛИ: просрочка у соисполнителей (лидеры) ----------------
section("Просрочка у соисполнителей  /api/leaders, /api/leader/tasks")
first_perf_id = None
items_perf, items_bu = [], []   # инициализация: иначе падение эндпоинта роняет весь прогон по NameError
try:
    st, j = _req("/api/leaders?by=bu")
    check("by=bu отвечает и by='bu'", st == 200 and j.get("by") == "bu")
    items_bu = j.get("items", [])
    check("items непусто", len(items_bu) > 0)
    check("coOverdue — целое число у каждого элемента", all(isinstance(x.get("coOverdue"), int) for x in items_bu))
except Exception as e:
    check("leaders by=bu", False, str(e))

try:
    st, j = _req("/api/leaders?by=performer")
    items_perf = j.get("items", [])
    check("в разрезе performer есть сотрудник с coOverdue>0", any(x.get("coOverdue", 0) > 0 for x in items_perf))
    if items_perf: first_perf_id = items_perf[0].get("id")
except Exception as e:
    check("leaders by=performer (coOverdue>0)", False, str(e))

if first_perf_id is not None:
    try:
        st, j = _req(f"/api/leader/tasks?by=performer&id={first_perf_id}")
        lt_items = j.get("items", [])
        check("leader/tasks отвечает списком", st == 200 and isinstance(lt_items, list))
        check("co.total — целое число у каждого элемента", all(isinstance(x.get("co", {}).get("total"), int) for x in lt_items))
    except Exception as e:
        check("leader/tasks", False, str(e))
else:
    check("leader/tasks (нет id для проверки)", False)

try:
    st, jd = _req("/api/leaders?by=dept")
    items_dept = jd.get("items", [])
    check("coOverdue == 0 по значению у всех групп в dept и bu",
          all(x.get("coOverdue") == 0 for x in items_dept) and all(x.get("coOverdue") == 0 for x in items_bu))
except Exception as e:
    check("coOverdue==0 (dept/bu)", False, str(e))

check("контракт: если coOverdue>0, то risk истинно (performer)",
      all(x.get("risk") is True for x in items_perf if x.get("coOverdue", 0) > 0))

# ---------------- ИИ (ПРОВЕРИТЬ В ПОНЕДЕЛЬНИК) ----------------
section("ИИ-функциональность  (LLM Ario; проверять в ПОНЕДЕЛЬНИК)")
def ai_check(name, path, method="GET", body=None):
    try:
        st, j = _req(path, method=method, body=body, timeout=180)
        if isinstance(j, dict) and j.get("error"):
            AINOTE.append(f"{name}: LLM недоступна — '{str(j['error'])[:60]}' (ожидаемо до пн)")
            print(f"  [ИИ?] {name}: graceful-ошибка (LLM лежит) — в ПН ожидаем ответ")
        else:
            txt = (j.get("text") or j.get("reply") or "") if isinstance(j, dict) else ""
            ok = len(txt.strip()) > 20
            check(f"{name}: содержательный ответ ИИ", ok, f"{len(txt)} симв.")
    except Exception as e:
        AINOTE.append(f"{name}: запрос не прошёл — {e}")
        print(f"  [ИИ?] {name}: запрос не прошёл — {e}")

ai_check("summary (сводка по региону)", "/api/ai/summary")
ai_check("summary по процессу", f"/api/ai/summary?key={KEY}")
ai_check("chat (вопрос-ответ)", "/api/ai/chat", "POST",
         {"messages": [{"role": "user", "content": "Где главный затор?"}]})
ai_check("explain (разбор блока)", "/api/ai/explain", "POST",
         {"title": "Поток-эффективность", "key": KEY})

# ---------------- ХАРНЕСС: валидатор SQL ----------------
section("Валидатор SQL  /api/ai/sql/check")
import urllib.parse
def sqlcheck(q):
    # Падение ОДНОЙ проверки не должно обрывать весь блок (было: общий
    # try/except на семь проверок сразу — см. находки раунда 1, п.6).
    try:
        st, d = _req("/api/ai/sql/check?q=" + urllib.parse.quote(q))
        return d
    except Exception as e:
        return {"_exc": str(e)}

d = sqlcheck("select count(*) from sungero_wf_task limit 1")
check("корректный запрос пропущен", d.get("ok") is True, d.get("reason") or "")

d = sqlcheck("drop table sungero_wf_task")
check("drop table отклонён", d.get("ok") is False and "drop" in (d.get("reason") or ""))
check("при отказе effective не отдаётся", d.get("effective") is None)

d = sqlcheck("select 1; delete from sungero_wf_task")
check("два оператора (с запрещённым словом) отклонены", d.get("ok") is False)

d = sqlcheck("select 1; select 2")
check("два безобидных оператора отклонены (правило ';' изолировано от списка слов)",
      d.get("ok") is False)

d = sqlcheck("select 1 -- безобидно\n; delete from sungero_wf_task")
check("маскировка комментарием не проходит", d.get("ok") is False)

d = sqlcheck("select /* delete */ count(*) from sungero_wf_task")
check("запрет не срабатывает на слове в комментарии",
      d.get("ok") is True, d.get("reason") or "")
check("комментарий /* */ вырезан из effective",
      "delete" not in (d.get("effective") or "") and "/*" not in (d.get("effective") or ""),
      d.get("effective"))

d = sqlcheck("select 1 -- delete from t")
check("строчный комментарий -- вырезан из effective",
      "--" not in (d.get("effective") or ""), d.get("effective"))

d = sqlcheck("select id from sungero_wf_task")
check("запросу без limit добавлена обёртка",
      d.get("ok") is True and "limit 201" in (d.get("effective") or "").lower(),
      d.get("effective"))

d = sqlcheck("update sungero_wf_task set subject = 'x'")
check("update отклонён", d.get("ok") is False)

# --- находки раунда 1, пункты 1-2-5: семейства функций, SELECT INTO, юникод-идентификаторы ---
for bad in ["select dblink_exec('dbname=x','select 1')",
            "select pg_read_binary_file('pg_hba.conf')",
            "select pg_terminate_backend(1)",
            "select set_config('statement_timeout','0',false)",
            "select pg_advisory_lock(42)",
            "select query_to_xml('select 1', true, false, '')",
            "select * into zzz from sungero_wf_task",
            'select U&"pg_sl\\0065ep"(60)']:
    d = sqlcheck(bad)
    check("отклонено: " + bad[:46], d.get("ok") is False, d.get("reason") or "")

for good in ["select created from sungero_wf_task",
             "select setting from pg_settings",
             "select subject from sungero_wf_task where subject like '%update%'",
             "select string_agg(subject, '; ') from sungero_wf_task"]:
    d = sqlcheck(good)
    check("пропущено: " + good[:46], d.get("ok") is True, d.get("reason") or "")

# --- находка раунда 1, пункт 3: обёртка limit 200 накладывается безусловно ---
d = sqlcheck("select id from sungero_wf_task limit 100000000")
check("обёртка накладывается даже при своём limit",
      "limit 201" in (d.get("effective") or "").lower(), d.get("effective"))

d = sqlcheck("select * from a where a.x in (select y from sungero_wf_task limit 10)")
check("вложенный limit не подменяет внешнюю обёртку",
      (d.get("effective") or "").lower().count("limit") >= 2 and
      (d.get("effective") or "").lower().rstrip().endswith("limit 201"),
      d.get("effective"))

# --- находка раунда 1, пункт 4: строковые литералы разбираются посимвольно ---
d = sqlcheck("select '-- это данные, а не комментарий' as x")
check("литерал, похожий на комментарий, не режется",
      d.get("ok") is True and "это данные" in (d.get("effective") or ""),
      d.get("effective"))

d = sqlcheck("select 'unterminated")
check("незакрытый строковый литерал отклонён", d.get("ok") is False)

d = sqlcheck("select 'it''s ok' as x")
check("удвоенная кавычка внутри литерала не рвёт разбор",
      d.get("ok") is True, d.get("reason") or "")

# --- находки раунда 2, п. 1-2: вырезание /* */ склеивало токены; имя в "" было невидимо ---
for bad in ["select dbl/**/ink_exec('dbname=x','select 1')",
            "select * in/**/to zzz from sungero_wf_task",
            "select se/**/t_config('statement_timeout','0',false)",
            'select "pg_sleep"(60)',
            'select "dblink_exec"(\'dbname=x\',\'select 1\')']:
    d = sqlcheck(bad)
    check("отклонено: " + bad[:46], d.get("ok") is False, d.get("reason") or "")

d = sqlcheck("select a/**/from sungero_wf_task")
check("комментарий заменён разделителем, а не склейкой",
      "afrom" not in (d.get("effective") or ""), d.get("effective"))

# --- находка раунда 2: легитимные запросы не должны попасть под новые правки ---
for good in ["select created from sungero_wf_task",
             "select setting from pg_settings",
             "select subject from sungero_wf_task where subject like '%update%'",
             "select string_agg(subject, '; ') from sungero_wf_task",
             'select "стран;ный" from t',
             "select $tag$ text with ; and -- inside $tag$",
             "select 'it''s ok'"]:
    d = sqlcheck(good)
    check("пропущено: " + good[:46], d.get("ok") is True, d.get("reason") or "")

# --- находка финальной проверки ветки: таблицы с учётными данными ролей БД должны быть
# отклонены валидатором независимо от /api/ai/schema, а безобидные системные представления
# (pg_settings, information_schema) — по-прежнему проходить ---
d = sqlcheck("select rolpassword from pg_authid")
check("запрос к таблице с хешами паролей отклонён", d.get("ok") is False, d.get("reason") or "")
d = sqlcheck("select name from pg_settings limit 5")
check("запрос к pg_settings по-прежнему проходит", d.get("ok") is True, d.get("reason") or "")

# ---------------- ХАРНЕСС: исполнитель SQL ----------------
section("Исполнитель SQL  /api/ai/sql/run")

def sqlrun(q):
    # Падение ОДНОЙ проверки не должно обрывать весь блок (находка ревью task-5,
    # раунд правок 1, п.5 — здесь снова был один try на несколько check подряд).
    try:
        st, d = _req("/api/ai/sql/run?q=" + urllib.parse.quote(q))
        return d
    except Exception as e:
        return {"_exc": str(e)}

d = sqlrun("select count(*) as c from sungero_wf_task")
check("исполнитель вернул строки", isinstance(d.get("rows"), list) and len(d.get("rows") or []) == 1, d.get("_exc") or d.get("error"))
check("исполнитель вернул колонки", d.get("cols") == ["c"])
check("исполнитель отдаёт время", isinstance(d.get("ms"), int))

d = sqlrun("select 'текст'::text as t, 42 as n, now() as d, true as b")
check("исполнитель отдаёт типы колонок",
      d.get("types") == ["text", "number", "date", "bool"], str(d.get("types")))

d = sqlrun("update sungero_wf_task set subject='x'")
check("запись не исполняется", bool(d.get("error")))

# --- второй рубеж: исполняется effective, а не исходный текст ---
# Комментарий вырезается валидатором ДО проверки, поэтому текст, замаскированный
# комментарием, обязан быть отклонён целиком, а не исполнен "как есть".
d = sqlcheck("select 1 -- /*\n drop table sungero_wf_task */")
check("маскировка комментарием отклонена валидатором", d.get("ok") is False)
d = sqlrun("select 1 -- /*\n drop table sungero_wf_task */")
check("исполнитель тоже отклоняет замаскированный drop", bool(d.get("error")), d.get("_exc"))

# --- лимит строк и честный truncated (находка ревью task-5, раунд правок 1, п.2) ---
# Раньше обёртка SqlCheck сама заканчивалась на "limit 200" — тем же числом, что и
# maxRows в SqlRun, поэтому 201-я строка физически никогда не попадала в SqlRun и
# truncated был всегда false, даже когда выборка была реально урезана (модель получила
# бы 200 строк из 81095 и решила бы, что это все данные). Теперь SqlCheck оборачивает
# в "limit 201", а maxRows в SqlRun остаётся 200 — лишняя 201-я строка используется
# только как признак усечения и в ответ не попадает.
d = sqlrun("select * from generate_series(1,1000) as g(n)")
check("лимит строк соблюдён (ровно 200)", len(d.get("rows") or []) == 200, len(d.get("rows") or []))
check("truncated=true: выдача реально урезана (1000 строк источника, отдано 200)",
      d.get("truncated") is True, d.get("truncated"))

d = sqlrun("select id from sungero_wf_task limit 5")
check("truncated=false: источник короче лимита, усечения нет",
      d.get("truncated") is False, d.get("truncated"))

# --- находка ревью task-5, раунд правок 2, п.C: граница ровно на стыке 200/201 ---
# SqlCheck оборачивает в "limit SqlMaxRows+1", SqlRun режет по maxRows=SqlMaxRows —
# два магических числа вынесены в одну общую константу именно потому, что их
# рассинхронизация уже один раз ломала truncated (раунд правок 1, п.2).
d = sqlrun("select * from generate_series(1,200) as g(n)")
check("граница снизу: ровно 200 строк источника — усечения нет",
      d.get("truncated") is False and len(d.get("rows") or []) == 200,
      (d.get("truncated"), len(d.get("rows") or [])))

d = sqlrun("select * from generate_series(1,201) as g(n)")
check("граница сверху: 201 строка источника — усечение есть",
      d.get("truncated") is True and len(d.get("rows") or []) == 200,
      (d.get("truncated"), len(d.get("rows") or [])))

# --- находка ревью task-5, раунд правок 1, п.3 / раунд правок 2, п.A и п.B ---
# Раунд 1 обрезал массив по .NET-типу через Convert.ToString, а не по представлению —
# результатом было "System.String[]" (имя типа, а не данные). Прежняя проверка здесь
# ("isinstance(val, str) and len(val) < 400") пропускала и это: "System.String[]" —
# строка длиной 15, короче 400, тест был ложно-зелёным. Теперь массив обязан остаться
# JSON-массивом, а обрезке подвергается каждый элемент по отдельности.
d = sqlrun("select array[repeat('y', 400)] as a")
val = (d.get("rows") or [[None]])[0][0] if d.get("rows") else None
check("массив остался массивом, а элемент обрезан",
      isinstance(val, list) and len(val) == 1
      and isinstance(val[0], str) and len(val[0]) <= 201, val)

d = sqlrun("select array['a','b'] as arr")
val = (d.get("rows") or [[None]])[0][0] if d.get("rows") else None
check("короткий массив отдан как JSON-массив, а не как имя типа", val == ["a", "b"], val)

d = sqlrun("select repeat('z', 400)::bytea as b")
val = (d.get("rows") or [[None]])[0][0] if d.get("rows") else None
check("значение-bytea (base64) обрезано, а не отдано целиком",
      isinstance(val, str) and len(val) <= 201, val if not isinstance(val, str) else len(val))

d = sqlrun("select inet '1.2.3.4' as ip")
check("inet не роняет эндпоинт в {error}", bool(d.get("rows")) and not d.get("error"), d.get("error"))

# --- находка ревью task-5, раунд правок 1, п.4: второй рубеж (read-only транзакция) ---
# Оба прежних "пишущих" теста (update/drop выше) отсекались ещё валидатором — то есть
# были бы зелёными при ЛЮБОЙ реализации SqlRun, включая вариант вообще без read-only
# транзакции. nextval() не входит ни в один деней-лист (это не запись данных с точки
# зрения SqlCheck), поэтому проходит валидатор и разбивается именно о PostgreSQL.
# Имя последовательности берём из самой БД: захардкоженное имя могло бы не существовать
# на другом стенде. nextval() физически НЕ увеличивает счётчик — read-only транзакция
# откатывает попытку целиком, это и есть предмет проверки.
seqd = sqlrun("select sequence_name from information_schema.sequences limit 1")
seqrows = seqd.get("rows") or []
if seqrows:
    seq = seqrows[0][0]
    d = sqlrun("select nextval('\"" + seq + "\"')")
    err = (d.get("error") or "").lower()
    check("read-only транзакция блокирует запись мимо валидатора (nextval на " + seq + ")",
          "nextval" in err and ("только" in err or "read-only" in err), d.get("error"))
else:
    check("read-only транзакция блокирует запись мимо валидатора", False, "в БД нет ни одной последовательности")

# --- statement_timeout: тяжёлый запрос обязан быть прерван, а не повесить стенд ---
# cross join двух generate_series по 20000 даёт count(*) по 400 млн строк — агрегату
# нужно пройти всё до конца, прежде чем вернуть хотя бы одну строку, поэтому внешний
# "limit 201" не спасает: без statement_timeout запрос считал бы десятки секунд/минуты.
d = sqlrun("select count(*) from generate_series(1,20000) a cross join generate_series(1,20000) b")
err = (d.get("error") or "").lower()
check("statement_timeout прерывает тяжёлый запрос", "57014" in err or "тайм-аут" in err or "timeout" in err, d.get("error"))

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

    st, d = _req("/api/ai/tool?name=" + urllib.parse.quote("нет_такого") + "&args=%7B%7D")
    check("неизвестный инструмент даёт понятную ошибку", "неизвестный" in (d.get("error") or ""))

    # сходимость ещё двух инструментов с прямыми эндпоинтами (находка р1, п.4)
    st, direct = _req("/api/leaders?by=dept")
    st, via_tool = _req("/api/ai/tool?name=leaders&args=" + urllib.parse.quote('{"by":"dept"}'))
    check("инструмент leaders совпадает с /api/leaders",
          [i.get("name") for i in direct.get("items", [])] == [i.get("name") for i in via_tool.get("items", [])])

    st, direct = _req("/api/process?key=appeals")
    st, via_tool = _req("/api/ai/tool?name=process&args=" + urllib.parse.quote('{"key":"appeals"}'))
    check("инструмент process совпадает с /api/process",
          direct.get("kpi") == via_tool.get("kpi"), str(direct.get("kpi"))[:80])

    # некорректные вызовы обязаны давать понятную ошибку модели, а не правдоподобные данные (п.1-3)
    st, d = _req("/api/ai/tool?name=leader_tasks&args=%7B%7D")
    check("leader_tasks без id даёт ошибку с подсказкой про id",
          "error" in d and "id" in (d.get("error") or ""), d.get("error"))

    st, d = _req("/api/ai/tool?name=leader_tasks&args=" +
                 urllib.parse.quote('{"by":"performer","id":"НЕ_ЧИСЛО"}'))
    check("leader_tasks с нечисловым id даёт ту же ошибку",
          "error" in d and "id" in (d.get("error") or ""), d.get("error"))

    st, d = _req("/api/ai/tool?name=process&args=" + urllib.parse.quote('{"key":123}'))
    check("process с key не строкой даёт ошибку про тип аргумента",
          "error" in d and "key" in (d.get("error") or ""), d.get("error"))

    st, d = _req("/api/ai/tool?name=process&args=" +
                 urllib.parse.quote('{"key":"выдуманный_процесс"}'))
    check("process с неизвестным ключом перечисляет допустимые значения",
          "error" in d and "poruchenia" in (d.get("error") or ""), d.get("error"))

    # нормальный вызов leader_tasks: id строкой и числом дают один и тот же результат
    st, d1 = _req("/api/ai/tool?name=leaders&args=" + urllib.parse.quote('{"by":"performer"}'))
    first = (d1.get("items") or [{}])[0].get("id")
    if first is not None:
        st, s1 = _req("/api/ai/tool?name=leader_tasks&args=" + urllib.parse.quote('{"by":"performer","id":"%s"}' % first))
        st, n1 = _req("/api/ai/tool?name=leader_tasks&args=" + urllib.parse.quote('{"by":"performer","id":%s}' % first))
        check("leader_tasks принимает id и строкой, и числом",
              not s1.get("error") and not n1.get("error")
              and len(s1.get("items", [])) == len(n1.get("items", [])),
              str(s1.get("error") or n1.get("error") or "ok"))
    else:
        check("есть хотя бы один сотрудник для проверки leader_tasks", False, "leaders вернул пустой список")

    d = _req("/api/ai/tool?name=process&args=" + urllib.parse.quote('{"key":true}'))[1]
    check("логическое значение аргумента отвергается", bool(d.get("error")), str(d.get("error"))[:80])

    st, d = _req("/api/ai/tool?name=process&args=" + urllib.parse.quote("не_json"))
    check("невалидный JSON в args даёт русскую ошибку про формат",
          "error" in d and "JSON" in (d.get("error") or "") and "LineNumber" not in (d.get("error") or ""),
          d.get("error"))

    # обычный путь не должен был пострадать от новых проверок
    st, d = _req("/api/ai/tool?name=process&args=" + urllib.parse.quote('{"key":"appeals"}'))
    check("process(key=appeals) через инструмент отвечает нормальными данными",
          "error" not in d and "kpi" in d, str(list(d)[:4]))

    st, d = _req("/api/ai/tool?name=my_tasks&args=%7B%7D")
    check("my_tasks через инструмент отвечает нормальными данными",
          "error" not in d, str(list(d)[:4]))
except Exception as e:
    check("каталог инструментов доступен", False, str(e))

section("Словарь схемы  /api/ai/schema")
try:
    st, d = _req("/api/ai/schema?table=sungero_wf_assignment")
    cols = [c["name"] for c in d.get("columns", [])]
    check("справка по таблице отдаёт колонки", len(cols) > 5, str(len(cols)))
    check("в колонках есть deadline", "deadline" in cols)
    check("у колонки есть тип", bool(d.get("columns", [{}])[0].get("type")))

    # Находка ревью (task-7, раунд правок 1, п.4): у каждой непустой колонки должны быть
    # samples — без них справка по типу USER-DEFINED (например status) не говорит ничего.
    deadline_col = next(c for c in d["columns"] if c["name"] == "deadline")
    check("у колонки deadline есть samples", isinstance(deadline_col.get("samples"), list) and len(deadline_col["samples"]) > 0,
          str(deadline_col.get("samples")))
    status_col = next(c for c in d["columns"] if c["name"] == "status")
    check("у колонки status есть непустые samples (тип USER-DEFINED без них бесполезен)",
          isinstance(status_col.get("samples"), list) and len(status_col["samples"]) > 0, str(status_col.get("samples")))
    check("каждое значение samples обрезано до 100 символов",
          all(isinstance(s, str) and len(s) <= 101 for c in d["columns"] for s in c.get("samples", [])))

    # Находка ревью (п.6): sungero_wf_assignment — 217 колонок, лимит справки 80 —
    # признак усечения обязан быть честным, как truncated у SqlRun.
    check("totalColumns отражает реальное число колонок (> лимита в 80)",
          isinstance(d.get("totalColumns"), int) and d["totalColumns"] > 80, str(d.get("totalColumns")))
    check("truncated=true для широкой таблицы sungero_wf_assignment", d.get("truncated") is True, str(d.get("truncated")))

    st, d = _req("/api/ai/schema?table=" + urllib.parse.quote("нет_такой_таблицы"))
    check("несуществующая таблица — понятная ошибка", bool(d.get("error")) or d.get("columns") == [])
    st, d = _req("/api/ai/schema?table=" + urllib.parse.quote("sungero; drop"))
    check("имя таблицы с недопустимыми символами отвергается", bool(d.get("error")), str(d.get("error")))

    # Находка финальной проверки ветки: справка собирала примеры значений из ЛЮБОЙ таблицы,
    # проходящей регулярку имени, включая системные каталоги PostgreSQL (pg_authid — хеши
    # паролей ролей БД). Теперь справка доступна только по таблицам RX (sungero_*, gd_govsol_*).
    st, d = _req("/api/ai/schema?table=pg_authid")
    check("справка по системной таблице отклонена", bool(d.get("error")), str(d.get("error"))[:80])
    st, d = _req("/api/ai/schema?table=sungero_wf_assignment")
    check("справка по таблице RX по-прежнему работает", len(d.get("columns", [])) > 5)

    # Находка ревью (task-7, раунд правок 2): справка на широкой таблице собирала примеры
    # отдельным SELECT на каждую колонку — 11,5с на sungero_wf_assignment. Кэш по имени таблицы
    # без TTL должен делать повторный вызов почти мгновенным. Берём другую широкую таблицу
    # (sungero_wf_task, 213 колонок) — sungero_wf_assignment уже прогрета проверками выше,
    # а тут нужен честный первый (некэшированный) вызов в рамках этого прогона smoke-теста.
    #
    # Раунд правок 3: относительный критерий (second < first/3) был хрупким — при повторном
    # прогоне smoke без перезапуска сервера кэш уже прогрет с прошлого раза, оба вызова идут
    # из кэша (first=0.013s second=0.014s), и деление не даёт нужного запаса. Критерий заменён
    # на абсолютный порог: сколько бы ни занял первый вызов (холодный или уже из кэша), повторный
    # обязан укладываться в 0,3с. Если кэш сломают, повторный вызов снова станет многосекундным
    # и проверка честно покраснеет — независимо от того, первый это прогон smoke или сотый.
    t0 = time.time()
    _req("/api/ai/schema?table=sungero_wf_task")
    first_s = time.time() - t0
    t0 = time.time()
    _req("/api/ai/schema?table=sungero_wf_task")
    second_s = time.time() - t0
    CACHE_MAX_S = 0.3
    check("повторный вызов справки по той же таблице укладывается в 0,3с (кэш)",
          second_s < CACHE_MAX_S,
          f"first={first_s:.3f}s second={second_s:.3f}s")
except Exception as e:
    check("справка по схеме доступна", False, str(e))

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

d = probe("tool=leaders&array=" + urllib.parse.quote("нет_такого") + "&columns=name")
check("несуществующий массив даёт понятную ошибку", bool(d.get("error")), str(d.get("error"))[:80])

d = probe("tool=leaders&array=items&columns=name," + urllib.parse.quote("нет_такой_колонки"))
check("несуществующая колонка отбрасывается, а не роняет сборку",
      [c["name"] for c in d.get("cols", [])] == ["name"], str(d.get("cols")))

# Находка ревью (круг правок 1, Important): JsonColType сузили так, чтобы "12:30" не
# считалось датой. Эта проверка — что настоящие даты (deadline инструмента stuck,
# формат "2023-07-07") сужение не задело.
d = probe("tool=stuck&array=items&columns=subject,deadline,overdueDays")
check("настоящая дата (stuck.deadline) по-прежнему определяется как date",
      [c.get("type") for c in d.get("cols", [])] == ["text", "date", "number"],
      str(d.get("cols")))

# ---------------- ИИ: цикл агента ----------------
section("Цикл агента  /api/ai/sql")

def agent_ok(name, d, cond, detail=""):
    # Тот же принцип, что у ai_check выше: недоступность модели (или БД на этапе
    # предзагрузки) — не провал теста, а ожидаемое до понедельника состояние. Раньше этот
    # блок использовал голый check() и любая недоступность LLM превращалась в семь FAIL
    # (находка ревью р1, п.I9) — заявленный FAIL=0 был достижим только при живой модели.
    if isinstance(d, dict) and d.get("error"):
        AINOTE.append(f"{name}: LLM/инфраструктура недоступна — '{str(d['error'])[:60]}' (ожидаемо до пн)")
        print(f"  [ИИ?] {name}: graceful-ошибка — в ПН ожидаем содержательный результат")
    else:
        check(name, cond, detail)

try:
    st, d = _req("/api/ai/sql", method="POST", body={"messages": [
        {"role": "user", "content": "Сколько заданий просрочено?"}]}, timeout=180)
    agent_ok("ответ непустой", d, bool(d.get("reply")))
    agent_ok("протокол шагов есть", d, isinstance(d.get("steps"), list))
    # Раньше здесь была проверка d.get("preloaded") == ["overview","processes"] — она
    # проходит при ЛЮБОЙ реализации, так как preloaded в ответе — захардкоженный литерал,
    # не зависящий от того, выполнилась ли предзагрузка на самом деле (находка ревью р1,
    # п.I9). Настоящий признак того, что предзагрузка сработала: вопрос, закрытый готовыми
    # метриками дашборда, не потребовал ни единого шага sql.
    agent_ok("вопрос из готовых метрик не потребовал SQL (предзагрузка сработала)", d,
             all(s.get("action") != "sql" for s in d.get("steps", [])),
             str([s.get("action") for s in d.get("steps", [])]))
    agent_ok("уложились в бюджет", d, isinstance(d.get("elapsedMs"), int) and d["elapsedMs"] < 70000)

    st, d = _req("/api/ai/sql", method="POST", body={"messages": [
        {"role": "user", "content":
         "Сколько заданий создано в 2023 году? Это не считает дашборд, нужен запрос."}]},
        timeout=180)
    agent_ok("нестандартный вопрос дошёл до SQL", d,
             any(s.get("action") == "sql" for s in d.get("steps", [])),
             str([s.get("action") for s in d.get("steps", [])]))
    agent_ok("у шага SQL виден текст запроса", d,
             any(s.get("sql") for s in d.get("steps", []) if s.get("action") == "sql"))

    st, d = _req("/api/ai/sql", method="POST", body={"messages": [
        {"role": "user", "content": "Сколько поручений создано за последний квартал?"}]},
        timeout=180)
    agent_ok("«за последний квартал» закрылось инструментом с period, а не своим SQL", d,
             any(s.get("action") == "tool" and "period" in str(s.get("args") or "")
                 for s in d.get("steps", [])) and
             all(s.get("action") != "sql" for s in d.get("steps", [])),
             str([(s.get("action"), s.get("tool"), s.get("args")) for s in d.get("steps", [])]))
except Exception as e:
    AINOTE.append(f"цикл агента: запрос не прошёл — {e}")
    print(f"  [ИИ?] цикл агента: запрос не прошёл — {e}")

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

# --- детерминированные проверки харнесса, не требующие живой модели (находка ревью р1, п.I9) ---
try:
    st, d = _req_raw("/api/ai/sql", b'{"messages": [invalid json')
    check("битое тело /api/ai/sql — понятная ошибка вместо падения",
          st == 200 and isinstance(d, dict) and "error" in d and "bad request" in str(d["error"]),
          str(d)[:160])
except Exception as e:
    check("битое тело /api/ai/sql — понятная ошибка вместо падения", False, str(e))

# ---------------- ИТОГ ----------------
section("ИТОГ")
print(f"  Проверок данных/UI: PASS={PASS}  FAIL={FAIL}")
if AINOTE:
    print("  ИИ (отложено на понедельник):")
    for n in AINOTE: print("    • " + n)
print("\n" + ("ВСЁ ОК (данные/UI)" if FAIL == 0 else f"ЕСТЬ ПАДЕНИЯ: {FAIL}"))
sys.exit(1 if FAIL else 0)
