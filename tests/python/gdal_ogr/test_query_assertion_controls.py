# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Assertion controls do not certify a candidate or enter the live GDAL report."""
import json
from copy import deepcopy
from types import SimpleNamespace

import httpx
import pytest

from .conftest import OgrResult
from .controlled_wfs import source_document
from .query_oracles import (
    exact_rows,
    identity,
    source_subset,
)
from .test_wfs_query import TestWfsQuery as WfsQueries


@pytest.fixture(scope="session", autouse=True)
def gdal_available():
    """Pure assertion controls execute even where the native client is unavailable."""
    yield


@pytest.fixture(scope="session", autouse=True)
def _write_evidence_report():
    yield



def run_probe(document, output, operation):
    collector = SimpleNamespace(records=[])
    collector.record = lambda *args, **kwargs: collector.records.append((args, kwargs))
    raw = deepcopy(document)
    if operation == "attribute":
        raw["features"] = raw["features"][:1]
    with httpx.Client(base_url="http://controlled", trust_env=False, transport=httpx.MockTransport(lambda request: httpx.Response(200, json=raw))) as client:
        def ogr(args):
            return OgrResult(args, 0, json.dumps(output), "")
        if operation == "attribute":
            WfsQueries().test_attribute_query(client, "WFS:http://controlled/wfs", ogr, "fixture:points", collector)
        elif operation == "bbox":
            WfsQueries().test_spatial_query_bbox(client, "WFS:http://controlled/wfs", ogr, "fixture:points", collector)
        else:
            WfsQueries().test_spatial_query_empty_bbox("WFS:http://controlled/wfs", ogr, "fixture:points")
    return collector.records


@pytest.mark.parametrize("canonical", [True, False])
def test_authored_window_is_a_discriminating_exact_source_subset(monkeypatch, canonical):
    if canonical:
        monkeypatch.setenv("HONUA_BASE_URL", "http://controlled")
    else:
        monkeypatch.delenv("HONUA_BASE_URL", raising=False)
    document = source_document()
    expected, bounds = source_subset(document, "fixture:points")
    selected = {"type": "FeatureCollection", "features": [feature for feature in document["features"] if feature["id"] in expected]}
    assert len(run_probe(document, selected, "bbox")) == 1
    exact_rows(selected, expected, "fixture:points", bounds=bounds)
    with pytest.raises(AssertionError, match="exact feature count"):
        run_probe(document, document, "bbox")


@pytest.mark.parametrize("fault", ["ignored", "empty", "duplicate", "substituted-id", "wrong-name", "conflicting-id", "missing-id"])
def test_actual_attribute_probe_rejects_nonsemantic_success(monkeypatch, fault):
    monkeypatch.setenv("HONUA_BASE_URL", "http://controlled")
    document = source_document()
    output = deepcopy(document)
    output["features"] = output["features"][:1]
    if fault == "ignored":
        output = deepcopy(document)
    elif fault == "empty":
        output["features"] = []
    elif fault == "duplicate":
        output["features"] *= 2
    elif fault == "substituted-id":
        output["features"][0].update(id=2)
        output["features"][0]["properties"]["objectid"] = 2
    elif fault == "wrong-name":
        output["features"][0]["properties"]["name"] = "beta"
    elif fault == "conflicting-id":
        output["features"][0]["properties"]["objectid"] = 2
    else:
        del output["features"][0]["id"]
        del output["features"][0]["properties"]["objectid"]
    with pytest.raises(AssertionError):
        run_probe(document, output, "attribute")


@pytest.mark.parametrize("fault", ["outside", "changed-bounds", "small-coordinate-drift", "null", "empty", "duplicate", "null-only-empty-window"])
def test_actual_spatial_probes_reject_wrong_rows_and_coordinates(monkeypatch, fault):
    monkeypatch.setenv("HONUA_BASE_URL", "http://controlled")
    document = source_document()
    output = deepcopy(document)
    output["features"] = output["features"][:3]
    operation = "bbox"
    if fault == "outside":
        output["features"][0]["geometry"]["coordinates"] = [0, 0]
    elif fault == "changed-bounds":
        output["features"][0]["geometry"]["coordinates"][0] += .001
    elif fault == "small-coordinate-drift":
        output["features"][0]["geometry"]["coordinates"][0] += .00001
    elif fault == "null":
        output["features"][0]["geometry"] = None
    elif fault == "empty":
        output["features"] = []
    elif fault == "duplicate":
        output["features"][1] = deepcopy(output["features"][0])
    else:
        output["features"] = [deepcopy(document["features"][-1])]
        operation = "empty"
    with pytest.raises(AssertionError):
        run_probe(document, output, operation)


@pytest.mark.parametrize("identifier", [True, 1.0, "unrelated.1", "points.extra.1", "fixture:points.1.extra", "", None])
def test_invalid_or_unrelated_wfs_identity_is_refused(identifier):
    with pytest.raises(AssertionError):
        identity({"id": identifier, "properties": {}}, "fixture:points")


@pytest.mark.parametrize("identifier", [1, "1", "points.1", "fixture:points.1"])
def test_documented_wfs_fid_forms_retain_one_identity(identifier):
    assert identity({"id": identifier, "properties": {"objectid": 1}}, "fixture:points") == 1


def test_actual_attribute_and_empty_window_probes_accept_correct_results(monkeypatch):
    monkeypatch.setenv("HONUA_BASE_URL", "http://controlled")
    document = source_document()
    output = deepcopy(document)
    output["features"] = output["features"][:1]
    assert len(run_probe(document, output, "attribute")) == 1
    assert run_probe(document, {"type": "FeatureCollection", "features": []}, "empty") == []




@pytest.mark.parametrize("missing", ["ogrinfo", "ogr2ogr"])
def test_canonical_lane_refuses_missing_native_tool_before_connecting(tmp_path, missing):
    import os
    import subprocess
    from pathlib import Path

    for tool in ("ogrinfo", "ogr2ogr"):
        if tool != missing:
            executable = tmp_path / tool
            executable.write_text("#!/bin/sh\nexit 0\n")
            executable.chmod(0o755)
    script = Path(__file__).parents[3] / "docker/client-compat/gdal/run.sh"
    result = subprocess.run(["/bin/bash", str(script)], env={**os.environ, "PATH": str(tmp_path)},
                            capture_output=True, text=True, timeout=10, check=False)
    assert result.returncode == 1
    assert f"GDAL lane requires {missing}" in result.stderr


def test_canonical_lane_refuses_unusable_native_client_before_connecting(tmp_path):
    import os
    import subprocess
    from pathlib import Path

    for tool in ("ogrinfo", "ogr2ogr"):
        executable = tmp_path / tool
        executable.write_text("#!/bin/sh\nexit 9\n")
        executable.chmod(0o755)
    script = Path(__file__).parents[3] / "docker/client-compat/gdal/run.sh"
    result = subprocess.run(["/bin/bash", str(script)], env={**os.environ, "PATH": str(tmp_path)},
                            capture_output=True, text=True, timeout=10, check=False)
    assert result.returncode == 9
