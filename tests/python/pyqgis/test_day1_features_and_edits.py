# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""Day-1 GeoServices FeatureServer read, filter, and edit tests via headless PyQGIS 3.44.

Exercises QGIS's stock ``arcgisfeatureserver`` provider against Honua's seeded
services and records certification evidence into the ``featureserver`` envelope.
"""

from __future__ import annotations

import json
import time
import uuid
from http import HTTPStatus

import httpx
import pytest

from .conftest import (
    EXPECTED_ACTIVE_COUNT,
    EXPECTED_TOTAL_FEATURES,
    CertificationEvidenceCollector,
    PyQgisCompatibilityRuntime,
)

pytestmark = [pytest.mark.integration, pytest.mark.pyqgis]

EDIT_SCRATCH_LAYER_ID = "10"
SF_BBOX = (-122.50, 37.70, -122.44, 37.74)
SCRATCH_POINT = (-122.42, 37.76)


def _make_featureserver_layer(layer_url: str, name: str = "honua_fs_day1"):
    from qgis.core import QgsVectorLayer

    return QgsVectorLayer(f"url='{layer_url}'", name, "arcgisfeatureserver")


def _apply_edits(layer_url: str, payload: dict[str, str]) -> dict:
    resp = httpx.post(
        f"{layer_url}/applyEdits",
        data={"f": "json", **payload},
        timeout=30.0,
    )
    assert resp.status_code == HTTPStatus.OK, (
        f"applyEdits returned {resp.status_code}: {resp.text[:300]}"
    )
    body = resp.json()
    assert "error" not in body, f"applyEdits returned GeoServices error: {body}"
    return body


def _cleanup_featureserver_record(layer_url: str, object_id: int) -> None:
    try:
        httpx.post(
            f"{layer_url}/applyEdits",
            data={"f": "json", "deletes": str(object_id)},
            timeout=30.0,
        )
    except httpx.HTTPError:
        pass


def _find_feature_by_name(layer, expected_name: str):
    return [feat for feat in layer.getFeatures() if feat["name"] == expected_name]


def _run_native_qgis_edit_roundtrip(layer_url: str, marker: str, updated: str) -> str:
    from qgis.core import QgsFeature, QgsGeometry, QgsPointXY

    layer = _make_featureserver_layer(layer_url, "honua_fs_edit_native")
    assert layer.isValid(), layer.error().message()
    created_oid: int | None = None
    try:
        assert layer.startEditing(), "Expected startEditing() to open edit buffer."
        feature = QgsFeature(layer.fields())
        feature.setGeometry(QgsGeometry.fromPointXY(QgsPointXY(*SCRATCH_POINT)))
        feature.setAttribute("name", marker)
        assert layer.addFeature(feature), "Failed to stage feature insert."
        assert layer.commitChanges(), f"Insert commit failed: {layer.commitErrors()}"

        reloaded = _make_featureserver_layer(layer_url, "honua_fs_reload_insert")
        matches = _find_feature_by_name(reloaded, marker)
        assert len(matches) == 1, f"Expected 1 inserted feature {marker!r}, got {len(matches)}"
        created_oid = int(matches[0]["objectid"])

        name_idx = reloaded.fields().indexOf("name")
        assert reloaded.startEditing(), "Expected startEditing() for update."
        assert reloaded.changeAttributeValue(matches[0].id(), name_idx, updated)
        assert reloaded.commitChanges(), f"Update commit failed: {reloaded.commitErrors()}"

        verified = _make_featureserver_layer(layer_url, "honua_fs_reload_update")
        updated_matches = _find_feature_by_name(verified, updated)
        assert len(updated_matches) == 1, f"Expected updated feature {updated!r} after reload."

        assert verified.startEditing(), "Expected startEditing() for delete."
        assert verified.deleteFeature(updated_matches[0].id())
        assert verified.commitChanges(), f"Delete commit failed: {verified.commitErrors()}"
        created_oid = None
        return "native-qgis-edit"
    finally:
        if created_oid is not None:
            _cleanup_featureserver_record(layer_url, created_oid)


def _run_apply_edits_with_qgis_reload(layer_url: str, marker: str, updated: str) -> str:
    created_oid: int | None = None
    try:
        lon, lat = SCRATCH_POINT
        adds = [{"geometry": {"x": lon, "y": lat}, "attributes": {"name": marker}}]
        add_result = _apply_edits(layer_url, {"adds": json.dumps(adds)})
        add_rows = add_result.get("addResults") or []
        assert len(add_rows) == 1 and add_rows[0].get("success"), f"Add failed: {add_result}"
        created_oid = int(add_rows[0]["objectId"])

        after_add = _make_featureserver_layer(layer_url, "honua_fs_verify_add")
        assert after_add.isValid(), after_add.error().message()
        assert len(_find_feature_by_name(after_add, marker)) == 1

        updates = [{"attributes": {"objectid": created_oid, "name": updated}}]
        upd_result = _apply_edits(layer_url, {"updates": json.dumps(updates)})
        upd_rows = upd_result.get("updateResults") or []
        assert len(upd_rows) == 1 and upd_rows[0].get("success"), f"Update failed: {upd_result}"

        after_upd = _make_featureserver_layer(layer_url, "honua_fs_verify_upd")
        assert after_upd.isValid(), after_upd.error().message()
        assert len(_find_feature_by_name(after_upd, updated)) == 1

        del_result = _apply_edits(layer_url, {"deletes": str(created_oid)})
        del_rows = del_result.get("deleteResults") or []
        assert len(del_rows) == 1 and del_rows[0].get("success"), f"Delete failed: {del_result}"
        created_oid = None

        after_del = _make_featureserver_layer(layer_url, "honua_fs_verify_del")
        assert after_del.isValid(), after_del.error().message()
        assert not _find_feature_by_name(after_del, updated)
        return "applyEdits-with-qgis-reload"
    finally:
        if created_oid is not None:
            _cleanup_featureserver_record(layer_url, created_oid)


@pytest.mark.cert("CERT-QFLT-01")
@pytest.mark.cert_protocol("featureserver")
def test_featureserver_vector_layer_read_and_filters(
    qgis_app,
    pyqgis_runtime: PyQgisCompatibilityRuntime,
    featureserver_evidence: CertificationEvidenceCollector,
) -> None:
    """QgsVectorLayer loads FeatureServer via arcgisfeatureserver and filters features."""
    from qgis.core import QgsFeatureRequest, QgsRectangle

    started = time.monotonic()
    layer_url = (
        f"{pyqgis_runtime.base_url}/rest/services/"
        f"{pyqgis_runtime.service_id}/FeatureServer/{pyqgis_runtime.collection_id}"
    )
    layer = _make_featureserver_layer(layer_url)
    assert layer.isValid(), f"FeatureServer layer invalid: {layer.error().message()}"
    assert layer.featureCount() == EXPECTED_TOTAL_FEATURES

    field_names = {f.name().lower() for f in layer.fields()}
    assert {"name", "status", "active"} <= field_names, f"Unexpected fields: {field_names}"

    attr_req = QgsFeatureRequest().setFilterExpression('"active" = true')
    active_count = len(list(layer.getFeatures(attr_req)))
    assert active_count == EXPECTED_ACTIVE_COUNT

    spatial_req = QgsFeatureRequest().setFilterRect(QgsRectangle(*SF_BBOX))
    spatial_count = len(list(layer.getFeatures(spatial_req)))
    assert 0 < spatial_count < EXPECTED_TOTAL_FEATURES

    featureserver_evidence.record(
        "CERT-QFLT-01",
        "pass",
        duration_ms=int((time.monotonic() - started) * 1000),
        measured_count=active_count,
        notes=f"FeatureServer read={EXPECTED_TOTAL_FEATURES}, active={active_count}, bbox={spatial_count}.",
    )


@pytest.mark.cert("CERT-EDIT-01")
@pytest.mark.cert_protocol("featureserver")
def test_featureserver_round_trip_edit_on_scratch_layer(
    qgis_app,
    pyqgis_runtime: PyQgisCompatibilityRuntime,
    featureserver_evidence: CertificationEvidenceCollector,
) -> None:
    """Verify FeatureServer edit round-trip and clean restoration via QGIS reload."""
    started = time.monotonic()
    layer_url = (
        f"{pyqgis_runtime.base_url}/rest/services/"
        f"{pyqgis_runtime.service_id}/FeatureServer/{EDIT_SCRATCH_LAYER_ID}"
    )
    probe = _make_featureserver_layer(layer_url, "honua_fs_edit_probe")
    assert probe.isValid(), f"Scratch layer invalid: {probe.error().message()}"

    token = uuid.uuid4().hex[:8]
    marker = f"pyqgis-day1-add-{token}"
    updated = f"pyqgis-day1-upd-{token}"

    if probe.startEditing():
        probe.rollBack()
        mode = _run_native_qgis_edit_roundtrip(layer_url, marker, updated)
    else:
        mode = _run_apply_edits_with_qgis_reload(layer_url, marker, updated)

    featureserver_evidence.record(
        "CERT-EDIT-01",
        "pass",
        duration_ms=int((time.monotonic() - started) * 1000),
        notes=f"FeatureServer edit round-trip verified and restored ({mode}).",
    )
