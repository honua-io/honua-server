#!/usr/bin/env bash
# Decide whether a pull request is DOCS-ONLY. A docs-only pull request skips the
# build and test steps of the required PR Gate jobs, and those jobs still
# conclude success themselves (operator ruling A, 2026-09-11; epic #3213). This
# script is the single definition of the class: pr-gate.yml's `format`,
# `pr-gate` and `affected-shards-select` jobs all call it, and nothing else
# decides. It is separate from, and wider than, the observe-only `docs-only`
# class of scripts/ci/classify-pr-gate-impact.py, which is left as it is.
#
# USAGE
#   docs-only-diff.sh [--github-output]                   pull_request merge ref (CI)
#   docs-only-diff.sh [--github-output] --base REF [--head REF]
#   docs-only-diff.sh --paths < changed-paths.txt         what-if, one path per line
#
# EXIT STATUS
#   0 docs-only, 1 not docs-only, 2 usage error. Every run prints
#   `docs_only=<true|false>` and `reason=<why>`. With --github-output both lines
#   are also appended to $GITHUB_OUTPUT, a docs-only answer adds the line
#   `PR Gate: docs-only diff, build and tests skipped (<reason>)` to
#   $GITHUB_STEP_SUMMARY, and the exit status is 0 for both answers. An internal
#   failure then answers docs_only=false: a broken classifier costs a full gate
#   run, never a skipped one.
#
# WHAT COUNTS AS DOCS-ONLY
#   Every changed path -- a rename counts as its old AND its new path, because
#   the diff is taken with --no-renames -- must be
#     * a top-level Markdown file (README.md, AGENTS.md, SECURITY.md, ...) that
#       is added or modified (deleting one is never docs-only), or
#     * a prose or image file under docs/ (.md .png .jpg .jpeg .gif .svg .webp)
#       outside docs/gis/data/, added, modified or deleted;
#   and no changed path may be referenced by a GATE INPUT (below). A type change
#   (file <-> symlink) is never docs-only.
#
#   Any doubt answers "not docs-only": a non-pull_request event, a checkout that
#   is not a two-parent merge commit, a base or head that does not resolve, an
#   empty diff, more than 400 changed paths, no python3.
#
# THE DIFF BASE
#   On pull_request the checked-out ref is refs/pull/N/merge. Its first parent
#   is the current tip of the target branch and its tree is exactly what that
#   branch would hold after the merge, so HEAD^1..HEAD is the pull request's
#   diff against its merge base with the target branch as it would land (a path
#   the PR changes identically to the target does not appear, and correctly so:
#   the target would not change). pr-gate.yml checks out with fetch-depth 2 for
#   this, as compute-lean-gate-build-scope.sh does.
#
# WHY NOT "EVERYTHING UNDER docs/"
#   docs/ holds data as well as prose. Non-prose files that tests, generators
#   or builds read (grep of tests/, scripts/, src/ and .github/, 2026-09-11):
#     docs/gis/data/*.json               feature catalog, capability keys/matrix,
#                                        GeoServices parity, proof ledger, client
#                                        certification roster/matrix/fixture;
#                                        architecture tests, generators, and the
#                                        Honua.Ai/Honua.Server embedded resources
#     docs/developer/api-specs/*.json    embedded by Honua.Server; OpenAPI drift,
#                                        admin-operation parity tests
#     docs/developer/sdk-*.json          ReleaseTrainManifestTests, SDK scripts
#     docs/internal/developer/metadata-catalog-endpoints.v1.json
#                                        DocumentationMatrixDriftTests
#     docs/guides/deploy/examples/prometheus-alerts.yml
#                                        MetricNameContractTests, docs link gate
#     docs/.gitbook.yaml                 check-doc-links.py
#     docs/gis/client-templates/*, docs/user/client-templates/*
#                                        client-compat smoke
#   The extension allowlist keeps every one of them out without a per-file
#   list. Markdown needs more than an extension check: the architecture and
#   Server tests assert the CONTENT of dozens of documents (docs/cite-status.md,
#   docs/reference/geoprocessing-operations.md,
#   docs/gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md,
#   docs/internal/operator/audit-coverage-matrix.md, ...). A docs-only exit that
#   skipped those tests would let a documentation edit turn trunk red.
#
# THE GATE-INPUT SCAN
#   A changed path is refused when a gate input references it, or a directory
#   containing it. Gate inputs are what the skipped steps compile, execute or
#   read (GATE_INPUTS below): all of src/ and tests/dotnet/ -- every .NET test,
#   including the affected-shard families, not only the lean smoke -- the root
#   build files, the PR Gate workflow and its composite actions, the scripts its
#   skipped steps run, docker/worker-gdal/, certification/, and every NON-prose
#   file under docs/ (data such as docs/gis/data/public-interface-proof.json
#   names the documents the proof-ledger tests open). Recognised spellings:
#     * a path literal: `docs/cite-status.md`, `docs/gis/data/`;
#     * a path assembled from adjacent string literals, the architecture-test
#       house style: CombinePath(root, "docs", "gis", "X.md");
#     * a quoted top-level Markdown name: "AGENTS.md";
#     * an MSBuild item path, resolved against its project: ..\..\README.md.
#   Whole-line comments in code are ignored. A path built from variables at run
#   time is out of reach of any static scan; everywhere else the scan errs the
#   other way (prose inside strings, URLs and data all count), which only ever
#   costs a full gate run. The scan reads the head tree at gate time, so a test
#   that starts reading a document governs it from that commit on; there is no
#   list to keep in sync. REFERENCE_ONLY below names the few (reference, source)
#   pairs reviewed as not reading content; an `existence` entry holds only while
#   the path still exists in the head tree.
#
# This script only reads the repository. It has no git write primitive.

set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

usage() {
  echo "usage: $(basename "$0") [--github-output] [--base REF [--head REF] | --paths]" >&2
  exit 2
}

github_output=0
mode=merge-ref
base_ref=""
head_ref=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --github-output) github_output=1 ;;
    --base)
      [[ $# -ge 2 && "${mode}" != paths ]] || usage
      mode=refs
      base_ref="$2"
      shift
      ;;
    --head)
      [[ $# -ge 2 ]] || usage
      head_ref="$2"
      shift
      ;;
    --paths)
      [[ "${mode}" == merge-ref ]] || usage
      mode=paths
      ;;
    *) usage ;;
  esac
  shift
done
if [[ -n "${head_ref}" && "${mode}" != refs ]]; then
  usage
fi

tmp="$(mktemp -d)"
trap 'rm -rf "${tmp}"' EXIT

answer() {
  local verdict="$1" reason
  reason="$(printf '%s' "$2" | tr '\n\r' '  ')"
  echo "docs_only=${verdict}"
  echo "reason=${reason}"
  if [[ "${github_output}" == 1 ]]; then
    if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
      printf 'docs_only=%s\nreason=%s\n' "${verdict}" "${reason}" >> "${GITHUB_OUTPUT}"
    fi
    if [[ "${verdict}" == true ]]; then
      echo "::notice::PR Gate: docs-only diff, build and tests skipped (${reason})"
      if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
        echo "PR Gate: docs-only diff, build and tests skipped (${reason})" >> "${GITHUB_STEP_SUMMARY}"
      fi
    else
      echo "::notice::PR Gate: full gate (${reason})"
    fi
    exit 0
  fi
  if [[ "${verdict}" == true ]]; then
    exit 0
  fi
  exit 1
}

# An unexpected failure anywhere below must answer "not docs-only" rather than
# fail the calling job or, worse, leave a half-written answer behind.
trap 'trap - ERR; answer false "classifier error at line ${LINENO}; refusing to skip the gate"' ERR

case "${mode}" in
  merge-ref)
    if [[ "${GITHUB_EVENT_NAME:-}" != pull_request ]]; then
      answer false "event '${GITHUB_EVENT_NAME:-unknown}' has no pull-request diff base"
    fi
    if ! git rev-parse -q --verify 'HEAD^2^{commit}' >/dev/null; then
      answer false "HEAD is not a pull-request merge commit (no second parent in this checkout)"
    fi
    if ! git cat-file -e 'HEAD^1^{commit}' 2>/dev/null; then
      answer false "the target-branch parent is not in this checkout (fetch-depth too shallow)"
    fi
    base_ref='HEAD^1'
    head_ref=HEAD
    ;;
  refs)
    head_ref="${head_ref:-HEAD}"
    for ref in "${base_ref}" "${head_ref}"; do
      if ! git rev-parse -q --verify "${ref}^{commit}" >/dev/null; then
        answer false "ref '${ref}' does not resolve to a commit in this checkout"
      fi
    done
    ;;
  paths)
    head_ref=HEAD
    cat > "${tmp}/paths"
    ;;
esac

if [[ "${mode}" != paths ]]; then
  if ! git diff --no-renames --no-ext-diff --name-status -z "${base_ref}" "${head_ref}" -- > "${tmp}/changes"; then
    answer false "git diff ${base_ref} ${head_ref} failed"
  fi
fi

if ! command -v python3 >/dev/null 2>&1; then
  answer false "python3 is unavailable; cannot scan gate inputs"
fi

head_tree="$(git rev-parse "${head_ref}^{tree}")"
status=0
python3 - "${mode}" "${tmp}" "${head_tree}" > "${tmp}/verdict" <<'PY' || status=$?
"""Classify the changed paths; exit 0 docs-only, 1 not, print one reason line."""

from __future__ import annotations

import os
import re
import subprocess
import sys
from pathlib import PurePosixPath

MAX_PATHS = 400
PROSE_EXTENSIONS = {".md", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp"}

# Everything a step the docs-only exit skips compiles, executes or reads. A
# gate step added to pr-gate.yml or the lean-gate action that reads a new file
# means adding that file here; scripts/ci/fixtures/validate-docs-only-diff.sh
# fails if an entry stops existing.
GATE_INPUTS = (
    # Compiled and run by the lean gate, the execution proofs and the
    # affected-shard families.
    "src",
    "tests/dotnet",
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "Honua.sln",
    # The required workflow, its composite actions, and what its skipped steps run.
    ".github/workflows/pr-gate.yml",
    ".github/actions/lean-gate",
    ".github/actions/format-check",
    ".github/actions/setup-dotnet-ci",
    ".github/ci-shards.json",
    "scripts/ci/lib",
    "scripts/ci/merge-train",
    "scripts/ci/check-server-test-shard-coverage.py",
    "scripts/ci/compute-affected-projects.sh",
    "scripts/ci/compute-affected-shards.py",
    "scripts/ci/compute-lean-gate-build-scope.sh",
    "scripts/ci/compute-lean-gate-format-scope.sh",
    "scripts/ci/dotnet-restore-retry.sh",
    "scripts/ci/honua-server-targeted-tests.sh",
    "scripts/ci/openapi-drift-check.py",
    "scripts/ci/prepull-testcontainers-postgis.sh",
    "scripts/ci/promote-verified-image.py",
    "scripts/ci/server-boot-smoke.sh",
    "scripts/ci/verify-serving-image-boundary.py",
    "scripts/ci/fixtures/validate-affected-shards.py",
    "scripts/ci/fixtures/validate-lean-gate.py",
    "scripts/ci/fixtures/validate-promote-verified-image.py",
    "scripts/ci/fixtures/validate-serving-image-boundary.py",
    "docker/worker-gdal",
    "certification",
    # Data under docs/ (prose there is the subject, not an input; see below).
    "docs",
)

# Reviewed (reference, source) pairs that do not read the referenced content.
# `existence`: the source needs the path to exist, so the pair is honoured only
# while it does in the head tree. `mention`: the text names the path without
# opening it.
REFERENCE_ONLY = {
    ("docs", "tests/dotnet/Honua.Architecture.Tests/BasicArchitectureTests.cs"):
        ("existence", "asserts Directory.Exists(docs)"),
    ("AGENTS.md", "tests/dotnet/Honua.Architecture.Tests/BasicArchitectureTests.cs"):
        ("existence", "asserts File.Exists(AGENTS.md)"),
    ("docs/internal", "tests/dotnet/Honua.Architecture.Tests/AuditCoverageMatrixDriftTests.cs"):
        ("mention", "assertion-message text; the document it reads keeps its own literal"),
    ("README.md", "scripts/ci/fixtures/validate-affected-shards.py"):
        ("mention", "a synthetic changed path handed to the shard selector, never opened"),
    ("docs/internal/ci/gate-model.md", "scripts/ci/fixtures/validate-affected-shards.py"):
        ("mention", "a synthetic changed path handed to the shard selector, never opened"),
}

CODE_SUFFIXES = {".cs", ".sh", ".bash", ".py", ".yml", ".yaml", ".ps1", ".psm1", ".js", ".mjs", ".cjs", ".ts"}
MSBUILD_SUFFIXES = {".csproj", ".props", ".targets", ".proj", ".sln", ".slnf"}
COMMENT_LINE = re.compile(r"^\s*(?://|#|/\*|\*|<!--)")
DOCS_LITERAL = re.compile(r"(?<![A-Za-z0-9_\-])docs(?:/[A-Za-z0-9_.+\-]+)+")
QUOTED_DOCS_HEAD = re.compile(r"([\"'])(docs(?:/[A-Za-z0-9_.+\-]+)*)/?\1")
NEXT_SEGMENT = re.compile(r"\s*,\s*([\"'])([A-Za-z0-9_.+\-]+)\1")
FILE_LIKE = re.compile(r"\.[A-Za-z][A-Za-z0-9]*$")
QUOTED_TOP_MARKDOWN = re.compile(r"([\"'])(?:\./)?([A-Za-z0-9_\-]+\.md)\1", re.IGNORECASE)
MSBUILD_ELEMENT = re.compile(r"<([A-Za-z_][\w.\-]*)((?:\s+[\w.:\-]+\s*=\s*\"[^\"]*\")*)\s*(/?)>")
MSBUILD_ATTRIBUTE = re.compile(r"([\w.:\-]+)\s*=\s*\"([^\"]*)\"")


def refusal(status: str, path: str) -> str | None:
    """Why `path` cannot be part of a docs-only diff, or None when it can."""
    if status not in ("A", "M", "D"):
        return f"{path}: change type {status!r} (only additions, modifications and deletions qualify)"
    suffix = PurePosixPath(path).suffix.lower()
    if "/" not in path:
        if suffix != ".md":
            return f"{path}: not documentation"
        if status == "D":
            return f"{path}: deletes a top-level Markdown file"
        return None
    if not path.startswith("docs/"):
        return f"{path}: outside docs/"
    if path.startswith("docs/gis/data/"):
        return f"{path}: generated data under docs/gis/data/"
    if suffix not in PROSE_EXTENSIONS:
        return f"{path}: not a prose or image file ({suffix or 'no extension'})"
    return None


def references(source: str, text: str) -> dict[str, str]:
    """Every docs/ or top-level Markdown path `text` names, with how it is used.

    The kind is `content`, except for a self-closing MSBuild `<None Pack="true">`
    item without a CopyTo* attribute: that only ships the file inside a NuGet
    package (every packable project packs the top-level README.md this way), so
    the build and the tests need it to exist and read nothing from it.
    """
    found: dict[str, str] = {}

    def add(reference: str, kind: str = "content") -> None:
        reference = reference.rstrip("/")
        if found.get(reference) != "content":
            found[reference] = kind

    suffix = PurePosixPath(source).suffix.lower()
    if suffix in MSBUILD_SUFFIXES:
        parent = PurePosixPath(source).parent
        for element in MSBUILD_ELEMENT.finditer(text):
            name, attributes, closing = element.groups()
            pairs = MSBUILD_ATTRIBUTE.findall(attributes)
            pack_only = (
                name == "None"
                and closing == "/"
                and any(key.lower() == "pack" and value.strip().lower() == "true" for key, value in pairs)
                and not any(key.lower().startswith("copyto") for key, _ in pairs)
            )
            for _, value in pairs:
                for item in value.replace("\\", "/").split(";"):
                    item = item.strip()
                    if "docs" not in item and not item.lower().endswith(".md"):
                        continue
                    resolved = os.path.normpath((parent / item).as_posix())
                    if resolved != ".." and not resolved.startswith("../"):
                        add(resolved, "existence" if pack_only else "content")
    if suffix in CODE_SUFFIXES:
        text = "\n".join(line for line in text.split("\n") if not COMMENT_LINE.match(line))
    continued: list[tuple[int, int]] = []
    for head in QUOTED_DOCS_HEAD.finditer(text):
        segments = [head.group(2)]
        cursor = head.end()
        while not FILE_LIKE.search(segments[-1]):
            following = NEXT_SEGMENT.match(text, cursor)
            if following is None:
                break
            segments.append(following.group(2))
            cursor = following.end()
        if len(segments) > 1:
            continued.append((head.start(), head.end()))
        add("/".join(segments))
    for literal in DOCS_LITERAL.finditer(text):
        if not any(start <= literal.start() < end for start, end in continued):
            add(literal.group(0))
    for match in QUOTED_TOP_MARKDOWN.finditer(text):
        add(match.group(2))
    return found


def git(*args: str, **kwargs) -> subprocess.CompletedProcess:
    return subprocess.run(["git", *args], check=False, **kwargs)


def scan(tree: str) -> dict[str, dict[str, str]]:
    """Map each reference found in the gate inputs to {source: kind}."""
    listed = git(
        "grep", "-I", "-l", "-i", "-E", r"docs|\.md", tree, "--", *GATE_INPUTS,
        capture_output=True,
    )
    if listed.returncode not in (0, 1):
        raise RuntimeError("git grep over the gate inputs failed")
    prefix = tree + ":"
    sources = []
    for line in listed.stdout.decode("utf-8", "surrogateescape").splitlines():
        source = line[len(prefix):] if line.startswith(prefix) else line
        # Prose documents are the subject of the check, not inputs to it.
        if refusal("M", source) is None:
            continue
        sources.append(source)
    if not sources:
        return {}
    batch = git(
        "cat-file", "--batch",
        input="".join(f"{tree}:{source}\n" for source in sources).encode("utf-8", "surrogateescape"),
        capture_output=True,
    )
    if batch.returncode != 0:
        raise RuntimeError("git cat-file over the gate inputs failed")
    data = batch.stdout
    found: dict[str, dict[str, str]] = {}
    cursor = 0
    for source in sources:
        newline = data.index(b"\n", cursor)
        header = data[cursor:newline].split()
        if len(header) != 3:
            raise RuntimeError(f"unreadable gate input {source}")
        size = int(header[2])
        body = data[newline + 1:newline + 1 + size].decode("utf-8", "replace")
        cursor = newline + 1 + size + 1
        for reference, kind in references(source, body).items():
            found.setdefault(reference, {})[source] = kind
    return found


def exists(tree: str, path: str) -> bool:
    return git("cat-file", "-e", f"{tree}:{path}", capture_output=True).returncode == 0


def verdict(code: int, reason: str, details: list[str] = ()) -> None:
    # DOCS_ONLY_EXPLAIN=1 lists every refusal, not just the first; that is the
    # answer to "why is my documentation change running the full gate?".
    if os.environ.get("DOCS_ONLY_EXPLAIN") == "1":
        for detail in details:
            print(detail, file=sys.stderr)
    print(reason)
    sys.exit(code)


def main() -> None:
    mode, workdir, tree = sys.argv[1:4]
    changes: list[tuple[str, str]] = []
    if mode == "paths":
        with open(os.path.join(workdir, "paths"), encoding="utf-8") as handle:
            for line in handle:
                path = line.strip()
                while path.startswith("./"):
                    path = path[2:]
                if path:
                    changes.append(("M", path))
    else:
        with open(os.path.join(workdir, "changes"), "rb") as handle:
            fields = handle.read().decode("utf-8", "surrogateescape").split("\0")
        if fields and fields[-1] == "":
            fields.pop()
        if len(fields) % 2:
            verdict(1, "unparseable git diff --name-status output")
        changes = [(fields[i], fields[i + 1]) for i in range(0, len(fields), 2)]

    if not changes:
        verdict(1, "empty diff")
    if len(changes) > MAX_PATHS:
        verdict(1, f"{len(changes)} changed paths exceeds the {MAX_PATHS}-path bound")

    refused = [reason for status, path in changes if (reason := refusal(status, path))]
    if refused:
        more = f" (and {len(refused) - 1} more)" if len(refused) > 1 else ""
        verdict(1, refused[0] + more, refused)

    found = scan(tree)
    governed: list[str] = []
    for _, path in changes:
        key = path.casefold()
        for reference, sources in sorted(found.items()):
            folded = reference.casefold()
            if key != folded and not key.startswith(folded + "/"):
                continue
            for source, kind in sorted(sources.items()):
                kind = REFERENCE_ONLY.get((reference, source), (kind, ""))[0]
                if kind == "mention" or (kind == "existence" and exists(tree, reference)):
                    continue
                governed.append(f"{path} is referenced by {source}" + ("" if reference == path else f" (as {reference})"))
    if governed:
        more = f" (and {len(governed) - 1} more)" if len(governed) > 1 else ""
        verdict(1, governed[0] + more, governed)

    noun = "path" if len(changes) == 1 else "paths"
    verdict(0, f"{len(changes)} documentation {noun}; no gate input reads them")


main()
PY

reason="$(head -n 1 "${tmp}/verdict" 2>/dev/null || true)"
case "${status}" in
  0) answer true "${reason}" ;;
  1) answer false "${reason}" ;;
  *) answer false "classifier failed (python exit ${status}); refusing to skip the gate" ;;
esac
