#!/usr/bin/env python3
"""Emit the OpenAPI operation -> capability join instead of discarding it.

`openapi-drift-check.py` already resolves every spec's server prefix and
normalizes every route so it can compare specs against the endpoint registry.
That comparison establishes, for all 491 documented operations across eight
OpenAPI documents, exactly which route each one is — and `feature-catalog.json`
stamps a capability on every route. The join has therefore been computed on
every CI run and thrown away every time (G6 of the OKF knowledge-graph program).

Discarding it costs two things:

  * Nothing machine-readable answers "which capability does this endpoint
    belong to", so an agent holding an operationId cannot find the capability
    that governs it, and a capability cannot enumerate its own surface.
  * The correspondence is unguarded. A route that silently changes capability
    shows up nowhere, because the only artifact that knew was a local variable.

This script imports the drift checker rather than reimplementing its
normalization. Two normalizers that disagree would be worse than none: the join
would look green while matching different routes than the gate it claims to
extend.

Usage:
    python3 scripts/ci/generate-operation-capability.py            # write
    python3 scripts/ci/generate-operation-capability.py --check    # drift gate
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import sys
from collections import Counter
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
DRIFT_CHECK = REPO_ROOT / "scripts" / "ci" / "openapi-drift-check.py"
CATALOG = REPO_ROOT / "docs" / "gis" / "data" / "feature-catalog.json"
CAPABILITY_KEYS = REPO_ROOT / "docs" / "gis" / "data" / "capability-keys.v1.json"
OUTPUT = REPO_ROOT / "docs" / "gis" / "data" / "operation-capability.v1.json"


def known_capability_keys() -> set[str]:
    """The published capability vocabulary this join must resolve against."""
    document = json.loads(CAPABILITY_KEYS.read_text(encoding="utf-8"))
    capabilities = document.get("capabilities")
    if isinstance(capabilities, dict):
        return set(capabilities)
    if isinstance(capabilities, list):
        keys = set()
        for item in capabilities:
            if isinstance(item, dict):
                key = item.get("key") or item.get("id")
                if key:
                    keys.add(key)
            elif isinstance(item, str):
                keys.add(item)
        return keys
    return set()


def load_drift_module():
    """Import openapi-drift-check.py so the join uses its exact route semantics."""
    spec = importlib.util.spec_from_file_location("openapi_drift_check", DRIFT_CHECK)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"cannot load {DRIFT_CHECK}")
    module = importlib.util.module_from_spec(spec)
    # Registered before exec because the module's frozen dataclasses resolve
    # their __module__ through sys.modules during class construction.
    sys.modules["openapi_drift_check"] = module
    spec.loader.exec_module(module)
    return module


def build() -> dict:
    drift = load_drift_module()
    entries = json.loads(CATALOG.read_text(encoding="utf-8"))["entries"]

    by_route: dict[tuple[str, str], dict] = {}
    for entry in entries:
        key = (entry["method"].lower(), drift.normalize_route(entry["route"]))
        by_route.setdefault(key, entry)

    operations: list[dict] = []
    unresolved: list[dict] = []
    for spec_config in drift.SPECS:
        document = drift.load_spec(spec_config)
        prefix = drift.resolve_prefix(spec_config, document)
        for path, method, operation_id, _operation in drift.iter_spec_operations(document, prefix):
            entry = by_route.get((method, path))
            record = {
                "spec": spec_config.name,
                "method": method.upper(),
                "path": path,
                "operationId": operation_id or None,
            }
            if entry is None:
                unresolved.append(record)
                continue
            record.update(
                {
                    "capability": entry["capability"],
                    "family": entry.get("family"),
                    "maturity": entry.get("maturity"),
                    "catalogEntryId": entry.get("id"),
                }
            )
            operations.append(record)

    operations.sort(key=lambda r: (r["spec"], r["path"], r["method"]))
    unresolved.sort(key=lambda r: (r["spec"], r["path"], r["method"]))

    per_capability = Counter(r["capability"] for r in operations)
    per_spec = Counter(r["spec"] for r in operations)

    return {
        "schemaVersion": "1.0.0",
        "generator": "scripts/ci/generate-operation-capability.py",
        "description": (
            "Every documented OpenAPI operation joined to the capability that governs it. "
            "Routes are resolved with openapi-drift-check.py's own prefix and normalization "
            "logic; capabilities come from feature-catalog.json, which stamps one on every "
            "registered route. Regenerate with the script; the committed copy is drift-gated."
        ),
        "sources": {
            "operations": "OpenAPI documents declared in scripts/ci/openapi-drift-check.py SPECS",
            "capabilities": "docs/gis/data/feature-catalog.json",
        },
        "summary": {
            "operationCount": len(operations),
            "unresolvedCount": len(unresolved),
            "capabilityCount": len(per_capability),
            "perSpec": dict(sorted(per_spec.items())),
            "perCapability": dict(sorted(per_capability.items())),
        },
        "operations": operations,
        # An operation the catalog cannot place. Empty today; kept in the shape
        # so a future spec addition that outruns the catalog is visible in the
        # artifact rather than silently absent from it.
        "unresolved": unresolved,
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="fail if the committed artifact is stale")
    args = parser.parse_args(argv)

    payload = build()

    # An edge that points at nothing is not an edge. Every capability this join
    # names must resolve in the published registry, which is the same invariant
    # WS1 is unifying the five id namespaces to guarantee everywhere else.
    known = known_capability_keys()
    unresolvable = sorted(set(payload["summary"]["perCapability"]) - known)
    if unresolvable:
        print(
            "::error::operation-capability join names capabilities that resolve in no "
            f"published catalog: {', '.join(unresolvable)}. Register them in "
            "capability-keys.v1.json or correct the route mapping.",
            file=sys.stderr,
        )
        return 1

    rendered = json.dumps(payload, indent=2, ensure_ascii=False) + "\n"

    if args.check:
        if not OUTPUT.exists():
            print(f"::error::{OUTPUT.relative_to(REPO_ROOT)} is missing. Run "
                  "'python3 scripts/ci/generate-operation-capability.py' and commit the result.",
                  file=sys.stderr)
            return 1
        if OUTPUT.read_text(encoding="utf-8") != rendered:
            print(f"::error::{OUTPUT.relative_to(REPO_ROOT)} is stale. Run "
                  "'python3 scripts/ci/generate-operation-capability.py' and commit the result.",
                  file=sys.stderr)
            return 1
        print(
            f"operation-capability.v1.json is current: {payload['summary']['operationCount']} "
            f"operation(s) across {payload['summary']['capabilityCount']} capabilities."
        )
        return 0

    OUTPUT.write_text(rendered, encoding="utf-8")
    print(
        f"Wrote {OUTPUT.relative_to(REPO_ROOT)} "
        f"({payload['summary']['operationCount']} operations, "
        f"{payload['summary']['capabilityCount']} capabilities, "
        f"{payload['summary']['unresolvedCount']} unresolved)."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
