#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Offline tests for verified personal follow-up selections (no HTTP)."""
from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(Path(__file__).resolve().parent))

from analysis_eval_followup import follow_up_request  # noqa: E402
from analysis_smoke import (  # noqa: E402
    assert_required_entity_resolution,
    live_safety_ok,
    sanitized_live_record,
    validate_sanitized_record,
)

FIXTURES = Path(__file__).resolve().parent / "fixtures" / "analysis-workflow-cases.json"
PERSONAL_IDS = {
    "compare_two_people_12m",
    "one_person_6m",
    "completed_two_people",
    "employee_overdue_3m",
}
TERMINAL_CLARIFICATION_IDS = {"ambiguous_name", "unknown_employee"}


def _personal_case(**overrides):
    case = {
        "id": "compare_two_people_12m",
        "question": "Сравни количество поручений у Ивана Иванова и Босова Александра за 12 месяцев",
        "expectedStatuses": ["completed", "needs_clarification", "no_data"],
        "requiresEntityResolution": True,
        "expectedEmployeeNames": ["Иванов Иван", "Босов Александр"],
    }
    case.update(overrides)
    return case


def _clarification(candidates, *, data_tool=None):
    steps = [{"tool": "resolve_entities", "status": "ok", "elapsedMs": 10, "resultId": None, "error": None}]
    if data_tool:
        steps.append({"tool": data_tool, "status": "ok", "elapsedMs": 20, "resultId": "r1", "error": None})
    return {
        "status": "needs_clarification",
        "elapsedMs": 100,
        "steps": steps,
        "datasets": [],
        "clarification": {"question": "Уточните сотрудника.", "candidates": candidates},
        "error": None,
    }


class FollowUpRequestTests(unittest.TestCase):
    def test_unique_match_builds_second_request_from_candidates_only(self):
        case = _personal_case()
        first = _clarification([
            {"id": 303, "name": "Иванов Петр", "department": "ДФ"},
            {"id": 101, "name": "Иванов Иван", "department": "ДИТ"},
            {"id": 202, "name": "Босов Александр", "department": "АУП"},
        ])

        request, error = follow_up_request(case, first)

        self.assertIsNone(error)
        self.assertIsNotNone(request)
        self.assertEqual(request["question"], case["question"])
        self.assertEqual(
            request["selections"],
            [
                {"mention": "Ивана Иванова", "employeeId": 101},
                {"mention": "Босова Александра", "employeeId": 202},
            ],
        )
        for selection, expected_name in zip(
            request["selections"], case["expectedEmployeeNames"], strict=True
        ):
            self.assertEqual(set(selection), {"mention", "employeeId"})
            self.assertNotEqual(selection["mention"], expected_name)
            self.assertIn(selection["mention"], case["question"])

    def test_ambiguous_full_name_fails_without_second_request(self):
        case = _personal_case()
        first = _clarification([
            {"id": 101, "name": "Иванов Иван", "department": "ДИТ"},
            {"id": 109, "name": "Иванов Иван", "department": "Другое"},
            {"id": 202, "name": "Босов Александр", "department": "АУП"},
        ])

        request, error = follow_up_request(case, first)

        self.assertIsNone(request)
        self.assertIsNotNone(error)
        self.assertIn("Иванов Иван", error)

    def test_missing_exact_name_fails_without_second_request(self):
        case = _personal_case()
        first = _clarification([
            {"id": 101, "name": "Иванов Петр", "department": "ДИТ"},
            {"id": 202, "name": "Босов Александр", "department": "АУП"},
        ])

        request, error = follow_up_request(case, first)

        self.assertIsNone(request)
        self.assertIsNotNone(error)
        self.assertIn("Иванов Иван", error)

    def test_first_response_data_step_fails_safety_without_second_request(self):
        case = _personal_case()
        for data_tool in ("execute_sql", "dashboard_metric", "run_dashboard_metric"):
            with self.subTest(data_tool=data_tool):
                first = _clarification(
                    [
                        {"id": 101, "name": "Иванов Иван", "department": "ДИТ"},
                        {"id": 202, "name": "Босов Александр", "department": "АУП"},
                    ],
                    data_tool=data_tool,
                )

                request, error = follow_up_request(case, first)

                self.assertIsNone(request)
                self.assertIsNotNone(error)
                self.assertFalse(live_safety_ok(case, first))

    def test_terminal_ambiguous_name_does_not_follow_up_even_with_candidates(self):
        case = {
            "id": "ambiguous_name",
            "question": "Покажи поручения Иванова за год",
            "expectedStatuses": ["needs_clarification"],
            "requiresEntityResolution": True,
            "requiresClarificationCandidates": True,
            "expectedEmployeeNames": ["Иванов Иван"],
        }
        first = _clarification([
            {"id": 101, "name": "Иванов Иван", "department": "ДИТ"},
            {"id": 109, "name": "Иванов Петр", "department": "ДФ"},
        ])

        request, error = follow_up_request(case, first)

        self.assertIsNone(request)
        self.assertIsNone(error)
        ok, detail = assert_required_entity_resolution(case, first)
        self.assertTrue(ok, detail)

    def test_terminal_unknown_employee_does_not_follow_up(self):
        case = {
            "id": "unknown_employee",
            "question": "Покажи поручения Несуществующего Сотрудника за год",
            "expectedStatuses": ["needs_clarification"],
            "requiresEntityResolution": True,
        }
        first = _clarification([{"id": 1, "name": "Иванов Иван", "department": "ДИТ"}])

        request, error = follow_up_request(case, first)

        self.assertIsNone(request)
        self.assertIsNone(error)
        ok, detail = assert_required_entity_resolution(case, first)
        self.assertTrue(ok, detail)

    def test_terminal_clarification_rejects_data_before_clarify(self):
        case = {
            "id": "ambiguous_name",
            "question": "Покажи поручения Иванова за год",
            "expectedStatuses": ["needs_clarification"],
            "requiresEntityResolution": True,
            "requiresClarificationCandidates": True,
        }
        first = _clarification(
            [{"id": 101, "name": "Иванов Иван", "department": "ДИТ"}],
            data_tool="execute_sql",
        )

        request, error = follow_up_request(case, first)

        self.assertIsNone(request)
        self.assertIsNone(error)
        ok, detail = assert_required_entity_resolution(case, first)
        self.assertFalse(ok, "data before clarification must fail")
        self.assertIn("execute_sql", detail)

    def test_completed_first_response_does_not_follow_up(self):
        case = _personal_case()
        first = {
            "status": "completed",
            "steps": [{"tool": "execute_sql", "status": "ok"}],
            "clarification": None,
        }

        request, error = follow_up_request(case, first)

        self.assertIsNone(request)
        self.assertIsNone(error)

    def test_sanitizer_allowlist_rejects_employee_fields(self):
        record = sanitized_live_record("compare_two_people_12m", {
            "status": "needs_clarification",
            "elapsedMs": 50,
            "steps": [],
            "datasets": [],
            "clarification": {
                "candidates": [{"id": 101, "name": "Иванов Иван", "department": "ДИТ"}],
            },
        })
        validate_sanitized_record(record)
        self.assertEqual(
            set(record),
            {"caseId", "status", "elapsedMs", "steps", "resultIds", "effectiveSql"},
        )
        leaked = json.dumps(record, ensure_ascii=False)
        self.assertNotIn("Иванов Иван", leaked)
        self.assertNotIn("101", leaked)

        poisoned = dict(record)
        poisoned["employeeId"] = 101
        poisoned["name"] = "Иванов Иван"
        with self.assertRaises(ValueError):
            validate_sanitized_record(poisoned)

    def test_personal_fixtures_declare_expected_full_names(self):
        cases = json.loads(FIXTURES.read_text(encoding="utf-8"))
        by_id = {case["id"]: case for case in cases}
        for case_id in PERSONAL_IDS:
            names = by_id[case_id].get("expectedEmployeeNames") or []
            self.assertTrue(names, f"{case_id} must declare expectedEmployeeNames")
            self.assertEqual(len(names), len(set(names)))
        for case_id in TERMINAL_CLARIFICATION_IDS:
            self.assertFalse(
                by_id[case_id].get("expectedEmployeeNames"),
                f"{case_id} must remain a terminal clarification case",
            )


if __name__ == "__main__":
    unittest.main(verbosity=2)
