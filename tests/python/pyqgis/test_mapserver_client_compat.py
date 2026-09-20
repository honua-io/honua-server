# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""GeoServices REST MapServer compatibility through the real QGIS AMS client.

QGIS 3.44 LTR ships an ``arcgismapserver`` data provider ("ArcGIS Map Service
data provider", confirmed present in ``QgsProviderRegistry.providerList()``).
The four checklist operations are exercised through it, one case each:

    service-info  ``GET /rest/services/{id}/MapServer?f=json``   CERT-CONN-01
    export        ``GET .../MapServer/export``                   CERT-RNDR-01
    identify      ``GET .../MapServer/identify``                 CERT-SCHM-01
    legend        ``GET .../MapServer/legend``                    CERT-RNDR-URL-01

Each one was confirmed to reach the named server operation: the server's own
request log tags them ``HonuaOperation: metadata|export|identify|legend`` with
``UserAgent: Mozilla/5.0 QGIS/34414``, so these cases close on client traffic
rather than on the server's self-tests.

Two provider behaviours are load-bearing and neither is obvious.

**``isValid()`` proves nothing here.** Unlike the WMS provider, the AMS provider
builds a layer QGIS calls valid even when the service info request 404s, when the
host does not resolve, or when the requested sublayer does not exist. All four
cases were measured: ``isValid()`` is ``True`` throughout. What actually
distinguishes a resolved service is the *extent* - a service whose info document
was parsed carries the advertised ``fullExtent``, and everything else carries an
empty ``0,0 : 0,0`` extent. So every case here asserts a non-degenerate extent,
and each is paired with the failing sibling that shows the discriminator is real.
Asserting only ``isValid()`` would have produced four passes against a dead host.

**The provider URI must use the quoted-key form.** ``url='...' crs='...'
format='...'`` is what ``QgsProviderRegistry.decodeUri('arcgismapserver', ...)``
round-trips. The ``&``-delimited form used for the WMS/WMTS providers decodes to
``url=NULL`` here, and the resulting layer still reports ``isValid() == True``
with an empty extent - the same silent-blank trap.

The fixture is ``browser_compat`` (``tests/seed/browser-compat.yaml``): layers
2000 "Browser Points", 2001 "Browser Lines", 2002 "Browser Polygons", all three
publishing ``drawingInfo``, and a seeded point ``pt-alpha`` (objectid 13) at
-122.4194, 37.7749.
"""

from __future__ import annotations

import time

import httpx
import pytest

from .conftest import CertificationEvidenceCollector

SERVICE_ID = "browser_compat"

POINT_LAYER = "2000"
LINE_LAYER = "2001"
POLYGON_LAYER = "2002"
ALL_LAYERS = (POINT_LAYER, LINE_LAYER, POLYGON_LAYER)

# A service name the fixture does not publish, used as the paired negative that
# shows the extent check is a real discriminator rather than incidental.
UNPUBLISHED_SERVICE_ID = "no_such_service"
# A sublayer id browser_compat does not publish.
UNPUBLISHED_LAYER = "9999"

# A seeded browser_compat point, so identify is asked somewhere a feature is.
KNOWN_FEATURE_NAME = "pt-alpha"
KNOWN_FEATURE_OBJECTID = "13"
KNOWN_FEATURE_LON = -122.4194
KNOWN_FEATURE_LAT = 37.7749

# Far outside the seeded cluster (-122.55..-122.30, 37.65..37.90) but still a
# well-formed request, so an identify that resolves there and finds nothing shows
# the server is answering the query rather than echoing a canned payload.
EMPTY_LON = -122.00
EMPTY_LAT = 37.20

RENDER_SIZE = 256
LEGEND_TIMEOUT_MS = 20000

# Extents are compared against the server's own advertised fullExtent, so the
# tolerance only has to absorb float round-tripping through the info document.
EXTENT_TOLERANCE_DEG = 1e-6


def make_mapserver_layer(
    base_url: str,
    service_id: str = SERVICE_ID,
    *,
    layer: str = "",
    crs: str = "EPSG:3857",
    image_format: str = "PNG32",
):
    """Construct a QGIS raster layer via the stock ``arcgismapserver`` provider.

    The quoted-key form is required: it is what the provider's own ``decodeUri``
    round-trips. An ``&``-delimited URI decodes to ``url=NULL`` and yields a
    layer that reports ``isValid() == True`` with an empty extent.

    ``layer`` selects a single sublayer. Leaving it unset is what a whole-service
    layer looks like, and that path is currently unusable against this server -
    see ``test_export_renders_each_sublayer``.
    """
    from qgis.core import QgsRasterLayer

    uri = (
        f"url='{base_url}/rest/services/{service_id}/MapServer' "
        f"crs='{crs}' format='{image_format}'"
    )
    if layer:
        uri = f"{uri} layer='{layer}'"
    return QgsRasterLayer(uri, "mapserver_test", "arcgismapserver")


def _advertised_full_extent(base_url: str, service_id: str = SERVICE_ID) -> dict:
    """Read ``fullExtent`` straight out of the MapServer info document."""
    response = httpx.get(
        f"{base_url}/rest/services/{service_id}/MapServer",
        params={"f": "json"},
        timeout=30,
    )
    response.raise_for_status()
    document = response.json()
    assert "error" not in document, (
        f"the MapServer info document is an error envelope: {document['error']}")
    extent = document.get("fullExtent")
    assert extent, "the MapServer info document advertises no fullExtent"
    return extent


def _identified_attribute_maps(result) -> list[dict]:
    """Flatten a QgsRasterIdentifyResult into one attribute map per feature.

    With ``IdentifyFormatFeature`` the AMS provider keys ``results()`` by result
    index, and each value is a *list* of ``QgsFeatureStore`` rather than a bare
    store - so a naive ``value.features()`` raises ``AttributeError`` instead of
    reporting an empty identify. Both shapes are accepted here.
    """
    if not result.isValid():
        return []
    attribute_maps = []
    for value in result.results().values():
        stores = value if isinstance(value, (list, tuple)) else [value]
        for store in stores:
            for feature in store.features():
                attribute_maps.append(feature.attributeMap())
    return attribute_maps


def _opaque_pixel_count(image) -> int:
    """Count non-transparent pixels on a 2px lattice.

    Sampling rather than a full scan keeps this cheap; the seeded layers differ
    by hundreds of pixels at this stride, so it is sensitive enough to tell a
    rendered layer from a blank one and from its siblings.
    """
    return sum(
        1
        for row in range(0, image.height(), 2)
        for col in range(0, image.width(), 2)
        if (image.pixel(col, row) >> 24) & 0xFF
    )


@pytest.mark.integration
@pytest.mark.pyqgis
class TestMapServerClientCompat:
    """GeoServices REST MapServer 10.8 via the QGIS arcgismapserver provider."""

    # ---------------------------------------------------------------- service-info
    # CERT-CONN-01. The provider fetches ``/MapServer?f=json`` and the layer it
    # builds has to carry what that document advertised.
    #
    # The extent is read live from the server rather than hardcoded, so the case
    # fails if either side drifts. The negative half is mandatory: because
    # ``isValid()`` is True for an unpublished service too, without it this case
    # would pass against a server that was not there at all.
    @pytest.mark.cert("CERT-CONN-01")
    def test_service_info_is_parsed_into_the_layer(
        self, qgis_app, base_url: str,
        mapserver_evidence: CertificationEvidenceCollector,
    ) -> None:
        started = time.monotonic()
        advertised = _advertised_full_extent(base_url)

        layer = make_mapserver_layer(base_url)
        assert layer.isValid(), (
            f"the arcgismapserver provider built no layer: {layer.error().summary()}")

        extent = layer.extent()
        assert not extent.isEmpty(), (
            "the layer extent is empty, so the MapServer info document was not "
            f"parsed: {layer.error().summary()!r}"
        )
        assert extent.xMinimum() == pytest.approx(
            advertised["xmin"], abs=EXTENT_TOLERANCE_DEG)
        assert extent.yMinimum() == pytest.approx(
            advertised["ymin"], abs=EXTENT_TOLERANCE_DEG)
        assert extent.xMaximum() == pytest.approx(
            advertised["xmax"], abs=EXTENT_TOLERANCE_DEG)
        assert extent.yMaximum() == pytest.approx(
            advertised["ymax"], abs=EXTENT_TOLERANCE_DEG)

        assert layer.crs().isValid()
        assert layer.crs().postgisSrid() == advertised["spatialReference"]["wkid"], (
            "the provider did not adopt the spatialReference the service info "
            f"advertised: layer={layer.crs().authid()} "
            f"advertised wkid={advertised['spatialReference']['wkid']}"
        )

        # The discriminator, asserted explicitly. An unpublished service also
        # yields isValid() == True; only the extent separates the two.
        missing = make_mapserver_layer(base_url, UNPUBLISHED_SERVICE_ID)
        assert missing.extent().isEmpty(), (
            f"the unpublished service {UNPUBLISHED_SERVICE_ID!r} produced a "
            "non-empty extent, so a parsed extent does not actually prove the "
            "info document was read and this case proves nothing"
        )

        mapserver_evidence.record(
            "CERT-CONN-01", "pass",
            duration_ms=int((time.monotonic() - started) * 1000),
            notes=(
                "MapServer info parsed by the arcgismapserver provider: extent "
                f"matched the advertised fullExtent and CRS resolved to "
                f"{layer.crs().authid()}."
            ),
            evidence_ref=f"{base_url}/rest/services/{SERVICE_ID}/MapServer?f=json",
        )

    # ---------------------------------------------------------------------- export
    # CERT-RNDR-01. ``block()`` makes the provider issue
    # ``/MapServer/export?...&f=image`` and decode the PNG it gets back.
    #
    # All three sublayers are rendered and their opaque-pixel counts compared,
    # because a single non-blank tile would also pass against a server returning
    # one canned image for every request. The three seeded geometries differ
    # enough that their counts cannot collide by accident.
    @pytest.mark.cert("CERT-RNDR-01")
    def test_export_renders_each_sublayer(
        self, qgis_app, base_url: str,
        mapserver_evidence: CertificationEvidenceCollector,
    ) -> None:
        counts: dict[str, int] = {}
        for sublayer in ALL_LAYERS:
            layer = make_mapserver_layer(base_url, layer=sublayer)
            assert not layer.extent().isEmpty(), (
                f"sublayer {sublayer} did not resolve: {layer.error().summary()}")

            block = layer.dataProvider().block(
                1, layer.extent(), RENDER_SIZE, RENDER_SIZE)
            assert block is not None and block.isValid(), (
                f"export produced no valid block for sublayer {sublayer}. The "
                "provider reports the server's error envelope through the block "
                "feedback rather than raising, so a 200-with-JSON-error lands here"
            )
            assert block.width() == RENDER_SIZE and block.height() == RENDER_SIZE

            image = block.image()
            assert image is not None and not image.isNull(), (
                f"export returned an undecodable image for sublayer {sublayer}")
            counts[sublayer] = _opaque_pixel_count(image)

        blank = [name for name, count in counts.items() if count == 0]
        assert not blank, (
            f"export returned a fully transparent image for sublayer(s) {blank}, "
            f"so nothing was drawn: {counts}"
        )
        assert len(set(counts.values())) == len(counts), (
            "every sublayer rendered to the same number of opaque pixels "
            f"({counts}), which is what a server returning one canned image for "
            "every export would produce"
        )

        # Paired negative: an unpublished sublayer must not render.
        missing = make_mapserver_layer(base_url, layer=UNPUBLISHED_LAYER)
        missing_block = missing.dataProvider().block(
            1, missing.extent(), RENDER_SIZE, RENDER_SIZE)
        assert missing_block is None or not missing_block.isValid(), (
            f"the unpublished sublayer {UNPUBLISHED_LAYER!r} produced a valid "
            "export block, so a valid block does not prove the requested layer "
            "was rendered"
        )

        mapserver_evidence.record(
            "CERT-RNDR-01", "pass",
            measured_count=sum(counts.values()),
            notes=(
                "export decoded through the arcgismapserver provider for all "
                f"three sublayers with distinct opaque-pixel counts {counts} at "
                f"{RENDER_SIZE}x{RENDER_SIZE}."
            ),
        )

    # -------------------------------------------------------------------- identify
    # CERT-SCHM-01. ``identify()`` drives ``/MapServer/identify`` and the
    # provider parses the results into a QgsFeatureStore per hit.
    #
    # Identified at the seeded feature rather than at the extent centre, and the
    # attributes are checked, so an identify that resolves but reports nothing
    # cannot pass. The empty-location half shows the server is answering the
    # query rather than replaying a fixed document.
    @pytest.mark.cert("CERT-SCHM-01")
    def test_identify_returns_the_seeded_feature(
        self, qgis_app, base_url: str,
        mapserver_evidence: CertificationEvidenceCollector,
    ) -> None:
        from qgis.core import QgsPointXY, QgsRaster, QgsRectangle

        layer = make_mapserver_layer(base_url, layer=POINT_LAYER)
        assert not layer.extent().isEmpty(), (
            f"the point sublayer did not resolve: {layer.error().summary()}")
        provider = layer.dataProvider()

        extent = QgsRectangle(-122.55, 37.65, -122.30, 37.90)
        hit = provider.identify(
            QgsPointXY(KNOWN_FEATURE_LON, KNOWN_FEATURE_LAT),
            QgsRaster.IdentifyFormatFeature,
            extent, RENDER_SIZE, RENDER_SIZE,
        )
        assert hit.isValid(), (
            f"identify did not resolve: {hit.error().summary()}")

        attribute_maps = _identified_attribute_maps(hit)
        assert attribute_maps, (
            "identify resolved but the provider parsed no features out of the "
            "response, so a client would report a successful empty identify"
        )

        matched = [
            attributes for attributes in attribute_maps
            if attributes.get("name") == KNOWN_FEATURE_NAME
        ]
        assert matched, (
            f"identify did not report {KNOWN_FEATURE_NAME!r} at its seeded "
            f"location; it returned {attribute_maps}"
        )
        assert str(matched[0].get("objectid")) == KNOWN_FEATURE_OBJECTID, (
            f"{KNOWN_FEATURE_NAME!r} came back with objectid "
            f"{matched[0].get('objectid')!r}, expected {KNOWN_FEATURE_OBJECTID!r}"
        )

        # Paired negative: the same layer, a location with no features.
        empty = provider.identify(
            QgsPointXY(EMPTY_LON, EMPTY_LAT),
            QgsRaster.IdentifyFormatFeature,
            QgsRectangle(EMPTY_LON - 0.05, EMPTY_LAT - 0.05,
                         EMPTY_LON + 0.05, EMPTY_LAT + 0.05),
            RENDER_SIZE, RENDER_SIZE,
        )
        empty_attributes = _identified_attribute_maps(empty)
        assert not empty_attributes, (
            "identify reported features at a location with none seeded "
            f"({EMPTY_LON}, {EMPTY_LAT}): {empty_attributes}. The server is not "
            "answering the query, so the positive half proves nothing"
        )

        mapserver_evidence.record(
            "CERT-SCHM-01", "pass",
            measured_count=len(attribute_maps),
            notes=(
                f"identify returned {len(attribute_maps)} feature(s) through the "
                f"arcgismapserver provider including {KNOWN_FEATURE_NAME!r} "
                f"(objectid {KNOWN_FEATURE_OBJECTID}), and nothing at an empty "
                "location."
            ),
        )

    # ---------------------------------------------------------------------- legend
    # CERT-RNDR-URL-01. The provider fetches ``/MapServer/legend`` and composes
    # the base64 ``imageData`` swatches into one image, asynchronously, so the
    # fetcher has to be driven by a Qt event loop.
    #
    # ``supportsLegendGraphic()`` returns True even for an unpublished service,
    # so it is not evidence of anything; the decoded image is. The paired
    # negative - an unpublished service yields a null image from the same code
    # path - is what makes the positive meaningful.
    @pytest.mark.cert("CERT-RNDR-URL-01")
    def test_legend_returns_composed_swatches(
        self, qgis_app, base_url: str,
        mapserver_evidence: CertificationEvidenceCollector,
    ) -> None:
        layer = make_mapserver_layer(base_url)
        assert not layer.extent().isEmpty(), (
            f"the service did not resolve: {layer.error().summary()}")
        provider = layer.dataProvider()
        assert provider.supportsLegendGraphic(), (
            "the arcgismapserver provider reports no legend support")

        image = self._fetch_legend(layer)
        assert image is not None, (
            "the legend fetch produced neither an image nor an error within "
            f"{LEGEND_TIMEOUT_MS} ms"
        )
        assert not image.isNull(), "the legend fetch returned a null image"
        assert image.width() > 0 and image.height() > 0

        colours = {
            image.pixel(col, row)
            for row in range(image.height())
            for col in range(image.width())
        }
        opaque = sum(
            1
            for row in range(image.height())
            for col in range(image.width())
            if (image.pixel(col, row) >> 24) & 0xFF
        )
        assert opaque > 0, "the legend image is fully transparent"
        assert len(colours) > 1, (
            "the legend image is a single flat colour, so no swatch was drawn "
            f"into it ({image.width()}x{image.height()})"
        )

        # Paired negative through the identical code path.
        missing_image = self._fetch_legend(
            make_mapserver_layer(base_url, UNPUBLISHED_SERVICE_ID))
        assert missing_image is None or missing_image.isNull(), (
            f"the unpublished service {UNPUBLISHED_SERVICE_ID!r} also produced a "
            "legend image, so this case would pass without a legend endpoint"
        )

        mapserver_evidence.record(
            "CERT-RNDR-URL-01", "pass",
            measured_count=opaque,
            notes=(
                f"legend composed a {image.width()}x{image.height()} image with "
                f"{len(colours)} distinct colours and {opaque} opaque pixels "
                "through the arcgismapserver provider's legend fetcher."
            ),
            evidence_ref=f"{base_url}/rest/services/{SERVICE_ID}/MapServer/legend",
        )

    @staticmethod
    def _fetch_legend(layer):
        """Drive the provider's async legend fetcher to completion.

        Returns the fetched image, or None if the fetcher errored, was never
        offered, or did not answer inside the timeout. The wait is bounded so a
        server that never replies fails the case instead of hanging the lane.
        """
        from qgis.core import QgsMapSettings
        from qgis.PyQt.QtCore import QEventLoop, QTimer

        settings = QgsMapSettings()
        settings.setExtent(layer.extent())
        settings.setDestinationCrs(layer.crs())
        settings.setLayers([layer])

        fetcher = layer.dataProvider().getLegendGraphicFetcher(settings)
        if fetcher is None:
            return None

        outcome: dict[str, object] = {}
        loop = QEventLoop()
        fetcher.finish.connect(
            lambda image: (outcome.__setitem__("image", image), loop.quit()))
        fetcher.error.connect(
            lambda message: (outcome.__setitem__("error", message), loop.quit()))
        QTimer.singleShot(LEGEND_TIMEOUT_MS, loop.quit)
        fetcher.start()
        loop.exec()

        if "error" in outcome:
            return None
        return outcome.get("image")
