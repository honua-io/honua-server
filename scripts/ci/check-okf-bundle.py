#!/usr/bin/env python3
"""Gate the Open Knowledge Format bundle that docs/ publishes.

The link gate next door (check-doc-links.py) proves the *edges* resolve. This
proves the *nodes* exist and are typed: every page inside the bundle boundary
carries OKF frontmatter with a `type` the bundle declares, and every page
outside it is outside by a recorded decision rather than by being un-navigable.

That second half is the point. Before this gate, 39 pages in the product tree
were unreachable from SUMMARY.md and nothing distinguished "deliberately not
published" from "quietly lost". The boundary now lives in
scripts/ci/okf-bundle.v1.json, and a new directory has to be classified before
the build goes green.

OKF v0.2 conformance notes that shape the rules below:

  - `type` is the only always-required key. A concept carrying just `type` is
    fully conformant, so `title` and `description` are encouraged, not demanded.
  - There is no `timestamp` field in the specification. The trust family spells
    the build stamp `generated`. honua-site emitted `timestamp` until
    2026-09-10; that mistake is rejected by name here so it cannot spread.
  - The specification requires *consumers* to tolerate unknown types and to
    preserve unknown keys. This is a producer gate over our own bundle, so it
    deliberately does neither: an unrecognised `type` in docs/ means a typo or a
    page nobody classified, and both are worth failing on.

Usage:
    python3 scripts/ci/check-okf-bundle.py            # gate docs/
    python3 scripts/ci/check-okf-bundle.py --summary  # also print a type census
"""
from __future__ import annotations

import json
import pathlib
import re
import sys

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
MANIFEST_PATH = REPO_ROOT / "scripts" / "ci" / "okf-bundle.v1.json"

DATE_RE = re.compile(
    r"^\d{4}-\d{2}-\d{2}"
    r"(?:[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:\d{2})?)?$"
)
SCALAR_RE = re.compile(r"^([A-Za-z_][A-Za-z0-9_]*):\s*(.*)$")


def load_manifest() -> dict:
    with open(MANIFEST_PATH, "r", encoding="utf-8") as fh:
        return json.load(fh)


def parse_frontmatter(text: str) -> tuple[dict[str, str] | None, str | None]:
    """Return (fields, error). fields is None when the file has no frontmatter."""
    if not text.startswith("---\n") and not text.startswith("---\r\n"):
        return None, None
    lines = text.splitlines()
    fields: dict[str, str] = {}
    for i, raw in enumerate(lines[1:], start=1):
        if raw.strip() == "---":
            return fields, None
        if not raw.strip() or raw.lstrip().startswith("#"):
            continue
        m = SCALAR_RE.match(raw)
        if not m:
            # Nested or list values are legal YAML but nothing here emits them;
            # flag rather than silently skipping a line we cannot read.
            return fields, f"line {i + 1} is not a `key: value` scalar: {raw.strip()!r}"
        key, value = m.group(1), m.group(2).strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
            value = value[1:-1]
        fields[key] = value
    return fields, "frontmatter fence was never closed"


def is_real_calendar_date(value: str) -> bool:
    m = re.match(r"^(\d{4})-(\d{2})-(\d{2})", value)
    if not m:
        return False
    year, month, day = (int(g) for g in m.groups())
    if not 1 <= month <= 12:
        return False
    import calendar

    return 1 <= day <= calendar.monthrange(year, month)[1]


MERMAID_RE = re.compile(r"```mermaid\r?\n(.*?)```", re.DOTALL)
MERMAID_NODE_RE = re.compile(r"^\s*(\w+)[\[\(\{]", re.MULTILINE)
MERMAID_SUBGRAPH_ID_RE = re.compile(r"^\s*subgraph\s+(\w+)[\[\(]", re.MULTILINE)
MERMAID_EDGE_RE = re.compile(r"^\s*(\w+)\s*-[.-]*->(?:\|[^|]*\|)?\s*(\w+)", re.MULTILINE)


def check_mermaid_diagrams(root: pathlib.Path, excluded) -> list[str]:
    """A diagram that does not render is worse than no diagram.

    ASCII box-drawing always renders and is unreadable to anything that is not a
    human eye; mermaid is parseable by both but fails silently when it is
    malformed — GitBook shows an error card and the markdown reader sees source.
    These are the two breakages that actually happen when a diagram is edited:
    an unbalanced `subgraph`/`end`, and an edge naming a node that no longer
    exists after a rename.

    This is the foundation the WS8 `diagram` concept type needs. Checking that a
    diagram's referenced capability ids resolve comes with that type; checking
    that the diagram renders at all comes first.
    """
    problems: list[str] = []
    for path in sorted(root.rglob("*.md")):
        if excluded(path):
            continue
        text = path.read_text(encoding="utf-8")
        for index, block in enumerate(MERMAID_RE.findall(text), start=1):
            rel = path.relative_to(REPO_ROOT).as_posix()
            opens = len(re.findall(r"^\s*subgraph\b", block, re.MULTILINE))
            closes = len(re.findall(r"^\s*end\s*$", block, re.MULTILINE))
            if opens != closes:
                problems.append(
                    f"{rel}: mermaid block {index} has {opens} `subgraph` and {closes} `end`; "
                    "it will render as an error card"
                )
                continue
            declared = set(MERMAID_NODE_RE.findall(block)) | set(MERMAID_SUBGRAPH_ID_RE.findall(block))
            declared.discard("subgraph")
            endpoints = set()
            for left, right in MERMAID_EDGE_RE.findall(block):
                endpoints.update((left, right))
            dangling = sorted(endpoints - declared)
            if dangling:
                problems.append(
                    f"{rel}: mermaid block {index} draws edges to undeclared node(s): "
                    f"{', '.join(dangling)}"
                )
    return problems


SUMMARY_LINK_RE = re.compile(r"\]\(([^)]+\.md)(?:#[^)]*)?\)")


def check_summary_is_in_bundle(root: pathlib.Path, excluded) -> list[str]:
    """If a page is in the table of contents, it is published documentation.

    SUMMARY.md is what GitBook publishes. A page listed there but excluded from
    the bundle would be documentation we ship to readers and withhold from
    agents, which is the split this program exists to close. Three pages were in
    exactly that state when the boundary was first drawn — the CITE status
    snapshot and two client-template pages under docs/gis — and they are
    re-included by name rather than by widening the directory rules.
    """
    summary = root / "SUMMARY.md"
    if not summary.is_file():
        return []
    problems: list[str] = []
    for match in SUMMARY_LINK_RE.finditer(summary.read_text(encoding="utf-8")):
        target = match.group(1)
        if target.startswith(("http://", "https://", "..")):
            continue
        path = (root / target).resolve()
        if not path.is_file():
            continue  # the link gate owns dead-target reporting
        if excluded(path):
            rel = path.relative_to(REPO_ROOT).as_posix()
            problems.append(
                f"{rel}: listed in SUMMARY.md but excluded from the bundle. Publishing a page "
                f"to readers while withholding it from agents is the split this bundle closes — "
                f"either re-include it in {MANIFEST_PATH.name} or take it out of the table of contents."
            )
    return problems


REMEDIATION_RE = re.compile(r"runbook_url|remediationRef", re.IGNORECASE)


def check_runbook_typing(root: pathlib.Path, excluded) -> list[str]:
    """A page an alert or an error payload sends someone to is a runbook. Say so.

    scripts/ci/code-referenced-anchors.v1.json already records which docs URLs
    product code and shipped alert rules hand to an operator at runtime, and why.
    Entries whose reason names a `runbook_url` or a `remediationRef` are
    remediation; the SCIM `documentationUri` entry beside them is not. Deriving
    the type from that register rather than re-deciding it by hand keeps one
    fact in one place: when an alert starts pointing at a new page, that page has
    to become a runbook before the build is green.
    """
    registry_path = REPO_ROOT / "scripts" / "ci" / "code-referenced-anchors.v1.json"
    if not registry_path.is_file():
        return []
    with open(registry_path, "r", encoding="utf-8") as fh:
        registry = json.load(fh)
    base = registry.get("docsBaseUrl", "").rstrip("/")

    expected: dict[str, str] = {}
    for entry in registry.get("references", []):
        why = entry.get("why", "")
        if not REMEDIATION_RE.search(why):
            continue
        url = entry.get("url", "")
        if base and url.startswith(base):
            url = url[len(base):]
        rel = url.lstrip("/").split("#", 1)[0]
        if rel:
            expected.setdefault(f"{rel}.md", why.split(".")[0])

    problems: list[str] = []
    for rel, why in sorted(expected.items()):
        path = root / rel
        if not path.is_file() or excluded(path):
            continue
        fields, error = parse_frontmatter(path.read_text(encoding="utf-8"))
        if not fields or error:
            continue  # already reported by the frontmatter pass
        if fields.get("type") != "runbook":
            problems.append(
                f"docs/{rel}: is cited as remediation ({why}) but is typed "
                f"{fields.get('type')!r}; a page an alert or an error payload sends "
                "someone to must be `type: runbook`"
            )
    return problems


def main(argv: list[str]) -> int:
    manifest = load_manifest()
    root = REPO_ROOT / manifest["root"]
    concept_types = set(manifest["conceptTypes"])
    rejected = manifest["rejectedFields"]
    date_fields = set(manifest["dateFields"])

    excluded_dirs = []
    problems: list[str] = []
    for entry in manifest["excludedDirs"]:
        path = REPO_ROOT / entry["path"]
        if not path.is_dir():
            problems.append(
                f"{MANIFEST_PATH.name}: excluded directory {entry['path']} does not exist; "
                "remove the entry rather than leaving a stale exclusion"
            )
            continue
        excluded_dirs.append(path.resolve())
    excluded_files = set()
    for entry in manifest["excludedFiles"]:
        path = REPO_ROOT / entry["path"]
        if not path.is_file():
            problems.append(
                f"{MANIFEST_PATH.name}: excluded file {entry['path']} does not exist; "
                "remove the entry rather than leaving a stale exclusion"
            )
            continue
        excluded_files.add(path.resolve())

    included_files = set()
    for entry in manifest.get("includedFiles", []):
        path = REPO_ROOT / entry["path"]
        if not path.is_file():
            problems.append(
                f"{MANIFEST_PATH.name}: re-included file {entry['path']} does not exist; "
                "remove the entry rather than leaving a stale inclusion"
            )
            continue
        included_files.add(path.resolve())

    def excluded(path: pathlib.Path) -> bool:
        resolved = path.resolve()
        if resolved in included_files:  # an explicit re-inclusion beats its directory
            return False
        if resolved in excluded_files:
            return True
        return any(d in resolved.parents for d in excluded_dirs)

    census: dict[str, int] = {}
    checked = 0
    for path in sorted(root.rglob("*.md")):
        if excluded(path):
            continue
        rel = path.relative_to(REPO_ROOT).as_posix()
        checked += 1
        text = path.read_text(encoding="utf-8")
        fields, error = parse_frontmatter(text)
        if fields is None:
            problems.append(
                f"{rel}: no OKF frontmatter. Every page inside the bundle must open with a "
                "`---` fence carrying at least `type`. If this page is not documentation, "
                f"add it to {MANIFEST_PATH.name} with a reason."
            )
            continue
        if error:
            problems.append(f"{rel}: {error}")
            continue

        concept_type = fields.get("type")
        if not concept_type:
            problems.append(f"{rel}: frontmatter has no `type` (the one field OKF requires)")
        elif concept_type not in concept_types:
            problems.append(
                f"{rel}: unknown `type` {concept_type!r} — declare it in "
                f"{MANIFEST_PATH.name} conceptTypes or use one of: "
                f"{', '.join(sorted(concept_types))}"
            )
        else:
            census[concept_type] = census.get(concept_type, 0) + 1

        for field, why in rejected.items():
            if field in fields:
                problems.append(f"{rel}: `{field}` must not be used — {why}")

        for key in ("title", "description"):
            if key in fields and not fields[key].strip():
                problems.append(f"{rel}: `{key}` is present but empty; omit it instead")

        for field in sorted(date_fields & set(fields)):
            value = fields[field]
            if not DATE_RE.match(value) or not is_real_calendar_date(value):
                problems.append(
                    f"{rel}: `{field}` must be an ISO-8601 date or date-time, got {value!r}"
                )

    problems.extend(check_runbook_typing(root, excluded))
    problems.extend(check_summary_is_in_bundle(root, excluded))
    problems.extend(check_mermaid_diagrams(root, excluded))

    if problems:
        print("OKF bundle validation failed:", file=sys.stderr)
        for problem in problems:
            print(f"- {problem}", file=sys.stderr)
        return 1

    print(
        f"OKF bundle OK: {checked} concept(s) under {manifest['root']}/ carry valid "
        f"v{manifest['spec']['version']} frontmatter."
    )
    if "--summary" in argv:
        for concept_type in sorted(census):
            print(f"  {concept_type:<10} {census[concept_type]}")
        print(
            f"  excluded: {len(manifest['excludedDirs'])} director(ies), "
            f"{len(manifest['excludedFiles'])} file(s), each with a recorded reason"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
