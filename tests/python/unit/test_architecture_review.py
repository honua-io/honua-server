# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

from __future__ import annotations

import importlib.util
from pathlib import Path


SCRIPT_PATH = Path(__file__).resolve().parents[3] / "scripts" / "architecture-review.py"
SPEC = importlib.util.spec_from_file_location("architecture_review", SCRIPT_PATH)
assert SPEC is not None
assert SPEC.loader is not None
ARCHITECTURE_REVIEW = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ARCHITECTURE_REVIEW)


def test_extract_acceptance_criteria_handles_escaped_newlines() -> None:
    issue_body = (
        "## Why\\n"
        "Operational sample work stays separate from automated compatibility proof.\\n\\n"
        "## Acceptance Criteria\\n"
        "- Reviewers can launch the sample quickly\\n"
        "- The sample shows both healthy and warning compatibility signals\\n\\n"
        "## Notes\\n"
        "- Stored with escaped newlines by the issue authoring flow.\\n"
    )

    criteria = ARCHITECTURE_REVIEW.extract_acceptance_criteria(issue_body)

    assert criteria == [
        "Reviewers can launch the sample quickly",
        "The sample shows both healthy and warning compatibility signals",
    ]


def test_extract_acceptance_criteria_accepts_legacy_acceptance_heading() -> None:
    issue_body = (
        "## Scope\\n"
        "Implement workspace retention and cleanup semantics.\\n\\n"
        "## Acceptance\\n"
        "- Operator workflows can rely on durable and temporary artifacts\\n"
        "- Retention behavior is deterministic and testable\\n"
        "- Publishing and packaging tickets are unblocked\\n"
    )

    criteria = ARCHITECTURE_REVIEW.extract_acceptance_criteria(issue_body)

    assert criteria == [
        "Operator workflows can rely on durable and temporary artifacts",
        "Retention behavior is deterministic and testable",
        "Publishing and packaging tickets are unblocked",
    ]
