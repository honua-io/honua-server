# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Cloud Optimized GeoTIFF range-read compatibility through the stock QGIS gdal provider.

The cell this closes was recorded as ``blocked``: "no COG-serving surface in this
configuration". That was true. The ImageServer and WCS surfaces render on demand and stamp
``Accept-Ranges: none``, and the cloud-raster catalog consumes COGs server-side. The
artifact this module reads is produced by the admin publish
``POST /api/v1/admin/raster-artifacts/cog`` with ``{"layerId": 0}``, which exports the
layer's primary raster through PostGIS ``ST_AsGDALRaster(..., 'COG')`` under the
deterministic key ``cog/{layerId}/{rasterId}.tif`` and serves it from the policy-aware range proxy
``/api/v1/rasters/cog/{artifactId}``.

PREREQUISITE: that publish is the fixture's responsibility (``seed/publish-cog.py``), not
this module's. LocalFileStorage indexes its objects at construction, so the artifact has to
be published through the running server. Every case here is credential-free and read-only;
on a fixture where the publish has not run these cases fail (not skip) on a 404.

QGIS reads a COG over HTTP with its ``gdal`` provider: GDAL's ``/vsicurl`` sizes the file
from a HEAD, then reads the header and the IFD/tile it needs by ``Range``. Two server
behaviours are therefore load-bearing and are asserted directly: HEAD must report the real
length on every probe, and a ranged GET must answer 206 even after an unranged GET has
warmed the output cache.

The raster is ``test_service`` layer 0's ``Client Compat Coverage``: 64x64, one 8BUI band,
EPSG:4326 over -122.5,37.7 / -122.35,37.84 (``tests/seed/client-compat-v1.sql``). The pixel
oracle is the ImageServer identify at the same point, so a wrong CRS or a flipped axis fails
the comparison.
"""

from __future__ import annotations

import json
import time
import urllib.error
import urllib.parse
import urllib.request

import pytest

from .conftest import CertificationEvidenceCollector

LAYER_ID = 0
RASTER_ID = 1
ARTIFACT_ID = f"cog/{LAYER_ID}/{RASTER_ID}.tif"
PROXY_PREFIX = "/api/v1/rasters/cog/"
COG_CONTENT_TYPE = "image/tiff; application=geotiff; profile=cloud-optimized"

EXPECTED_WIDTH = 64
EXPECTED_HEIGHT = 64
EXPECTED_BANDS = 1
EXPECTED_CRS = "EPSG:4326"
EXPECTED_EXTENT = (-122.5, 37.7, -122.35, 37.84)
EXTENT_TOLERANCE = 1e-6

# Sample well inside the raster; the identify oracle is queried at the same lon/lat.
SAMPLE_LON = -122.42
SAMPLE_LAT = 37.77
VALUE_TOLERANCE = 1e-4


def _artifact_url(base_url: str) -> str:
    return f"{base_url.rstrip('/')}{PROXY_PREFIX}{ARTIFACT_ID}"


def _http(url: str, *, method: str = "GET", headers: dict[str, str] | None = None):
    request = urllib.request.Request(url, method=method, headers=headers or {})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, dict(response.headers), response.read()
    except urllib.error.HTTPError as exc:
        return exc.code, dict(exc.headers or {}), exc.read()


def _layer(base_url: str):
    """Open the artifact the way Data Source Manager > Raster > Protocol does: plain URL, gdal."""
    from qgis.core import QgsRasterLayer

    return QgsRasterLayer(_artifact_url(base_url), "cog-cert", "gdal")


def _identify_oracle(base_url: str, lon: float, lat: float) -> float:
    query = urllib.parse.urlencode({
        "f": "json",
        "geometryType": "esriGeometryPoint",
        "geometry": json.dumps({"x": lon, "y": lat, "spatialReference": {"wkid": 4326}}),
        "returnGeometry": "false",
        "returnCatalogItems": "false",
    })
    status, _, body = _http(f"{base_url.rstrip('/')}/rest/services/test_service/ImageServer/identify?{query}")
    assert status == 200, f"identify oracle -> {status}: {body[:200]!r}"
    payload = json.loads(body)
    value = payload.get("value")
    assert value not in (None, "NoData"), f"identify oracle returned no pixel: {payload}"
    return float(value)


@pytest.mark.pyqgis
class TestCogClientCompat:
    """COG range-read via the QGIS gdal provider over GDAL /vsicurl."""

    # ------------------------------------------------------------------
    # CERT-CONN-01: the gdal provider opens the published artifact
    # ------------------------------------------------------------------
    @pytest.mark.cert("CERT-CONN-01")
    def test_provider_opens_the_published_cog(
        self,
        qgis_app,
        base_url: str,
        cog_evidence: CertificationEvidenceCollector,
    ) -> None:
        started = time.monotonic()
        layer = _layer(base_url)

        assert layer.isValid(), (
            "QGIS could not open the published COG at "
            f"{_artifact_url(base_url)} through the gdal provider: "
            f"{layer.error().summary()!r}"
        )
        cog_evidence.record(
            "CERT-CONN-01",
            "pass",
            duration_ms=int((time.monotonic() - started) * 1000),
            notes="QgsRasterLayer opened the range-proxied COG through the stock gdal provider.",
        )

    # ------------------------------------------------------------------
    # CERT-SCHM-01: size, bands and CRS match the fixture raster
    # ------------------------------------------------------------------
    @pytest.mark.cert("CERT-SCHM-01")
    def test_raster_metadata_matches_the_fixture(
        self,
        qgis_app,
        base_url: str,
        cog_evidence: CertificationEvidenceCollector,
    ) -> None:
        layer = _layer(base_url)
        assert layer.isValid(), layer.error().summary()

        assert (layer.width(), layer.height()) == (EXPECTED_WIDTH, EXPECTED_HEIGHT), (
            f"expected {EXPECTED_WIDTH}x{EXPECTED_HEIGHT}, got {layer.width()}x{layer.height()}"
        )
        assert layer.bandCount() == EXPECTED_BANDS
        assert layer.crs().authid() == EXPECTED_CRS, layer.crs().authid()
        extent = layer.extent()
        got = (extent.xMinimum(), extent.yMinimum(), extent.xMaximum(), extent.yMaximum())
        for expected, actual in zip(EXPECTED_EXTENT, got):
            assert abs(expected - actual) <= EXTENT_TOLERANCE, f"extent {got} != {EXPECTED_EXTENT}"
        cog_evidence.record(
            "CERT-SCHM-01",
            "pass",
            measured_count=layer.bandCount(),
            notes=(
                f"{layer.width()}x{layer.height()}, {layer.bandCount()} band, "
                f"{layer.crs().authid()}, extent {got}."
            ),
        )

    # ------------------------------------------------------------------
    # CERT-DISC-02: the proxy advertises and honours byte ranges
    # ------------------------------------------------------------------
    @pytest.mark.cert("CERT-DISC-02")
    def test_proxy_serves_the_cog_by_byte_range(
        self,
        qgis_app,
        base_url: str,
        cog_evidence: CertificationEvidenceCollector,
    ) -> None:
        url = _artifact_url(base_url)

        # GDAL sizes the file from HEAD before every open; the length must be the real
        # one on every probe, not a replayed cache entry.
        sizes = []
        for _ in range(2):
            head_status, head_headers, _ = _http(url, method="HEAD")
            assert head_status == 200, f"HEAD {url} -> {head_status}"
            assert head_headers.get("Accept-Ranges") == "bytes", head_headers.get("Accept-Ranges")
            sizes.append(int(head_headers["Content-Length"]))
        assert sizes[0] == sizes[1] > 0, f"HEAD lengths drifted: {sizes}"
        total = sizes[0]

        # Warm any cache with an unranged GET, then range into the same URL: a ranged GET
        # must still be a 206, never a cached whole-body 200.
        full_status, full_headers, full_body = _http(url)
        assert full_status == 200 and len(full_body) == total
        assert full_headers.get("Content-Type", "").lower().startswith("image/tiff"), full_headers.get("Content-Type")

        range_status, range_headers, body = _http(url, headers={"Range": "bytes=0-3"})
        assert range_status == 206, (
            f"the range proxy must answer 206 for a ranged GET, got {range_status}; a 200 "
            "means the range was ignored (or a cached full body was replayed)."
        )
        assert range_headers.get("Content-Range") == f"bytes 0-3/{total}", range_headers.get("Content-Range")
        assert body in (b"II*\x00", b"MM\x00*"), f"not a TIFF header: {body!r}"
        assert "Age" not in range_headers, "ranged GET was served from the output cache"
        cog_evidence.record(
            "CERT-DISC-02",
            "pass",
            measured_count=total,
            notes=(
                f"HEAD reported {total} bytes twice, unranged GET returned {len(full_body)} "
                "bytes, and Range: bytes=0-3 answered 206 with the TIFF magic after it."
            ),
        )

    # ------------------------------------------------------------------
    # CERT-RNDR-01: a pixel read through the provider matches the server oracle
    # ------------------------------------------------------------------
    @pytest.mark.cert("CERT-RNDR-01")
    def test_pixel_sample_matches_the_identify_oracle(
        self,
        qgis_app,
        base_url: str,
        cog_evidence: CertificationEvidenceCollector,
    ) -> None:
        from qgis.core import QgsPointXY

        layer = _layer(base_url)
        assert layer.isValid(), layer.error().summary()

        value, ok = layer.dataProvider().sample(QgsPointXY(SAMPLE_LON, SAMPLE_LAT), 1)
        assert ok, "the gdal provider could not sample the COG at the probe point"
        oracle = _identify_oracle(base_url, SAMPLE_LON, SAMPLE_LAT)
        assert abs(float(value) - oracle) <= VALUE_TOLERANCE, (
            f"COG sample {value} at ({SAMPLE_LON}, {SAMPLE_LAT}) != ImageServer identify {oracle}"
        )
        cog_evidence.record(
            "CERT-RNDR-01",
            "pass",
            notes=(
                f"provider.sample at ({SAMPLE_LON}, {SAMPLE_LAT}) = {float(value):.6f}, "
                f"ImageServer identify = {oracle:.6f}."
            ),
        )
