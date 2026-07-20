#!/usr/bin/env python3
"""Capability-graph CI impact comparison and evidence freshness reporting.

ADR-0037 remains authoritative. This tool emits a parallel, report-only
selection so its recall can be observed before it is allowed to select tests.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CATALOG = ROOT / "docs/gis/data/feature-catalog.json"
KEYS = ROOT / "docs/gis/data/capability-keys.v1.json"
SHARDS = ROOT / ".github/ci-shards.json"
ALLOWLIST = ROOT / ".github/capability-impact-allowlist.json"


class FilterParser:
    """Evaluate the FullyQualifiedName subset used by ci-shards.json."""

    _tokens = re.compile(r"\s*(\(|\)|\&|\||FullyQualifiedName(?:!~|~|!=|=)[^&|()]+)")

    def __init__(self, expression: str, test_name: str):
        self.tokens = [item.strip() for item in self._tokens.findall(expression)]
        self.index = 0
        self.test_name = test_name

    def evaluate(self) -> bool:
        value = self._or_expression()
        if self.index != len(self.tokens):
            raise ValueError(f"unsupported shard filter near {self.tokens[self.index:]}")
        return value

    def _or_expression(self) -> bool:
        value = self._and_expression()
        while self._accept("|"):
            right = self._and_expression()
            value = value or right
        return value

    def _and_expression(self) -> bool:
        value = self._primary()
        while self._accept("&"):
            right = self._primary()
            value = value and right
        return value

    def _primary(self) -> bool:
        if self._accept("("):
            value = self._or_expression()
            self._expect(")")
            return value
        if self.index >= len(self.tokens):
            raise ValueError("incomplete shard filter")
        predicate = self.tokens[self.index]
        self.index += 1
        match = re.fullmatch(r"FullyQualifiedName(!~|~|!=|=)(.+)", predicate)
        if not match:
            raise ValueError(f"unsupported shard predicate: {predicate}")
        operator, expected = match.groups()
        if operator == "~":
            return expected in self.test_name
        if operator == "!~":
            return expected not in self.test_name
        if operator == "=":
            return expected == self.test_name
        return expected != self.test_name

    def _accept(self, token: str) -> bool:
        if self.index < len(self.tokens) and self.tokens[self.index] == token:
            self.index += 1
            return True
        return False

    def _expect(self, token: str) -> None:
        if not self._accept(token):
            raise ValueError(f"expected {token!r} in shard filter")


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def shard_names_for_test(test_name: str, config: dict) -> list[str]:
    return sorted(
        shard["name"]
        for shard in config["shards"]
        if FilterParser(shard["filter"], test_name).evaluate()
    )


def validate_graph(catalog: dict, keys: dict, config: dict, allowlist: dict) -> list[str]:
    errors: list[str] = []
    capabilities = {item["key"] for item in keys["capabilities"]}
    test_exceptions = allowlist.get("provingTests", [])
    allowed_tests = {
        row.get("test") if isinstance(row, dict) else row
        for row in test_exceptions
    }
    for row in test_exceptions:
        if not isinstance(row, dict) or not row.get("test") or not row.get("reason") or not row.get("issue"):
            errors.append(f"invalid proving-test exception (test, reason, and issue are required): {row!r}")
    allowed_families = set(allowlist.get("routeFamilies", []))
    seen_tests: set[str] = set()

    for entry in catalog["entries"]:
        identity = f"{entry.get('method', '?')} {entry.get('route', '?')}"
        capability = entry.get("capability")
        if capability not in capabilities:
            errors.append(f"{identity}: missing or unknown capability {capability!r}")
        family = entry.get("family")
        if not family and identity not in allowed_families:
            errors.append(f"{identity}: route has no family mapping")
        for test_name in entry.get("proving_tests", []):
            seen_tests.add(test_name)
            if test_name in allowed_tests:
                continue
            if not shard_names_for_test(test_name, config):
                errors.append(f"{identity}: proving test is outside every CI shard: {test_name}")

    unknown_test_exceptions = allowed_tests - seen_tests
    if unknown_test_exceptions:
        errors.append("stale proving-test allowlist entries: " + ", ".join(sorted(unknown_test_exceptions)))

    families = {entry.get("family") for entry in catalog["entries"]}
    stale_family_exceptions = allowed_families - families
    if stale_family_exceptions:
        errors.append("stale route-family allowlist entries: " + ", ".join(sorted(stale_family_exceptions)))

    for row in keys.get("crosswalks", {}).get("interop", []):
        if row.get("capability") not in capabilities:
            errors.append(f"interop crosswalk has unknown capability: {row}")
    return errors


def is_source_path(path: str, config: dict) -> bool:
    prefixes = config.get("unmapped_source_run_all_prefixes", [])
    return any(path.startswith(prefix) for prefix in prefixes)


def affected_entries(changed_files: list[str], entries: list[dict]) -> list[dict]:
    changed = {item.replace("\\", "/") for item in changed_files if item.strip()}
    test_classes = {
        Path(path).stem
        for path in changed
        if path.startswith("tests/") and path.endswith(".cs")
    }
    return [
        entry
        for entry in entries
        if entry.get("code_location", "").replace("\\", "/") in changed
        or any(f".{class_name}." in test for class_name in test_classes for test in entry.get("proving_tests", []))
    ]


def load_envelopes(root: Path | None) -> dict[tuple[str, str], dict]:
    if root is None or not root.exists():
        return {}
    envelopes: dict[tuple[str, str], dict] = {}
    for path in root.rglob("*.cert.json"):
        try:
            envelope = load_json(path)
        except (OSError, json.JSONDecodeError):
            continue
        lane = envelope.get("client_lane") or envelope.get("clientLane")
        protocol = envelope.get("protocol")
        if lane and protocol:
            envelopes[(lane, protocol)] = envelope
    return envelopes


def parse_timestamp(envelope: dict) -> dt.datetime | None:
    for field in ("generated_at", "generatedAt", "timestamp", "run_at", "runAt"):
        value = envelope.get(field)
        if not value:
            continue
        try:
            return dt.datetime.fromisoformat(str(value).replace("Z", "+00:00"))
        except ValueError:
            pass
    return None


def is_green(envelope: dict) -> bool:
    summary = envelope.get("summary") or {}
    if int(summary.get("failed", summary.get("fail", 0)) or 0) > 0:
        return False
    statuses = [str(item.get("status", "")).lower() for item in envelope.get("results", [])]
    return bool(statuses) and not any(status in {"fail", "failed", "error"} for status in statuses)


def build_report(changed_files: list[str], legacy: dict, envelope_root: Path | None, labels: list[str]) -> dict:
    catalog = load_json(CATALOG)
    keys = load_json(KEYS)
    config = load_json(SHARDS)
    entries = affected_entries(changed_files, catalog["entries"])
    capabilities = sorted({entry["capability"] for entry in entries})
    tests = sorted({test for entry in entries for test in entry.get("proving_tests", [])})
    shards = sorted({name for test in tests for name in shard_names_for_test(test, config)})
    unmatched_source = sorted(path for path in changed_files if is_source_path(path, config) and not entries)
    run_all = bool(unmatched_source)
    if run_all:
        shards = sorted(item["name"] for item in config["shards"])

    interop = [
        {"clientLane": row["clientLane"], "protocol": row["protocol"]}
        for row in keys.get("crosswalks", {}).get("interop", [])
        if row["capability"] in capabilities
    ]
    interop = sorted(interop, key=lambda row: (row["clientLane"], row["protocol"]))
    envelopes = load_envelopes(envelope_root)
    now = dt.datetime.now(dt.timezone.utc)
    freshness = []
    for row in keys.get("crosswalks", {}).get("interop", []):
        key = (row["clientLane"], row["protocol"])
        envelope = envelopes.get(key)
        timestamp = parse_timestamp(envelope or {})
        age_days = (now - timestamp.astimezone(dt.timezone.utc)).days if timestamp else None
        freshness.append(
            {
                "capability": row["capability"],
                "clientLane": key[0],
                "protocol": key[1],
                "green": bool(envelope and is_green(envelope)),
                "ageDays": age_days,
                "stale": not envelope or not is_green(envelope) or age_days is None or age_days > 14,
            }
        )

    legacy_shards = set(legacy.get("shards", []))
    graph_shards = set(shards)
    return {
        "schemaVersion": 1,
        "mode": "report-only",
        "changedFileCount": len(changed_files),
        "capabilityLabels": sorted(label for label in labels if label.startswith("cap/")),
        "legacy": legacy,
        "capabilitySelection": {
            "runAll": run_all,
            "reason": "unmapped_graph_source" if run_all else ("capability_match" if entries else "no_capability_match"),
            "capabilities": capabilities,
            "provingTestCount": len(tests),
            "shards": shards,
            "interopLanes": interop,
            "unmatchedSourceFiles": unmatched_source,
        },
        "comparison": {
            "legacyShardCount": len(legacy_shards),
            "capabilityShardCount": len(graph_shards),
            "legacyOnlyShards": sorted(legacy_shards - graph_shards),
            "capabilityOnlyShards": sorted(graph_shards - legacy_shards),
            "escapedDefectCandidates": sorted(legacy_shards - graph_shards),
        },
        "freshness": freshness,
    }


def markdown(report: dict) -> str:
    selection = report["capabilitySelection"]
    comparison = report["comparison"]
    stale = [row for row in report["freshness"] if row["stale"]]
    lines = [
        "## Capability impact comparison (report-only)",
        "",
        "> ADR-0037 remains authoritative; this report does not reduce the executed shard set.",
        "",
        f"- Changed files: {report['changedFileCount']}",
        f"- Capability selector: {len(selection['capabilities'])} capabilities, {selection['provingTestCount']} proving tests, {len(selection['shards'])} shards",
        f"- ADR-0037 selector: {comparison['legacyShardCount']} shards",
        f"- Potential escaped-defect candidates: {len(comparison['escapedDefectCandidates'])}",
        f"- Suggested interop protocol pairs: {len(selection['interopLanes'])}",
        f"- Stale or unknown interop evidence pairs (>14 days): {len(stale)}",
        "",
        "### Capability-selected shards",
        "",
        ", ".join(selection["shards"]) or "None (no capability-bearing route or proving test changed).",
        "",
        "### Legacy-only shards to monitor",
        "",
        ", ".join(comparison["legacyOnlyShards"]) or "None.",
        "",
        "### Interop selection semantics",
        "",
        "Only the listed `(clientLane, protocol)` pairs are asserted. A selected lane may emit zero envelopes for protocols not mapped to the touched capabilities; those absences are not regressions.",
    ]
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("validate")
    select = subparsers.add_parser("select")
    select.add_argument("--changed-files", type=Path, required=True)
    select.add_argument("--legacy", type=Path, required=True)
    select.add_argument("--envelopes", type=Path)
    select.add_argument("--labels-json", default="[]")
    select.add_argument("--markdown", type=Path)
    args = parser.parse_args()

    if args.command == "validate":
        errors = validate_graph(load_json(CATALOG), load_json(KEYS), load_json(SHARDS), load_json(ALLOWLIST))
        if errors:
            for error in errors:
                print(f"ERROR: {error}", file=sys.stderr)
            return 1
        print("Capability impact completeness: valid")
        return 0

    changed_files = [line.strip().replace("\\", "/") for line in args.changed_files.read_text().splitlines() if line.strip()]
    labels_value = json.loads(args.labels_json or "[]")
    labels = [item.get("name", "") if isinstance(item, dict) else str(item) for item in labels_value]
    report = build_report(changed_files, load_json(args.legacy), args.envelopes, labels)
    print(json.dumps(report, indent=2))
    if args.markdown:
        args.markdown.write_text(markdown(report), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
