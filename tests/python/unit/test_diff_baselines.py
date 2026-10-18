# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
