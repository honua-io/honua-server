#!/usr/bin/env python3
"""Verify the 2026.1 client-certification fixture manifest against the repository.

The manifest at ``docs/gis/data/client-certification-fixture.v1.json`` freezes the
fixture, server-configuration, and auth-policy contract for the 2026.1 client
certification gate (honua-io/honua-server#3393). This verifier is the standalone,
PR-tier check: it recomputes every digest from the real files on disk, recomputes
the three composite revisions from the documented algorithms, and re-derives the
manifest's internal invariants. It exits non-zero the moment any of them drift.

Usage:
    python3 scripts/certification/verify-fixture-manifest.py
    python3 scripts/certification/verify-fixture-manifest.py --root /path/to/repo
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

MANIFEST_RELATIVE_PATH = "docs/gis/data/client-certification-fixture.v1.json"
MATRIX_RELATIVE_PATH = "docs/gis/data/client-certification-matrix.v1.json"
MANIFEST_ID = "honua.client-certification-fixture/v1"
TRACKING_PREFIX = "https://github.com/honua-io/"
DIGEST_RE = re.compile(r"^sha256:[0-9a-f]{64}$")
ROOT_MARKER = "Honua.sln"

# Python-literal forms the canonical-fixture projection is allowed to use.
_STRING_RE = re.compile(r'^(?:"((?:[^"\\]|\\.)*)"|\'((?:[^\'\\]|\\.)*)\')$')
_NUMBER_RE = re.compile(r"^[+-]?(?:\d+\.\d*|\.\d+|\d+)(?:[eE][+-]?\d+)?$")
_IDENTIFIER_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")


# ---------------------------------------------------------------------------
# digest algorithms
# ---------------------------------------------------------------------------

def file_digest(path: Path) -> str:
    """honua.file-digest/v1: SHA-256 over the raw bytes of one file."""
    return "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest()


def input_set_digest(entries) -> str:
    """honua.input-set-digest/v1: composite digest over an ordered input set.

    ``entries`` is an iterable of ``(repo_relative_path, "sha256:<hex>")`` pairs.
    They are sorted ascending by the UTF-8 bytes of the path and rendered as
    ``"{hex}  {path}\\n"`` — byte-identical to GNU ``sha256sum`` output — before a
    final SHA-256 pass, so the value reproduces as
    ``sha256sum <paths in sorted order> | sha256sum``.
    """
    rendered = []
    for path, digest in sorted(entries, key=lambda entry: entry[0].encode("utf-8")):
        if not DIGEST_RE.match(digest):
            raise ValueError(f"not a sha256 digest for {path}: {digest!r}")
        rendered.append(f"{digest.split(':', 1)[1]}  {path}\n")
    return "sha256:" + hashlib.sha256("".join(rendered).encode("utf-8")).hexdigest()


def canonical_json(value) -> str:
    """honua.canonical-json-digest/v1 serialization.

    Object members sorted by name, no insignificant whitespace, non-ASCII emitted
    literally. Numbers are rejected so the digest never depends on a numeric
    formatter — the auth-policy object is strings, booleans, arrays, and objects.
    """
    _reject_numbers(value, "authPolicy")
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def canonical_json_digest(value) -> str:
    return "sha256:" + hashlib.sha256(canonical_json(value).encode("utf-8")).hexdigest()


def _reject_numbers(value, path: str) -> None:
    if isinstance(value, bool):
        return
    if isinstance(value, (int, float)):
        raise ValueError(f"{path}: numbers are not permitted in a canonically digested object")
    if isinstance(value, dict):
        for key, item in value.items():
            _reject_numbers(item, f"{path}.{key}")
    elif isinstance(value, list):
        for index, item in enumerate(value):
            _reject_numbers(item, f"{path}[{index}]")


# ---------------------------------------------------------------------------
# python projection parsing
# ---------------------------------------------------------------------------

def parse_python_constants(source: str) -> tuple[dict, dict]:
    """Extract ``NAME = <literal>`` and ``NAME = OTHER_NAME`` from a module.

    Returns ``(literals, aliases)``. Only flat literals are understood: strings,
    numbers, and parenthesized tuples of them. Anything else (calls, paths,
    attribute access) is ignored rather than guessed at.
    """
    literals: dict[str, object] = {}
    aliases: dict[str, str] = {}
    for match in re.finditer(r"^([A-Z][A-Z0-9_]*)\s*=\s*", source, re.MULTILINE):
        name = match.group(1)
        raw = _read_value(source, match.end())
        if raw is None:
            continue
        stripped = raw.strip()
        if _IDENTIFIER_RE.match(stripped):
            aliases[name] = stripped
            continue
        try:
            literals[name] = parse_python_literal(stripped)
        except ValueError:
            continue
    return literals, aliases


def _read_value(source: str, start: int) -> str | None:
    """Read one right-hand side: to end of line, or to the balanced ')'."""
    if start >= len(source):
        return None
    if source[start] == "(":
        depth = 0
        index = start
        while index < len(source):
            char = source[index]
            if char in "\"'":
                index = _skip_string(source, index)
                continue
            if char == "(":
                depth += 1
            elif char == ")":
                depth -= 1
                if depth == 0:
                    return source[start:index + 1]
            index += 1
        return None
    end = source.find("\n", start)
    return source[start:] if end < 0 else source[start:end]


def _skip_string(source: str, index: int) -> int:
    quote = source[index]
    index += 1
    while index < len(source):
        if source[index] == "\\":
            index += 2
            continue
        if source[index] == quote:
            return index + 1
        index += 1
    return index


def parse_python_literal(raw: str):
    text = raw.strip()
    if text.startswith("(") and text.endswith(")"):
        return [parse_python_literal(part) for part in _split_tuple(text[1:-1])]
    text = _strip_comment(text)
    match = _STRING_RE.match(text)
    if match:
        quoted = match.group(1) if match.group(1) is not None else match.group(2)
        return json.loads('"' + quoted.replace('\\\'', "'").replace('"', '\\"') + '"')
    if _NUMBER_RE.match(text):
        return float(text) if any(char in text for char in ".eE") else int(text)
    raise ValueError(f"unsupported literal: {raw!r}")


def _split_tuple(body: str) -> list[str]:
    parts: list[str] = []
    depth = 0
    current = ""
    index = 0
    while index < len(body):
        char = body[index]
        if char in "\"'":
            end = _skip_string(body, index)
            current += body[index:end]
            index = end
            continue
        if char == "#":
            end = body.find("\n", index)
            index = len(body) if end < 0 else end
            continue
        if char in "([":
            depth += 1
        elif char in ")]":
            depth -= 1
        if char == "," and depth == 0:
            parts.append(current)
            current = ""
            index += 1
            continue
        current += char
        index += 1
    parts.append(current)
    return [part for part in (item.strip() for item in parts) if part]


def _strip_comment(text: str) -> str:
    index = 0
    while index < len(text):
        char = text[index]
        if char in "\"'":
            index = _skip_string(text, index)
            continue
        if char == "#":
            return text[:index].strip()
        index += 1
    return text.strip()


def values_match(expected, actual) -> bool:
    if isinstance(expected, list) or isinstance(actual, list):
        if not isinstance(expected, list) or not isinstance(actual, list):
            return False
        return len(expected) == len(actual) and all(
            values_match(left, right) for left, right in zip(expected, actual))
    if isinstance(expected, bool) or isinstance(actual, bool):
        return expected is actual
    if isinstance(expected, (int, float)) and isinstance(actual, (int, float)):
        return abs(float(expected) - float(actual)) <= 1e-12
    return expected == actual


# ---------------------------------------------------------------------------
# verification
# ---------------------------------------------------------------------------

def repository_root(start: Path) -> Path:
    current = start.resolve()
    for candidate in [current, *current.parents]:
        if (candidate / ROOT_MARKER).is_file():
            return candidate
    raise SystemExit(f"unable to locate {ROOT_MARKER} above {start}")


def role_entries(manifest: dict, role: str):
    return [(entry["path"], entry["sha256"])
            for entry in manifest["inputs"] if entry["role"] == role]


def verify(manifest: dict, root: Path) -> list[str]:
    problems: list[str] = []
    problems += verify_identity(manifest)
    problems += verify_inputs(manifest, root)
    problems += verify_revisions(manifest)
    problems += verify_case_graph(manifest)
    problems += verify_lane_coverage(manifest, root)
    problems += verify_auth_profiles(manifest)
    problems += verify_gaps(manifest)
    problems += verify_python_projection(manifest, root)
    return problems


def verify_identity(manifest: dict) -> list[str]:
    problems = []
    if manifest.get("manifestId") != MANIFEST_ID:
        problems.append(f"manifestId must be {MANIFEST_ID}")
    if manifest.get("targetRelease") != "2026.1":
        problems.append("targetRelease must be 2026.1")
    for field in ("fixtureRevision", "serverConfigRevision", "authPolicyRevision"):
        if not DIGEST_RE.match(manifest.get(field, "")):
            problems.append(f"{field} must be a sha256: digest")
    if not manifest.get("outOfScope", {}).get("issue", "").startswith(TRACKING_PREFIX):
        problems.append("outOfScope.issue must point at a honua-io tracking issue")
    return problems


def verify_inputs(manifest: dict, root: Path) -> list[str]:
    problems = []
    seen = set()
    for entry in manifest["inputs"]:
        path = entry["path"]
        if path in seen:
            problems.append(f"inputs: duplicate path {path}")
        seen.add(path)
        if not DIGEST_RE.match(entry["sha256"]):
            problems.append(f"inputs: {path} digest is not a sha256: digest")
            continue
        absolute = root / path
        if not absolute.is_file():
            problems.append(f"inputs: {path} does not exist")
            continue
        actual = file_digest(absolute)
        if actual != entry["sha256"]:
            problems.append(
                f"inputs: {path} digest drift; manifest={entry['sha256']} actual={actual}")
    if not role_entries(manifest, "fixture"):
        problems.append("inputs: no fixture-role input declared")
    if not role_entries(manifest, "server-config"):
        problems.append("inputs: no server-config-role input declared")
    return problems


def verify_revisions(manifest: dict) -> list[str]:
    problems = []
    expected_fixture = input_set_digest(role_entries(manifest, "fixture"))
    if expected_fixture != manifest["fixtureRevision"]:
        problems.append(
            f"fixtureRevision drift; manifest={manifest['fixtureRevision']} recomputed={expected_fixture}")
    expected_config = input_set_digest(role_entries(manifest, "server-config"))
    if expected_config != manifest["serverConfigRevision"]:
        problems.append(
            f"serverConfigRevision drift; manifest={manifest['serverConfigRevision']} recomputed={expected_config}")
    try:
        expected_auth = canonical_json_digest(manifest["authPolicy"])
    except ValueError as error:
        return problems + [f"authPolicy is not canonically digestible: {error}"]
    if expected_auth != manifest["authPolicyRevision"]:
        problems.append(
            f"authPolicyRevision drift; manifest={manifest['authPolicyRevision']} recomputed={expected_auth}")

    current = manifest["receiptBindings"]["currentValues"]
    by_path = {entry["path"]: entry["sha256"] for entry in manifest["inputs"]}
    for field, expected_source in (
        ("fixture_revision", "tests/seed/client-compat-v1.sql"),
        ("server_config_revision", "tests/config/client-compat-server-v1.json"),
    ):
        recorded = current[field]["value"]
        if by_path.get(expected_source) != recorded:
            problems.append(
                f"receiptBindings.currentValues.{field} must equal the {expected_source} input digest")
    if current["auth_policy_revision"]["value"] != manifest["authPolicyRevision"]:
        problems.append("receiptBindings.currentValues.auth_policy_revision must equal authPolicyRevision")
    return problems


def verify_case_graph(manifest: dict) -> list[str]:
    problems = []
    facets = {entry["id"] for entry in manifest["scenarioFacets"]}
    cases = {}
    for entry in manifest["cases"]:
        if entry["id"] in cases:
            problems.append(f"cases: duplicate case id {entry['id']}")
        cases[entry["id"]] = entry["scenarioFacetId"]
        if entry["scenarioFacetId"] not in facets:
            problems.append(f"cases: {entry['id']} references unknown facet {entry['scenarioFacetId']}")
        if not entry.get("description"):
            problems.append(f"cases: {entry['id']} has no description")

    reasons = {entry["id"] for entry in manifest["notApplicableReasons"]}
    for entry in manifest["notApplicableReasons"]:
        if not entry.get("reason"):
            problems.append(f"notApplicableReasons: {entry['id']} has no reason")

    referenced: set[str] = set()
    for lane in manifest["laneBindings"]:
        for binding in lane["protocols"]:
            applicable = set(binding["applicableCases"])
            not_applicable: set[str] = set()
            for reason, reason_cases in binding["notApplicableCases"].items():
                if reason not in reasons:
                    problems.append(
                        f"{lane['laneId']}/{binding['protocol']}: ungoverned not-applicable reason {reason}")
                not_applicable.update(reason_cases)
            overlap = applicable & not_applicable
            if overlap:
                problems.append(
                    f"{lane['laneId']}/{binding['protocol']}: {sorted(overlap)} are both applicable and not-applicable")
            if not applicable:
                problems.append(
                    f"{lane['laneId']}/{binding['protocol']}: every case excused; that is a placeholder binding")
            unknown = (applicable | not_applicable | set(binding.get("extensionCases", []))) - set(cases)
            if unknown:
                problems.append(
                    f"{lane['laneId']}/{binding['protocol']}: unknown case ids {sorted(unknown)}")
            referenced |= applicable | not_applicable | set(binding.get("extensionCases", []))

    for entry in manifest["unboundCases"]:
        if entry["caseId"] not in cases:
            problems.append(f"unboundCases: unknown case id {entry['caseId']}")
        if entry["caseId"] in referenced:
            problems.append(f"unboundCases: {entry['caseId']} is bound by a lane after all")
        if not entry.get("reason"):
            problems.append(f"unboundCases: {entry['caseId']} has no reason")
        if not entry.get("trackingIssue", "").startswith(TRACKING_PREFIX):
            problems.append(f"unboundCases: {entry['caseId']} needs a honua-io tracking issue")

    unbound = {entry["caseId"] for entry in manifest["unboundCases"]}
    orphaned = set(cases) - referenced - unbound
    if orphaned:
        problems.append(
            f"cases with no lane binding and no governed unbound reason: {sorted(orphaned)}")
    return problems


def verify_lane_coverage(manifest: dict, root: Path) -> list[str]:
    problems = []
    bound = {lane["laneId"]: lane for lane in manifest["laneBindings"]}
    matrix_path = root / MATRIX_RELATIVE_PATH
    if not matrix_path.is_file():
        return problems + [f"{MATRIX_RELATIVE_PATH} is missing"]
    matrix = json.loads(matrix_path.read_text(encoding="utf-8"))
    for lane in matrix["lanes"]:
        lane_id = lane["id"]
        entry = bound.get(lane_id)
        if entry is None:
            problems.append(f"active lane {lane_id} has no fixture projection in the manifest")
            continue
        if not entry["protocols"]:
            problems.append(f"active lane {lane_id} declares no protocol binding")
        for binding in entry["protocols"]:
            if not binding.get("fixtureProjection"):
                problems.append(f"{lane_id}/{binding['protocol']} has no fixture projection")
    return problems


def verify_auth_profiles(manifest: dict) -> list[str]:
    problems = []
    required = {
        "anonymous", "valid-credential", "invalid-credential", "expired-credential",
        "insufficient-role-or-scope", "cross-tenant-denial", "separate-proposer-approver",
        "licensed-entitlement",
    }
    gaps = {entry["id"] for entry in manifest["gaps"]}
    inputs = {entry["path"] for entry in manifest["inputs"]}
    declared = set()
    for profile in manifest["authPolicy"]["profiles"]:
        profile_id = profile["id"]
        declared.add(profile_id)
        status = profile.get("status")
        if status in ("realized", "realized-not-asserted"):
            fixtures = profile.get("realizedByFixture") or []
            if not fixtures:
                problems.append(f"auth profile {profile_id} claims realization with no fixture")
            for fixture in fixtures:
                if fixture not in inputs:
                    problems.append(
                        f"auth profile {profile_id} names fixture {fixture}, which is not a manifest input")
            config = profile.get("realizedByServerConfig")
            if config not in inputs:
                problems.append(
                    f"auth profile {profile_id} names server config {config}, which is not a manifest input")
        if status in ("gap", "realized-not-asserted"):
            gap_id = profile.get("gapId")
            if gap_id not in gaps:
                problems.append(
                    f"auth profile {profile_id} has status {status} but no matching gaps[] entry")
        if status not in ("realized", "realized-not-asserted", "gap"):
            problems.append(f"auth profile {profile_id} has unknown status {status!r}")
    missing = required - declared
    if missing:
        problems.append(f"auth profiles missing from the manifest: {sorted(missing)}")
    try:
        _reject_numbers(manifest["authPolicy"], "authPolicy")
    except ValueError as error:
        problems.append(str(error))
    return problems


def verify_gaps(manifest: dict) -> list[str]:
    problems = []
    seen = set()
    for gap in manifest["gaps"]:
        gap_id = gap.get("id", "")
        if gap_id in seen:
            problems.append(f"gaps: duplicate id {gap_id}")
        seen.add(gap_id)
        if not gap.get("reason"):
            problems.append(f"gaps: {gap_id} has no reason")
        if not gap.get("trackingIssue", "").startswith(TRACKING_PREFIX):
            problems.append(f"gaps: {gap_id} needs a {TRACKING_PREFIX} tracking issue")
    referenced = set()
    for section in ("vectorSeedCoverage",):
        for entry in manifest[section]:
            if entry.get("gapId"):
                referenced.add(entry["gapId"])
    for entry in manifest["supportingFixtures"].values():
        if entry.get("gapId"):
            referenced.add(entry["gapId"])
    for profile in manifest["authPolicy"]["profiles"]:
        if profile.get("gapId"):
            referenced.add(profile["gapId"])
    unknown = referenced - seen
    if unknown:
        problems.append(f"gapId references with no gaps[] entry: {sorted(unknown)}")
    return problems


def verify_python_projection(manifest: dict, root: Path) -> list[str]:
    problems = []
    projection = manifest["pythonProjection"]
    path = root / projection["path"]
    if not path.is_file():
        return [f"pythonProjection: {projection['path']} does not exist"]
    literals, aliases = parse_python_constants(path.read_text(encoding="utf-8"))
    for name, expected in projection["symbols"].items():
        if name not in literals:
            problems.append(f"pythonProjection: {name} is not defined as a literal in {projection['path']}")
            continue
        if not values_match(expected, literals[name]):
            problems.append(
                f"pythonProjection: {name} drift; manifest={expected!r} python={literals[name]!r}")
    for name, target in projection.get("aliases", {}).items():
        if aliases.get(name) != target:
            problems.append(
                f"pythonProjection: {name} must alias {target}; found {aliases.get(name)!r}")
    return problems


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=None,
                        help="repository root (defaults to the tree containing this script)")
    parser.add_argument("--manifest", type=Path, default=None,
                        help=f"manifest path (defaults to <root>/{MANIFEST_RELATIVE_PATH})")
    parser.add_argument("--quiet", action="store_true", help="print nothing on success")
    arguments = parser.parse_args(argv)

    root = arguments.root or repository_root(Path(__file__).parent)
    manifest_path = arguments.manifest or (root / MANIFEST_RELATIVE_PATH)
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        print(f"FAIL {manifest_path} does not exist", file=sys.stderr)
        return 2
    except json.JSONDecodeError as error:
        print(f"FAIL {manifest_path} is not valid JSON: {error}", file=sys.stderr)
        return 2

    try:
        problems = verify(manifest, root)
    except KeyError as error:
        print(f"FAIL manifest is missing required member {error}", file=sys.stderr)
        return 2

    if problems:
        print(f"FAIL {len(problems)} fixture-manifest problem(s):", file=sys.stderr)
        for problem in problems:
            print(f"  - {problem}", file=sys.stderr)
        return 1

    if not arguments.quiet:
        print(f"OK   {MANIFEST_RELATIVE_PATH}")
        print(f"     fixtureRevision      {manifest['fixtureRevision']}")
        print(f"     serverConfigRevision {manifest['serverConfigRevision']}")
        print(f"     authPolicyRevision   {manifest['authPolicyRevision']}")
        print(f"     inputs {len(manifest['inputs'])}, lanes {len(manifest['laneBindings'])}, "
              f"cases {len(manifest['cases'])}, gaps {len(manifest['gaps'])}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
