# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""Day-1 WMS, WMTS, and vector-tile compatibility via headless PyQGIS 3.44.

Exercises real QGIS providers against Honua's seeded services and records
certification evidence into per-protocol ``.cert.json`` envelopes:
- WMS (``wms`` provider) load, extent, and ``QgsMapRendererCustomPainterJob`` render
- WMTS (``wms`` provider with GetCapabilities-derived tileMatrixSet/layer) load and render
- Vector tiles (``QgsVectorTileLayer`` for VectorTileServer and MVT XYZ) metadata and zoom bounds.
"""

from __future__ import annotations

import time
import xml.etree.ElementTree as ET
from http import HTTPStatus
from urllib.parse import quote

import httpx
import pytest

from .conftest import (
    CertificationEvidenceCollector,
    PyQgisCompatibilityRuntime,
)

pytestmark = [pytest.mark.integration, pytest.mark.pyqgis]

WMS_NS = {"wms": "http://www.opengis.net/wms"}
WMTS_NS = {
    "wmts": "http://www.opengis.net/wmts/1.0",
    "ows": "http://www.opengis.net/ows/1.1",
}
RENDER_SIZE_PX = 256
RGBA_CHANNELS = 4
ADD_TO_LEGEND = False
MVT_MAX_ZOOM = 14


def _fetch_xml(url: str) -> ET.Element:
    resp = httpx.get(url, timeout=30.0)
    assert resp.status_code == HTTPStatus.OK, (
        f"GET {url} returned {resp.status_code}: {resp.text[:200]}"
    )
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


def _build_map_settings(layer, target_crs_id: str, size: int = RENDER_SIZE_PX):
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
    project.addMapLayer(layer, ADD_TO_LEGEND)
    try:
        settings = _build_map_settings(layer, target_crs_id)
        image = QImage(RENDER_SIZE_PX, RENDER_SIZE_PX, QImage.Format_ARGB32_Premultiplied)
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
        scan_line.setsize(width * RGBA_CHANNELS)
        row = bytes(scan_line)
        drawn += sum(1 for alpha in row[3::RGBA_CHANNELS] if alpha > 0)
    return drawn


@pytest.mark.cert("CERT-RNDR-01")
@pytest.mark.cert_protocol("wms")
def test_wms_raster_layer_loads_and_renders(
    qgis_app,
    pyqgis_runtime: PyQgisCompatibilityRuntime,
    wms_evidence: CertificationEvidenceCollector,
) -> None:
    """QgsRasterLayer loads Honua WMS and renders a non-blank image."""
    from qgis.core import QgsRasterLayer

    started = time.monotonic()
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

    wms_evidence.record(
        "CERT-RNDR-01",
        "pass",
        duration_ms=int((time.monotonic() - started) * 1000),
        measured_count=drawn,
        notes=f"WMS layer {layer_name!r} rendered {drawn} non-transparent pixels.",
    )


@pytest.mark.cert("CERT-RNDR-01")
@pytest.mark.cert_protocol("wmts")
def test_wmts_raster_layer_loads_and_renders(
    qgis_app,
    pyqgis_runtime: PyQgisCompatibilityRuntime,
    wmts_evidence: CertificationEvidenceCollector,
) -> None:
    """QgsRasterLayer loads Honua WMTS from GetCapabilities and renders cleanly."""
    from qgis.core import QgsRasterLayer

    started = time.monotonic()
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
    assert (image.width(), image.height()) == (RENDER_SIZE_PX, RENDER_SIZE_PX)

    wmts_evidence.record(
        "CERT-RNDR-01",
        "pass",
        duration_ms=int((time.monotonic() - started) * 1000),
        notes=f"WMTS layer {params['layer']!r} ({params['tile_matrix_set']}) rendered cleanly.",
    )


@pytest.mark.cert("CERT-CONN-01")
@pytest.mark.cert_protocol("mvt")
def test_vector_tile_server_layer_loads_metadata_and_zoom_bounds(
    qgis_app,
    pyqgis_runtime: PyQgisCompatibilityRuntime,
    mvt_evidence: CertificationEvidenceCollector,
) -> None:
    """QgsVectorTileLayer loads Honua VectorTileServer with valid zoom bounds."""
    from qgis.core import QgsVectorTileLayer

    started = time.monotonic()
    vts_url = f"{pyqgis_runtime.base_url}/rest/services/{pyqgis_runtime.service_id}/VectorTileServer"
    meta_resp = httpx.get(f"{vts_url}?f=json", timeout=30.0)
    assert meta_resp.status_code == HTTPStatus.OK, f"VectorTileServer metadata -> {meta_resp.status_code}"

    uri = f"type=arcgisvectortileservice&url={quote(vts_url, safe='')}"
    layer = QgsVectorTileLayer(uri, "honua_vts_day1")
    assert layer.isValid(), f"VectorTileServer layer invalid: {layer.error().message()}"
    assert not layer.extent().isEmpty(), "VectorTileServer layer extent is empty."
    assert layer.sourceMinZoom() >= 0
    assert layer.sourceMaxZoom() >= layer.sourceMinZoom()

    mvt_evidence.record(
        "CERT-CONN-01",
        "pass",
        duration_ms=int((time.monotonic() - started) * 1000),
        notes=f"VectorTileServer loaded (zoom {layer.sourceMinZoom()}..{layer.sourceMaxZoom()}).",
    )


@pytest.mark.cert("CERT-DISC-02")
@pytest.mark.cert_protocol("mvt")
def test_mvt_xyz_vector_tile_layer_loads_metadata_and_zoom_bounds(
    qgis_app,
    pyqgis_runtime: PyQgisCompatibilityRuntime,
    mvt_evidence: CertificationEvidenceCollector,
) -> None:
    """QgsVectorTileLayer loads Honua MVT XYZ tiles with valid zoom bounds."""
    from qgis.core import QgsVectorTileLayer

    started = time.monotonic()
    mvt_url = f"{pyqgis_runtime.base_url}/tiles/{pyqgis_runtime.collection_id}/{{z}}/{{x}}/{{y}}.mvt"
    uri = f"type=xyz&url={quote(mvt_url, safe='')}&zmin=0&zmax={MVT_MAX_ZOOM}"

    layer = QgsVectorTileLayer(uri, "honua_mvt_day1")
    assert layer.isValid(), f"MVT XYZ layer invalid: {layer.error().message()}"
    assert not layer.extent().isEmpty(), "MVT XYZ layer extent is empty."
    assert layer.sourceMinZoom() == 0
    assert layer.sourceMaxZoom() == MVT_MAX_ZOOM

    mvt_evidence.record(
        "CERT-DISC-02",
        "pass",
        duration_ms=int((time.monotonic() - started) * 1000),
        notes="MVT XYZ vector tile layer loaded with zoom bounds 0..14.",
    )
