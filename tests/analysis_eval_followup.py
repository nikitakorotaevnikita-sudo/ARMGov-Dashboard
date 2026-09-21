#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Deterministic verified selections for personal live follow-up (no HTTP)."""
from __future__ import annotations

TERMINAL_CLARIFICATION_IDS = frozenset({"ambiguous_name", "unknown_employee"})


def follow_up_request(case: dict, first_response: dict) -> tuple[dict | None, str | None]:
    """Return (second_request, error) from a first analysis response.

    (body, None) — resubmit this request with verified selections.
    (None, error) — semantic failure; do not send a second request.
    (None, None) — no follow-up; evaluate the first response.
    """
    if not case or not first_response:
        return None, None
    if case.get("id") in TERMINAL_CLARIFICATION_IDS:
        return None, None
    expected_statuses = case.get("expectedStatuses") or []
    if "completed" not in expected_statuses:
        return None, None
    expected_names = case.get("expectedEmployeeNames") or []
    if not expected_names:
        return None, None
    if first_response.get("status") != "needs_clarification":
        return None, None

    candidates = ((first_response.get("clarification") or {}).get("candidates")) or []
    selections: list[dict] = []
    for name in expected_names:
        matches = [
            candidate
            for candidate in candidates
            if isinstance(candidate, dict) and candidate.get("name") == name
        ]
        if len(matches) == 0:
            return None, f"expected employee {name!r} has no exact candidate match"
        if len(matches) > 1:
            return None, (
                f"expected employee {name!r} has {len(matches)} exact candidate matches"
            )
        employee_id = matches[0].get("id")
        try:
            parsed_id = int(employee_id)
        except (TypeError, ValueError):
            return None, f"expected employee {name!r} has invalid candidate id"
        if parsed_id <= 0:
            return None, f"expected employee {name!r} has invalid candidate id"
        selections.append({"mention": name, "employeeId": parsed_id})

    return {"question": case["question"], "selections": selections}, None
