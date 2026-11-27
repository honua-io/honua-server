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


def test_day1_protocol_collectors_write_envelopes_and_route_markers(tmp_path, monkeypatch):
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[1]))
    lane = importlib.import_module("pyqgis.conftest")
    monkeypatch.setenv("HONUA_PYQGIS_OUTPUT_DIR", str(tmp_path))
    monkeypatch.delenv("PYTEST_XDIST_WORKER", raising=False)
    runtime = SimpleNamespace(
        server_version="test",
        server_commit="b" * 40,
        fixture_revision="fixture",
        server_config_revision="config",
    )
    extra = {
        protocol: lane.CertificationEvidenceCollector(runtime, "3.44", protocol)
        for protocol in ("wms", "wmts", "mvt", "featureserver")
    }
    extra["wms"].record("CERT-RNDR-01", "pass", measured_count=42)
    extra["featureserver"].record("CERT-EDIT-01", "fail", notes="edit failed")
    monkeypatch.setattr(lane, "_extra_evidence", extra)

    fake_item = SimpleNamespace(
        fspath="tests/python/pyqgis/test_day1_raster_and_tiles.py",
        get_closest_marker=lambda name: SimpleNamespace(args=("wms",)) if name == "cert_protocol" else None,
    )
    assert lane._collector_for_item(fake_item) is extra["wms"]

    base = [lane.CertificationEvidenceCollector(runtime, "3.44", p) for p in ("ogc-features", "wfs", "cog")]
    teardown = lane._write_cert_evidence.__wrapped__(*base)
    next(teardown)
    with pytest.raises(StopIteration):
        next(teardown)

    receipts = sorted(tmp_path.glob("*-desktop-qgis-*.cert.json"))
    by_protocol = {json.loads(r.read_text())["protocol"]: json.loads(r.read_text()) for r in receipts}
    assert set(by_protocol) == {"wms", "featureserver"}
    assert by_protocol["wms"]["summary"]["passed"] == 1
    assert by_protocol["featureserver"]["summary"]["failed"] == 1
