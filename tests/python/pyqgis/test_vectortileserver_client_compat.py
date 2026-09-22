# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""GeoServices REST VectorTileServer compatibility through the QGIS AVT client.

QGIS 3.44 LTR ships an ``arcgisvectortileservice`` data provider ("ArcGIS Vector
Tile Service data provider"), confirmed present in
``QgsProviderRegistry.providerList()``. The client side is therefore *not* the
blocker for any of the three checklist operations:

    service-info  ``GET /rest/services/{id}/VectorTileServer``               CERT-CONN-02
    tile          ``GET .../VectorTileServer/tile/{z}/{y}/{x}.pbf``          CERT-RNDR-02
    style         ``GET .../VectorTileServer/resources/styles[/root.json]``  CERT-RNDR-SYM-01

**All three are BLOCKED on the fixture, not on the client.** The honua server
implements the surface (``src/Honua.Protocols.GeoServices/VectorTileServer/``,
routed from ``src/Honua.Server/EndpointRegistry.VectorTileServer.cs``), but the
``VectorTileServer`` protocol is not enabled on any service the compatibility
fixture publishes. Measured against the live fixture:

  * ``GET /rest/services?f=json`` lists ``browser_compat``, ``portal_public`` and
    ``test_service`` as FeatureServer / MapServer / ImageServer only - no
    VectorTileServer row.
  * ``GET /rest/services/browser_compat/VectorTileServer?f=json`` answers
    ``{"error":{"code":404,...,"details":["Service 'browser_compat' not
    found."]}}`` - a *service*-level miss, distinct from the
    ``"The requested operation or resource was not found."`` that an unrouted
    path returns, which is how the route is known to exist.
  * ``GET /api/v1/admin/services/{name}/settings`` reports ``VectorTileServer``
    in ``availableProtocols`` and absent from ``enabledProtocols`` for all three
    services.

So the cause is a fixture configuration gap: no seeded service enables the
protocol. Each case below probes for a published VectorTileServer and skips with
that named cause when there is none, rather than asserting something that would
pass vacuously against an unrouted surface. Recording a skip keeps the cell
visibly open in the evidence envelope instead of silently absent.

CAVEAT for whoever enables the protocol: the assertions past the publication
probe have never executed, because nothing has ever been published to run them
against. Treat the first green run as the real certification, not this file's
presence. What *is* verified is the provider URI: the form below is what
``QgsProviderRegistry.encodeUri('arcgisvectortileservice', ...)`` emits, and a
layer built from it binds to ``providerType() == 'arcgisvectortileservice'``
(measured; it fails to load only because the service is absent).
"""

from __future__ import annotations

import httpx
import pytest

from .conftest import CertificationEvidenceCollector

# Every service the fixture publishes; the probe accepts whichever one first
# turns out to expose a VectorTileServer.
CANDIDATE_SERVICE_IDS = ("browser_compat", "test_service", "portal_public")

BLOCKED_REASON = (
    "blocked: no fixture service publishes a VectorTileServer. The protocol is "
    "implemented server-side and listed in availableProtocols, but it is absent "
    "from enabledProtocols on browser_compat, test_service and portal_public, so "
    "/rest/services/{id}/VectorTileServer answers a 404 error envelope. Enable "
    "the VectorTileServer protocol on a seeded service (tests/seed/*.yaml, or "
    "PUT /api/v1/admin/services/{name}/protocols) to close this cell."
)

# The seeded browser_compat features cluster here, so a tile covering this box
# must carry layer data once the surface is published.
# The seeded browser_compat point the tile assertions target, and the zoom at
# which a single tile contains it.
KNOWN_FEATURE_LON = -122.4194
KNOWN_FEATURE_LAT = 37.7749
TILE_ZOOM = 12


def _tile_index(lon: float, lat: float, zoom: int) -> tuple[int, int]:
    """Web Mercator tile column/row containing a coordinate.

    tileInfo advertises 512 px tiles, but its level-0 resolution is halved to
    match, so the grid is still 2**zoom tiles across at every level and the
    indices are the standard XYZ ones.
    """
    import math

    span = 2 ** zoom
    column = int((lon + 180.0) / 360.0 * span)
    row = int(
        (1.0 - math.asinh(math.tan(math.radians(lat))) / math.pi) / 2.0 * span)
    return column, row


CLUSTER_XMIN = -122.55
CLUSTER_YMIN = 37.65
CLUSTER_XMAX = -122.30
CLUSTER_YMAX = 37.90


def _transparent():
    """A fully transparent render background, so opaque pixels are tile content."""
    from qgis.PyQt.QtGui import QColor

    return QColor(0, 0, 0, 0)


def make_vectortile_layer(base_url: str, service_id: str):
    """Construct a QGIS vector tile layer via the ``arcgisvectortileservice`` provider.

    The URI shape is the provider's own canonical encoding -
    ``QgsProviderRegistry.encodeUri('arcgisvectortileservice', {...})`` rewrites
    every input spelling to ``serviceType=arcgis&type=xyz&url=<endpoint>`` - and
    a layer built from it reports
    ``providerType() == 'arcgisvectortileservice'``. The more obvious
    ``type=arcgis&...`` and bare ``url='...'`` spellings both leave
    ``providerType()`` empty, so the layer never reaches the provider at all.
    """
    from qgis.core import QgsVectorTileLayer

    endpoint = f"{base_url}/rest/services/{service_id}/VectorTileServer"
    uri = f"serviceType=arcgis&type=xyz&url={endpoint}"
    return QgsVectorTileLayer(uri, "vectortileserver_test")


def _published_service_id(base_url: str) -> str:
    """Return a service publishing a VectorTileServer, or skip with the cause.

    The GeoServices REST convention is HTTP 200 carrying an error envelope, so
    the status code says nothing; the body has to be inspected. A document
    without an ``error`` key and with ``tiles``/``tileInfo`` is a real service
    info response.
    """
    observed: dict[str, str] = {}
    for service_id in CANDIDATE_SERVICE_IDS:
        endpoint = f"{base_url}/rest/services/{service_id}/VectorTileServer"
        try:
            response = httpx.get(endpoint, params={"f": "json"}, timeout=30)
        except httpx.HTTPError as exc:
            observed[service_id] = f"transport error: {exc}"
            continue
        try:
            document = response.json()
        except ValueError:
            observed[service_id] = (
                f"HTTP {response.status_code} non-JSON "
                f"{response.headers.get('content-type', '')}"
            )
            continue
        if "error" in document:
            details = document["error"].get("details") or [
                document["error"].get("message", "")
            ]
            observed[service_id] = f"error envelope: {details[0]}"
            continue
        if "tileInfo" not in document and "tiles" not in document:
            observed[service_id] = (
                "200 with neither tileInfo nor tiles, so not a VectorTileServer "
                f"info document: keys={sorted(document)[:8]}"
            )
            continue
        return service_id

    pytest.skip(f"{BLOCKED_REASON} Observed: {observed}")
    raise AssertionError("unreachable")  # pytest.skip() always raises


@pytest.mark.integration
@pytest.mark.pyqgis
class TestVectorTileServerClientCompat:
    """GeoServices REST VectorTileServer 10.8 via the QGIS AVT provider."""

    # ------------------------------------------------------------- service-info
    # CERT-CONN-02. The provider reads the VectorTileServer info document and
    # has to come out of it with a layer that carries the advertised tile
    # structure. As with the AMS provider, a vector tile layer that failed to
    # load is not guaranteed to raise, so validity is asserted explicitly.
    @pytest.mark.cert("CERT-CONN-02")
    def test_service_info_yields_a_valid_layer(
        self, qgis_app, base_url: str,
        vectortileserver_evidence: CertificationEvidenceCollector,
    ) -> None:
        service_id = _published_service_id(base_url)

        layer = make_vectortile_layer(base_url, service_id)
        assert layer.providerType() == "arcgisvectortileservice", (
            "the URI did not bind to the ArcGIS vector tile provider; QGIS chose "
            f"{layer.providerType()!r}"
        )
        assert layer.isValid(), (
            "the arcgisvectortileservice provider could not build a layer from "
            f"the VectorTileServer info document: {layer.error().summary()}"
        )
        assert not layer.extent().isEmpty(), (
            "the layer resolved with an empty extent, so the info document's "
            "fullExtent was not parsed"
        )
        assert layer.sourceMaxZoom() > layer.sourceMinZoom(), (
            "the provider read no usable zoom range out of tileInfo: "
            f"{layer.sourceMinZoom()}..{layer.sourceMaxZoom()}"
        )

        vectortileserver_evidence.record(
            "CERT-CONN-02", "pass",
            measured_count=layer.sourceMaxZoom() - layer.sourceMinZoom(),
            notes=(
                "VectorTileServer info parsed by the arcgisvectortileservice "
                f"provider for {service_id}: zoom "
                f"{layer.sourceMinZoom()}..{layer.sourceMaxZoom()}."
            ),
            evidence_ref=(
                f"{base_url}/rest/services/{service_id}/VectorTileServer?f=json"),
        )

    # -------------------------------------------------------------------- tile
    # CERT-RNDR-02. Two halves, because neither alone is worth much: the server
    # has to serve a non-empty ``.pbf`` for a tile over the seeded cluster, and
    # the provider has to turn that tile into pixels. Rendering through a map
    # render job is deliberately plain - it is the same mechanism the canvas
    # uses, and it avoids betting the case on a tile-fetch API whose signature
    # has moved between QGIS releases.
    @pytest.mark.cert("CERT-RNDR-02")
    def test_tile_is_served_and_renders(
        self, qgis_app, base_url: str,
        vectortileserver_evidence: CertificationEvidenceCollector,
    ) -> None:
        from qgis.core import (
            QgsCoordinateReferenceSystem,
            QgsCoordinateTransform,
            QgsMapRendererParallelJob,
            QgsMapSettings,
            QgsProject,
            QgsRectangle,
        )
        from qgis.PyQt.QtCore import QEventLoop, QSize, QTimer

        service_id = _published_service_id(base_url)

        # Derived from the seeded feature rather than hardcoded. A hardcoded
        # column was off by one here, landing on an empty neighbouring tile,
        # and the server correctly answered 204 - the right answer to the wrong
        # question.
        zoom = TILE_ZOOM
        column, row = _tile_index(KNOWN_FEATURE_LON, KNOWN_FEATURE_LAT, zoom)
        tile = httpx.get(
            f"{base_url}/rest/services/{service_id}/VectorTileServer"
            f"/tile/{zoom}/{row}/{column}.pbf",
            timeout=30,
        )
        # 204 is the correct response for a tile with no features, so it is
        # called out separately: seeing it here means the index is wrong or the
        # cache is cold, not that tiling is broken.
        assert tile.status_code != 204, (
            f"tile {zoom}/{row}/{column} is empty (204), but it is the tile "
            f"containing the seeded feature at ({KNOWN_FEATURE_LON}, "
            f"{KNOWN_FEATURE_LAT})"
        )
        assert tile.status_code == 200, (
            f"the tile endpoint returned {tile.status_code}")
        assert tile.content, "the tile endpoint served an empty body"

        layer = make_vectortile_layer(base_url, service_id)
        assert layer.isValid(), (
            f"the layer did not resolve: {layer.error().summary()}")

        to_web_mercator = QgsCoordinateTransform(
            QgsCoordinateReferenceSystem("EPSG:4326"),
            QgsCoordinateReferenceSystem("EPSG:3857"),
            QgsProject.instance(),
        )
        settings = QgsMapSettings()
        settings.setLayers([layer])
        settings.setDestinationCrs(QgsCoordinateReferenceSystem("EPSG:3857"))
        settings.setExtent(
            to_web_mercator.transformBoundingBox(
                QgsRectangle(CLUSTER_XMIN, CLUSTER_YMIN, CLUSTER_XMAX, CLUSTER_YMAX)))
        settings.setOutputSize(QSize(256, 256))
        settings.setBackgroundColor(_transparent())

        job = QgsMapRendererParallelJob(settings)
        loop = QEventLoop()
        job.finished.connect(loop.quit)
        QTimer.singleShot(30000, loop.quit)
        job.start()
        loop.exec()
        job.waitForFinished()

        assert not job.errors(), f"the render job reported errors: {job.errors()}"
        image = job.renderedImage()
        assert not image.isNull(), "the render job produced no image"

        # A transparent background means every opaque pixel came from the tile.
        drawn = sum(
            1
            for y in range(0, image.height(), 2)
            for x in range(0, image.width(), 2)
            if (image.pixel(x, y) >> 24) & 0xFF
        )
        assert drawn > 0, (
            "the vector tile layer rendered nothing over the seeded cluster, so "
            f"the {len(tile.content)}-byte tile carried no usable features"
        )

        vectortileserver_evidence.record(
            "CERT-RNDR-02", "pass", measured_count=drawn,
            notes=(
                f"VectorTileServer served a {len(tile.content)}-byte .pbf at "
                f"{zoom}/{row}/{column} and the arcgisvectortileservice provider "
                f"rendered {drawn} opaque pixels over the seeded cluster."
            ),
        )

    # ------------------------------------------------------------------- style
    # CERT-RNDR-SYM-01. The provider fetches the service's style resource and
    # converts it into QGIS renderers. A style that downloads but converts to
    # nothing leaves the layer drawing blank, so the converted renderer is what
    # is asserted, not the HTTP fetch.
    @pytest.mark.cert("CERT-RNDR-SYM-01")
    def test_style_resource_converts_to_a_renderer(
        self, qgis_app, base_url: str,
        vectortileserver_evidence: CertificationEvidenceCollector,
    ) -> None:
        service_id = _published_service_id(base_url)

        # The style resource must exist on its own terms before the provider is
        # blamed for not using it.
        styles = httpx.get(
            f"{base_url}/rest/services/{service_id}/VectorTileServer"
            "/resources/styles/root.json",
            timeout=30,
        )
        assert styles.status_code == 200, (
            f"the style resource returned {styles.status_code}")
        style_document = styles.json()
        assert "error" not in style_document, (
            f"the style resource is an error envelope: {style_document['error']}")
        assert style_document.get("layers"), (
            "the style document declares no layers, so there is nothing for a "
            "client to convert"
        )

        layer = make_vectortile_layer(base_url, service_id)
        assert layer.isValid(), (
            f"the layer did not resolve: {layer.error().summary()}")

        renderer = layer.renderer()
        assert renderer is not None, (
            "the provider applied no renderer, so the service style was not "
            "converted and the layer would draw with QGIS defaults"
        )
        styled = renderer.styles() if hasattr(renderer, "styles") else []
        assert styled, (
            "the converted renderer carries no styles, so the service style "
            f"document ({len(style_document['layers'])} layers) was dropped"
        )

        vectortileserver_evidence.record(
            "CERT-RNDR-SYM-01", "pass", measured_count=len(styled),
            notes=(
                f"The service style ({len(style_document['layers'])} style "
                f"layers) converted into {len(styled)} QGIS vector tile "
                "style(s) through the arcgisvectortileservice provider."
            ),
            evidence_ref=(
                f"{base_url}/rest/services/{service_id}/VectorTileServer"
                "/resources/styles/root.json"
            ),
        )
