# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
PMTiles v3 archive-read compatibility exercised through the real QGIS OGR provider.

This lane was recorded as ``blocked`` with the cause "no archive published; 404
zero-byte body". That cause was accurate but incomplete: the range proxy at
``/api/v1/tiles/pmtiles/{*artifactId}`` is registered and unauthenticated in this
fixture, it was simply empty, because nothing had ever run a durable PMTiles
publish against it. The archive this module reads is produced by the tile-operations
publish job (``POST /api/v1/admin/tile-operations/jobs`` with
``{"operation":"publish","serviceId":"test_service","layerId":0,
"tileMatrixSetId":"WebMercatorQuad","minZoom":0,"maxZoom":3}``), which writes a
deterministic object key - ``pmtiles/{serviceId}/{layerId}/{tileMatrixSetId}.pmtiles``
- with ``operation=publish`` metadata, which is exactly what
``PMTilesProxyService.IsPublishedPMTilesArtifact`` requires before the proxy will
serve a byte.

PREREQUISITE: that publish job is the fixture's responsibility, not this module's.
The artifact lives in the server's ``Local`` file-storage root (``/tmp/honua-storage``),
and ``LocalFileStorage`` only rebuilds its in-memory file index at construction, so
the archive cannot be dropped onto disk after start-up - it has to be published
through the admin API against the running server. Every case here is deliberately
credential-free and read-only; it never calls the admin surface, so on a fixture
where the publish step has not run these cases fail (not skip) with a 404, which is
the honest report of an unpublished archive.

Two client-side facts are load-bearing and cost real time to find, so they are
recorded here rather than rediscovered:

1. QGIS 3.44 ships **no** PMTiles vector-tile provider. ``QgsProviderRegistry``
   lists ``mbtilesvectortiles``, ``vtpkvectortiles``, ``xyzvectortiles`` and
   ``arcgisvectortileservice`` - nothing for PMTiles. The archive is therefore
   read as an **OGR vector dataset** through GDAL's ``PMTiles`` driver
   (GDAL 3.10.3, ``DCAP_VIRTUALIO=YES``), not as a tiled basemap.
2. The URI must be the **plain HTTP URL**. QGIS prefixes ``/vsicurl/`` itself;
   handing it an already-prefixed ``/vsicurl/http://...`` yields an invalid layer
   with an *empty* error summary. ``gdal.OpenEx`` accepts the prefixed form, so
   the difference is QGIS's URI normalisation, not GDAL's.

The archive carries the 9 seeded points of ``test_service`` layer 0 from
``tests/seed/client-compat-v1.sql:1043-1051`` (alpha..iota, on a diagonal across
San Francisco). Because the publish ran with ``maxZoom=3``, the coordinates in the
archive are MVT-quantised to roughly a 0.005 degree grid, so position assertions
here carry a deliberate tolerance: they exist to catch a wrong CRS, a flipped axis
or a mismatched feature, not to re-measure the seed.
"""

from __future__ import annotations

import time
import urllib.error
import urllib.request

import pytest

from .conftest import CertificationEvidenceCollector

# Deterministic publish key from TileOperationExecutionCore.BuildPublishObjectKey:
# "{PMTilesPublish:KeyPrefix}/{serviceId}/{layerId}/{tileMatrixSetId}.pmtiles",
# served under the proxy's own "/api/v1/tiles/pmtiles/" route prefix.
ARTIFACT_ID = "pmtiles/test_service/0/WebMercatorQuad.pmtiles"
PROXY_PREFIX = "/api/v1/tiles/pmtiles/"

PMTILES_MAGIC = b"PMTiles"

# OGR's PMTiles driver names the single MVT layer after the archive's layer
# metadata, which PMTilesArchiveMetadata writes as "layer".
OGR_LAYER_NAME = "layer"

EXPECTED_FEATURE_COUNT = 9
EXPECTED_CRS = "EPSG:3857"

# tests/seed/client-compat-v1.sql:1043-1051. Order is by objectid.
SEEDED_POINTS = {
    "alpha": (-122.4900, 37.7100),
    "beta": (-122.4800, 37.7200),
    "gamma": (-122.4700, 37.7300),
    "delta": (-122.4500, 37.7400),
    "epsilon": (-122.4300, 37.7500),
    "zeta": (-122.4100, 37.7600),
    "eta": (-122.4000, 37.7700),
    "theta": (-122.3900, 37.7800),
    "iota": (-122.3700, 37.7900),
}

# A z3 MVT tile is 4096 units across 1/8 of the world, so one unit is ~0.011
# degrees of longitude. Measured error against the seed is <= 0.005 degrees;
# 0.02 leaves headroom while still failing a wrong CRS (which is off by orders
# of magnitude) or a swapped axis pair.
QUANTISATION_TOLERANCE_DEG = 0.02

# Attribute columns the vector-tile encoder carries over from layer 0, plus the
# synthetic feature id OGR's MVT reader always prepends.
EXPECTED_FIELDS = {
    "mvt_id",
    "objectid",
    "name",
    "status",
    "count",
    "ratio",
    "active",
    "created_at",
    "event_date",
    "event_time",
    "uid",
}


def _archive_url(base_url: str, artifact_id: str = ARTIFACT_ID) -> str:
    return f"{base_url.rstrip('/')}{PROXY_PREFIX}{artifact_id}"


def _layer(base_url: str, artifact_id: str = ARTIFACT_ID):
    """Build the layer the way QGIS itself would from a pasted HTTP URL.

    ``|layername=`` is what ``QgsProviderRegistry.querySublayers`` returns for
    this archive, so this is the exact URI the Data Source Manager would hand the
    provider after the user picks the sublayer.
    """
    from qgis.core import QgsVectorLayer

    uri = f"{_archive_url(base_url, artifact_id)}|layername={OGR_LAYER_NAME}"
    return QgsVectorLayer(uri, "pmtiles-cert", "ogr")


def _http(url: str, *, method: str = "GET", headers: dict[str, str] | None = None):
    request = urllib.request.Request(url, method=method, headers=headers or {})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, dict(response.headers), response.read()
    except urllib.error.HTTPError as exc:
        return exc.code, dict(exc.headers or {}), exc.read()


@pytest.mark.pyqgis
class TestPMTilesClientCompat:
    """PMTiles v3 archive-read via the QGIS ogr provider over GDAL's PMTiles driver."""

    # ------------------------------------------------------------------
    # CERT-CONN-01: the provider opens the published archive at all
    # ------------------------------------------------------------------
    @pytest.mark.cert("CERT-CONN-01")
    def test_provider_opens_the_published_archive(
        self,
        qgis_app,
        base_url: str,
        pmtiles_evidence: CertificationEvidenceCollector,
    ) -> None:
        started = time.monotonic()
        layer = _layer(base_url)

        # The OGR provider builds an invalid layer rather than raising when GDAL
        # cannot identify the dataset, and for a remote PMTiles archive it does
        # so with an empty error summary, so validity is the only real signal.
        assert layer.isValid(), (
            "QGIS could not open the published PMTiles archive at "
            f"{_archive_url(base_url)}. QGIS has no PMTiles provider, so this "
            "goes through GDAL's PMTiles driver over /vsicurl, which needs the "
            "proxy to answer HEAD and honour Range: "
            f"{layer.error().summary()!r}"
        )
        pmtiles_evidence.record(
            "CERT-CONN-01",
            "pass",
            duration_ms=int((time.monotonic() - started) * 1000),
            notes=(
                "QgsVectorLayer opened the range-proxied PMTiles archive through "
                "the stock ogr provider (GDAL PMTiles driver)."
            ),
        )

    # ------------------------------------------------------------------
    # CERT-DISC-01: QGIS's own sublayer probe recognises the archive
    # ------------------------------------------------------------------
    @pytest.mark.cert("CERT-DISC-01")
    def test_sublayer_discovery_identifies_the_archive(
        self,
        qgis_app,
        base_url: str,
        pmtiles_evidence: CertificationEvidenceCollector,
    ) -> None:
        from qgis.core import QgsProviderRegistry

        # This is the code path behind Data Source Manager > Vector > Protocol:
        # the user pastes a URL and QGIS decides what it is.
        sublayers = QgsProviderRegistry.instance().querySublayers(_archive_url(base_url))

        assert sublayers, (
            "QGIS did not recognise the published archive as any kind of layer. "
            "querySublayers returning nothing is how a pasted URL shows up as "
            "'unsupported' in the Data Source Manager."
        )
        names = {sublayer.name() for sublayer in sublayers}
        providers = {sublayer.providerKey() for sublayer in sublayers}

        assert OGR_LAYER_NAME in names, (
            f"expected the archive's MVT layer {OGR_LAYER_NAME!r} among the "
            f"discovered sublayers, got {sorted(names)}"
        )
        assert providers == {"ogr"}, (
            "PMTiles must resolve to the ogr provider - QGIS 3.44 ships no "
            f"PMTiles vector-tile provider - got {sorted(providers)}"
        )
        pmtiles_evidence.record(
            "CERT-DISC-01",
            "pass",
            measured_count=len(sublayers),
            notes=(
                f"querySublayers resolved {sorted(names)} via provider(s) "
                f"{sorted(providers)}."
            ),
        )

    # ------------------------------------------------------------------
    # CERT-DISC-02: the proxy advertises and honours byte ranges
    # ------------------------------------------------------------------
    @pytest.mark.cert("CERT-DISC-02")
    def test_proxy_serves_the_archive_by_byte_range(
        self,
        qgis_app,
        base_url: str,
        pmtiles_evidence: CertificationEvidenceCollector,
    ) -> None:
        url = _archive_url(base_url)

        head_status, head_headers, _ = _http(url, method="HEAD")
        assert head_status == 200, f"HEAD {url} -> {head_status}"
        assert head_headers.get("Accept-Ranges") == "bytes", (
            "a PMTiles reader picks the archive apart by range; without "
            "Accept-Ranges: bytes GDAL falls back to whole-file reads. Got "
            f"{head_headers.get('Accept-Ranges')!r}"
        )
        total = int(head_headers["Content-Length"])
        assert total > 0

        # The ranged GET goes to a nonce-suffixed URL on purpose. The output-cache
        # base policy (ObservabilityServiceCollectionExtensions.AddBasePolicy, wired
        # for every anonymous endpoint by Program.cs UseOutputCache) does not vary
        # its key on the Range request header, so once any unranged GET has warmed
        # the entry for this artifact - which GDAL itself does while opening the
        # layer in the cases above - every ranged GET for the next TTL is answered
        # from cache as a whole-archive 200 with an Age header, and a client
        # Cache-Control: no-cache does not bypass it. A nonce gives this case a cold
        # cache key so it measures the endpoint rather than the cache. Asserting the
        # warm URL here would make the case order-dependent and would certify the
        # cache's behaviour, not the proxy's.
        nonce_url = f"{url}?cb={time.monotonic_ns():x}"
        range_status, range_headers, body = _http(
            nonce_url, headers={"Range": "bytes=0-6"}
        )
        assert range_status == 206, (
            "the range proxy must answer 206 Partial Content for a ranged GET, "
            f"got {range_status}. A 200 here means ranges are ignored and the "
            "'archive-read' claim is a whole-file download."
        )
        assert range_headers.get("Content-Range") == f"bytes 0-6/{total}", (
            f"unexpected Content-Range {range_headers.get('Content-Range')!r} "
            f"for a {total}-byte archive"
        )
        assert body == PMTILES_MAGIC, (
            "the first 7 bytes of a PMTiles v3 archive are the ASCII magic "
            f"{PMTILES_MAGIC!r}, got {body!r}"
        )
        pmtiles_evidence.record(
            "CERT-DISC-02",
            "pass",
            measured_count=total,
            notes=(
                f"HEAD advertised Accept-Ranges: bytes over {total} bytes; "
                "a ranged GET returned 206 with the PMTiles v3 magic."
            ),
        )

    # ------------------------------------------------------------------
    # CERT-SCHM-01: the attribute schema survives the MVT round-trip
    # ------------------------------------------------------------------
    @pytest.mark.cert("CERT-SCHM-01")
    def test_attribute_schema_round_trips(
        self,
        qgis_app,
        base_url: str,
        pmtiles_evidence: CertificationEvidenceCollector,
    ) -> None:
        layer = _layer(base_url)
        assert layer.isValid(), layer.error().summary()

        field_names = {field.name() for field in layer.fields()}
        missing = EXPECTED_FIELDS - field_names
        assert not missing, (
            f"the published archive dropped attribute column(s) {sorted(missing)}; "
            f"it carries {sorted(field_names)}"
        )
        pmtiles_evidence.record(
            "CERT-SCHM-01",
            "pass",
            measured_count=len(field_names),
            notes=f"{len(field_names)} attribute column(s) survived the MVT encode.",
        )

    # ------------------------------------------------------------------
    # CERT-GEOM-01: geometry, CRS and the seeded features themselves
    # ------------------------------------------------------------------
    @pytest.mark.cert("CERT-GEOM-01")
    def test_features_and_geometry_match_the_seed(
        self,
        qgis_app,
        base_url: str,
        pmtiles_evidence: CertificationEvidenceCollector,
    ) -> None:
        from qgis.core import (
            QgsCoordinateReferenceSystem,
            QgsCoordinateTransform,
            QgsProject,
            QgsWkbTypes,
        )

        layer = _layer(base_url)
        assert layer.isValid(), layer.error().summary()

        # WebMercatorQuad, so the archive is authored in 3857 whatever the source
        # layer's CRS was; a 4326 answer here would mean no reprojection happened.
        assert layer.crs().authid() == EXPECTED_CRS, (
            f"a WebMercatorQuad archive is authored in {EXPECTED_CRS}; got "
            f"{layer.crs().authid()}"
        )
        assert QgsWkbTypes.geometryType(layer.wkbType()) == QgsWkbTypes.PointGeometry, (
            f"layer 0 is a point layer; got wkbType {layer.wkbType()}"
        )
        assert layer.featureCount() == EXPECTED_FEATURE_COUNT, (
            f"expected the {EXPECTED_FEATURE_COUNT} seeded points, got "
            f"{layer.featureCount()}"
        )

        transform = QgsCoordinateTransform(
            layer.crs(),
            QgsCoordinateReferenceSystem("EPSG:4326"),
            QgsProject.instance(),
        )

        seen: dict[str, tuple[float, float]] = {}
        for feature in layer.getFeatures():
            geometry = feature.geometry()
            assert not geometry.isEmpty(), f"feature {feature['name']!r} has no geometry"
            point = transform.transform(geometry.asPoint())
            seen[feature["name"]] = (point.x(), point.y())

        assert set(seen) == set(SEEDED_POINTS), (
            "the archive does not carry the seeded feature set: missing "
            f"{sorted(set(SEEDED_POINTS) - set(seen))}, unexpected "
            f"{sorted(set(seen) - set(SEEDED_POINTS))}"
        )

        worst_name = ""
        worst_delta = 0.0
        for name, (expected_lon, expected_lat) in SEEDED_POINTS.items():
            got_lon, got_lat = seen[name]
            delta = max(abs(got_lon - expected_lon), abs(got_lat - expected_lat))
            if delta > worst_delta:
                worst_delta, worst_name = delta, name
            assert delta <= QUANTISATION_TOLERANCE_DEG, (
                f"{name} landed at ({got_lon:.4f}, {got_lat:.4f}) but the seed "
                f"puts it at ({expected_lon:.4f}, {expected_lat:.4f}); a "
                f"{delta:.4f} degree miss is far beyond z3 MVT quantisation and "
                "points at a CRS or axis-order fault, not tile rounding"
            )

        pmtiles_evidence.record(
            "CERT-GEOM-01",
            "pass",
            measured_count=len(seen),
            measured_delta=worst_delta,
            notes=(
                f"all {len(seen)} seeded points round-tripped in {EXPECTED_CRS}; "
                f"worst position error {worst_delta:.4f} deg ({worst_name})."
            ),
        )

    # ------------------------------------------------------------------
    # CERT-ERRH-01: an unpublished artifact id is a clean miss
    # ------------------------------------------------------------------
    @pytest.mark.cert("CERT-ERRH-01")
    def test_unpublished_artifact_is_a_clean_404(
        self,
        qgis_app,
        base_url: str,
        pmtiles_evidence: CertificationEvidenceCollector,
    ) -> None:
        # Assert the known-good sibling FIRST. Without this, every assertion
        # below would also hold on a server where nothing at all resolves, and
        # the case would "pass" while certifying nothing - the exact failure mode
        # that let three WMTS cases report green while every layer was invalid.
        good = _layer(base_url)
        assert good.isValid(), (
            "the negative case needs a working positive control; the published "
            f"archive did not open: {good.error().summary()!r}"
        )
        assert good.featureCount() == EXPECTED_FEATURE_COUNT

        absent = "pmtiles/test_service/0/NoSuchTileMatrixSet.pmtiles"
        status, _, body = _http(_archive_url(base_url, absent))
        assert status == 404, (
            f"an unpublished artifact id must 404, got {status}"
        )

        layer = _layer(base_url, absent)
        assert not layer.isValid(), (
            "QGIS built a usable layer from an artifact the proxy does not serve"
        )
        pmtiles_evidence.record(
            "CERT-ERRH-01",
            "pass",
            notes=(
                f"unpublished artifact returned {status} with a "
                f"{len(body)}-byte body and yielded no usable layer, while the "
                "published archive opened in the same test."
            ),
        )
