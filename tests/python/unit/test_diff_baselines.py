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
