# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Assertion controls do not certify a candidate or enter the live GDAL report."""
import json
from copy import deepcopy
from types import SimpleNamespace

import httpx
import pytest

from .conftest import OgrResult
from .query_oracles import (
    NAMES,
    authored_geometries,
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


def source_document():
    authored, _, _ = authored_geometries()
    return {"type": "FeatureCollection", "features": [
        {"type": "Feature", "id": index, "properties": {"objectid": index, "name": name}, "geometry": authored[name]}
        for index, name in enumerate(NAMES, 1)]}


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


@pytest.mark.parametrize("operation,fault", [("attribute", None), ("bbox", None), ("empty", None),
                                             ("attribute", "ignored-where"), ("bbox", "ignored-spat")])
def test_real_gdal_wfs_client_exercises_the_changed_probe_and_ignored_argument_controls(monkeypatch, operation, fault):
    import shutil
    import subprocess

    from .controlled_wfs import controlled_wfs

    assert shutil.which("ogr2ogr"), "real GDAL is required for this selected client control"
    monkeypatch.setenv("HONUA_BASE_URL", "http://controlled")
    document = source_document()
    collector = SimpleNamespace(records=[])
    collector.record = lambda *args, **kwargs: collector.records.append((args, kwargs))
    with controlled_wfs(document) as (url, requests):
        monkeypatch.setenv("HONUA_BASE_URL", url)
        def ogr(args):
            args = list(args)
            option, count = ("-where", 2) if fault == "ignored-where" else ("-spat", 5)
            if fault and option in args:
                index = args.index(option)
                del args[index:index + count]
            result = subprocess.run(args, capture_output=True, text=True, timeout=30, check=False)
            return OgrResult(args, result.returncode, result.stdout, result.stderr)
        dsn = f"WFS:{url}/wfs?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetCapabilities"
        with httpx.Client(base_url=url, trust_env=False) as client:
            def execute():
                if operation == "attribute":
                    WfsQueries().test_attribute_query(client, dsn, ogr, "fixture:points", collector)
                elif operation == "bbox":
                    WfsQueries().test_spatial_query_bbox(client, dsn, ogr, "fixture:points", collector)
                else:
                    WfsQueries().test_spatial_query_empty_bbox(dsn, ogr, "fixture:points")
            if fault:
                with pytest.raises(AssertionError, match="exact feature count"):
                    execute()
                assert collector.records == []
            else:
                execute()
        assert any(request["operation"].lower() == "describefeaturetype" for request in requests), "native WFS schema path was not exercised"
        assert any(request["operation"].lower() == "getfeature" for request in requests)
