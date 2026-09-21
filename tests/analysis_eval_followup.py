#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Deterministic verified selections for personal live follow-up (no HTTP)."""
from __future__ import annotations

import re

TERMINAL_CLARIFICATION_IDS = frozenset({"ambiguous_name", "unknown_employee"})
DATA_TOOLS = frozenset({"execute_sql", "dashboard_metric", "run_dashboard_metric"})


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
    tools = [
        step.get("tool")
        for step in (first_response.get("steps") or [])
        if isinstance(step, dict)
    ]
    if any(tool in DATA_TOOLS for tool in tools):
        return None, "data executed before clarification"

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
        mention = _bindable_mention(case, matches[0], name)
        if not mention:
            return None, f"expected employee {name!r} has no bindable question mention"
        selections.append({"mention": mention, "employeeId": parsed_id})

    return {"question": case["question"], "selections": selections}, None


def _bindable_mention(case: dict, candidate: dict, expected_name: str) -> str | None:
    raw = candidate.get("mention")
    if isinstance(raw, str) and raw.strip():
        return raw.strip()
    return _question_mention(case.get("question") or "", expected_name)


def _question_mention(question: str, expected_name: str) -> str | None:
    name_tokens = _tokens(expected_name)
    question_tokens = _tokens(question)
    width = len(name_tokens)
    if width == 0:
        return None
    matches = [
        " ".join(question_tokens[index : index + width])
        for index in range(len(question_tokens) - width + 1)
        if _window_matches_name(question_tokens[index : index + width], name_tokens)
    ]
    if len(matches) != 1:
        return None
    return matches[0]


def _window_matches_name(window: list[str], name_tokens: list[str]) -> bool:
    remaining = list(name_tokens)
    for question_token in window:
        match_index = next(
            (
                index
                for index, name in enumerate(remaining)
                if _inflects_to(question_token, name)
            ),
            None,
        )
        if match_index is None:
            return False
        remaining.pop(match_index)
    return not remaining


def _inflects_to(question_token: str, name_token: str) -> bool:
    left = _fold_token(question_token)
    right = _fold_token(name_token)
    if len(left) < 3 or len(right) < 3:
        return False
    shorter, longer = (left, right) if len(left) <= len(right) else (right, left)
    return longer.startswith(shorter)


def _fold_token(token: str) -> str:
    return token.strip(".,;:!?«»\"'()").casefold()


def _tokens(text: str) -> list[str]:
    return [token for token in re.split(r"\s+", text.strip()) if token]
