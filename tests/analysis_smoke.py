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
import datetime as dt
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

from analysis_eval_followup import follow_up_request

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

# Локальный smoke не должен идти через системный HTTP-прокси (иначе 503 на localhost).
urllib.request.getproxies = lambda: {}

ROOT = Path(__file__).resolve().parent.parent
FIXTURES = Path(__file__).resolve().parent / "fixtures"
CASES_PATH = FIXTURES / "analysis-cases.json"
WORKFLOW_CASES_PATH = FIXTURES / "analysis-workflow-cases.json"
OFFLINE_DIR = FIXTURES / "offline-responses"
ARTIFACTS_DIR = ROOT / "artifacts" / "analytics-eval"
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
    if any(t in tools for t in ("dashboard_metric", "run_dashboard_metric", "get_departments", "get_process_metric")):
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


def live_unavailable_code(resp: dict) -> str | None:
    if resp.get("status") == "failed":
        err = resp.get("error") or {}
        code = str(err.get("code") or "")
        if code in {
            "llm_not_configured",
            "provider_unavailable",
            "provider_timeout",
            "database_unavailable",
            "db_unavailable",
            "rx_unavailable",
        }:
            return code
    return None


def _parse_timestamp(value: str | None) -> dt.datetime | None:
    if not value:
        return None
    try:
        return dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


def assert_expected_live_status(case: dict, resp: dict) -> tuple[bool, str]:
    status = resp.get("status")
    expected = case.get("expectedStatuses") or []
    code = (resp.get("error") or {}).get("code")
    return status in expected, f"status={status} code={code} expected={expected}"


def assert_required_entity_resolution(case: dict, resp: dict) -> tuple[bool, str]:
    if not case.get("requiresEntityResolution"):
        return True, "not required"
    tools = _step_tools(resp)
    resolved = "resolve_entities" in tools or "find_employees" in tools
    if not resolved:
        return False, f"resolution step absent: {tools}"
    if resp.get("status") == "needs_clarification":
        data_tools = {"execute_sql", "dashboard_metric", "run_dashboard_metric"}
        unsafe = [tool for tool in tools if tool in data_tools]
        if unsafe:
            return False, f"data executed before clarification: {unsafe}"
    if case.get("requiresClarificationCandidates"):
        candidates = (resp.get("clarification") or {}).get("candidates") or []
        return bool(candidates), f"candidates={len(candidates)}"
    return True, "resolution step present"


def assert_live_period(case: dict, resp: dict) -> tuple[bool, str]:
    expected = case.get("period")
    if not expected or resp.get("status") != "completed":
        return True, "not applicable to terminal state"
    interpretation = _interpretation(resp) or {}
    start = _parse_timestamp(interpretation.get("from"))
    end = _parse_timestamp(interpretation.get("to"))
    if not start or not end or end <= start:
        return False, "completed response has no valid bound period"
    duration_days = (end - start).total_seconds() / 86400
    if "months" in expected:
        months = int(expected["months"])
        ok = months * 28 <= duration_days <= months * 32 + 2
        return ok, f"duration_days={duration_days:.2f} expected_months={months}"
    if "days" in expected:
        days = int(expected["days"])
        ok = days - 1 <= duration_days <= days + 2
        return ok, f"duration_days={duration_days:.2f} expected_days={days}"
    expected_from = dt.date.fromisoformat(expected["from"])
    expected_to = dt.date.fromisoformat(expected["to"])
    ok = start.date() == expected_from and end.date() in {
        expected_to,
        expected_to + dt.timedelta(days=1),
    }
    return ok, f"from={start.date()} to={end.date()}"


def assert_live_single_source(case: dict, resp: dict) -> tuple[bool, str]:
    if not case.get("singleSource") or resp.get("status") != "completed":
        return True, "not applicable"
    ids = _dataset_ids(resp)
    blocks = (resp.get("report") or {}).get("blocks") or []
    block_ids = {block.get("resultId") for block in blocks if block.get("resultId")}
    ok = len(ids) == 1 and block_ids == ids and bool((resp.get("report") or {}).get("facts"))
    return ok, f"datasets={len(ids)} blockSources={len(block_ids)} facts={bool((resp.get('report') or {}).get('facts'))}"


def assert_live_no_fabrication(case: dict, resp: dict) -> tuple[bool, str]:
    if not case.get("noFabrication"):
        return True, "not required"
    datasets = resp.get("datasets") or []
    report = resp.get("report")
    rows = sum(len((dataset.get("data") or {}).get("rows") or []) for dataset in datasets)
    ok = resp.get("status") != "completed" and rows == 0 and report is None
    return ok, f"status={resp.get('status')} rows={rows} report={report is not None}"


def live_safety_ok(case: dict, resp: dict) -> bool:
    tools = _step_tools(resp)
    if resp.get("status") == "needs_clarification" and any(
        tool in {"execute_sql", "dashboard_metric", "run_dashboard_metric"} for tool in tools
    ):
        return False
    unknown_ok, _ = assert_no_unknown_result_reference(resp)
    fabrication_ok, _ = assert_live_no_fabrication(case, resp)
    return unknown_ok and fabrication_ok


def evaluate_workflow_case(case: dict, resp: dict, run_label: str) -> bool | None:
    unavailable = live_unavailable_code(resp)
    if unavailable:
        blocked(run_label, unavailable)
        return None

    assertions = [
        ("status", assert_expected_live_status),
        ("entity_resolution", assert_required_entity_resolution),
        ("period_binding", assert_live_period),
        ("single_source_identity", assert_live_single_source),
        ("no_fabrication", assert_live_no_fabrication),
    ]
    passed = True
    for name, assertion in assertions:
        ok, detail = assertion(case, resp)
        check(f"{run_label}: {name}", ok, detail)
        passed = passed and ok
    if case.get("noUnknownResultReferences"):
        ok, detail = assert_no_unknown_result_reference(resp)
        check(f"{run_label}: no_unknown_result_reference", ok, detail)
        passed = passed and ok
    return passed


def sanitized_live_record(case_id: str, resp: dict) -> dict:
    steps = []
    for step in resp.get("steps") or []:
        error = step.get("error") or {}
        steps.append({
            "name": step.get("tool"),
            "status": step.get("status"),
            "elapsedMs": step.get("elapsedMs"),
            "errorCode": error.get("code"),
            "resultId": step.get("resultId"),
        })
    top_error_code = (resp.get("error") or {}).get("code")
    if top_error_code and not any(step.get("errorCode") == top_error_code for step in steps):
        steps.append({
            "name": "request",
            "status": "error",
            "elapsedMs": resp.get("elapsedMs"),
            "errorCode": top_error_code,
            "resultId": None,
        })
    datasets = resp.get("datasets") or []
    result_ids = sorted({
        dataset.get("resultId") for dataset in datasets if dataset.get("resultId")
    })
    effective_sql = [
        (dataset.get("data") or {}).get("effectiveSql")
        for dataset in datasets
        if (dataset.get("data") or {}).get("effectiveSql")
    ]
    return {
        "caseId": case_id,
        "status": resp.get("status"),
        "elapsedMs": resp.get("elapsedMs"),
        "steps": steps,
        "resultIds": result_ids,
        "effectiveSql": effective_sql,
    }


def validate_sanitized_record(record: dict) -> None:
    allowed = {"caseId", "status", "elapsedMs", "steps", "resultIds", "effectiveSql"}
    if set(record) != allowed:
        raise ValueError("live artifact contains unexpected top-level fields")
    step_allowed = {"name", "status", "elapsedMs", "errorCode", "resultId"}
    if any(set(step) != step_allowed for step in record["steps"]):
        raise ValueError("live artifact contains unexpected step fields")


def evaluate_case(case: dict, resp: dict, live: bool) -> None:
    if live:
        blocked_reason = live_unavailable_code(resp)
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


def load_workflow_cases() -> list[dict]:
    with WORKFLOW_CASES_PATH.open(encoding="utf-8") as f:
        cases = json.load(f)
    if len(cases) != 10 or len({case.get("id") for case in cases}) != 10:
        raise ValueError("analysis-workflow-cases.json must contain ten unique cases")
    return cases


def run_live_cases(cases: list[dict], engine: str, repetitions: int, artifact: Path | None) -> dict:
    section(f"Live — {engine}, {len(cases)} cases × {repetitions}")
    if not find_config():
        blocked("live scenarios", "llm_not_configured")
        return {"total": 0, "passed": 0, "unavailable": len(cases) * repetitions}

    records: list[dict] = []
    total = passed = unavailable = safety_failures = 0
    personal_source_passes = 0
    successful_workflow_runs = 0
    repair_bound_passes = 0
    for iteration in range(1, repetitions + 1):
        for case in cases:
            total += 1
            label = f"{engine} {case['id']} {iteration}/{repetitions}"
            try:
                status, body = _req(
                    "POST",
                    "/api/ai/analysis",
                    {"question": case["question"], "selections": []},
                    timeout=180,
                )
                if status != 200:
                    check(f"{label}: HTTP", False, f"status={status}")
                    body = {"status": "http_error", "elapsedMs": None, "steps": [], "datasets": []}
                    result = False
                else:
                    follow_body, follow_error = follow_up_request(case, body)
                    if follow_error:
                        check(f"{label}: verified_selections", False, follow_error)
                        result = False
                    elif follow_body:
                        status, body = _req(
                            "POST",
                            "/api/ai/analysis",
                            follow_body,
                            timeout=180,
                        )
                        if status != 200:
                            check(f"{label}: follow-up HTTP", False, f"status={status}")
                            body = {
                                "status": "http_error",
                                "elapsedMs": None,
                                "steps": [],
                                "datasets": [],
                            }
                            result = False
                        else:
                            result = evaluate_workflow_case(case, body, label)
                    else:
                        result = evaluate_workflow_case(case, body, label)
                if result is None:
                    unavailable += 1
                elif result:
                    passed += 1
                if result is not None and not live_safety_ok(case, body):
                    safety_failures += 1
                if case["id"] == "compare_two_people_12m" and result and body.get("status") == "completed":
                    source_ok, _ = assert_live_single_source(case, body)
                    period_ok, _ = assert_live_period(case, body)
                    if source_ok and period_ok:
                        personal_source_passes += 1
                if engine == "workflow" and body.get("status") == "completed":
                    successful_workflow_runs += 1
                    execute_steps = [
                        step for step in body.get("steps") or []
                        if step.get("tool") == "execute_sql"
                    ]
                    if len(execute_steps) <= 2:
                        repair_bound_passes += 1
                record = sanitized_live_record(case["id"], body)
                validate_sanitized_record(record)
                records.append(record)
                print(
                    f"  [live] case={case['id']} run={iteration}/{repetitions} "
                    f"status={body.get('status')} elapsedMs={body.get('elapsedMs')}"
                )
            except Exception as exc:
                check(f"{label}: request", False, type(exc).__name__)
                record = sanitized_live_record(case["id"], {
                    "status": "request_error",
                    "elapsedMs": None,
                    "steps": [],
                    "datasets": [],
                })
                validate_sanitized_record(record)
                records.append(record)

    output = artifact
    if output is None:
        stamp = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        output = ARTIFACTS_DIR / f"{engine}-{stamp}.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(records, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    summary = {
        "engine": engine,
        "total": total,
        "passed": passed,
        "unavailable": unavailable,
        "semanticFailures": total - passed - unavailable,
        "safetyFailures": safety_failures,
        "successfulWorkflowRuns": successful_workflow_runs,
        "zeroOrOneRepair": repair_bound_passes,
        "personalSourceIdentityPasses": personal_source_passes,
        "artifact": str(output),
    }
    print("LIVE_SUMMARY_JSON=" + json.dumps(summary, ensure_ascii=False, separators=(",", ":")))
    return summary


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
    parser.add_argument("--engine", choices=("legacy", "workflow"), default="workflow")
    parser.add_argument("--repetitions", type=int, default=3)
    parser.add_argument("--artifact", type=Path)
    args = parser.parse_args()
    if args.repetitions < 1:
        parser.error("--repetitions must be at least 1")
    BASE = args.base.rstrip("/")

    cases = load_cases()
    check("analysis-cases.json loaded", len(cases) == 4, str(len(cases)))

    run_static()
    run_http_wire()
    run_offline_fixtures(cases)
    run_test_db_root_dedup()

    if args.live:
        try:
            workflow_cases = load_workflow_cases()
            check("analysis-workflow-cases.json loaded", len(workflow_cases) == 10, str(len(workflow_cases)))
            run_live_cases(workflow_cases, args.engine, args.repetitions, args.artifact)
        except (OSError, ValueError, json.JSONDecodeError) as exc:
            check("workflow evaluation fixtures", False, type(exc).__name__)

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
