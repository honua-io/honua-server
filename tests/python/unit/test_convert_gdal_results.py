# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""The GDAL report converter certifies only the CERT-* IDs each protocol's GDAL client exercises."""

from __future__ import annotations

import importlib.util
from pathlib import Path
import sys


SCRIPT = Path(__file__).parents[3] / "scripts" / "client-compat" / "convert-gdal-results.py"
SPEC = importlib.util.spec_from_file_location("convert_gdal_results", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
convert = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = convert
SPEC.loader.exec_module(convert)


def _statuses(protocol: str, categories: dict[str, str]) -> dict[str, str]:
    envelope = convert.build_envelope(protocol, categories, "GDAL 3.x", "run", "2026-11-23T00:00:00Z", "local", "local")
    return {row["test_case_id"]: row["status"] for row in envelope["results"]}


def _non_applicable_removed(statuses: dict[str, str]) -> dict[str, str]:
    return {cid: status for cid, status in statuses.items() if status != "not-applicable"}


def test_feature_protocols_keep_their_four_ids():
    statuses = _statuses("wfs", {"feature_read": "pass", "discovery": "pass"})

    assert _non_applicable_removed(statuses) == {
        "CERT-CONN-01": "pass", "CERT-DISC-01": "pass", "CERT-SCHM-01": "skip", "CERT-QFLT-01": "skip",
    }


def test_featureserver_adds_paging_errors_and_geometry():
    categories = {"read": "pass", "schema": "pass", "schema_introspection": "pass",
                  "attribute_query": "pass", "spatial_query": "pass", "paging": "pass", "errors": "pass",
                  "geometry": "pass", "export": "fail"}

    applicable = _non_applicable_removed(_statuses("featureserver", categories))

    assert set(applicable) == {"CERT-CONN-01", "CERT-SCHM-01", "CERT-QFLT-01",
                               "CERT-PAGE-01", "CERT-ERRH-01", "CERT-GEOM-01"}
    assert set(applicable.values()) == {"pass"}


def test_mapserver_certifies_connection_and_rendering_only():
    applicable = _non_applicable_removed(_statuses("mapserver", {"read": "pass", "render": "fail"}))

    assert applicable == {"CERT-CONN-01": "pass", "CERT-RNDR-01": "fail"}


def test_render_never_certifies_a_feature_protocol():
    assert _statuses("ogc-features", {"render": "pass"})["CERT-RNDR-01"] == "not-applicable"


def test_new_protocols_map_to_their_cert_names():
    assert convert.PROTOCOL_MAP["featureserver"] == "featureserver"
    assert convert.PROTOCOL_MAP["mapserver"] == "mapserver"


def test_a_failure_outside_the_protocol_scope_is_warned(capsys):
    statuses = _statuses("mapserver", {"read": "pass", "render": "pass", "errors": "fail"})

    assert statuses["CERT-ERRH-01"] == "not-applicable"
    assert "CERT-ERRH-01, which does not apply" in capsys.readouterr().err


def test_geometry_measurement_survives_report_conversion(tmp_path, monkeypatch):
    import json
    from gdal_ogr.conftest import EvidenceCollector

    collector = EvidenceCollector()
    collector.record_geometry_delta("round_trip", "featureserver", 2e-8)
    collector.record("round_trip", "featureserver", "geometry", "pass")
    collector.record_geometry_delta("round_trip_second", "featureserver", 2e-6)
    collector.record("round_trip_second", "featureserver", "geometry", "fail")
    collector.record_geometry_delta("other_protocol", "wfs", 1.0)
    collector.record("other_protocol", "wfs", "geometry", "pass")
    report = tmp_path / "report.json"
    collector.write_report(report)
    output = tmp_path / "envelopes"
    monkeypatch.setattr(sys, "argv", [str(SCRIPT), "--input", str(report), "--output-dir", str(output),
                                    "--run-id", "measurement"])

    assert convert.main() == 0
    envelope = json.loads((output / "measurement-cli-gdal-featureserver.cert.json").read_text())
    geometry = next(row for row in envelope["results"] if row["test_case_id"] == "CERT-GEOM-01")
    assert geometry["status"] == "fail"
    assert geometry["measured_delta"] == 2e-6
