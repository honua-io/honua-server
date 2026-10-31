# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""Regression tests for the client-compat baseline comparator."""

from __future__ import annotations

import importlib.util
from pathlib import Path
import sys


SCRIPT = Path(__file__).parents[3] / "scripts" / "client-compat" / "diff-baselines.py"
SPEC = importlib.util.spec_from_file_location("diff_baselines", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
diff_baselines = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = diff_baselines
SPEC.loader.exec_module(diff_baselines)


def test_historical_stub_baseline_is_compared_without_rewriting_evidence(tmp_path):
    import json

    old_lane = "arc" + "gis-stub"
    baseline_dir = tmp_path / "baseline"
    current_dir = tmp_path / "current"
    baseline_dir.mkdir()
    current_dir.mkdir()
    original = {"client_lane": old_lane, "protocol": "portal", "run_date": "2026-01-01",
                "results": [{"test_case_id": "CERT-CONN-01", "status": "pass"}]}
    baseline_file = baseline_dir / "old.cert.json"
    baseline_file.write_text(json.dumps(original))
    (current_dir / "current.cert.json").write_text(json.dumps({
        **original, "client_lane": "geoservices-rest-stub",
        "results": [{"test_case_id": "CERT-CONN-01", "status": "fail"}],
    }))
    baseline = diff_baselines.index_envelopes(baseline_dir, historical_baselines=True)
    current = diff_baselines.index_envelopes(current_dir)
    assert set(baseline) == {( "geoservices-rest-stub", "portal")}
    assert baseline[("geoservices-rest-stub", "portal")]["client_lane"] == old_lane
    assert json.loads(baseline_file.read_text()) == original
    assert diff_baselines.diff(baseline, current)[0].classification == "regression"
    assert set(diff_baselines.index_envelopes(baseline_dir)) == {(old_lane, "portal")}


def test_passing_status_does_not_hide_numeric_regressions():
    baseline = {"measured_count": 10, "measured_delta": 0.001}
    assert not diff_baselines.measurement_regression(baseline, dict(baseline))
    assert not diff_baselines.measurement_regression(baseline, {**baseline, "measured_delta": 0})
    for value in (9, 11, None, True, "10", float("nan"), float("inf")):
        assert diff_baselines.measurement_regression(baseline, {**baseline, "measured_count": value})
    for value in (0.002, -0.002, None, False, "0", float("nan"), float("inf")):
        assert diff_baselines.measurement_regression(baseline, {**baseline, "measured_delta": value})


def test_measurement_regression_reaches_strict_diff_classification():
    key = ("gdal", "exports")
    baseline = {key: {"results": [{"test_case_id": "export", "status": "pass", "measured_count": 2}]}}
    current = {key: {"results": [{"test_case_id": "export", "status": "pass", "measured_count": 1}]}}
    changes = diff_baselines.diff(baseline, current)
    assert len(changes) == 1 and changes[0].classification == "regression"
    assert "measured_count changed" in changes[0].notes


def test_delta_tolerance_is_explicit_finite_and_nonnegative():
    baseline = {"measured_delta": 0, "measured_delta_tolerance": 0.01}
    assert not diff_baselines.measurement_regression(baseline, {"measured_delta": -0.01})
    assert diff_baselines.measurement_regression(baseline, {"measured_delta": 0.011})
    for value in (-1, True, None, float("nan"), float("inf")):
        assert diff_baselines.measurement_regression(
            {**baseline, "measured_delta_tolerance": value}, {"measured_delta": 0})


def test_index_results_includes_lane_extensions() -> None:
    envelope = {
        "results": [{"test_case_id": "CERT-CONN-01", "status": "pass"}],
        "extensions": [{"test_case_id": "NB-OWS-WFS-100-01", "status": "fail"}],
    }

    indexed = diff_baselines.index_results(envelope)

    assert set(indexed) == {"CERT-CONN-01", "NB-OWS-WFS-100-01"}
    assert indexed["NB-OWS-WFS-100-01"]["status"] == "fail"


def test_exact_client_registry_stays_joined_to_documented_and_baselined_extensions():
    import fnmatch
    import json
    import re

    root = SCRIPT.parents[2]
    registry = json.loads((root / "docs/gis/data/client-certification-matrix.v1.json").read_text())
    declared = {row["id"] for row in registry["testCases"]}
    markdown = (root / "docs/gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md").read_text()
    documented = set(re.findall(r"^\| ((?:CERT|JS|EL|DSK|CLI|BI|NB)-[A-Z0-9-]*\d) \|", markdown, re.M))
    assert declared == documented
    lane = next(row for row in registry["lanes"] if row["id"] == "js")
    automated = {case_id for case_id in declared if any(fnmatch.fnmatchcase(case_id, pattern) for pattern in lane["automated"])}
    evidence = set()
    for path in (root / "tests/baselines/client-compat").rglob("*.cert.json"):
        envelope = json.loads(path.read_text())
        if envelope.get("client_lane") == "js":
            evidence.update(diff_baselines.index_results(envelope))
    assert automated <= evidence
    assert "JS-EXT-OL-WFS-GETFEATURE-01" in automated


if __name__ == "__main__":
    import unittest
    suite = unittest.TestSuite(unittest.FunctionTestCase(value) for name, value in list(globals().items())
                               if name.startswith("test_"))
    sys.exit(not unittest.TextTestRunner().run(suite).wasSuccessful())
