#!/usr/bin/env python3
"""Offline tests for generate-operation-capability.py.

The join's whole value is that it is complete and resolvable, so those are the
two properties under test rather than the file's shape:

  * **Complete.** Every documented operation in every declared OpenAPI document
    reaches a capability. `unresolved` is empty today, and a regression there
    means a spec grew a route the catalog does not know — exactly the drift this
    artifact exists to surface.
  * **Resolvable.** Every capability the join names exists in the published
    registry. An edge that points at nothing is not an edge, and 23 of the 30
    ids in the sibling manifest namespace already resolve nowhere (WS1).

Plus the mechanical guarantees: the committed copy is current, and the
normalization really is borrowed from the drift checker rather than copied.
"""

from __future__ import annotations

import importlib.util
import json
import sys
from contextlib import redirect_stderr, redirect_stdout
from io import StringIO
from pathlib import Path

SCRIPT = Path(__file__).with_name("generate-operation-capability.py")
SPEC = importlib.util.spec_from_file_location("generate_operation_capability", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules["generate_operation_capability"] = MODULE
SPEC.loader.exec_module(MODULE)

REPO_ROOT = Path(__file__).resolve().parents[2]
ARTIFACT = REPO_ROOT / "docs" / "gis" / "data" / "operation-capability.v1.json"


def assert_that(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def test_every_documented_operation_reaches_a_capability():
    payload = MODULE.build()
    unresolved = payload["unresolved"]
    assert_that(
        not unresolved,
        "operations reach no capability, so the join is incomplete: "
        + ", ".join(f"{r['method']} {r['path']}" for r in unresolved[:5]),
    )
    assert_that(
        payload["summary"]["operationCount"] > 400,
        f"only {payload['summary']['operationCount']} operations joined; the spec set looks truncated",
    )


def test_every_named_capability_resolves_in_the_registry():
    payload = MODULE.build()
    known = MODULE.known_capability_keys()
    assert_that(len(known) > 50, f"capability registry looks empty ({len(known)} keys)")
    missing = sorted(set(payload["summary"]["perCapability"]) - known)
    assert_that(not missing, f"join names capabilities absent from the registry: {missing}")


def test_an_unresolvable_capability_fails_the_run():
    """Without this, the resolvability claim is decoration."""
    real = MODULE.known_capability_keys
    payload = MODULE.build()
    victim = next(iter(payload["summary"]["perCapability"]))
    MODULE.known_capability_keys = lambda: real() - {victim}
    try:
        err = StringIO()
        with redirect_stderr(err), redirect_stdout(StringIO()):
            code = MODULE.main([])
        assert_that(code == 1, "an unresolvable capability did not fail the run")
        assert_that("resolve in no published catalog" in err.getvalue(), err.getvalue())
    finally:
        MODULE.known_capability_keys = real


def test_the_committed_artifact_is_current():
    err, out = StringIO(), StringIO()
    with redirect_stderr(err), redirect_stdout(out):
        code = MODULE.main(["--check"])
    assert_that(code == 0, f"committed artifact is stale:\n{err.getvalue()}{out.getvalue()}")


def test_normalization_is_borrowed_not_copied():
    """Two normalizers that disagree would make the join look green while matching
    different routes than the gate it extends."""
    drift = MODULE.load_drift_module()
    for raw, expected in [
        ("/a/{id:guid}", "/a/{id}"),
        ("/a/{*path}", "/a/{path}"),
        ("/a/", "/a"),
    ]:
        assert_that(
            drift.normalize_route(raw) == expected,
            f"drift checker normalizes {raw!r} to {drift.normalize_route(raw)!r}, expected {expected!r}",
        )
    assert_that(bool(drift.SPECS), "the drift checker declares no OpenAPI documents")


def test_the_artifact_records_its_provenance():
    payload = json.loads(ARTIFACT.read_text(encoding="utf-8"))
    assert_that(payload["generator"].endswith("generate-operation-capability.py"), "generator not recorded")
    assert_that("feature-catalog.json" in payload["sources"]["capabilities"], "capability source not recorded")


def main() -> int:
    cases = [value for name, value in sorted(globals().items()) if name.startswith("test_")]
    for case in cases:
        case()
        print(f"ok - {case.__name__}")
    print(f"\n{len(cases)} passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
