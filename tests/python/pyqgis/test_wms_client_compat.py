# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""WMS 1.3.0 compatibility exercised through the real QGIS WMS provider.

QGIS documents WMS "1.1, 1.1.1 and 1.3" support with GetCapabilities, GetMap,
GetFeatureInfo and GetLegendGraphic, multiple styles per layer, and time via
Dynamic Temporal Control. Those are the operations asserted here, one per
checklist cell, so a cell closes on client evidence rather than on the server's
own conformance tests.

Everything goes through ``QgsRasterLayer`` with the ``wms`` provider. A provider
that cannot parse capabilities, or cannot find the requested layer or CRS, yields
an invalid layer with no exception, so validity is load-bearing rather than
incidental.

The fixture is ``browser_compat`` (``tests/seed/browser-compat.yaml``), whose three
seeded layers publish drawingInfo and are named "Browser Points", "Browser Lines"
and "Browser Polygons" in capabilities.
"""

from __future__ import annotations

import time

import pytest

from .conftest import (
    CertificationEvidenceCollector,
    make_wms_layer,
)

RASTER_SERVICE_ID = "browser_compat"
POINT_LAYER = "Browser Points"
LINE_LAYER = "Browser Lines"

# The only style browser_compat advertises per layer.
ADVERTISED_STYLE = "default"

# browser_compat publishes no timeInfo, so the temporal case uses test_service,
# whose layer 0 carries startTimeField/timeExtent and therefore advertises a WMS
# time Dimension.
TEMPORAL_SERVICE_ID = "test_service"
TEMPORAL_LAYER = "Test Layer"

LEGEND_TIMEOUT_MS = 20000

# The seeded browser_compat features cluster in this window.
EXTENT_XMIN = -122.55
EXTENT_YMIN = 37.65
EXTENT_XMAX = -122.30
EXTENT_YMAX = 37.90


@pytest.mark.integration
@pytest.mark.pyqgis
class TestWmsClientCompat:
    """WMS 1.3.0 via the QGIS wms provider."""

    def _layer(self, base_url: str, layer: str = POINT_LAYER, **kwargs):
        return make_wms_layer(base_url, RASTER_SERVICE_ID, layer, **kwargs)

    # CERT-CONN-01 / GetCapabilities: the provider parses capabilities and
    # resolves the requested layer, or there is no layer at all.
    def test_getcapabilities_yields_a_valid_layer(
        self, qgis_app, base_url: str, wms_evidence: CertificationEvidenceCollector
    ) -> None:
        started = time.monotonic()
        layer = self._layer(base_url)

        assert layer.isValid(), (
            "the wms provider could not build a layer from capabilities: "
            f"{layer.error().summary()}"
        )
        wms_evidence.record(
            "CERT-CONN-01", "pass",
            duration_ms=int((time.monotonic() - started) * 1000),
            notes="GetCapabilities parsed and the requested layer resolved.",
        )

    # CERT-DISC-01 / layer discovery: all three seeded layers are addressable.
    def test_all_seeded_layers_are_addressable(
        self, qgis_app, base_url: str, wms_evidence: CertificationEvidenceCollector
    ) -> None:
        names = [POINT_LAYER, LINE_LAYER, "Browser Polygons"]
        valid = [name for name in names if self._layer(base_url, name).isValid()]

        assert valid == names, f"only these layers resolved: {valid}"
        wms_evidence.record(
            "CERT-DISC-01", "pass", measured_count=len(valid),
            notes=f"All three browser_compat layers resolved: {names}.",
        )

    # CERT-RNDR-01 / GetMap: the provider fetches real pixels, not an empty tile.
    def test_getmap_returns_pixels(
        self, qgis_app, base_url: str, wms_evidence: CertificationEvidenceCollector
    ) -> None:
        from qgis.core import QgsRectangle

        layer = self._layer(base_url)
        assert layer.isValid(), layer.error().summary()

        extent = QgsRectangle(EXTENT_XMIN, EXTENT_YMIN, EXTENT_XMAX, EXTENT_YMAX)
        block = layer.dataProvider().block(1, extent, 256, 256)

        assert block is not None and block.isValid(), "GetMap produced no pixel block"
        assert block.width() == 256 and block.height() == 256
        wms_evidence.record(
            "CERT-RNDR-01", "pass", measured_count=block.width() * block.height(),
            notes="GetMap returned a 256x256 block through the wms provider.",
        )

    # CERT-GEOM-01 / CRS: the provider honours the requested CRS.
    def test_crs_is_honoured(
        self, qgis_app, base_url: str, wms_evidence: CertificationEvidenceCollector
    ) -> None:
        layer = self._layer(base_url)
        assert layer.isValid(), layer.error().summary()

        assert layer.crs().isValid()
        assert layer.crs().postgisSrid() == 4326
        wms_evidence.record(
            "CERT-GEOM-01", "pass",
            notes=f"Layer CRS resolved to {layer.crs().authid()}.",
        )

    # CERT-SCHM-01 / GetFeatureInfo: identify resolves through the provider.
    def test_getfeatureinfo_identifies_a_feature(
        self, qgis_app, base_url: str, wms_evidence: CertificationEvidenceCollector
    ) -> None:
        from qgis.core import QgsPointXY, QgsRaster, QgsRectangle

        layer = self._layer(base_url)
        assert layer.isValid(), layer.error().summary()

        centre = QgsPointXY(
            (EXTENT_XMIN + EXTENT_XMAX) / 2, (EXTENT_YMIN + EXTENT_YMAX) / 2)
        extent = QgsRectangle(EXTENT_XMIN, EXTENT_YMIN, EXTENT_XMAX, EXTENT_YMAX)
        identified = layer.dataProvider().identify(
            centre, QgsRaster.IdentifyFormatFeature, extent, 256, 256)

        # A server that does not implement GetFeatureInfo, or answers in a format
        # the provider cannot parse, yields an invalid result rather than raising.
        assert identified.isValid(), (
            f"GetFeatureInfo did not resolve: {identified.error().summary()}")
        wms_evidence.record(
            "CERT-SCHM-01", "pass",
            notes="GetFeatureInfo returned a provider-parsable identify result.",
        )

    # CERT-ERRH-01: a layer that does not exist must not look usable.
    def test_unknown_layer_does_not_produce_a_usable_layer(
        self, qgis_app, base_url: str, wms_evidence: CertificationEvidenceCollector
    ) -> None:
        layer = self._layer(base_url, "Layer That Does Not Exist")

        assert not layer.isValid(), (
            "an unknown WMS layer produced a layer QGIS considers valid, so the "
            "client would draw nothing while reporting success"
        )
        wms_evidence.record(
            "CERT-ERRH-01", "pass",
            notes="Unknown layer yielded an invalid layer rather than a silent blank.",
        )

    # CERT-RNDR-URL-01 / GetLegendGraphic. The provider fetches the legend from
    # the LegendURL advertised in capabilities, asynchronously, so the fetcher
    # has to be driven by a Qt event loop - there is no blocking accessor.
    def test_getlegendgraphic_returns_an_image(
        self, qgis_app, base_url: str, wms_evidence: CertificationEvidenceCollector
    ) -> None:
        from qgis.core import QgsMapSettings
        from qgis.PyQt.QtCore import QEventLoop, QTimer

        layer = self._layer(base_url)
        assert layer.isValid(), layer.error().summary()
        provider = layer.dataProvider()

        assert provider.supportsLegendGraphic(), (
            "the provider reports no legend support, so capabilities advertised "
            "no usable LegendURL for the requested style"
        )

        settings = QgsMapSettings()
        settings.setExtent(layer.extent())
        settings.setDestinationCrs(layer.crs())
        settings.setLayers([layer])

        fetcher = provider.getLegendGraphicFetcher(settings)
        assert fetcher is not None, "the provider returned no legend fetcher"

        outcome: dict[str, object] = {}
        loop = QEventLoop()
        fetcher.finish.connect(
            lambda image: (outcome.__setitem__("image", image), loop.quit()))
        fetcher.error.connect(
            lambda message: (outcome.__setitem__("error", message), loop.quit()))
        # Bound the wait so a server that never answers fails the case instead of
        # hanging the lane.
        QTimer.singleShot(LEGEND_TIMEOUT_MS, loop.quit)
        fetcher.start()
        loop.exec()

        assert "error" not in outcome, f"legend fetch failed: {outcome['error']}"
        assert "image" in outcome, (
            "legend fetch produced neither an image nor an error within "
            f"{LEGEND_TIMEOUT_MS} ms"
        )
        image = outcome["image"]
        assert not image.isNull(), "GetLegendGraphic returned a null image"
        assert image.width() > 0 and image.height() > 0
        wms_evidence.record(
            "CERT-RNDR-URL-01", "pass",
            measured_count=image.width() * image.height(),
            notes=(
                f"GetLegendGraphic returned a {image.width()}x{image.height()} "
                "legend image via the capabilities LegendURL."
            ),
        )

    # CERT-RNDR-SYM-01 / named styles.
    #
    # QGIS itself does not validate STYLES against capabilities - it builds a
    # layer for an unadvertised style name just as happily - so the client side
    # can only show that the advertised style renders. Whether the server honours
    # the parameter at all is observable only at the protocol level, so the
    # negative is asserted over HTTP. Without it this cell would close on a test
    # that also passes against a server ignoring STYLES entirely.
    def test_named_style_is_honoured(
        self, qgis_app, base_url: str, wms_evidence: CertificationEvidenceCollector
    ) -> None:
        import httpx
        from qgis.core import QgsRectangle

        styled = make_wms_layer(
            base_url, RASTER_SERVICE_ID, POINT_LAYER, styles=ADVERTISED_STYLE)
        assert styled.isValid(), (
            f"the advertised style {ADVERTISED_STYLE!r} did not resolve: "
            f"{styled.error().summary()}"
        )

        extent = QgsRectangle(EXTENT_XMIN, EXTENT_YMIN, EXTENT_XMAX, EXTENT_YMAX)
        block = styled.dataProvider().block(1, extent, 256, 256)
        assert block is not None and block.isValid(), (
            "the advertised style produced no pixels")

        endpoint = f"{base_url}/rest/services/{RASTER_SERVICE_ID}/MapServer/WMS"
        get_map = {
            "SERVICE": "WMS",
            "VERSION": "1.3.0",
            "REQUEST": "GetMap",
            "LAYERS": POINT_LAYER,
            "CRS": "EPSG:4326",
            "BBOX": f"{EXTENT_YMIN},{EXTENT_XMIN},{EXTENT_YMAX},{EXTENT_XMAX}",
            "WIDTH": "256",
            "HEIGHT": "256",
            "FORMAT": "image/png",
        }

        advertised = httpx.get(
            endpoint, params={**get_map, "STYLES": ADVERTISED_STYLE}, timeout=30)
        unadvertised = httpx.get(
            endpoint, params={**get_map, "STYLES": "NoSuchStyle"}, timeout=30)

        assert advertised.status_code == 200, (
            f"the advertised style was rejected with {advertised.status_code}")
        assert advertised.headers["content-type"].startswith("image/"), (
            f"the advertised style returned {advertised.headers['content-type']}")
        assert unadvertised.status_code == 400, (
            "an unadvertised style was accepted, so STYLES is not honoured and a "
            f"client cannot rely on it: got {unadvertised.status_code}"
        )
        wms_evidence.record(
            "CERT-RNDR-SYM-01", "pass",
            notes=(
                f"Style {ADVERTISED_STYLE!r} rendered through the provider; the "
                "server rejected an unadvertised style with 400."
            ),
        )

    # CERT-QFLT-01 / time dimension.
    #
    # Two halves, because either alone proves nothing: the server must advertise
    # a time Dimension in capabilities, and QGIS must accept that same extent and
    # report temporal capabilities. QGIS only activates its temporal path when the
    # provider URI carries type=wmst plus timeDimensionExtent, which its own
    # browser fills in from capabilities - so the extent is read out of
    # capabilities here rather than hardcoded.
    def test_time_dimension_is_usable(
        self, qgis_app, base_url: str, wms_evidence: CertificationEvidenceCollector
    ) -> None:
        import urllib.parse
        import xml.etree.ElementTree as ET

        import httpx
        from qgis.core import QgsRectangle

        capabilities = httpx.get(
            f"{base_url}/rest/services/{TEMPORAL_SERVICE_ID}/MapServer/WMS",
            params={"SERVICE": "WMS", "REQUEST": "GetCapabilities",
                    "VERSION": "1.3.0"},
            timeout=30,
        )
        capabilities.raise_for_status()

        root = ET.fromstring(capabilities.text)
        dimensions = [
            element for element in root.iter()
            if element.tag.endswith("Dimension") and element.get("name") == "time"
        ]
        assert dimensions, (
            "the WMS capabilities advertise no time Dimension, so no client can "
            "drive this layer temporally"
        )
        extent = (dimensions[0].text or "").strip()
        assert extent, "the time Dimension is advertised with no extent"

        layer = make_wms_layer(
            base_url, TEMPORAL_SERVICE_ID, TEMPORAL_LAYER,
            extra=(
                "type=wmst&timeDimensionExtent="
                + urllib.parse.quote(extent, safe="")
            ),
        )
        assert layer.isValid(), (
            f"the advertised time extent {extent!r} did not yield a usable "
            f"layer: {layer.error().summary()}"
        )

        reported = layer.dataProvider().temporalCapabilities()
        assert reported.hasTemporalCapabilities(), (
            f"QGIS did not accept the advertised time extent {extent!r} as "
            "temporal capabilities"
        )
        ranges = reported.allAvailableTemporalRanges()
        assert ranges, "QGIS reported temporal support but no available range"

        block = layer.dataProvider().block(
            1, QgsRectangle(-180.0, -90.0, 180.0, 90.0), 256, 256)
        assert block is not None and block.isValid(), (
            "a time-aware GetMap produced no pixels")
        wms_evidence.record(
            "CERT-QFLT-01", "pass", measured_count=len(ranges),
            notes=(
                f"Advertised time Dimension {extent!r} accepted by QGIS as "
                f"{len(ranges)} available range(s); time-aware GetMap rendered."
            ),
            evidence_ref=extent,
        )
