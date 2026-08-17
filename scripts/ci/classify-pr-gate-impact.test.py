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

# Gate-governed documents: markdown whose content a gate step asserts. A
# docs-only route would skip that step, so these must never be candidates even
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

# Documents that gate sources only *mention* in prose, doc comments, or assertion
# messages. They stay eligible for the docs-only class; they are enumerated so
# the drift guard can tell "reviewed and safe" apart from "never looked at".
# This list lives here rather than in the classifier so that curating it never
# changes the classifier blob that observation receipts bind.
REFERENCE_ONLY_DOCS = frozenset(
    {
        "docs/internal/admin-api/studio-package-lifecycle.md",
        "docs/internal/contributor/adr/0041-core-abstractions-extraction.md",
        "docs/internal/contributor/entitlement-sweep-known-gaps.md",
    }
)

assert not (MODULE.LEAN_GATE_GOVERNED_DOCS & REFERENCE_ONLY_DOCS), (
    "a document cannot be both governed and reference-only"
)
for referenced in sorted(REFERENCE_ONLY_DOCS):
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


# ---------------------------------------------------------------------------
# Drift guard
#
# Every `docs/internal/**.md` reference reachable from a gate input must be
# explicitly classified as governed or reference-only. Without this, a new
# content assertion silently widens the docs-only class.
#
# The guard reads both spellings that appear in this repository: a whole-path
# literal, and a path assembled segment-by-segment through
# `ArchitectureTestHelpers.CombinePath`, `Path.Combine`, or `Path.Join`, which is
# the prevailing style in the architecture tests. A path whose segments are not
# adjacent string literals (a variable, a loop, a runtime concatenation) is out
# of reach; the governed list, not this scan, is the contract.
# ---------------------------------------------------------------------------

# Sources read by a gate step the docs-only route would skip, plus the always-on
# PR Gate steps whose governed inputs must stay visible to this scan. Adding a
# gate step means adding its source here.
GATE_INPUT_SOURCE_GLOBS = (
    "tests/dotnet/Honua.Architecture.Tests/**/*.cs",
    "tests/dotnet/Honua.Server.Tests/**/*.cs",
    "tests/dotnet/Honua.Ai.Tests/**/*.cs",
    ".github/actions/lean-gate/action.yml",
    ".github/workflows/pr-gate.yml",
    "scripts/ci/base-image-mirrors.sh",
    "scripts/ci/fixtures/validate-lean-gate.py",
    "scripts/ci/check-markdown-command-policy.ps1",
    "scripts/ci/openapi-drift-check.py",
    "scripts/ci/merge-train/fixtures/validate-timeout-retry.sh",
    "scripts/ci/merge-train/fixtures/validate-early-failure-observe.sh",
)

LITERAL_DOC_PATTERN = re.compile(
    r"docs/internal/[A-Za-z0-9_.+\-]+(?:/[A-Za-z0-9_.+\-]+)*\.md"
)
SEGMENT_HEAD_PATTERN = re.compile(r'"docs"\s*,\s*"internal"|"docs/internal"')
SEGMENT_NEXT_PATTERN = re.compile(r'\s*,\s*"([A-Za-z0-9_.+\-]+)"')


def read_source(path: Path) -> str:
    """Read a gate input as UTF-8, naming the file/byte/offset on failure.

    Skipping an undecodable source would fail the guard *open*: the file could
    be exactly the one introducing a new content assertion.
    """
    try:
        return path.read_text(encoding="utf-8")
    except UnicodeDecodeError as error:
        byte = error.object[error.start : error.start + 1]
        raise AssertionError(
            f"{path}: gate input is not valid UTF-8 -- byte 0x{byte.hex()} at offset "
            f"{error.start} ({error.reason}). The drift guard cannot read it, so it cannot "
            "prove the docs-only class is sound; re-save the file as UTF-8."
        ) from error


def document_references(text: str) -> set[str]:
    references = set(LITERAL_DOC_PATTERN.findall(text))
    for head in SEGMENT_HEAD_PATTERN.finditer(text):
        segments: list[str] = []
        cursor = head.end()
        while True:
            following = SEGMENT_NEXT_PATTERN.match(text, cursor)
            if following is None:
                break
            segments.append(following.group(1))
            cursor = following.end()
            if following.group(1).endswith(".md"):
                references.add("docs/internal/" + "/".join(segments))
                break
    return references


# The guard must see the two spellings it exists for, whatever the repository
# currently contains.
assert document_references('File.ReadAllText("docs/internal/ci/gate-model.md")') == {
    "docs/internal/ci/gate-model.md"
}
assert document_references(
    'CombinePath(root,\n  "docs",\n  "internal",\n  "spikes",\n  "sample.md")'
) == {"docs/internal/spikes/sample.md"}
assert document_references('Path.Combine(root, "docs/internal", "ci", "sample.md")') == {
    "docs/internal/ci/sample.md"
}
assert document_references('ReadJson(root, "docs", "internal", "developer", "data.json")') == set()

REPOSITORY_ROOT = SCRIPT.resolve().parents[2]
discovered: dict[str, set[str]] = {}
scanned = 0
for glob in GATE_INPUT_SOURCE_GLOBS:
    for path in sorted(REPOSITORY_ROOT.glob(glob)):
        if not path.is_file() or path.resolve() == SCRIPT.resolve():
            continue
        scanned += 1
        for reference in document_references(read_source(path)):
            discovered.setdefault(reference, set()).add(
                path.relative_to(REPOSITORY_ROOT).as_posix()
            )

assert scanned > 0, "the drift guard scanned no gate inputs; its globs are stale"
classified_docs = MODULE.LEAN_GATE_GOVERNED_DOCS | REFERENCE_ONLY_DOCS
unclassified = sorted(set(discovered) - classified_docs)
assert not unclassified, (
    "gate inputs reference internal documents that the PR Gate impact classifier has never "
    "been told about. Decide, for each, whether a gate step asserts its CONTENT (add to "
    "LEAN_GATE_GOVERNED_DOCS in classify-pr-gate-impact.py) or only mentions it (add to "
    "REFERENCE_ONLY_DOCS here): "
    + ", ".join(f"{name} <- {sorted(discovered[name])}" for name in unclassified)
)

# Advisory only. A governed document that stops being asserted is merely
# over-conservative, and failing here would push maintainers to prune correct
# entries -- which also resets any observation cohort bound to the classifier.
unreferenced = sorted(classified_docs - set(discovered))
if unreferenced:
    print(
        "pr-gate-impact-classifier: note: no gate input references "
        + ", ".join(unreferenced)
        + " any more (safe to keep; prune only deliberately)"
    )

print(f"pr-gate-impact-classifier=ok mode=observe gate-inputs-scanned={scanned}")
