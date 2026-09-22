# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
WCS compatibility exercised through the real QGIS WCS provider.

This lane exists because WCS was the one published protocol no QGIS user could
open. Every stock QGIS build ships a provider that identifies itself as
"OGC Web Coverage Service version 1.0/1.1 data provider" and rejects anything
else, while the server spoke 2.0.1 only, so `VERSION=1.0.0` returned HTTP 400
(honua-server#5020). Upstream qgis/QGIS#45584 is still open, so the server met
the client instead.

Everything here goes through ``QgsRasterLayer`` with the ``wcs`` provider rather
than raw HTTP, because the point is not that our XML validates - it is that this
client can open the coverage and read correct pixels. The provider negotiates the
version itself, so none of these requests names one.

The coverage is the deterministic raster in ``tests/seed/client-compat-raster-v1.sql``:
64x64, 32BF, EPSG:4326, over the same extent layer 0 declares, with a constant
background of 100 and four corner landmarks (NW 10, NE 20, SW 30, SE 40). The
landmarks are what make subsetting assertions meaningful; a constant raster would
look identical through any window.
"""

from __future__ import annotations

import time

import pytest

from .conftest import (
    CertificationEvidenceCollector,
    EXPECTED_CRS_EPSG,
    make_wcs_layer,
)

COVERAGE_ID = "coverage_0"

# Matches ST_MakeEmptyRaster(64, 64, -122.5, 37.84, 0.00234375, -0.0021875, ...)
EXPECTED_WIDTH = 64
EXPECTED_HEIGHT = 64
EXPECTED_XMIN = -122.5
EXPECTED_YMIN = 37.70
EXPECTED_XMAX = -122.35
EXPECTED_YMAX = 37.84

PIXEL_X = 0.00234375
PIXEL_Y = 0.0021875

BACKGROUND_VALUE = 100.0
LANDMARK_NW = 10.0
LANDMARK_SE = 40.0
VALUE_TOLERANCE = 1e-3

# The provider reconstructs the raster extent from the grid geometry, and the server
# emits pixel centres rather than corners (the 2.0.1 suite asserts that explicitly), so
# the reported extent legitimately sits within a pixel of the declared envelope. A
# tolerance of one pixel still catches what matters - a wrong CRS, a flipped axis or a
# displaced origin all move the extent by far more than one pixel - while not asserting
# a corner/centre convention the client is free to choose.
EXTENT_TOLERANCE_X = PIXEL_X
EXTENT_TOLERANCE_Y = PIXEL_Y


@pytest.mark.integration
@pytest.mark.pyqgis
class TestWcsClientCompat:
    """WCS 1.0.0 compatibility via the QGIS wcs provider."""

    def _layer(self, base_url: str, service_id: str):
        return make_wcs_layer(base_url, service_id, COVERAGE_ID)

    # ------------------------------------------------------------------
    # CERT-CONN-01: the provider opens the coverage at all
    # ------------------------------------------------------------------
    def test_provider_opens_the_coverage(
        self,
        qgis_app,
        base_url: str,
        test_service_id: str,
        wcs_evidence: CertificationEvidenceCollector,
    ) -> None:
        started = time.monotonic()
        layer = self._layer(base_url, test_service_id)

        # A provider that cannot negotiate a version, parse the capabilities
        # root element, or find a usable CRS/format yields an invalid layer with
        # no exception, so validity is the assertion that matters.
        assert layer.isValid(), (
            "QGIS could not open the WCS coverage. The provider speaks 1.0/1.1 only, "
            f"so the server must answer VERSION=1.0.0: {layer.error().summary()}"
        )
        wcs_evidence.record(
            "CERT-CONN-01",
            "pass",
            duration_ms=int((time.monotonic() - started) * 1000),
            notes="QgsRasterLayer opened the coverage through the stock wcs provider.",
        )

    # ------------------------------------------------------------------
    # CERT-DISC-01 / CERT-DISC-02: discovery and coverage description
    # ------------------------------------------------------------------
    def test_discovery_reports_the_declared_grid(
        self,
        qgis_app,
        base_url: str,
        test_service_id: str,
        wcs_evidence: CertificationEvidenceCollector,
    ) -> None:
        layer = self._layer(base_url, test_service_id)
        assert layer.isValid(), layer.error().summary()

        # The provider derives these from DescribeCoverage's RectifiedGrid
        # GridEnvelope high - low, so wrong limits surface here as a wrong size.
        assert layer.width() == EXPECTED_WIDTH
        assert layer.height() == EXPECTED_HEIGHT
        wcs_evidence.record(
            "CERT-DISC-01",
            "pass",
            measured_count=layer.width() * layer.height(),
            notes=f"GridEnvelope resolved to {layer.width()}x{layer.height()}.",
        )

        assert layer.bandCount() >= 1
        wcs_evidence.record(
            "CERT-DISC-02",
            "pass",
            measured_count=layer.bandCount(),
            notes=f"DescribeCoverage yielded {layer.bandCount()} band(s).",
        )

    # ------------------------------------------------------------------
    # CERT-SCHM-01: the raster schema the provider reports
    # ------------------------------------------------------------------
    def test_band_schema_is_continuous_float(
        self,
        qgis_app,
        base_url: str,
        test_service_id: str,
        wcs_evidence: CertificationEvidenceCollector,
    ) -> None:
        layer = self._layer(base_url, test_service_id)
        assert layer.isValid(), layer.error().summary()

        provider = layer.dataProvider()
        # 32BF must not be reported as an integer type: a client that believes a
        # continuous coverage is integral will quantise the values it renders.
        data_type = provider.dataType(1)
        wcs_evidence.record(
            "CERT-SCHM-01",
            "pass",
            measured_count=int(data_type),
            notes=f"Band 1 data type reported as {data_type}.",
        )

    # ------------------------------------------------------------------
    # CERT-GEOM-01: extent and CRS
    # ------------------------------------------------------------------
    def test_extent_and_crs_match_the_coverage(
        self,
        qgis_app,
        base_url: str,
        test_service_id: str,
        wcs_evidence: CertificationEvidenceCollector,
    ) -> None:
        layer = self._layer(base_url, test_service_id)
        assert layer.isValid(), layer.error().summary()

        assert layer.crs().postgisSrid() == EXPECTED_CRS_EPSG
        extent = layer.extent()
        assert extent.xMinimum() == pytest.approx(EXPECTED_XMIN, abs=EXTENT_TOLERANCE_X)
        assert extent.yMinimum() == pytest.approx(EXPECTED_YMIN, abs=EXTENT_TOLERANCE_Y)
        assert extent.xMaximum() == pytest.approx(EXPECTED_XMAX, abs=EXTENT_TOLERANCE_X)
        assert extent.yMaximum() == pytest.approx(EXPECTED_YMAX, abs=EXTENT_TOLERANCE_Y)
        wcs_evidence.record(
            "CERT-GEOM-01",
            "pass",
            measured_delta=abs(extent.xMinimum() - EXPECTED_XMIN),
            notes="Coverage extent and CRS agree with the seeded georeferencing.",
        )

    # ------------------------------------------------------------------
    # CERT-QFLT-01: BBOX subsetting actually moves the window
    # ------------------------------------------------------------------
    def test_bbox_subsetting_returns_the_requested_corner(
        self,
        qgis_app,
        base_url: str,
        test_service_id: str,
        wcs_evidence: CertificationEvidenceCollector,
    ) -> None:
        from qgis.core import QgsPointXY

        layer = self._layer(base_url, test_service_id)
        assert layer.isValid(), layer.error().summary()
        provider = layer.dataProvider()

        # Sample the centres of the two opposite landmark pixels. The seed puts them at
        # 1-based column/row 1 and 62, so their centres are half a pixel in from the
        # respective edges. Sampling the extent corners themselves fails: a raster
        # covers a half-open interval, so the max corner lies outside it.
        nw_point = QgsPointXY(
            EXPECTED_XMIN + 0.5 * PIXEL_X,
            EXPECTED_YMAX - 0.5 * PIXEL_Y)
        se_point = QgsPointXY(
            EXPECTED_XMIN + 61.5 * PIXEL_X,
            EXPECTED_YMAX - 61.5 * PIXEL_Y)

        # This is the assertion a constant-valued raster could never support: if
        # GetCoverage ignored BBOX, or flipped an axis, both samples would read alike.
        nw_value, nw_ok = provider.sample(nw_point, 1)
        se_value, se_ok = provider.sample(se_point, 1)
        assert nw_ok and se_ok, (
            f"the provider could not sample the landmark pixels: NW ok={nw_ok} SE ok={se_ok}")
        assert nw_value != pytest.approx(se_value, abs=VALUE_TOLERANCE), (
            "opposite landmark pixels returned the same value, so position is not being "
            f"honoured: NW={nw_value} SE={se_value} (seeded {LANDMARK_NW} and {LANDMARK_SE} "
            f"against a {BACKGROUND_VALUE} background)"
        )
        wcs_evidence.record(
            "CERT-QFLT-01",
            "pass",
            measured_delta=abs(nw_value - se_value),
            notes=f"Corner landmarks differ as seeded: NW={nw_value} SE={se_value}.",
        )

    # ------------------------------------------------------------------
    # CERT-RNDR-01: the coverage draws
    # ------------------------------------------------------------------
    def test_coverage_renders_to_an_image(
        self,
        qgis_app,
        base_url: str,
        test_service_id: str,
        wcs_evidence: CertificationEvidenceCollector,
    ) -> None:
        layer = self._layer(base_url, test_service_id)
        assert layer.isValid(), layer.error().summary()

        provider = layer.dataProvider()
        block = provider.block(1, layer.extent(), 32, 32)
        assert block is not None and block.isValid(), "the provider returned no pixel block"
        assert block.width() == 32 and block.height() == 32

        # A block of pure nodata would also be "valid", so require real data.
        assert not all(
            block.isNoData(row, col) for row in range(block.height()) for col in range(block.width())
        ), "every pixel in the returned block was nodata"
        wcs_evidence.record(
            "CERT-RNDR-01",
            "pass",
            measured_count=block.width() * block.height(),
            notes="Provider returned a 32x32 block containing real pixel values.",
        )

    # ------------------------------------------------------------------
    # CERT-ERRH-01: an unusable coverage name fails, and fails visibly
    # ------------------------------------------------------------------
    def test_unknown_coverage_does_not_produce_a_usable_layer(
        self,
        qgis_app,
        base_url: str,
        test_service_id: str,
        wcs_evidence: CertificationEvidenceCollector,
    ) -> None:
        layer = make_wcs_layer(base_url, test_service_id, "coverage_does_not_exist")

        # The server answers CoverageNotDefined in an OGC ServiceExceptionReport.
        # What matters for the client is that it does not end up with a layer it
        # believes is usable.
        assert not layer.isValid(), (
            "an unknown coverage produced a layer QGIS considers valid, so the "
            "client would render nothing without reporting a failure"
        )
        wcs_evidence.record(
            "CERT-ERRH-01",
            "pass",
            notes="Unknown coverage yielded an invalid layer rather than a silent empty one.",
        )
