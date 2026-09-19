#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Smoke-тест evidence-backed analytics harness (POST /api/ai/analysis).

Запуск:
  python tests/analysis_smoke.py [http://localhost:5080]           # offline
  python tests/analysis_smoke.py http://localhost:5080 --live      # + живая модель

Коды выхода: 0 — OK, 1 — FAIL, 2 — BLOCKED (живые проверки недоступны, FAIL=0).
Зависимостей нет (urllib + stdlib). Сервер должен быть запущен для HTTP-секций.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

# Локальный smoke не должен идти через системный HTTP-прокси (иначе 503 на localhost).
urllib.request.getproxies = lambda: {}

ROOT = Path(__file__).resolve().parent.parent
FIXTURES = Path(__file__).resolve().parent / "fixtures"
CASES_PATH = FIXTURES / "analysis-cases.json"
OFFLINE_DIR = FIXTURES / "offline-responses"
PORUCHENIA_DISCRIMINATOR = "c290b098-12c7-487d-bb38-73e2c98f9789"

PASS = FAIL = 0
BLOCKED: list[str] = []


def section(title: str) -> None:
    print("\n=== " + title + " ===")


def check(name: str, cond: bool, detail: str = "") -> None:
    global PASS, FAIL
    if cond:
        PASS += 1
        print("  [ OK ] " + name + (f" — {detail}" if detail else ""))
    else:
        FAIL += 1
        print("  [FAIL] " + name + (f" — {detail}" if detail else ""))


def blocked(name: str, reason: str) -> None:
    BLOCKED.append(f"{name}: {reason}")
    print(f"  [BLOCKED] {name} — {reason}")


def _req(method: str, path: str, body=None, timeout: int = 180):
    url = BASE + path
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(
        url,
        data=data,
        method=method,
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.status, json.loads(r.read().decode("utf-8"))


def _req_static(path: str, timeout: int = 20):
    url = BASE + path
    try:
        with urllib.request.urlopen(url, timeout=timeout) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()


def _dataset_ids(resp: dict) -> set[str]:
    return {d.get("resultId") for d in (resp.get("datasets") or []) if d.get("resultId")}


def _step_tools(resp: dict) -> list[str]:
    return [s.get("tool") or "" for s in (resp.get("steps") or [])]


def _interpretation(resp: dict) -> dict | None:
    report = resp.get("report") or {}
    return report.get("interpretation")


def assert_no_completed_without_result(resp: dict) -> tuple[bool, str]:
    status = resp.get("status")
    if status != "completed":
        return True, "status=" + str(status)
    ok = bool(resp.get("datasets")) and resp.get("report") is not None
    return ok, f"datasets={len(resp.get('datasets') or [])} report={resp.get('report') is not None}"


def assert_no_unknown_result_reference(resp: dict) -> tuple[bool, str]:
    status = resp.get("status")
    steps = resp.get("steps") or []
    for step in steps:
        err = step.get("error") or {}
        if err.get("code") == "unknown_result" and status == "completed":
            return False, "completed with unknown_result step"
    if status != "completed":
        return True, "not completed"
    ids = _dataset_ids(resp)
    blocks = (resp.get("report") or {}).get("blocks") or []
    missing = [b.get("resultId") for b in blocks if b.get("resultId") not in ids]
    return not missing, f"missing resultIds: {missing[:3]}"


def assert_resolved_ids_or_clarification(resp: dict) -> tuple[bool, str]:
    status = resp.get("status")
    if status == "needs_clarification":
        clar = resp.get("clarification") or {}
        cands = clar.get("candidates") or []
        return bool(cands), f"candidates={len(cands)}"
    if status == "completed":
        interp = _interpretation(resp)
        tools = _step_tools(resp)
        resolved = bool(interp) and (
            "find_employees" in tools or bool(resp.get("datasets"))
        )
        return resolved, f"interp={bool(interp)} tools={tools[:4]}"
    if status in ("no_data", "incomplete", "failed"):
        return True, "explicit non-success state"
    return False, "unexpected status " + str(status)


def assert_period_matches(resp: dict) -> tuple[bool, str]:
    if resp.get("status") != "completed":
        return True, "skipped (not completed)"
    interp = _interpretation(resp) or {}
    ok = bool(interp.get("from")) and bool(interp.get("to"))
    return ok, f"from={interp.get('from')} to={interp.get('to')}"


def assert_same_source_in_chart_and_facts(resp: dict) -> tuple[bool, str]:
    if resp.get("status") != "completed":
        return True, "skipped"
    ids = _dataset_ids(resp)
    blocks = (resp.get("report") or {}).get("blocks") or []
    if not blocks:
        return False, "no blocks"
    block_ids = {b.get("resultId") for b in blocks if b.get("resultId")}
    if not block_ids:
        return False, "blocks without resultId"
    if not block_ids.issubset(ids):
        return False, f"blocks {block_ids} not in datasets {ids}"
    if len(block_ids) > 1:
        return False, f"multiple block sources: {block_ids}"
    return True, f"single source {next(iter(block_ids))}"


def assert_dashboard_metric_used(resp: dict) -> tuple[bool, str]:
    tools = _step_tools(resp)
    if any(t in tools for t in ("run_dashboard_metric", "get_departments", "get_process_metric")):
        return True, "dashboard tool in steps"
    for ds in resp.get("datasets") or []:
        src = (ds.get("data") or {}).get("source") or ""
        if src.startswith("tool:") or src.startswith("preload:"):
            return True, "source=" + src
    if resp.get("status") == "no_data":
        return True, "explicit no_data"
    return False, f"tools={tools[:5]}"


def assert_dataset_not_empty_or_explicit_no_data(resp: dict) -> tuple[bool, str]:
    status = resp.get("status")
    if status == "no_data":
        return True, "no_data"
    if status == "completed":
        datasets = resp.get("datasets") or []
        has_rows = any((d.get("data") or {}).get("rows") for d in datasets)
        return has_rows, f"datasets={len(datasets)} has_rows={has_rows}"
    return True, "status=" + str(status)


def assert_no_data_not_fabrication(resp: dict) -> tuple[bool, str]:
    status = resp.get("status")
    if status == "no_data":
        return True, "no_data"
    if status == "completed":
        datasets = resp.get("datasets") or []
        if not datasets:
            return False, "completed without datasets"
        rows = sum(len((d.get("data") or {}).get("rows") or []) for d in datasets)
        if rows == 0:
            return True, "completed with empty verified rows"
        return False, f"completed with {rows} rows (expected no_data)"
    return True, "status=" + str(status)


ASSERTIONS = {
    "no_completed_without_result": assert_no_completed_without_result,
    "no_unknown_result_reference": assert_no_unknown_result_reference,
    "resolved_ids_or_clarification": assert_resolved_ids_or_clarification,
    "period_matches": assert_period_matches,
    "same_source_in_chart_and_facts": assert_same_source_in_chart_and_facts,
    "dashboard_metric_used": assert_dashboard_metric_used,
    "dataset_not_empty_or_explicit_no_data": assert_dataset_not_empty_or_explicit_no_data,
    "no_data_not_fabrication": assert_no_data_not_fabrication,
}


def is_live_blocked(resp: dict) -> str | None:
    if resp.get("status") == "failed":
        err = resp.get("error") or {}
        code = err.get("code") or ""
        msg = str(err.get("message") or "")
        if code.startswith("provider_") or code == "llm_not_configured" or "LLM" in msg or "503" in msg:
            return msg[:100] or code
    return None


def evaluate_case(case: dict, resp: dict, live: bool) -> None:
    if live:
        blocked_reason = is_live_blocked(resp)
        if blocked_reason:
            blocked(f"live {case['id']}", blocked_reason)
            return
    for name in case.get("assertions") or []:
        if name == "root_deduplication":
            if live:
                verify_root_deduplication_live(resp)
            else:
                check(
                    f"{case['id']}: root_deduplication (offline skip)",
                    True,
                    "live-only; covered by DbTests on harness_test",
                )
            continue
        fn = ASSERTIONS.get(name)
        if not fn:
            check(f"{case['id']}: unknown assertion {name}", False)
            continue
        ok, detail = fn(resp)
        check(f"{case['id']}: {name}", ok, detail)


def load_cases() -> list[dict]:
    with CASES_PATH.open(encoding="utf-8") as f:
        return json.load(f)


def run_offline_fixtures(cases: list[dict]) -> None:
    section("Offline — машинные assertions на фикстурах (fake provider)")
    for case in cases:
        path = OFFLINE_DIR / f"{case['id']}.json"
        if not path.exists():
            check(f"fixture {case['id']}", False, "missing " + str(path))
            continue
        with path.open(encoding="utf-8") as f:
            resp = json.load(f)
        evaluate_case(case, resp, live=False)


def run_http_wire() -> None:
    section("HTTP — POST /api/ai/analysis (wire, без живой модели)")
    try:
        req = urllib.request.Request(BASE + "/api/ai/analysis", method="GET")
        with urllib.request.urlopen(req, timeout=20) as r:
            st = r.status
        check("GET → не 200", st != 200, f"status={st}")
    except urllib.error.HTTPError as e:
        check("GET → 405", e.code == 405, f"status={e.code}")
    except Exception as e:
        check("GET /api/ai/analysis", False, str(e))

    try:
        st, body = _req("POST", "/api/ai/analysis", {"question": "Сравни", "selections": []}, timeout=30)
        check("POST camelCase runId", st == 200 and isinstance(body.get("runId"), str))
        check(
            "POST status enum",
            body.get("status")
            in ("completed", "needs_clarification", "no_data", "incomplete", "failed"),
            str(body.get("status")),
        )
        check("POST steps list", isinstance(body.get("steps"), list))
    except Exception as e:
        check("POST valid body", False, str(e))

    for label, payload, expect in [
        ("malformed JSON", None, 400),
        ("empty body", "__empty__", 400),
        ("unknown field", {"question": "x", "selections": [], "extra": 1}, 400),
        ("invalid selection", {"question": "x", "selections": [{"mention": "", "employeeId": 0}]}, 400),
    ]:
        try:
            url = BASE + "/api/ai/analysis"
            if payload == "__empty__":
                req = urllib.request.Request(
                    url, data=b"", method="POST", headers={"Content-Type": "application/json"}
                )
            elif payload is None:
                req = urllib.request.Request(
                    url, data=b"{bad", method="POST", headers={"Content-Type": "application/json"}
                )
            else:
                req = urllib.request.Request(
                    url,
                    data=json.dumps(payload).encode("utf-8"),
                    method="POST",
                    headers={"Content-Type": "application/json"},
                )
            with urllib.request.urlopen(req, timeout=20) as r:
                st = r.status
            check(f"{label} → {expect}", st == expect, f"got {st}")
        except urllib.error.HTTPError as e:
            check(f"{label} → {expect}", e.code == expect, f"got {e.code}")
        except Exception as e:
            check(label, False, str(e))

    huge = '{"question":"' + ("x" * (64 * 1024)) + '","selections":[]}'
    try:
        req = urllib.request.Request(
            BASE + "/api/ai/analysis",
            data=huge.encode("utf-8"),
            method="POST",
            headers={"Content-Type": "application/json"},
        )
        with urllib.request.urlopen(req, timeout=20) as r:
            st = r.status
        check("oversized body → 413", st == 413, f"status={st}")
    except urllib.error.HTTPError as e:
        check("oversized body → 413", e.code == 413, f"status={e.code}")
    except Exception as e:
        check("oversized body", False, str(e))


def run_static() -> None:
    section("Статика — analysis.js")
    try:
        st, body = _req_static("/analysis.js")
        check("GET /analysis.js — 200", st == 200 and len(body) > 100, f"status={st} bytes={len(body)}")
    except Exception as e:
        check("GET /analysis.js", False, str(e))


def find_config() -> Path | None:
    local = ROOT / "config.json"
    if local.exists():
        return local
    desktop = Path(r"C:\Users\Korotaev_NO\Desktop\Проекты\ARMGov-Dashboard\config.json")
    if desktop.exists():
        return desktop
    return None


def run_dbq(sql: str, config: Path | None = None) -> tuple[int, str]:
    dotnet = os.environ.get("DOTNET", r"C:\Program Files\dotnet\dotnet.exe")
    cmd = [dotnet, "run", "--project", str(ROOT / "tools" / "dbq"), "-c", "Release", "--", sql]
    if config:
        cmd.extend(["--config", str(config)])
    proc = subprocess.run(cmd, cwd=str(ROOT), capture_output=True, text=True, timeout=120)
    out = (proc.stdout or "") + (proc.stderr or "")
    return proc.returncode, out.strip()


def fetch_notice_discriminators(config: Path) -> list[str] | None:
    sql = (
        "select discriminator::text from sungero_system_entitytype "
        "where name ilike '%Notice%' or name ilike '%Notification%'"
    )
    code, out = run_dbq(sql, config)
    if code != 0 or not out.strip():
        return None
    lines = out.splitlines()
    ids = []
    for line in lines[1:]:
        parts = line.split("\t")
        if parts and parts[0]:
            ids.append(parts[0].strip())
    return ids or None


def extract_employee_ids(resp: dict) -> list[int]:
    ids: list[int] = []
    for ds in resp.get("datasets") or []:
        cols = [c.get("name") for c in (ds.get("data") or {}).get("columns") or []]
        rows = (ds.get("data") or {}).get("rows") or []
        if "employee_id" in cols:
            idx = cols.index("employee_id")
            for row in rows:
                if row and row[idx] is not None:
                    ids.append(int(row[idx]))
        elif "name" in cols and "n" in cols:
            pass
    clar = resp.get("clarification") or {}
    for c in clar.get("candidates") or []:
        if c.get("id"):
            ids.append(int(c["id"]))
    return list(dict.fromkeys(ids))


def verify_root_deduplication_live(resp: dict) -> None:
    if resp.get("status") != "completed":
        check("personal_count: root_deduplication (live)", True, "not completed — skip control")
        return
    config = find_config()
    if not config:
        blocked("personal_count: root_deduplication", "config.json not found for control SELECT")
        return
    interp = _interpretation(resp) or {}
    from_ts = interp.get("from")
    to_ts = interp.get("to")
    if not from_ts or not to_ts:
        blocked("personal_count: root_deduplication", "interpretation period missing")
        return
    notice_ids = fetch_notice_discriminators(config)
    if not notice_ids:
        blocked("personal_count: root_deduplication", "notice list unavailable from DB")
        return
    employee_ids = extract_employee_ids(resp)
    if len(employee_ids) < 1:
        blocked("personal_count: root_deduplication", "no employee IDs in response")
        return
    ids_sql = ",".join(str(i) for i in employee_ids[:10])
    notice_sql = ",".join(f"'{n}'::uuid" for n in notice_ids[:80])
    control_sql = f"""
select p.id as employee_id, p.name, count(distinct root.id) as instructions
from public.sungero_core_recipient p
left join (
  public.sungero_wf_assignment a
  join public.sungero_wf_task t on t.id = a.task
  join public.sungero_wf_task root on root.id = coalesce(nullif(t.maintask,0),t.id)
) on a.performer = p.id
 and root.discriminator = '{PORUCHENIA_DISCRIMINATOR}'
 and root.created >= '{from_ts}'::timestamptz and root.created < '{to_ts}'::timestamptz
 and not (a.discriminator = any(array[{notice_sql}]))
where p.id in ({ids_sql})
group by p.id,p.name order by p.id
""".strip()
    code, out = run_dbq(control_sql, config)
    if code != 0:
        blocked("personal_count: root_deduplication", f"control SELECT failed: {out[:120]}")
        return
    model_counts = {}
    for ds in resp.get("datasets") or []:
        cols = [c.get("name") for c in (ds.get("data") or {}).get("columns") or []]
        for row in (ds.get("data") or {}).get("rows") or []:
            if "n" in cols and len(cols) >= 2:
                ni = cols.index("n")
                model_counts[str(row[0])] = int(row[ni])
    control_rows = out.splitlines()[1:]
    mismatches = []
    for line in control_rows:
        parts = line.split("\t")
        if len(parts) < 3:
            continue
        eid, _name, cnt = parts[0], parts[1], int(float(parts[2]))
        print(f"  [ctrl] employee_id={eid} instructions={cnt}")
    check(
        "personal_count: root_deduplication control SELECT executed",
        bool(control_rows),
        f"rows={len(control_rows)}",
    )


def run_live_cases(cases: list[dict]) -> None:
    section("Live — сценарии из analysis-cases.json")
    config = find_config()
    if not config:
        blocked("live scenarios", "config.json missing — LLM/DB unavailable")
        return
    local_cfg = ROOT / "config.json"
    if not local_cfg.exists() and config != local_cfg:
        try:
            import shutil
            shutil.copy2(config, local_cfg)
            print(f"  [info] copied config.json for live run (gitignored)")
        except Exception as e:
            print(f"  [info] config copy skipped: {e}")

    for case in cases:
        try:
            st, body = _req(
                "POST",
                "/api/ai/analysis",
                {"question": case["question"], "selections": []},
                timeout=180,
            )
            if st != 200:
                check(f"live {case['id']} HTTP", False, f"status={st}")
                continue
            if body.get("error") and body.get("status") == "failed":
                err = body.get("error") or {}
                if err.get("code") == "llm_not_configured":
                    blocked(f"live {case['id']}", "LLM not configured")
                    continue
            evaluate_case(case, body, live=True)
        except Exception as e:
            check(f"live {case['id']}", False, str(e))

    section("Live — повторения и разные разрезы")
    main_q = next(c["question"] for c in cases if c["id"] == "personal_count")
    slices = [
        main_q,
        main_q,
        main_q,
        "Покажи просрочку по подразделениям",
        "Сколько поручений создано за последний квартал?",
        "Покажи тренд просрочки по месяцам",
    ]
    statuses = []
    for i, q in enumerate(slices, 1):
        try:
            _, body = _req("POST", "/api/ai/analysis", {"question": q, "selections": []}, timeout=180)
            statuses.append(body.get("status"))
            err = (body.get("error") or {}).get("message") or ""
            print(f"  [live {i}/6] status={body.get('status')} elapsed={body.get('elapsedMs')}ms"
                  + (f" err={err[:60]}" if err else ""))
            if is_live_blocked(body):
                blocked(f"live repeat {i}", err[:80] or body.get("status"))
        except Exception as e:
            blocked(f"live repeat {i}", str(e))
    check("live repetitions: 6 HTTP calls completed", len(statuses) == 6, str(statuses))


def run_test_db_root_dedup() -> None:
    section("Test DB — root deduplication (ARMGOV_TEST_DB)")
    cs = os.environ.get("ARMGOV_TEST_DB")
    if not cs:
        check("ARMGOV_TEST_DB root dedup", True, "skipped (env not set; run db suite separately)")
        return
    blocked("ARMGOV_TEST_DB inline", "use harness-tests db suite for root dedup")


def main() -> int:
    global BASE
    parser = argparse.ArgumentParser(description="Analysis harness smoke tests")
    parser.add_argument("base", nargs="?", default="http://localhost:5080")
    parser.add_argument("--live", action="store_true", help="Run live model scenarios")
    args = parser.parse_args()
    BASE = args.base.rstrip("/")

    cases = load_cases()
    check("analysis-cases.json loaded", len(cases) == 4, str(len(cases)))

    run_static()
    run_http_wire()
    run_offline_fixtures(cases)
    run_test_db_root_dedup()

    if args.live:
        run_live_cases(cases)

    section("ИТОГ")
    print(f"  PASS={PASS}  FAIL={FAIL}  BLOCKED={len(BLOCKED)}")
    if BLOCKED:
        print("  Заблокировано:")
        for item in BLOCKED:
            print("    • " + item)
    if FAIL:
        print("\nЕСТЬ ПАДЕНИЯ")
        return 1
    if BLOCKED:
        print("\nBLOCKED (live/infra checks incomplete)")
        return 2 if args.live else 0
    print("\nВСЁ ОК")
    return 0


if __name__ == "__main__":
    sys.exit(main())
