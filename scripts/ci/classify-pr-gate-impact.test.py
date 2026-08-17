#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
import re
from pathlib import Path


SCRIPT = Path(__file__).with_name("classify-pr-gate-impact.py")
SPEC = importlib.util.spec_from_file_location("pr_gate_impact", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

BASE = "a" * 40
HEAD = "b" * 40
POLICY_BLOB = "c" * 40
GATE_WORKFLOW_BLOB = "1" * 40
OBSERVER_WORKFLOW_BLOB = "e" * 40
RESOLVER_BLOB = "f" * 40
POLICY = "d" * 40


def payload(files: list[dict], **overrides: object) -> dict:
    value = {
        "repository": "honua-io/honua-server",
        "pull_request": 3231,
        "base_sha": BASE,
        "head_sha": HEAD,
        "policy_sha": POLICY,
        "policy_blob_sha": POLICY_BLOB,
        "gate_workflow_blob_sha": GATE_WORKFLOW_BLOB,
        "resolver_blob_sha": RESOLVER_BLOB,
        "observer_workflow_blob_sha": OBSERVER_WORKFLOW_BLOB,
        "trusted_execution": "default-branch-workflow-run/v1",
        "gate_workflow_path": ".github/workflows/pr-gate.yml",
        "gate_run_id": 123456,
        "gate_run_attempt": 2,
        "gate_run_head_sha": HEAD,
        "gate_run_conclusion": "success",
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
assert result["policy_sha"] == POLICY
assert result["policy_blob_sha"] == POLICY_BLOB
assert result["gate_workflow_blob_sha"] == GATE_WORKFLOW_BLOB
assert result["resolver_blob_sha"] == RESOLVER_BLOB
assert result["observer_workflow_blob_sha"] == OBSERVER_WORKFLOW_BLOB
assert result["trusted_execution"] == "default-branch-workflow-run/v1"
assert result["gate_workflow_path"] == ".github/workflows/pr-gate.yml"
assert result["gate_run_id"] == 123456
assert result["gate_run_attempt"] == 2
assert result["gate_run_head_sha"] == HEAD
assert result["gate_run_conclusion"] == "success"
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
    payload([docs[0]], policy_sha="short"),
    payload([docs[0]], policy_blob_sha="short"),
    payload([docs[0]], gate_workflow_blob_sha="short"),
    payload([docs[0]], resolver_blob_sha="short"),
    payload([docs[0]], observer_workflow_blob_sha="short"),
    payload([docs[0]], trusted_execution="candidate-workflow"),
    payload([docs[0]], gate_workflow_path=".github/workflows/lookalike.yml"),
    payload([docs[0]], gate_run_id=0),
    payload([docs[0]], gate_run_attempt=0),
    payload([docs[0]], gate_run_head_sha=BASE),
    payload([docs[0]], gate_run_conclusion=None),
    payload([docs[0]], gate_run_conclusion="in_progress"),
]
for case in full_cases:
    classified = MODULE.classify(case)
    assert classified["mode"] == "full", case
    assert classified["rollout"] == "observe", case
    assert classified["authoritative_gate"] == "full", case

# Lean-gate governed documents: markdown whose content a lean-gate step asserts.
# A docs-only route would skip that step, so these must never be candidates even
# though they satisfy the docs/internal/**.md shape.
assert MODULE.LEAN_GATE_GOVERNED_DOCS, "the governed-document denylist must not be empty"
for governed in sorted(MODULE.LEAN_GATE_GOVERNED_DOCS):
    assert governed.startswith("docs/internal/") and governed.endswith(".md"), governed
    for status in ("modified", "added"):
        alone = MODULE.classify(payload([{"filename": governed, "status": status}]))
        assert alone["mode"] == "full", governed
        assert alone["reason"] == "lean-gate-governed-doc", governed
        assert alone["authoritative_gate"] == "full", governed
    mixed = MODULE.classify(
        payload(
            [
                {"filename": "docs/internal/ci/gate-model.md", "status": "modified"},
                {"filename": governed, "status": "modified"},
            ]
        )
    )
    assert mixed["mode"] == "full", governed
    assert mixed["reason"] == "lean-gate-governed-doc", governed

assert not (MODULE.LEAN_GATE_GOVERNED_DOCS & MODULE.LEAN_GATE_REFERENCED_DOCS), (
    "a document cannot be both governed and reference-only"
)
for referenced in sorted(MODULE.LEAN_GATE_REFERENCED_DOCS):
    classified = MODULE.classify(payload([{"filename": referenced, "status": "modified"}]))
    assert classified["mode"] == "docs-only", referenced
    assert classified["reason"] == "internal-markdown-only", referenced

# Generated data and evidence assets that merely live under docs/ are never
# documentation for routing purposes.
for hostile in (
    "docs/gis/data/feature-catalog.json",
    "docs/internal/ci/evidence/pr-gate.json.gz",
    "docs/internal/ci/evidence/receipt.json",
    "docs/internal/README",
    "docs/internal",
    "docs/internal/ci/gate-model.md.bak",
    "docs/internalish/ci/gate-model.md",
):
    classified = MODULE.classify(payload([{"filename": hostile, "status": "modified"}]))
    assert classified["mode"] == "full", hostile
    assert classified["reason"] == "path-requires-full-gate", hostile

# Renames and deletions of a governed document still route to the full gate; the
# status check fires before the path check, which is the fail-closed order.
for status in ("removed", "renamed", "copied", "changed", "unchanged", ""):
    classified = MODULE.classify(
        payload([{"filename": "docs/internal/operator/audit-coverage-matrix.md", "status": status}])
    )
    assert classified["mode"] == "full", status
    assert classified["reason"] == "rename-delete-or-unknown-status", status

# Drift guard: every `docs/internal/**.md` literal reachable from a lean-gate
# source must be explicitly classified as governed or reference-only. Without
# this, a new content-asserting test would silently widen the docs-only class.
LEAN_GATE_DOC_PATTERN = re.compile(r"docs/internal/[A-Za-z0-9_./-]+\.md")
REPOSITORY_ROOT = SCRIPT.resolve().parents[2]
discovered: dict[str, set[str]] = {}
for glob in MODULE.LEAN_GATE_SOURCE_GLOBS:
    for path in sorted(REPOSITORY_ROOT.glob(glob)):
        if not path.is_file() or path.resolve() == SCRIPT.resolve():
            continue
        try:
            text = path.read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError):
            continue
        for reference in LEAN_GATE_DOC_PATTERN.findall(text):
            discovered.setdefault(reference, set()).add(
                str(path.relative_to(REPOSITORY_ROOT).as_posix())
            )

classified_docs = MODULE.LEAN_GATE_GOVERNED_DOCS | MODULE.LEAN_GATE_REFERENCED_DOCS
unclassified = sorted(set(discovered) - classified_docs)
assert not unclassified, (
    "lean-gate sources reference internal documents that the PR Gate impact classifier has "
    "never been told about. Decide, for each, whether the lean gate asserts its CONTENT "
    "(add to LEAN_GATE_GOVERNED_DOCS) or only mentions it (add to LEAN_GATE_REFERENCED_DOCS): "
    + ", ".join(f"{name} <- {sorted(discovered[name])}" for name in unclassified)
)
stale = sorted(classified_docs - set(discovered))
assert not stale, (
    "these documents are classified for the lean gate but no lean-gate source references them "
    "any more; prune them from classify-pr-gate-impact.py: " + ", ".join(stale)
)

print("pr-gate-impact-classifier=ok mode=observe")
