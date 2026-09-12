#!/usr/bin/env python3
"""Derive the capability-manifest crosswalk (WS1 of the OKF knowledge-graph program).

`honua.capability_manifest.v1` advertises capability ids on the wire. Licensing
uses a different vocabulary in `capability-keys.v1.json`. Nothing joined them, so
an id a client receives — `jobs.runner` in a typed refusal, say — resolved in no
published catalog and could not be looked up.

ADR-0058 says derive the mechanical half and hand-maintain only the judgement.
That splits cleanly here:

  derived   an id that *is* a capability key, or whose EntitlementKey is one
  authored  everything else, adjudicated once in
            docs/gis/data/capability-manifest-adjudication.v1.json

The gate is that every manifest id lands in exactly one of those buckets. A new
manifest id with no key and no adjudication fails the build, which is what stops
the drift this artifact exists to close from reopening.

`register` adjudications name a capability key that does not exist yet. They are
reported as unresolved and fail the gate on purpose: the entry records the
decision to add the key, and the build stays red until someone does.

Usage:
    python3 scripts/ci/generate-capability-manifest-crosswalk.py            # write
    python3 scripts/ci/generate-capability-manifest-crosswalk.py --check    # gate
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
REGISTRY_CS = REPO_ROOT / "src" / "Honua.Core" / "Features" / "Capabilities" / "CapabilityRegistry.cs"
KEYS = REPO_ROOT / "docs" / "gis" / "data" / "capability-keys.v1.json"
ADJUDICATION = REPO_ROOT / "docs" / "gis" / "data" / "capability-manifest-adjudication.v1.json"
OUTPUT = REPO_ROOT / "docs" / "gis" / "data" / "capability-manifest-crosswalk.v1.json"

# The entitlement slot is deliberately permissive. An earlier version accepted
# only `null` or a string literal, which silently skipped the nine rows passing a
# `FeatureCatalog.*Key` constant — the gate then reported "39 manifest ids, all
# accounted for" while the array held 48. A regex that drops rows is worse than
# no gate, because it reports success. `assert_complete` below is the guard that
# makes any future format change fail loudly instead.
DESCRIPTOR_RE = re.compile(
    r'\(\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*([^,]+?)\s*,\s*CapabilityKind\.(\w+)'
)
FEATURE_CATALOG_CONST_RE = re.compile(
    r'public\s+const\s+string\s+(\w+)\s*=\s*"([^"]+)"'
)
COMMENT_RE = re.compile("//[^" + chr(10) + "]*|/[*].*?[*]/", re.DOTALL)


def feature_catalog_constants() -> dict[str, str]:
    """`FeatureCatalog.XKey` -> the capability key it holds."""
    path = REPO_ROOT / "src" / "Honua.Core" / "Features" / "Licensing" / "Domain" / "FeatureCatalog.cs"
    return dict(FEATURE_CATALOG_CONST_RE.findall(path.read_text(encoding="utf-8")))


def manifest_descriptors() -> list[dict]:
    """The manifest ids, read from the registry that builds the wire surface."""
    src = REGISTRY_CS.read_text(encoding="utf-8")
    start = src.index("BuildManifestCapabilityDescriptors")
    array = src.index("capabilities =", start)
    end = src.index("];", array)
    block = COMMENT_RE.sub("", src[array:end])

    constants = feature_catalog_constants()
    rows = []
    for manifest_id, category, entitlement, kind in DESCRIPTOR_RE.findall(block):
        entitlement = entitlement.strip()
        if entitlement == "null":
            key = None
        elif entitlement.startswith('"'):
            key = entitlement.strip('"')
        elif entitlement.startswith("FeatureCatalog."):
            name = entitlement.split(".", 1)[1]
            key = constants.get(name)
            if key is None:
                raise RuntimeError(
                    f"{manifest_id}: cannot resolve entitlement constant {entitlement!r} in FeatureCatalog.cs"
                )
        else:
            raise RuntimeError(f"{manifest_id}: unrecognised entitlement expression {entitlement!r}")
        rows.append(
            {"manifestId": manifest_id, "category": category, "kind": kind, "entitlementKey": key}
        )

    # The array has one CapabilityKind. per row, so a mismatch means the pattern
    # stopped matching a row shape. Fail loudly rather than under-report.
    expected = len(re.findall(r"CapabilityKind\.", block))
    if len(rows) != expected:
        raise RuntimeError(
            f"parsed {len(rows)} manifest descriptors but the array declares {expected} rows — "
            "the descriptor pattern no longer matches every row shape"
        )
    return rows


def known_keys() -> set[str]:
    return {c["key"] for c in json.loads(KEYS.read_text(encoding="utf-8"))["capabilities"]}


def build() -> tuple[dict, list[str]]:
    descriptors = manifest_descriptors()
    keys = known_keys()
    adjudication = json.loads(ADJUDICATION.read_text(encoding="utf-8"))
    by_id = {a["manifestId"]: a for a in adjudication["adjudications"]}

    entries: list[dict] = []
    problems: list[str] = []
    seen_adjudications: set[str] = set()

    for d in descriptors:
        manifest_id = d["manifestId"]
        entry = {"manifestId": manifest_id, "category": d["category"], "kind": d["kind"]}

        if manifest_id in keys:
            entry.update(capability=manifest_id, resolution="direct")
        elif d["entitlementKey"] and d["entitlementKey"] in keys:
            entry.update(capability=d["entitlementKey"], resolution="entitlement")
        else:
            adj = by_id.get(manifest_id)
            if adj is None:
                problems.append(
                    f"manifest id {manifest_id!r} resolves to no capability key and has no entry in "
                    f"{ADJUDICATION.name}. Add one: map it, mark it for registration, or record why "
                    "it is not a licensable capability."
                )
                continue
            seen_adjudications.add(manifest_id)
            disposition = adj["disposition"]
            entry.update(
                capability=adj.get("capability"),
                resolution=disposition,
                note=adj["note"],
            )
            target = adj.get("capability")
            if disposition == "mapped" and target not in keys:
                problems.append(
                    f"{manifest_id!r} is adjudicated 'mapped' to {target!r}, "
                    "which is not a capability key"
                )
            if disposition == "declared-gap" and target is not None:
                # A declared gap has no key by definition. Naming one would
                # publish an absent behaviour against a served capability,
                # which is the error this bucket exists to prevent.
                problems.append(
                    f"{manifest_id!r} is adjudicated 'declared-gap' but names capability "
                    f"{target!r}; a declared gap must name no key"
                )

            if disposition == "register":
                # `register` is a decision that a key should exist, so it stays
                # red only until someone adds it. Once the key is in the
                # registry the entry resolves like any other and the adjudication
                # becomes the record of why the key was created.
                if target in keys:
                    entry["resolution"] = "registered"
                else:
                    problems.append(
                        f"{manifest_id!r} awaits registration of capability key "
                        f"{target!r}, which does not exist yet"
                    )
        entries.append(entry)

    stale = sorted(set(by_id) - seen_adjudications)
    for manifest_id in stale:
        problems.append(
            f"{ADJUDICATION.name} adjudicates {manifest_id!r}, which is no longer a manifest id "
            "(or now resolves on its own) — remove the entry"
        )

    entries.sort(key=lambda e: e["manifestId"])
    counts = Counter(e["resolution"] for e in entries)
    payload = {
        "schemaVersion": "1.0.0",
        "generator": "scripts/ci/generate-capability-manifest-crosswalk.py",
        "trackingIssue": "https://github.com/honua-io/honua-server/issues/3607",
        "description": (
            "Joins honua.capability_manifest.v1 ids to licensing capability keys. `direct` and "
            "`entitlement` are derived from CapabilityRegistry.cs; `mapped`, `register`, "
            "`declared-gap` and `not-licensable` come from capability-manifest-adjudication.v1.json. Every manifest id "
            "appears exactly once."
        ),
        "sources": {
            "manifestIds": "src/Honua.Core/Features/Capabilities/CapabilityRegistry.cs",
            "capabilityKeys": "docs/gis/data/capability-keys.v1.json",
            "adjudication": "docs/gis/data/capability-manifest-adjudication.v1.json",
        },
        "summary": {
            "manifestIdCount": len(entries),
            "resolvedCount": sum(1 for e in entries if e.get("capability")),
            "byResolution": dict(sorted(counts.items())),
        },
        "entries": entries,
    }
    return payload, problems


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="fail if stale or unresolved")
    args = parser.parse_args(argv)

    payload, problems = build()
    rendered = json.dumps(payload, indent=2, ensure_ascii=False) + "\n"

    if problems:
        print("Capability-manifest crosswalk is incomplete:", file=sys.stderr)
        for problem in problems:
            print(f"- {problem}", file=sys.stderr)
        if not args.check:
            OUTPUT.write_bytes(rendered.encode("utf-8"))
            print(f"\nWrote {OUTPUT.name} anyway so the gap is visible in the artifact.", file=sys.stderr)
        return 1

    if args.check:
        if not OUTPUT.exists() or OUTPUT.read_text(encoding="utf-8") != rendered:
            print(
                "::error::docs/gis/data/capability-manifest-crosswalk.v1.json is stale. Run "
                "'python3 scripts/ci/generate-capability-manifest-crosswalk.py' and commit the result.",
                file=sys.stderr,
            )
            return 1
        s = payload["summary"]
        print(
            f"capability-manifest crosswalk is current: {s['manifestIdCount']} manifest id(s), "
            f"{s['resolvedCount']} resolved."
        )
        return 0

    # Written as bytes with explicit \n so a Windows run does not rewrite the
    # whole file with CRLF and show up as a no-content diff.
    OUTPUT.write_bytes(rendered.encode("utf-8"))
    s = payload["summary"]
    print(f"Wrote {OUTPUT.name} ({s['manifestIdCount']} manifest ids, {s['resolvedCount']} resolved).")
    for resolution, count in sorted(payload["summary"]["byResolution"].items()):
        print(f"  {resolution:<16} {count}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
