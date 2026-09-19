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
urllib.request.getproxies = lambda: {}  # локальный smoke не через системный прокси

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

def _req_static(path, timeout=20):
    # Для статики: тело не JSON (HTML/CSS/JS/бинарь), а на 404 urlopen кидает
    # HTTPError вместо обычного возврата — ловим его, чтобы код ответа читался
    # тем же способом, что и у успешного запроса.
    url = BASE + path
    try:
        with urllib.request.urlopen(url, timeout=timeout) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()

def check(name, cond, detail=""):
    global PASS, FAIL
    if cond: PASS += 1; print(f"  [ OK ] {name}" + (f" — {detail}" if detail else ""))
    else:    FAIL += 1; print(f"  [FAIL] {name}" + (f" — {detail}" if detail else ""))

def section(t): print("\n=== " + t + " ===")

# ---------------- СТАТИКА: белый список раздачи файлов ----------------
# CRITICAL из финального ревью ветки, п.1: раньше GET отдавал ЛЮБОЙ файл из
# AppContext.BaseDirectory, включая config.json с паролем БД и токенами LLM.
# Белый список должен не только закрыть утечку, но и не задеть ни один из
# файлов, которые реально запрашивает index.html — иначе экраны частично
# перестанут отрисовываться.
section("Статика — белый список раздачи (config.json больше не отдаётся)")
try:
    st, body = _req_static("/config.json")
    check("GET /config.json — не 200 с содержимым (404 или иной отказ)",
          st != 200, f"status={st} bytes={len(body)}")
except Exception as e:
    check("GET /config.json — не 200 с содержимым (404 или иной отказ)", False, str(e))

for fname in ("/index.html", "/charts.js", "/analysis.js", "/style.css", "/tokens.css", "/logo-directum.svg"):
    try:
        st, body = _req_static(fname)
        check(f"GET {fname} — по-прежнему отдаётся (200)", st == 200 and len(body) > 0,
              f"status={st} bytes={len(body)}")
    except Exception as e:
        check(f"GET {fname} — по-прежнему отдаётся (200)", False, str(e))

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
             "select subject from sungero_wf_task where subject like '%update%'"]:
    d = sqlcheck(good)
    check("пропущено: " + good[:46], d.get("ok") is True, d.get("reason") or "")

d = sqlcheck("select string_agg(subject, '; ') from sungero_wf_task")
check("string_agg вне allowlist функций — намеренный отказ scope",
      d.get("ok") is False, d.get("reason") or "")

# --- находка раунда 1, пункт 3: обёртка limit 200 накладывается безусловно ---
d = sqlcheck("select id from sungero_wf_task limit 100000000")
check("обёртка накладывается даже при своём limit",
      "limit 201" in (d.get("effective") or "").lower(), d.get("effective"))

d = sqlcheck("select id from sungero_wf_task where id in (select id from sungero_wf_task limit 10)")
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
             "select subject from sungero_wf_task where subject like '%update%'",
             'select subject as "стран;ный" from sungero_wf_task limit 1',
             "select $tag$ text with ; and -- inside $tag$",
             "select 'it''s ok'"]:
    d = sqlcheck(good)
    check("пропущено: " + good[:46], d.get("ok") is True, d.get("reason") or "")

# --- находка финальной проверки ветки: таблицы с учётными данными ролей БД должны быть
# отклонены валидатором независимо от /api/ai/schema. С Task 3+ единая SqlScopePolicy
# намеренно отклоняет системные каталоги вне allowlist каталога харнесса ---
d = sqlcheck("select rolpassword from pg_authid")
check("запрос к таблице с хешами паролей отклонён", d.get("ok") is False, d.get("reason") or "")
d = sqlcheck("select name from pg_settings limit 5")
check("pg_settings вне каталога — намеренный отказ scope",
      d.get("ok") is False and "pg_settings" in (d.get("reason") or "").lower(),
      d.get("reason") or "")
d = sqlcheck("select sequence_name from information_schema.sequences limit 1")
check("information_schema вне каталога — намеренный отказ scope",
      d.get("ok") is False, d.get("reason") or "")
d = sqlcheck("select count(*) from sungero_wf_task limit 1")
check("таблица RX из allowlist по-прежнему проходит scope",
      d.get("ok") is True, d.get("reason") or "")

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

d = sqlrun("select 'текст'::text as t, 42 as n, '2026-01-01'::date as d, true as b")
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
# generate_series/array/repeat вне scope allowlist — лимиты строк проверяем на RX-таблице.
d = sqlrun("select id from sungero_wf_task order by id")
check("лимит строк соблюдён (ровно 200)", len(d.get("rows") or []) == 200, len(d.get("rows") or []))
check("truncated=true: sungero_wf_task длиннее лимита",
      d.get("truncated") is True, d.get("truncated"))

d = sqlrun("select id from sungero_wf_task order by id limit 5")
check("truncated=false: явный limit 5 короче потолка",
      d.get("truncated") is False, d.get("truncated"))

# Граница 200/201 и обрезка array/repeat/bytea — harness-tests ResultsTests (offline).

# --- read-only транзакция: nextval() через information_schema больше недоступен (scope).
# Поведение read-only на INSERT/nextval покрыто DbTests на harness_test (Task 11).
# Здесь фиксируем, что information_schema для SqlRun тоже отклоняется до исполнения.
d = sqlrun("select sequence_name from information_schema.sequences limit 1")
check("information_schema для SqlRun отклонён scope до исполнения",
      bool(d.get("error")), d.get("error") or d.get("_exc"))

# --- statement_timeout: тяжёлый запрос обязан быть прерван, а не повесить стенд ---
# cross join двух generate_series по 20000 даёт count(*) по 400 млн строк — агрегату
# нужно пройти всё до конца, прежде чем вернуть хотя бы одну строку, поэтому внешний
# "limit 201" не спасает: без statement_timeout запрос считал бы десятки секунд/минуты.
d = sqlrun(
    "select count(*) from sungero_wf_task a "
    "cross join sungero_wf_assignment b "
    "cross join sungero_wf_task c "
    "cross join sungero_wf_assignment d")
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
section("Плоские колонки для графика  (разбивка по процессам и тренд)")

# Пользователь поймал расхождение: на графике у Иванова 123, в тексте 118. Обе цифры
# настоящие — 123 это просрочка по всем процессам, 118 только по поручениям. Разбивка
# лежала в items[].processes[], куда сборщик датасета не дотягивается, и модели нечего
# было указать, кроме итоговой колонки. Плоские колонки закрывают это по построению.
try:
    st, _lead = _req("/api/leaders?by=performer")
    _nested = {}
    for _it in (_lead.get("items") or []):
        for _p in (_it.get("processes") or []):
            if _p.get("key") == "poruchenia":
                _nested[_it.get("name")] = _p.get("overdue", 0)
    d = probe("tool=leaders&array=items&columns=name,overdue_poruchenia")
    check("плоская колонка overdue_poruchenia доступна графику",
          [c.get("name") for c in (d.get("cols") or [])] == ["name", "overdue_poruchenia"],
          str(d.get("cols") or d.get("error"))[:110])
    _rows = {r[0]: r[1] for r in (d.get("rows") or []) if isinstance(r, list) and len(r) >= 2}
    _mismatch = [(n, v, _rows.get(n)) for n, v in _nested.items() if _rows.get(n) != v]
    check("плоская разбивка совпадает со вложенной у всех исполнителей",
          bool(_rows) and not _mismatch, str(_mismatch[:3]))
except Exception as e:
    check("разбивка по процессам: запрос прошёл", False, str(e))

# Тот же пробел с другой стороны: месячный тренд лежал в processes[].trend, и на вопрос
# «покажи тренд» модель уходила сочинять SQL вместо готовых и верных чисел дашборда.
try:
    st, _procs = _req("/api/processes")
    _flat = {r.get("month"): r for r in (_procs.get("trendByMonth") or [])}
    _agg = {}
    for _p in (_procs.get("processes") or []):
        for _t in (_p.get("trend") or []):
            _a = _agg.setdefault(_t.get("month"), [0, 0])
            _a[0] += _t.get("ontime", 0)
            _a[1] += _t.get("overdue", 0)
    check("плоский тренд по месяцам есть в предзагрузке", bool(_flat),
          ("месяцев: %d" % len(_flat)) if _flat else "trendByMonth отсутствует или пуст")
    _bad = [(m, v, _flat.get(m)) for m, v in _agg.items()
            if (_flat.get(m) or {}).get("ontime") != v[0] or (_flat.get(m) or {}).get("overdue") != v[1]]
    check("суммы плоского тренда сходятся с разбивкой по процессам",
          bool(_flat) and not _bad, str(_bad[:2])[:150])
except Exception as e:
    check("плоский тренд: запрос прошёл", False, str(e))

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
    # Потолок проверки = бюджет агента (120 с) плюс те же 10 с запаса, что были при
    # бюджете 60 с: проверка цикла агента в Program.cs стоит в начале шага, поэтому шаг,
    # начавшийся у самой границы, вправе закончиться чуть позже неё.
    agent_ok("уложились в бюджет", d, isinstance(d.get("elapsedMs"), int) and d["elapsedMs"] < 130000)

    st, d = _req("/api/ai/sql", method="POST", body={"messages": [
        {"role": "user", "content":
         "Сколько заданий создано в 2023 году? Это не считает дашборд, нужен запрос."}]},
        timeout=180)
    _llm_down = isinstance(d, dict) and (d.get("error") or
        any(s.get("action") == "error" for s in d.get("steps", [])))
    if _llm_down:
        AINOTE.append("нестандартный вопрос/SQL: LLM недоступна — " + str(d.get("error") or d.get("steps"))[:60])
        print("  [ИИ?] нестандартный вопрос/SQL: graceful-ошибка LLM")
    else:
        agent_ok("нестандартный вопрос дошёл до SQL", d,
                 any(s.get("action") == "sql" for s in d.get("steps", [])),
                 str([s.get("action") for s in d.get("steps", [])]))
        agent_ok("у шага SQL виден текст запроса", d,
                 any(s.get("sql") for s in d.get("steps", []) if s.get("action") == "sql"))

    st, d = _req("/api/ai/sql", method="POST", body={"messages": [
        {"role": "user", "content": "Сколько поручений создано за последний квартал?"}]},
        timeout=180)
    _llm_down = isinstance(d, dict) and (d.get("error") or
        any(s.get("action") == "error" for s in d.get("steps", [])))
    if _llm_down:
        AINOTE.append("period tool path: LLM недоступна — " + str(d.get("error") or d.get("steps"))[:60])
        print("  [ИИ?] period tool path: graceful-ошибка LLM")
    else:
        agent_ok("«за последний квартал» закрылось инструментом с period, а не своим SQL", d,
                 any(s.get("action") == "tool" and "period" in str(s.get("args") or "")
                     for s in d.get("steps", [])) and
                 all(s.get("action") != "sql" for s in d.get("steps", [])),
                 str([(s.get("action"), s.get("tool"), s.get("args")) for s in d.get("steps", [])]))
except Exception as e:
    AINOTE.append(f"цикл агента: запрос не прошёл — {e}")
    print(f"  [ИИ?] цикл агента: запрос не прошёл — {e}")

# --- датасет для визуализации (страница «Аналитика по запросу») ---
# Точка 4 ревью р2: исключение здесь означает недоступную/медленную модель (_req падает
# именно на таймауте/отказе соединения) — тот же случай, что уже обрабатывает agent_ok,
# поэтому на исключении пишем AINOTE + [ИИ?], а не check(False), иначе гарантия "лежащая
# модель не даёт ни PASS, ни FAIL" здесь не выполняется.
try:
    st, d = _req("/api/ai/sql", method="POST", body={"messages": [
        {"role": "user", "content": "Покажи просрочку по подразделениям"}]}, timeout=180)
except Exception as e:
    AINOTE.append(f"датасет: запрос к агенту не прошёл — {e}")
    print(f"  [ИИ?] датасет: запрос к агенту не прошёл — {e}")
else:
    # Разбор ответа — ВНЕ try/except: если сервер вернёт dataset не того типа или rows не
    # списком, разбор должен упасть громко (необработанное исключение), а не превратиться в
    # "[ИИ?] запрос не прошёл" — под этот путь иначе маскируется самая ценная проверка всей
    # ветки, сверка датасета с preview SQL-шага ниже (находка финального ревью, п.8). Только
    # вызов _req (сеть/таймаут/недоступность LLM) — законный повод для мягкого [ИИ?].
    ds = d.get("dataset") or {}
    # Модель вправе ответить без графика — если сочла, что рисовать нечего, это её
    # выбор, а не поломка контракта. Раньше здесь было четыре жёстких agent_ok, и такой
    # прогон давал четыре FAIL на законном поведении, то есть набор переставал быть
    # сигналом. Теперь отсутствие графика уводим в [ИИ?] — но не молча: печатаем
    # причину, которую сервер положил в datasetError шага answer.
    if not ds:
        _ans = [x for x in d.get("steps", []) if x.get("action") == "answer"]
        _why = (_ans[-1].get("datasetError") if _ans else None) or "модель не заполнила chart"
        AINOTE.append("контракт датасета: графика в этом прогоне не было — " + str(_why)[:90])
        print("  [ИИ?] контракт датасета: графика не было — " + str(_why)[:90])
    else:
        agent_ok("у колонок датасета проставлены типы", d,
                 bool(ds.get("cols")) and all(c.get("type") in ("text", "number", "date", "bool")
                                              for c in ds["cols"]),
                 str(ds.get("cols"))[:120])
        agent_ok("источник датасета указан", d,
                 (ds.get("source") or "").split(":")[0] in ("sql", "tool", "preload"),
                 str(ds.get("source")))
        # Точка 6 ревью р2: старые "rows<=200" и "rowCount>=len(rows)" не могли упасть — первая
        # недостижима (JSON-путь режет по SqlMaxRows=200, SQL-путь по потолку цикла 50 — оба
        # меньше 200 по построению), вторая тождественно истинна для sql-источника
        # (DatasetFromSqlResult кладёт rowCount=rows.Count). Разная семантика rowCount между
        # двумя сборщиками — известный и отложенный контроллером вопрос, здесь его не трогаем,
        # а проверяем реальный, различный по источнику инвариант согласованности с truncated.
        src = ds.get("source") or ""
        rows_ds = ds.get("rows") or []
        rc = ds.get("rowCount")
        if src.startswith("sql"):
            ok_rc = isinstance(rc, int) and rc == len(rows_ds)
            detail_rc = f"sql: rowCount={rc} rows={len(rows_ds)}"
        else:
            trunc = bool(ds.get("truncated"))
            ok_rc = isinstance(rc, int) and rc >= len(rows_ds) and trunc == (rc > len(rows_ds))
            detail_rc = f"{src}: rowCount={rc} rows={len(rows_ds)} truncated={trunc}"
        agent_ok("rowCount согласован с truncated и числом отданных строк", d, ok_rc, detail_rc)

# Ответ без визуализируемых данных — штатное состояние, а не ошибка (§9 спеки).
try:
    st, d = _req("/api/ai/sql", method="POST", body={"messages": [
        {"role": "user", "content": "Что ты умеешь?"}]}, timeout=180)
    # Точка 3 ревью р2: старое условие "bool(reply) and not error" тавтологично — agent_ok
    # уже уходит в [ИИ?] на d["error"], значит внутри условия "not error" истинно всегда,
    # а "bool(reply)" дублирует более раннюю проверку "ответ непустой". Настоящее
    # требование этого блока — датасет НЕ ОБЯЗАН быть, и его отсутствие не должно
    # выглядеть как сломанный контракт: отсутствующее поле, null и словарь — все три
    # штатные, а строка/число/список означали бы, что dataset вернул что-то не то.
    ds2 = d.get("dataset", None) if isinstance(d, dict) else None
    agent_ok("вопрос без данных: dataset отсутствует, null или объект — контракт не нарушен", d,
             ds2 is None or isinstance(ds2, dict),
             f"type={type(ds2).__name__} value={str(ds2)[:80]}")
except Exception as e:
    AINOTE.append(f"вопрос без данных: запрос не прошёл — {e}")
    print(f"  [ИИ?] вопрос без данных: запрос не прошёл — {e}")

# --- регрессия: цифра на графике обязана совпадать с цифрой, добытой харнессом (§ главное
# обещание фичи; находка ревью р2, п.7) ---
try:
    st, d = _req("/api/ai/sql", method="POST", body={"messages": [
        {"role": "user", "content":
         "Сколько заданий создано в 2023 году? Это не считает дашборд, нужен запрос."}]},
        timeout=180)
except Exception as e:
    AINOTE.append(f"регрессия датасет=факт: запрос не прошёл — {e}")
    print(f"  [ИИ?] регрессия датасет=факт: запрос не прошёл — {e}")
else:
    # Разбор — вне try/except (см. комментарий у блока «датасет для визуализации» выше):
    # это САМАЯ ценная проверка всей ветки — сверка датасета с preview SQL-шага, единственный
    # автоматический страж главного обещания продукта («цифра на графике обязана совпадать
    # с цифрой в тексте ответа»). Контрактная поломка здесь не должна маскироваться под
    # «модель недоступна» (находка финального ревью, п.8).
    if isinstance(d, dict) and d.get("error"):
        AINOTE.append(f"регрессия датасет=факт: LLM/инфраструктура недоступна — '{str(d['error'])[:60]}' (ожидаемо до пн)")
        print("  [ИИ?] регрессия датасет=факт: graceful-ошибка — в ПН ожидаем содержательный результат")
    else:
        sql_steps = [s for s in d.get("steps", []) if s.get("action") == "sql" and not s.get("error")]
        if not sql_steps:
            # Модель вправе закрыть вопрос иначе (инструментом, готовыми метриками) — это
            # не провал теста, а неприменимость сценария к этому конкретному прогону.
            AINOTE.append("регрессия датасет=факт: модель не сходила в sql в этом прогоне (вправе ответить иначе)")
            print("  [ИИ?] регрессия датасет=факт: sql-шага не было в этом прогоне")
        else:
            ds = d.get("dataset") or {}
            preview = sql_steps[-1].get("preview") or []
            ds_rows = ds.get("rows") or []
            agent_ok("датасет по SQL-вопросу собран из sql-источника", d,
                     (ds.get("source") or "") == "sql", str(ds.get("source")))
            # Инвариант, ради которого убран запасной путь (разбор 17.09.2026): график по
            # SQL показывается ТОЛЬКО когда модель сама его попросила через chart.from.
            # Раньше мы рисовали результат последнего запроса и без просьбы — и текст,
            # написанный по предзагруженным метрикам дашборда, оказывался рядом с
            # картинкой по постороннему запросу. Поле chart в шаге answer хранит то,
            # что попросила модель (null — не просила ничего).
            ans = [x for x in d.get("steps", []) if x.get("action") == "answer"]
            asked = ans[-1].get("chart") if ans else None
            agent_ok("график по SQL показан только по прямому указанию модели", d,
                     (ds.get("source") or "") != "sql" or asked == "sql",
                     "source=%s chart=%s" % (ds.get("source"), asked))
            if len(preview) == 0:
                # COUNT(*)-подобный запрос всегда возвращает одну строку, но модель могла
                # выбрать другой запрос (например, группировку, давшую пустой результат) —
                # сравнивать тогда нечего, и это не повод объявлять FAIL, но и молчать об
                # этом нельзя (требование п.7 — проверка не должна ничего утверждать молча).
                AINOTE.append("регрессия датасет=факт: sql-шаг вернул 0 строк, сравнивать нечего")
                print("  [ИИ?] регрессия датасет=факт: preview пуст — сравнение пропущено")
            else:
                # preview в протоколе — первые 5 строк ТОГО ЖЕ SqlRun, что попал в датасет
                # (Program.cs, ветка action=="sql": steps.Add(..., preview = rows.Take(5))).
                # Датасет мог урезать лишние колонки сверх DatasetMaxCols — сравниваем
                # только по фактической длине строки датасета, лишнего в preview не ждём.
                n = min(len(preview), len(ds_rows))
                match = n > 0 and all(
                    list(ds_rows[i]) == list(preview[i])[:len(ds_rows[i])] for i in range(n))
                agent_ok("значения в датасете совпадают со значениями SQL-шага (preview)", d,
                         match and n == len(preview),
                         f"preview={preview[:2]} dataset_rows={ds_rows[:2]}")

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
