#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
from pathlib import Path


SCRIPT = Path(__file__).with_name("classify-pr-gate-impact.py")
SPEC = importlib.util.spec_from_file_location("pr_gate_impact", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

BASE = "a" * 40
HEAD = "b" * 40


def payload(files: list[dict], **overrides: object) -> dict:
    value = {
        "repository": "honua-io/honua-server",
        "pull_request": 3231,
        "base_sha": BASE,
        "head_sha": HEAD,
        "changed_files": len(files),
        "files": files,
    }
    value.update(overrides)
    return value


docs = [
    {"filename": "docs/internal/contributor/adr/0074-evidence-driven-ci-pipeline.md", "status": "modified"},
    {"filename": "docs/internal/ci/gate-model.md", "status": "added"},
]
result = MODULE.classify(payload(docs))
assert result["mode"] == "docs-only"
assert result["rollout"] == "observe"
assert result["reason"] == "internal-markdown-only"
assert result["authoritative_gate"] == "full"
assert result["changed_file_count"] == 2
assert result["repository"] == "honua-io/honua-server"
assert result["pull_request"] == 3231
assert result["base_sha"] == BASE
assert result["head_sha"] == HEAD
assert len(result["files_sha256"]) == 64
assert result["files_sha256"] == MODULE.classify(payload(list(reversed(docs))))["files_sha256"]

full_cases = [
    payload([{"filename": "README.md", "status": "modified"}]),
    payload([{"filename": "docs/reference/overview.md", "status": "modified"}]),
    payload([{"filename": "docs/internal/ci/receipt.json", "status": "modified"}]),
    payload([{"filename": ".github/workflows/pr-gate.yml", "status": "modified"}]),
    payload([{"filename": "docs/internal/ci/old.md", "status": "removed"}]),
    payload([{"filename": "docs/internal/ci/new.md", "status": "renamed", "previous_filename": "docs/internal/ci/old.md"}]),
    payload([{"filename": "docs/internal/../escape.md", "status": "modified"}]),
    payload([{"filename": "docs\\internal\\ci\\gate.md", "status": "modified"}]),
    payload([{"filename": "docs/internal/ci/gate.md", "status": "modified"}], changed_files=2),
    payload([], changed_files=0),
    payload(docs, changed_files=3_000),
    payload([docs[0], docs[0]]),
    {"unavailable_reason": "api-failed", "files": []},
    payload([docs[0]], head_sha="short"),
    payload([docs[0]], base_sha="g" * 40),
    payload([docs[0]], repository="other/repository"),
    payload([docs[0]], pull_request=0),
]
for case in full_cases:
    classified = MODULE.classify(case)
    assert classified["mode"] == "full", case
    assert classified["rollout"] == "observe", case
    assert classified["authoritative_gate"] == "full", case

print("pr-gate-impact-classifier=ok mode=observe")
