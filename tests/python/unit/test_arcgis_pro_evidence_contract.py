from __future__ import annotations

import importlib.util
import json
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[3]
RUNNER_PATH = REPO_ROOT / "scripts/client-compat/arcgis-pro/run-arcgis-pro-evidence.py"


def load_runner():
    spec = importlib.util.spec_from_file_location("arcgis_pro_evidence_runner", RUNNER_PATH)
    assert spec is not None
    assert spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_fixture_observations_emit_desktop_arcgis_envelopes(tmp_path, monkeypatch):
    runner = load_runner()
    monkeypatch.setenv("HONUA_API_KEY", "fixture-secret")
    screenshot = tmp_path / "screenshots" / "map.png"

    observations = {
        "run_id": "fixture-arcgis-pro",
        "run_date": "2026-05-18T00:00:00Z",
        "server_version": "fixture-sha",
        "client_version": "ArcGIS Pro 3.4 fixture",
        "environment": "local",
        "protocols": {
            "featureserver": {
                "checks": {
                    "CERT-CONN-01": {
                        "status": "pass",
                        "duration_ms": 12,
                        "notes": "https://fixture.example/rest?token=fixture-secret",
                    },
                    "CERT-RNDR-01": {
                        "status": "pass",
                        "evidence_ref": str(screenshot),
                    },
                    "CERT-RNDR-SPR-01": "not-applicable",
                },
                "extensions": {
                    "DSK-EXT-01": {
                        "status": "pass",
                        "evidence_ref": str(tmp_path / "project" / "Honua-ArcGISPro-Evidence.aprx"),
                    }
                },
            },
            "mapserver": {
                "checks": {
                    "CERT-CONN-01": "pass",
                    "CERT-RNDR-01": "pass",
                },
                "extensions": {
                    "DSK-EXT-01": "pass",
                },
            },
        },
    }

    paths = runner.write_envelopes(observations, tmp_path)

    assert {path.name for path in paths} == {
        "fixture-arcgis-pro-desktop-arcgis-featureserver.cert.json",
        "fixture-arcgis-pro-desktop-arcgis-mapserver.cert.json",
    }

    feature_envelope = json.loads((tmp_path / "certification/fixture-arcgis-pro-desktop-arcgis-featureserver.cert.json").read_text())
    assert feature_envelope["client_lane"] == "desktop-arcgis"
    assert feature_envelope["protocol"] == "featureserver"
    assert feature_envelope["summary"]["total"] == 24
    assert len(feature_envelope["results"]) == 24
    assert feature_envelope["extensions"][0]["test_case_id"] == "DSK-EXT-01"

    by_id = {item["test_case_id"]: item for item in feature_envelope["results"]}
    assert by_id["CERT-RNDR-01"]["evidence_ref"] == "screenshots/map.png"
    assert by_id["CERT-RNDR-SPR-01"]["status"] == "not-applicable"
    serialized = json.dumps(feature_envelope)
    assert "fixture-secret" not in serialized
    assert "token=[REDACTED]" in serialized


def test_redaction_removes_headers_query_params_and_env_secrets(monkeypatch):
    runner = load_runner()
    monkeypatch.setenv("HONUA_AUTHORIZATION", "Bearer env-secret-token")

    redacted = runner.redact_text(
        "Authorization: Bearer env-secret-token "
        "https://user:pass@example.test/path?api_key=env-secret-token&client_secret=abc123 "
        "password=hunter2"
    )

    assert "env-secret-token" not in redacted
    assert "hunter2" not in redacted
    assert "abc123" not in redacted
    assert "Authorization: [REDACTED]" in redacted
    assert "api_key=[REDACTED]" in redacted
    assert "password=[REDACTED]" in redacted
    assert "https://[REDACTED]@example.test" in redacted


def test_fixture_template_round_trips_without_arcpy(tmp_path):
    runner = load_runner()
    template = tmp_path / "arcgis-pro-observations.template.json"
    output = tmp_path / "out"

    assert runner.main(["--write-fixture-template", str(template)]) == 0
    assert template.exists()

    assert runner.main([
        "--fixture-observations",
        str(template),
        "--output-dir",
        str(output),
    ]) == 0

    envelopes = sorted((output / "certification").glob("*.cert.json"))
    assert len(envelopes) == 2
    for envelope_path in envelopes:
        envelope = json.loads(envelope_path.read_text())
        assert envelope["client_lane"] == "desktop-arcgis"
        assert envelope["summary"]["total"] == 24
