# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

from __future__ import annotations

import importlib.util
import subprocess
from pathlib import Path


SCRIPT_PATH = (
    Path(__file__).resolve().parents[3] / "scripts" / "ci" / "local-architecture-review.py"
)
SPEC = importlib.util.spec_from_file_location("local_architecture_review", SCRIPT_PATH)
assert SPEC is not None
assert SPEC.loader is not None
LOCAL_ARCHITECTURE_REVIEW = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(LOCAL_ARCHITECTURE_REVIEW)


def test_resolve_base_ref_prefers_origin_tracking_branch(monkeypatch) -> None:
    def fake_run(args, capture_output, text, check):
        ref = args[-1]
        if ref == "origin/trunk^{commit}":
            return subprocess.CompletedProcess(args, 0, "", "")
        if ref == "trunk^{commit}":
            raise subprocess.CalledProcessError(1, args)
        raise AssertionError(f"Unexpected git command: {args}")

    monkeypatch.setattr(LOCAL_ARCHITECTURE_REVIEW.subprocess, "run", fake_run)

    assert LOCAL_ARCHITECTURE_REVIEW.resolve_base_ref("trunk") == "origin/trunk"


def test_local_architecture_review_uses_resolved_base_ref(monkeypatch) -> None:
    monkeypatch.setattr(
        LOCAL_ARCHITECTURE_REVIEW,
        "resolve_base_ref",
        lambda base_ref="trunk": "origin/trunk",
    )

    changed_base_refs: list[str] = []
    diff_base_refs: list[str] = []

    def fake_get_changed_files(base_ref: str = "trunk"):
        changed_base_refs.append(base_ref)
        return ["src/Honua.Server/Features/Infrastructure/Events/RedisLeaseCoordinator.cs"]

    def fake_get_file_content_and_diff(file_path: str, base_ref: str = "trunk"):
        diff_base_refs.append(base_ref)
        return {"content": "internal class RedisLeaseCoordinator {}", "diff": ""}

    monkeypatch.setattr(
        LOCAL_ARCHITECTURE_REVIEW, "get_changed_files", fake_get_changed_files
    )
    monkeypatch.setattr(
        LOCAL_ARCHITECTURE_REVIEW,
        "get_file_content_and_diff",
        fake_get_file_content_and_diff,
    )

    results = LOCAL_ARCHITECTURE_REVIEW.local_architecture_review()

    assert changed_base_refs == ["origin/trunk"]
    assert diff_base_refs == ["origin/trunk"]
    assert results["assessment"] == "APPROVED"
    assert results["files_reviewed"] == 1
