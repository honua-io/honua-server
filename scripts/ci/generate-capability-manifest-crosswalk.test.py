#!/usr/bin/env python3
"""Offline tests for generate-capability-manifest-crosswalk.py.

The crosswalk's value is that every manifest id is accounted for, so the tests
are about accounting rather than shape: a new id with no home has to fail, a
stale adjudication has to fail, a `mapped` entry pointing at a key that does not
exist has to fail, and `register` has to stop failing the moment the key lands.

The last cases assert the live repository is complete and that the two ids the
program singled out — `jobs.runner` and the gRPC transports — actually resolve.
"""
from __future__ import annotations

import pathlib
import tempfile
import importlib.util
import json
import sys
from contextlib import redirect_stderr, redirect_stdout
from io import StringIO
from pathlib import Path

SCRIPT = Path(__file__).with_name("generate-capability-manifest-crosswalk.py")
SPEC = importlib.util.spec_from_file_location("gen_manifest_crosswalk", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules["gen_manifest_crosswalk"] = MODULE
SPEC.loader.exec_module(MODULE)


def assert_that(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def build_quiet():
    err = StringIO()
    with redirect_stderr(err):
        payload, problems = MODULE.build()
    return payload, problems


def test_the_live_crosswalk_accounts_for_every_manifest_id():
    payload, problems = build_quiet()
    assert_that(not problems, "crosswalk is incomplete:\n" + "\n".join(problems))
    entries = payload["entries"]
    ids = [e["manifestId"] for e in entries]
    assert_that(len(ids) == len(set(ids)), "a manifest id appears more than once")
    assert_that(len(entries) == payload["summary"]["manifestIdCount"], "summary count disagrees with entries")
    for e in entries:
        assert_that(bool(e.get("resolution")), f"{e['manifestId']} has no resolution")


def test_the_ids_the_program_singled_out_resolve():
    """jobs.runner is named by live typed refusals; gRPC was the only unkeyed protocol."""
    payload, _ = build_quiet()
    by_id = {e["manifestId"]: e for e in payload["entries"]}
    jobs = by_id["jobs.runner"]
    assert_that(jobs.get("capability") == "jobs.durable-runtime", f"jobs.runner -> {jobs.get('capability')}")
    for transport in ("transport.grpc", "transport.grpc-web", "transport.native-grpc"):
        assert_that(
            by_id[transport].get("capability") == "serve.grpc",
            f"{transport} -> {by_id[transport].get('capability')}",
        )


def test_not_licensable_entries_carry_a_reason():
    payload, _ = build_quiet()
    for e in payload["entries"]:
        if e["resolution"] == "not-licensable":
            assert_that(e.get("capability") is None, f"{e['manifestId']} is not-licensable but names a capability")
            assert_that(len(e.get("note", "")) > 40, f"{e['manifestId']} has no substantive reason")


def test_an_unaccounted_manifest_id_fails():
    real = MODULE.manifest_descriptors
    MODULE.manifest_descriptors = lambda: real() + [
        {"manifestId": "sandwich.delivery", "category": "x", "kind": "Feature", "entitlementKey": None}
    ]
    try:
        _, problems = build_quiet()
        assert_that(any("sandwich.delivery" in p for p in problems), f"unaccounted id passed: {problems}")
    finally:
        MODULE.manifest_descriptors = real


def test_a_stale_adjudication_fails():
    real = MODULE.manifest_descriptors
    MODULE.manifest_descriptors = lambda: [d for d in real() if d["manifestId"] != "operate.status"]
    try:
        _, problems = build_quiet()
        assert_that(
            any("operate.status" in p and "no longer" in p for p in problems),
            f"stale adjudication passed: {problems}",
        )
    finally:
        MODULE.manifest_descriptors = real


def test_mapping_to_a_nonexistent_key_fails():
    real = MODULE.known_keys
    MODULE.known_keys = lambda: real() - {"admin.control-plane"}
    try:
        _, problems = build_quiet()
        assert_that(
            any("operate.status" in p and "not a capability key" in p for p in problems),
            f"bad mapping passed: {problems}",
        )
    finally:
        MODULE.known_keys = real


def test_a_declared_gap_may_not_name_a_capability_key():
    """The bucket exists because mapping a gap onto a served key lies twice.

    `edit.geoservices-version-tokens` and `collaboration.feature-locks.cross-node`
    are Planned registry entries the manifest publishes as supported:false. Both
    were previously mapped onto working keys, which told clients those keys were
    unavailable and hid the gap. Naming a key here must fail.
    """
    real = MODULE.ADJUDICATION
    payload = json.loads(real.read_text(encoding="utf-8"))
    for row in payload["adjudications"]:
        if row["manifestId"] == "edit.geoservices-version-tokens":
            row["capability"] = "editing.branch-versioning"
    with tempfile.TemporaryDirectory() as scratch:
        poisoned = pathlib.Path(scratch) / "adjudication.json"
        poisoned.write_text(json.dumps(payload), encoding="utf-8")
        MODULE.ADJUDICATION = poisoned
        try:
            _, problems = build_quiet()
            assert_that(
                any("declared-gap" in m and "must name no key" in m for m in problems),
                f"a declared gap naming a key passed: {problems}",
            )
        finally:
            MODULE.ADJUDICATION = real


def test_register_stays_red_until_the_key_exists():
    """Otherwise `register` is a wish rather than a decision with a deadline."""
    real = MODULE.known_keys
    MODULE.known_keys = lambda: real() - {"serve.grpc"}
    try:
        _, problems = build_quiet()
        assert_that(
            any("serve.grpc" in p and "does not exist" in p for p in problems),
            f"missing registration passed: {problems}",
        )
    finally:
        MODULE.known_keys = real
    payload, problems = build_quiet()
    assert_that(not problems, "with the key present the gate must be green")
    by_id = {e["manifestId"]: e for e in payload["entries"]}
    assert_that(by_id["transport.grpc"]["resolution"] == "registered", "resolution should become 'registered'")


def test_the_committed_artifact_is_current():
    out, err = StringIO(), StringIO()
    with redirect_stdout(out), redirect_stderr(err):
        code = MODULE.main(["--check"])
    assert_that(code == 0, f"committed crosswalk is stale:\n{err.getvalue()}{out.getvalue()}")


def test_the_parser_sees_every_row_in_the_array():
    """The gate's promise is 'every manifest id'. That is worth checking against
    the source rather than against the parser's own output.

    An earlier descriptor pattern accepted only `null` or a string literal in the
    entitlement slot and silently skipped the nine rows passing a
    `FeatureCatalog.*Key` constant: 48 rows in, 39 out, `--check` green, all
    tests passing. A parser that drops rows and reports success is worse than no
    parser, so the row count is now asserted against the array itself.
    """
    import re

    src = MODULE.REGISTRY_CS.read_text(encoding="utf-8")
    start = src.index("BuildManifestCapabilityDescriptors")
    array = src.index("capabilities =", start)
    end = src.index("];", array)
    block = MODULE.COMMENT_RE.sub("", src[array:end])
    declared = len(re.findall(r"CapabilityKind\.", block))

    parsed = MODULE.manifest_descriptors()
    assert_that(
        len(parsed) == declared,
        f"parser saw {len(parsed)} of {declared} rows — it is silently dropping manifest ids",
    )
    assert_that(declared >= 48, f"only {declared} rows found; the array look-up is wrong")

    ids = {d["manifestId"] for d in parsed}
    for constant_row in ("security.mtls", "edit.features", "versioning.branch", "sync.offline"):
        assert_that(constant_row in ids, f"{constant_row} (FeatureCatalog.* entitlement) was dropped")


def test_feature_catalog_entitlement_constants_resolve():
    parsed = {d["manifestId"]: d for d in MODULE.manifest_descriptors()}
    assert_that(parsed["security.mtls"]["entitlementKey"] == "identity.mtls-client-certificate",
                f"security.mtls -> {parsed['security.mtls']['entitlementKey']}")
    assert_that(parsed["versioning.branch"]["entitlementKey"] == "editing.branch-versioning",
                f"versioning.branch -> {parsed['versioning.branch']['entitlementKey']}")


def main() -> int:
    cases = [v for n, v in sorted(globals().items()) if n.startswith("test_")]
    for case in cases:
        case()
        print(f"ok - {case.__name__}")
    print(f"\n{len(cases)} passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
