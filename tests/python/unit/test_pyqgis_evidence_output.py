# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

import importlib
import json
from pathlib import Path
from types import SimpleNamespace

import pytest


@pytest.mark.parametrize("override", [False, True])
def test_session_teardown_writes_all_protocol_envelopes(tmp_path, monkeypatch, override):
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[1]))
    lane = importlib.import_module("pyqgis.conftest")
    canonical_fixture = importlib.import_module("shared.canonical_fixture")
    default = tmp_path / "artifacts" / "test-results"
    monkeypatch.setattr(canonical_fixture, "TEST_RESULTS_ROOT", default)
    monkeypatch.delenv("HONUA_PYQGIS_OUTPUT_DIR", raising=False)
    monkeypatch.setenv("PYTEST_XDIST_WORKER", "gw2")
    output = tmp_path / "custom-output" if override else default
    if override:
        monkeypatch.setenv("HONUA_PYQGIS_OUTPUT_DIR", str(output))
    runtime = SimpleNamespace(server_version="test", server_commit="a" * 40,
                              fixture_revision="fixture", server_config_revision="config")
    collectors = [lane.CertificationEvidenceCollector(runtime, "test", protocol)
                  for protocol in ("ogc-features", "wfs", "cog")]
    for collector in collectors:
        collector.record("CERT-QUERY-01", "pass", measured_count=10)
    teardown = lane._write_cert_evidence.__wrapped__(*collectors)
    next(teardown)
    assert not output.exists()
    with pytest.raises(StopIteration):
        next(teardown)
    receipts = sorted(output.glob("*-desktop-qgis-*-gw2.cert.json"))
    assert len(receipts) == 3
    envelopes = [json.loads(receipt.read_text()) for receipt in receipts]
    assert {envelope["protocol"] for envelope in envelopes} == {"ogc-features", "wfs", "cog"}
    assert all(envelope["summary"]["passed"] == 1 for envelope in envelopes)
    if override:
        assert not default.exists()
