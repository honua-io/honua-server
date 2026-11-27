# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""Day-1 canonical GIS layer and edit coverage via headless PyQGIS 3.44.

Exercises real QGIS providers against Honua's seeded services:
- WMS (``wms`` provider) load, extent, and ``QgsMapRendererCustomPainterJob`` render
- WMTS (``wms`` provider with GetCapabilities-derived tileMatrixSet/layer) load and render
- Vector tiles (``QgsVectorTileLayer`` for VectorTileServer and MVT XYZ) metadata and zoom bounds
- GeoServices FeatureServer (``arcgisfeatureserver`` provider) read, schema, filters, and
  isolated round-trip edit verification with guaranteed cleanup.
"""

from __future__ import annotations

import json
import uuid
import xml.etree.ElementTree as ET
from urllib.parse import quote

import httpx
import pytest

from .conftest import (
    EXPECTED_ACTIVE_COUNT,
    EXPECTED_TOTAL_FEATURES,
    PyQgisCompatibilityRuntime,
)

pytestmark = [pytest.mark.integration, pytest.mark.pyqgis]

WMS_NS = {"wms": "http://www.opengis.net/wms"}
WMTS_NS = {
    "wmts": "http://www.opengis.net/wmts/1.0",
    "ows": "http://www.opengis.net/ows/1.1",
}
EDIT_SCRATCH_LAYER_ID = "10"
SF_BBOX = (-122.50, 37.70, -122.44, 37.74)


# ---------------------------------------------------------------------------
# Capabilities discovery helpers
# ---------------------------------------------------------------------------


def _fetch_xml(url: str) -> ET.Element:
    resp = httpx.get(url, timeout=30.0)
    assert resp.status_code == 200, f"GET {url} returned {resp.status_code}: {resp.text[:200]}"
    return ET.fromstring(resp.text)


def _discover_wms_layer_name(wms_url: str) -> str:
    caps_url = f"{wms_url}?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetCapabilities"
    root = _fetch_xml(caps_url)
    for elem in root.findall(".//wms:Layer/wms:Name", WMS_NS):
        if elem.text and elem.text.strip():
            return elem.text.strip()
    for elem in root.iter():
        is_name = elem.tag.endswith("}Name") or elem.tag == "Name"
        if is_name and elem.text and elem.text.strip():
            return elem.text.strip()
    pytest.fail(f"WMS GetCapabilities at {caps_url} advertised no named layers.")
    raise AssertionError("unreachable")


def _pick_preferred(values: list[str], preferred: str) -> str:
    if preferred in values:
        return preferred
    return values[0] if values else ""


def _extract_wmts_layer_params(layer_el: ET.Element) -> dict[str, str]:
    ident = layer_el.findtext("ows:Identifier", default="", namespaces=WMTS_NS).strip()
    formats = [el.text.strip() for el in layer_el.findall("wmts:Format", WMTS_NS) if el.text]
    tms_list = [
        el.text.strip()
        for el in layer_el.findall("wmts:TileMatrixSetLink/wmts:TileMatrixSet", WMTS_NS)
        if el.text
    ]
    styles = [
        el.text.strip()
        for el in layer_el.findall("wmts:Style/ows:Identifier", WMTS_NS)
        if el.text
    ]
    tms = _pick_preferred(tms_list, "WebMercatorQuad")
    crs = "EPSG:3857" if ("WebMercator" in tms or "3857" in tms) else "EPSG:4326"
    return {
        "layer": ident,
        "format": _pick_preferred(formats, "image/png") or "image/png",
        "tile_matrix_set": tms,
        "style": _pick_preferred(styles, "default"),
        "crs": crs,
    }


def _discover_wmts_params(wmts_url: str) -> dict[str, str]:
    caps_url = f"{wmts_url}?SERVICE=WMTS&VERSION=1.0.0&REQUEST=GetCapabilities"
    root = _fetch_xml(caps_url)
    layer_el = root.find(".//wmts:Contents/wmts:Layer", WMTS_NS)
    assert layer_el is not None, f"WMTS GetCapabilities at {caps_url} advertised no Layer entries."
    params = _extract_wmts_layer_params(layer_el)
    assert params["layer"], f"WMTS Layer at {caps_url} has no ows:Identifier."
    assert params["tile_matrix_set"], f"WMTS Layer {params['layer']!r} has no TileMatrixSet."
    return params


# ---------------------------------------------------------------------------
# Headless rendering helpers (QgsMapRendererCustomPainterJob)
# ---------------------------------------------------------------------------


def _build_map_settings(layer, target_crs_id: str, size: int = 256):
    from qgis.core import (
        QgsCoordinateReferenceSystem,
        QgsCoordinateTransform,
        QgsMapSettings,
        QgsProject,
    )
    from qgis.PyQt.QtCore import QSize

    project = QgsProject.instance()
    target_crs = QgsCoordinateReferenceSystem(target_crs_id)
    extent = layer.extent()
    if layer.crs().isValid() and layer.crs() != target_crs:
        xform = QgsCoordinateTransform(layer.crs(), target_crs, project)
        extent = xform.transformBoundingBox(extent)
    settings = QgsMapSettings()
    settings.setDestinationCrs(target_crs)
    settings.setLayers([layer])
    settings.setOutputSize(QSize(size, size))
    settings.setExtent(extent)
    return settings


def _render_with_custom_painter(layer, target_crs_id: str = "EPSG:4326"):
    from qgis.core import QgsMapRendererCustomPainterJob, QgsProject
    from qgis.PyQt.QtCore import Qt
    from qgis.PyQt.QtGui import QImage, QPainter

    project = QgsProject.instance()
    project.addMapLayer(layer, False)
    try:
        settings = _build_map_settings(layer, target_crs_id)
        image = QImage(256, 256, QImage.Format_ARGB32_Premultiplied)
        image.fill(Qt.transparent)
        painter = QPainter(image)
        try:
            job = QgsMapRendererCustomPainterJob(settings, painter)
            job.start()
            job.waitForFinished()
            assert not job.errors(), f"Render job reported errors: {job.errors()}"
        finally:
            painter.end()
        return image
    finally:
        project.takeMapLayer(layer)


def _count_non_transparent_pixels(image) -> int:
    from qgis.PyQt.QtGui import QImage

    rgba = image.convertToFormat(QImage.Format_RGBA8888)
    width, height = rgba.width(), rgba.height()
    drawn = 0
    for y in range(height):
        scan_line = rgba.constScanLine(y)
        scan_line.setsize(width * 4)
        row = bytes(scan_line)
        drawn += sum(1 for alpha in row[3::4] if alpha > 0)
    return drawn


# ---------------------------------------------------------------------------
# FeatureServer layer & edit helpers
# ---------------------------------------------------------------------------


def _make_featureserver_layer(layer_url: str, name: str = "honua_fs_day1"):
    from qgis.core import QgsVectorLayer

    return QgsVectorLayer(f"url='{layer_url}'", name, "arcgisfeatureserver")


def _apply_edits(layer_url: str, payload: dict[str, str]) -> dict:
    resp = httpx.post(
        f"{layer_url}/applyEdits",
        data={"f": "json", **payload},
        timeout=30.0,
    )
    assert resp.status_code == 200, f"applyEdits returned {resp.status_code}: {resp.text[:300]}"
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


def _run_native_qgis_edit_roundtrip(layer_url: str, marker: str, updated: str) -> None:
    from qgis.core import QgsFeature, QgsGeometry, QgsPointXY

    layer = _make_featureserver_layer(layer_url, "honua_fs_edit_native")
    assert layer.isValid(), layer.error().message()
    created_oid: int | None = None
    try:
        assert layer.startEditing(), "Expected startEditing() to open edit buffer."
        feature = QgsFeature(layer.fields())
        feature.setGeometry(QgsGeometry.fromPointXY(QgsPointXY(-122.42, 37.76)))
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
    finally:
        if created_oid is not None:
            _cleanup_featureserver_record(layer_url, created_oid)


def _run_apply_edits_with_qgis_reload(layer_url: str, marker: str, updated: str) -> None:
    created_oid: int | None = None
    try:
        adds = [{"geometry": {"x": -122.42, "y": 37.76}, "attributes": {"name": marker}}]
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
    finally:
        if created_oid is not None:
            _cleanup_featureserver_record(layer_url, created_oid)


# ---------------------------------------------------------------------------
# Day-1 PyQGIS compatibility tests
# ---------------------------------------------------------------------------


def test_wms_raster_layer_loads_and_renders(
    qgis_app,
    pyqgis_runtime: PyQgisCompatibilityRuntime,
) -> None:
    """QgsRasterLayer loads Honua WMS and renders a non-blank image."""
    from qgis.core import QgsRasterLayer

    wms_url = f"{pyqgis_runtime.base_url}/rest/services/{pyqgis_runtime.service_id}/MapServer/WMS"
    layer_name = _discover_wms_layer_name(wms_url)
    uri = (
        "contextualWMSLegend=0&crs=EPSG:4326&dpiMode=7&featureCount=10"
        f"&format=image/png&layers={quote(layer_name, safe='')}"
        f"&styles=&url={quote(wms_url, safe='')}"
    )

    layer = QgsRasterLayer(uri, "honua_wms_day1", "wms")
    assert layer.isValid(), f"WMS layer failed to load ({uri}): {layer.error().message()}"
    assert not layer.extent().isEmpty(), "WMS layer extent is empty."

    image = _render_with_custom_painter(layer, "EPSG:4326")
    drawn = _count_non_transparent_pixels(image)
    assert drawn > 0, "WMS render via QgsMapRendererCustomPainterJob produced a blank image."


def test_wmts_raster_layer_loads_and_renders(
    qgis_app,
    pyqgis_runtime: PyQgisCompatibilityRuntime,
) -> None:
    """QgsRasterLayer loads Honua WMTS from GetCapabilities and renders cleanly."""
    from qgis.core import QgsRasterLayer

    wmts_url = f"{pyqgis_runtime.base_url}/rest/services/{pyqgis_runtime.service_id}/MapServer/WMTS"
    params = _discover_wmts_params(wmts_url)
    caps_url = f"{wmts_url}?SERVICE=WMTS&VERSION=1.0.0&REQUEST=GetCapabilities"
    uri = (
        f"crs={params['crs']}&dpiMode=7"
        f"&format={quote(params['format'], safe='')}"
        f"&layers={quote(params['layer'], safe='')}"
        f"&styles={quote(params['style'], safe='')}"
        f"&tileMatrixSet={quote(params['tile_matrix_set'], safe='')}"
        f"&url={quote(caps_url, safe='')}"
    )

    layer = QgsRasterLayer(uri, "honua_wmts_day1", "wms")
    assert layer.isValid(), f"WMTS layer failed to load ({uri}): {layer.error().message()}"
    assert not layer.extent().isEmpty(), "WMTS layer extent is empty."

    image = _render_with_custom_painter(layer, params["crs"])
    assert not image.isNull(), "WMTS render produced a null QImage."
    assert (image.width(), image.height()) == (256, 256)


def test_vector_tile_server_layer_loads_metadata_and_zoom_bounds(
    qgis_app,
    pyqgis_runtime: PyQgisCompatibilityRuntime,
) -> None:
    """QgsVectorTileLayer loads Honua VectorTileServer with valid zoom bounds."""
    from qgis.core import QgsVectorTileLayer

    vts_url = f"{pyqgis_runtime.base_url}/rest/services/{pyqgis_runtime.service_id}/VectorTileServer"
    meta_resp = httpx.get(f"{vts_url}?f=json", timeout=30.0)
    assert meta_resp.status_code == 200, f"VectorTileServer metadata -> {meta_resp.status_code}"

    uri = f"type=arcgisvectortileservice&url={quote(vts_url, safe='')}"
    layer = QgsVectorTileLayer(uri, "honua_vts_day1")
    assert layer.isValid(), f"VectorTileServer layer invalid: {layer.error().message()}"
    assert not layer.extent().isEmpty(), "VectorTileServer layer extent is empty."
    assert layer.sourceMinZoom() >= 0
    assert layer.sourceMaxZoom() >= layer.sourceMinZoom()


def test_mvt_xyz_vector_tile_layer_loads_metadata_and_zoom_bounds(
    qgis_app,
    pyqgis_runtime: PyQgisCompatibilityRuntime,
) -> None:
    """QgsVectorTileLayer loads Honua MVT XYZ tiles with valid zoom bounds."""
    from qgis.core import QgsVectorTileLayer

    mvt_url = f"{pyqgis_runtime.base_url}/tiles/{pyqgis_runtime.collection_id}/{{z}}/{{x}}/{{y}}.mvt"
    uri = f"type=xyz&url={quote(mvt_url, safe='')}&zmin=0&zmax=14"

    layer = QgsVectorTileLayer(uri, "honua_mvt_day1")
    assert layer.isValid(), f"MVT XYZ layer invalid: {layer.error().message()}"
    assert not layer.extent().isEmpty(), "MVT XYZ layer extent is empty."
    assert layer.sourceMinZoom() == 0
    assert layer.sourceMaxZoom() == 14


def test_featureserver_vector_layer_read_and_filters(
    qgis_app,
    pyqgis_runtime: PyQgisCompatibilityRuntime,
) -> None:
    """QgsVectorLayer loads FeatureServer via arcgisfeatureserver and filters features."""
    from qgis.core import QgsFeatureRequest, QgsRectangle

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
    assert len(list(layer.getFeatures(attr_req))) == EXPECTED_ACTIVE_COUNT

    spatial_req = QgsFeatureRequest().setFilterRect(QgsRectangle(*SF_BBOX))
    spatial_count = len(list(layer.getFeatures(spatial_req)))
    assert 0 < spatial_count < EXPECTED_TOTAL_FEATURES


def test_featureserver_round_trip_edit_on_scratch_layer(
    qgis_app,
    pyqgis_runtime: PyQgisCompatibilityRuntime,
) -> None:
    """Verify FeatureServer edit round-trip and clean restoration via QGIS reload."""
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
        _run_native_qgis_edit_roundtrip(layer_url, marker, updated)
    else:
        _run_apply_edits_with_qgis_reload(layer_url, marker, updated)
