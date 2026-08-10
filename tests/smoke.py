#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Функциональный smoke-тест ARMGov Dashboard (описывает ожидаемую функциональность).
Запуск:  python tests/smoke.py [http://localhost:5080]
Зависимостей нет (urllib из stdlib). Сервер должен быть запущен.

Секция ИИ (LLM Ario) проверяется отдельно: пока модель недоступна — фиксируем
graceful-ошибку; в понедельник, когда модель поднимут, ожидаем содержательный ответ.
"""
import sys, json, urllib.request, urllib.error
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

# ---------------- ИТОГ ----------------
section("ИТОГ")
print(f"  Проверок данных/UI: PASS={PASS}  FAIL={FAIL}")
if AINOTE:
    print("  ИИ (отложено на понедельник):")
    for n in AINOTE: print("    • " + n)
print("\n" + ("ВСЁ ОК (данные/UI)" if FAIL == 0 else f"ЕСТЬ ПАДЕНИЯ: {FAIL}"))
sys.exit(1 if FAIL else 0)
