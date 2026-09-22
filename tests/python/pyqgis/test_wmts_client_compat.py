# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""WMTS 1.0.0 compatibility exercised through the real QGIS WMTS client.

QGIS serves WMTS through the same ``wms`` provider; supplying ``tileMatrixSet`` is
what selects the tiled path. The manual documents both the Key-Value-Pairs and
RESTful interfaces, so both are asserted here - the RESTful path separately,
because a server can advertise a ResourceURL template that no client can use, and
that has bitten this server before: the committed baselines recorded
``owslib.wmts.buildTileResource`` raising ``KeyError: 'style'`` on our template.

The fixture is ``browser_compat``, whose capabilities advertise layer identifiers
2000/2001/2002, style ``default``, and the ``WebMercatorQuad`` and
``WorldCRS84Quad`` tile matrix sets.

One provider detail is load-bearing and not obvious: ``make_wmts_layer`` must
percent-encode a full KVP ``GetCapabilities`` URL into the ``url`` key. A bare
endpoint makes QGIS append its own WMS-flavoured capabilities parameters, which a
WMTS endpoint rejects, and every case here then fails with "Download of
capabilities failed" - including the negative ones, which is how they can pass
without proving anything. Each negative therefore asserts its known-good sibling
first.
"""

from __future__ import annotations

import time

import httpx
import pytest

from .conftest import (
    CertificationEvidenceCollector,
    make_wmts_layer,
)

RASTER_SERVICE_ID = "browser_compat"
POINT_LAYER = "2000"
WEB_MERCATOR_QUAD = "WebMercatorQuad"
WORLD_CRS84_QUAD = "WorldCRS84Quad"

# A seeded browser_compat point, used so GetFeatureInfo is identified somewhere
# a feature actually is.
KNOWN_FEATURE_NAME = "pt-alpha"
KNOWN_FEATURE_LON = -122.4194
KNOWN_FEATURE_LAT = 37.7749
IDENTIFY_PAD_METRES = 300.0


@pytest.mark.integration
@pytest.mark.pyqgis
class TestWmtsClientCompat:
    """WMTS 1.0.0 via the QGIS wms provider's tiled path."""

    def _layer(self, base_url: str, layer: str = POINT_LAYER, **kwargs):
        return make_wmts_layer(base_url, RASTER_SERVICE_ID, layer, **kwargs)

    # CERT-CONN-01 / GetCapabilities
    def test_getcapabilities_yields_a_valid_tiled_layer(
        self, qgis_app, base_url: str, wmts_evidence: CertificationEvidenceCollector
    ) -> None:
        started = time.monotonic()
        layer = self._layer(base_url)

        assert layer.isValid(), (
            "the wms provider could not build a WMTS layer. It needs the advertised "
            "layer identifier, style and tile matrix set to agree with capabilities: "
            f"{layer.error().summary()}"
        )
        wmts_evidence.record(
            "CERT-CONN-01", "pass",
            duration_ms=int((time.monotonic() - started) * 1000),
            notes=f"WMTS capabilities parsed; tileMatrixSet {WEB_MERCATOR_QUAD} selected.",
        )

    # CERT-DISC-01: both advertised tile matrix sets are usable, not just the default.
    def test_both_advertised_tile_matrix_sets_resolve(
        self, qgis_app, base_url: str, wmts_evidence: CertificationEvidenceCollector
    ) -> None:
        mercator = self._layer(base_url, tile_matrix_set=WEB_MERCATOR_QUAD)
        geographic = self._layer(
            base_url, tile_matrix_set=WORLD_CRS84_QUAD, crs="EPSG:4326")

        assert mercator.isValid(), (
            f"{WEB_MERCATOR_QUAD} did not resolve: {mercator.error().summary()}")
        assert geographic.isValid(), (
            f"{WORLD_CRS84_QUAD} did not resolve: {geographic.error().summary()}")
        wmts_evidence.record(
            "CERT-DISC-01", "pass", measured_count=2,
            notes=f"Both {WEB_MERCATOR_QUAD} and {WORLD_CRS84_QUAD} resolved.",
        )

    # CERT-RNDR-01 / GetTile
    def test_gettile_returns_pixels(
        self, qgis_app, base_url: str, wmts_evidence: CertificationEvidenceCollector
    ) -> None:
        layer = self._layer(base_url)
        assert layer.isValid(), layer.error().summary()

        block = layer.dataProvider().block(1, layer.extent(), 256, 256)

        assert block is not None and block.isValid(), "GetTile produced no pixel block"
        assert block.width() == 256 and block.height() == 256
        wmts_evidence.record(
            "CERT-RNDR-01", "pass", measured_count=block.width() * block.height(),
            notes="GetTile returned a 256x256 block through the tiled provider path.",
        )

    # CERT-DISC-02 / RESTful interface: the advertised ResourceURL template must be
    # usable by a client, not merely present.
    def test_restful_resourceurl_template_is_usable(
        self, qgis_app, base_url: str, wmts_evidence: CertificationEvidenceCollector
    ) -> None:
        capabilities = httpx.get(
            f"{base_url}/rest/services/{RASTER_SERVICE_ID}/MapServer/WMTS",
            params={"SERVICE": "WMTS", "REQUEST": "GetCapabilities", "VERSION": "1.0.0"},
            timeout=30,
        )
        capabilities.raise_for_status()
        body = capabilities.text

        if "ResourceURL" not in body:
            pytest.skip("no RESTful ResourceURL advertised in WMTS capabilities")

        # Substitute the documented placeholders. A template carrying a placeholder
        # the client cannot fill - {style} was the one that broke owslib - leaves an
        # unsubstituted brace behind, so assert none survive.
        import re

        template = re.search(r'ResourceURL[^>]*template="([^"]+)"', body).group(1)
        resolved = (
            template.replace("{TileMatrixSet}", WEB_MERCATOR_QUAD)
            .replace("{TileMatrix}", "0")
            .replace("{TileRow}", "0")
            .replace("{TileCol}", "0")
            .replace("{style}", "default")
            .replace("{Style}", "default")
            .replace("{layer}", POINT_LAYER)
            .replace("{Layer}", POINT_LAYER)
        )
        assert "{" not in resolved, (
            f"the ResourceURL template carries a placeholder a client cannot fill: {resolved}")

        tile = httpx.get(resolved, timeout=30)
        assert tile.status_code == 200, f"RESTful tile fetch returned {tile.status_code}"
        assert tile.content, "RESTful tile fetch returned an empty body"
        wmts_evidence.record(
            "CERT-DISC-02", "pass", measured_count=len(tile.content),
            notes=f"RESTful ResourceURL resolved and served {len(tile.content)} bytes.",
            evidence_ref=resolved,
        )

    # CERT-ERRH-01. Paired with a known-good sibling on the same endpoint, so
    # this cannot pass merely because nothing on this server resolves - which is
    # exactly how it passed while the provider URI was malformed.
    def test_unknown_tile_matrix_set_does_not_produce_a_usable_layer(
        self, qgis_app, base_url: str, wmts_evidence: CertificationEvidenceCollector
    ) -> None:
        assert self._layer(base_url).isValid(), (
            "the known-good tile matrix set does not resolve, so this negative "
            "case proves nothing"
        )

        layer = self._layer(base_url, tile_matrix_set="NoSuchMatrixSet")

        assert not layer.isValid(), (
            "an unknown tile matrix set produced a layer QGIS considers valid"
        )
        wmts_evidence.record(
            "CERT-ERRH-01", "pass",
            notes="Unknown tileMatrixSet yielded an invalid layer rather than a blank one.",
        )

    # CERT-ERRH-02: same guard for the layer identifier.
    def test_unknown_layer_does_not_produce_a_usable_layer(
        self, qgis_app, base_url: str, wmts_evidence: CertificationEvidenceCollector
    ) -> None:
        assert self._layer(base_url).isValid(), (
            "the known-good layer does not resolve, so this negative case proves nothing"
        )

        layer = self._layer(base_url, layer="9999")

        assert not layer.isValid(), (
            "an unadvertised WMTS layer identifier produced a layer QGIS "
            "considers valid, so the client would draw nothing while reporting "
            "success"
        )
        wmts_evidence.record(
            "CERT-ERRH-02", "pass",
            notes="Unadvertised layer identifier yielded an invalid layer.",
        )

    # CERT-SCHM-01 / GetFeatureInfo over the tiled path. Identified at a seeded
    # feature rather than the extent centre, which is empty - an identify that
    # resolves but finds nothing would pass against a server that answers every
    # GetFeatureInfo with an empty document.
    def test_getfeatureinfo_identifies_a_feature(
        self, qgis_app, base_url: str, wmts_evidence: CertificationEvidenceCollector
    ) -> None:
        from qgis.core import (
            QgsCoordinateReferenceSystem,
            QgsCoordinateTransform,
            QgsPointXY,
            QgsProject,
            QgsRaster,
            QgsRectangle,
        )

        layer = self._layer(base_url)
        assert layer.isValid(), layer.error().summary()

        to_web_mercator = QgsCoordinateTransform(
            QgsCoordinateReferenceSystem("EPSG:4326"),
            QgsCoordinateReferenceSystem("EPSG:3857"),
            QgsProject.instance(),
        )
        point = to_web_mercator.transform(
            QgsPointXY(KNOWN_FEATURE_LON, KNOWN_FEATURE_LAT))
        box = QgsRectangle(
            point.x() - IDENTIFY_PAD_METRES, point.y() - IDENTIFY_PAD_METRES,
            point.x() + IDENTIFY_PAD_METRES, point.y() + IDENTIFY_PAD_METRES)

        identified = layer.dataProvider().identify(
            point, QgsRaster.IdentifyFormatText, box, 256, 256)

        assert identified.isValid(), (
            f"GetFeatureInfo did not resolve: {identified.error().summary()}")
        text = "".join(str(value) for value in identified.results().values())
        assert KNOWN_FEATURE_NAME in text, (
            f"GetFeatureInfo resolved but did not report {KNOWN_FEATURE_NAME!r} "
            f"at the seeded feature location: {text!r}"
        )
        wmts_evidence.record(
            "CERT-SCHM-01", "pass",
            notes=(
                f"GetFeatureInfo identified {KNOWN_FEATURE_NAME!r} through the "
                "tiled provider path."
            ),
        )
